#!/bin/bash
# ============================================================================
#  .NET DI — DEPLOYED-APP demo (the REAL enable path), STEP BY STEP
#
#  A normal console app (SampleApp, zero DI code) is launched through the real
#  ADOT distribution with Dynamic Instrumentation enabled purely via env vars.
#  Operator creates a probe+breakpoint → agent polls the mock backend → native
#  profiler ReJIT-weaves the app's methods. Press Enter between each step.
#  (Set DEMO_NOPAUSE=1 to run straight through, e.g. for a warm-up.)
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
# REPO_ROOT defaults to two levels up (the harness's original in-repo home). DI_REPO_ROOT overrides it so
# the harness can live outside the repo — which it now does — without relocating it.
REPO_ROOT="${DI_REPO_ROOT:-$(cd "$SCRIPT_DIR/../.." && pwd)}"
[ -d "$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation" ] || {
  echo "ERROR: '$REPO_ROOT' is not the aws-otel-dotnet-instrumentation repo."
  echo "  Set DI_REPO_ROOT=/path/to/aws-otel-dotnet-instrumentation"
  exit 1
}
DISTRO_DIR="$REPO_ROOT/OpenTelemetryDistribution"
PORT=2000
LOGDIR="$SCRIPT_DIR/logs"
rm -rf "$LOGDIR"; mkdir -p "$LOGDIR"

# ── Presentation ─────────────────────────────────────────────────────────────
# Colour only when stdout is a terminal, so a redirected run (warm-up, CI, `tee` to a file) stays plain text
# and greppable. Every assertion's PASS/FAIL wording is unchanged, so the log remains machine-readable.
if [ -t 1 ] && [ "${DEMO_NOCOLOR:-0}" != "1" ]; then
  C_RESET=$'\033[0m'; C_BOLD=$'\033[1m'; C_DIM=$'\033[2m'
  C_GREEN=$'\033[32m'; C_RED=$'\033[31m'; C_CYAN=$'\033[36m'; C_YELLOW=$'\033[33m'
else
  C_RESET=''; C_BOLD=''; C_DIM=''; C_GREEN=''; C_RED=''; C_CYAN=''; C_YELLOW=''
fi

# READS FROM /dev/tty, NOT STDIN, AND THAT IS THE FIX. The old `read -rp ... _` took stdin, which is not
# reliably the keyboard here: the demo launches the sample app and a listener in the background, and anything
# that redirects or consumes stdin (a pipe into `tee`, a run from an editor's task runner) makes `read` return
# instantly. The pause then silently did nothing and the whole demo scrolled past in one burst — which is the
# symptom this fixes. /dev/tty is the controlling terminal regardless of how stdin is wired.
#
# If there is no controlling terminal at all (CI), fall through without waiting rather than hanging forever.
pause() {
  [ "${DEMO_NOPAUSE:-0}" = "1" ] && return 0
  if [ -e /dev/tty ] && { : >/dev/tty; } 2>/dev/null; then
    printf '\n%s' "${C_DIM}   ────────────────────────────────────────────────────────────────${C_RESET}" >/dev/tty
    printf '\n%s\n' "${C_BOLD}${C_CYAN}   ⏎  Press Enter — next: ${1:-continue}${C_RESET}" >/dev/tty
    read -r _ </dev/tty || true
  fi
}

# A pause used mid-step, for a single reveal worth stopping on rather than a whole phase boundary.
beat() {
  [ "${DEMO_NOPAUSE:-0}" = "1" ] && return 0
  if [ -e /dev/tty ] && { : >/dev/tty; } 2>/dev/null; then
    printf '\n%s' "${C_DIM}   ⏎  ${1:-}${C_RESET}" >/dev/tty
    read -r _ </dev/tty || true
    printf '\n' >/dev/tty
  fi
}

# Groups the verification output so a reader sees CAPABILITIES, not 19 undifferentiated lines. Each group
# also records its own tally, which is what the closing scoreboard is built from.
GROUP_NAMES=(); GROUP_PASS=(); GROUP_FAIL=(); CUR_GROUP=-1
section() {
  CUR_GROUP=$((CUR_GROUP+1))
  GROUP_NAMES[$CUR_GROUP]="$1"; GROUP_PASS[$CUR_GROUP]=0; GROUP_FAIL[$CUR_GROUP]=0
  echo
  echo "  ${C_BOLD}${C_CYAN}▏$1${C_RESET}"
}

# ── Beta mode (opt-in) ───────────────────────────────────────────────────────
# Default: fully local, offline mock (no creds, no network) — unchanged.
# DI_BETA_ENDPOINT set: the mock SigV4-signs + forwards the config-API leg (list/create/report) to the REAL
# beta backend, playing the CloudWatch Agent's signing role. The DI client stays on its exact GA path
# (unsigned → this local proxy). Snapshots stay local so the run can still decode + assert the captured body.
# Requires AWS creds in the environment (standard chain). Example:
#   DI_BETA_ENDPOINT=$DI_BETA_ENDPOINT_URL DEMO_NOPAUSE=1 ./run-demo.sh
if [ -n "${DI_BETA_ENDPOINT:-}" ]; then
  export DI_BETA_ENDPOINT
  export DI_BETA_REGION="${DI_BETA_REGION:-us-west-2}"
  export DI_BETA_SERVICE="${DI_BETA_SERVICE:-application-signals}"
  echo "▶ BETA MODE: config API leg → $DI_BETA_ENDPOINT (SigV4 as $DI_BETA_SERVICE/$DI_BETA_REGION); snapshots stay local"
else
  echo "▶ LOCAL MODE: fully offline mock (set DI_BETA_ENDPOINT to forward the config leg to real beta)"
fi

OS=$(uname -s | tr '[:upper:]' '[:lower:]'); ARCH=$(uname -m)
case "$ARCH" in x86_64) ARCH="x64";; aarch64|arm64) ARCH="arm64";; esac
case "$OS" in
  linux)  PROFILER_PATH="$DISTRO_DIR/linux-$ARCH/OpenTelemetry.AutoInstrumentation.Native.so";;
  darwin) PROFILER_PATH="$DISTRO_DIR/osx-$ARCH/OpenTelemetry.AutoInstrumentation.Native.dylib";;
esac
[ -f "$PROFILER_PATH" ] || { echo "ERROR: native profiler not found: $PROFILER_PATH"; exit 1; }

# LINE-LEVEL REQUIRES THE FORKED PROFILER. The stock upstream binary has no AddLineProbes export, and the
# managed side reports that cleanly (ProfilerMissingLineProbeSupport) rather than crashing — which would make
# this run "pass" the method-level checks while silently proving nothing about line-level. So the export is
# checked by CONTENT here, not by the file merely existing.
# DI_PROFILER_OVERRIDE lets a locally-built fork be used without rebuilding the whole distribution.
if [ -n "${DI_PROFILER_OVERRIDE:-}" ]; then
  [ -f "$DI_PROFILER_OVERRIDE" ] || { echo "ERROR: DI_PROFILER_OVERRIDE not found: $DI_PROFILER_OVERRIDE"; exit 1; }
  PROFILER_PATH="$DI_PROFILER_OVERRIDE"
  echo "▶ profiler override: $PROFILER_PATH"
fi
# NOT `nm ... | grep -q`: under `set -o pipefail` grep -q exits on the first match, nm then dies of SIGPIPE,
# and the pipeline returns 141 — so the check reported "no export" for a binary that HAS it. Capture first,
# match after.
PROFILER_SYMS="$(nm -gU "$PROFILER_PATH" 2>/dev/null || true)"
if printf '%s' "$PROFILER_SYMS" | grep -q "_AddLineProbes"; then
  echo "▶ profiler exports AddLineProbes → line-level is testable"
  LINE_CAPABLE=1
else
  echo "▶ WARNING: profiler does NOT export AddLineProbes (stock upstream binary)."
  echo "  Line-level checks would fail for a DEPLOYMENT reason, not a code defect."
  echo "  Set DI_PROFILER_OVERRIDE=/path/to/forked/OpenTelemetry.AutoInstrumentation.Native.dylib"
  LINE_CAPABLE=0
fi

MOCK_DLL="$SCRIPT_DIR/MockBackend/bin/Release/net8.0/MockBackend.dll"
APP_DLL="$SCRIPT_DIR/SampleApp/bin/Release/net8.0/SampleApp.dll"

cleanup() {
  kill "${MOCK_PID:-}" "${APP_PID:-}" "${TAIL_PID:-}" 2>/dev/null
  pkill -f "$MOCK_DLL" 2>/dev/null
  pkill -f "$APP_DLL" 2>/dev/null
}
trap cleanup EXIT INT TERM

# Guard: a leftover process on the port from a prior run breaks the mock silently. Clear it.
if lsof -iTCP:$PORT -sTCP:LISTEN >/dev/null 2>&1; then
  echo "port $PORT busy — clearing a leftover process from a prior run…"
  lsof -tiTCP:$PORT -sTCP:LISTEN 2>/dev/null | xargs kill 2>/dev/null
  sleep 1
fi

