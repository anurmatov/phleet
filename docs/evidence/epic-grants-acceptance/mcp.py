import json, urllib.request
BASE = "http://127.0.0.1:3001/"
class Mcp:
    def __init__(self, agent):
        self.url = BASE + f"?agent={agent}"; self.sid = None; self.n = 0
    def _post(self, payload):
        h = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
        if self.sid: h["Mcp-Session-Id"] = self.sid
        req = urllib.request.Request(self.url, json.dumps(payload).encode(), h, method="POST")
        with urllib.request.urlopen(req, timeout=180) as r:
            self.sid = r.headers.get("Mcp-Session-Id") or self.sid
            body = r.read().decode()
        if not body.strip(): return None
        if body.lstrip().startswith("{"): return json.loads(body)
        data = [l[5:].strip() for l in body.splitlines() if l.startswith("data:")]
        return json.loads(data[-1]) if data else None
    def init(self):
        self.n += 1
        r = self._post({"jsonrpc": "2.0", "id": self.n, "method": "initialize", "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "accept436", "version": "1"}}})
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return r
    def call(self, name, args):
        self.n += 1
        r = self._post({"jsonrpc": "2.0", "id": self.n, "method": "tools/call", "params": {"name": name, "arguments": args}})
        return "".join(c.get("text", "") for c in r["result"]["content"])
