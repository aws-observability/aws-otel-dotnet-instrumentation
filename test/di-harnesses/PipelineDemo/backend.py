#!/usr/bin/env python3
# Pipeline demo — BACKEND STAGES (real live AWS calls, SigV4-signed).
# Subcommands: create | list | dotnet-reject | report | delete
import json, sys, os
import botocore.session
from botocore.awsrequest import AWSRequest
from botocore.auth import SigV4Auth
import requests

REGION = os.environ.get("DEMO_REGION", "us-east-1")
SVC = "application-signals"
# HOST defaults to the public prod endpoint for REGION, but the Dotnet enablement first lands in a
# BETA stage whose endpoint is usually NOT the public host — override DEMO_HOST with the beta endpoint.
HOST = os.environ.get("DEMO_HOST", f"application-signals.{REGION}.api.aws")
ACCT = os.environ.get("DEMO_ACCOUNT_ID", "978751493859")
SERVICE = "pipeline-demo"
ENV = "staging"
STATE = "/tmp/pipeline-demo"

sess = botocore.session.get_session()
creds = sess.get_credentials().get_frozen_credentials()


def call(path, body):
    url = f"https://{HOST}{path}"
    data = body if isinstance(body, str) else json.dumps(body)
    req = AWSRequest(method="POST", url=url, data=data, headers={"Content-Type": "application/json"})
    SigV4Auth(creds, SVC, REGION).add_auth(req)
    r = requests.post(url, data=data, headers=dict(req.headers), timeout=25)
    return r.status_code, r.text


def create():
    # Create the config with the REAL .NET target coordinates (the type/method the profiler process
    # actually defines: Demo.PaymentService.Charge, capturing the "orderId" arg). Language must be
    # "Java" because the backend still rejects "Dotnet" (Stage 2) — that ONE field is the only thing
    # the .NET side rewrites post-GA. Everything else (class, method, capture config) flows through
    # from the live backend untouched, so the profiler weaves exactly what the backend returned.
    sc, txt = call("/create-instrumentation-configuration", {
        "AccountId": ACCT, "Service": SERVICE, "Environment": ENV,
        "InstrumentationType": "PROBE", "SignalType": "SNAPSHOT",
        "Location": {"CodeLocation": {"Language": "Java", "CodeUnit": "Demo",
                     "ClassName": "PaymentService", "MethodName": "Charge", "FilePath": "PaymentService.cs"}},
        "CaptureConfiguration": {"CodeCapture": {"CaptureArguments": ["orderId"], "CaptureReturn": True,
                     "CaptureLimits": {"MaxHits": 10}}}})
    print(f"   HTTP {sc}")
    if sc != 200:
        print("   " + txt[:300]); sys.exit(1)
    lh = json.loads(txt)["LocationHash"]
    open(f"{STATE}/location-hash.txt", "w").write(lh)
    print(f"   created config, LocationHash = {lh}")


def dotnet_reject():
    sc, txt = call("/create-instrumentation-configuration", {
        "AccountId": ACCT, "Service": SERVICE, "Environment": ENV,
        "InstrumentationType": "PROBE", "SignalType": "SNAPSHOT",
        "Location": {"CodeLocation": {"Language": "Dotnet", "CodeUnit": "Demo",
                     "ClassName": "PaymentService", "MethodName": "Charge", "FilePath": "PaymentService.cs"}},
        "CaptureConfiguration": {"CodeCapture": {"CaptureReturn": True, "CaptureLimits": {"MaxHits": 10}}}})
    print(f"   HTTP {sc}")
    print("   " + txt[:300])


def do_list():
    sc, txt = call("/list-instrumentation-configurations",
                   {"Service": SERVICE, "Environment": ENV, "InstrumentationType": "PROBE"})
    print(f"   HTTP {sc}")
    open(f"{STATE}/list-response.json", "w").write(txt)
    obj = json.loads(txt)
    print(f"   Changed={obj.get('Changed')}  SyncInterval={obj.get('SyncInterval')}  "
          f"SyncedAt={obj.get('SyncedAt')} (JSON number)")
    print(f"   LatestConfigurations: {len(obj.get('LatestConfigurations') or [])} config(s)")
    print("   --- verbatim response bytes saved for the .NET client to parse ---")


def report():
    body = open(f"{STATE}/status-body.json").read()
    lh = open(f"{STATE}/location-hash.txt").read().strip()
    obj = json.loads(body)
    obj["AccountId"] = ACCT  # proxy injects in prod
    for c in obj.get("Configurations", []):
        c["LocationHash"] = lh
    sc, txt = call("/report-instrumentation-configuration-status", json.dumps(obj))
    print(f"   HTTP {sc}")
    print(f"   response: {txt}")
    unproc = json.loads(txt).get("UnprocessedStatusEvents", None)
    print(f"   UnprocessedStatusEvents = {unproc}  (empty = backend accepted our status)")


def delete():
    try:
        lh = open(f"{STATE}/location-hash.txt").read().strip()
    except FileNotFoundError:
        print("   (nothing to delete)"); return
    sc, txt = call("/delete-instrumentation-configuration", {
        "AccountId": ACCT, "Service": SERVICE, "Environment": ENV, "SignalType": "SNAPSHOT",
        "InstrumentationType": "PROBE", "LocationIdentifier": {"LocationHash": lh}})
    print(f"   HTTP {sc}  {txt[:120]}")


{"create": create, "list": do_list, "dotnet-reject": dotnet_reject,
 "report": report, "delete": delete}[sys.argv[1]]()
