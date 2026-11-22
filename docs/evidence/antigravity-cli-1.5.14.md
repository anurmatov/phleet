# Antigravity CLI 1.5.14: what the pinned binary does (2026-11-22)

This is the record that lets `gemini` agents be provisioned again: `McpHeaderSupport["gemini"]` is
true because of the MCP section below. It is the output of `bash tests/antigravity-cli/run.sh` on
the commit that added it, 45 checks, all passing.

**What it is.** The real pinned binary: `agy` 1.5.14, Linux arm64, release archive
`agy_cli_linux_arm64.tar.gz` with sha256
`e5f69a3123fac66974b303d9a201f381a0fb32b3ef869d9139da85d5caf67a2a`. The x64 archive, pinned as
`d58386322d2073e96b33f7cc32b4e9ae2db6228f58440369725967b2de48c7b5`, passed the same 45 checks
under emulation on the same arm64 machine (`PLATFORM=linux/amd64`); it has not run on x86-64
hardware outside CI. It ran
in a container started with `--network none`, driven the way `GeminiExecutor` drives it, with the
MCP file written by the agent's own `prepare config`.

**What it is not.** No real model: a scripted stand-in (`tests/antigravity-cli/fake_gemini.py`)
answered for the Gemini API, so nothing here says how a Gemini model uses the CLI's tools or
prompts. No MCP server of the fleet: a recording server (`mcp_recorder.py`) that answers 401 to
any request without its header, as the fleet's servers do. No Google account and no real key; the
key is a synthetic string. No network, so nothing here shows what the CLI's own calls
(`play.googleapis.com`, its updater) carry or receive, only that it tries to make them. Not a
deployment and not an acceptance of a running agent.

The same 45 checks, and one more for the image's own `prepare config`, also passed inside an agent
image built from this commit's `Dockerfile` (`AGENT_IMAGE=… bash tests/antigravity-cli/run.sh`):

```
$ dotnet /app/Fleet.Agent.dll prepare config
  Gemini MCP: wrote 2 server(s) to /root/.gemini/config/mcp_config.json
PASS the image's `prepare config` wrote the MCP file
PASS the image's `prepare config` wrote the MCP file
```

## What the fleet relies on, and where it is in the transcript

| Reliance | Check |
|---|---|
| `init` names the conversation before the CLI reads any input | "init is printed before the CLI has read any input" |
| The key goes out in `X-Goog-Api-Key` only | "the key is in X-Goog-Api-Key on every model request … and nowhere else" |
| `--conversation <id>` continues in a new process; an unknown id becomes another conversation, said in `init` | the three "conversation" checks and the two "unknown conversation id" checks |
| A killed process leaves a conversation that continues | "the conversation continues after a kill" |
| The model's shell and write tools run without approval | `run_command`, `write_file` |
| Every MCP request carries the configured header; OAuth discovery gets 401 and nothing follows | the "MCP server saw" block |
| A message of 190,000 bytes arrives whole; over 192,000 the CLI cuts it | the two "message" checks |
| Instructions of 56,000 bytes in rule files arrive whole and in order; the CLI cuts a file over 24,000 bytes and leaves files out past 60,000 | the six "instructions" and "rule file" checks |
| With `AGY_CLI_DISABLE_AUTO_UPDATE=true` the updater host is never contacted and the binary does not change; without it, it is contacted | the last six checks |

## Measured before the harness existed, and not repeated in it

These were measured the same way (same binary, no network, the same stand-ins) while the design
was chosen, and are stated in `docs/providers/gemini.md`:

- A prompt cannot be an argument (128 KB per argument), `-p -` and `-p ""` do not read the input,
  and plain text on the input is cut at the same 192,000 bytes as a stream-json message.
- The message limit counts bytes, not characters: ASCII, Cyrillic, emoji and dense punctuation of
  191,500 bytes all arrived whole and of 192,500 bytes all were cut.
- The rule limits count bytes too: 59,959 bytes of rules arrived whole and 60,030 did not, in ASCII
  and in Cyrillic; one file of 23,891 bytes arrived whole and one of 24,033 did not.
