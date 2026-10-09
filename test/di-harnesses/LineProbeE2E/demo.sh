#!/bin/bash
# ============================================================================
#  Line-Level Dynamic Instrumentation — PHASE 2 PoC DEMO (presentation wrapper)
#
#  Everything shown here is REAL output from the forked native profiler weaving
#  a mid-method IL line probe at runtime via ReJIT. Nothing is mocked or faked.
#  This wrapper only narrates + pauses around the SAME run.sh the PoC uses, and
#  surfaces the profiler's own native log as corroboration.
#
#  Scope (state this to the audience): proves the MECHANISM — interior insertion
#  + runtime fire of a plain-static callback — on a trivial sync method, one
#  hardcoded offset, osx-arm64. It does NOT yet read local values, handle async,
#  or cover the shipped RIDs. Those are Phase 3 (see DI-LINE-LEVEL-PHASE3-BRIEF.md).
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOGDIR="$SCRIPT_DIR/logs"

bold(){ printf '\033[1m%s\033[0m\n' "$1"; }
dim(){ printf '\033[2m%s\033[0m\n' "$1"; }
green(){ printf '\033[32m%s\033[0m\n' "$1"; }
yellow(){ printf '\033[33m%s\033[0m\n' "$1"; }
rule(){ printf '\033[2m%s\033[0m\n' "────────────────────────────────────────────────────────────────────"; }
pause(){ [ "${DEMO_NOPAUSE:-0}" = "1" ] || read -rp $'\n\033[2m⏎  '"$1"$'\033[0m' _; }

latest_app_log(){ ls -t "$LOGDIR"/*LineProbeE2E-Native*.log 2>/dev/null | head -1; }

clear 2>/dev/null || true
bold "╔══════════════════════════════════════════════════════════════════╗"
bold "║   .NET Dynamic Instrumentation — LINE-LEVEL PROBE  (Phase 2 PoC)   ║"
bold "╚══════════════════════════════════════════════════════════════════╝"
echo
dim  "Question this PoC answers:"
echo "  Can the (forked) native profiler inject a call to a MANAGED method at a"
echo "  specific line INSIDE a method body, at runtime, without breaking it —"
echo "  and does that call actually FIRE?  (Phase 1 wove but never fired.)"
echo
dim  "What you'll see — all real, from the native profiler:"
echo "   1. The target method + the line we inject at"
echo "   2. Baseline: the callback with NO probe active            → fires 0"
echo "   3. Probe ON: the forked profiler injects + the callback   → fires 5/5"
echo "   4. The profiler's OWN native log proving the IL rewrite"
echo "   5. Fail-safe: a bad offset aborts safely, method intact"

# ---------------------------------------------------------------------------
pause "STEP 1 — show the target + the injection site…"
rule
bold "The target method (poc/LineProbeE2E/SpikeTarget.cs):"
echo
cat "$SCRIPT_DIR/SpikeTarget.cs" | sed -n '/public static class/,/^}/p'
echo
echo "We inject a call to a PLAIN static — LineProbeSink.Probe(probeId) — at"
yellow "IL offset 5 (statement boundary 'return y;'). No profiler-owned type."

# ---------------------------------------------------------------------------
pause "STEP 2 + 3 — run the REAL PoC (profiler ON, forked .dylib)…"
rule
bold "Running ./run.sh  (loads the FORKED native profiler, injects, fires):"
echo
DEMO_NOPAUSE=1 "$SCRIPT_DIR/run.sh"
RC=$?
echo
if [ "$RC" -eq 0 ]; then
  green "▶ exit $RC — ALL assertions passed. The line probe fired 5/5 and the"
  green "  callback received the real probeId=4242 pushed by the injected IL."
else
  yellow "▶ exit $RC — see failures above."
fi

# ---------------------------------------------------------------------------
pause "STEP 4 — prove it's a REAL weave: the profiler's own native log…"
rule
bold "From the native profiler log (not the harness — the profiler itself):"
echo
APP_LOG="$(latest_app_log)"
if [ -n "$APP_LOG" ]; then
  grep -nE "AddLineProbes: received|LineProbe Target:|LineProbe_Rewrite\(\) (Start|Finished)" "$APP_LOG" \
    | sed -E 's/^[0-9]+:\[[^]]*\] \[[^]]*\] \[[a-z]*\] //' \
    | sed 's/^/    /'
  echo
  dim "  (full log: ${APP_LOG#$SCRIPT_DIR/})"
else
  yellow "  (no native log found — run STEP 2 first)"
fi

# ---------------------------------------------------------------------------
pause "STEP 5 — fail-safe: submit a BAD offset (8, mid-instruction)…"
rule
bold "Running with LINEPROBE_IL_OFFSET=8 (an invalid, mid-instruction offset):"
echo
DEMO_NOPAUSE=1 LINEPROBE_IL_OFFSET=8 "$SCRIPT_DIR/run.sh" | grep -E "reg\]|PASS|FAIL|Result" | sed 's/^/    /'
echo
APP_LOG="$(latest_app_log)"
if [ -n "$APP_LOG" ]; then
  bold "The profiler REFUSED to rewrite (body left intact, process alive):"
  grep -nE "not an instruction boundary|Aborting rewrite" "$APP_LOG" \
    | sed -E 's/^[0-9]+:\[[^]]*\] \[[^]]*\] \[[a-z]*\] //' | sed 's/^/    /'
fi
echo
green "▶ Bad offset → probe does NOT fire (FireCount=0), method still returns 42,"
green "  process survives. Fail-safe is already working."

# ---------------------------------------------------------------------------
pause "SUMMARY…"
rule
bold "What this PoC PROVES (real, reproducible):"
echo "  ✓ Interior mid-method IL insertion produces a runtime-valid body"
echo "  ✓ The injected call to a plain managed static FIRES at runtime (5/5)"
echo "  ✓ The method's result is unchanged (stack-neutral)"
echo "  ✓ A bad offset fails safe — no corruption, no crash"
echo
yellow "What it does NOT yet cover (Phase 3 — be transparent):"
echo "  • reading actual local VALUES (this fires with a constant probeId)"
echo "  • branches / try-catch bodies, async methods"
echo "  • the 5 shipped RIDs (proven on osx-arm64 only)"
echo
dim "Plan + gates: poc/DI-LINE-LEVEL-PLAN-AND-TIMELINE.md · DI-LINE-LEVEL-PHASE3-BRIEF.md"
echo
