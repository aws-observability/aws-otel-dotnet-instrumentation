#!/bin/bash
# ============================================================================
#  .NET Dynamic Instrumentation — the two-part demo (most complete story)
#
#  PART 1 — DeployedAppDemo:   proves ENABLEMENT is real.
#           An unmodified app + env vars → agent polls the (mock) backend →
#           native profiler weaves the app's methods. No app code touches DI.
#
#  PART 2 — ProbeBreakpointDemo: proves CAPTURE works.
#           Same probe/breakpoint mechanism, showing the actual captured
#           argument + return values, and BREAKPOINT hit-limit gating (5 vs 3).
#
#  Together: enablement is real (Part 1) + capture works (Part 2). PR3 merges
#  them — the deployed app will print these same snapshots itself.
#
#  Runs step-by-step (press Enter between phases). DEMO_NOPAUSE=1 to run straight
#  through (e.g. a warm-up so first-build compile time is out of the way).
# ============================================================================
set -uo pipefail
POC_DIR="$(cd "$(dirname "$0")" && pwd)"

pause() { [ "${DEMO_NOPAUSE:-0}" = "1" ] || read -rp $'\n\033[1m⏎  '"${1:-continue}"$'…\033[0m\n' _; }

echo "╔══════════════════════════════════════════════════════════════════════════╗"
echo "║  .NET DYNAMIC INSTRUMENTATION — full story in two parts                    ║"
echo "║    PART 1  DeployedAppDemo    → enablement is real (unmodified app + env)  ║"
echo "║    PART 2  ProbeBreakpointDemo → capture works (real args/return, 5 vs 3)  ║"
echo "╚══════════════════════════════════════════════════════════════════════════╝"

pause "PART 1 — DeployedAppDemo: how a customer enables DI, and proof it weaves"
echo
echo "┌────────────────────────────────────────────────────────────────────────┐"
echo "│  PART 1 — DEPLOYED APP: real enablement path (env vars only)             │"
echo "└────────────────────────────────────────────────────────────────────────┘"
bash "$POC_DIR/DeployedAppDemo/run-demo.sh"

pause "PART 2 — ProbeBreakpointDemo: what DI actually captures"
echo
echo "┌────────────────────────────────────────────────────────────────────────┐"
echo "│  PART 2 — CAPTURE: PROBE vs BREAKPOINT, with real captured values        │"
echo "└────────────────────────────────────────────────────────────────────────┘"
bash "$POC_DIR/ProbeBreakpointDemo/run-demo.sh"

echo
echo "╔══════════════════════════════════════════════════════════════════════════╗"
echo "║  ✅ Enablement proven (Part 1) + capture proven (Part 2).                  ║"
echo "║     PR3 merges them: the deployed app will print these snapshots itself.   ║"
echo "╚══════════════════════════════════════════════════════════════════════════╝"