echo "Building DI assembly + SampleApp + MockBackend…"
# Build the CURRENT DI source and refresh the copy inside the distribution the agent loads at runtime.
# Without this the app would load whatever stale DI dll shipped in OpenTelemetryDistribution/net, silently
# testing old code — the E2E must exercise the source in this tree.
DI_PROJ="$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/AWS.Distro.OpenTelemetry.DynamicInstrumentation.csproj"
( cd "$REPO_ROOT" && dotnet build "$DI_PROJ" -c Release -f net8.0 -v q --nologo ) >/dev/null || { echo "DI build failed"; exit 1; }
DI_DLL="$REPO_ROOT/src/AWS.Distro.OpenTelemetry.DynamicInstrumentation/bin/Release/net8.0/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll"
cp "$DI_DLL" "$DISTRO_DIR/net/AWS.Distro.OpenTelemetry.DynamicInstrumentation.dll" || { echo "DI dll refresh failed"; exit 1; }
echo "  refreshed distribution DI dll from current source"

( cd "$SCRIPT_DIR/MockBackend" && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "mock build failed"; exit 1; }
( cd "$SCRIPT_DIR/SampleApp"  && dotnet build -c Release -v q --nologo ) >/dev/null || { echo "app build failed"; exit 1; }

# Configuration creation lives in a function because it must run AFTER the app is up and its target
# methods are JIT-compiled. RequestReJIT only rewrites a method that already has JIT'd code, and DI's
# first poll is kicked off from the startup hook — before Main runs. Creating the configs first therefore
# raced the first invocation and lost: the profiler logged "Request ReJIT done for 1 methods" while
# CallTargetRewriter stayed at 0ms/0 and NOTHING was woven. Because the manager latches an applied key,
# that skip was never retried. Creating them against an already-warm app removes the race — and it is
# also the truthful operator story: the service is already serving traffic when a probe is added.
# Beta-mode housekeeping: delete every line-level config this service/environment already has before
# creating fresh ones.
#
# WHY THIS IS NEEDED. Beta PERSISTS configs until ExpiresAt (~24 h), and a line-level config stores an
# absolute source LINE NUMBER. Editing SampleApp.cs moves the target line, so yesterday's config now points
# at whatever happens to sit at the old number — usually a comment. The agent then correctly refuses it and
# reports ERROR/LINE_NOT_EXECUTABLE forever, and `create` returns 409 because a config already exists for
# that (service, environment, signalType, location). Deleting first makes each run start from a known state
# instead of accumulating stale probes.
#
# Deletes ONLY line-level configs (LineNumber non-null). The two method-level ones carry no line number, so
# they are stable across edits and are left in place — their 409-or-200 is itself an asserted signal.
# The FIELD SET comes from DeleteInstrumentationConfigurationRequest in
# APMPulseDynamicInstrumentationTypes (instrumentationType, service, environment, signalType,
# locationIdentifier, accountId), but the CASING must be PascalCase, not the Kotlin camelCase.
#
# Learned the hard way: sending the Kotlin names verbatim produced
#   HTTP 400 "5 validation errors detected: Value at 'environment' ... must not be null" (×5)
# for fields that were plainly present in the body — the tell that the names bound to nothing. The wire
# convention for these APIs is PascalCase (same as list/create/report, which already work here); the
# camelCase in the Kotlin types is the INTERNAL shape the CloudWatch Agent proxy translates to.
# LocationIdentifier's inner key is likewise PascalCase.
# DI_CLEAN_ALL=1 also removes the METHOD-LEVEL configs, giving a genuinely empty starting state.
#
# WHY IT IS OPT-IN. Method-level configs carry no line number, so they are stable across source edits and
# cost nothing to leave behind — and their `create` returning 409 instead of 200 is itself an asserted
# signal that beta authenticated, parsed the request and matched an existing LocationHash. Deleting them
# every run would throw that signal away. But leaving them also means the agent has already applied the
# Process probe before Main runs, so the first captures are the WARM-UP calls rather than the tick loop —
# harmless for the assertions (they require `order-`, not `warm-`) but misleading to read. Use the flag
# when you want a run whose every create is a fresh 200 and whose captures start at tick 0.
#
# BOTH INSTRUMENTATION TYPES ARE LISTED, and that matters: `Process` is a PROBE while everything else is a
# BREAKPOINT, and list/delete are both scoped by type. Listing only BREAKPOINT — as this function used to —
# cannot see the PROBE config at all, so a "delete everything" that queried one type would silently leave it
# behind and report success.
cleanup_stale_line_configs() {
  [ -n "${DI_BETA_ENDPOINT:-}" ] || return 0

  local clean_all="${DI_CLEAN_ALL:-0}"
  local scope="line-level"
  [ "$clean_all" = "1" ] && scope="ALL (incl. method-level)"

  local found=0
  local itype listed hashes
  for itype in PROBE BREAKPOINT; do
    listed=$(curl -s -X POST "http://127.0.0.1:$PORT/list-instrumentation-configurations" \
      -H "Content-Type: application/json" \
      -d '{"Service":"sample-app","Environment":"staging","InstrumentationType":"'"$itype"'"}' 2>/dev/null)

    hashes=$(printf '%s' "$listed" | CLEAN_ALL="$clean_all" python3 -c '
import json,os,sys
try:
    d=json.load(sys.stdin)
except Exception:
    sys.exit(0)
clean_all = os.environ.get("CLEAN_ALL") == "1"
for c in (d.get("LatestConfigurations") or []):
    loc=(c.get("Location") or {}).get("CodeLocation") or {}
    line=loc.get("LineNumber")
    # Line-level only by default: a method-level config has LineNumber null.
    if not clean_all and not line:
        continue
    h=c.get("LocationHash")
    if h: print(h, loc.get("MethodName") or "?", line if line else "method-level")
' 2>/dev/null)

    [ -n "$hashes" ] || continue

    # A here-string, not `echo ... | while`: a piped loop runs in a SUBSHELL, so `found` would be
    # incremented in a child and lost — the caller would then report "nothing to clean" after deleting.
    while read -r H M L; do
      [ -n "$H" ] || continue
      found=$((found + 1))
      echo "  deleting $itype config $H ($M line $L)"
      curl -s -X POST "http://127.0.0.1:$PORT/delete-instrumentation-configuration" \
        -H "Content-Type: application/json" -d '{
        "InstrumentationType":"'"$itype"'","Service":"sample-app","Environment":"staging",
        "SignalType":"SNAPSHOT","AccountId":"000000000000",
        "LocationIdentifier":{"LocationHash":"'"$H"'"}}' >/dev/null
    done <<< "$hashes"
  done

  if [ "$found" = "0" ]; then
    echo "  no pre-existing configs to clean up (scope: $scope)"
  else
    echo "  cleaned $found config(s) (scope: $scope)"
  fi
}

create_configs() {
# DEMO_LINE_ONLY=1 creates NO method-level configuration, which isolates the line-probe path.
#
# WHY THAT MATTERS. A customer assembly has no compile-time reference to the DI assembly, so the native side
# has to EMIT the callback AssemblyRef itself. In every other run that emit never happens: the method-level
# CallTarget weave on the SAME module runs first and emits a TypeRef to DiIntegrationN, which drags the
# AssemblyRef in as a side effect. The line probe then "finds" a reference it did nothing to create. So the
# define-if-absent branch was unreachable in this harness, and a module carrying only line probes was
# untested. This mode removes the masking.
if [ -z "${DEMO_LINE_ONLY:-}" ]; then
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"PROBE","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"OrderService","MethodName":"Process","FilePath":"OrderService.cs"}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureArguments":["orderId"],"CaptureReturn":true,"CaptureLimits":{"MaxHits":100}}}}' >/dev/null
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"CheckoutService","MethodName":"Complete","FilePath":"CheckoutService.cs"}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureArguments":["cartId"],"CaptureReturn":true,"CaptureLimits":{"MaxHits":3}}}}' >/dev/null

# SCHEMA SHOWCASE. One probe whose seven arguments cover every shape a captured value can take, so the body
# shows the whole schema rather than one branch: value(+truncated), elements(+size), fields, is_null, and
# not_captured_reason for DEPTH and ALREADY_CAPTURED. CaptureStackTrace is the only config here that asks for
# `stack`, which nothing else in the demo exercised.
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"PROBE","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"ShowcaseService","MethodName":"Describe","FilePath":"Program.cs"}},
  "CaptureConfiguration":{"CodeCapture":{
    "CaptureArguments":["longText","manyItems","tags","address","chain","selfRef","missing"],
    "CaptureReturn":true,"CaptureStackTrace":true,"CaptureLimits":{"MaxHits":100}}}}' >/dev/null

# THROWABLE. Arity 1 against Describe's 7, so the two cannot be confused at capture time.
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"PROBE","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"ShowcaseService","MethodName":"Fail","FilePath":"Program.cs"}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureArguments":["reason"],"CaptureReturn":true,"CaptureLimits":{"MaxHits":100}}}}' >/dev/null
else
  echo "  DEMO_LINE_ONLY=1 — skipping the method-level PROBE/BREAKPOINT so nothing else can create the"
  echo "  callback AssemblyRef; the line-probe path must define it itself."
fi

# LINE-LEVEL config. The line number is DERIVED from a marker comment in the source rather than
# hardcoded, so an edit to SampleApp.cs cannot silently make this probe point at the wrong statement —
# a hardcoded line would still "resolve", just to the wrong place, which is the failure mode that looks
# like success. Fails loudly here if the marker is missing.
LINE_NO=$(grep -n '@line-probe-target: total' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
[ -n "$LINE_NO" ] || { echo "ERROR: '@line-probe-target: total' marker not found in SampleApp/Program.cs"; exit 1; }
echo "  line-level probe target: InventoryService.Reserve, SampleApp/Program.cs line $LINE_NO (local 'total')"
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"InventoryService","MethodName":"Reserve","FilePath":"Program.cs","LineNumber":'"$LINE_NO"'}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureLocals":["total"],"CaptureLimits":{"MaxHits":5}}}}' >/dev/null

