/*
 * Copyright The OpenTelemetry Authors
 * SPDX-License-Identifier: Apache-2.0
 *
 * PHASE-2 LINE-PROBE PoC (AWS Distro DI). See line_probe.h. ADDITIVE — no CallTarget code touched.
 */

#include "line_probe.h"

#include <algorithm> // std::remove_if (N2 removal)
#include "clr_helpers.h"
#include "cor_profiler.h"
#include "il_rewriter.h"
#include "il_rewriter_wrapper.h"
#include "logger.h"
#include "module_metadata.h"

namespace trace
{

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
    // Dedup by (offset, probeId): repeat config polls re-submit the same probe, and we must not weave
    // it twice into one body. A distinct offset OR distinct probeId is a genuinely new probe.
    for (const auto& existing : m_requests)
    {
        if (existing.il_offset == request.il_offset && existing.probe_id == request.probe_id)
        {
            return;
        }
    }

    m_requests.push_back(request);
}

const std::vector<LineProbeRequest>& LineProbeRejitHandlerModuleMethod::GetLineProbeRequests() const
{
    return m_requests;
}

size_t LineProbeRejitHandlerModuleMethod::RemoveLineProbeRequest(int probeId)
{
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

    m_requests = std::move(kept);
    return m_requests.size();
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
    auto        lineMethodHandler = static_cast<LineProbeRejitHandlerModuleMethod*>(methodHandler);
    const auto& requests          = lineMethodHandler->GetLineProbeRequests();
    if (requests.empty())
    {
        Logger::Warn("LineProbeMethodRewriter::Rewrite: no LineProbeRequests.");
        return S_FALSE;
    }

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

    Logger::Info("*** LineProbe_Rewrite() Start: ", caller->type.name, ".", caller->name, "() with ",
                 requests.size(), " probe(s) — resolving callbacks PER PROBE.");

    // ---- 1. Resolve the corlib box TypeRef ONCE per method (it is probe-independent). ----
    // Lazily emitted on first use below so a method whose probes need no box does no metadata work.
    mdTypeRef systemInt32TypeRef = mdTypeRefNil;
    bool      int32BoxResolved   = false;
    bool      int32BoxFailed     = false;
    HRESULT   hr                 = S_OK;

    auto resolveInt32Box = [&]() -> bool {
        if (int32BoxResolved)
        {
            return true;
        }
        if (int32BoxFailed)
        {
            return false;
        }

        mdAssemblyRef corlibRef = mdAssemblyRefNil;
        HRESULT       hrBox = GetCorLibAssemblyRef(module_metadata.assembly_emit,
                                                   *module_metadata.corAssemblyProperty, &corlibRef);
        if (FAILED(hrBox))
        {
            Logger::Warn("*** LineProbe_Rewrite(): GetCorLibAssemblyRef failed for box token.");
            int32BoxFailed = true;
            return false;
        }

        hrBox = metaEmit->DefineTypeRefByName(corlibRef, WStr("System.Int32"), &systemInt32TypeRef);
        if (FAILED(hrBox))
        {
            Logger::Warn("*** LineProbe_Rewrite(): DefineTypeRefByName System.Int32 failed for box token.");
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
    mdAssemblyRef callbackAssemblyRef = mdAssemblyRefNil;
    for (mdAssemblyRef candidate : EnumAssemblyRefs(assemblyImport))
    {
        const auto& asmMeta = GetReferencedAssemblyMetadata(assemblyImport, candidate);
        if (asmMeta.name == request->callback_assembly)
        {
            callbackAssemblyRef = candidate;
            break;
        }
    }

    if (callbackAssemblyRef == mdAssemblyRefNil)
    {
        Logger::Warn("*** LineProbe_Rewrite(): callback AssemblyRef not found in target module for '",
                     request->callback_assembly, "'. Skipping probeId=", request->probe_id);
        continue;
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
        continue;
    }

    // ASYNC (DECISION B): a hoisted-field token means read a HOISTED LOCAL off the async state machine
    //   `ldarg.0; ldfld <hoistedField>; box <System.Int32>; call CaptureLocal(int32, object)`.
    const bool isAsyncHoistedCapture = (request->hoisted_field_token != mdTokenNil);

    // BOX-GATE (DECISION A): two box-emitting modes with a `Capture(int32, object)` callback.
    const bool isGatedBox   = (request->emission_mode == LINE_EMIT_GATED_BOX);
    const bool isUngatedBox = (request->emission_mode == LINE_EMIT_UNGATED_BOX);
    const bool isBoxGate    = isGatedBox || isUngatedBox;

    // G1: sync local capture — `ldc.i4 probeId; ldloc <slot>; box; call CaptureLocal(int32,object)`.
    const bool isLocalCapture = (request->emission_mode == LINE_EMIT_LOCAL_CAPTURE);

    // These three groups need a two-arg `(int32, object)` callback and a System.Int32 box token.
    const bool needsTwoArgCallback = isAsyncHoistedCapture || isBoxGate || isLocalCapture;
    const bool needsInt32Box       = needsTwoArgCallback;

    if (needsInt32Box && !resolveInt32Box())
    {
        Logger::Warn("*** LineProbe_Rewrite(): no box token available. Skipping probeId=", request->probe_id);
        continue;
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
        // N2: skip THIS probe (bad offset), keep weaving the rest — a per-probe fail-safe rather than
        // aborting the whole method. Others already emitted into this rewriter remain.
        Logger::Warn("*** LineProbe_Rewrite(): il_offset ", request->il_offset,
                     " is not an instruction boundary. Skipping this probe. HR=", HResultStr(hr));
        continue;
    }

    // G1 SAFETY GATE: refuse to inject at an EH-clause STRUCTURAL boundary. InsertBefore(targetInstr)
    // prepends the probe IL to targetInstr, so if targetInstr is the FIRST instruction of a try, a
    // handler, or a filter, the injected sequence would fall OUTSIDE the clause it belongs to (an EH
    // region must begin exactly at its first instruction — the injected code would either land in the
    // wrong protected region or violate the "empty eval stack at region entry" rule). Datadog's line
    // debugger imposes the same constraint. We compare INSTRUCTION POINTERS: `targetInstr` was resolved
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
        // G1 GATE sync local capture: read a normal stack local and box it, so we capture a REAL value
        // (e.g. the loop variable) at an interior statement boundary in a method with real control flow.
        //   ldc.i4 <probeId>        ; arg0 = probeId
        //   ldloc  <slot>           ; the local to capture (slot index carried in box_value)
        //   box    System.Int32
        //   call   CaptureLocal(int32, object)
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.LoadLocal(static_cast<unsigned>(request->box_value));
        reWriterWrapper.Box(systemInt32TypeRef);
        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }
    else if (isAsyncHoistedCapture)
    {
        // ASYNC SPIKE core emission (mirrors Datadog's AsyncLineDebuggerInvoker reading hoisted
        // locals off the state machine). At a mid-MoveNext offset:
        //   ldc.i4 <probeId>          ; arg0 = probeId
        //   ldarg.0                   ; `this` == the state-machine instance
        //   ldfld  <hoistedFieldTok>  ; read the hoisted local (this.<y>5__1)
        //   box    System.Int32       ; value-type local -> object
        //   call   CaptureLocal(int32, object)
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.LoadArgument(0); // ldarg.0 -> the state machine `this`
        ILInstr* ldfld = reWriterWrapper.CreateInstr(CEE_LDFLD);
        ldfld->m_Arg32 = request->hoisted_field_token;
        reWriterWrapper.Box(systemInt32TypeRef);
        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }
    else
    {
        // Proven Phase-2 sync path: `ldc.i4 <probeId>; call Probe(int32)`.
        reWriterWrapper.LoadInt32(request->probe_id);
        reWriterWrapper.CallMember(callbackMemberRef, /* is_virtual */ false);
    }

    wovenCount++;
    Logger::Info("*** LineProbe_Rewrite(): wove probeId=", request->probe_id, " at ilOffset=",
                 request->il_offset, " (", wovenCount, "/", requests.size(), ") in ", caller->type.name, ".",
                 caller->name, "()");
    } // end per-probe loop (N2 FIX)

    if (wovenCount == 0)
    {
        // N2 REMOVAL: reaching here with an EMPTY request set (vs all-offsets-skipped) is the
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
        return S_FALSE;
    }

    Logger::Info("*** LineProbe_Rewrite() Finished: wove ", wovenCount, " of ", requests.size(),
                 " probe(s) into ", caller->type.name, ".", caller->name, "() in a single Import/Export.");
    return S_OK;
}

} // namespace trace
