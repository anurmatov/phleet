#!/usr/bin/env python3
"""#460 AC-C1: every Codex key the harness sets or allows is one the pinned codex CLI actually reads.

    python3 tests/codex-harness-keys/run.py

Exit 0: pass, 1: assertion, 2: infrastructure. Needs the pinned `codex` on PATH; no credential and no
network: every server is a local stub.

It also checks the Codex family aliases in src/Fleet.Shared/HarnessDefaults.cs: each maps to an id in the
pinned CLI's bundled catalog (`model/list`), and the CLI still sends a bare alias unchanged (so the map
is still needed).

Codex's config parser ignores unknown keys, so `thread/start` succeeding proves nothing about a key.
Instead, each allowlisted key (read from src/Fleet.Shared/HarnessExtra.cs) is sent with a value of the
wrong type: a key the CLI reads fails thread/start naming it, an unknown key is accepted (checked with
a control key). Then the material local-mode effect is checked on a captured request: with the local
defaults the model request carries no `web_search` and no `view_image` tool, and without them it does.
"""
import json, os, re, subprocess, sys, tempfile, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def fail(msg, code=1):
    print(("FAIL " if code == 1 else "INFRA ") + msg, file=sys.stderr)
    sys.exit(code)


source = open(os.path.join(ROOT, "src/Fleet.Shared/HarnessExtra.cs"), encoding="utf-8").read()
KEYS = re.findall(r'new\("([a-z_.]+)", HarnessExtraTarget\.Codex(?:Root|Thread)', source)
if len(KEYS) != 10:
    fail(f"expected 10 Codex keys in HarnessExtra.cs, read {len(KEYS)}: {KEYS}", 2)
ALIASES = dict(re.findall(r'\["(sol|terra|luna)"\] = "([^"]+)"',
                          open(os.path.join(ROOT, "src/Fleet.Shared/HarnessDefaults.cs"), encoding="utf-8").read()))
if sorted(ALIASES) != ["luna", "sol", "terra"]:
    fail(f"expected the sol, terra and luna aliases in HarnessDefaults.cs, read {ALIASES}", 2)


class Codex:
    def __init__(self, oss_base_url=None):
        self.home = tempfile.mkdtemp(prefix="codexhome-")
        env = {k: v for k, v in os.environ.items() if not k.startswith(("CODEX_", "OPENAI_"))}
        env["CODEX_HOME"] = self.home
        if oss_base_url:
            env["CODEX_OSS_BASE_URL"] = oss_base_url
        try:
            self.p = subprocess.Popen(["codex", "app-server", "--listen", "stdio://"], stdin=subprocess.PIPE,
                                      stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, env=env, cwd=self.home)
        except FileNotFoundError:
            fail("codex is not on PATH", 2)
        self.send({"id": 1, "method": "initialize", "params": {"clientInfo": {"name": "phleet", "title": "t", "version": "0"},
                                                              "capabilities": {"experimentalApi": False}}})
        self.wait(id=1)
        self.send({"method": "initialized"})

    def send(self, msg):
        self.p.stdin.write(json.dumps({"jsonrpc": "2.0", **msg}) + "\n")
        self.p.stdin.flush()

    def wait(self, id=None, method=None, timeout=60):
        deadline = time.time() + timeout
        for line in self.p.stdout:
            msg = json.loads(line)
            if (id is not None and msg.get("id") == id) or (method and msg.get("method") == method):
                return msg
            if time.time() > deadline:
                break
        fail(f"codex app-server gave no {'response ' + str(id) if id else method}", 2)

    def thread_start(self, config, local=False, model=None, provider=None):
        params = {"model": model or ("qwen-test" if local else "gpt-test"), "config": config, "cwd": self.home,
                  "approvalPolicy": "never", "sandbox": "danger-full-access", "serviceName": "phleet",
                  "baseInstructions": "x", "ephemeral": True}
        if local or provider:
            params["modelProvider"] = provider or "ollama"
        self.send({"id": 2, "method": "thread/start", "params": params})
        return self.wait(id=2)

    def close(self):
        self.p.kill()


# ── 1. every key is read ──────────────────────────────────────────────────────

WRONG = {"__not_a_value__": 1}
codex = Codex()
control = codex.thread_start({"phleet_unknown_key": WRONG})
if "error" in control:
    fail(f"control: an unknown key was refused ({control['error']}); the discriminator no longer holds", 2)
