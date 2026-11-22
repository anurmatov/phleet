"""A stand-in for the Gemini API, for the Antigravity CLI harness. No model is involved.

    fake_gemini.py PORT LOG

It records what each request carried (one JSON object per line in LOG) and answers from the text of
the newest user request in it:

    CALL <tool> <json>   answers a function call of that tool with those arguments, once
    SLOW                 streams a chunk every half second for 20 seconds before it finishes
    FAIL <status>        answers that HTTP status with a Gemini-style error body
    anything else        answers "Scripted reply N." where N counts the user texts so far

After a function response it answers "Tool said: <the response>". STANDIN_KEY is the value the
record looks for (it says where the key was, never the key). The system instruction and the turn
text of the latest request are kept whole in LOG.last-request.json, for the checks that compare
them byte for byte.
"""
import hashlib
import json
import os
import re
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

LOG = sys.argv[2]
KEY = os.environ.get("STANDIN_KEY", "")
USAGE = {"promptTokenCount": 1200, "candidatesTokenCount": 30, "totalTokenCount": 1237,
         "cachedContentTokenCount": 400, "thoughtsTokenCount": 7}


def record(entry):
    with open(LOG, "a") as log:
        log.write(json.dumps(entry, ensure_ascii=False) + "\n")


def text_of(content):
    return "\n".join(p["text"] for p in (content or {}).get("parts", []) if "text" in p)


def shape(content):
    kinds = []
    for part in content.get("parts", []):
        kinds.append(next((k for k in ("text", "functionCall", "functionResponse", "inlineData") if k in part),
                          "+".join(part.keys())))
    return content.get("role", "?") + "[" + ",".join(kinds) + "]"


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def key_places(self, raw):
        places = [name for name, value in self.headers.items() if KEY and KEY in value]
        if KEY and KEY in self.path:
            places.append("(url)")
        if KEY and KEY.encode() in raw:
            places.append("(body)")
        return sorted(places)

    def send_json(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        record({"GET": self.path, "key_in": self.key_places(b"")})
        self.send_json(200, {})

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        try:
            body = json.loads(raw or b"{}")
        except ValueError:
            body = {}
        path = self.path.split("?")[0]
        contents = body.get("contents", [])
        system = text_of(body.get("systemInstruction"))
        declarations = [d for t in body.get("tools", []) for d in t.get("functionDeclarations", [])]
        last = contents[-1] if contents else {}
        last_model = max((i for i, c in enumerate(contents) if c.get("role") == "model"), default=-1)
        turn = "\n".join(text_of(c) for c in contents[last_model + 1:])
        # The script is read from the newest request only: an earlier one that never got its
        # answer (a killed turn) is still in the turn text.
        requests = [text_of(c) for c in contents if c.get("role") == "user" and "<USER_REQUEST>" in text_of(c)]
        script = requests[-1] if requests else turn
        answered = any("functionResponse" in p for p in last.get("parts", []))

        record({
            "POST": path, "key_in": self.key_places(raw),
            "system_bytes": len(system.encode()), "system_sha256": hashlib.sha256(system.encode()).hexdigest(),
            "turn_bytes": len(turn.encode()), "contents": [shape(c) for c in contents],
            "user_texts": [text_of(c)[:80] for c in contents if c.get("role") == "user" and text_of(c)],
            "tools": [d["name"] for d in declarations],
        })
        with open(LOG + ".last-request.json", "w") as last_request:
            json.dump({"system": system, "turn": turn, "tools": [d["name"] for d in declarations]}, last_request)

        fail = re.search(r"\bFAIL (\d{3})\b", script)
        if fail and not answered:
            status = int(fail.group(1))
            self.send_json(status, {"error": {"code": status, "message": "scripted failure", "status": "INTERNAL"}})
            return

        call = re.search(r"\bCALL (\w+) (\{.*\})", script)
        slow = False
        if answered:
            response = next(p["functionResponse"] for p in last["parts"] if "functionResponse" in p)
            parts = [{"text": "Tool said: " + json.dumps(response.get("response"), ensure_ascii=False)[:600]}]
        elif call:
            parts = [{"functionCall": {"name": call.group(1), "args": json.loads(call.group(2))}}]
        else:
            slow = re.search(r"\bSLOW\b", script) is not None
            count = sum(1 for c in contents if c.get("role") == "user" and text_of(c))
            parts = [{"text": "Scripted reply %d." % count}]
        answer = {"candidates": [{"content": {"role": "model", "parts": parts}, "finishReason": "STOP", "index": 0}],
                  "usageMetadata": USAGE, "modelVersion": "stand-in"}

        if path.endswith(":countTokens"):
            self.send_json(200, {"totalTokens": USAGE["promptTokenCount"]})
            return
        if not path.endswith(":streamGenerateContent"):
            self.send_json(200, answer)
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Connection", "close")
        self.end_headers()
        try:
            if slow:
                for i in range(40):
                    chunk = {"candidates": [{"content": {"role": "model", "parts": [{"text": "slow %d " % i}]}, "index": 0}]}
                    self.wfile.write(("data: " + json.dumps(chunk) + "\n\n").encode())
                    self.wfile.flush()
                    time.sleep(0.5)
            self.wfile.write(("data: " + json.dumps(answer) + "\n\n").encode())
            self.wfile.flush()
        except OSError:
            record({"note": "the client closed the stream"})
        self.close_connection = True


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
