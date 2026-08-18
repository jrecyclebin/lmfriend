#!/usr/bin/env python3
"""Drives the lmfriend proxy binary against tests/mock_server.py.

Covers acceptance items 5, 6 and 7 from design.md:
  5. proxy with a valid token passes initialize and tools/list through end to end
  6. stdout carries nothing but JSON-RPC frames
  7. kill the server mid-session, bring it back: proxy survives, replays the
     handshake and the next tools/list succeeds without the client resending
     anything
Also: requests during the outage get a synthesized error within ~10s,
and EOF on stdin is a clean exit 0.
"""
import hashlib
import json
import os
import queue
import subprocess
import sys
import threading
import time
import urllib.request

ROOT = os.path.dirname(os.path.abspath(__file__))
BINARY = os.environ.get("LMFRIEND", os.path.join(
    ROOT, "..", "bin", "Release", "net10.0", "linux-x64", "publish", "lmfriend"))
URL = "http://127.0.0.1:8791/mcp"
PORT = 8791
LOG = os.path.join(ROOT, "mock_log.jsonl")

failures = []


def check(name, ok, detail=""):
    print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail and not ok else ""))
    if not ok:
        failures.append(name)


def seed_tokens():
    digest = hashlib.sha256(URL.encode()).hexdigest()
    store = os.path.expanduser("~/.config/lmfriend")
    os.makedirs(store, exist_ok=True)
    with open(os.path.join(store, digest + ".json"), "w") as f:
        json.dump({
            "tokenType": "Bearer", "accessToken": "test-token", "refreshToken": "",
            "expiresIn": 86400, "scope": None,
            "obtainedAt": "2026-08-18T00:00:00+00:00",
            "clientId": "lmfriend-test", "clientSecret": None,
            "tokenEndpointAuthMethod": "none",
            "authorizationServer": "http://127.0.0.1:8791",
        }, f)


def start_server():
    proc = subprocess.Popen(
        [sys.executable, os.path.join(ROOT, "mock_server.py"), "--port", str(PORT), "--log", LOG],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    for _ in range(50):
        try:
            urllib.request.urlopen(f"http://127.0.0.1:{PORT}/mcp", timeout=0.2)
        except urllib.error.HTTPError:
            return proc  # server is up and answering (405), good enough
        except Exception:
            time.sleep(0.1)
    raise RuntimeError("mock server didn't come up")


class Proxy:
    def __init__(self):
        self.proc = subprocess.Popen([BINARY, "proxy", URL],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.PIPE, text=True, bufsize=1)
        self.stdout_lines = queue.Queue()
        self.stderr = []
        self.stale = []
        threading.Thread(target=self._pump_stdout, daemon=True).start()
        threading.Thread(target=self._pump_stderr, daemon=True).start()

    def _pump_stdout(self):
        for line in self.proc.stdout:
            self.stdout_lines.put(line.rstrip("\n"))

    def _pump_stderr(self):
        for line in self.proc.stderr:
            self.stderr.append(line.rstrip("\n"))

    def send(self, msg):
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()

    def read_response(self, want_id, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                line = self.stdout_lines.get(timeout=deadline - time.time())
            except queue.Empty:
                return None
            try:
                msg = json.loads(line)
                if msg.get("jsonrpc") != "2.0":
                    check("stdout purity", False, f"not JSON-RPC: {line[:120]}")
                    continue
            except json.JSONDecodeError:
                check("stdout purity", False, f"not JSON: {line[:120]}")
                continue
            if msg.get("id") == want_id:
                return msg
            self.stale.append(msg)
        return None


INIT = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"protocolVersion": "2025-06-18", "capabilities": {},
                   "clientInfo": {"name": "harness", "version": "1.0"}}}


def tools(i):
    return {"jsonrpc": "2.0", "id": i, "method": "tools/list", "params": {}}


def main():
    if os.path.exists(LOG):
        os.remove(LOG)
    seed_tokens()
    server = start_server()
    proxy = Proxy()

    # initialize + tools/list round trip through the bridge
    proxy.send(INIT)
    resp = proxy.read_response(1, timeout=30)
    check("initialize round-trips", resp is not None and "result" in resp, str(resp))
    proxy.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    proxy.send(tools(2))
    resp = proxy.read_response(2, timeout=30)
    check("tools/list round-trips",
          resp is not None and bool(resp.get("result", {}).get("tools")), str(resp))
    check("stdout purity", not any(f.startswith("stdout purity") for f in failures))

    # kill the server: pending request gets a synthesized error, proxy keeps living
    server.kill()
    server.wait()
    time.sleep(2)
    proxy.send(tools(3))
    resp = proxy.read_response(3, timeout=14)
    check("orphaned request gets an error within ~10s, not a hang",
          resp is not None and "error" in resp
          and ("no connection" in resp["error"]["message"] or "lost the connection" in resp["error"]["message"]),
          str(resp))
    check("proxy survives the outage", proxy.proc.poll() is None)

    # server reboots as a stranger; the replay must re-initialize it for us
    server = start_server()
    time.sleep(3)  # outlast at least one backoff step
    proxy.send(tools(4))
    resp = proxy.read_response(4, timeout=30)
    check("post-reconnect tools/list succeeds with no client resend",
          resp is not None and bool(resp.get("result", {}).get("tools")), str(resp))
    check("stderr shows the reconnect", any("connect" in l for l in proxy.stderr))

    # the mock must have seen two initializes: ours (id 1) and the replay (lmfriend-init-N)
    events = [json.loads(l) for l in open(LOG)]
    init_ids = [e["msg"].get("id") for e in events if e["msg"].get("method") == "initialize"]
    check("server saw the handshake replay",
          1 in init_ids and any(str(i).startswith("lmfriend-init-") for i in init_ids),
          f"initialize ids seen: {init_ids}")
    replay = [e for e in events if str(e["msg"].get("id", "")).startswith("lmfriend-init-")]
    check("replayed response was never forwarded to the client",
          not any(str(m.get("id", "")).startswith("lmfriend-init-") for m in proxy.stale),
          f"stale frames on stdout: {proxy.stale}")

    # EOF on stdin is the one clean exit
    proxy.proc.stdin.close()
    try:
        rc = proxy.proc.wait(timeout=10)
        check("EOF on stdin exits 0", rc == 0, f"exit code {rc}")
    except subprocess.TimeoutExpired:
        check("EOF on stdin exits 0", False, "didn't exit within 10s")
        proxy.proc.kill()

    server.kill()
    print()
    print("FAILURES:", failures if failures else "none")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
