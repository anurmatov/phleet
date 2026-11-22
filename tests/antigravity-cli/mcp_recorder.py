"""A minimal MCP server (Streamable HTTP, JSON answers) that records what each request carried.

    mcp_recorder.py PORT LOG

Like the fleet's own servers it answers 401 with `WWW-Authenticate: Bearer` to every request that
does not carry exactly MCP_EXPECTED_AUTHORIZATION, the OAuth discovery document included. Its one
tool, get_message, returns a fixed synthetic sentence.
"""
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = sys.argv[2]
EXPECTED = os.environ["MCP_EXPECTED_AUTHORIZATION"]
MESSAGE = "stored message 7 says: the heron lands at dusk"
TOOL = {"name": "get_message", "description": "Returns the stored message with the given id.",
        "inputSchema": {"type": "object", "properties": {"id": {"type": "integer"}}, "required": ["id"]}}


def record(entry):
    with open(LOG, "a") as log:
        log.write(json.dumps(entry) + "\n")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def auth(self):
        value = self.headers.get("Authorization")
        return "ok" if value == EXPECTED else ("missing" if value is None else "other")

    def answer(self, status, payload=None, headers=()):
        body = b"" if payload is None else json.dumps(payload).encode()
        self.send_response(status)
        for name, value in headers:
            self.send_header(name, value)
        if payload is not None:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def refuse(self):
        self.answer(401, {"error": "agent_identity_required"}, [("WWW-Authenticate", "Bearer")])

    def do_GET(self):
        auth = self.auth()
        record({"http": "GET", "path": self.path, "auth": auth, "status": 405 if auth == "ok" else 401})
        if auth != "ok":
            self.refuse()
        else:
            self.answer(405)

    def do_DELETE(self):
        auth = self.auth()
        record({"http": "DELETE", "path": self.path, "auth": auth})
        if auth != "ok":
            self.refuse()
        else:
            self.answer(200)

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers.get("Content-Length", "0"))) or b"{}")
        method = body.get("method")
        auth = self.auth()
        entry = {"http": "POST", "path": self.path, "method": method, "auth": auth}
        if method == "tools/call":
            entry["tool"] = body["params"].get("name")
            entry["args"] = body["params"].get("arguments")
        record(entry)
        if auth != "ok":
            self.refuse()
            return
        if "id" not in body:
            self.answer(202)
            return
        if method == "initialize":
            result = {"protocolVersion": body["params"].get("protocolVersion", "2025-03-26"),
                      "capabilities": {"tools": {}}, "serverInfo": {"name": "recorder", "version": "1"}}
        elif method == "tools/list":
            result = {"tools": [TOOL]}
        elif method == "tools/call":
            result = {"content": [{"type": "text", "text": MESSAGE}]}
        else:
            result = {}
        self.answer(200, {"jsonrpc": "2.0", "id": body["id"], "result": result})


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
