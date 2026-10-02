# Attachment acceptance status

This is implementation evidence, not permission to merge. The PR stays draft
until all remaining rows pass at the final head; no scope or acceptance waiver.

| Gate | Evidence and remaining work |
|---|---|
| XAC1 | Real MySQL `JournalReadStoreTests` covers private photo/group PDF metadata, same Telegram id, both identifiers, hidden/foreign/missing/ordinal scope and unbound; Comms route tests cover state/error order, but the full SQL state matrix is not claimed |
| XAC2 | `JournalAttachmentContentTests` covers authorization, integrity, caps and fake-clock 30/40-second deadlines |
| XAC3 | Real SQL content test makes 20 fetches without changing message/object counts or fake-bucket keys; full captured-log and scoped real-bucket proof remains |
| XAC4 | `JournalFilesToolTests` covers read token, fixed errors, serialization, 50-second deadline, two-second resend, exactly one retry and turn-change refusal |
| XAC5 | `JournalFileStoreTests` and `JournalFilesSweepTests` cover permissions, safe names, partial cleanup, links, quota, startup/hourly TTL and ordinary sweeper isolation |
| XAC6 | Actual 8091 tools/list, readiness and unrelated-route 404 tests; final-head control-listener/container isolation acceptance remains |
| XAC7 | Actual provisioning graph covers all providers, disabled grants, warning, reserved endpoint and runtime read token; six bot/headless fixtures prove disabled-grant byte identity |
| XAC8 | Entrypoint runs its actual Python/Node translators; Gemini gets headerless `httpUrl`, Codex gets headerless URL and `enabled_tools` |
| XAC9 | Maintainer-owned exact-head pinned-provider image/PDF/20 MiB reads, MCP timeout and network/credential checks remain |
| XAC10 | Agent, orchestrator, dashboard and selected Comms/real SQL suites pass; AC13 and final-head CI remain required |

X9 also requires an integration test against the scoped identity of
`comms-media-store-smoke`, not the Admin-identity S3 fixture. It has not yet been
added or run; this development environment has no Docker daemon. That requirement
is not replaced by the fake-bucket SQL test or the provider transport probes.

[D3 transport transcripts](journal-provider-probes.md) prove Codex headers and
record the credential-blocked Gemini probe with its flag still false. Neither
transcript is final-head AC13/XAC9 evidence.
