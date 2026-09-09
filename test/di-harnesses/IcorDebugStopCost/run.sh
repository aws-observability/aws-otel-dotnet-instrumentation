#!/bin/bash
# ============================================================================
#  ICorDebug "stop the world" cost proxy — Linux container runner
#
#  Prices ONE stop/read/continue cycle of the DEBUGGER line-level approach
#  (Approach B). Mirrors the poc/DeployedAppDemo/run-e2e-linux.sh pattern:
#  host builds nothing special; everything (build + run) happens inside a
#  glibc .NET SDK container because ICorDebug/dbgshim are only tractable on
#  Linux.
#
#  KEY CONTAINER FLAG: --cap-add=SYS_PTRACE. dbgshim's out-of-process attach
#  uses ptrace under the hood; without it, attach fails. yama ptrace_scope in
#  the colima VM is 1, but that only restricts cross-uid attach; we run as the
#  same (root) uid and add the capability, which is sufficient.
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
IMAGE="mcr.microsoft.com/dotnet/sdk:8.0"
IL_OFFSET="${IL_OFFSET:-8}"
MAX_HITS="${MAX_HITS:-1500}"
ITER="${ITER:-2000}"
CALLGAP="${CALLGAP:-5}"

docker run --rm \
  --cap-add=SYS_PTRACE \
  -e DI_ITER="$ITER" \
  -e DI_CALLGAP="$CALLGAP" \
  -e IL_OFFSET="$IL_OFFSET" \
  -e MAX_HITS="$MAX_HITS" \
  -e DI_SENSOR="${DI_SENSOR:-1}" \
  -e DI_ARM="${DI_ARM:-1}" \
  -e DI_WORKERS="${DI_WORKERS:-0}" \
  -e DI_FREEZE_THR_US="${DI_FREEZE_THR_US:-200}" \
  -e DI_BLAST="${DI_BLAST:-0}" \
  -e DI_NONSTOP="${DI_NONSTOP:-0}" \
  -e DI_HEALTH_DEADLINE_MS="${DI_HEALTH_DEADLINE_MS:-5}" \
  -e DI_HEALTH_INTERVAL_MS="${DI_HEALTH_INTERVAL_MS:-10}" \
  -v "$SCRIPT_DIR":/w \
  -w /w \
  -e HOME=/w \
  "$IMAGE" bash /w/incontainer.sh
echo "exit=$?"
