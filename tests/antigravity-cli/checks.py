"""The checks of tests/antigravity-cli/run.sh. Runs inside the container, which has no network.

It starts the stand-ins on loopback, drives the pinned Antigravity CLI (`agy`) the way
GeminiExecutor does, and asserts what the fleet relies on. Every line it prints starts with PASS or
FAIL; it exits 1 when any check failed. What it sent and what came back is written to
/out/transcript.txt.

The constants below are GeminiExecutor's and GeminiInstructionFiles' own;
AntigravityHarnessParityTests (tests/Fleet.Agent.Tests) fails when they differ.
"""
import hashlib
import json
import os
import pathlib
import queue
import re
import subprocess
import sys
import threading
import time

FLAGS = ["--output-format", "stream-json", "--input-format", "stream-json", "--disable-slash-commands", "--dangerously-skip-permissions", "--settings", '{"modelProvider":"gemini","enableTerminalSandbox":false}']
MAX_PROMPT_BYTES = 190000
CHUNK_BYTES = 20000
BUDGET_BYTES = 56000
FRONT_MATTER = "---\ntrigger: always_on\n---\n"
FILE_PREFIX = "fleet-instructions-"
DISABLE_AUTO_UPDATE = "AGY_CLI_DISABLE_AUTO_UPDATE"

# What `agy models` prints for an API key; the dashboard offers exactly these.
MODELS = [
    "gemini-4-neon-high", "gemini-4-neon-xhigh", "gemini-4-neon-medium", "gemini-4-neon-low",
    "gemini-4-carbon-high", "gemini-4-carbon-medium", "gemini-4-carbon-low",
    "gemini-4-hydrogen-high", "gemini-4-hydrogen-xhigh", "gemini-4-hydrogen-medium", "gemini-4-hydrogen-low",
    "gemini-4-argon-high", "gemini-4-argon-medium", "gemini-4-argon-low",
    "gemini-3.8-flash-high", "gemini-3.8-flash-medium", "gemini-3.8-flash-low",
    "gemini-3.7-flash-high", "gemini-3.7-flash-medium", "gemini-3.7-flash-low",
    "gemini-3.6-flash-high", "gemini-3.6-flash-medium", "gemini-3.6-flash-low",
    "gemini-3.1-pro-high", "gemini-3.1-pro-low",
]

HARNESS = pathlib.Path(__file__).resolve().parent
OUT = pathlib.Path("/out")
HOME = pathlib.Path(os.environ["HOME"])
GEMINI = HOME / ".gemini"
RULES = GEMINI / "config" / "rules"
WORKSPACE = pathlib.Path("/workspace")
API_LOG, MCP_LOG, NET_LOG = OUT / "api.log", OUT / "mcp.log", OUT / "net.log"
LAST_REQUEST = pathlib.Path(str(API_LOG) + ".last-request.json")

KEY = "synthetic-gemini-key-0001-not-real"
MCP_AUTHORIZATION = "Bearer synthetic-mcp-token-1"
UPDATER_HOST = "antigravity-cli-auto-updater"
EXPECTED_VERSION = os.environ["AGY_EXPECTED_VERSION"]

failures = []
transcript = []


def note(text):
    transcript.append(text)


def check(name, ok, detail=None):
    line = ("PASS " if ok else "FAIL ") + name + ("" if ok or detail is None else " :: " + str(detail)[:600])
    print(line, flush=True)
    note(line)
    if not ok:
        failures.append(name)
    return ok


def lines_of(path):
    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def api_posts():
    return [entry for entry in lines_of(API_LOG) if "POST" in entry]


def last_request():
    return json.loads(LAST_REQUEST.read_text())


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def environment(**extra):
    env = dict(os.environ)
    env.update({"GEMINI_API_KEY": KEY, "GOOGLE_GEMINI_BASE_URL": "http://127.0.0.1:18912", DISABLE_AUTO_UPDATE: "true"})
    for name, value in extra.items():
        if value is None:
            env.pop(name, None)
        else:
            env[name] = value
    return env


