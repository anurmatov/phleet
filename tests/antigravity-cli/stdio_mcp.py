"""A minimal stdio MCP server with one tool, ping, for the Antigravity CLI harness."""
import json
import sys

ANSWERS = {
    "tools/list": {"tools": [{"name": "ping", "description": "Answers pong.",
                              "inputSchema": {"type": "object", "properties": {}}}]},
    "tools/call": {"content": [{"type": "text", "text": "pong from the stdio server"}]},
}

for line in sys.stdin:
    message = json.loads(line)
    if "id" not in message:
        continue
    if message["method"] == "initialize":
        result = {"protocolVersion": message.get("params", {}).get("protocolVersion", "2025-03-26"),
                  "capabilities": {"tools": {}}, "serverInfo": {"name": "stdio", "version": "1"}}
    else:
        result = ANSWERS.get(message["method"], {})
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": result}) + "\n")
    sys.stdout.flush()