- When rules pass the limit the CLI leaves out the largest files first, whoever wrote them: with
  58,000 bytes of ours in 20,000-byte files and two 5,000-byte files of others, one of ours was
  left out. That is why other rule files are counted before a turn starts.
- A rule file in `~/.gemini/config/rules/` or `<work dir>/.agents/rules/` is loaded only with
  `trigger: always_on` front matter; `GEMINI.md` and `AGENTS.md` in `~/.gemini`, `~/.gemini/config`
  and the work directory are loaded as they are.
- Without `--dangerously-skip-permissions` a shell command and a write inside the workspace are
  denied (exit 0, `denied_actions` in the result); with `--sandbox` shell commands fail with
  `fork/exec … operation not permitted`; `--mode auto` is "not available for this account".
- `AGY_CLI_DISABLE_AUTO_UPDATE=1` does not switch the updater off; only `true` does. No directory
  permission stops it for root. `--settings` rejects a key it does not know, and of about 7,000
  names taken from the binary the 39 it accepts hold nothing about updates or telemetry.
- A wrong MCP header value makes the CLI drop that server silently (no `call_mcp_tool`, exit 0).

## Transcript

Events are shortened at 420 characters and the tool list of `init` is elided; nothing else is
edited. The conversation ids are the CLI's own random ids.

