#!/usr/bin/env python3
"""Synthetic HTTPS MCP server for tests/secret-proxy/mcp-headers.sh (#593).

Answers streamable-HTTP MCP at POST /mcp for one host (mcp.example.test): initialize, the
initialized notification, tools/list and ping. A request is answered only when both mapped headers
carry the two expected synthetic secrets; anything else is a 401. Every request is logged as one JSON
line of booleans and the JSON-RPC method, never a header value, so the harness can count what
arrived and prove no placeholder reached it. Nothing here is a real service.
"""
import json, ssl, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

KEY_SECRET, SECRET_SECRET, LOG = sys.argv[1], sys.argv[2], sys.argv[3]
SESSION = "synthetic-session-1"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def record(self, rpc_method):
        key = self.headers.get("X-Example-Key") or ""
        secret = self.headers.get("X-Example-Secret") or ""
        entry = {
            "host": (self.headers.get("Host") or "").lower(),
            "http_method": self.command,
            "path": self.path,
            "rpc_method": rpc_method,
            "key_ok": key == KEY_SECRET,
            "secret_ok": secret == SECRET_SECRET,
            "placeholder_seen": any("fleet_placeholder_" in (v or "") for v in self.headers.values()),
        }
        with open(LOG, "a") as log:
            log.write(json.dumps(entry) + "\n")
        return entry

    def send(self, status, payload=None, headers=None):
        body = b"" if payload is None else json.dumps(payload).encode()
        self.send_response(status)
        if payload is not None:
            self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        # No server-initiated stream: a client that asks for one is told so and carries on.
        self.record(None)
        self.send(405, {"error": "no stream"})

    def do_DELETE(self):
        self.record(None)
        self.send(200)

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        try:
            message = json.loads(self.rfile.read(length) or b"{}")
        except ValueError:
            message = {}
        entry = self.record(message.get("method"))
        if self.path.split("?")[0] != "/mcp":
            return self.send(404, {"error": "not found"})
        if not (entry["key_ok"] and entry["secret_ok"]):
            return self.send(401, {"error": "synthetic credentials missing"})

        method, rid = message.get("method"), message.get("id")
        if rid is None:
            return self.send(202)
        if method == "initialize":
            version = (message.get("params") or {}).get("protocolVersion") or "2025-06-18"
            result = {"protocolVersion": version, "capabilities": {"tools": {}},
                      "serverInfo": {"name": "synthetic-mcp", "version": "1.0.0"}}
            return self.send(200, {"jsonrpc": "2.0", "id": rid, "result": result}, {"Mcp-Session-Id": SESSION})
        if method == "tools/list":
            tools = [{"name": "ping", "description": "Answers pong.", "inputSchema": {"type": "object", "properties": {}}}]
            return self.send(200, {"jsonrpc": "2.0", "id": rid, "result": {"tools": tools}})
        if method == "ping":
            return self.send(200, {"jsonrpc": "2.0", "id": rid, "result": {}})
        return self.send(200, {"jsonrpc": "2.0", "id": rid, "result": {}})


server = ThreadingHTTPServer(("0.0.0.0", 443), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(sys.argv[4], sys.argv[5])
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
