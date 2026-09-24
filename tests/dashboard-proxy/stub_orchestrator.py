#!/usr/bin/env python3
"""Stub orchestrator for the dashboard-proxy acceptance harness (#341).

The bug under test lives entirely in the dashboard's nginx proxy, so the
"orchestrator" only needs to identify itself and echo what the proxy sent:
raw path, Host header and X-Real-IP. WebSocket upgrades are completed with a
correct Sec-WebSocket-Accept and then closed, which is enough to prove which
upstream instance the proxy selected.

Stdlib only; listens on :3600.
"""

import base64
import hashlib
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STUB_NAME = os.environ.get("STUB_NAME", "?")
WS_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:
        upgrade = (self.headers.get("Upgrade") or "").strip().lower()
        if upgrade == "websocket":
            self._upgrade()
            return

        payload = json.dumps(
            {
                "instance": STUB_NAME,
                "path": self.path,
                "host": self.headers.get("Host"),
                "xRealIp": self.headers.get("X-Real-IP"),
            },
            separators=(",", ":"),
        ).encode("utf-8")

        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def _upgrade(self) -> None:
        key = self.headers.get("Sec-WebSocket-Key", "")
        accept = base64.b64encode(hashlib.sha1((key + WS_GUID).encode()).digest()).decode("ascii")
        raw = (
            "HTTP/1.1 101 Switching Protocols\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Accept: {accept}\r\n"
            f"X-Stub: {STUB_NAME}\r\n"
            f"X-Stub-Path: {self.path}\r\n"
            "\r\n"
        ).encode("ascii")
        self.wfile.write(raw)
        self.close_connection = True

    def log_message(self, fmt: str, *args: object) -> None:
        pass


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", 3600), Handler).serve_forever()
