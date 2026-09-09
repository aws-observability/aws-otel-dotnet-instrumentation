#!/bin/bash
# ============================================================================
#  DI OPERATOR CONSOLE — you are the customer. Attach and detach probes at will.
#
#  Run this in a SECOND TERMINAL while run-demo.sh has the app running. That
#  separation is the point: this process shares nothing with the application —
#  no code, no restart, no coordination. It only talks to the backend API, the
#  same way the CloudWatch console does.
#
#    Terminal 1:  bash run-demo.sh          (or: bash run-live.sh)
#    Terminal 2:  bash di-console.sh
#
#  Everything here is a loop, on purpose. A scripted create-then-delete proves
#  it works ONCE; this proves you can do it whenever you like, to whatever you
#  like, as many times as you like, while the app keeps serving traffic.
# ============================================================================
set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PORT="${DI_PORT:-2000}"
API="http://127.0.0.1:$PORT"
MOCK_OUT="$SCRIPT_DIR/logs/mock-backend.out"
APP_SRC="$SCRIPT_DIR/SampleApp/Program.cs"

if [ -t 1 ]; then
  R=$'\033[0m'; B=$'\033[1m'; D=$'\033[2m'; G=$'\033[32m'; RD=$'\033[31m'; C=$'\033[36m'; Y=$'\033[33m'
else
  R=''; B=''; D=''; G=''; RD=''; C=''; Y=''
fi

# ── Preflight ───────────────────────────────────────────────────────────────
# A console that silently talks to nothing is worse than one that refuses to start: every "no hits" answer
# would look like a product failure instead of a missing backend.
# Service and Environment are REQUIRED by the real API — beta returns
# "Value at 'environment' failed to satisfy constraint: Member must not be null" without them. A preflight
# that sends a shape the real backend rejects pollutes the log with a 400 that looks like a product failure.
if ! curl -s -m 2 -o /dev/null -X POST "$API/list-instrumentation-configurations" \
      -H 'Content-Type: application/json' \
      -d '{"InstrumentationType":"PROBE","Service":"sample-app","Environment":"staging","SignalType":"SNAPSHOT"}'; then
  echo "${RD}ERROR${R}: no backend on $API."
  echo "  Start the demo first, in another terminal:"
  echo "    cd $SCRIPT_DIR && DI_REPO_ROOT=/path/to/aws-otel-dotnet-instrumentation bash run-demo.sh"
  exit 1
fi