# NON-INT LOCAL CAPTURE — one config per local, on three SEPARATE methods. Not because only one local can be
# captured (DescribeAll below captures three at once), but so each type family fails INDEPENDENTLY: a shared
# line would collapse them into one config and one failure would hide the others.
# Each type family hits a different branch of the box logic:
#   note  = System.String   -> reference type, NO box emitted at all
#   stamp = System.DateTime -> value type, boxed against its OWN token (not System.Int32)
#   ratio = System.Double   -> a second, differently-sized value type
for PAIR in "DescribeNote note" "DescribeStamp stamp" "DescribeRatio ratio"; do
  set -- $PAIR
  METHOD="$1"; LOCAL="$2"
  NL=$(grep -n "@line-probe-target: $LOCAL\$" "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
  [ -n "$NL" ] || { echo "ERROR: '@line-probe-target: $LOCAL' marker not found"; exit 1; }
  echo "  non-int probe: InventoryService.$METHOD line $NL (local '$LOCAL')"
  curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
    -H "Content-Type: application/json" -d '{
    "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
    "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
    "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"InventoryService","MethodName":"'"$METHOD"'","FilePath":"Program.cs","LineNumber":'"$NL"'}},
    "CaptureConfiguration":{"CodeCapture":{"CaptureLocals":["'"$LOCAL"'"],"CaptureLimits":{"MaxHits":5}}}}' >/dev/null
done

# MULTI-LOCAL: ONE config capturing THREE locals of three type families at ONE line. This is the shape an
# operator actually creates ("show me the state at this line"), and it is what the earlier
# one-local-per-method workaround existed to avoid. Applied as N probes at the same IL offset, batched into a
# single AddLineProbes call.
MULTI_LINE=$(grep -n '@line-probe-target-multi: count,label,weight' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
[ -n "$MULTI_LINE" ] || { echo "ERROR: '@line-probe-target-multi' marker not found"; exit 1; }
echo "  multi-local probe: InventoryService.DescribeAll line $MULTI_LINE (locals count/label/weight)"
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"InventoryService","MethodName":"DescribeAll","FilePath":"Program.cs","LineNumber":'"$MULTI_LINE"'}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureLocals":["count","label","weight"],"CaptureLimits":{"MaxHits":30}}}}' >/dev/null

# ASYNC: the operator names `ReserveAsync`, but that method's body contains NO interior source line — it is
# only a state-machine launcher. The line lives in `<ReserveAsync>d__N.MoveNext()`, and `total` is not a local
# there but a FIELD `<total>5__N`, because its lifetime crosses the await. So this config exercises a
# completely different resolution path (follow AsyncStateMachineAttribute, then read a field token) and a
# different native emission (`ldarg.0; ldfld` instead of `ldloc`) than every probe above.
#
# The CONFIG IS IDENTICAL IN SHAPE to a sync one — same class, same method name, same line number. Nothing in
# the wire contract says "async"; the agent has to work it out from the target assembly's metadata. That is
# the property worth proving, because it means an operator never has to know.
ASYNC_MULTI_LINE=$(grep -n '@line-probe-target-async-multi: note,ratio,count' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
[ -n "$ASYNC_MULTI_LINE" ] || { echo "ERROR: '@line-probe-target-async-multi' marker not found"; exit 1; }
echo "  async non-int probe: InventoryService.DescribeAsync line $ASYNC_MULTI_LINE (hoisted note/ratio/count)"
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"InventoryService","MethodName":"DescribeAsync","FilePath":"Program.cs","LineNumber":'"$ASYNC_MULTI_LINE"'}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureLocals":["note","ratio","count"],"CaptureLimits":{"MaxHits":30}}}}' >/dev/null

ASYNC_LINE=$(grep -n '@line-probe-target-async: total' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
[ -n "$ASYNC_LINE" ] || { echo "ERROR: '@line-probe-target-async: total' marker not found"; exit 1; }
echo "  async probe: InventoryService.ReserveAsync line $ASYNC_LINE (hoisted local 'total')"
curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
  -H "Content-Type: application/json" -d '{
  "AccountId":"000000000000","Service":"sample-app","Environment":"staging",
  "InstrumentationType":"BREAKPOINT","SignalType":"SNAPSHOT",
  "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders","ClassName":"InventoryService","MethodName":"ReserveAsync","FilePath":"Program.cs","LineNumber":'"$ASYNC_LINE"'}},
  "CaptureConfiguration":{"CodeCapture":{"CaptureLocals":["total"],"CaptureLimits":{"MaxHits":5}}}}' >/dev/null

sleep 1   # let the mock print the create request/response blocks
}

# ── STEP 1 ──────────────────────────────────────────────────────────────────
pause "STEP 1 — start the backend (empty)"
echo "############################################################################"
echo "#  STEP 1 — start the (empty) backend"
echo "############################################################################"
MOCK_OUT="$LOGDIR/mock-backend.out"
dotnet "$MOCK_DLL" $PORT > "$MOCK_OUT" 2>&1 &
MOCK_PID=$!
sleep 2   # let the listener come up
# Echo the mock's output live too, so the run is still readable while we also assert on the file.
tail -f "$MOCK_OUT" & TAIL_PID=$!

# Purge stale line-level configs BEFORE the app starts. Doing it later (alongside create_configs) was too
# late: the agent's first poll fires from the startup hook, so it had already fetched the stale config and
# reported ERROR/LINE_NOT_EXECUTABLE for it — which the "no stale config" check then correctly caught, even
# though the config was deleted moments afterwards. Cleaning up while nothing is polling makes the run's
# starting state deterministic.
cleanup_stale_line_configs

# Mark where cleanup finished. The line-number assertion in STEP 4 must only consider traffic AFTER this
# point: the pre-cleanup list legitimately contains the stale configs (that is how cleanup finds them), so
# scanning the whole log reports the very lines it just deleted and fails a run that actually succeeded.
#
# A LINE COUNT, NOT A MARKER WRITTEN INTO THE LOG. The previous version appended "[CLEANUP-COMPLETE]" to
# $MOCK_OUT with `>>`, which does not work: the mock backend is STILL RUNNING and holds its own non-append
# fd on that same file from `dotnet ... > "$MOCK_OUT"`. It writes at its own tracked offset, so its next
# flush overwrote the appended marker. The marker was then absent, `sed` matched nothing, and the checks
# silently fell back to scanning the ENTIRE log — including the pre-cleanup inventory — which failed a run
# whose product code was correct. Verified: 0 occurrences of the marker survived in the log.
CLEANUP_BOUNDARY=$(wc -l < "$MOCK_OUT" 2>/dev/null | tr -d ' ')
echo "  [cleanup boundary recorded at mock-log line ${CLEANUP_BOUNDARY:-unknown}]"

# Emits ONLY the mock traffic recorded after cleanup finished. Returns non-zero when the boundary is unknown,
# so a caller must decide explicitly rather than inheriting a silently-widened scope.
post_cleanup_log() {
  [ -n "${CLEANUP_BOUNDARY:-}" ] || return 1
  tail -n +$((CLEANUP_BOUNDARY + 1)) "$MOCK_OUT" 2>/dev/null
}

# ── STEP 2 ──────────────────────────────────────────────────────────────────
pause "STEP 2 — enable DI on the unmodified app (env vars only)"
echo "############################################################################"
echo "#  STEP 2 — enable DI on an UNMODIFIED app (100% env vars, as a customer would)"
echo "############################################################################"
echo "  OTEL_DOTNET_AUTO_PLUGINS = AWS...AutoInstrumentation.Plugin   (registers the DI-hosting plugin)"
echo "  OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED = true"
echo "  OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL = http://127.0.0.1:$PORT   (→ real backend after PR3)"
echo "  OTEL_AWS_DYNAMIC_INSTRUMENTATION_*_POLL_INTERVAL = 5   (fast poll for the demo)"

export CORECLR_ENABLE_PROFILING=1
export CORECLR_PROFILER="{918728DD-259F-4A6A-AC2B-B85E1B658318}"
export CORECLR_PROFILER_PATH="$PROFILER_PATH"
export OTEL_DOTNET_AUTO_HOME="$DISTRO_DIR"
export DOTNET_ADDITIONAL_DEPS="$DISTRO_DIR/AdditionalDeps"
export DOTNET_SHARED_STORE="$DISTRO_DIR/store"
export DOTNET_STARTUP_HOOKS="$DISTRO_DIR/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll"
export OTEL_DOTNET_AUTO_LOG_DIRECTORY="$LOGDIR"
export OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_ENABLED=true
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL="http://127.0.0.1:$PORT"
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_PROBE_POLL_INTERVAL=5
export OTEL_AWS_DYNAMIC_INSTRUMENTATION_BREAKPOINT_POLL_INTERVAL=5
# PR3 output leg: snapshots export as OTLP/HTTP logs to the mock (which also decodes + asserts them).
# http/protobuf so the mock can ParseFrom the payload. Same endpoint the real CW agent exposes.
# DI_LOGS_ENDPOINT_OVERRIDE points the snapshot leg somewhere else — set it to a dead port to check that a
# failing export is actually REPORTED rather than silently dropping snapshots. The body assertions below are
# expected to fail in that mode; the point is the agent's own warning.
export OTEL_AWS_OTLP_LOGS_ENDPOINT="${DI_LOGS_ENDPOINT_OVERRIDE:-http://127.0.0.1:$PORT/v1/logs}"
export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
export OTEL_SERVICE_NAME=sample-app
export OTEL_RESOURCE_ATTRIBUTES=deployment.environment.name=staging

