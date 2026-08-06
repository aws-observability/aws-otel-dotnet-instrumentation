// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// Resolves a line-level configuration's (file, line, local name) into the interior IL offset and
/// local slot the native rewriter needs, using the target assembly's portable or embedded PDB.
/// </summary>
// This is the line-level analogue of ProfilerTranslator.ReflectionResolveMethod: function-level only
// needs a type + arity, but line-level additionally needs to know WHERE inside the method body to
// inject and WHICH local slot to read. Everything before this class existed used hardcoded offsets.
//
// Uses only System.Reflection.Metadata, which ships in the shared framework — no new package
// dependency (verified: net8.0 resolves it without a PackageReference).
//
// THREE CORRECTNESS RULES THIS ENCODES, each learned from a spike that silently produced wrong data:
//
//  R-A (offset -> statement+1). A sequence point's IL offset is the START of its statement, i.e.
//      BEFORE that statement's assignment has executed. To read the local assigned by statement k you
//      must inject at the start of statement k+1. Injecting at statement k's own start reads a slot
//      that is allocated but NOT YET ASSIGNED and silently yields 0/null. Proven live: the first R9
//      run captured a boxed 0 for every probe with no error anywhere.
//
//  R-B (instruction boundary + not a branch target). See IlBoundaryScanner. A branch-target offset
//      weaves successfully and NEVER FIRES.
//
//  R-C (Mvid must match). A PDB from a different build yields plausible-but-wrong offsets. Wrong-but-
//      plausible is worse than absent, because the resulting snapshot looks valid. Reject on mismatch.
internal sealed class PdbReader : IDisposable
{
    /// <summary>The hidden-sequence-point line marker (0xFEEFEE). Not a user statement.</summary>
    internal const int HiddenSequencePointLine = 0xFEEFEE;

