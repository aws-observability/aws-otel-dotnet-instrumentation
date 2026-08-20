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
#include <mutex>
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

// WHAT ACTUALLY HAPPENED TO ONE PROBE AT REJIT TIME.
//
// AddLineProbes returns void and returns EARLY: it enqueues a ReJIT request and waits at most 100ms for the
// REQUEST to be accepted — not for the rewrite. The rewrite runs later, on a CLR ReJIT thread, when the target
// method is next invoked. So the managed side cannot learn the weave outcome from the call that asked for it,
// and until this existed it inferred success from its OWN resolution: a probe whose rewrite was skipped here
// still reported READY to the operator and could never fire. That was not hypothetical — the callback
// AssemblyRef gap silently skipped eleven probes in one measured run while every one reported READY.
//
// Hence a process-wide record the managed side polls. Only two outcomes are terminal-good: WOVEN, and PENDING
// (no record yet, which is the normal state for a probe on a method nobody has called). Everything else names
// the reason the rewriter declined, and each corresponds to exactly one `continue`/early-return in
// LineProbeMethodRewriter::Rewrite.
enum LineProbeWeaveOutcome : INT32
{
    // NOT STORED — the absence of a record. Named so the managed enum can round-trip a defaulted value, and so
    // no reason code is ever 0 (a zeroed buffer element must not read as a real failure).
    LINE_WEAVE_PENDING = 0,
    LINE_WEAVE_WOVEN   = 1,

    // Callback resolution: the target module could not be given a usable reference to the DI callback.
    LINE_WEAVE_FAILED_CALLBACK_ASSEMBLY_REF = 2,
    LINE_WEAVE_FAILED_CALLBACK_TYPE_REF     = 3,
    LINE_WEAVE_FAILED_CALLBACK_MEMBER_REF   = 4,
    LINE_WEAVE_FAILED_GATE_MEMBER_REF       = 5,

    // The captured local's type cannot be named through the corlib AssemblyRef, so no `box` token exists.
    LINE_WEAVE_FAILED_BOX_TYPE = 6,

    // The requested local slot is outside the `ldloc` operand range (guards the exported ABI).
    LINE_WEAVE_FAILED_LOCAL_SLOT_RANGE = 7,

    // The requested IL offset cannot host an injection: not an instruction boundary, or the structural entry
    // of a try/handler/filter. Both are properties of the LINE, not of the process.
    LINE_WEAVE_FAILED_OFFSET_NOT_INSTR = 8,
    LINE_WEAVE_FAILED_EH_BOUNDARY      = 9,

    // Whole-method failures. Every probe on the method shares the verdict, because the body is all-or-nothing:
    // one Import and one Export per ReJIT.
    LINE_WEAVE_FAILED_IMPORT = 10,
    LINE_WEAVE_FAILED_EXPORT = 11,
};

// One (probeId, outcome) pair, marshaled to the managed side. Flat and blittable ON PURPOSE: it crosses the
// P/Invoke boundary as a raw array, so it must stay POD with no padding surprises (two INT32s = 8 bytes).
typedef struct _LineProbeWeaveResult
{
    INT32 probeId;
    INT32 outcome; // LineProbeWeaveOutcome
} LineProbeWeaveResult;

// The process-wide weave record. Static rather than a member of CorProfiler because the rewriter is a
// Singleton reached without a profiler pointer, and because the managed query must work whether or not a
// rewrite is in flight.
class LineProbeWeaveLog
{
public:
    // Publishes the outcomes for ONE method's rewrite, replacing any previous verdict for those probe ids.
    // REPLACING IS CORRECT: a method is re-ReJIT-ed after a sibling probe is removed, and the fresh pass is
    // the authoritative account of what its body now contains.
    static void Record(const std::vector<LineProbeWeaveResult>& results);

    // Drops one probe's record, so the log tracks live probes rather than growing for the process lifetime.
    // Called from RemoveLineProbe — the same place the request itself is dropped.
    static void Forget(INT32 probeId);