# KEEP THE APP SERVING for the operator-driven segment (STEP 5). The app's default loop is 60 ticks (~60s),
# which the automated assertions finish well inside — but a human reading a request, deciding, and pressing
# Enter does not. Without this the app would exit mid-segment and "no more snapshots after delete" would be
# true because the process was gone, not because the probe was removed: a false proof of the very thing the
# segment exists to show. Only raised for an interactive run, so the warm-up and CI keep the short loop.
if [ "${DEMO_NOPAUSE:-0}" != "1" ] && [ -e /dev/tty ] && { : >/dev/tty; } 2>/dev/null; then
  export DI_TICKS="${DI_TICKS:-1200}"
  DEMO_INTERACTIVE=1
else
  DEMO_INTERACTIVE=0
fi

# Disable the AWS EC2/EKS/ECS resource detectors. OFF-MACHINE they each try to reach the EC2 metadata
# endpoint (169.254.169.254) and the EKS service-account token path, and every attempt has to time out.
# Measured: ~76 s of blocking BEFORE Main runs, which is longer than the harness's warm-up wait and made
# the app look dead when it was merely stalled in resource detection. They contribute nothing to a DI
# capture test. Not needed on a real EC2/EKS host, where these resolve immediately.
# Var name verified in Plugin.cs (ResourceDetectorEnableConfig) — bare, no OTEL_ prefix — where the
# comment records it exists precisely "to be able to disable during local testing".
export RESOURCE_DETECTORS_ENABLED=false

# ── STEP 3 ──────────────────────────────────────────────────────────────────
pause "STEP 3 — start the app; the agent polls and the profiler weaves"
echo "############################################################################"
echo "#  STEP 3 — agent polls the backend + native profiler weaves the app's methods"
echo "############################################################################"
APP_OUT="$LOGDIR/app.out"
dotnet "$APP_DLL" > "$APP_OUT" 2>&1 &
APP_PID=$!

# Wait for the app to report its target methods JIT-compiled, THEN create the configs. See create_configs.
echo "  waiting for the app to warm its target methods (TARGETS-WARM, up to 60s)…"
WARM=0
for i in $(seq 1 60); do
  if grep -q "TARGETS-WARM" "$APP_OUT" 2>/dev/null; then WARM=1; break; fi
  # Don't spin on a dead app.
  kill -0 "$APP_PID" 2>/dev/null || break
  sleep 1
done
if [ "$WARM" != "1" ]; then
  echo "  ERROR: app never reported TARGETS-WARM. App output:"; sed -e 's/^/    /' "$APP_OUT" | head -30
  exit 1
fi
echo "  targets warm → creating the probe + breakpoint + line probe now (operator step)"
create_configs

