# Platform support

| Platform | Provider | Status |
|----------|----------|--------|
| macOS (Apple silicon) | Claude | ✅ Tested end-to-end |
| Linux | Claude / Codex | ✅ The maintainer's fleet runs on Linux (x86-64) |
| Windows | Claude / Codex | ⚠️ Docker Desktop + WSL2 is the intended path. Unverified |
| Any | Codex | ⚠️ Code paths ship in `seed.example.json`, but Claude has seen far more wall-clock time in real workflows |
| Any | Codex + GLM (Z.ai GLM Coding Plan) | ⚠️ A `zai/` model prefix runs GLM on your Coding Plan subscription through a loopback forwarder inside the agent. Subscriber-only use. The merged implementation passed exact-head canary acceptance. See `docs/providers/codex-hosted-models.md` |
| Any | Claude or Codex + local model (e.g. Ollama) | ⚠️ Set **Runs on → Local server** per agent, with the server URL, model tag and context window. Claude local runs with no Claude credential in the container and passed exact-head canary acceptance. Codex local still holds the OpenAI credential until #383. See `docs/providers/local-models.md` |
| Any | Gemini | ⚠️ Runs Google's Antigravity CLI (`agy`) with a paid Gemini API key (`GEMINI_API_KEY` Env Ref); no Google sign-in. Keeps its conversation across restarts. Limits vs claude/codex: instructions at most 56,000 bytes and a message at most 190,000 bytes (the CLI cuts more without failing, so the agent refuses instead); attachments are path hints, not content blocks; `AllowedTools` is not enforced by the CLI; no effort setting. Measured on the pinned binary against a stand-in, never against a real Gemini model. See `docs/providers/gemini.md`. |

If you run Phleet on Windows, on a Linux host, or with Codex or Gemini as the primary provider and hit something broken — PRs and issue reports are very welcome. Small fixes and "it works on my box" confirmations are just as valuable as new features here.
