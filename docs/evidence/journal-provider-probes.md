# Journal provider CLI probes — 2026-10-02

This is pinned-CLI transport evidence over the real Comms journal auth, binding
and MCP tools with a synthetic in-memory read store. It is not AC13/XAC9,
a real database/bucket acceptance run, or a production grant.

## Codex 0.159.2 — pass

`codex --version` → `codex-cli 0.159.2`.
The CLI used an isolated config with the production translator’s `url`,
`http_headers.Authorization` and `enabled_tools` shape. The fixture’s token was
read-purpose, subject `agent1`, and is redacted below. No production config,
endpoint or tool grant was changed.

```toml
[mcp_servers.fleet-comms-journal]
url = "http://127.0.0.1:8094/journal/v1/mcp"
http_headers = { Authorization = "Bearer <fixture-read-token>" }
enabled_tools = ["get_message", "search_messages", "get_conversation"]
```

The actual `codex exec --json --skip-git-repo-check` turn asked for two sequential
`get_message(telegram_message_id=5)` calls. The fixture unbound the subject after
the first response. Exit status: `0`.

Captured CLI tool events (unmodified synthetic results):

```json
{"type":"item.completed","item":{"id":"item_1","type":"mcp_tool_call","server":"fleet-comms-journal","tool":"get_message","arguments":{"telegram_message_id":5},"result":{"content":[{"type":"text","text":"{\"message_id\":\"01M3Y7F6RXFRWXTD8KYH9N13SQ\",\"conversation\":{\"id\":\"01ARZ3NDEKTSV4RRFFQ69G5FAV\",\"chat_kind\":\"private\",\"telegram_chat_id\":10,\"title\":null},\"telegram_message_id\":5,\"reply_to\":null,\"media_group_id\":null,\"direction\":\"inbound\",\"sender\":{\"kind\":\"human\",\"id\":\"u_1\",\"display\":null},\"sent_at\":\"2026-10-02T11:54:52.061461Z\",\"recorded_at\":\"2026-10-02T11:54:52.061461Z\",\"text\":\"synthetic sentinel\",\"text_format\":\"plain\",\"transcript\":null,\"transcript_truncated\":false,\"origin\":\"telegram_update\",\"delivery_state\":\"received\",\"send_group\":null,\"attachments\":[]}"}],"structured_content":null},"error":null,"status":"completed"}}
{"type":"item.completed","item":{"id":"item_2","type":"mcp_tool_call","server":"fleet-comms-journal","tool":"get_message","arguments":{"telegram_message_id":5},"result":{"content":[{"type":"text","text":"{\"error\":\"unavailable\",\"reason\":\"no_bound_conversation\"}"}],"structured_content":null},"error":null,"status":"failed"}}
```

Authenticated Comms request trace (the bearer itself was never logged):

```json
{"http":"POST","path":"/journal/v1/mcp","rpc":"notifications/initialized","readHeader":true,"status":202}
{"http":"POST","path":"/journal/v1/mcp","rpc":"tools/list","readHeader":true,"status":200}
{"http":"POST","path":"/journal/v1/mcp","rpc":"tools/call","readHeader":true,"status":200}
{"http":"POST","path":"/journal/v1/mcp","rpc":"tools/call","readHeader":true,"status":200}
{"http":"POST","path":"/journal/v1/mcp","rpc":"initialize","readHeader":true,"status":200}
{"http":"POST","path":"/journal/v1/mcp","rpc":"notifications/initialized","readHeader":true,"status":202}
{"http":"POST","path":"/journal/v1/mcp","rpc":"ping","readHeader":true,"status":200}
```

`tools/list` returned HTTP 200, the bound lookup returned the seeded record, and
the unbound lookup returned `unavailable:no_bound_conversation`. Every observed
MCP request carried the expected read bearer; Comms status reported
`rejectedSinceStart={}` after the run, so no unauthenticated request was omitted
by the post-auth trace. This is the evidence for changing only Codex’s compiled
header-support flag to true.

## Gemini 0.40.1 — failing proof, flag remains false