def turn(message, conversation=None, model=None, kill_after=None, env=None, label=""):
    """One process, driven as GeminiExecutor drives it: read init, write the message, close the input."""
    args = ["agy"] + FLAGS + (["--model", model] if model else []) + (["--conversation", conversation] if conversation else [])
    posts_before = len(api_posts())
    started = time.time()
    process = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               cwd=WORKSPACE, env=env or environment())
    received = queue.Queue()
    stderr_lines = []
    threading.Thread(target=lambda: [received.put(line) for line in process.stdout] + [received.put(None)], daemon=True).start()
    threading.Thread(target=lambda: stderr_lines.extend(line.decode(errors="replace") for line in process.stderr), daemon=True).start()

    result = {"args": args[1:], "events": [], "init": None, "requests_before_input": None, "killed": False, "wrote": False}
    try:
        first = received.get(timeout=60)
    except queue.Empty:
        first = None
    if first:
        try:
            event = json.loads(first)
        except ValueError:
            event = {"event": "(not json)", "text": first.decode(errors="replace")[:200]}
        result["events"].append(event)
        if event.get("event") == "init":
            result["init"] = event.get("conversation_id")
            # Nothing has been written yet: the CLI said which conversation this is before any input.
            result["requests_before_input"] = len(api_posts()) - posts_before
            process.stdin.write((json.dumps({"event": "user", "message": {"content": message}}, ensure_ascii=False) + "\n").encode())
            process.stdin.close()
            result["wrote"] = True
    if kill_after is not None:
        time.sleep(kill_after)
        process.kill()
        result["killed"] = True
    deadline = time.time() + 180
    while first is not None:
        try:
            line = received.get(timeout=max(1, deadline - time.time()))
        except queue.Empty:
            process.kill()
            result["timed_out"] = True
            break
        if line is None:
            break
        try:
            result["events"].append(json.loads(line))
        except ValueError:
            result["events"].append({"event": "(not json)", "text": line.decode(errors="replace")[:200]})
    result["exit"] = process.wait()
    time.sleep(0.2)
    result["stderr"] = "".join(stderr_lines)
    final = next((e["result"] for e in result["events"] if e.get("event") == "result"), {})
    result["status"], result["response"], result["usage"] = final.get("status"), final.get("response"), final.get("usage")

    note("")
    note("$ agy " + " ".join(a if " " not in a and "{" not in a else "'" + a + "'" for a in args[1:]) + ("   # " + label if label else ""))
    if result["wrote"]:
        note("  stdin (after init): " + json.dumps({"event": "user", "message": {"content": message if len(message) < 200 else message[:80] + "… (%d bytes)" % len(message.encode())}}, ensure_ascii=False))
    else:
        note("  stdin: nothing was written (no init)")
    deltas = 0
    for event in result["events"]:
        step = event.get("step_update", {})
        if step.get("step_type") == "agent_response" and step.get("state") == "ACTIVE":
            deltas += 1
            if deltas > 2:
                continue
        elif deltas > 2:
            note("  … %d more text deltas of the same shape" % (deltas - 2))
            deltas = 0
        text = json.dumps(event, ensure_ascii=False)
        text = re.sub(r'"tools": \[[^\]]*\]', '"tools": […]', text)
        note("  " + (text if len(text) < 420 else text[:420] + "…"))
    if deltas > 2:
        note("  … %d more text deltas of the same shape, then the process was killed" % (deltas - 2))
    for line in result["stderr"].splitlines()[:6]:
        note("  stderr: " + line[:300])
    note("  exit: %s   (%.1f s)" % (result["exit"], time.time() - started))
    return result


def tools_of(result):
    return [e["step_update"] for e in result["events"]
            if e.get("event") == "step_update" and e["step_update"].get("step_type") == "tool"]


def write_instructions(text):
    """The layout GeminiInstructionFiles.Prepare writes: line-bounded pieces, names that sort."""
    RULES.mkdir(parents=True, exist_ok=True)
    chunks, current = [], ""
    for line in text.splitlines(keepends=True):
        if current and len((current + line).encode()) > CHUNK_BYTES:
            chunks.append(current)
            current = ""
        current += line
    if current:
        chunks.append(current)
    for old in RULES.glob(FILE_PREFIX + "*"):
        old.unlink()
    for number, chunk in enumerate(chunks, 1):
        (RULES / ("%s%03d.md" % (FILE_PREFIX, number))).write_bytes((FRONT_MATTER + chunk).encode())
    return chunks