# ── Target discovery ────────────────────────────────────────────────────────
# Read from the SOURCE rather than a hardcoded list, so the console cannot drift from the app it is driving,
# and so line numbers are always the CURRENT ones. A stale absolute line number is the single most common way
# a line-level probe fails, and hardcoding one here would manufacture that failure during a demo.
discover() {
  python3 - "$APP_SRC" <<'PY'
import os, re, sys
src = open(sys.argv[1]).read().split("\n")
method_re = re.compile(r'^\s*public\s+(?:async\s+)?[\w<>\[\]?]+\s+(\w+)\s*\(([^)]*)\)')
class_re  = re.compile(r'^\s*public\s+class\s+(\w+)')
ns_re     = re.compile(r'^\s*namespace\s+([\w.]+)')
# Accepts @line-probe-target, @live-probe-target, and the -async / -multi suffixes. Written as one
# alternation rather than an optional "live-" prefix, because the live marker is NOT "live-line-probe-target"
# and an over-clever optional group silently skipped it — the newest target was missing from the menu.
marker_re = re.compile(r'@(?:line|live)-probe-target(?:-async)?(?:-multi)?:\s*([\w,]+)')

# BulkService exists only for the bulk-apply load test and is called ONLY when DI_BULK_MODE=1. Offering those
# methods in a normal run would let the presenter probe something the app never calls, and "0 hits" would
# read as a product failure rather than a target that is simply idle.
skip_classes = set() if os.environ.get("DI_BULK_MODE") == "1" else {"BulkService"}

# CODE UNIT IS THE NAMESPACE, READ FROM THE SOURCE. The agent binds a target as CodeUnit + "." + ClassName,
# so this must be "MyCompany.Orders" — the namespace the probe classes are declared in — and NOT the assembly
# name "SampleApp". Getting it wrong names a type that does not exist: the configuration is accepted, the
# agent finds nothing to instrument, and the only symptom is a probe that never captures. Measured: a config
# with CodeUnit=SampleApp sat at zero hits for 40s with no error anywhere.
namespace = ""
cur_class = cur_method = None
methods, lines = [], []
for i, line in enumerate(src, start=1):
    m = ns_re.match(line)
    if m:
        namespace = m.group(1)
    m = class_re.match(line)
    if m:
        cur_class = m.group(1); continue
    m = method_re.match(line)
    if m and cur_class:
        cur_method = m.group(1)
        # Split on top-level commas only: `Dictionary<string, int> tags` has a comma INSIDE the generic,
        # and a plain split produced 'Dictionary<string' as a parameter name.
        raw, depth, buf = m.group(2), 0, ""
        parts = []
        for ch in raw:
            if ch in "<([":
                depth += 1
            elif ch in ">)]":
                depth -= 1
            if ch == "," and depth == 0:
                parts.append(buf); buf = ""
            else:
                buf += ch
        parts.append(buf)
        params = [x.strip().split()[-1] for x in parts if x.strip()]
        if cur_class not in skip_classes:
            methods.append((cur_class, cur_method, ",".join(params)))
    m = marker_re.search(line)
    if m and cur_class and cur_method and cur_class not in skip_classes:
        lines.append((cur_class, cur_method, i, m.group(1)))

print("#CODEUNIT")
print(namespace)
print("#METHODS")
for c, mm, pp in methods:
    print(f"{c}\t{mm}\t{pp}")
print("#LINES")
for c, mm, ln, locals_ in lines:
    print(f"{c}\t{mm}\t{ln}\t{locals_}")
PY
}

# NO `mapfile` HERE. macOS ships bash 3.2, where mapfile does not exist — the script would have aborted
# before showing a single menu. A plain read loop is portable to both.
DISC="$(discover)"
CODE_UNIT="$(printf '%s' "$DISC" | sed -n '/^#CODEUNIT$/,/^#METHODS$/p' | grep -v '^#' | head -1)"
METHODS=(); LINES=()
while IFS= read -r row; do
  [ -n "$row" ] && METHODS+=("$row")
done <<< "$(printf '%s' "$DISC" | sed -n '/^#METHODS$/,/^#LINES$/p' | grep -v '^#')"
while IFS= read -r row; do
  [ -n "$row" ] && LINES+=("$row")
done <<< "$(printf '%s' "$DISC" | sed -n '/^#LINES$/,$p' | grep -v '^#')"

if [ -z "$CODE_UNIT" ]; then
  echo "${RD}ERROR${R}: could not read the namespace from $APP_SRC."
  echo "  CodeUnit must be the namespace; a wrong one is accepted by the backend and then never captures."
  exit 1
fi
if [ "${#METHODS[@]}" -eq 0 ] || [ "${#LINES[@]}" -eq 0 ]; then
  echo "${RD}ERROR${R}: found no probe targets in $APP_SRC (methods=${#METHODS[@]} lines=${#LINES[@]})."
  echo "  The console reads targets from the source; if that parse breaks, every menu would be empty."
  exit 1
fi

# ── Backend helpers ─────────────────────────────────────────────────────────
list_configs() { # list_configs TYPE
  curl -s -X POST "$API/list-instrumentation-configurations" -H 'Content-Type: application/json' \
    -d '{"InstrumentationType":"'"$1"'","Service":"sample-app","Environment":"staging","SignalType":"SNAPSHOT"}'
}