`gemini --version` → `0.40.1`.
The isolated config used the existing journal translator’s `url` and
`headers.Authorization` shape, with `oauth-personal` selected. No Gemini OAuth
credential is present in this development container and no login was attempted.
`gemini mcp list` exited `0` with no output, which is not a passing transcript.
The actual headless `gemini -p ... --yolo --output-format stream-json` probe
exited `41` before a model turn:

```text
YOLO mode is enabled. All tool calls will be automatically approved.
Error authenticating: FatalAuthenticationError: Manual authorization is required
but the current session is non-interactive.
```

Bound/unbound model calls and the every-request header proof are therefore
unproven for Gemini. This is a credential-blocked proof attempt, not evidence
that the CLI cannot carry headers. Its compiled flag remains false; an operator
with an already-authorized isolated Gemini credential must rerun the full probe
before changing it. This does not waive the separate exact-head acceptance gate.

The new headerless `fleet-journal-files` server uses `httpUrl` only on Gemini:
that explicitly selects streamable HTTP in the pinned CLI. Existing server
translations are unchanged. EntrypointMcpHeaderTests pins this exception and
Codex’s `enabled_tools=["fetch_attachment"]` entry.

## Claude 2.1.280 — initial credential-blocked attempt

`claude --version` → `2.1.280 (Claude Code)`.
The actual pinned CLI used `--strict-mcp-config`, an HTTP server with
`headers.Authorization` containing the fixture read token, and
`--allowedTools mcp__fleet-comms-journal__get_message`. The prompt requested
`get_message(telegram_message_id=5)` twice sequentially against the same
bound-then-unbound fixture used above. No login or production change was made.

The CLI's stream-json initialization reported the journal server `connected`
and exposed `get_message`, `search_messages` and `get_conversation`.
Selected fields from the actual transcript (session identifiers and unrelated
CLI configuration omitted):

```json
{"type":"system","subtype":"init","claude_code_version":"2.1.280","apiKeySource":"none","mcp_servers":[{"name":"fleet-comms-journal","status":"connected","source":"dynamic"}]}
{"type":"assistant","error":"authentication_failed","is_api_error_message":true,"message":{"content":[{"type":"text","text":"Not logged in · Please run /login"}]}}
{"type":"result","is_error":true,"num_turns":1,"result":"Not logged in · Please run /login","terminal_reason":"api_error"}
```

Exit status: `1`. Authenticated Comms trace from this actual CLI run (only
bearer equality was logged, never the token):

```json
{"http":"POST","path":"/journal/v1/mcp","rpc":"server/discover","readHeader":true,"status":200}
{"http":"POST","path":"/journal/v1/mcp","rpc":"initialize","readHeader":true,"status":200}
{"http":"POST","path":"/journal/v1/mcp","rpc":"notifications/initialized","readHeader":true,"status":202}
{"http":"POST","path":"/journal/v1/mcp","rpc":"tools/list","readHeader":true,"status":200}
```

Every observed request carried the expected read bearer and `tools/list`
succeeded, but neither requested `tools/call` happened because the container
had no authorized Claude credential. This initial attempt did not pass AC8
and is retained as failed evidence; the later operator result is below.
No provider flag changed during this attempt.

## Claude 2.1.280 — authorized operator proof passed

At final implementation head `a79b89e6`, the actual pinned CLI used an
already-authorized credential mounted read-only in place, with no credential
copy or account login. Against the actual Comms auth/binding/MCP application
and a synthetic in-memory read store, two sequential
`get_message(telegram_message_id=5)` calls returned the bound synthetic record
and then `unavailable:no_bound_conversation`; the CLI exited `0`.

All six observed MCP requests carried the expected read bearer, including both
lookups. [Operator receipt](https://github.com/anurmatov/phleet/pull/402#issuecomment-5953201389).
This closes the Claude AC8 transport proof only, not real-provider attachment
reads, database/bucket acceptance or AC13/XAC9. Gemini remains credential-blocked
and its compiled header-support flag remains false.
