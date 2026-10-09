/*
 * Copyright The OpenTelemetry Authors
 * SPDX-License-Identifier: Apache-2.0
 *
 * PHASE-2 LINE-PROBE PoC (AWS Distro DI). See line_probe.h. ADDITIVE — no CallTarget code touched.
 */

#include "line_probe.h"

#include <algorithm> // std::remove_if (per-probe removal)
#include <map>       // std::map (per-method box-token memoization). Compiles without it here only through
                     // a transitive include; naming it keeps the MSVC and libstdc++ legs honest.
#include "clr_helpers.h"
#include "cor_profiler.h"
#include "il_rewriter.h"
#include "il_rewriter_wrapper.h"
#include "logger.h"
#include "module_metadata.h"

namespace trace
{

//
// LineProbeWeaveLog — the per-probe record of what the rewriter actually did. See line_probe.h for why
// this exists at all (AddLineProbes cannot report a weave outcome it has not happened yet).
//

namespace
{
// TOUCHED FROM TWO THREADS: a CLR ReJIT thread publishes through Record while whichever managed thread
// drives the configuration poll reads through Snapshot and erases through Forget. std::map is not
// thread-safe for concurrent insert+read, and an insert can rebalance the tree under an in-flight
// iteration — so every entry point below locks. Same discipline, and the same reason, as
// LineProbeRejitHandlerModuleMethod::m_requestsLock.
std::mutex             g_weave_log_lock;
std::map<INT32, INT32> g_weave_log; // probeId -> LineProbeWeaveOutcome
} // namespace

void LineProbeWeaveLog::Record(const std::vector<LineProbeWeaveResult>& results)
{
    std::scoped_lock<std::mutex> lock(g_weave_log_lock);
    for (const auto& result : results)
    {
        g_weave_log[result.probeId] = result.outcome;
    }
}

void LineProbeWeaveLog::Forget(INT32 probeId)
{
    std::scoped_lock<std::mutex> lock(g_weave_log_lock);
    g_weave_log.erase(probeId);
}

INT32 LineProbeWeaveLog::Snapshot(LineProbeWeaveResult* buffer, INT32 capacity)
{
    std::scoped_lock<std::mutex> lock(g_weave_log_lock);

    INT32 written = 0;
    if (buffer != nullptr)
    {
        for (const auto& entry : g_weave_log)
        {
            if (written >= capacity)
            {
                break;
            }

            buffer[written].probeId = entry.first;
            buffer[written].outcome = entry.second;
            written++;
        }
    }

    // The TOTAL, not `written`. A caller handed a short buffer must be able to tell it got a partial view;
    // returning the written count would make truncation indistinguishable from completeness, and the managed
    // side would silently stop reporting failures once the log outgrew its first guess.
    return static_cast<INT32>(g_weave_log.size());
}

//
// LineProbeRejitPreprocessor — reuses RejitPreprocessor<T> (enumeration, signature-count match,
// ReJIT enqueue). We only supply the request-specific hooks.
//

const MethodReference& LineProbeRejitPreprocessor::GetTargetMethod(const LineProbeRequest& request)
{
    return request.target_method;
}

const bool LineProbeRejitPreprocessor::GetIsDerived(const LineProbeRequest& request)
{
    return false; // no abstract/derived handling for the PoC
}

const bool LineProbeRejitPreprocessor::GetIsExactSignatureMatch(const LineProbeRequest& request)
{
    // Match on argument COUNT (with "_" wildcards) exactly like the CallTarget path, so the target
    // is disambiguated by arity — see the existing ProcessTypeDefForRejit signature comparison.
    return true;
}

const std::unique_ptr<RejitHandlerModuleMethod> LineProbeRejitPreprocessor::CreateMethod(
    const mdMethodDef       methodDef,
    RejitHandlerModule*     module,
    const FunctionInfo&     functionInfo,
    const LineProbeRequest& request)
{
    return std::make_unique<LineProbeRejitHandlerModuleMethod>(methodDef, module, functionInfo, request);
}

//
// LineProbeRejitHandlerModuleMethod
//

LineProbeRejitHandlerModuleMethod::LineProbeRejitHandlerModuleMethod(mdMethodDef             methodDef,
                                                                     RejitHandlerModule*     module,
                                                                     const FunctionInfo&     functionInfo,
                                                                     const LineProbeRequest& request) :
    RejitHandlerModuleMethod(methodDef, module, functionInfo)
{
    m_requests.push_back(request);
}

void LineProbeRejitHandlerModuleMethod::AddLineProbeRequest(const LineProbeRequest& request)
{
    std::lock_guard<std::mutex> guard(m_requestsLock);

    // Dedup by (offset, probeId): repeat config polls re-submit the same probe, and we must not weave
    // it twice into one body. A distinct offset OR distinct probeId is a genuinely new probe. The scan and
    // the push_back must be ONE critical section, or two polls carrying the same probe can both find it
    // absent and both append it.
    for (const auto& existing : m_requests)
    {
        if (existing.il_offset == request.il_offset && existing.probe_id == request.probe_id)
        {
            return;
        }
    }

    m_requests.push_back(request);
}

std::vector<LineProbeRequest> LineProbeRejitHandlerModuleMethod::GetLineProbeRequests() const
{
    std::lock_guard<std::mutex> guard(m_requestsLock);
    return m_requests;
}

size_t LineProbeRejitHandlerModuleMethod::RequestCount() const
{
    std::lock_guard<std::mutex> guard(m_requestsLock);
    return m_requests.size();
}

size_t LineProbeRejitHandlerModuleMethod::RemoveLineProbeRequest(int probeId)
{
    std::lock_guard<std::mutex> guard(m_requestsLock);

    // LineProbeRequest is not move/copy-assignable (MethodReference has const fields), so std::remove_if
    // won't work. Rebuild the vector keeping non-matching probes (push_back uses the copy CTOR, which IS
    // available). Fine for the small per-method probe counts we expect.
    std::vector<LineProbeRequest> kept;
    kept.reserve(m_requests.size());
    for (const auto& r : m_requests)
    {
        if (r.probe_id != probeId)
        {
            kept.push_back(r);
        }
    }

    const size_t removed = m_requests.size() - kept.size();
    m_requests           = std::move(kept);
    return removed;
}

MethodRewriter* LineProbeRejitHandlerModuleMethod::GetMethodRewriter()
{
    return LineProbeMethodRewriter::Instance();
}

//
// LineProbeMethodRewriter — the make-or-break entry. Splice `ldc.i4 <probeId>; call <cb>` at the
// hardcoded interior offset and hand the new body back through the standard Export() path.
//

HRESULT LineProbeMethodRewriter::Rewrite(RejitHandlerModule* moduleHandler, RejitHandlerModuleMethod* methodHandler)
{
    auto lineMethodHandler = static_cast<LineProbeRejitHandlerModuleMethod*>(methodHandler);

    // BY VALUE. The managed side can add or remove probes on another thread while this rewrite is in
    // flight, so iterating the handler's live vector would be a use-after-free. This snapshot is the set
    // that gets woven; a probe added a moment later arrives with its own ReJIT.
    const auto requests = lineMethodHandler->GetLineProbeRequests();
    if (requests.empty())
    {
        Logger::Warn("LineProbeMethodRewriter::Rewrite: no LineProbeRequests.");
        return S_FALSE;
    }

    // PER-PROBE WEAVE OUTCOMES, accumulated locally and published ONCE at the end rather than as each probe
    // is decided. Two reasons it cannot be published incrementally:
    //   * Export is all-or-nothing. A probe marked WOVEN mid-loop is only really woven if Export succeeds, so
    //     publishing early would tell the operator a probe is live during the window before Export fails —
    //     and, if Export never succeeds, forever.
    //   * The managed reader would otherwise observe a half-finished pass and report an ERROR for a probe the
    //     rewriter was still working through.
    std::vector<LineProbeWeaveResult> outcomes;
    outcomes.reserve(requests.size());

    // GAP-3 FIX (per-probe callback resolution). Callback + emission-mode resolution now happens
    // INSIDE the per-probe loop, so probes with DIFFERENT callbacks and DIFFERENT emission modes can
    // coexist on ONE method. Previously all of this was resolved ONCE from requests[0], so every probe
    // on a method silently inherited the FIRST probe's callback and mode. That was proven live by
    // poc/R9RemovalUnderLoadE2E STEP 7: four mixed-mode probes all fired 200/200, but all of them
    // called probe[0]'s one-arg `Probe`, so the gate never ran and no boxed value ever arrived.
    // The metadata handles below are genuinely per-METHOD and stay hoisted.
    auto corProfiler = trace::profiler;

    ModuleID        module_id       = moduleHandler->GetModuleId();
    ModuleMetadata& module_metadata = *moduleHandler->GetModuleMetadata();
    FunctionInfo*   caller          = methodHandler->GetFunctionInfo();
    mdToken         function_token  = caller->id;
    auto            metaEmit        = module_metadata.metadata_emit;
    auto            metaImport      = module_metadata.metadata_import;
    auto            assemblyImport  = module_metadata.assembly_import;
    auto            assemblyEmit    = module_metadata.assembly_emit;

    Logger::Info("*** LineProbe_Rewrite() Start: ", caller->type.name, ".", caller->name, "() with ",
                 requests.size(), " probe(s) — resolving callbacks PER PROBE.");

    // ---- 1. Resolve the corlib box TypeRef ONCE per method (it is probe-independent). ----
    // Lazily emitted on first use below so a method whose probes need no box does no metadata work.
    mdTypeRef systemInt32TypeRef = mdTypeRefNil;
    bool      int32BoxResolved   = false;
    bool      int32BoxFailed     = false;
    HRESULT   hr                 = S_OK;

    // Box tokens are resolved PER TYPE NAME and memoized for this method, because several probes in one
    // body may capture locals of different types. The empty name means System.Int32 (the historical
    // behavior every pre-existing harness relies on).
    std::map<WSTRING, mdTypeRef> boxTokenCache;

    // True only for a name that a corlib-scoped TypeRef can actually DENOTE.
    //
    // WHY A CHECK IS NEEDED AT ALL. DefineTypeRefByName resolves nothing — it appends a TypeRef row for
    // whatever name it is handed, which is exactly why this file uses it as the fallback when FindTypeRef
    // reports "record not found" below. So it cannot report a bad name by failing. Without this test, a
    // customer's enum local (`MyApp.Color`: IsValueType, so it needs a box) emitted
    // `box [System.Private.CoreLib]MyApp.Color`; the JIT resolves a box operand when it compiles the method,
    // so the CUSTOMER'S METHOD died with TypeLoadException for every caller — strictly worse than the
    // wrong-value bug the typed-box work replaced. Nullable<int> (a TypeSpec, not a TypeRef-by-name) and a
    // value type nested in another type fail the same way.
    //
    // THIS IS THE LAST LINE OF DEFENSE, NOT THE ONLY ONE. PdbReader.IsNameableThroughCorlib is the
    // authoritative check: it tests real assembly identity, IsGenericType and IsNested, and refuses the probe
    // with a reason the operator can see. This one exists because AddLineProbes is an exported ABI that must
    // not corrupt a method body for a caller that is not LineProbeTranslator. A name cannot prove assembly
    // identity, so it is deliberately conservative and rejects anything it cannot vouch for.
    auto isCorlibNameableType = [](const WSTRING& name) -> bool {
        // compare() clamps to the string's length, so a name shorter than "System." is simply unequal.
        if (name.compare(0, 7, WStr("System.")) != 0)
        {
            return false;
        }

        if (name.find_first_of(WStr("`[]+*&,")) != WSTRING::npos)
        {
            return false;
        }

        // Exactly ONE dot, i.e. `System.<Name>`. A deeper namespace is the tell that the type is not in
        // corlib at all: System.Numerics.BigInteger, System.Numerics.Vector3, System.Drawing.Point and
        // System.Data.SqlTypes.SqlInt32 are all plain, non-generic, non-nested value types that pass a bare
        // `System.` prefix test and live in OTHER assemblies — so each would still produce a corlib TypeRef
        // the JIT cannot resolve. Every value type this path is meant to serve (Int32, Int64, Double,
        // Decimal, DateTime, DateTimeOffset, TimeSpan, Guid, Boolean, Char) is a single segment.
        return name.find(WStr("."), 7) == WSTRING::npos;
    };

    // Resolves a corlib TypeRef for `typeName` to use as a `box` token. Only corlib types are resolvable
    // this way: DefineTypeRefByName against the corlib AssemblyRef cannot name a type that lives in the
    // customer's own assembly or a third-party one. That is a real limitation, and it is why the caller
    // treats a failure as "skip this probe" rather than "emit something and hope".
    auto resolveBoxToken = [&](const WSTRING& typeName, mdTypeRef* out) -> bool {
        const WSTRING& effective = typeName.empty() ? WStr("System.Int32") : typeName;

        auto cached = boxTokenCache.find(effective);
        if (cached != boxTokenCache.end())
        {
            *out = cached->second;
            return cached->second != mdTypeRefNil;
        }

        if (!isCorlibNameableType(effective))
        {
            Logger::Warn("*** LineProbe_Rewrite(): box type '", effective,
                         "' cannot be named through the corlib AssemblyRef (not a plain System.* type). "
                         "Refusing to emit a box against an unresolvable TypeRef.");
            boxTokenCache[effective] = mdTypeRefNil;
            *out                     = mdTypeRefNil;
            return false;
        }

        mdAssemblyRef corlibRef = mdAssemblyRefNil;
        HRESULT       hrBox = GetCorLibAssemblyRef(module_metadata.assembly_emit,
                                                   *module_metadata.corAssemblyProperty, &corlibRef);
        if (FAILED(hrBox))
        {
            Logger::Warn("*** LineProbe_Rewrite(): GetCorLibAssemblyRef failed for box token.");
            boxTokenCache[effective] = mdTypeRefNil;
            *out                     = mdTypeRefNil;
            return false;
        }

        mdTypeRef resolved = mdTypeRefNil;
        hrBox              = metaEmit->DefineTypeRefByName(corlibRef, effective.c_str(), &resolved);
        if (FAILED(hrBox))
        {
            Logger::Warn("*** LineProbe_Rewrite(): DefineTypeRefByName failed for box type ", effective);
            boxTokenCache[effective] = mdTypeRefNil;
            *out                     = mdTypeRefNil;
            return false;
        }

        boxTokenCache[effective] = resolved;
        *out                     = resolved;
        return true;
    };

    auto resolveInt32Box = [&]() -> bool {
        if (int32BoxResolved)
        {
            return true;
        }
        if (int32BoxFailed)
        {
            return false;
        }

        if (!resolveBoxToken(WStr("System.Int32"), &systemInt32TypeRef))
        {
            int32BoxFailed = true;
            return false;
        }

        int32BoxResolved = true;
        return true;
    };

    // ---- 2. Import the method body ONCE, then weave EVERY probe for this method into it. ----
    // One Import + one Export per ReJIT, with per-probe callback resolution + offset-find + emit in
    // between. ILRewriterWrapper::SetILPosition is re-pointed for each probe, so N probes at N offsets
    // all land in a single rewritten body — "N edits, 1 ReJIT".
    ILRewriter rewriter(corProfiler->info_, methodHandler->GetFunctionControl(), module_id, function_token);
    hr = rewriter.Import();
    if (FAILED(hr))
    {
        Logger::Warn("*** LineProbe_Rewrite(): ILRewriter.Import() failed for ", module_id, " ", function_token);

        // EVERY probe on this method failed, not just one. Recorded rather than left PENDING because Import
        // failing is permanent for this body — a caller that saw PENDING would wait for a verdict forever.
        for (const auto& req : requests)
        {
            outcomes.push_back({req.probe_id, LINE_WEAVE_FAILED_IMPORT});
        }

        LineProbeWeaveLog::Record(outcomes);
        return S_FALSE;
    }

    ILRewriterWrapper reWriterWrapper(&rewriter);
    int wovenCount = 0;
    for (const auto& req : requests)
    {
        const LineProbeRequest* request = &req; // per-probe view

    Logger::Info("*** LineProbe_Rewrite():   probe ilOffset=", request->il_offset, " probeId=", request->probe_id,
                 " hoistedFieldToken=", request->hoisted_field_token, " emissionMode=", request->emission_mode,
                 " callback=[", request->callback_assembly, "]", request->callback_type, ".",
                 request->callback_method);

    // ---- 2a. PER-PROBE callback resolution (GAP-3 FIX). Each probe names its OWN callback
    //          assembly/type/method and its OWN emission mode, so all of this must be resolved inside
    //          the loop. A failure here skips ONLY this probe (`continue`), matching the per-probe
    //          fail-safe already used for bad offsets — one malformed probe must not discard the
    //          method's other, valid probes.
    // The managed side sends a FULL DISPLAY NAME here ("Name, Version=..., Culture=..., PublicKeyToken=..."),
    // which AssemblyReference parses. A bare simple name still parses — name only, version 0.0.0.0, empty key —
    // which keeps the pre-existing spike harnesses working: they pass a simple name AND their target assembly
    // has a compile-time reference, so the search below always succeeds and the define path is never reached.
    const AssemblyReference* callbackAssembly = AssemblyReference::GetFromCache(request->callback_assembly);

    mdAssemblyRef callbackAssemblyRef = mdAssemblyRefNil;
    for (mdAssemblyRef candidate : EnumAssemblyRefs(assemblyImport))
    {
        const auto& asmMeta = GetReferencedAssemblyMetadata(assemblyImport, candidate);
        if (asmMeta.name == callbackAssembly->name)
        {
            callbackAssemblyRef = candidate;
            break;
        }
    }

    // DEFINE THE REF IF THE MODULE DOES NOT ALREADY HAVE ONE, rather than skipping the probe.
    //
    // A customer assembly has no compile-time reference to the DI assembly — verified: the demo's SampleApp.dll
    // references only System.Runtime/System.Console/System.Threading.Thread. Line-level nevertheless worked in
    // every E2E because a FUNCTION-level probe on the same module ran first, and the CallTarget weave emits a
    // TypeRef to DiIntegrationN, which forces the AssemblyRef into that module's metadata as a side effect.
    // So this search only ever succeeded by accident of ordering. A module carrying ONLY line-level probes hit
    // the `continue` below and was silently never woven — a probe reporting READY that can never fire.
    //
    // Mirrors calltarget_tokens.cpp's EnsureBaseCalltargetTokens, which defines its profiler AssemblyRef the
    // same way. Upstream's MetadataBuilder::FindIntegrationTypeRef has the identical gap, still marked
    // "TODO: emit assembly reference if not found?" — this is that TODO, for the line-probe path.
    if (callbackAssemblyRef == mdAssemblyRefNil)
    {
        ASSEMBLYMETADATA callbackAssemblyMetadata{};
        callbackAssemblyMetadata.usMajorVersion   = callbackAssembly->version.major;
        callbackAssemblyMetadata.usMinorVersion   = callbackAssembly->version.minor;
        callbackAssemblyMetadata.usBuildNumber    = callbackAssembly->version.build;
        callbackAssemblyMetadata.usRevisionNumber = callbackAssembly->version.revision;

        if (callbackAssembly->locale == WStr("neutral"))
        {
            callbackAssemblyMetadata.szLocale = const_cast<WCHAR*>(WStr("\0"));
            callbackAssemblyMetadata.cbLocale = 0;
        }
        else
        {
            callbackAssemblyMetadata.szLocale = const_cast<WCHAR*>(callbackAssembly->locale.c_str());
            callbackAssemblyMetadata.cbLocale = (DWORD)(callbackAssembly->locale.size());
        }

        // A STRONG-NAMED assembly must carry its public key token or the emitted ref binds to nothing at
        // runtime — the probe would weave a call that resolves to no method. Zero length is only correct for an
        // unsigned assembly, which is why the size is derived from the parsed key rather than hardcoded.
        DWORD publicKeySize = kPublicKeySize;
        if (callbackAssembly->public_key == trace::PublicKey())
        {
            publicKeySize = 0;
        }

        hr = assemblyEmit->DefineAssemblyRef(&callbackAssembly->public_key.data, publicKeySize,
                                             callbackAssembly->name.c_str(), &callbackAssemblyMetadata,
                                             // hash blob, size, flags
                                             nullptr, 0, 0, &callbackAssemblyRef);

        if (FAILED(hr) || callbackAssemblyRef == mdAssemblyRefNil)
        {
            Logger::Warn("*** LineProbe_Rewrite(): could not DEFINE callback AssemblyRef for '",
                         request->callback_assembly, "' (hr=", HResultStr(hr), "). Skipping probeId=",
                         request->probe_id);
            outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_CALLBACK_ASSEMBLY_REF});
            continue;
        }

        Logger::Info("*** LineProbe_Rewrite(): defined a new callback AssemblyRef for '",
                     callbackAssembly->name, "' in the target module (it had none).");
    }

