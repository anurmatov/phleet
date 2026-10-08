"""Unauthenticated GitHub repo-read stub for the #436 acceptance run (loopback only)."""
import json, sys
from http.server import BaseHTTPRequestHandler, HTTPServer
REPOS = {"/repos/example-org/accept-private": (404, {"message": "Not Found"}),
         "/repos/example-org/accept-public": (200, {"private": False, "full_name": "example-org/accept-public"})}
class H(BaseHTTPRequestHandler):
    def do_GET(self):
        status, body = REPOS.get(self.path, (404, {"message": "Not Found"}))
        data = json.dumps(body).encode()
        self.send_response(status); self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
        print(f"GET {self.path} -> {status} ua={self.headers.get('User-Agent')} auth={'yes' if self.headers.get('Authorization') else 'no'}", flush=True)
    def log_message(self, *a): pass
HTTPServer(("127.0.0.1", 3901), H).serve_forever()
