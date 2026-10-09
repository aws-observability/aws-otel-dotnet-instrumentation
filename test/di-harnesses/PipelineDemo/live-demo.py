#!/usr/bin/env python3
"""
LIVE DI backend demo — every request and response printed as full JSON.
Runs real SigV4-signed calls against application-signals.us-east-1.api.aws.

  1. CREATE  a Java config          -> 200, full config echoed
  2. LIST    configs                -> 200, full response (numeric SyncedAt/CreatedAt)
  3. REPORT  status                 -> 200, UnprocessedStatusEvents: []
  4. CREATE  a Dotnet config        -> 400, enum rejection  (THE GA BLOCKER)
  5. DELETE  cleanup                -> 200

Requires AWS creds exported. Usage: python3 live-demo.py
"""
import copy, json, os, socket, sys, time
import botocore.session
from botocore.awsrequest import AWSRequest
from botocore.auth import SigV4Auth
import requests

REGION, SVC = "us-east-1", "application-signals"
HOST = f"application-signals.{REGION}.api.aws"
ACCT = "978751493859"
SERVICE, ENV = "di-live-demo", "staging"

# --trace (or DEMO_TRACE=1): print the proof this hit real AWS — resolved IP, the SigV4
# Authorization header, and the AWS-generated x-amzn-RequestId from the response.
TRACE = ("--trace" in sys.argv) or (os.environ.get("DEMO_TRACE") == "1")

C = {"cyan": "\033[96m", "green": "\033[92m", "red": "\033[91m",
     "yellow": "\033[93m", "bold": "\033[1m", "dim": "\033[2m", "off": "\033[0m"}

sess = botocore.session.get_session()
creds = sess.get_credentials()
if creds is None:
    print("ERROR: no AWS creds. Export AWS_ACCESS_KEY_ID/SECRET/SESSION_TOKEN first.")
    sys.exit(1)
creds = creds.get_frozen_credentials()


def step(msg):
    """Pause between demo steps. Press Enter to fire the call; set DEMO_NOPAUSE=1 to disable."""
    import os
    if os.environ.get("DEMO_NOPAUSE") == "1":
        return
    try:
        input(f"\n{C['dim']}   ⏎  {msg}{C['off']}")
    except (EOFError, KeyboardInterrupt):
        print()


def call(op, path, body):
    # Show the step header + request FIRST, pause so it can be narrated, THEN fire the live call.
    print(f"\n{C['bold']}{C['cyan']}━━━ {op}{C['off']}")
    print(f"{C['dim']}POST https://{HOST}{path}{C['off']}")
    print(f"{C['bold']}REQUEST:{C['off']}")
    print(json.dumps(body, indent=2))

    step("press Enter to send this request to the LIVE backend…")

    url = f"https://{HOST}{path}"
    data = json.dumps(body)
    req = AWSRequest(method="POST", url=url, data=data, headers={"Content-Type": "application/json"})
    SigV4Auth(creds, SVC, REGION).add_auth(req)
    signed = dict(req.headers)

    if TRACE:
        try:
            ip = socket.gethostbyname(HOST)
        except Exception as e:
            ip = f"(resolve failed: {e})"
        print(f"{C['dim']}   DNS  {HOST} → {ip}{C['off']}")
        print(f"{C['dim']}   SigV4 Authorization: {signed.get('Authorization', '')[:110]}…{C['off']}")
        print(f"{C['dim']}   X-Amz-Date: {signed.get('X-Amz-Date', '')}   "
              f"(session token: {'yes' if 'X-Amz-Security-Token' in signed else 'no'}){C['off']}")

    r = requests.post(url, data=data, headers=signed, timeout=25)
    ok = r.status_code == 200
    color = C["green"] if ok else (C["yellow"] if r.status_code == 400 else C["red"])

    print(f"{C['bold']}RESPONSE:{C['off']} {color}HTTP {r.status_code}{C['off']}")
    if TRACE:
        rid = r.headers.get("x-amzn-RequestId") or r.headers.get("x-amzn-requestid") or "(none)"
        print(f"{C['dim']}   ← served by AWS: x-amzn-RequestId = {C['off']}{C['bold']}{rid}{C['off']}")
        print(f"{C['dim']}     Date: {r.headers.get('Date','')}   "
              f"x-amz-apigw-id: {r.headers.get('x-amz-apigw-id','(n/a)')}{C['off']}")
    try:
        print(json.dumps(json.loads(r.text), indent=2))
    except Exception:
        print(r.text)
    return r.status_code, r.text


