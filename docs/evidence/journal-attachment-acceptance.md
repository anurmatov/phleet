# Attachment acceptance status

This is implementation evidence, not permission to merge. The PR stays draft
until all remaining rows pass at the final head; no scope or acceptance waiver.

| Gate | Evidence and remaining work |
|---|---|
| XAC1 | Real MySQL `JournalReadStoreTests` covers private photo/group PDF metadata, same Telegram id, both identifiers, hidden/foreign/missing/ordinal scope and unbound; the SQL state matrix additionally covers lost, uploaded/committed, aborted/deleting/absent, not_archived and excluded for both identifiers |
| XAC2 | `JournalAttachmentContentTests` covers authorization, integrity, caps and fake-clock 30/40-second deadlines |
| XAC3 | Real SQL content test makes 20 fetches without changing all five journal-table counts or fake-bucket keys; runtime captured-log test passes, scoped-store test asserts real listing stability and captured Comms/store logs |
| XAC4 | `JournalFilesToolTests` covers read token, other-kind files, fixed errors, serialization, both cancellation orders and a delayed timer callback at the 50-second deadline, two-second resend, exactly one retry and turn-change refusal |
| XAC5 | `JournalFileStoreTests` and `JournalFilesSweepTests` cover permissions, safe names, partial cleanup, links, quota, startup/hourly TTL and ordinary sweeper isolation |
| XAC6 | Actual 8091 tools/list, readiness and unrelated-route 404 tests; final-head control-listener/container isolation acceptance remains |
| XAC7 | Actual provisioning graph covers all providers, disabled grants, warning, reserved endpoint and runtime read token; six bot/headless fixtures prove disabled-grant byte identity |
| XAC8 | Entrypoint runs its actual Python/Node translators; Gemini gets headerless `httpUrl`, Codex gets headerless URL and `enabled_tools` |
| XAC9 | Maintainer-owned exact-head pinned-provider image/PDF/20 MiB reads, MCP timeout and network/credential checks remain |
| XAC10 | Agent, orchestrator, dashboard and selected Comms/real SQL suites pass; AC13 and final-head CI remain required |

The CI configuration builds and runs `JournalScopedAttachmentStoreTests` inside the
`comms-media-store-smoke` job against that job's existing scoped runtime identity,
not the Admin-identity S3 fixture. The script requires exactly one passing test
and zero skipped tests in its TRX result, including 20 content-route reads,
unchanged real-bucket listing and captured-log checks. It was compiled but not
executed locally because this development environment has neither a Docker daemon
nor the Compose plugin; its CI result remains required, not inferred from the
fake-bucket SQL test or provider transport probes.

[D3 transport transcripts](journal-provider-probes.md) prove Codex headers and
record the credential-blocked Gemini probe with its flag still false. Neither
transcript is final-head AC13/XAC9 evidence.