all_configs() { # -> "TYPE<TAB>HASH<TAB>Class.Method<TAB>line"
  for t in PROBE BREAKPOINT; do
    list_configs "$t" | python3 -c '
import json,sys
t=sys.argv[1]
try: d=json.load(sys.stdin)
except Exception: sys.exit()
for c in d.get("LatestConfigurations") or []:
    cl=c.get("Location",{}).get("CodeLocation",{})
    print("\t".join([t, c.get("LocationHash",""),
        f'"'"'{cl.get("ClassName","?")}.{cl.get("MethodName","?")}'"'"',
        str(cl.get("LineNumber") or 0)]))
' "$t"
  done
}

# `grep -c` PRINTS 0 AND EXITS 1 when there is no match, so the obvious `grep -c … || echo 0` emits TWO
# lines ("0\n0"). That leaked a stray 0 into the table here, and in run-demo.sh it fed a two-line value into
# `[ "$n" -gt … ]`, which is an "integer expression expected" error rather than a comparison. Take the first
# line and validate it is a number, which also covers grep exiting 2 on a missing file.
hits_for() {
  local n
  n=$(grep -c "locationHash=$1" "$MOCK_OUT" 2>/dev/null | head -1)
  case "$n" in ''|*[!0-9]*) n=0 ;; esac
  printf '%s' "$n"
}

app_running() { pgrep -f "SampleApp.dll" >/dev/null 2>&1; }

# POSTS A CREATE AND CHECKS THE STATUS, instead of firing and hoping. In beta mode the local backend forwards
# to the real API and returns ITS status, so a rejected configuration is knowable immediately — and the old
# `curl … >/dev/null` threw that away, leaving the console to wait 40s for a capture that could never come.
# Measured against real beta: a method-level create is rejected with two validation errors, and the only
# symptom was a silent 40-second wait.
# Returns 0 on success; on failure prints the backend's own message and returns 1.
CREATED_HASH=""
post_create() { # post_create JSON  -> sets CREATED_HASH on success
  local out status body
  out=$(curl -s -w '\n%{http_code}' -X POST "$API/create-instrumentation-configuration" \
        -H 'Content-Type: application/json' -d "$1")
  status=$(printf '%s' "$out" | tail -1)
  body=$(printf '%s' "$out" | sed '$d')
  case "$status" in
    2*)
      # TAKE THE HASH FROM THE RESPONSE, never derive it. The local mock happens to build a predictable
      # "breakpoint-class-method" hash, but the REAL backend assigns its own opaque 16-hex id — so a derived
      # hash matches nothing in beta mode and every capture check would report "no capture" while the probe was
      # working perfectly. The server is the only authority on its own identifier.
      CREATED_HASH=$(printf '%s' "$body" | python3 -c '
import json,sys
try: print(json.load(sys.stdin).get("LocationHash",""))
except Exception: print("")
' 2>/dev/null)
      [ -n "$CREATED_HASH" ] && echo "  ${D}backend assigned LocationHash: $CREATED_HASH${R}"
      return 0 ;;
    403)
      # `{"Message":null}` is all beta returns for an unauthorized principal, which is useless on its own. The
      # cause is almost always the credentials, not the configuration — and the tell is that LIST fails too.
      echo "  ${RD}${B}403 — not authorized against beta.${R}"
      echo "  ${D}Beta lives in its own account and only accepts that account's credentials. Check:"
      echo "    1. are the credentials expired?  Isengard temp creds are short-lived"
      echo "    2. is this the BETA account's principal?  your own account cannot call the beta endpoint"
      echo "  Confirm with: aws sts get-caller-identity"
      echo "  The backend log shows which key was used: grep BETA-CREDS logs/mock-backend.out${R}"
      return 1 ;;
    409)
      echo "  ${Y}409 — a configuration already exists for this exact location.${R}"
      echo "  ${D}Beta keeps configurations for ~24h, so an earlier run's probe is still there. Delete it"
      echo "  first (option 3), or pick a different target.${R}"
      return 1 ;;
    *)
      echo "  ${RD}${B}create rejected — HTTP $status${R}"
      printf '%s\n' "$body" | python3 -c '
