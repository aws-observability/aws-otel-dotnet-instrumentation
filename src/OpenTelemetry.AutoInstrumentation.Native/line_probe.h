/*
 * Copyright The OpenTelemetry Authors
 * SPDX-License-Identifier: Apache-2.0
 *
 * PHASE-2 LINE-PROBE PoC (AWS Distro DI). ADDITIVE fork delta — does NOT modify the existing
 * CallTarget path. Mirrors the existing IntegrationDefinition / TracerRejitPreprocessor /
 * TracerRejitHandlerModuleMethod / TracerMethodRewriter quartet, but the rewrite emits a single
 * direct `ldc.i4 <probeId>; call <cross-assembly MemberRef>` at a hardcoded interior IL offset
 * (Q1) to a PLAIN managed static (Q2), resolved from NAMES (Q3).
 */

#ifndef OTEL_CLR_PROFILER_LINE_PROBE_H_
#define OTEL_CLR_PROFILER_LINE_PROBE_H_

#include <corhlpr.h>
#include <vector>

#include "integration.h"
#include "method_rewriter.h"
#include "rejit_handler.h"
#include "rejit_preprocessor.h"
#include "string_utils.h"
#include "util.h"

namespace trace
{

// Line-probe emission mode (BOX-GATE SPIKE, DECISION A). Selects the IL sequence the rewriter
// splices at the interior offset. Kept as a trailing field so LEGACY(0) preserves the exact
// Phase-2 single-call / async hoisted-field behavior for the two existing harnesses.
enum LineProbeEmissionMode : INT32
{
    // Legacy Phase-2 path: `ldc.i4 <probeId>; call Probe(int32)` — OR, if hoistedFieldToken != 0,
    // the async `ldarg.0; ldfld <hoisted>; box; call CaptureLocal(int32,object)` path.
    LINE_EMIT_LEGACY = 0,
    // A1 (two-call gated): `ldc.i4 probeId; call ShouldCapture; brfalse SKIP;
    //                       ldc.i4 probeId; ldc.i4 <boxValue>; box System.Int32;
    //                       call Capture(int32,object); SKIP:` — the box lives PAST the branch, so
    // when ShouldCapture returns false the value-type -> heap box is SKIPPED (no allocation).
    LINE_EMIT_GATED_BOX = 1,
    // A2 (unconditional box, the proven single-call shape extended to a boxed value): emits the box +
    // call with NO gate: `ldc.i4 probeId; ldc.i4 <boxValue>; box System.Int32; call Capture`. Used as
    // the contrast case — it allocates on EVERY execution. Proves the gate is what avoids the alloc.
    LINE_EMIT_UNGATED_BOX = 2,
    // G1 GATE (branch + EH relocation): read a SYNC local off a normal stack slot and box it, to
    // capture a real value (e.g. the loop variable) at an interior statement boundary in a method with
    // real control flow. Reuses the existing `boxValue` field as the LOCAL SLOT INDEX (no ABI change).
    //   `ldc.i4 <probeId>; ldloc <boxValue-as-slot>; box System.Int32; call CaptureLocal(int32,object)`
    LINE_EMIT_LOCAL_CAPTURE = 3,
};

// Flat C ABI marshaled from managed NativeLineProbeDefinition. Names-based (Q3): the target is
// located by assembly/type/method(+signature-count), the callback MemberRef is built on the native
// side from callbackAssembly/type/method. Plus the interior ilOffset and an opaque probeId.
typedef struct _LineProbeDefinition
{
    WCHAR*  targetAssembly;
    WCHAR*  targetType;
    WCHAR*  targetMethod;
    WCHAR** signatureTypes;
    USHORT  signatureTypesLength;
    ULONG   ilOffset;
    INT32   probeId;
    // ASYNC SPIKE (DECISION B): when nonzero, the injection reads a HOISTED LOCAL off the async
    // state machine. It is an mdFieldDef in the SAME module as the target (the state-machine field
    // lives in the target's module), hardcoded by the harness from `ilspycmd`/PDB inspection. When
    // zero, the original Phase-2 single-`call Capture(probeId)` path is emitted unchanged.
    ULONG   hoistedFieldToken;
    WCHAR*  callbackAssembly;
    WCHAR*  callbackType;
    WCHAR*  callbackMethod;
    // BOX-GATE SPIKE (DECISION A) — trailing additive fields; 0/null => LEGACY, so the two existing
    // harnesses (LineProbeE2E, AsyncLineProbeE2E) keep their exact behavior with unchanged struct
    // stride. See LineProbeEmissionMode.
    INT32   emissionMode; // LineProbeEmissionMode
    INT32   boxValue;     // the constant int the gated/ungated box path materializes as object
    WCHAR*  gateMethod;   // GATED mode: the `bool ShouldCapture(int32)` method name on callbackType
} LineProbeDefinition;

// Internal (non-marshaled) representation of one line probe, analogous to IntegrationDefinition.
struct LineProbeRequest
{
    MethodReference target_method;
    ULONG           il_offset;
    INT32           probe_id;
    mdFieldDef      hoisted_field_token; // ASYNC SPIKE: 0 => sync single-call; nonzero => read hoisted local
    WSTRING         callback_assembly;
    WSTRING         callback_type;   // fully-qualified, e.g. "Ns.LineProbeSink"
    WSTRING         callback_method; // e.g. "Probe"
    INT32           emission_mode;   // BOX-GATE SPIKE (DECISION A): LineProbeEmissionMode
    INT32           box_value;       // constant int materialized as object on the box path
    WSTRING         gate_method;     // GATED: `bool ShouldCapture(int32)` name on callback_type

