# Platform support

| Platform | Provider | Status |
|----------|----------|--------|
| macOS (Apple silicon) | Claude | ✅ Tested end-to-end |
| Linux | Claude / Codex | ✅ The maintainer's fleet runs on Linux (x86-64) |
| Windows | Claude / Codex | ⚠️ Docker Desktop + WSL2 is the intended path. Unverified |
| Any | Codex | ⚠️ Code paths ship in `seed.example.json`, but Claude has seen far more wall-clock time in real workflows |
| Any | Codex + GLM (Z.ai GLM Coding Plan) | ⚠️ A `zai/` model prefix runs GLM on your Coding Plan subscription through a loopback forwarder inside the agent. Subscriber-only use. The merged implementation passed exact-head canary acceptance. See `docs/providers/codex-hosted-models.md` |
| Any | Claude or Codex + local model (e.g. Ollama) | ⚠️ Set **Runs on → Local server** per agent, with the server URL, model tag and context window. Claude local runs with no Claude credential in the container and passed exact-head canary acceptance. Codex local still holds the OpenAI credential until #383. See `docs/providers/local-models.md` |
| Any | Gemini | ⚠️ Supported via `gemini` CLI headless mode. Known trade-offs vs claude/codex: (1) no session persistence in headless mode — system prompt is re-sent on every task, so per-task token cost is higher; (2) PDFs are not passed as native content blocks — agent reads from disk via `@`-reference hints; (3) HTTP/SSE MCP transport only — stdio MCP servers are filtered out; (4) OAuth-only, no API key fallback — personal Google account required. See `docs/providers/gemini.md` for full details. |

If you run Phleet on Windows, on a Linux host, or with Codex or Gemini as the primary provider and hit something broken — PRs and issue reports are very welcome. Small fixes and "it works on my box" confirmations are just as valuable as new features here.