def main():
    print(f"{C['bold']}╔══════════════════════════════════════════════════════════════════╗{C['off']}")
    print(f"{C['bold']}║   .NET Dynamic Instrumentation — LIVE backend contract demo       ║{C['off']}")
    print(f"{C['bold']}║   account 978751493859 · application-signals.us-east-1.api.aws    ║{C['off']}")
    print(f"{C['bold']}╚══════════════════════════════════════════════════════════════════╝{C['off']}")

    # ONE base config, reused verbatim for step 1 and step 4 — the only difference between the two
    # is the Language field, so the 400 at step 4 can ONLY be about the enum, nothing else.
    base_config = {
        "AccountId": ACCT, "Service": SERVICE, "Environment": ENV,
        "InstrumentationType": "PROBE", "SignalType": "SNAPSHOT",
        "Location": {"CodeLocation": {"Language": "Java", "CodeUnit": "com.example",
                     "ClassName": "OrderService", "MethodName": "processOrder",
                     "FilePath": "OrderService.java"}},
        "CaptureConfiguration": {"CodeCapture": {"CaptureArguments": ["orderId"],
                     "CaptureReturn": True, "CaptureLimits": {"MaxHits": 10}}},
    }

    # 1. CREATE (Java — accepted today)
    sc, txt = call("1. CREATE instrumentation config (Language=Java)",
                   "/create-instrumentation-configuration", base_config)
    if sc != 200:
        print(f"\n{C['red']}CREATE failed (token expired?). Re-export fresh creds.{C['off']}")
        sys.exit(1)
    lh = json.loads(txt)["LocationHash"]

    # 2. LIST
    call("2. LIST instrumentation configs",
         "/list-instrumentation-configurations",
         {"Service": SERVICE, "Environment": ENV, "InstrumentationType": "PROBE"})

    # 3. REPORT status
    call("3. REPORT instrumentation status",
         "/report-instrumentation-configuration-status", {
             "AccountId": ACCT, "Service": SERVICE, "Environment": ENV,
             "Configurations": [{"InstrumentationType": "PROBE", "SignalType": "SNAPSHOT",
                                 "LocationHash": lh, "Status": "ACTIVE",
                                 "Time": int(time.time())}]})

    # 4. THE BLOCKER: byte-for-byte the SAME request as step 1, with ONLY Language flipped to Dotnet.
    dotnet_config = copy.deepcopy(base_config)
    dotnet_config["Location"]["CodeLocation"]["Language"] = "Dotnet"
    print(f"\n{C['bold']}{C['yellow']}┌─ THE GA BLOCKER — identical to step 1, ONLY Language: Java → Dotnet ─┐{C['off']}")
    call("4. CREATE with Language=Dotnet  (expected: 400 rejection)",
         "/create-instrumentation-configuration", dotnet_config)

    # 5. DELETE cleanup
    call("5. DELETE (cleanup)",
         "/delete-instrumentation-configuration", {
             "AccountId": ACCT, "Service": SERVICE, "Environment": ENV, "SignalType": "SNAPSHOT",
             "InstrumentationType": "PROBE", "LocationIdentifier": {"LocationHash": lh}})

    print(f"\n{C['bold']}{C['green']}✅ The wire contract works live. The ONLY change for .NET is adding")
    print(f"   \"Dotnet\" to the ProgrammingLanguage enum (see the 400 at step 4).{C['off']}\n")


main()