import json,sys
raw=sys.stdin.read()
try:
    msg=json.loads(raw).get("message") or raw
except Exception:
    msg=raw
for part in str(msg).split(";"):
    part=part.strip()
    if part: print("    " + part)
' 2>/dev/null || printf '    %s\n' "$body"
      return 1 ;;
  esac
}

banner() {
  echo
  echo "${C}${B}══════════════════════════════════════════════════════════════════════════${R}"
  echo "${C}${B}  $1${R}"
  echo "${C}${B}══════════════════════════════════════════════════════════════════════════${R}"
}

show_state() {
  banner "CURRENT STATE"
  if app_running; then
    echo "  application: ${G}running${R} (calling every target once per second)"
  else
    echo "  application: ${RD}NOT running${R} — start run-demo.sh in the other terminal"
  fi
  echo
  local rows; rows="$(all_configs)"
  if [ -z "$rows" ]; then
    echo "  ${D}no configurations — the app is running completely uninstrumented${R}"
  else
    printf "  %-11s %-42s %-28s %6s %s\n" "TYPE" "LOCATION HASH" "TARGET" "LINE" "HITS"
    while IFS=$'\t' read -r t h tgt ln; do
      [ -n "$h" ] || continue
      printf "  %-11s %-42s %-28s %6s ${B}%s${R}\n" "$t" "$h" "$tgt" "$ln" "$(hits_for "$h")"
    done <<< "$rows"
  fi
  echo
}

# For an ARBITRARY line number, find the class and method that contain it. Needed because a configuration is
# addressed by ClassName + MethodName + LineNumber, so "probe line 173" still has to resolve to a method — and
# the presenter should not have to know which one. Also returns the source text, so the audience sees exactly
# what is being probed rather than a bare number.
enclosing() { # enclosing LINE -> "Class<TAB>Method<TAB>source text"
  python3 - "$APP_SRC" "$1" <<'INNER'
import re, sys
src = open(sys.argv[1]).read().split("\n")
want = int(sys.argv[2])
if want < 1 or want > len(src):
    print("\t\t")
    raise SystemExit
method_re = re.compile(r'^\s*public\s+(?:async\s+)?[\w<>\[\]?]+\s+(\w+)\s*\(([^)]*)\)')
class_re  = re.compile(r'^\s*public\s+class\s+(\w+)')
cl = mm = ""
for i, line in enumerate(src[:want], start=1):
    m = class_re.match(line)
    if m:
        cl = m.group(1)
    m = method_re.match(line)
    if m:
        mm = m.group(1)
print(f"{cl}\t{mm}\t{src[want-1].strip()}")
INNER
}

# ── Actions ─────────────────────────────────────────────────────────────────
add_probe() { # function-level
  banner "ADD A FUNCTION-LEVEL PROBE  (captures arguments + return value)"
  local i=1
  for m in "${METHODS[@]}"; do
    IFS=$'\t' read -r cl mm pp <<< "$m"
    printf "   %2d) %-28s args: %s\n" "$i" "$cl.$mm" "${pp:-<none>}"
    i=$((i+1))
  done
  printf '\n%s' "  ${B}${C}which method? [1-$((i-1))]: ${R}"; read -r pick
  [[ "$pick" =~ ^[0-9]+$ ]] || { echo "  ${Y}cancelled${R}"; return; }
  IFS=$'\t' read -r cl mm pp <<< "${METHODS[$((pick-1))]}"
  [ -n "${mm:-}" ] || { echo "  ${Y}no such choice${R}"; return; }

  local json_args
  json_args=$(printf '%s' "${pp:-}" | python3 -c 'import json,sys; print(json.dumps([x.strip() for x in sys.stdin.read().split(",") if x.strip()]))')

  echo
  echo "  creating a PROBE on ${B}$cl.$mm${R} capturing ${B}${pp:-<no args>}${R} + return …"
  # TWO THINGS THE REAL API REQUIRES THAT THE LOCAL MOCK DOES NOT.
  #  1. CaptureLimits must be present — beta: "captureLimits ... Member must not be null".
  #  2. LineNumber is OMITTED, not sent as 0. Beta enforces "lineNumber ... must be >= 1", so a method-level
  #     config that sends 0 is rejected outright. Omitting it is the shape beta itself returns for an existing
  #     method-level config (it came back with lineNumber absent), so "absent" is how method-level is
  #     expressed. HYPOTHESIS FROM ONE OBSERVATION -- confirm against beta before relying on it.
  post_create '{
    "InstrumentationType":"PROBE","Service":"sample-app","Environment":"staging",
    "SignalType":"SNAPSHOT","AccountId":"000000000000",
    "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"'"$CODE_UNIT"'",
      "ClassName":"'"$cl"'","MethodName":"'"$mm"'","FilePath":"Program.cs"}},
    "CaptureConfiguration":{"CodeCapture":{"CaptureArguments":'"$json_args"',"CaptureReturn":true,"CaptureStackTrace":true,"CaptureLimits":{"MaxHits":100}}}}' || return
  watch_hits "probe-$(lower "$cl")-$(lower "$mm")"
}

