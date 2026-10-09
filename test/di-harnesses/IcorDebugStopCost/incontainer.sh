#!/bin/bash
# In-container half: build Host + Target, locate dbgshim, run the proxy.
set -uo pipefail
W=/w
IL_OFFSET="${IL_OFFSET:-8}"
MAX_HITS="${MAX_HITS:-1500}"

echo "[c] arch=$(uname -m) dotnet=$(dotnet --version)"
echo "[c] ptrace_scope=$(cat /proc/sys/kernel/yama/ptrace_scope 2>/dev/null || echo n/a)"

echo "[c] building Target (Debug)…"
dotnet build "$W/Target/Target.csproj" -c Debug -o "$W/out/target" -v q --nologo 2>&1 | tail -3 || exit 91
echo "[c] building Host (Debug)…"
dotnet build "$W/Host/Host.csproj" -c Debug -o "$W/out/host" -v q --nologo 2>&1 | tail -3 || exit 92

# dbgshim ships with the ClrDebug transitive package restore.
DBGSHIM=$(find "$W" -name "libdbgshim.so" 2>/dev/null | head -1)
if [ -z "$DBGSHIM" ]; then
  # Fallback: some SDK images ship it under the shared runtime dir.
  DBGSHIM=$(find /usr/share/dotnet -name "libdbgshim.so" 2>/dev/null | head -1)
fi
echo "[c] dbgshim=$DBGSHIM"
[ -z "$DBGSHIM" ] && { echo "[c] BLOCKER: libdbgshim.so not found"; exit 93; }

TARGET_DLL="$W/out/target/Target.dll"
READY="$W/out/host-ready.$$"
rm -f "$READY"

# Diagnostics on: helps interpret any dbgshim attach failure.
export DOTNET_EnableDiagnostics=1
export DOTNET_ROOT=/usr/share/dotnet

echo "[c] launching debugger host: ilOffset=$IL_OFFSET maxHits=$MAX_HITS"
echo "============================================================================"
dotnet "$W/out/host/Host.dll" "$TARGET_DLL" "$IL_OFFSET" "$MAX_HITS" "$READY" "$DBGSHIM"
HOSTRC=$?
echo "============================================================================"
echo "[c] host exit=$HOSTRC"
exit $HOSTRC