    private readonly Dictionary<string, AssemblyDebugInfo?> cache = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    /// <summary>
    /// Resolves the injection location for a line-level target.
    /// </summary>
    /// <param name="type">The resolved target type.</param>
    /// <param name="methodName">The target method name.</param>
    /// <param name="lineNumber">The 1-based source line to instrument.</param>
    /// <param name="localName">Optional local variable name to capture.</param>
    /// <returns>The resolution outcome.</returns>
    public LineProbeResolution Resolve(Type type, string methodName, int lineNumber, string? localName)
    {
        if (type == null)
        {
            return LineProbeResolution.Fail(LineProbeResolutionStatus.TypeNotLoaded);
        }

        var assembly = type.Assembly;
        var debugInfo = this.GetDebugInfo(assembly);
        if (debugInfo == null)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoUnavailable,
                $"no readable PDB for {assembly.GetName().Name}");
        }

        if (!debugInfo.MvidMatches)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoMismatch,
                $"PDB does not belong to the loaded module {assembly.GetName().Name}");
        }

        // A line belongs to exactly one method body, but the configured method name disambiguates
        // overloads that share a file. Try every same-named overload and take the first whose debug
        // info actually contains the requested line.
        var candidates = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.Name == methodName)
            .ToArray();

        if (candidates.Length == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"no method named {methodName} on {type.FullName}");
        }

        LineProbeResolution? lastFailure = null;

        foreach (var method in candidates)
        {
            var result = this.ResolveInMethod(debugInfo, type, method, lineNumber, localName);
            if (result.IsResolved)
            {
                return result;
            }

            // Prefer the most specific failure: a local-scope failure means we DID find the line.
            if (lastFailure == null || result.Status == LineProbeResolutionStatus.LocalOutOfScope)
            {
                lastFailure = result;
            }
        }

        return lastFailure ?? LineProbeResolution.Fail(LineProbeResolutionStatus.LineNotExecutable);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        foreach (var info in this.cache.Values)
        {
            info?.Dispose();
        }

        this.cache.Clear();
        this.disposed = true;
    }

    /// <summary>
    /// Finds the local variable slot for a source name that is in scope at the given IL offset.
    /// </summary>
    // Scope intersection is load-bearing, not a nicety: the compiler REUSES slots across disjoint
    // scopes, so the same slot index can hold a different variable earlier or later in the body.
    // Matching on name alone would return a slot that holds an unrelated value at our offset — a
    // confidently-wrong capture. Only names whose declaring scope CONTAINS the offset are eligible.
    private static int? ResolveLocalSlot(
        MetadataReader reader, MethodDefinitionHandle methodHandle, string localName, uint ilOffset)
    {
        var localScopes = reader.GetLocalScopes(methodHandle);

        foreach (var scopeHandle in localScopes)
        {
            var scope = reader.GetLocalScope(scopeHandle);
            uint start = (uint)scope.StartOffset;
            uint end = (uint)scope.EndOffset;

            // Half-open [start, end): a scope ending exactly at our offset no longer covers it.
            if (ilOffset < start || ilOffset >= end)
            {
                continue;
            }

            foreach (var localHandle in scope.GetLocalVariables())
            {
                var local = reader.GetLocalVariable(localHandle);
                if (local.Attributes.HasFlag(LocalVariableAttributes.DebuggerHidden))
                {
                    continue;
                }

                if (string.Equals(reader.GetString(local.Name), localName, StringComparison.Ordinal))
                {
                    return local.Index;
                }
            }
        }

        return null;
    }

    private LineProbeResolution ResolveInMethod(
        AssemblyDebugInfo debugInfo, Type type, MethodInfo method, int lineNumber, string? localName)
    {
        var reader = debugInfo.Reader;
        var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);

        MethodDebugInformation methodDebugInfo;
        try
        {
            methodDebugInfo = reader.GetMethodDebugInformation(handle);
        }
        catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoUnavailable, $"unreadable debug info: {ex.Message}");
        }

        // Collect non-hidden sequence points in IL order. 0xFEEFEE marks compiler-generated code with
        // no user statement; injecting there is meaningless and its "line" is a sentinel, not a line.
        var points = new List<(int Line, uint Offset)>();
        try
        {
            foreach (var sp in methodDebugInfo.GetSequencePoints())
            {
                if (sp.IsHidden || sp.StartLine == HiddenSequencePointLine)
                {
                    continue;
                }

                points.Add((sp.StartLine, (uint)sp.Offset));
            }
        }
        catch (BadImageFormatException ex)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.DebugInfoUnavailable, $"corrupt sequence points: {ex.Message}");
        }

        if (points.Count == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable, $"{method.Name} has no sequence points");
        }

        int matchIndex = points.FindIndex(p => p.Line == lineNumber);
        if (matchIndex < 0)
        {
            var nearest = points.OrderBy(p => Math.Abs(p.Line - lineNumber)).First().Line;
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"line {lineNumber} is not an executable statement in {method.Name}; nearest is {nearest}");
        }

        var body = method.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il == null || il.Length == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable, $"{method.Name} has no IL body");
        }

        var scan = IlBoundaryScanner.Scan(il);

        // R-A: to observe the effect of the requested line, inject at the START OF THE NEXT statement.
        // Without a next statement in this method the line's own effect is never observable at a
        // boundary we control, so refuse rather than capture a pre-assignment value.
        var candidateOffsets = new List<uint>();
        for (int i = matchIndex + 1; i < points.Count; i++)
        {
            candidateOffsets.Add(points[i].Offset);
        }

        if (candidateOffsets.Count == 0)
        {
            return LineProbeResolution.Fail(
                LineProbeResolutionStatus.LineNotExecutable,
                $"line {lineNumber} is the last statement in {method.Name}; no following boundary to read it at");
        }

        // R-B: take the first candidate that is a real instruction start and not a branch target.
        uint? chosenOffset = null;
        foreach (var offset in candidateOffsets)
        {
            if (offset >= il.Length)
            {
                continue;
            }

            if (scan.IsSafeInjectionPoint(offset))
            {
                chosenOffset = offset;
                break;
            }
        }

        if (chosenOffset == null)
        {
            var detail = $"no safe injection boundary after line {lineNumber} in {method.Name} " +
                $"(all candidates were branch targets or mid-instruction; scanComplete={scan.Complete})";
            return LineProbeResolution.Fail(LineProbeResolutionStatus.LineNotExecutable, detail);
        }

        int slot = -1;
        if (!string.IsNullOrEmpty(localName))
        {
            var slotResult = ResolveLocalSlot(reader, handle, localName!, chosenOffset.Value);
            if (slotResult == null)
            {
                return LineProbeResolution.Fail(
                    LineProbeResolutionStatus.LocalOutOfScope,
                    $"local '{localName}' is not in scope at IL offset {chosenOffset.Value} in {method.Name}");
            }

            slot = slotResult.Value;
        }

        var typeName = type.FullName ?? type.Name;
        return LineProbeResolution.Success(new LineProbeLocation(
            MethodToken: method.MetadataToken,
            AssemblyName: type.Assembly.GetName().Name ?? string.Empty,
            TypeName: typeName,
            MethodName: method.Name,
            ParameterCount: method.GetParameters().Length,
            IlOffset: chosenOffset.Value,
            LocalSlot: slot,
            LocalName: localName));
    }

    private AssemblyDebugInfo? GetDebugInfo(Assembly assembly)
    {
        var key = assembly.FullName ?? assembly.GetName().Name ?? assembly.Location;
        if (this.cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var info = AssemblyDebugInfo.TryOpen(assembly);
        this.cache[key] = info;
        return info;
    }

    /// <summary>
    /// An opened PDB for one assembly, plus whether it actually belongs to that assembly's module.
    /// </summary>
    private sealed class AssemblyDebugInfo : IDisposable
    {
        private readonly IDisposable?[] owned;

        private AssemblyDebugInfo(MetadataReader reader, bool mvidMatches, params IDisposable?[] owned)
        {
            this.Reader = reader;
            this.MvidMatches = mvidMatches;
            this.owned = owned;
        }

        public MetadataReader Reader { get; }

        public bool MvidMatches { get; }

        /// <summary>
        /// Opens the embedded or sidecar portable PDB for an assembly, verifying it matches the module.
        /// </summary>
        /// <param name="assembly">The assembly whose debug info is wanted.</param>
        /// <returns>The opened debug info, or null when none is available.</returns>
        public static AssemblyDebugInfo? TryOpen(Assembly assembly)
        {
            // Dynamic/in-memory assemblies have no on-disk image to read debug info from.
            string location;
            try
            {
                location = assembly.Location;
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return null;
            }

            try
            {
                var peStream = File.OpenRead(location);
                var peReader = new PEReader(peStream);
                var moduleMvid = ReadMvid(peReader);

                // Prefer an EMBEDDED PDB: it cannot be stale by construction, since it ships inside
                // the same file as the IL.
                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.EmbeddedPortablePdb)
                    {
                        continue;
                    }

                    var embedded = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    return new AssemblyDebugInfo(
                        embedded.GetMetadataReader(), true, embedded, peReader, peStream);
                }

                // Fall back to a sidecar .pdb next to the assembly.
                var pdbPath = Path.ChangeExtension(location, ".pdb");
                if (!File.Exists(pdbPath))
                {
                    peReader.Dispose();
                    peStream.Dispose();
                    return null;
                }

                var pdbStream = File.OpenRead(pdbPath);
                MetadataReaderProvider provider;
                try
                {
                    provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                }
                catch (BadImageFormatException)
                {
                    // Almost certainly a Windows (native) PDB rather than a portable one. v1 is
                    // portable + embedded only, so treat it as unavailable rather than guessing.
                    pdbStream.Dispose();
                    peReader.Dispose();
                    peStream.Dispose();
                    return null;
                }

                var pdbReader = provider.GetMetadataReader();
                bool matches = MvidMatchesModule(peReader, pdbReader, moduleMvid);
                return new AssemblyDebugInfo(pdbReader, matches, provider, pdbStream, peReader, peStream);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            // Reverse order: readers before the streams they read from.
            for (int i = this.owned.Length - 1; i >= 0; i--)
            {
                try
                {
                    this.owned[i]?.Dispose();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Disposal of debug-info handles must never propagate: resolution already
                    // succeeded or failed, and throwing here would surface as an unrelated error.
                }
            }
        }

        private static Guid ReadMvid(PEReader peReader)
        {
            var mdReader = peReader.GetMetadataReader();
            return mdReader.GetGuid(mdReader.GetModuleDefinition().Mvid);
        }

        /// <summary>
        /// Verifies a sidecar PDB belongs to this module (R-C).
        /// </summary>
        // The PE's CodeView debug-directory entry carries the PDB's identity (GUID + age); the
        // portable PDB carries the same identity in its DebugMetadataHeader.Id blob (first 16 bytes =
        // the GUID). Equal GUIDs means same build. If we cannot read either side we FAIL CLOSED
        // (report mismatch) rather than assume a match — an unverifiable PDB is exactly the case R-C
        // exists to reject.
        private static bool MvidMatchesModule(PEReader peReader, MetadataReader pdbReader, Guid moduleMvid)
        {
            try
            {
                var pdbId = pdbReader.DebugMetadataHeader?.Id ?? default;
                if (pdbId.IsDefaultOrEmpty || pdbId.Length < 16)
                {
                    return false;
                }

                var pdbGuid = new Guid(pdbId.AsSpan(0, 16).ToArray());

                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    if (entry.Type != DebugDirectoryEntryType.CodeView)
                    {
                        continue;
                    }

                    var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                    return codeView.Guid == pdbGuid;
                }

                // No CodeView entry to compare against: fall back to the module Mvid, which the
                // deterministic-build toolchain also uses as the PDB id.
                return pdbGuid == moduleMvid;
            }
            catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
            {
                return false;
            }
        }
    }
}
