// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: Apache-2.0

namespace AWS.Distro.OpenTelemetry.DynamicInstrumentation.Instrumentation.LineLevel;

/// <summary>
/// What the native rewriter actually did with one probe, once the CLR ReJIT-compiled its target method.
/// </summary>
// WHY THIS EXISTS AT ALL. Applying a line probe is TWO steps that happen at different times: the managed side
// resolves a line to an IL offset and hands it over, and the native rewriter splices the call in later — on a
// ReJIT thread, the next time the target method runs. `AddLineProbes` returns void long before that, so a
// successful apply proves only that the REQUEST was accepted.
//
// Until this existed, the manager reported READY off its own resolution. A probe the rewriter then declined
// was reported to the operator as live and could never fire. Measured, not hypothetical: the callback
// AssemblyRef gap skipped eleven probes in one run, every one of which reported READY.
//
// MUST STAY IN LOCKSTEP WITH `LineProbeWeaveOutcome` in the forked profiler's line_probe.h — same discipline,
// and same absence of any compiler check, as <see cref="LineProbeEmissionMode"/>.
internal enum LineProbeWeaveOutcome
{
    /// <summary>
    /// No verdict yet: the target method has not been ReJIT-compiled, so the rewriter has not run.
    /// </summary>
    // THE NORMAL STATE, NOT A FAILURE. A probe on a method nobody has called sits here indefinitely, which is
    // exactly why a missing verdict must never be reported as an error. Zero on purpose: a zeroed buffer
    // element (a native call that wrote fewer entries than expected) must read as "nothing known", never as a
    // real failure reason.
    Pending = 0,

    /// <summary>The probe's call was spliced into the method body and the body was installed.</summary>
    Woven = 1,

    /// <summary>The target module could not be given an AssemblyRef to the DI callback assembly.</summary>
    CallbackAssemblyRefFailed = 2,

    /// <summary>The callback TypeRef could not be resolved or defined in the target module.</summary>
    CallbackTypeRefFailed = 3,

    /// <summary>The callback MemberRef could not be defined.</summary>
    CallbackMemberRefFailed = 4,

    /// <summary>The rate-limit gate MemberRef could not be defined (gated emission only).</summary>
    GateMemberRefFailed = 5,

    /// <summary>
    /// The captured local's type cannot be named through the corlib AssemblyRef, so no <c>box</c> token exists.
    /// </summary>
    // Reachable for a value type declared outside corlib. PdbReader.IsNameableThroughCorlib normally refuses
    // these at resolution time with a clear reason, so seeing this means the native guard caught something the
    // managed check did not — worth reporting rather than swallowing.
    BoxTypeUnresolvable = 6,

    /// <summary>The requested local slot is outside the <c>ldloc</c> operand range.</summary>
    LocalSlotOutOfRange = 7,

    /// <summary>The requested IL offset is not the start of an instruction.</summary>
    OffsetNotInstructionBoundary = 8,

    /// <summary>
    /// The requested IL offset is the structural entry of a try, handler, or filter, where an injection would
    /// fall outside its protected region.
    /// </summary>
    EhClauseBoundary = 9,

    /// <summary>The method body could not be imported for rewriting.</summary>
    ImportFailed = 10,

    /// <summary>
    /// The rewritten body could not be installed. Every probe on the method shares this verdict, because one
    /// Export covers the whole body.
    /// </summary>
    ExportFailed = 11,
}