add_breakpoint() { # line-level
  banner "ADD A LINE-LEVEL BREAKPOINT  (captures locals at a source line)"
  echo "  ${D}These are the lines in SampleApp that have capturable locals. Line numbers are read"
  echo "  from the source right now, so they are always current.${R}"
  echo
  local i=1
  for l in "${LINES[@]}"; do
    IFS=$'\t' read -r cl mm ln lv <<< "$l"
    printf "   %2d) %-18s line %-4s locals: %s\n" "$i" "$cl.$mm" "$ln" "$lv"
    i=$((i+1))
  done
  echo "    ${B} c${R}) ANY line — type a line number yourself"
  printf '\n%s' "  ${B}${C}which line? [1-$((i-1)), or c]: ${R}"; read -r pick

  if [ "$pick" = "c" ] || [ "$pick" = "C" ]; then
    # ARBITRARY LINE. The curated list above exists so a demo cannot accidentally pick a refused location, but
    # the real claim is that a probe goes anywhere a statement is. This path takes any line number, resolves the
    # enclosing method itself, shows the source text so the audience sees what is being probed, and then lets
    # the product answer — capture, or a typed refusal with a reason.
    printf '%s' "  ${B}${C}line number in Program.cs: ${R}"; read -r ln
    [[ "$ln" =~ ^[0-9]+$ ]] || { echo "  ${Y}not a line number${R}"; return; }
    IFS=$'\t' read -r cl mm src_text <<< "$(enclosing "$ln")"
    if [ -z "${mm:-}" ]; then
      echo "  ${Y}line $ln is not inside a method${R} — a probe has to sit in a method body."
      return
    fi
    echo
    echo "  line ${B}$ln${R} is in ${B}$cl.$mm${R}:"
    echo "      ${D}$ln:${R}  ${B}$src_text${R}"
    printf '\n%s' "  ${B}${C}local(s) to capture (comma separated): ${R}"; read -r want
    [ -n "$want" ] || { echo "  ${Y}no local named${R}"; return; }
    lv="$want"
  else
    [[ "$pick" =~ ^[0-9]+$ ]] || { echo "  ${Y}cancelled${R}"; return; }
    IFS=$'\t' read -r cl mm ln lv <<< "${LINES[$((pick-1))]}"
    [ -n "${mm:-}" ] || { echo "  ${Y}no such choice${R}"; return; }
  fi

  # Capture ONE local per config by default. InstrumentationKey is "{Type}.{Method}:{Line}" and does not
  # include the local name, so two configs naming different locals at the SAME line collapse into one
  # registry entry — the second would silently do nothing.
  if [ "${want:-}" = "" ]; then
    printf '%s' "  ${B}${C}locals to capture [default: $lv]: ${R}"; read -r want
    [ -n "$want" ] || want="$lv"
  fi
  local json_locals
  json_locals=$(printf '%s' "$want" | python3 -c 'import json,sys; print(json.dumps([s.strip() for s in sys.stdin.read().split(",") if s.strip()]))')

  echo
  echo "  creating a BREAKPOINT on ${B}$cl.$mm${R} line ${B}$ln${R}, capturing ${B}$want${R} …"
  post_create '{
    "InstrumentationType":"BREAKPOINT","Service":"sample-app","Environment":"staging",
    "SignalType":"SNAPSHOT","AccountId":"000000000000",
    "Location":{"CodeLocation":{"Language":"Dotnet","CodeUnit":"'"$CODE_UNIT"'",
      "ClassName":"'"$cl"'","MethodName":"'"$mm"'","FilePath":"Program.cs","LineNumber":'"$ln"'}},
    "CaptureConfiguration":{"CodeCapture":{"CaptureLocals":'"$json_locals"',"CaptureLimits":{"MaxHits":100}}}}' || return
  watch_hits "breakpoint-$(lower "$cl")-$(lower "$mm")"
}

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

