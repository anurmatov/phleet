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