def delivered_instructions(system):
    """Our pieces as they appear in the system instruction, in the order they appear."""
    pattern = r"<RULE\[(%s/%s\d+\.md)\]>\n(.*?)\n</RULE\[\1\]>" % (re.escape(str(RULES)), re.escape(FILE_PREFIX))
    blocks = re.findall(pattern, system, re.S)
    return [pathlib.Path(path).name for path, _ in blocks], "".join(body for _, body in blocks)


def filler(total_bytes, head, tail, unit):
    text, number = head + "\n", 0
    while len((text + unit % number).encode()) + len(tail.encode()) <= total_bytes:
        text += unit % number
        number += 1
    text += "." * (total_bytes - len(text.encode()) - len(tail.encode())) + tail
    assert len(text.encode()) == total_bytes
    return text


def start(script, *arguments, **extra_env):
    env = dict(os.environ)
    env.update(extra_env)
    return subprocess.Popen([sys.executable, str(HARNESS / script)] + [str(a) for a in arguments], env=env)


def main():
    # ── the stand-ins and the watcher ────────────────────────────────────────
    OUT.mkdir(exist_ok=True)
    for log in (API_LOG, MCP_LOG, NET_LOG):
        log.write_text("")
    pathlib.Path("/etc/resolv.conf").write_text("nameserver 127.0.0.1\n")
    start("netwatch.py", NET_LOG)
    start("fake_gemini.py", 18912, API_LOG, STANDIN_KEY=KEY)
    start("mcp_recorder.py", 18911, MCP_LOG, MCP_EXPECTED_AUTHORIZATION=MCP_AUTHORIZATION)
    time.sleep(1.5)
    WORKSPACE.mkdir(exist_ok=True)

    binary = subprocess.run(["which", "agy"], capture_output=True, text=True).stdout.strip()
    binary = os.path.realpath(binary)
    sha_before = sha256_of(binary)
    version = subprocess.run(["agy", "--version"], capture_output=True, text=True, env=environment()).stdout.strip()
    note("agy --version: %s   (binary sha256 %s)" % (version, sha_before))
    check("the binary is the pinned release %s" % EXPECTED_VERSION, version == EXPECTED_VERSION, version)
    check("the binary's directory is writable by this user, so only the switch keeps it from updating itself",
          os.access(os.path.dirname(binary), os.W_OK))

    # ── the MCP file, as the agent's own `prepare config` wrote it ───────────
    mcp_file = GEMINI / "config" / "mcp_config.json"
    if os.environ.get("HARNESS_PREPARE") == "image":
        prepared = subprocess.run(["dotnet", "/app/Fleet.Agent.dll", "prepare", "config"], capture_output=True, text=True, env=environment())
        note("$ dotnet /app/Fleet.Agent.dll prepare config\n  " + (prepared.stdout + prepared.stderr).strip().replace("\n", "\n  "))
        check("the image's `prepare config` wrote the MCP file", prepared.returncode == 0 and mcp_file.exists(), prepared.stderr)
    else:
        mcp_file.parent.mkdir(parents=True, exist_ok=True)
        mcp_file.write_bytes(pathlib.Path("/in/mcp_config.json").read_bytes())
        os.chmod(mcp_file, 0o600)
    note("~/.gemini/config/mcp_config.json:\n  " + mcp_file.read_text().replace(MCP_AUTHORIZATION, "Bearer <token>").strip().replace("\n", "\n  "))
    check("the MCP file is for its owner only", (mcp_file.stat().st_mode & 0o777) == 0o600, oct(mcp_file.stat().st_mode))
    check("no settings file is needed", not (GEMINI / "antigravity-cli" / "settings.json").exists())

    # ── a turn, with instructions ────────────────────────────────────────────
    instructions = "INSTRUCTIONS-ALPHA\nYou are a synthetic test agent.\n"
    write_instructions(instructions)
    first = turn("First question.", model="gemini-4-argon-medium", label="a turn in a new conversation")
    conversation = first["init"]
    posts = api_posts()
    check("a turn completes: init, then the message, then a SUCCESS result, exit 0",
          conversation and first["status"] == "SUCCESS" and first["exit"] == 0 and first["response"] == "Scripted reply 1.\n", first["events"][-1:])
    check("init is printed before the CLI has read any input, and before any model request", first["requests_before_input"] == 0)
    check("one request to the Gemini API's streamGenerateContent, model taken from the slug",
          len(posts) == 1 and posts[0]["POST"] == "/v1beta/models/gemini-4-argon:streamGenerateContent", [p["POST"] for p in posts])
    check("the result carries token counts", isinstance((first["usage"] or {}).get("input_tokens"), int), first["usage"])
    names, delivered = delivered_instructions(last_request()["system"])
    check("the instructions are in the system instruction, inside the CLI's own prompt",
          delivered == instructions and len(last_request()["system"]) > len(instructions) + 5000, names)
    check("a message that starts with a slash is a message",
          turn("/help", conversation=conversation, label="slash commands are off")["status"] == "SUCCESS" and "/help" in last_request()["turn"])

    # ── resume by id ─────────────────────────────────────────────────────────
    write_instructions("INSTRUCTIONS-BETA\nChanged instructions.\n")
    resumed = turn("Second question.", conversation=conversation, label="a new process continues the conversation")
    request = api_posts()[-1]
    check("--conversation <id> continues that conversation in a new process", resumed["init"] == conversation and resumed["status"] == "SUCCESS", resumed["init"])
    check("the earlier turns are sent again", any("First question." in t for t in request["user_texts"]) and any("Second question." in t for t in request["user_texts"]), request["user_texts"])
    check("a resumed conversation gets the instructions as they are now (so the fleet starts a new one when they change)",
          "INSTRUCTIONS-BETA" in last_request()["system"] and "INSTRUCTIONS-ALPHA" not in last_request()["system"])
    check("the result counts the whole conversation", (resumed["usage"] or {}).get("input_tokens", 0) > first["usage"]["input_tokens"], resumed["usage"])

    unknown = turn("Lost question.", conversation="00000000-0000-4000-8000-000000000000", label="a conversation the CLI does not have")
    check("an unknown conversation id does not fail: init names another conversation, before any input",
          unknown["init"] not in (None, "00000000-0000-4000-8000-000000000000") and unknown["requests_before_input"] == 0 and unknown["exit"] == 0, unknown["init"])
    check("and the CLI says so on stderr", 'conversation "00000000-0000-4000-8000-000000000000" not found' in unknown["stderr"], unknown["stderr"][:200])
    check("the message then written is the first of that new conversation", api_posts()[-1]["contents"] == ["user[text]"], api_posts()[-1]["contents"])

    # ── a killed process ─────────────────────────────────────────────────────
    killed = turn("SLOW answer, killed half way.", conversation=conversation, kill_after=4, label="killed with SIGKILL after 4 s")
    check("a process killed mid-answer was mid-answer", killed["killed"] and killed["status"] is None and any("slow" in json.dumps(e) for e in killed["events"]), killed["events"][-1:])
    after_kill = turn("Third question.", conversation=conversation, label="after the kill")
    check("the conversation continues after a kill", after_kill["init"] == conversation and after_kill["status"] == "SUCCESS", after_kill["events"][-1:])

    # ── the model's tools ────────────────────────────────────────────────────
    shell = turn('CALL run_command {"CommandLine":"id -un > /workspace/shell-proof.txt; echo outside > /tmp/outside-proof.txt; echo SHELL-RAN-$((6*7))","toolSummary":"Shell proof","WaitMsBeforeAsync":5000}',
                 label="the model's shell tool")
    steps = tools_of(shell)
    check("run_command runs in the container, with no approval and no sandbox refusal",
          pathlib.Path("/workspace/shell-proof.txt").exists() and pathlib.Path("/tmp/outside-proof.txt").exists()
          and any("SHELL-RAN-42" in (s.get("tool_info", {}).get("output") or "") for s in steps), steps[-1:])
    check("a tool step is ACTIVE with its name and parameters, then DONE with its output",
          [s["state"] for s in steps] == ["ACTIVE", "DONE"] and steps[0]["tool_name"] == "run_command" and "CommandLine" in steps[0]["tool_info"]["parameters"], steps)
    turn('CALL write_file {"Path":"/workspace/write-proof.txt","Content":"WRITE-RAN\\n"}', label="the model's write tool")
    check("write_file writes in the workspace", pathlib.Path("/workspace/write-proof.txt").exists() and pathlib.Path("/workspace/write-proof.txt").read_text() == "WRITE-RAN\n")

    # ── MCP ──────────────────────────────────────────────────────────────────
    MCP_LOG.write_text("")
    mcp = turn('CALL call_mcp_tool {"ServerName":"recorder","ToolName":"get_message","Arguments":{"id":7}}', label="an MCP tool over HTTP")
    requests = lines_of(MCP_LOG)
    note("  MCP server saw:\n    " + "\n    ".join(json.dumps(r) for r in requests))
    discovery = [r for r in requests if r["path"].startswith("/.well-known/")]
    others = [r for r in requests if not r["path"].startswith("/.well-known/")]
    methods = [r.get("method") for r in others]
    check("the MCP tool is listed and called", "tools/list" in methods and any(r.get("tool") == "get_message" and r.get("args") == {"id": 7} for r in others), methods)
    check("its answer reaches the model", "the heron lands at dusk" in (mcp["response"] or ""), mcp["response"])
    check("every MCP request carries the configured header (%d requests: %s)" % (len(others), ", ".join(sorted({m or r["http"] for m, r in zip(methods, others)}))),
          len(others) >= 5 and all(r["auth"] == "ok" for r in others), [r for r in others if r["auth"] != "ok"])
    check("the OAuth discovery requests carry no header, get 401, and nothing follows from that",
          len(discovery) >= 1 and all(r["auth"] == "missing" and r["status"] == 401 for r in discovery), discovery)
    stdio = turn('CALL call_mcp_tool {"ServerName":"local","ToolName":"ping","Arguments":{}}', label="an MCP tool over stdio")
    check("a stdio MCP server works beside it", "pong from the stdio server" in (stdio["response"] or ""), stdio["response"])

    # ── what is never cut: the message ───────────────────────────────────────
    at_limit = filler(MAX_PROMPT_BYTES, "MESSAGE-HEAD", "MESSAGE-TAIL", "line %06d of a synthetic message, with кириллица and 🙂.\n")
    whole = turn(at_limit, label="a message of exactly MaxPromptBytes")
    sent = last_request()["turn"]
    check("a message of %d bytes arrives whole" % MAX_PROMPT_BYTES, whole["status"] == "SUCCESS" and at_limit in sent and "<truncated" not in sent, sent[-120:])
    over = turn(filler(192100, "MESSAGE-HEAD", "MESSAGE-TAIL", "line %06d of a synthetic message.\n"), label="a message over the CLI's own limit")
    check("the CLI cuts a message over its limit without failing (why the fleet refuses one over %d)" % MAX_PROMPT_BYTES,
          over["status"] == "SUCCESS" and "MESSAGE-TAIL" not in last_request()["turn"] and "<truncated" in last_request()["turn"], last_request()["turn"][-80:])

    # ── what is never cut: the instructions ──────────────────────────────────
    long_instructions = filler(BUDGET_BYTES, "INSTRUCTIONS-HEAD", "INSTRUCTIONS-TAIL", "Rule %05d: synthetic instruction text — проверка доставки.\n\n  indented continuation\n")
    chunks = write_instructions(long_instructions)
    turn("hello", label="instructions of exactly BudgetBytes, in %d files" % len(chunks))
    names, delivered = delivered_instructions(last_request()["system"])
    check("instructions of %d bytes in %d files arrive whole: the files' text, joined as it appears, is the original" % (BUDGET_BYTES, len(chunks)),
          delivered == long_instructions, "%d of %d bytes" % (len(delivered.encode()), BUDGET_BYTES))
    check("and in order", names == ["%s%03d.md" % (FILE_PREFIX, n) for n in range(1, len(chunks) + 1)], names)
    check("no piece is over %d bytes" % CHUNK_BYTES, all(len(c.encode()) <= CHUNK_BYTES for c in chunks))

    # Why the fleet refuses instead of sending: the CLI cuts and leaves out without failing.
    for old in RULES.glob(FILE_PREFIX + "*"):
        old.unlink()
    one_big = filler(24100, "INSTRUCTIONS-HEAD", "INSTRUCTIONS-TAIL", "Rule %05d: synthetic instruction text.\n")
    (RULES / (FILE_PREFIX + "001.md")).write_text(FRONT_MATTER + one_big)
    turn("hello", label="one rule file of 24,100 bytes")
    check("the CLI cuts one rule file over 24,000 bytes (why a piece is at most %d)" % CHUNK_BYTES,
          "INSTRUCTIONS-TAIL" not in last_request()["system"] and "<truncated" in last_request()["system"])
    too_much = filler(60100, "INSTRUCTIONS-HEAD", "INSTRUCTIONS-TAIL", "Rule %05d: synthetic instruction text.\n")
    chunks = write_instructions(too_much)
    turn("hello", label="rule files of 60,100 bytes together")
    names, _ = delivered_instructions(last_request()["system"])
    check("the CLI leaves a rule file out when all of them pass 60,000 bytes (why the budget is %d)" % BUDGET_BYTES,
          len(names) < len(chunks) and "exceeded the rules token budget" in last_request()["system"], names)
    write_instructions(instructions)

    # ── models ───────────────────────────────────────────────────────────────
    listed = subprocess.run(["agy", "--settings", FLAGS[-1], "models"], capture_output=True, text=True, env=environment()).stdout
    slugs = [line.split("\t")[0] for line in listed.splitlines() if "\t" in line]
    check("`agy models` lists the %d slugs the dashboard offers" % len(MODELS), slugs == MODELS, slugs)
    bad_model = turn("hello", model="no-such-model", label="a model the CLI does not know")
    check("an unknown model is an ERROR result before any conversation", bad_model["init"] is None and bad_model["status"] == "ERROR" and bad_model["exit"] != 0, bad_model["events"][:1])
    failing = turn("FAIL 500 please.", label="the Gemini API answers 500")
    check("a model error is an ERROR result with the API's message, exit non-zero",
          failing["status"] == "ERROR" and failing["exit"] != 0 and "Error 500" in json.dumps(failing["events"][-1]), failing["events"][-1:])

    # ── the key ──────────────────────────────────────────────────────────────
    posts = api_posts()
    check("the key is in X-Goog-Api-Key on every model request (%d) and nowhere else in them" % len(posts),
          len(posts) > 10 and all(p["key_in"] == ["X-Goog-Api-Key"] for p in posts), sorted({str(p["key_in"]) for p in posts}))
    leaked = subprocess.run(["grep", "-rlF", KEY, str(GEMINI), str(WORKSPACE), str(MCP_LOG), str(NET_LOG)], capture_output=True, text=True).stdout.split()
    check("the key is not written under ~/.gemini or the workspace, and no MCP server saw it", not leaked, leaked)
    no_key = turn("hello", env=environment(GEMINI_API_KEY=None), label="without GEMINI_API_KEY")
    check("without the key the CLI exits non-zero and names the variable", no_key["exit"] != 0 and "GEMINI_API_KEY" in (no_key["stderr"] + json.dumps(no_key["events"])), no_key["stderr"][:200])

    # ── the pin holds ────────────────────────────────────────────────────────
    time.sleep(6)
    reached = lines_of(NET_LOG)
    hosts = sorted({entry.get("dns") or entry.get("tls") or entry.get("http") or "?" for entry in reached})
    note("\nhosts the CLI looked up or connected to during all of the above, besides the stand-ins: " + ", ".join(hosts))
    check("with %s=true the updater host is never looked up or connected to" % DISABLE_AUTO_UPDATE, not any(UPDATER_HOST in h for h in hosts), hosts)
    logs = "".join(p.read_text(errors="replace") for p in (GEMINI / "antigravity-cli" / "log").glob("*.log"))
    check("and the CLI's log says why", "Auto-update disabled via environment variable " + DISABLE_AUTO_UPDATE in logs)
    check("the binary is unchanged after every run above", sha256_of(binary) == sha_before)

    # The control: without the switch the same watcher sees the updater host, so its silence above means something.
    NET_LOG.write_text("")
    turn("hello", env=environment(**{DISABLE_AUTO_UPDATE: None}), label="control: without the switch")
    time.sleep(8)
    hosts = sorted({entry.get("dns") or entry.get("tls") or "?" for entry in lines_of(NET_LOG)})
    check("control: without the switch the CLI goes to its updater host", any(UPDATER_HOST in h for h in hosts), hosts)
    check("the binary is unchanged after the control (the container has no network)", sha256_of(binary) == sha_before)

    (OUT / "transcript.txt").write_text("\n".join(transcript) + "\n")
    print("%d checks, %d failed" % (sum(1 for line in transcript if line.startswith(("PASS ", "FAIL "))), len(failures)))
    return 1 if failures else 0


if __name__ == "__main__":
    code = 2
    try:
        code = main()
    finally:
        try:
            (OUT / "transcript.txt").write_text("\n".join(transcript) + "\n")
        except OSError:
            pass
        sys.stdout.flush()
        # The stand-ins are children that never end; leave without waiting for them.
        os._exit(code)