# Waits for the FIRST capture rather than declaring success on the create call. The create only records
# intent: the agent still has to poll (5s) and the profiler still has to ReJIT the method the next time it
# runs. Reporting "done" at create time would be the demo claiming something it had not observed.
watch_hits() { # watch_hits HASH
  local hash="$1" before after
  before=$(hits_for "$hash")
  echo
  echo -n "  waiting for the first capture (agent polls every 5s) "
  for _ in $(seq 1 40); do
    after=$(hits_for "$hash")
    if [ "$after" -gt "$before" ]; then
      echo
      echo "  ${G}${B}✔ CAPTURING${R} — live values off the running method:"
      echo
      grep "locationHash=$hash" -A1 "$MOCK_OUT" 2>/dev/null | grep 'SNAPSHOT-BODY' | tail -3 \
        | sed -E 's/^\[SNAPSHOT-BODY\] /    /' | cut -c1-190
      echo
      return
    fi
    echo -n "."; sleep 1
  done
  echo
  # WHY, not just "no". A refusal is a first-class product answer — the agent reports a typed status with a
  # cause — so surfacing it turns "the demo failed" into "the product told me the location will not work".
  local reported
  reported=$(grep -oE '"LocationHash":"'"$hash"'","Status":"[A-Z_]+"' "$MOCK_OUT" 2>/dev/null | tail -1 | grep -oE '"Status":"[A-Z_]+"' | cut -d'"' -f4)
  if [ -n "${reported:-}" ] && [ "$reported" != "READY" ] && [ "$reported" != "ACTIVE" ]; then
    echo "  ${Y}${B}the agent reported: $reported${R}"
  else
    echo "  ${Y}no capture within 40s${R} (agent status: ${reported:-not yet reported})"
  fi
  echo "  ${D}For a line-level probe the usual causes are all documented refusals, not bugs:"
  echo "    - the line is not an executable statement (a comment, a brace, a declaration)"
  echo "    - it is the LAST statement of the method — the probe reads at the next statement"
  echo "    - its next statement is a branch target (in Release, typically the last line of an if/loop body)"
  echo "    - the named local is not in scope at that line"
  echo "  Agent log: logs/*Managed*.log  |  profiler log: logs/*Native.log${R}"
}

