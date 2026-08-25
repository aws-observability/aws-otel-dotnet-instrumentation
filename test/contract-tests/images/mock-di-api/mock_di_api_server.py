# Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
# SPDX-License-Identifier: Apache-2.0
"""A mock Dynamic Instrumentation configuration API for contract tests.

The DI agent polls a LOCAL configuration API (in production, the CloudWatch Agent) for the probes an operator
created, and reports back the status of each one. Contract tests therefore need something on the other end of
that conversation: without it the agent polls, gets nothing, and instruments nothing, so a "no snapshots
arrived" failure would be indistinguishable from a broken capture path.

Deliberately stdlib-only. The image needs no `pip install` step, which keeps it far cheaper to build in CI
than the collector image, and there is nothing here that a dependency would make simpler.

Surfaces two kinds of endpoint:
  * the REAL agent-facing API (`/list-instrumentation-configurations`,
    `/report-instrumentation-configuration-status`) — shape must match the production contract exactly;
  * a `/_test/*` control API that the test uses to seed configurations and to read back what the agent
    reported. Namespaced so it can never be confused with the real contract.
"""

import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, List, Optional, Tuple

_LISTEN_PORT = int(os.environ.get("DI_API_PORT", "2000"))


class _State:
    """Configurations to serve and statuses received, guarded for concurrent polls.

    A lock is genuinely required: the agent polls PROBE and BREAKPOINT from two SEPARATE threads, so two
    requests land concurrently and both touch `status_reports`.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._configurations: List[Dict[str, Any]] = []
        self._status_reports: List[Dict[str, Any]] = []
        # Bumped whenever configurations change. Doubles as the SyncedAt cursor handed to the agent, which
        # lets the mock answer "nothing changed since you last asked" the way the real API does instead of
        # re-sending everything forever.
        self._generation: int = 1
        # How many times the agent has polled, per instrumentation type.
        #
        # EXISTS FOR THE ABSENCE TESTS. "No snapshots arrived" is a weak assertion on its own: it is equally
        # true of a disabled agent and of an agent that was simply too slow to poll before the test looked.
        # Counting polls turns that into a DIRECT claim -- a disabled agent must never poll at all -- and the
        # enabled tests assert the counter moves, so a zero can never be a broken counter.
        self._poll_counts: Dict[str, int] = {}

    def set_configurations(self, configurations: List[Dict[str, Any]]) -> int:
        with self._lock:
            self._configurations = configurations
            self._generation += 1
            return self._generation

    def record_poll(self, instrumentation_type: str) -> None:
        with self._lock:
            key = instrumentation_type.upper() or "UNKNOWN"
            self._poll_counts[key] = self._poll_counts.get(key, 0) + 1

    def poll_counts(self) -> Dict[str, int]:
        with self._lock:
            return dict(self._poll_counts)

    def configurations_for(self, instrumentation_type: str) -> Tuple[List[Dict[str, Any]], int]:
        with self._lock:
            matching = [
                config
                for config in self._configurations
                if str(config.get("InstrumentationType", "")).upper() == instrumentation_type.upper()
            ]
            return matching, self._generation

    def add_status_reports(self, service: str, environment: str, entries: List[Dict[str, Any]]) -> None:
        with self._lock:
            for entry in entries:
                recorded = dict(entry)
                recorded["Service"] = service
                recorded["Environment"] = environment
                self._status_reports.append(recorded)

    def status_reports(self) -> List[Dict[str, Any]]:
        with self._lock:
            return list(self._status_reports)

    def reset(self) -> None:
        with self._lock:
            self._configurations = []
            self._status_reports = []
            self._generation += 1
            # Cleared with the rest. A stale non-zero count would make the disabled test's "never polled"
            # assertion fail for a reason that has nothing to do with the agent under test.
            self._poll_counts = {}


_STATE = _State()


def _as_cursor(synced_at: Any) -> int:
    """Coerces the agent's SyncedAt cursor to an int, treating anything unusable as "never synced".

    `int(synced_at)` raised ValueError on a non-numeric cursor, and because this runs inside do_POST the
    request died in BaseHTTPRequestHandler's default handling -- a 500 with no body, which the agent sees as a
    broken poll. The mock then looks like the thing under test. -1 is below every real generation, so an
    unparseable cursor means Changed=true, which is the safe direction: the agent re-reads the configurations
    rather than trusting a cache it may not have.
    """
    try:
        return int(synced_at)
    except (TypeError, ValueError):
        return -1


class MockDiApiHandler(BaseHTTPRequestHandler):
    """Serves the agent-facing configuration API plus a /_test control API."""

    protocol_version = "HTTP/1.1"

    # pylint: disable=invalid-name
    def do_POST(self) -> None:
        parsed = self._read_json_or_none()

        # MALFORMED BODIES ARE TREATED DIFFERENTLY BY AUDIENCE, on purpose.
        #
        # The `/_test/*` control API is driven by the TEST, so bad JSON there is a bug in the test and must say
        # so. Swallowing it to `{}` seeded zero configurations and surfaced later as "why was my probe never
        # applied?" -- a confusing failure a long way from its cause.
        #
        # The agent-facing endpoints stay lenient (`{}`): they are a stand-in for a production API, and a
        # 400 there would turn a malformed poll into an agent-side error, which is not what this mock is for.
        if parsed is None and self.path.startswith("/_test/"):
            self._send_json({"Message": "request body was not valid JSON"}, status=400)
            return

        body: Dict[str, Any] = parsed if parsed is not None else {}
        if self.path.startswith("/list-instrumentation-configurations"):
            self._handle_list(body)
        elif self.path.startswith("/report-instrumentation-configuration-status"):
            self._handle_report(body)
        elif self.path.startswith("/_test/configurations"):
            configurations = body.get("Configurations", []) if isinstance(body, dict) else []
            generation = _STATE.set_configurations(configurations)
            self._send_json({"Accepted": len(configurations), "Generation": generation})
        elif self.path.startswith("/_test/reset"):
            _STATE.reset()
            self._send_json({"Reset": True})
        else:
            self._send_json({"Message": f"unknown path {self.path}"}, status=404)

    # pylint: disable=invalid-name
    def do_GET(self) -> None:
        if self.path.startswith("/_test/status-reports"):
            self._send_json({"StatusReports": _STATE.status_reports()})
        elif self.path.startswith("/_test/poll-counts"):
            counts = _STATE.poll_counts()
            self._send_json({"PollCounts": counts, "TotalPolls": sum(counts.values())})
        elif self.path.startswith("/_test/health"):
            self._send_json({"Status": "ok"})
        else:
            self._send_json({"Message": f"unknown path {self.path}"}, status=404)

    def _handle_list(self, body: Dict[str, Any]) -> None:
        instrumentation_type = str(body.get("InstrumentationType", "")) if isinstance(body, dict) else ""
        synced_at = body.get("SyncedAt") if isinstance(body, dict) else None

        # Recorded BEFORE anything that could fail, so the count reflects "the agent asked", which is what the
        # absence tests assert on -- not "the mock answered successfully".
        _STATE.record_poll(instrumentation_type)

        configurations, generation = _STATE.configurations_for(instrumentation_type)

        # Mirrors the real API's caching contract: Changed=false means "your cache is still valid", and the
        # agent then keeps what it has. Exercising this path matters — an always-Changed=true mock would hide
        # a client that mishandles the unchanged case.
        changed = synced_at is None or _as_cursor(synced_at) < generation

        self._send_json(
            {
                "Service": body.get("Service", "") if isinstance(body, dict) else "",
                "Environment": body.get("Environment", "") if isinstance(body, dict) else "",
                "Changed": changed,
                # NOTE null, not [], when unchanged — the real API omits the list rather than sending an
                # empty one, and an empty list would read as "every probe was deleted".
                "LatestConfigurations": configurations if changed else None,
                "NextToken": None,
                "SyncedAt": generation,
                "SyncInterval": 60,
            }
        )

    def _handle_report(self, body: Dict[str, Any]) -> None:
        service = body.get("Service", "") if isinstance(body, dict) else ""
        environment = body.get("Environment", "") if isinstance(body, dict) else ""
        entries = body.get("Configurations", []) if isinstance(body, dict) else []
        _STATE.add_status_reports(service, environment, entries)
        self._send_json({"Service": service, "Environment": environment, "UnprocessedStatusEvents": []})

    def _read_json_or_none(self) -> Optional[Dict[str, Any]]:
        """Parses the body, returning None when it is present but NOT a valid JSON object.

        None and `{}` are deliberately different: an EMPTY body is a legitimate request (`{}`), while an
        unparseable one is a caller error the `/_test/*` endpoints report as a 400. Collapsing both to `{}`
        is what made a malformed test payload look like a silently ignored one.
        """
        raw = self._read_body()
        if not raw:
            return {}
        try:
            parsed = json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return None
        return parsed if isinstance(parsed, dict) else None

    def _read_body(self) -> bytes:
        """Reads the request body, handling BOTH Content-Length and chunked transfer encoding.

        CHUNKED IS THE CASE THAT ACTUALLY MATTERS HERE, and it is not hypothetical. The DI client builds its
        request as `HttpRequestMessage { Content = JsonContent.Create(...) }` and calls SendAsync, which does
        not buffer to compute a length — so .NET sends `Transfer-Encoding: chunked` with NO Content-Length.
        BaseHTTPRequestHandler does not decode chunked bodies, so a Content-Length-only reader sees an EMPTY
        body and every field reads as absent.

        MEASURED failure mode when that happened: the mock answered `Changed: true` with ZERO configurations
        (the InstrumentationType filter matched nothing against an empty request), the agent instrumented
        nothing, and the symptom was an unexplained "no snapshots" — with no error anywhere. A curl-based
        check passes right through this, because curl sends Content-Length.
        """
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            chunks: List[bytes] = []
            while True:
                size_line = self.rfile.readline().strip()
                if not size_line:
                    break
                try:
                    # A chunk header may carry `;ext=...` extensions after the size.
                    size = int(size_line.split(b";")[0], 16)
                except ValueError:
                    break
                if size == 0:
                    # Terminal chunk: consume optional trailers up to the blank line so the connection stays
                    # usable for the next keep-alive request.
                    while True:
                        trailer = self.rfile.readline()
                        if trailer in (b"\r\n", b"\n", b""):
                            break
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)  # the CRLF that terminates each chunk
            return b"".join(chunks)

        length = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(length) if length > 0 else b""

    def _send_json(self, payload: Dict[str, Any], status: int = 200) -> None:
        encoded = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        # Explicit length so the agent's HttpClient can reuse the connection. Without it HTTP/1.1 keep-alive
        # stalls waiting for a close that never comes.
        self.send_header("Content-Length", str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def log_message(self, format: str, *args: Any) -> None:  # noqa: A002  pylint: disable=redefined-builtin
        # Every poll would otherwise print a line; at a 1s test poll interval that buries the useful output.
        return


def _seed_from_environment() -> None:
    """Seeds configurations from DI_CONFIGS so a test can be driven purely by container env vars."""
    raw = os.environ.get("DI_CONFIGS")
    if not raw:
        return
    try:
        parsed = json.loads(raw)
    except ValueError:
        print(f"DI_CONFIGS is not valid JSON, ignoring: {raw[:200]}", flush=True)
        return
    configurations = parsed if isinstance(parsed, list) else parsed.get("Configurations", [])
    _STATE.set_configurations(configurations)
    print(f"Seeded {len(configurations)} configuration(s) from DI_CONFIGS", flush=True)


def main() -> None:
    _seed_from_environment()
    server = ThreadingHTTPServer(("0.0.0.0", _LISTEN_PORT), MockDiApiHandler)
    # "Ready" is the token the contract-test harness waits for (wait_for_logs), matching the collector image.
    print("Ready", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
