#!/usr/bin/env python3
"""Synthetic HTTPS upstream for the fleet-secrets end-to-end harness.

One server answers for every bound host of the harness: a generic API (api.example.test), the GitHub
REST API and App endpoints (api.github.com) and the git smart-HTTP host (github.com). It records what
credential each request carried, so the harness can prove what left the forwarder. Nothing here is a
real service and every credential is a fixture.
"""
import base64, json, ssl, sys, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

STATIC_SECRET, LOG = sys.argv[1], sys.argv[2]
INSTALLATIONS = [
    {"id": 11, "account": {"login": "solo-dev", "type": "User"}},
    {"id": 22, "account": {"login": "acme", "type": "Organization"}},
]
TOKENS = {f"ghs_synthetic_installation_{i['id']}": i for i in INSTALLATIONS}


def pkt(line: bytes) -> bytes:
    return b"%04x" % (len(line) + 4) + line


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def record(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length) if length else b""
        entry = {
            "host": (self.headers.get("Host") or "").lower(),
            "method": self.command,
            "path": self.path,
            "authorization": self.headers.get("Authorization"),
            "x_api_key": self.headers.get("X-Api-Key"),
            "body": body.decode("utf-8", "replace")[:2000],
        }
        with open(LOG, "a") as log:
            log.write(json.dumps(entry) + "\n")
        return entry

    def send(self, status, payload=b"", content_type="application/json", headers=None):
        if isinstance(payload, (dict, list)):
            payload = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(payload)

    def installation_of(self, authorization):
        """The installation whose token this Authorization value carries, or None."""
        if not authorization:
            return None
        scheme, _, value = authorization.partition(" ")
        if scheme.lower() == "basic":
            try:
                value = base64.b64decode(value).decode().partition(":")[2]
            except Exception:
                return None
        return TOKENS.get(value.strip())

    def handle_request(self):
        entry = self.record()
        host, path, auth = entry["host"], self.path.split("?")[0], entry["authorization"]

        if host == "api.example.test":
            if path == "/redirect":
                return self.send(302, b"", headers={"Location": "https://evil.example.test/capture"})
            if path == "/echo":
                return self.send(200, b"echo", headers={"X-Echo": auth or ""})
            if path == "/public":
                return self.send(200, {"public": True})
            if auth == f"Bearer {STATIC_SECRET}" or entry["x_api_key"] == STATIC_SECRET:
                return self.send(200, {"ok": True, "path": path})
            return self.send(401, {"message": "bad credentials"})

        if host == "api.github.com":
            if path.startswith("/app/"):
                # App endpoints take the App's JWT, never an installation token.
                jwt = (auth or "").partition(" ")[2]
                if (auth or "").split(" ")[0] != "Bearer" or jwt.count(".") != 2 or jwt in TOKENS:
                    return self.send(401, {"message": "A JSON web token could not be decoded"})
                if path == "/app/installations":
                    return self.send(200, INSTALLATIONS)
                parts = path.strip("/").split("/")
                if len(parts) == 4 and parts[3] == "access_tokens" and self.command == "POST":
                    expires = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(time.time() + 3600))
                    return self.send(201, {"token": f"ghs_synthetic_installation_{parts[2]}", "expires_at": expires})
                return self.send(404, {"message": "Not Found"})
            installation = self.installation_of(auth)
            if installation is None:
                return self.send(401, {"message": "Bad credentials"})
            answer = {"installation": installation["id"], "account": installation["account"]["login"], "login": "synthetic-app[bot]"}
            if path == "/graphql":
                return self.send(200, {"data": {"viewer": {"login": f"installation-{installation['id']}"}}})
            return self.send(200, answer)

        if host == "github.com":
            installation = self.installation_of(auth)
            if installation is None:
                return self.send(401, b"", headers={"WWW-Authenticate": 'Basic realm="GitHub"'})
            owner = path.strip("/").split("/")[0]
            if owner.lower() != installation["account"]["login"].lower():
                return self.send(404, b"Repository not found.\n", "text/plain")
            if path.endswith("/info/refs"):
                sha = b"1" * 40
                body = (pkt(b"# service=git-upload-pack\n") + b"0000"
                        + pkt(sha + b" HEAD\0symref=HEAD:refs/heads/main agent=synthetic\n")
                        + pkt(sha + b" refs/heads/main\n") + b"0000")
                return self.send(200, body, "application/x-git-upload-pack-advertisement")
            return self.send(404, b"not found\n", "text/plain")

        return self.send(421, {"message": "unknown host"})

    do_GET = do_POST = do_PUT = do_DELETE = do_HEAD = do_PATCH = handle_request


server = ThreadingHTTPServer(("0.0.0.0", 443), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(sys.argv[3], sys.argv[4])
server.socket = context.wrap_socket(server.socket, server_side=True)
print("synthetic upstream ready", flush=True)
server.serve_forever()