delete_config() {
  banner "DELETE A CONFIGURATION  (capture stops; the app never restarts)"
  local rows; rows="$(all_configs)"
  if [ -z "$rows" ]; then echo "  ${D}nothing to delete${R}"; return; fi
  local i=1; local -a HS=() TS=()
  while IFS=$'\t' read -r t h tgt ln; do
    [ -n "$h" ] || continue
    printf "   %2d) %-11s %-28s line %-5s (%s hits)\n" "$i" "$t" "$tgt" "$ln" "$(hits_for "$h")"
    HS+=("$h"); TS+=("$t"); i=$((i+1))
  done <<< "$rows"
  printf '\n%s' "  ${B}${C}which one? [1-$((i-1))]: ${R}"; read -r pick
  [[ "$pick" =~ ^[0-9]+$ ]] || { echo "  ${Y}cancelled${R}"; return; }
  local h="${HS[$((pick-1))]:-}" t="${TS[$((pick-1))]:-}"
  [ -n "$h" ] || { echo "  ${Y}no such choice${R}"; return; }

  local at_delete; at_delete=$(hits_for "$h")
  curl -s -X POST "$API/delete-instrumentation-configuration" -H 'Content-Type: application/json' -d '{
    "InstrumentationType":"'"$t"'","Service":"sample-app","Environment":"staging",
    "SignalType":"SNAPSHOT","AccountId":"000000000000",
    "LocationIdentifier":{"LocationHash":"'"$h"'"}}' >/dev/null

  echo
  echo "  deleted. hits at that moment: ${B}$at_delete${R}"

  # TWO PHASES, BECAUSE DELETION IS NOT INSTANT. The backend forgets the configuration immediately, but the
  # agent only learns on its NEXT POLL — up to one poll interval later — and keeps capturing until then.
  # Measured: 9 hits at deletion, 11 a few seconds later, then frozen at 11 across the next 30s. So a check
  # that demands the count stop the instant the delete returns reports a FAILURE for correct behaviour, and a
  # fixed "+1" tolerance is just a guess at how many calls fit in that window.
  #
  # Settle first, then require the count to be genuinely UNCHANGED. That is the claim worth making.
  echo -n "  letting the agent notice (2 poll intervals) "
  for _ in $(seq 1 12); do echo -n "."; sleep 1; done
  echo
  local settled; settled=$(hits_for "$h")
  echo "  after the next poll: ${B}$settled${R}  ${D}(the difference is calls made before the agent noticed)${R}"

  echo -n "  now watching 15s of continued traffic "
  for _ in $(seq 1 15); do echo -n "."; sleep 1; done
  echo
  local after; after=$(hits_for "$h")
  if ! app_running; then
    echo "  ${Y}⚠ inconclusive — the app exited during the window, so a frozen counter proves nothing${R}"
  elif [ "$after" -eq "$settled" ]; then
    echo "  ${G}${B}✔ STOPPED${R} — frozen at ${B}$after${R} through 15s of traffic, app ${B}still running${R}."
  else
    echo "  ${RD}✘ still capturing${R} — $settled → $after after the agent should have dropped it"
  fi
}

live_hits_view() {
  banner "LIVE HIT COUNTERS  (Ctrl-C to return)"
  trap 'trap - INT; return 0' INT
  while :; do
    local rows; rows="$(all_configs)"
    printf '\r'
    if [ -z "$rows" ]; then
      printf "  no configurations                    "
    else
      local out=""
      while IFS=$'\t' read -r t h tgt ln; do
        [ -n "$h" ] || continue
        out+="$tgt=$(hits_for "$h")  "
      done <<< "$rows"
      printf "  %s" "$out"
    fi
    sleep 1
  done
}

# ── Menu ────────────────────────────────────────────────────────────────────
banner "DI OPERATOR CONSOLE — you are the customer"
echo "  The application in the other terminal is already running. Nothing you do here"
echo "  restarts it, redeploys it, or changes its code. Add and remove probes at will."

while :; do
  show_state
  echo "  ${B}1${R}) add a function-level probe      ${B}3${R}) delete a configuration"
  echo "  ${B}2${R}) add a line-level breakpoint     ${B}4${R}) live hit counters"
  echo "  ${B}5${R}) refresh                         ${B}q${R}) quit"
  printf '\n%s' "  ${B}${C}> ${R}"
  read -r choice || break
  case "${choice:-}" in
    1) add_probe ;;
    2) add_breakpoint ;;
    3) delete_config ;;
    4) live_hits_view ;;
    5|"") ;;
    q|Q) echo "  bye"; exit 0 ;;
    *) echo "  ${Y}?${R}" ;;
  esac
done