    // Copies up to `capacity` records into `buffer` and returns the TOTAL number held, which may exceed
    // `capacity`. Returning the total rather than the written count is what lets the caller size a second
    // call correctly instead of silently reading a truncated view as complete.
    static INT32 Snapshot(LineProbeWeaveResult* buffer, INT32 capacity);
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
    // NON-INT LOCAL CAPTURE. The declared type of the local being read, as a full name
    // (e.g. "System.String", "System.DateTime"), resolved managed-side from the method body's local
    // signature. Needed because `box` requires the local's OWN type token: boxing an int-typed token over
    // a DateTime slot is undefined behavior, not a clean failure. NULL keeps the historical
    // System.Int32 behavior so the pre-existing harnesses are unaffected.
    WCHAR*  localTypeName;
    // Whether the local is a VALUE type. Reference-type locals must NOT be boxed at all — `box` on an
    // object reference is invalid IL and the verifier rejects the whole rewritten body — so this is a
    // separate flag rather than something inferred from the name (the native side cannot resolve a name to
    // a type's value-ness without loading it).
    INT32   localIsValueType;
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
    // NON-INT LOCAL CAPTURE: declared type of the captured local, and whether it is a value type.
    // Empty local_type_name => historical System.Int32 behavior. local_is_value_type == FALSE suppresses
    // the `box` entirely (a reference-type local is already an object reference).
    WSTRING         local_type_name;
    bool            local_is_value_type;

    LineProbeRequest() :
        il_offset(0), probe_id(0), hoisted_field_token(mdTokenNil), emission_mode(LINE_EMIT_LEGACY), box_value(0),
        local_is_value_type(true)
    {
    }

    LineProbeRequest(const MethodReference& target_method, ULONG il_offset, INT32 probe_id,
                     mdFieldDef hoisted_field_token, const WSTRING& callback_assembly, const WSTRING& callback_type,
                     const WSTRING& callback_method, INT32 emission_mode = LINE_EMIT_LEGACY, INT32 box_value = 0,
                     const WSTRING& gate_method = WSTRING(), const WSTRING& local_type_name = WSTRING(),
                     bool local_is_value_type = true) :
        target_method(target_method),
        il_offset(il_offset),
        probe_id(probe_id),
        hoisted_field_token(hoisted_field_token),
        callback_assembly(callback_assembly),
        callback_type(callback_type),
        callback_method(callback_method),
        emission_mode(emission_mode),
        box_value(box_value),
        gate_method(gate_method),
        local_type_name(local_type_name),
        local_is_value_type(local_is_value_type)
    {
    }

    inline bool operator==(const LineProbeRequest& other) const
    {
        return target_method == other.target_method && il_offset == other.il_offset &&
               probe_id == other.probe_id && hoisted_field_token == other.hoisted_field_token &&
               callback_assembly == other.callback_assembly && callback_type == other.callback_type &&
               callback_method == other.callback_method && emission_mode == other.emission_mode &&
               box_value == other.box_value && gate_method == other.gate_method &&
               local_type_name == other.local_type_name && local_is_value_type == other.local_is_value_type;
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

    // m_requests is touched from TWO threads and must not be raced.
    //
    // The managed side mutates it from whichever thread drives the configuration poll (AddLineProbes /
    // RemoveLineProbe come in over the P/Invoke boundary), while the CLR calls Rewrite on a ReJIT thread
    // that reads it. Unsynchronized, a push_back that reallocates while the rewriter iterates leaves the
    // rewriter walking freed storage, and RemoveLineProbeRequest replaces the whole vector, which
    // invalidates every pointer into it. Both are use-after-free in the middle of rewriting a customer's
    // method body.
    //
    // `mutable` because RequestCount and the snapshot accessor are logically const but must still lock.
    mutable std::mutex m_requestsLock;

public:
    LineProbeRejitHandlerModuleMethod(mdMethodDef methodDef, RejitHandlerModule* module,
                                      const FunctionInfo& functionInfo, const LineProbeRequest& request);

    // Append another probe to this method's set (called when a 2nd+ probe targets an already-tracked
    // method). Deduplicates by (il_offset, probe_id) so repeat polls of the same config don't stack.
    void AddLineProbeRequest(const LineProbeRequest& request);

    // N2 REMOVAL: drop one probe by probeId. Returns the number of probes REMOVED (0 or 1), not the
    // number remaining, so the caller does not have to compare a separate RequestCount taken outside the
    // lock — that comparison was itself a race. The caller re-ReJITs the method: ReJIT recompiles from the
    // ORIGINAL body (verified — see poc/N2MultiProbeE2E), so the re-weave applies exactly the survivor set.
    // Removing the last probe means a re-ReJIT restores the pristine body.
    size_t RemoveLineProbeRequest(int probeId);

    size_t RequestCount() const;

    LineProbeRejitHandlerModuleMethod* AsLineProbeHandler() override { return this; }

    // A SNAPSHOT of this method's probes, BY VALUE — deliberately not a reference. A reference would be
    // read after the lock was released, which is exactly the use-after-free m_requestsLock exists to
    // prevent; the copy is what makes the rewriter's iteration safe. Per-method probe counts are small.
    std::vector<LineProbeRequest> GetLineProbeRequests() const;

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