codex.close()
for key in KEYS:
    codex = Codex()
    reply = codex.thread_start({key: WRONG})
    codex.close()
    message = (reply.get("error") or {}).get("message", "")
    if "error" not in reply or key.split(".")[0] not in message:
        fail(f"{key}: the pinned codex accepted a wrong-typed value, so it does not read this key")
    print(f"PASS {key}: read by the pinned codex ({message.splitlines()[0][:80]})")

# ── 2. the local defaults remove the two tools from the model request ─────────

class Stub(BaseHTTPRequestHandler):
    bodies = []

    def do_POST(self):
        Stub.bodies.append(json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0)))))
        self.send_response(400)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(b'{"error":{"message":"stub"}}')

    def log_message(self, *args):
        pass


server = ThreadingHTTPServer(("127.0.0.1", 0), Stub)
threading.Thread(target=server.serve_forever, daemon=True).start()
url = f"http://127.0.0.1:{server.server_address[1]}/v1"


def tools_sent(config):
    Stub.bodies.clear()
    codex = Codex(oss_base_url=url)
    thread = codex.thread_start(config, local=True)
    if "error" in thread:
        fail(f"local thread/start refused {config}: {thread['error']}")
    codex.send({"id": 3, "method": "turn/start", "params": {"threadId": thread["result"]["thread"]["id"],
                                                             "input": [{"type": "text", "text": "hi", "text_elements": []}]}})
    codex.wait(method="turn/completed")
    codex.close()
    if not Stub.bodies:
        fail("codex sent no model request to the stub server", 2)
    return {t.get("name") or t.get("type") for t in Stub.bodies[0].get("tools", [])}


LOCAL = {"web_search": "disabled", "features.view_image": False, "model_reasoning_summary": "none",
         "tool_output_token_limit": 8192, "features.apps": False, "features.remote_plugin": False,
         "features.skill_mcp_dependency_install": False}
before, after = tools_sent({}), tools_sent(LOCAL)
if not {"web_search", "view_image"} <= before:
    fail(f"without the defaults the request should carry web_search and view_image: {sorted(before)}", 2)
if after & {"web_search", "view_image"}:
    fail(f"with the local defaults the request still carries {sorted(after & {'web_search', 'view_image'})}")
if before - after != {"web_search", "view_image"}:
    fail(f"the local defaults removed more than the two tools: {sorted(before - after)}")
print(f"PASS local defaults: the model request drops web_search and view_image, keeps {sorted(after)}")

# ── 3. the Codex family aliases ───────────────────────────────────────────────

codex = Codex()
codex.send({"id": 5, "method": "model/list", "params": {}})
listed = codex.wait(id=5)
codex.close()
items = (listed.get("result") or {}).get("data")
if not isinstance(items, list) or not items:
    fail(f"model/list returned no catalog: {json.dumps(listed)[:200]}", 2)
catalog = {m.get("id") or m.get("model") for m in items}
for alias, model_id in ALIASES.items():
    if model_id not in catalog:
        fail(f"alias {alias} maps to {model_id}, which the pinned codex catalog does not list: {sorted(catalog)}")
    if alias in catalog:
        print(f"NOTE {alias}: the pinned catalog now lists the bare alias; the map may no longer be needed")
    print(f"PASS alias {alias} -> {model_id}: in the pinned codex catalog")

STUB = {"model_providers.stub.name": "stub", "model_providers.stub.base_url": url,
        "model_providers.stub.wire_api": "responses", "model_providers.stub.requires_openai_auth": False,
        "model_providers.stub.request_max_retries": 0, "model_providers.stub.stream_max_retries": 0}
Stub.bodies.clear()
codex = Codex()
thread = codex.thread_start(STUB, model="sol", provider="stub")
if "error" in thread:
    fail(f"thread/start with model sol was refused: {thread['error']}", 2)
codex.send({"id": 3, "method": "turn/start", "params": {"threadId": thread["result"]["thread"]["id"],
                                                         "input": [{"type": "text", "text": "hi", "text_elements": []}]}})
codex.wait(method="turn/completed")
codex.close()
sent = [b.get("model") for b in Stub.bodies]
if sent[:1] != ["sol"]:
    print(f"NOTE the pinned codex sent {sent[:1]} for model sol; it may now resolve aliases itself")
else:
    print("PASS the pinned codex sends a bare alias unchanged, so HarnessDefaults maps it")
server.shutdown()