    LineProbeRequest() :
        il_offset(0), probe_id(0), hoisted_field_token(mdTokenNil), emission_mode(LINE_EMIT_LEGACY), box_value(0)
    {
    }

    LineProbeRequest(const MethodReference& target_method, ULONG il_offset, INT32 probe_id,
                     mdFieldDef hoisted_field_token, const WSTRING& callback_assembly, const WSTRING& callback_type,
                     const WSTRING& callback_method, INT32 emission_mode = LINE_EMIT_LEGACY, INT32 box_value = 0,
                     const WSTRING& gate_method = WSTRING()) :
        target_method(target_method),
        il_offset(il_offset),
        probe_id(probe_id),
        hoisted_field_token(hoisted_field_token),
        callback_assembly(callback_assembly),
        callback_type(callback_type),
        callback_method(callback_method),
        emission_mode(emission_mode),
        box_value(box_value),
        gate_method(gate_method)
    {
    }

    inline bool operator==(const LineProbeRequest& other) const
    {
        return target_method == other.target_method && il_offset == other.il_offset &&
               probe_id == other.probe_id && hoisted_field_token == other.hoisted_field_token &&
               callback_assembly == other.callback_assembly && callback_type == other.callback_type &&
               callback_method == other.callback_method && emission_mode == other.emission_mode &&
               box_value == other.box_value && gate_method == other.gate_method;
    }
};

// Reuses the existing RejitPreprocessor<T> template (method enumeration, signature-count match,
// ReJIT enqueue) verbatim — we only provide the T-specific hooks.
class LineProbeRejitPreprocessor : public RejitPreprocessor<LineProbeRequest>
{
public:
    using RejitPreprocessor::RejitPreprocessor;

protected:
    const MethodReference& GetTargetMethod(const LineProbeRequest& request) final;
    const bool             GetIsDerived(const LineProbeRequest& request) final;
    const bool             GetIsExactSignatureMatch(const LineProbeRequest& request) final;
    const std::unique_ptr<RejitHandlerModuleMethod> CreateMethod(const mdMethodDef       methodDef,
                                                                 RejitHandlerModule*     module,
                                                                 const FunctionInfo&     functionInfo,
                                                                 const LineProbeRequest& request) final;
};

// Per-method ReJIT record carrying the line-probe request (mirror of TracerRejitHandlerModuleMethod).
class LineProbeRejitHandlerModuleMethod : public RejitHandlerModuleMethod
{
private:
    // N2 FIX (multi-probe-per-method): a method handler now holds a VECTOR of line-probe requests, not
    // a single one. Several probes can target different interior offsets of the SAME method; the fork
    // previously kept only the first (singular unique_ptr) and silently dropped the rest — proven by
    // poc/N2MultiProbeE2E. The whole set is woven in one ILRewriter pass (one Import/Export per ReJIT).
    std::vector<LineProbeRequest> m_requests;

public:
    LineProbeRejitHandlerModuleMethod(mdMethodDef methodDef, RejitHandlerModule* module,
                                      const FunctionInfo& functionInfo, const LineProbeRequest& request);

    // Append another probe to this method's set (called when a 2nd+ probe targets an already-tracked
    // method). Deduplicates by (il_offset, probe_id) so repeat polls of the same config don't stack.
    void AddLineProbeRequest(const LineProbeRequest& request);

    // N2 REMOVAL: drop one probe by probeId. Returns the number remaining. The caller re-ReJITs the
    // method: ReJIT recompiles from the ORIGINAL body (verified — see poc/N2MultiProbeE2E), so the
    // re-weave applies exactly the survivor set. Zero remaining => a re-ReJIT restores the pristine body.
    size_t RemoveLineProbeRequest(int probeId);

    size_t RequestCount() const { return m_requests.size(); }

    LineProbeRejitHandlerModuleMethod* AsLineProbeHandler() override { return this; }

    // All probes for this method. The rewriter iterates these, emitting each between one Import/Export.
    const std::vector<LineProbeRequest>& GetLineProbeRequests() const;

    MethodRewriter* GetMethodRewriter() override;
};

// The rewrite entry (mirror of TracerMethodRewriter). Splices `ldc.i4 <probeId>; call <cb>` at the
// hardcoded interior offset and requests the new body via the same Export()/SetILFunctionBody path.
class LineProbeMethodRewriter : public MethodRewriter, public Singleton<LineProbeMethodRewriter>
{
    friend class Singleton<LineProbeMethodRewriter>;

private:
    LineProbeMethodRewriter()
    {
    }

public:
    HRESULT Rewrite(RejitHandlerModule* moduleHandler, RejitHandlerModuleMethod* methodHandler) override;
};

} // namespace trace

#endif // OTEL_CLR_PROFILER_LINE_PROBE_H_