    mdTypeRef callbackTypeRef = mdTypeRefNil;
    hr = metaImport->FindTypeRef(callbackAssemblyRef, request->callback_type.c_str(), &callbackTypeRef);
    if (hr == HRESULT(0x80131130) /* record not found on lookup */ || callbackTypeRef == mdTypeRefNil)
    {
        hr = metaEmit->DefineTypeRefByName(callbackAssemblyRef, request->callback_type.c_str(), &callbackTypeRef);
    }
    if (FAILED(hr))
    {
        Logger::Warn("*** LineProbe_Rewrite(): could not resolve callback TypeRef for ", request->callback_type,
                     ". Skipping probeId=", request->probe_id);
        outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_CALLBACK_TYPE_REF});
        continue;
    }

    // ASYNC (DECISION B): a hoisted-field token means read a HOISTED LOCAL off the async state machine
    //   `ldarg.0; ldfld <hoistedField>; box <System.Int32>; call CaptureLocal(int32, object)`.
    const bool isAsyncHoistedCapture = (request->hoisted_field_token != mdTokenNil);

    // BOX-GATE (DECISION A): two box-emitting modes with a `Capture(int32, object)` callback.
    // GUARDED AGAINST A HOISTED TOKEN FOR THE SAME REASON isLocalCapture IS, AND THE BUG HERE WAS WORSE.
    // The emission chain below tests isGatedBox/isUngatedBox BEFORE the async case, while token resolution
    // tests the async case first. So a request carrying BOTH a box-gate mode and a hoisted token resolved down
    // the async branch — leaving systemInt32TypeRef at mdTypeRefNil (0x01000000: a TypeRef with RID 0) — and
    // then emitted `box <nil>` from the gate branch, which is the invalid-token crash described above. Before
    // the typed-box work, systemInt32TypeRef was resolved for EVERY path that emits a box, so this could not
    // happen; making the flags mutually exclusive restores that invariant no matter what the managed side
    // sends. Such a request is contradictory anyway: a hoisted field is a real variable, not the constant a
    // gate path materializes, so the async emission is the meaningful reading of it.
    const bool isGatedBox   = (request->emission_mode == LINE_EMIT_GATED_BOX) && !isAsyncHoistedCapture;
    const bool isUngatedBox = (request->emission_mode == LINE_EMIT_UNGATED_BOX) && !isAsyncHoistedCapture;
    const bool isBoxGate    = isGatedBox || isUngatedBox;

    // SYNC LOCAL CAPTURE — `ldc.i4 probeId; ldloc <slot>; box; call CaptureLocal(int32,object)`.
    //
    // MUTUALLY EXCLUSIVE WITH THE ASYNC PATH BY CONSTRUCTION. The emission chain below tests isLocalCapture
    // BEFORE isAsyncHoistedCapture, so a request carrying BOTH a LOCAL_CAPTURE mode and a hoisted token would
    // silently emit `ldloc <slot>` on a state machine whose variable lives in a FIELD — reading an unrelated
    // slot and boxing it. The managed side sends Legacy + token for async, but this makes the invariant hold
    // regardless of what the managed side sends.
    const bool isLocalCapture = (request->emission_mode == LINE_EMIT_LOCAL_CAPTURE) && !isAsyncHoistedCapture;

    // box_value doubles as the local slot index for a LOCAL_CAPTURE probe, and it is a SIGNED INT32 on the
    // ABI. A negative value casts to a huge unsigned, falls past LoadLocal's <=3 and <=255 forms, and is
    // TRUNCATED into the 16-bit operand of `ldloc` — reading an arbitrary slot in the customer's frame and
    // boxing whatever it holds against the intended type's token. LineProbeTranslator clamps this today, so
    // this guards the exported ABI rather than the current caller.
    if (isLocalCapture && (request->box_value < 0 || request->box_value > 0xFFFF))
    {
        Logger::Warn("*** LineProbe_Rewrite(): local slot ", request->box_value,
                     " is out of range for probeId=", request->probe_id, ". Skipping.");
        outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_LOCAL_SLOT_RANGE});
        continue;
    }

    // These three groups need a two-arg `(int32, object)` callback.
    const bool needsTwoArgCallback = isAsyncHoistedCapture || isBoxGate || isLocalCapture;

    // NON-INT LOCAL CAPTURE. Which box token (if any) this probe needs:
    //  - LOCAL_CAPTURE or ASYNC_HOISTED of a REFERENCE type -> NO box at all. The slot/field already holds
    //    an object reference; `box` on one is invalid IL and the verifier would reject the rewritten body.
    //  - LOCAL_CAPTURE or ASYNC_HOISTED of a VALUE type     -> box against THAT type's token, not Int32.
    //  - the box-gate spike paths                           -> System.Int32 (they materialize a constant).
    //
    // The async path is typed EXACTLY like the sync one because a hoisted field is just a relocated local:
    // `<note>5__2` is a System.String and `<stamp>5__3` a System.DateTime. Boxing either against a
    // hardcoded System.Int32 is undefined behavior in the CUSTOMER'S method, not a lost snapshot — the
    // sync path proved this by crashing with `TypeLoadException: Could not load type 'Invalid_Token...'`.
    mdTypeRef boxTypeRef = mdTypeRefNil;
    bool      needsBox   = needsTwoArgCallback;

    if (isLocalCapture || isAsyncHoistedCapture)
    {
        needsBox = request->local_is_value_type;
        if (needsBox && !resolveBoxToken(request->local_type_name, &boxTypeRef))
        {
            // Unresolvable box type — e.g. a value type declared in the customer's own assembly, which
            // cannot be named through the corlib AssemblyRef. Skip THIS probe rather than emit a `box`
            // against a wrong or nil token: a bad box corrupts the method body, while a skipped probe just
            // captures nothing.
            Logger::Warn("*** LineProbe_Rewrite(): cannot resolve box token for local type '",
                         request->local_type_name, "'. Skipping probeId=", request->probe_id);
            outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_BOX_TYPE});
            continue;
        }
    }
    else if (needsBox)
    {
        if (!resolveInt32Box())
        {
            Logger::Warn("*** LineProbe_Rewrite(): no box token available. Skipping probeId=", request->probe_id);
            outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_BOX_TYPE});
            continue;
        }

        boxTypeRef = systemInt32TypeRef;
    }

    // Build THIS probe's callback MemberRef signature.
    //   sync single-call:    static void Probe(int32)            -> [DEFAULT][argc=1][VOID][I4]
    //   async / box capture: static void Capture(int32, object)  -> [DEFAULT][argc=2][VOID][I4][OBJECT]
    COR_SIGNATURE callbackSignature[8];
    ULONG         callbackSigLen = 0;
    callbackSignature[callbackSigLen++] = IMAGE_CEE_CS_CALLCONV_DEFAULT;
    if (needsTwoArgCallback)
    {
        callbackSignature[callbackSigLen++] = 0x02; // paramCount
        callbackSignature[callbackSigLen++] = ELEMENT_TYPE_VOID;
        callbackSignature[callbackSigLen++] = ELEMENT_TYPE_I4;
        callbackSignature[callbackSigLen++] = ELEMENT_TYPE_OBJECT;
    }
    else
    {
        callbackSignature[callbackSigLen++] = 0x01; // paramCount
        callbackSignature[callbackSigLen++] = ELEMENT_TYPE_VOID;
        callbackSignature[callbackSigLen++] = ELEMENT_TYPE_I4;
    }

    mdMemberRef callbackMemberRef = mdMemberRefNil;
    hr = metaEmit->DefineMemberRef(callbackTypeRef, request->callback_method.c_str(), callbackSignature,
                                   callbackSigLen, &callbackMemberRef);
    if (FAILED(hr))
    {
        Logger::Warn("*** LineProbe_Rewrite(): DefineMemberRef failed for ", request->callback_method,
                     ". Skipping probeId=", request->probe_id);
        outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_CALLBACK_MEMBER_REF});
        continue;
    }

    // GATED mode: also resolve THIS probe's gate `bool ShouldCapture(int32)` on its own callback type.
    //   [DEFAULT][argc=1][ret=BOOLEAN][arg0=I4]
    mdMemberRef gateMemberRef = mdMemberRefNil;
    if (isGatedBox)
    {
        COR_SIGNATURE gateSignature[4];
        gateSignature[0] = IMAGE_CEE_CS_CALLCONV_DEFAULT;
        gateSignature[1] = 0x01;
        gateSignature[2] = ELEMENT_TYPE_BOOLEAN;
        gateSignature[3] = ELEMENT_TYPE_I4;

        hr = metaEmit->DefineMemberRef(callbackTypeRef, request->gate_method.c_str(), gateSignature,
                                       sizeof(gateSignature), &gateMemberRef);
        if (FAILED(hr))
        {
            Logger::Warn("*** LineProbe_Rewrite(): DefineMemberRef failed for gate method ",
                         request->gate_method, ". Skipping probeId=", request->probe_id);
            outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_GATE_MEMBER_REF});
            continue;
        }
    }

    // FAIL-SAFE: GetInstrFromOffset returns COR_E_INVALIDPROGRAM for any offset that is NOT the start
    // of an instruction (the sparse offset->instr map is NULL there). A bad/mid-instruction offset
    // therefore skips this probe with the ORIGINAL body left intact — no partial rewrite is exported.
    ILInstr* targetInstr = nullptr;
    hr = rewriter.GetInstrFromOffset(request->il_offset, &targetInstr);
    if (FAILED(hr) || targetInstr == nullptr)
    {
        // PER-PROBE FAIL-SOFT: skip THIS probe (bad offset), keep weaving the rest — a per-probe fail-safe rather than
        // aborting the whole method. Others already emitted into this rewriter remain.
        Logger::Warn("*** LineProbe_Rewrite(): il_offset ", request->il_offset,
                     " is not an instruction boundary. Skipping this probe. HR=", HResultStr(hr));
        outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_OFFSET_NOT_INSTR});
        continue;
    }

    // EH SAFETY GATE: refuse to inject at an EH-clause STRUCTURAL boundary. InsertBefore(targetInstr)
    // prepends the probe IL to targetInstr, so if targetInstr is the FIRST instruction of a try, a
    // handler, or a filter, the injected sequence would fall OUTSIDE the clause it belongs to (an EH
    // region must begin exactly at its first instruction — the injected code would either land in the
    // wrong protected region or violate the "empty eval stack at region entry" rule).
    // We compare INSTRUCTION POINTERS: `targetInstr` was resolved
    // from the same offset->instr map (m_pOffsetToInstr) that ImportEH used to set the clause-begin
    // pointers, so a pointer match means the requested offset is exactly that clause boundary. (Note:
    // ILInstr::m_offset is only assigned during Export, so an offset compare here would be wrong.)
    bool ehBoundary = false;
    for (unsigned iEH = 0; iEH < rewriter.GetEHCount(); iEH++)
    {
        const EHClause& clause = rewriter.GetEHPointer()[iEH];
        const bool isFilter    = (clause.m_Flags & COR_ILEXCEPTION_CLAUSE_FILTER) != 0;
        if (targetInstr == clause.m_pTryBegin || targetInstr == clause.m_pHandlerBegin ||
            (isFilter && targetInstr == clause.m_pFilter))
        {
            Logger::Warn("*** LineProbe_Rewrite(): il_offset ", request->il_offset,
                         " is an EH-clause boundary (try/handler/filter entry). Skipping this probe "
                         "— it would fall outside its protected region.");
            ehBoundary = true;
            break;
        }
    }

    if (ehBoundary)
    {
        outcomes.push_back({request->probe_id, LINE_WEAVE_FAILED_EH_BOUNDARY});
        continue; // skip this probe, keep the rest
    }

    // ---- 2b. Emit THIS probe's capture sequence BEFORE the target instruction. ----
    // Every instruction the wrapper emits is inserted BEFORE targetInstr, so targetInstr is the
    // natural "SKIP:" label — the first original instruction executed after the injected sequence.
    reWriterWrapper.SetILPosition(targetInstr);

    if (isGatedBox)
    {
        // BOX-GATE SPIKE (DECISION A / A1) — the make-or-break interior conditional-branch emission:
        //   ldc.i4  <probeId>              ; gate arg
        //   call    bool ShouldCapture(i4) ; cheap managed gate, NO allocation
        //   brfalse.s SKIP                 ; if gate says no -> jump PAST the box+Capture
        //   ldc.i4  <probeId>              ; Capture arg0
        //   ldc.i4  <boxValue>             ; the value-type to capture
        //   box     System.Int32          ; <-- the allocation we are gating; only runs if gate=true
        //   call    void Capture(i4, obj)
        // SKIP: (== targetInstr, the original instruction)
        // The `brfalse` target is a REAL ILInstr (targetInstr); Export() recomputes the branch delta
        // and, if it no longer fits INT8, auto-promotes brfalse.s -> brfalse (the `goto again` pass).
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.CallMember(gateMemberRef, /* is_virtual */ false);

        ILInstr* brFalse   = reWriterWrapper.CreateInstr(CEE_BRFALSE_S);
        brFalse->m_pTarget = targetInstr; // SKIP label = the original interior instruction

        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.LoadInt32(request->box_value);
        reWriterWrapper.Box(systemInt32TypeRef);
        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }
    else if (isUngatedBox)
    {
        // A2 contrast case: box + Capture with NO gate. Allocates on EVERY execution — the exact
        // hot-line hazard DECISION A is about. Used to prove the gate (A1) is what avoids the alloc.
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.LoadInt32(request->box_value);
        reWriterWrapper.Box(systemInt32TypeRef);
        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }
    else if (isLocalCapture)
    {
        // SYNC LOCAL CAPTURE: read a normal stack local and box it, so we capture a REAL value
        // (e.g. the loop variable) at an interior statement boundary in a method with real control flow.
        //   ldc.i4 <probeId>        ; arg0 = probeId
        //   ldloc  <slot>           ; the local to capture (slot index carried in box_value)
        //   box    System.Int32
        //   call   CaptureLocal(int32, object)
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.LoadLocal(static_cast<unsigned>(request->box_value));

        // Box ONLY a value type. A reference-type local is already an object reference, so `ldloc` alone
        // satisfies the callback's `object` parameter — emitting `box` there would be invalid IL.
        if (request->local_is_value_type)
        {
            reWriterWrapper.Box(boxTypeRef);
        }

        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }
    else if (isAsyncHoistedCapture)
    {
        // ASYNC / ITERATOR emission (hoisted locals are read off the state machine). At a mid-MoveNext
        // offset:
        //   ldc.i4 <probeId>          ; arg0 = probeId
        //   ldarg.0                   ; `this` == the state-machine instance
        //   ldfld  <hoistedFieldTok>  ; read the hoisted local (this.<total>5__2)
        //   box    <the field's type> ; ONLY for a value type
        //   call   CaptureLocal(int32, object)
        //
        // `ldarg.0` is correct for BOTH state-machine shapes: MEASURED on net8.0, Roslyn emits a STRUCT
        // state machine in Release and a CLASS in Debug. For the struct, arg 0 is a managed pointer
        // (`&this`), and `ldfld` accepts a managed pointer as well as an object reference — so no
        // `ldobj`/`ldflda` variation is needed between configurations.
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.LoadArgument(0); // ldarg.0 -> the state machine `this`
        ILInstr* ldfld = reWriterWrapper.CreateInstr(CEE_LDFLD);
        ldfld->m_Arg32 = request->hoisted_field_token;

        // Box ONLY a value-type field, and against its OWN token — same rule and same reason as the sync
        // local path above. A reference-type field is already an object reference.
        if (request->local_is_value_type)
        {
            reWriterWrapper.Box(boxTypeRef);
        }

        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }
    else
    {
        // Proven Phase-2 sync path: `ldc.i4 <probeId>; call Probe(int32)`.
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }

    wovenCount++;
    outcomes.push_back({request->probe_id, LINE_WEAVE_WOVEN});
    Logger::Info("*** LineProbe_Rewrite(): wove probeId=", request->probe_id, " at ilOffset=",
                 request->il_offset, " (", wovenCount, "/", requests.size(), ") in ", caller->type.name, ".",
                 caller->name, "()");
    } // end per-probe loop

    if (wovenCount == 0)
    {
        // PER-PROBE REMOVAL: reaching here with an EMPTY request set (vs all-offsets-skipped) is the
        // remove-to-zero case. ReJIT recompiled from the ORIGINAL body and we injected nothing, so
        // Export()ing now writes back the pristine method — a clean physical un-instrument. (If instead
        // requests was non-empty but every offset was skipped, Export of the untouched-original is
        // still correct: no partial state.) Fall through to Export rather than returning S_FALSE.
        Logger::Info("*** LineProbe_Rewrite(): 0 probes to weave (removal); exporting ORIGINAL body.");
    }

    // ---- 3. Export ONCE — recomputes offsets/branch deltas/EH extents/.maxstack globally for ALL
    //         injected probes at once (Q1). One Export per method regardless of probe count. ----
    hr = rewriter.Export();
    if (FAILED(hr))
    {
        Logger::Warn("*** LineProbe_Rewrite(): ILRewriter.Export() failed for ModuleID=", module_id, " ",
                     function_token);

        // NOTHING reached the customer's method: the rewritten body was never installed, so every probe this
        // pass had marked WOVEN is in fact not woven. Downgrade only those — a probe that already failed for
        // its own reason keeps that reason, which is the more useful one to show an operator.
        for (auto& outcome : outcomes)
        {
            if (outcome.outcome == LINE_WEAVE_WOVEN)
            {
                outcome.outcome = LINE_WEAVE_FAILED_EXPORT;
            }
        }

        LineProbeWeaveLog::Record(outcomes);
        return S_FALSE;
    }

    // PUBLISHED ONLY HERE ON THE SUCCESS PATH — after Export has actually installed the body. Everything
    // above this line is provisional.
    LineProbeWeaveLog::Record(outcomes);

    Logger::Info("*** LineProbe_Rewrite() Finished: wove ", wovenCount, " of ", requests.size(),
                 " probe(s) into ", caller->type.name, ".", caller->name, "() in a single Import/Export.");
    return S_OK;
}

} // namespace trace