# Wait for the weave to actually appear in the native log (poll up to ~45s) instead of a blind
# sleep — plugin boot + poll + ReJIT timing varies, and a fixed sleep can declare a false failure.
echo "  waiting for the agent to poll and the profiler to ReJIT (up to 45s)…"
# Key off the method-level ReJIT finish line (hash-independent) so this works whether the config's
# LocationHash is the local mock's synthetic id or a real beta-assigned hex hash.
WOVE=0
for i in $(seq 1 45); do
  NATIVE_LOG=$(ls -t "$LOGDIR"/*dotnet-Native.log 2>/dev/null | head -1)
  WEAVE_PATTERN="CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.(OrderService.Process|CheckoutService.Complete)"
  # No method-level config in line-only mode means no CallTarget weave will EVER appear, so waiting on it
  # would burn the full 45s and then report a spurious failure. Key off the line-probe weld instead.
  [ -n "${DEMO_LINE_ONLY:-}" ] && WEAVE_PATTERN="wove probeId"
  if [ -n "$NATIVE_LOG" ] && grep -qE "$WEAVE_PATTERN" "$NATIVE_LOG" 2>/dev/null; then
    WOVE=1; break
  fi
  sleep 1
done
# After the weave lands, let the app keep calling the woven methods for a while so probes fire,
# capture into DIDataStore, the collector drains (10ms), and the OTLP batch exporter flushes to the
# mock's /v1/logs. Then wait for the first snapshot to actually arrive (up to ~15s) before stopping.
echo "  weave detected; letting the woven methods run so snapshots capture + export…"
SNAP=0
for i in $(seq 1 15); do
  if grep -q "\[SNAPSHOT-RECEIVED\]" "$MOCK_OUT" 2>/dev/null; then
    SNAP=1; break
  fi
  sleep 1
done
# The LINE snapshot can lag the method one: Reserve is called once per 1s tick, and tick 0 captures
# total=0 which is deliberately not accepted as proof (indistinguishable from an unassigned local). So wait
# for a level=line snapshot specifically rather than assuming the first snapshot covers both paths.
if [ "$LINE_CAPABLE" = "1" ]; then
  echo "  waiting for a LINE-level snapshot (up to 20s)…"
  for i in $(seq 1 20); do
    if grep -qE "\[SNAPSHOT-RECEIVED\].*level=line" "$MOCK_OUT" 2>/dev/null; then break; fi
    sleep 1
  done
  # Then wait for all THREE non-int locals. They ride separate probes on separate methods, so they land
  # independently; checking only for "a line snapshot" would race the slowest of them.
  echo "  waiting for the non-int locals (string/DateTime/double, up to 25s)…"
  for i in $(seq 1 25); do
    if grep -q '"note"' "$MOCK_OUT" 2>/dev/null \
       && grep -q '"stamp"' "$MOCK_OUT" 2>/dev/null \
       && grep -q '"ratio"' "$MOCK_OUT" 2>/dev/null; then break; fi
    sleep 1
  done
  # And the multi-local trio. These ride three probes on ONE config, so they can land on different ticks.
  echo "  waiting for the multi-local trio (count/label/weight, up to 25s)…"
  for i in $(seq 1 25); do
    if grep -q '"count"' "$MOCK_OUT" 2>/dev/null \
       && grep -q '"label"' "$MOCK_OUT" 2>/dev/null \
       && grep -q '"weight"' "$MOCK_OUT" 2>/dev/null; then break; fi
    sleep 1
  done
  # And the ASYNC capture. It rides the resumed continuation, so it can lag the synchronous probes by a tick.
  echo "  waiting for the async hoisted local (ReserveAsync, up to 25s)…"
  for i in $(seq 1 25); do
    if grep -q 'method=ReserveAsync' "$MOCK_OUT" 2>/dev/null; then break; fi
    sleep 1
  done
  # And the async NON-INT hoisted locals, which ride three probes on one MoveNext offset.
  echo "  waiting for the async non-int hoisted locals (note/ratio, up to 25s)…"
  for i in $(seq 1 25); do
    if grep -q '"note":{"type":"System.String","value":"async-item-' "$MOCK_OUT" 2>/dev/null \
       && grep -q '"ratio":{"type":"System.Double"' "$MOCK_OUT" 2>/dev/null; then break; fi
    sleep 1
  done
fi
# A beat for the body line to flush alongside the marker, then stop the app.
sleep 1
# KEEP THE APP ALIVE FOR AN INTERACTIVE RUN. Stopping it here freezes the log before the assertions, which is
# right for an automated run — but it is also what made the operator-driven segment impossible: by STEP 5 the
# process was already gone, so "add and remove a probe on a live service" had no live service. The assertions
# below are existence and value checks over traffic that has ALREADY been recorded, so more traffic arriving
# afterwards cannot invalidate them. The EXIT trap still cleans up either way.
if [ "$DEMO_INTERACTIVE" = "1" ]; then
  echo "  ${C_DIM}(leaving the app running — STEP 5 and di-console.sh need a live service)${C_RESET}"
else
  kill "$APP_PID" 2>/dev/null; pkill -f "$APP_DLL" 2>/dev/null; wait "$APP_PID" 2>/dev/null
fi
kill "${TAIL_PID:-}" 2>/dev/null

# ── STEP 4 — VERIFY the full end-to-end loop (exit-code contract) ────────────
pause "STEP 4 — verify: weave + captured snapshot received over real OTLP"
echo "############################################################################"
echo "#  STEP 4 — E2E VERIFICATION (pass/fail; exit code = number of failed checks)"
echo "############################################################################"
NATIVE_LOG=$(ls -t "$LOGDIR"/*dotnet-Native.log 2>/dev/null | head -1)
FAILS=0
PASSES=0
check() { # check "name" <0-or-1>
  # The literal "[PASS]"/"[FAIL]" tokens are load-bearing: the runbook, the warm-up tally and any grep over a
  # saved log key off them. Colour is added around them, never instead of them.
  if [ "$2" = "1" ]; then
    echo "  ${C_GREEN}[PASS]${C_RESET} $1"
    PASSES=$((PASSES+1))
    [ "$CUR_GROUP" -ge 0 ] && GROUP_PASS[$CUR_GROUP]=$(( ${GROUP_PASS[$CUR_GROUP]} + 1 ))
  else
    echo "  ${C_RED}${C_BOLD}[FAIL]${C_RESET} $1"
    FAILS=$((FAILS+1))
    [ "$CUR_GROUP" -ge 0 ] && GROUP_FAIL[$CUR_GROUP]=$(( ${GROUP_FAIL[$CUR_GROUP]} + 1 ))
  fi
}

# ── DEMO_LINE_ONLY: prove the define-if-absent AssemblyRef branch ────────────
# Deliberately a SEPARATE, SMALL check set rather than the full block below: with no method-level config,
# every method-level assertion would fail for a reason that has nothing to do with what is under test.
if [ -n "${DEMO_LINE_ONLY:-}" ]; then
  echo "  (DEMO_LINE_ONLY: line-probe path in isolation — no method-level weave to piggyback on)"

  # THE ISOLATION CHECK, and it comes first because every other result here is meaningless without it. If a
  # CallTarget weave happened anyway, something DID create the AssemblyRef and this run proves nothing.
  NO_CALLTARGET=0
  if [ -n "$NATIVE_LOG" ] && ! grep -qE "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders" "$NATIVE_LOG" 2>/dev/null; then
    NO_CALLTARGET=1
  fi
  check "ISOLATION: no CallTarget weave in this run, so nothing else could supply the AssemblyRef" "$NO_CALLTARGET"

  # THE PROOF. This line exists only on the branch that emits a reference the module did not have.
  DEFINED_REF=0
  [ -n "$NATIVE_LOG" ] && grep -q "defined a new callback AssemblyRef" "$NATIVE_LOG" 2>/dev/null && DEFINED_REF=1
  check "native profiler DEFINED the callback AssemblyRef (module had none)" "$DEFINED_REF"

  # And the old failure must not appear: before the fix this is what a line-only module produced.
  NOT_SKIPPED=1
  [ -n "$NATIVE_LOG" ] && grep -q "callback AssemblyRef not found" "$NATIVE_LOG" 2>/dev/null && NOT_SKIPPED=0
  check "no probe was skipped for a missing AssemblyRef" "$NOT_SKIPPED"

  WOVE_LINE=0
  [ -n "$NATIVE_LOG" ] && grep -q "wove probeId" "$NATIVE_LOG" 2>/dev/null && WOVE_LINE=1
  check "line probe actually welded into the method body" "$WOVE_LINE"

  # A defined-but-wrong reference does not fail loudly: it binds to nothing and the woven call resolves to no
  # method. So weaving is NOT sufficient — a real captured value has to come back over the wire.
  LINE_SNAP=0
  grep -qE "\[SNAPSHOT-RECEIVED\].*level=line" "$MOCK_OUT" 2>/dev/null && LINE_SNAP=1
  check "LINE-level snapshot received over OTLP (the emitted ref actually bound)" "$LINE_SNAP"

  LINE_VALUE=0
  grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -qE '"total":\{"type":"System.Int32","value":"[1-9][0-9]*"' && LINE_VALUE=1
  check "captured local 'total' carried a real non-zero value through the defined ref" "$LINE_VALUE"

  echo "════════════════════════════════════════════════════════════════════════════"
  if [ "$FAILS" = "0" ]; then
    echo "  ✅ DEFINE-BRANCH PROVEN: a module with NO reference to the DI assembly and NO method-level"
    echo "     weave had its callback AssemblyRef emitted by the profiler, and the line probe captured."
  else
    echo "  ❌ $FAILS check(s) failed in DEMO_LINE_ONLY mode."
  fi
  echo "════════════════════════════════════════════════════════════════════════════"
  exit "$FAILS"
fi

# 1. Native profiler wove the unmodified app's method (from its OWN log).
WOVE_FINISHED=0
[ -n "$NATIVE_LOG" ] && grep -qE "CallTarget_RewriterCallback\(\) Finished: MyCompany.Orders.OrderService.Process" "$NATIVE_LOG" 2>/dev/null && WOVE_FINISHED=1
section "FUNCTION-LEVEL  —  method entry/exit capture on an unmodified app"
check "native profiler ReJIT-wove OrderService.Process" "$WOVE_FINISHED"

# 2. A DI snapshot arrived at the backend over the real OTLP/HTTP wire.
check "snapshot LogRecord received over OTLP /v1/logs" "$SNAP"

# 3. The received snapshot has the correct event name + aws.di.* attributes.
SNAP_ATTRS=0
grep -qE "\[SNAPSHOT-RECEIVED\] event=aws.dynamic_instrumentation.snapshot .*method=Process level=method" "$MOCK_OUT" 2>/dev/null && SNAP_ATTRS=1
check "snapshot has event.name + aws.di.method_name=Process + level=method" "$SNAP_ATTRS"

# 4. The snapshot body carried the captured argument + return value (the actual capture payload).
SNAP_BODY=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "order-" \
  && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "processed:order-" \
  && SNAP_BODY=1
check "snapshot body captured the orderId argument + return value" "$SNAP_BODY"

# 4b. The body arrived as a STRUCTURED OTLP kvlist, not as one JSON string. This is the parity fix
#     DiOtlpLogExporter exists for: consumers walk body.captures.entry.arguments.*, which is impossible
#     when the whole tree is a single string_value. Asserted on the shape the mock reports from the wire,
#     and asserted as an absence too — a string body would still satisfy every other body check here.
BODY_SHAPE=0
grep -q "\[SNAPSHOT-BODY-SHAPE\] kvlist" "$MOCK_OUT" 2>/dev/null \
  && ! grep -q "\[SNAPSHOT-BODY-SHAPE\] string" "$MOCK_OUT" 2>/dev/null \
  && BODY_SHAPE=1
check "snapshot body is a structured OTLP kvlist (no string_value bodies)" "$BODY_SHAPE"

# 5. The agent reported status back to the backend.
STATUS_OK=0
grep -q "report-instrumentation-configuration-status" "$MOCK_OUT" 2>/dev/null && STATUS_OK=1
check "agent reported instrumentation status to the backend" "$STATUS_OK"

# ── LINE-LEVEL checks ────────────────────────────────────────────────────────
# These are the ones that prove the newly-wired line-level path (LineProbeSink + Manager routing +
# native interior weave). They are asserted SEPARATELY from the method-level checks above so a
# line-level failure cannot be masked by a passing function-level run.

if [ "$LINE_CAPABLE" != "1" ]; then
  echo "  [SKIP] line-level checks — profiler has no AddLineProbes export (see the warning above)."
  echo "         This is NOT a pass. Line-level is unproven in this run."
else

# 6. The native profiler performed an INTERIOR (line-probe) rewrite — from its own log, not our code.
LINE_WOVE=0
[ -n "$NATIVE_LOG" ] && grep -qE "LineProbe_Rewrite\(\)|AddLineProbes:" "$NATIVE_LOG" 2>/dev/null && LINE_WOVE=1
beat "now the new capability: line-level"
section "LINE-LEVEL  —  a local variable read out of a running method"
check "native profiler performed a line-probe IL rewrite (AddLineProbes/LineProbe_Rewrite)" "$LINE_WOVE"

# 7. A LINE-level snapshot arrived over the wire. level=line is what distinguishes it from the
#    method-level snapshots — asserted on the attribute the emitter sets from CaptureType.LINE.
LINE_SNAP=0
grep -qE "\[SNAPSHOT-RECEIVED\].*method=Reserve level=line" "$MOCK_OUT" 2>/dev/null && LINE_SNAP=1
check "LINE-level snapshot received over OTLP (method=Reserve level=line)" "$LINE_SNAP"

# 8. THE ASSERTION THAT MATTERS: the captured local carries a REAL, CORRECT value.
#    Reserve(i) computes total = i * 7, so the body must contain a "lines" block whose locals include
#    `total` at one of the expected multiples of 7. A fire count or a mere non-empty body would pass even
#    if the probe read the wrong slot, an unassigned default (0), or a constant — this is rule R11
#    ("assert values, never fire counts") applied to the line path.
LINE_VALUE=0
if grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q '"lines"'; then
  # Accept any tick's value: total ∈ {7,14,21,...}. Tick 0 gives 0, which is indistinguishable from an
  # unassigned local, so 0 is deliberately NOT accepted as proof.
  grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
    | grep -q '"total"' \
    && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
    | grep -qE '"total"[^}]*"(7|14|21|28|35|42|49|56|63|70|77|84|91|98|105|112|119|126|133|140)"' \
    && LINE_VALUE=1
fi
check "captured local 'total' carried a correct non-zero value (i*7)" "$LINE_VALUE"

# ── NON-INT LOCAL CAPTURE ────────────────────────────────────────────────────
# The old native code hardcoded a System.Int32 box token, so ONLY int locals were capturable. These three
# assert the fix per type family, and each checks the captured TYPE as well as the value — a body that
# merely contains the local's name would pass a value-only check even if the box token were wrong.

# 9. Reference type (System.String): must arrive with NO box emitted at all.
STR_OK=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
  | grep -q '"note":{"type":"System.String","value":"item-' && STR_OK=1
section "LINE-LEVEL TYPES  —  reference types and value types, each boxed as itself"
check "captured a STRING local (reference type, no box) with its real value" "$STR_OK"

# 10. Non-int value type (System.DateTime): boxed against its OWN token, not System.Int32.
DT_OK=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
  | grep -q '"stamp":{"type":"System.DateTime"' && DT_OK=1
check "captured a DATETIME local (value type boxed as its own type)" "$DT_OK"

# 11. A second, differently-sized value type (System.Double), so a DateTime pass can't be a size accident.
DBL_OK=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
  | grep -q '"ratio":{"type":"System.Double"' && DBL_OK=1
check "captured a DOUBLE local (second value-type family)" "$DBL_OK"

# ── MULTI-LOCAL CAPTURE ──────────────────────────────────────────────────────
# 12. All three locals from ONE config at ONE line. Asserted per-local rather than as "a DescribeAll
#     snapshot arrived", because the failure mode being guarded is a config that applies only its FIRST
#     local — which would still produce snapshots and still look healthy.
MULTI_OK=0
if grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q '"count":{"type":"System.Int32"' \
   && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q '"label":{"type":"System.String","value":"all-' \
   && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q '"weight":{"type":"System.Double"'; then
  MULTI_OK=1
fi
section "MULTIPLE LOCALS  —  one operator config, several variables, one line"
check "ONE config captured all THREE locals (count/label/weight) at one line" "$MULTI_OK"

# 13. The three probes share ONE IL offset, so all three land on the same source line in the body. If the
#     line keys differed, the probes would have resolved to different offsets — a resolution bug that
#     per-local presence checks alone would not catch.
MULTI_LINE_NOW=$(grep -n '@line-probe-target-multi: count,label,weight' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
SAME_LINE=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
  | grep -E '"(count|label|weight)"' | grep -q "\"lines\":{\"$MULTI_LINE_NOW\"" && SAME_LINE=1
check "all three multi-local probes report the SAME source line ($MULTI_LINE_NOW)" "$SAME_LINE"

# 14. ASYNC — a snapshot arrived for ReserveAsync at all. The operator named `ReserveAsync`, whose own body
#     holds no interior line; resolution had to follow AsyncStateMachineAttribute into
#     `<ReserveAsync>d__N.MoveNext()`. If it had woven the launcher instead, the probe would either fail to
#     resolve or weave somewhere that never runs the user's statement — so the mere arrival of this snapshot,
#     attributed to the operator's method name, is the retarget working.
ASYNC_OK=0
grep -qE "\[SNAPSHOT-RECEIVED\].*method=ReserveAsync.*level=line" "$MOCK_OUT" 2>/dev/null && ASYNC_OK=1
beat "the hard one: async"
section "ASYNC  —  the compiler-generated state machine, invisible to the operator"
check "ASYNC line snapshot received (method=ReserveAsync level=line)" "$ASYNC_OK"

# 15. And it carried the HOISTED local's real, changing value. `total` is a FIELD `<total>5__N` on the state
#     machine, not a slot, so this is the `ldarg.0; ldfld` emission rather than `ldloc`. total == i * 9, and
#     the values must VARY across ticks: a probe that read the wrong field, or an unassigned one, would yield
#     a constant (typically 0) that a presence-only check would happily accept. Requiring two DISTINCT
#     non-zero multiples of 9 is what makes a misread distinguishable from a real read.
ASYNC_VALS=$(grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
  | grep -oE '"total":\{"type":"System.Int32","value":"[0-9]+"\}' \
  | grep -oE '[0-9]+"\}$' | tr -d '"}' | sort -un)
ASYNC_VAL_OK=0
ASYNC_DISTINCT=0
for v in $ASYNC_VALS; do
  # Only multiples of 9 can come from ReserveAsync (the sync Reserve uses *7). Guards against crediting a
  # sync snapshot for the async check, since both locals are named `total`.
  if [ "$v" -gt 0 ] && [ $((v % 9)) -eq 0 ]; then ASYNC_DISTINCT=$((ASYNC_DISTINCT+1)); fi
done
[ "$ASYNC_DISTINCT" -ge 2 ] && ASYNC_VAL_OK=1
check "async hoisted 'total' carried changing real values (i*9); saw $ASYNC_DISTINCT distinct" "$ASYNC_VAL_OK"

# 16. The async probe wove into MoveNext, not into ReserveAsync. This is asserted from the NATIVE log rather
#     than the snapshot, because it is the one place the woven method's real name appears. A weave of
#     `ReserveAsync` itself would be the silent-wrong-target failure: it can succeed and simply never observe
#     the user's line.
ASYNC_WEAVE=0
if [ -n "$NATIVE_LOG" ] && grep -qE "LineProbe_Rewrite.*<ReserveAsync>d__[0-9]+\.MoveNext" "$NATIVE_LOG" 2>/dev/null; then
  ASYNC_WEAVE=1
fi
check "async probe wove the state machine's MoveNext, not the launcher method" "$ASYNC_WEAVE"

# 17. ASYNC NON-INT HOISTED LOCALS. This is the check ReserveAsync CANNOT make: boxing an Int32 against a
#     hardcoded System.Int32 token is accidentally correct, so an int-only async probe passes either way.
#     A String hoisted field must arrive with NO box emitted (a `box` on an object reference is invalid IL and
#     would make the verifier reject the entire rewritten MoveNext), and a Double must be boxed against its
#     OWN token. Values are matched, not just types: `async-item-N` and a real N*2.5 prove the right field was
#     read rather than a same-typed neighbour.
#
#     BOTH CHECKS ARE SCOPED TO THE ASYNC LINE, and that is not cosmetic. The first version grepped the whole
#     log for '"ratio":{"type":"System.Double"' and PASSED under a deliberate revert of the fix — because the
#     SYNCHRONOUS DescribeRatio probe also captures a local named `ratio` of type Double, on a different line.
#     The check was measuring the sync path while claiming to measure the async one. Anchoring on the line
#     number is what makes it a real check.
ASYNC_ML=$(grep -n '@line-probe-target-async-multi: note,ratio,count' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
ASYNC_STR_OK=0
ASYNC_DBL_OK=0
grep -F "\"lines\":{\"$ASYNC_ML\"" "$MOCK_OUT" 2>/dev/null \
  | grep -q '"note":{"type":"System.String","value":"async-item-' && ASYNC_STR_OK=1
# The value must keep its FRACTION. Reverting the fix boxed the double against System.Int32 and produced
# 30/32/35 for 30.0/32.5/35.0 — truncated, plausible, and completely wrong. Requiring a decimal point
# catches that even if the reported type were somehow right.
grep -F "\"lines\":{\"$ASYNC_ML\"" "$MOCK_OUT" 2>/dev/null \
  | grep -qE '"ratio":\{"type":"System.Double","value":"[0-9]+\.[0-9]+"\}' && ASYNC_DBL_OK=1
check "async STRING hoisted field captured with no box (line $ASYNC_ML, real value)" "$ASYNC_STR_OK"
check "async DOUBLE hoisted field boxed as System.Double with its fraction intact (line $ASYNC_ML)" "$ASYNC_DBL_OK"

# 18. All three async locals resolved to DISTINCT fields on the same line. One shared field token would report
#     three names reading one variable — plausible-looking and completely wrong.
ASYNC_MULTI_NOW=$(grep -n '@line-probe-target-async-multi: note,ratio,count' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
ASYNC_TRIO=0
if grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "\"lines\":{\"$ASYNC_MULTI_NOW\".*\"note\"" \
   && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "\"lines\":{\"$ASYNC_MULTI_NOW\".*\"ratio\"" \
   && grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep -q "\"lines\":{\"$ASYNC_MULTI_NOW\".*\"count\""; then
  ASYNC_TRIO=1
fi
check "all THREE async hoisted locals captured at one line ($ASYNC_MULTI_NOW)" "$ASYNC_TRIO"

fi  # LINE_CAPABLE

# ── CAPTURED-VALUE SCHEMA ────────────────────────────────────────────────────
# Every branch of SerializeValue in one snapshot. Each assertion below names the WIRE key it expects, because
# `type` plus exactly one of value/elements/fields/is_null/not_captured_reason is the contract a consumer
# parses against — a body that silently loses a branch still looks like a valid snapshot.
section "CAPTURED-VALUE SCHEMA  —  every shape a captured value can take"

# One grep per branch against the Describe snapshot, identified by an argument name no other probe has.
SHOWCASE_LINES=$(grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null | grep '"longText"' || true)
has_shape() { printf '%s' "$SHOWCASE_LINES" | grep -q "$1" && echo 1 || echo 0; }

check "schema showcase snapshot received (ShowcaseService.Describe, 7 arguments)" \
  "$([ -n "$SHOWCASE_LINES" ] && echo 1 || echo 0)"

# 255 is MaxStringLength; the assertion is on the CLAMPED length, so a change to the enforced maximum breaks
# it deliberately rather than silently widening what a customer sees.
check "STRING over the limit: value + truncated=true, clamped to 255" \
  "$(has_shape '"longText":{"type":"System.String","value":"x\{255\}","truncated":true}')"

# size is the ORIGINAL count (50), elements holds the capped 20. Reporting 20 as the size would tell an
# operator the collection really had 20 items — wrong rather than partial.
check "COLLECTION over the limit: elements capped at 20 with size=50 (the original)" \
  "$(has_shape '"manyItems":{"type":"System.Collections.Generic.List.*"size":50')"

check "DICTIONARY: fields keyed by the dictionary key (alpha/beta/gamma)" \
  "$(has_shape '"tags":{[^}]*"fields":{"alpha".*"beta".*"gamma"')"

check "OBJECT: public fields and properties captured (Address.City/Zip)" \
  "$(has_shape '"address":{[^}]*"fields":{[^}]*"City"')"

check "NULL argument: is_null=true rather than an absent key" \
  "$(has_shape '"missing":{"type":"null","is_null":true}')"

# The two not_captured_reason branches, which only fire when there is no partial data to emit.
check "DEPTH limit: nested chain reports not_captured_reason=DEPTH" \
  "$(has_shape '"not_captured_reason":"DEPTH"')"

check "CYCLE: a self-referencing object reports not_captured_reason=ALREADY_CAPTURED" \
  "$(has_shape '"not_captured_reason":"ALREADY_CAPTURED"')"

# CaptureStackTrace is the only config in this run that asks for a stack, so this also proves the flag is
# honoured rather than ignored.
check "STACK: CaptureStackTrace produced a stack containing the customer's own method" \
  "$(has_shape '"stack":\[.*MyCompany.Orders.ShowcaseService.Describe')"

# A thrown exception replaces the return value. Asserted on the MESSAGE, not just the presence of the key:
# an empty throwable would satisfy a key-exists check.
THROWABLE_OK=0
grep -E "\[SNAPSHOT-BODY\]" "$MOCK_OUT" 2>/dev/null \
  | grep -q '"throwable":{"type":"System.InvalidOperationException","message":"showcase failure: tick-' \
  && THROWABLE_OK=1
check "THROWABLE: a throwing method reports type + message instead of a return value" "$THROWABLE_OK"

# 6. (beta mode only) the config API leg actually round-tripped through REAL beta with no forward errors.
if [ -n "${DI_BETA_ENDPOINT:-}" ]; then
  # Round-trip is proven when the SIGNED requests reach beta and beta evaluates them. create is 2xx on a
  # fresh config OR 409 (Conflict) when it already exists from a prior run — both prove signed connectivity
  # (409 = beta authenticated, parsed, matched an existing LocationHash). Configs persist until explicit
  # cleanup, so 409 is the expected steady-state. list must be 2xx.
  BETA_OK=0
  if grep -qE "\[BETA-FORWARD\] list-instrumentation-configurations → HTTP 2" "$MOCK_OUT" 2>/dev/null \
     && grep -qE "\[BETA-FORWARD\] create-instrumentation-configuration → HTTP (2..|409)" "$MOCK_OUT" 2>/dev/null; then
    BETA_OK=1
  fi
  check "config API leg (list+create) round-tripped through REAL beta ($DI_BETA_ENDPOINT)" "$BETA_OK"

  # No LINE_NOT_EXECUTABLE anywhere in the run. This is the stale-config guard: beta persists configs for
  # ~24 h, and a line-level config holds an absolute line number, so a source edit leaves yesterday's probe
  # pointing at a comment. It then reports ERROR/LINE_NOT_EXECUTABLE on every poll — forever. That is the
  # agent behaving CORRECTLY on a bad config, which is exactly why it can hide in a run whose other checks
  # pass. cleanup_stale_line_configs deletes them first; this asserts the cleanup actually worked.
  # Deletes must have SUCCEEDED, asserted separately from their effect. The first version of this cleanup
  # sent the Kotlin camelCase field names and every delete returned HTTP 400 — but the run still reported
  # the deletes as "issued", so the only visible symptom was the two checks below failing for a reason that
  # looked unrelated. Asserting the response code names the real cause immediately.
  DELETE_OK=1
  grep -q "delete-instrumentation-configuration → HTTP [45]" "$MOCK_OUT" 2>/dev/null && DELETE_OK=0
  check "every delete-instrumentation-configuration returned a non-error status" "$DELETE_OK"

  # Also scoped to post-cleanup traffic: on a first run against a dirty account the agent can report ERROR
  # for a stale config in the window before cleanup deletes it, which says nothing about this run's health.
  # The boundary is asserted SEPARATELY and first. Without it the two scoped checks below cannot mean what
  # they claim, and a silent fallback to the whole log makes them report on pre-cleanup traffic.
  BOUNDARY_OK=0
  [ -n "${CLEANUP_BOUNDARY:-}" ] && BOUNDARY_OK=1
  check "cleanup boundary recorded, so post-cleanup assertions have a real scope" "$BOUNDARY_OK"

  POST_CLEANUP_ERR=$(post_cleanup_log) || POST_CLEANUP_ERR=""
  NO_STALE=0
  if [ "$BOUNDARY_OK" = "1" ]; then
    NO_STALE=1
    printf '%s' "$POST_CLEANUP_ERR" | grep -q "LINE_NOT_EXECUTABLE" && NO_STALE=0
  fi
  check "no stale line-level config reported LINE_NOT_EXECUTABLE (cleanup worked)" "$NO_STALE"

  # And every line-level config that beta returned must carry the CURRENT source line. A stale config that
  # somehow survived cleanup would show a different number here, which the check above only catches once the
  # agent has reported on it.
  CUR_LINE=$(grep -n '@line-probe-target: total' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
  # CAPTURE FIRST, then compare. `... | sort -u | grep -qv PATTERN` looks right and is not: grep -q exits on
  # its first match, sort dies of SIGPIPE, and under `set -o pipefail` the whole pipeline reports failure —
  # so the check returned "clean" for deliberately stale input. Verified by feeding it a known-stale sample
  # (line 122 alongside 131) and confirming it now goes red where the piped form stayed green.
  # Only traffic AFTER the recorded cleanup boundary. Everything before it is the pre-cleanup inventory,
  # which is SUPPOSED to contain the stale lines — that is how cleanup located them.
  POST_CLEANUP=$(post_cleanup_log) || POST_CLEANUP=""
  RESERVE_LINES=$(printf '%s' "$POST_CLEANUP" \
    | grep -oE '"LineNumber":[0-9]+,"MethodName":"Reserve"' \
    | grep -oE '^"LineNumber":[0-9]+' | grep -oE '[0-9]+' | sort -u)
  LINES_CURRENT=$BOUNDARY_OK
  # A CHECK THAT CANNOT FAIL ON AN EMPTY SET IS NOT A CHECK. The loop below only ever downgrades, so when
  # beta returned no Reserve line config at all this reported PASS having observed NOTHING — measured on an
  # expired-credential run where every call 403'd: "saw: none" and still green. That is the one assertion
  # meant to catch a stale absolute line number, so it has to require at least one observation.
  if [ -z "$RESERVE_LINES" ]; then
    LINES_CURRENT=0
  fi
  # No `tr '\n' ' '` here: it leaves a TRAILING space, and the loop variable then compares "131 " against
  # "131" and never matches — a check that fails on correct input is as useless as one that passes on bad
  # input. Word-splitting on the raw newline-separated list avoids it entirely.
  for L in $RESERVE_LINES; do
    [ "$L" = "$CUR_LINE" ] || LINES_CURRENT=0
  done
  check "every Reserve line config points at the CURRENT source line ($CUR_LINE); saw: $(echo ${RESERVE_LINES:-none} | tr '\n' ' ')" "$LINES_CURRENT"
fi

echo "════════════════════════════════════════════════════════════════════════════"
if [ -n "$NATIVE_LOG" ]; then
  echo "  native-profiler proof (its own log):"
  grep -E "received id: probe-orderservice|CallTarget_RewriterCallback.*OrderService.Process" "$NATIVE_LOG" 2>/dev/null \
    | sed -E 's/^\[[^]]*\] \[[^]]*\] \[[^]]*\] /    /' | head -4
fi
echo
# ── SCOREBOARD ───────────────────────────────────────────────────────────────
# Per-capability, because "19 checks passed" tells a reader nothing about WHAT is covered. Each row is a
# feature an operator can actually use, and the counts are accumulated by check() rather than restated here,
# so a row cannot claim coverage that did not run.
if [ "$CUR_GROUP" -ge 0 ]; then
  echo "  ${C_BOLD}FEATURE COVERAGE${C_RESET}"
  for i in $(seq 0 "$CUR_GROUP"); do
    gp=${GROUP_PASS[$i]}; gf=${GROUP_FAIL[$i]}
    if [ "$gf" -eq 0 ] && [ "$gp" -gt 0 ]; then mark="${C_GREEN}✔${C_RESET}"; else mark="${C_RED}✘${C_RESET}"; fi
    printf "    %s  %2s/%-2s  %s\n" "$mark" "$gp" "$((gp+gf))" "${GROUP_NAMES[$i]}"
  done
  echo
fi

TOTAL=$((PASSES+FAILS))
if [ "$FAILS" -eq 0 ]; then
  echo "  ${C_GREEN}${C_BOLD}✅  ALL $TOTAL CHECKS PASSED${C_RESET}"
  echo
  echo "  ${C_BOLD}What this proves, end to end:${C_RESET}"
  echo "    an ${C_BOLD}unmodified${C_RESET} app, enabled by ${C_BOLD}environment variables only${C_RESET} — no code change, no redeploy"
  echo "    → operator creates a probe through the ${C_BOLD}public API shape${C_RESET}"
  echo "    → agent polls, native profiler ${C_BOLD}ReJIT-weaves the running method${C_RESET}"
  echo "    → arguments, return values and ${C_BOLD}local variables${C_RESET} captured with real values"
  echo "    → exported over ${C_BOLD}real OTLP${C_RESET} and verified on the wire, status reported back"
  echo
  echo "  ${C_DIM}The only mock is the backend, and it speaks the exact wire shape production does:"
  echo "  swap OTEL_AWS_DYNAMIC_INSTRUMENTATION_API_URL at GA and nothing else changes.${C_RESET}"
else
  echo "  ${C_RED}${C_BOLD}❌  $FAILS of $TOTAL CHECKS FAILED${C_RESET} — see the [FAIL] lines above; mock output: $MOCK_OUT"
fi
echo "════════════════════════════════════════════════════════════════════════════"

# ════════════════════════════════════════════════════════════════════════════
#  STEP 5 — YOU DRIVE IT: create a probe by hand, watch it capture, delete it
# ════════════════════════════════════════════════════════════════════════════
# Deliberately AFTER the scoreboard: the automated result is stated first, then the controls are handed over.
#
# The target is InventoryService.LiveDemo, which carries NO configuration at boot. That matters — every other
# target in this run was woven during startup, so attaching a probe to one of them would prove nothing about
# doing it to a method that is already serving traffic uninstrumented.
#
# NOT COUNTED IN $FAILS. This segment's outcome depends on a human pressing Enter within a poll interval, so
# folding it into the exit code would make the demo's pass/fail depend on presentation timing. It reports its
# own result plainly instead.
if [ "$DEMO_INTERACTIVE" = "1" ] && [ "$LINE_CAPABLE" = "1" ]; then
  # `grep -c` prints 0 AND exits 1 on no match, so `grep -c … || echo 0` yields "0\n0" — which then reaches
  # `[ "$NOW" -gt "$BEFORE" ]` as a two-line string and errors with "integer expression expected" instead of
  # comparing. Take the first line and validate it is numeric.
  live_hits() {
    local n
    n=$(grep -c "locationHash=$1" "$MOCK_OUT" 2>/dev/null | head -1)
    case "$n" in ''|*[!0-9]*) n=0 ;; esac
    printf '%s' "$n"
  }

  # Is the app still serving? If it exited, everything below would be vacuous.
  if ! kill -0 "${APP_PID:-0}" 2>/dev/null; then
    echo
    echo "  ${C_YELLOW}(skipping the live segment: the sample app has already exited)${C_RESET}"
    exit "$FAILS"
  fi

  pause "STEP 5 — create a probe yourself, watch it capture, then delete it"
  echo "############################################################################"
  echo "#  ${C_BOLD}STEP 5 — YOU are the operator${C_RESET}"
  echo "############################################################################"
  echo
  echo "  The app is ${C_BOLD}still running${C_RESET} and has been calling InventoryService.LiveDemo(i) on every"
  echo "  tick this whole time — with ${C_BOLD}no probe on it${C_RESET}. Nothing has been captured from it:"
  echo

  LIVE_LINE=$(grep -n '@live-probe-target: amount' "$SCRIPT_DIR/SampleApp/Program.cs" | cut -d: -f1)
  echo "    $ grep -c 'method=LiveDemo' mock-backend.out   →   $(grep -c 'method=LiveDemo' "$MOCK_OUT" 2>/dev/null || echo 0)"
  echo
  echo "  Pick what to attach, live, to that running method:"
  echo "    ${C_BOLD}1${C_RESET}) FUNCTION-LEVEL probe  — captures the argument and the return value"
  echo "    ${C_BOLD}2${C_RESET}) LINE-LEVEL breakpoint — captures the local 'amount' at source line $LIVE_LINE"
  printf '\n%s' "  ${C_BOLD}${C_CYAN}Choice [1/2, default 2]: ${C_RESET}" >/dev/tty
  read -r LIVE_CHOICE </dev/tty || LIVE_CHOICE=2
  [ -n "$LIVE_CHOICE" ] || LIVE_CHOICE=2

  if [ "$LIVE_CHOICE" = "1" ]; then
    LIVE_TYPE="PROBE"; LIVE_HASH="probe-inventoryservice-livedemo"
    LIVE_LOC='"LineNumber":0'
    LIVE_CAP='"CaptureArguments":["id"],"CaptureReturn":true'
    LIVE_DESC="function-level probe (argument + return value)"
  else
    LIVE_TYPE="BREAKPOINT"; LIVE_HASH="breakpoint-inventoryservice-livedemo"
    LIVE_LOC='"LineNumber":'"$LIVE_LINE"
    LIVE_CAP='"CaptureLocals":["amount"]'
    LIVE_DESC="line-level breakpoint on line $LIVE_LINE, capturing the local 'amount'"
  fi

  echo
  echo "  → $LIVE_DESC"
  pause "send the create-instrumentation-configuration request"

  curl -s -X POST "http://127.0.0.1:$PORT/create-instrumentation-configuration" \
    -H "Content-Type: application/json" -d '{
    "InstrumentationType":"'"$LIVE_TYPE"'","Service":"sample-app","Environment":"staging",
    "SignalType":"SNAPSHOT","AccountId":"000000000000",
    "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"MyCompany.Orders",
      "ClassName":"InventoryService","MethodName":"LiveDemo",
      "FilePath":"Program.cs",'"$LIVE_LOC"'}},
    "CaptureConfiguration":{"CodeCapture":{'"$LIVE_CAP"'}}}' >/dev/null
  sleep 1   # let the mock print its operator banner before the narration continues

  echo
  echo "  ${C_DIM}No restart. No redeploy. The agent polls every 5s, then the profiler ReJIT-rewrites"
  echo "  the method the next time it is called.${C_RESET}"
  echo
  BEFORE=$(live_hits "$LIVE_HASH")
  echo -n "  waiting for the first capture "
  LIVE_OK=0
  for _ in $(seq 1 40); do
    NOW=$(live_hits "$LIVE_HASH")
    if [ "$NOW" -gt "$BEFORE" ]; then LIVE_OK=1; break; fi
    echo -n "."
    sleep 1
  done
  echo

  if [ "$LIVE_OK" = "1" ]; then
    echo "  ${C_GREEN}${C_BOLD}✔ CAPTURING${C_RESET} — telemetry is arriving from a method that had no probe a moment ago:"
    echo
    grep "locationHash=$LIVE_HASH" -A1 "$MOCK_OUT" 2>/dev/null | grep "SNAPSHOT-BODY" | tail -3 \
      | sed -E 's/^/    /' | cut -c1-200
    echo
    sleep 4
    echo "    hits so far: ${C_BOLD}$(live_hits "$LIVE_HASH")${C_RESET} and climbing (one per tick)"
  else
    echo "  ${C_YELLOW}no capture yet${C_RESET} — the weave lands on the method's next call; give it a moment"
    echo "  and re-check with: grep -c 'locationHash=$LIVE_HASH' $MOCK_OUT"
  fi

  pause "now DELETE it — and watch the telemetry stop"

  HITS_AT_DELETE=$(live_hits "$LIVE_HASH")
  echo
  echo "  hits at the moment of deletion: ${C_BOLD}$HITS_AT_DELETE${C_RESET}"
  curl -s -X POST "http://127.0.0.1:$PORT/delete-instrumentation-configuration" \
    -H "Content-Type: application/json" -d '{
    "InstrumentationType":"'"$LIVE_TYPE"'","Service":"sample-app","Environment":"staging",
    "SignalType":"SNAPSHOT","AccountId":"000000000000",
    "LocationIdentifier":{"LocationHash":"'"$LIVE_HASH"'"}}' >/dev/null
  sleep 1

  echo
  echo "  ${C_DIM}The next poll returns a configuration list without it, and capture stops. The app keeps"
  echo "  calling the method the entire time — watch the tick counter, not the snapshot counter.${C_RESET}"
  echo
  # DELETION IS NOT INSTANT, and the check has to respect that or it reports a failure for correct behaviour.
  # The backend forgets the config at once; the agent only learns on its NEXT POLL (5s here) and keeps
  # capturing until then. Measured: 9 hits at deletion → 11 shortly after → frozen at 11 for the next 30s.
  # So: settle for two poll intervals, then require the count to be genuinely UNCHANGED.
  echo -n "  letting the agent notice (2 poll intervals) "
  for _ in $(seq 1 12); do echo -n "."; sleep 1; done
  echo
  HITS_SETTLED=$(live_hits "$LIVE_HASH")
  echo "  after the next poll: ${C_BOLD}$HITS_SETTLED${C_RESET} ${C_DIM}(calls made before the agent noticed)${C_RESET}"
  echo -n "  now watching 15s of continued traffic "
  for _ in $(seq 1 15); do echo -n "."; sleep 1; done
  echo
  HITS_AFTER=$(live_hits "$LIVE_HASH")
  APP_ALIVE=0; kill -0 "${APP_PID:-0}" 2>/dev/null && APP_ALIVE=1

  echo
  if [ "$APP_ALIVE" != "1" ]; then
    echo "  ${C_YELLOW}⚠ inconclusive: the app exited during the observation window, so a frozen counter"
    echo "    proves nothing. Re-run with a larger DI_TICKS.${C_RESET}"
  elif [ "$HITS_AFTER" -eq "$HITS_SETTLED" ]; then
    echo "  ${C_GREEN}${C_BOLD}✔ STOPPED${C_RESET} — frozen at ${C_BOLD}$HITS_AFTER${C_RESET} through 15s of continued traffic,"
    echo "    and the app is ${C_BOLD}still running and still calling the method${C_RESET}."
    echo
    echo "    ${C_DIM}That is the full operator lifecycle: attach to a live process, capture real values,"
    echo "    detach — no restart, no redeploy, no code change, at any point.${C_RESET}"
  else
    echo "  ${C_RED}✘ still capturing:${C_RESET} $HITS_SETTLED → $HITS_AFTER after the agent should have dropped it"
  fi
  echo "════════════════════════════════════════════════════════════════════════════"
fi

exit "$FAILS"
