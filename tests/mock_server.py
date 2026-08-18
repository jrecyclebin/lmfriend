#!/usr/bin/env python3
"""A minimal Streamable-HTTP MCP server for pump tests.

Every POST is appended as a JSON line to the log file (see --log) so the retry
harness can verify the handshake replay. Sessions are tracked: an initialize
gets a fresh mcp-session-id; any other POST without a known session id gets a
400 - just enough state to prove the proxy's handshake replay after a "server
reboot" (process restart).
"""
import argparse
import datetime
import json
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

p = argparse.ArgumentParser()
p.add_argument("--port", type=int, required=True)
p.add_argument("--log", required=True)
cli = p.parse_args()

sessions = {}


def log_event(**kw):
    with open(cli.log, "a") as f:
        f.write(json.dumps({"at": datetime.datetime.now(datetime.UTC).isoformat(), **kw}) + "\n")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def reply(self, code, body=None, sid=None):
        body = json.dumps(body).encode() if body is not None else b""
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        if sid:
            self.send_header("mcp-session-id", sid)
        self.end_headers()
        if body:
            self.wfile.write(body)

    def do_GET(self):
        self.reply(405)

    def do_DELETE(self):
        self.reply(200, {})

    def read_body(self):
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            body = b""
            while True:
                size = int(self.rfile.readline().strip().split(b";")[0], 16)
                if size == 0:
                    while self.rfile.readline().strip():
                        pass  # trailers
                    break
                body += self.rfile.read(size)
                self.rfile.readline()  # trailing CRLF
            return body
        return self.rfile.read(int(self.headers.get("Content-Length", 0)))

    def do_POST(self):
        msg = json.loads(self.read_body() or b"{}")
        sid = self.headers.get("mcp-session-id")
        method = msg.get("method")
        log_event(session=sid, msg=msg)

        if "id" not in msg:  # notification
            self.reply(202, None)
            return

        if method == "initialize":
            new_sid = uuid.uuid4().hex
            sessions[new_sid] = True
            self.reply(200, {"jsonrpc": "2.0", "id": msg["id"],
                             "result": {"protocolVersion": msg["params"]["protocolVersion"],
                                        "capabilities": {"tools": {}},
                                        "serverInfo": {"name": "mock", "version": "0.1"}}},
                       sid=new_sid)
            return

        if sid not in sessions:
            self.reply(400, {"jsonrpc": "2.0", "id": msg["id"],
                             "error": {"code": -32000, "message": "unknown session"}})
            return

        if method == "tools/list":
            self.reply(200, {"jsonrpc": "2.0", "id": msg["id"],
                             "result": {"tools": [{"name": "echo", "inputSchema": {"type": "object"}}]}})
            return

        self.reply(200, {"jsonrpc": "2.0", "id": msg["id"], "result": {}})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", cli.port), Handler).serve_forever()