```
agy --version: 1.5.14   (binary sha256 702b888abddc7d482ee30592fa153cda5785afae3188179d38b511b6ec360286)
PASS the binary is the pinned release 1.5.14
PASS the binary's directory is writable by this user, so only the switch keeps it from updating itself
~/.gemini/config/mcp_config.json:
  {
    "mcpServers": {
      "recorder": {
        "url": "http://127.0.0.1:18911/mcp",
        "headers": {
          "Authorization": "Bearer <token>"
        }
      },
      "local": {
        "command": "python3",
        "args": [
          "/harness/stdio_mcp.py"
        ]
      }
    }
  }
PASS the MCP file is for its owner only
PASS no settings file is needed

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --model gemini-4-argon-medium   # a turn in a new conversation
  stdin (after init): {"event": "user", "message": {"content": "First question."}}
  {"event": "init", "conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "init": {"model": "gemini-4-argon-medium", "cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.020209959, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.067727459, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.7 s)
PASS a turn completes: init, then the message, then a SUCCESS result, exit 0
PASS init is printed before the CLI has read any input, and before any model request
PASS one request to the Gemini API's streamGenerateContent, model taken from the slug
PASS the result carries token counts
PASS the instructions are in the system instruction, inside the CLI's own prompt

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --conversation ba8dc616-6182-41cb-9818-1abb1af7cbb4   # slash commands are off
  stdin (after init): {"event": "user", "message": {"content": "/help"}}
  {"event": "init", "conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 2, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 3, "state": "DONE", "step_type": "system_message", "duration_seconds": 0.000188208}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 4, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 3.\n", "duration_seconds": 0.024250833, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "status": "SUCCESS", "response": "Scripted reply 3.\n", "duration_seconds": 0.741204834, "num_turns": 2, "usage": {"input_tokens": 1600, "output_tokens": 74, "thinking_tokens": 14, "cache_read_tokens": 800, "total_tokens": 1674}}}
  exit: 0   (0.7 s)
PASS a message that starts with a slash is a message

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --conversation ba8dc616-6182-41cb-9818-1abb1af7cbb4   # a new process continues the conversation
  stdin (after init): {"event": "user", "message": {"content": "Second question."}}
  {"event": "init", "conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 5, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 6, "state": "DONE", "step_type": "system_message", "duration_seconds": 0.000351792}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 7, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 5.\n", "duration_seconds": 0.029647583, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "status": "SUCCESS", "response": "Scripted reply 5.\n", "duration_seconds": 1.373525709, "num_turns": 3, "usage": {"input_tokens": 2400, "output_tokens": 111, "thinking_tokens": 21, "cache_read_tokens": 1200, "total_tokens": 2511}}}
  exit: 0   (0.6 s)
PASS --conversation <id> continues that conversation in a new process
PASS the earlier turns are sent again
PASS a resumed conversation gets the instructions as they are now (so the fleet starts a new one when they change)
PASS the result counts the whole conversation

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --conversation 00000000-0000-4000-8000-000000000000   # a conversation the CLI does not have
  stdin (after init): {"event": "user", "message": {"content": "Lost question."}}
  {"event": "init", "conversation_id": "9cfb31af-ef6a-4519-bd74-063548c9f40e", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "9cfb31af-ef6a-4519-bd74-063548c9f40e", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "9cfb31af-ef6a-4519-bd74-063548c9f40e", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.017797917, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "9cfb31af-ef6a-4519-bd74-063548c9f40e", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.050516334, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  stderr: warning: conversation "00000000-0000-4000-8000-000000000000" not found
  exit: 0   (0.6 s)
PASS an unknown conversation id does not fail: init names another conversation, before any input
PASS and the CLI says so on stderr
PASS the message then written is the first of that new conversation

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --conversation ba8dc616-6182-41cb-9818-1abb1af7cbb4   # killed with SIGKILL after 4 s
  stdin (after init): {"event": "user", "message": {"content": "SLOW answer, killed half way."}}
  {"event": "init", "conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 8, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 9, "state": "DONE", "step_type": "system_message", "duration_seconds": 0.00017675}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 10, "state": "ACTIVE", "step_type": "agent_response", "text_delta": "slow 0 "}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 10, "state": "ACTIVE", "step_type": "agent_response", "text_delta": "slow 1 "}}
  … 6 more text deltas of the same shape, then the process was killed
  exit: -9   (4.6 s)
PASS a process killed mid-answer was mid-answer

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --conversation ba8dc616-6182-41cb-9818-1abb1af7cbb4   # after the kill
  stdin (after init): {"event": "user", "message": {"content": "Third question."}}
  {"event": "init", "conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 11, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 12, "state": "DONE", "step_type": "system_message", "duration_seconds": 0.000302791}}
  {"event": "step_update", "step_update": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "step_index": 13, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 9.\n", "duration_seconds": 0.021079542, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "ba8dc616-6182-41cb-9818-1abb1af7cbb4", "status": "SUCCESS", "response": "Scripted reply 9.\n", "duration_seconds": 7.315349254, "num_turns": 5, "usage": {"input_tokens": 3200, "output_tokens": 148, "thinking_tokens": 28, "cache_read_tokens": 1600, "total_tokens": 3348}}}
  exit: 0   (0.7 s)
PASS the conversation continues after a kill

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # the model's shell tool
  stdin (after init): {"event": "user", "message": {"content": "CALL run_command {\"CommandLine\":\"id -un > /workspace/shell-proof.txt; echo outside > /tmp/outside-proof.txt; echo SHELL-RAN-$((6*7))\",\"toolSummary\":\"Shell proof\",\"WaitMsBeforeAsync\":5000}"}}
  {"event": "init", "conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "step_index": 1, "state": "DONE", "step_type": "agent_response", "duration_seconds": 0.018174917, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "step_update", "step_update": {"conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "step_index": 2, "state": "ACTIVE", "step_type": "tool", "tool_name": "run_command", "tool_info": {"name": "run_command", "parameters": {"CommandLine": "id -un > /workspace/shell-proof.txt; echo outside > /tmp/outside-proof.txt; echo SHELL-RAN-$((6*7))"}}}}
  {"event": "step_update", "step_update": {"conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "step_index": 2, "state": "DONE", "step_type": "tool", "tool_name": "run_command", "duration_seconds": 0.019090042, "tool_info": {"name": "run_command", "parameters": {"CommandLine": "id -un > /workspace/shell-proof.txt; echo outside > /tmp/outside-proof.txt; echo SHELL-RAN-$((6*7))"}, "output": "Created At: 2026-11-22…
  {"event": "step_update", "step_update": {"conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "step_index": 3, "state": "DONE", "step_type": "agent_response", "text_delta": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:31Z\\nCompleted At: 2026-11-22T14:58:31Z\\nCommand exited with code 0.\\n\\nCommand output:\\nSHELL-RAN-42\\r\\n\"}\n", "duration_seconds": 0.023099083, "usage": {"input_tokens": 800, "…
  {"event": "result", "result": {"conversation_id": "50ed1e3c-b8f8-473a-8366-fc06500ef663", "status": "SUCCESS", "response": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:31Z\\nCompleted At: 2026-11-22T14:58:31Z\\nCommand exited with code 0.\\n\\nCommand output:\\nSHELL-RAN-42\\r\\n\"}\n", "duration_seconds": 0.09003725, "num_turns": 1, "usage": {"input_tokens": 1600, "output_tokens": 74, "thinking_tokens": 1…
  exit: 0   (0.6 s)
PASS run_command runs in the container, with no approval and no sandbox refusal
PASS a tool step is ACTIVE with its name and parameters, then DONE with its output

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # the model's write tool
  stdin (after init): {"event": "user", "message": {"content": "CALL write_file {\"Path\":\"/workspace/write-proof.txt\",\"Content\":\"WRITE-RAN\\n\"}"}}
  {"event": "init", "conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "step_index": 1, "state": "DONE", "step_type": "agent_response", "duration_seconds": 0.018544375, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "step_update", "step_update": {"conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "step_index": 2, "state": "ACTIVE", "step_type": "tool", "tool_name": "write_file", "tool_info": {"name": "write_file", "parameters": {"Path": "/workspace/write-proof.txt"}}}}
  {"event": "step_update", "step_update": {"conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "step_index": 2, "state": "DONE", "step_type": "tool", "tool_name": "write_file", "duration_seconds": 0.004706625, "tool_info": {"name": "write_file", "parameters": {"Path": "/workspace/write-proof.txt"}}}}
  {"event": "step_update", "step_update": {"conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "step_index": 3, "state": "DONE", "step_type": "agent_response", "text_delta": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:32Z\\nCompleted At: 2026-11-22T14:58:32Z\\nCreated file.\"}\n", "duration_seconds": 0.019581083, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_to…
  {"event": "result", "result": {"conversation_id": "4ab1e8c7-4c88-4cbe-b0fa-b24f38445096", "status": "SUCCESS", "response": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:32Z\\nCompleted At: 2026-11-22T14:58:32Z\\nCreated file.\"}\n", "duration_seconds": 0.077326625, "num_turns": 1, "usage": {"input_tokens": 1600, "output_tokens": 74, "thinking_tokens": 14, "cache_read_tokens": 800, "total_tokens": 1674}}}
  exit: 0   (0.7 s)
PASS write_file writes in the workspace

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # an MCP tool over HTTP
  stdin (after init): {"event": "user", "message": {"content": "CALL call_mcp_tool {\"ServerName\":\"recorder\",\"ToolName\":\"get_message\",\"Arguments\":{\"id\":7}}"}}
  {"event": "init", "conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "step_index": 1, "state": "DONE", "step_type": "agent_response", "duration_seconds": 0.016800708, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "step_update", "step_update": {"conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "step_index": 2, "state": "ACTIVE", "step_type": "tool", "tool_name": "call_mcp_tool", "tool_info": {"name": "call_mcp_tool", "parameters": {"Arguments": {"id": 7}, "ServerName": "recorder", "ToolName": "get_message"}}}}
  {"event": "step_update", "step_update": {"conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "step_index": 2, "state": "DONE", "step_type": "tool", "tool_name": "call_mcp_tool", "duration_seconds": 0.009263292, "tool_info": {"name": "call_mcp_tool", "parameters": {"Arguments": {"id": 7}, "ServerName": "recorder", "ToolName": "get_message"}, "output": "stored message 7 says: the heron lands at dusk"}}}
  {"event": "step_update", "step_update": {"conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "step_index": 3, "state": "DONE", "step_type": "agent_response", "text_delta": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:33Z\\nCompleted At: 2026-11-22T14:58:33Z\\nstored message 7 says: the heron lands at dusk\"}\n", "duration_seconds": 0.018473834, "usage": {"input_tokens": 800, "output_tokens": 37, "th…
  {"event": "result", "result": {"conversation_id": "e2157db6-2cd5-4e93-90ef-95f8567fc5b5", "status": "SUCCESS", "response": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:33Z\\nCompleted At: 2026-11-22T14:58:33Z\\nstored message 7 says: the heron lands at dusk\"}\n", "duration_seconds": 0.077216209, "num_turns": 1, "usage": {"input_tokens": 1600, "output_tokens": 74, "thinking_tokens": 14, "cache_read_tokens"…
  exit: 0   (0.7 s)
  MCP server saw:
    {"http": "POST", "path": "/mcp", "method": "server/discover", "auth": "ok"}
    {"http": "POST", "path": "/mcp", "method": "initialize", "auth": "ok"}
    {"http": "GET", "path": "/mcp", "auth": "ok", "status": 405}
    {"http": "POST", "path": "/mcp", "method": "notifications/initialized", "auth": "ok"}
    {"http": "GET", "path": "/.well-known/oauth-protected-resource/mcp", "auth": "missing", "status": 401}
    {"http": "GET", "path": "/.well-known/oauth-protected-resource", "auth": "missing", "status": 401}
    {"http": "POST", "path": "/mcp", "method": "tools/list", "auth": "ok"}
    {"http": "POST", "path": "/mcp", "method": "notifications/roots/list_changed", "auth": "ok"}
    {"http": "POST", "path": "/mcp", "method": "notifications/roots/list_changed", "auth": "ok"}
    {"http": "POST", "path": "/mcp", "method": "tools/call", "auth": "ok", "tool": "get_message", "args": {"id": 7}}
PASS the MCP tool is listed and called
PASS its answer reaches the model
PASS every MCP request carries the configured header (8 requests: GET, initialize, notifications/initialized, notifications/roots/list_changed, server/discover, tools/call, tools/list)
PASS the OAuth discovery requests carry no header, get 401, and nothing follows from that

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # an MCP tool over stdio
  stdin (after init): {"event": "user", "message": {"content": "CALL call_mcp_tool {\"ServerName\":\"local\",\"ToolName\":\"ping\",\"Arguments\":{}}"}}
  {"event": "init", "conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "step_index": 1, "state": "DONE", "step_type": "agent_response", "duration_seconds": 0.017743916, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "step_update", "step_update": {"conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "step_index": 2, "state": "ACTIVE", "step_type": "tool", "tool_name": "call_mcp_tool", "tool_info": {"name": "call_mcp_tool", "parameters": {"Arguments": {}, "ServerName": "local", "ToolName": "ping"}}}}
  {"event": "step_update", "step_update": {"conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "step_index": 2, "state": "DONE", "step_type": "tool", "tool_name": "call_mcp_tool", "duration_seconds": 0.005429625, "tool_info": {"name": "call_mcp_tool", "parameters": {"Arguments": {}, "ServerName": "local", "ToolName": "ping"}, "output": "pong from the stdio server"}}}
  {"event": "step_update", "step_update": {"conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "step_index": 3, "state": "DONE", "step_type": "agent_response", "text_delta": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:33Z\\nCompleted At: 2026-11-22T14:58:33Z\\npong from the stdio server\"}\n", "duration_seconds": 0.020413375, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "…
  {"event": "result", "result": {"conversation_id": "91f04393-bbae-4949-b377-d17ea0a722a6", "status": "SUCCESS", "response": "Tool said: {\"output\": \"Created At: 2026-11-22T14:58:33Z\\nCompleted At: 2026-11-22T14:58:33Z\\npong from the stdio server\"}\n", "duration_seconds": 0.074947, "num_turns": 1, "usage": {"input_tokens": 1600, "output_tokens": 74, "thinking_tokens": 14, "cache_read_tokens": 800, "total_tokens": …
  exit: 0   (0.6 s)
PASS a stdio MCP server works beside it

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # a message of exactly MaxPromptBytes
  stdin (after init): {"event": "user", "message": {"content": "MESSAGE-HEAD\nline 000000 of a synthetic message, with кириллица and 🙂.\nline 0000… (190000 bytes)"}}
  {"event": "init", "conversation_id": "5b7e9abd-ad24-4904-ad08-32ddd232475b", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "5b7e9abd-ad24-4904-ad08-32ddd232475b", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "5b7e9abd-ad24-4904-ad08-32ddd232475b", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.039712541, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "5b7e9abd-ad24-4904-ad08-32ddd232475b", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.076204166, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.7 s)
PASS a message of 190000 bytes arrives whole

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # a message over the CLI's own limit
  stdin (after init): {"event": "user", "message": {"content": "MESSAGE-HEAD\nline 000000 of a synthetic message.\nline 000001 of a synthetic mess… (192100 bytes)"}}
  {"event": "init", "conversation_id": "fed64e78-e8c6-43bd-82d9-c258cef1ecb9", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "fed64e78-e8c6-43bd-82d9-c258cef1ecb9", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "fed64e78-e8c6-43bd-82d9-c258cef1ecb9", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.030556167, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "fed64e78-e8c6-43bd-82d9-c258cef1ecb9", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.068237708, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.6 s)
PASS the CLI cuts a message over its limit without failing (why the fleet refuses one over 190000)

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # instructions of exactly BudgetBytes, in 3 files
  stdin (after init): {"event": "user", "message": {"content": "hello"}}
  {"event": "init", "conversation_id": "896c584b-84e9-4e93-aaa3-c226ef5fa9a9", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "896c584b-84e9-4e93-aaa3-c226ef5fa9a9", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "896c584b-84e9-4e93-aaa3-c226ef5fa9a9", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.021290708, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "896c584b-84e9-4e93-aaa3-c226ef5fa9a9", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.052396, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.6 s)
PASS instructions of 56000 bytes in 3 files arrive whole: the files' text, joined as it appears, is the original
PASS and in order
PASS no piece is over 20000 bytes

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # one rule file of 24,100 bytes
  stdin (after init): {"event": "user", "message": {"content": "hello"}}
  {"event": "init", "conversation_id": "f5142285-9541-43f3-86bf-b252d1ae0c8f", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "f5142285-9541-43f3-86bf-b252d1ae0c8f", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "f5142285-9541-43f3-86bf-b252d1ae0c8f", "step_index": 1, "state": "ACTIVE", "step_type": "agent_response", "text_delta": "Scripted rep"}}
  {"event": "step_update", "step_update": {"conversation_id": "f5142285-9541-43f3-86bf-b252d1ae0c8f", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "ly 1.\n", "duration_seconds": 0.018993, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "f5142285-9541-43f3-86bf-b252d1ae0c8f", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.052423792, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.6 s)
PASS the CLI cuts one rule file over 24,000 bytes (why a piece is at most 20000)

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # rule files of 60,100 bytes together
  stdin (after init): {"event": "user", "message": {"content": "hello"}}
  {"event": "init", "conversation_id": "f80a3c57-3f77-456a-b1b5-9119dc4912d4", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "f80a3c57-3f77-456a-b1b5-9119dc4912d4", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "f80a3c57-3f77-456a-b1b5-9119dc4912d4", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.020050334, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "f80a3c57-3f77-456a-b1b5-9119dc4912d4", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.049148459, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.6 s)
PASS the CLI leaves a rule file out when all of them pass 60,000 bytes (why the budget is 56000)
PASS `agy models` lists the 25 slugs the dashboard offers

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' --model no-such-model   # a model the CLI does not know
  stdin: nothing was written (no init)
  {"event": "result", "result": {"conversation_id": "", "status": "ERROR", "response": "", "error": "invalid model selection (--model \"no-such-model\" --effort \"\"): model no-such-model is not recognized as a known model or custom model in settings\nAvailable models:\n  Gemini 4 Neon (High)\n  Gemini 4 Neon (XHigh)\n  Gemini 4 Neon (Medium)\n  Gemini 4 Neon (Low)\n  Gemini 4 Carbon (High)\n  Gemini 4 Carbon (Medium)\…
  stderr: error: invalid model selection (--model "no-such-model" --effort ""): model no-such-model is not recognized as a known model or custom model in settings
  stderr: Available models:
  stderr:   Gemini 4 Neon (High)
  stderr:   Gemini 4 Neon (XHigh)
  stderr:   Gemini 4 Neon (Medium)
  stderr:   Gemini 4 Neon (Low)
  exit: 1   (0.5 s)
PASS an unknown model is an ERROR result before any conversation

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # the Gemini API answers 500
  stdin (after init): {"event": "user", "message": {"content": "FAIL 500 please."}}
  {"event": "init", "conversation_id": "db3f7f36-bfdc-48ed-bfbb-f219dd818961", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "db3f7f36-bfdc-48ed-bfbb-f219dd818961", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "db3f7f36-bfdc-48ed-bfbb-f219dd818961", "step_index": 1, "state": "DONE", "step_type": "error_message"}}
  {"event": "result", "result": {"conversation_id": "db3f7f36-bfdc-48ed-bfbb-f219dd818961", "status": "ERROR", "response": "", "error": "agent executor error: generating and executing: Error 500, Message: scripted failure, Status: INTERNAL, Details: []", "duration_seconds": 0, "num_turns": 1, "usage": {"input_tokens": 0, "output_tokens": 0, "thinking_tokens": 0, "cache_read_tokens": 0, "total_tokens": 0}, "finished_rea…
  stderr: error: agent executor error: generating and executing: Error 500, Message: scripted failure, Status: INTERNAL, Details: []
  stderr: AGY_ERROR: {"short_error":"agent executor error: generating and executing: Error 500, Message: scripted failure, Status: INTERNAL, Details: []","status":"INTERNAL","error_code":500,"code_kind":"http","retryable":false,"error_id":"6dbd4466-96cf-44a0-b8e6-ad7ab17a42b5-1"}
  exit: 3   (0.6 s)
PASS a model error is an ERROR result with the API's message, exit non-zero
PASS the key is in X-Goog-Api-Key on every model request (20) and nowhere else in them
PASS the key is not written under ~/.gemini or the workspace, and no MCP server saw it

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # without GEMINI_API_KEY
  stdin: nothing was written (no init)
  stderr: modelProvider is set to "gemini" in settings.json, but the GEMINI_API_KEY environment variable is not set. Set GEMINI_API_KEY to your Gemini API key, or remove "modelProvider" from settings.json to use the default backend.
  exit: 1   (0.3 s)
PASS without the key the CLI exits non-zero and names the variable

hosts the CLI looked up or connected to during all of the above, besides the stand-ins: play.googleapis.com
PASS with AGY_CLI_DISABLE_AUTO_UPDATE=true the updater host is never looked up or connected to
PASS and the CLI's log says why
PASS the binary is unchanged after every run above

$ agy --output-format stream-json --input-format stream-json --disable-slash-commands --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}'   # control: without the switch
  stdin (after init): {"event": "user", "message": {"content": "hello"}}
  {"event": "init", "conversation_id": "fb618c40-f18c-4c2c-a28c-970324284ac6", "init": {"cwd": "/workspace", "tools": […], "permission_mode": "always-proceed"}}
  {"event": "step_update", "step_update": {"conversation_id": "fb618c40-f18c-4c2c-a28c-970324284ac6", "step_index": 0, "state": "DONE", "step_type": "user_input"}}
  {"event": "step_update", "step_update": {"conversation_id": "fb618c40-f18c-4c2c-a28c-970324284ac6", "step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Scripted reply 1.\n", "duration_seconds": 0.024924833, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  {"event": "result", "result": {"conversation_id": "fb618c40-f18c-4c2c-a28c-970324284ac6", "status": "SUCCESS", "response": "Scripted reply 1.\n", "duration_seconds": 0.058563958, "num_turns": 1, "usage": {"input_tokens": 800, "output_tokens": 37, "thinking_tokens": 7, "cache_read_tokens": 400, "total_tokens": 837}}}
  exit: 0   (0.6 s)
PASS control: without the switch the CLI goes to its updater host
PASS the binary is unchanged after the control (the container has no network)
```
