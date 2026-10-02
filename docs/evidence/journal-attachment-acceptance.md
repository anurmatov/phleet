# Attachment acceptance status

PR #402 merged as `7e1875148afe1bdb22f0dd2fd40c53bad2d83023` after the maintainer
explicitly accepted the remaining manual rows as **unrun**, not passed
([recorded decision](https://github.com/anurmatov/phleet/pull/402#issuecomment-5953757700)).
Merge does not establish deployment, tool grants or full acceptance.

| Gate | Evidence and remaining work |
|---|---|
| XAC1 | Real MySQL `JournalReadStoreTests` covers private photo/group PDF metadata, same Telegram id, both identifiers, hidden/foreign/missing/ordinal scope and unbound; the SQL state matrix additionally covers lost, uploaded/committed, aborted/deleting/absent, not_archived and excluded for both identifiers |
| XAC2 | `JournalAttachmentContentTests` covers authorization, integrity, caps and fake-clock 30/40-second deadlines |
| XAC3 | Real SQL content test makes 20 fetches without changing all five journal-table counts or fake-bucket keys; runtime captured-log test passes, scoped-store test asserts real listing stability and captured Comms/store logs |
| XAC4 | `JournalFilesToolTests` covers read token, other-kind files, fixed errors, serialization, both cancellation orders and a delayed timer callback at the 50-second deadline, two-second resend, exactly one retry and turn-change refusal |
| XAC5 | `JournalFileStoreTests` and `JournalFilesSweepTests` cover permissions, safe names, partial cleanup, links, quota, startup/hourly TTL and ordinary sweeper isolation |
| XAC6 | Actual 8091 tools/list and unrelated-route 404 tests; exact-head enabled/disabled control-cancel, listener-before-CLI readiness and bounded container isolation passed; remaining full upgrade/provider rows are unrun |
| XAC7 | Actual provisioning graph covers all providers, disabled grants, warning, reserved endpoint and runtime read token; six bot/headless fixtures prove disabled-grant byte identity |
| XAC8 | Entrypoint runs its actual Python/Node translators; Gemini gets headerless `httpUrl`, Codex gets headerless URL and `enabled_tools` |
| XAC9 | Maintainer-owned exact-head pinned-provider image/PDF/20 MiB reads, MCP timeout and network/credential checks remain |
| XAC10 | Agent, orchestrator, dashboard and selected Comms/real SQL suites pass; all 11 final-head CI checks passed; AC13 remains unrun |

The CI configuration builds and runs `JournalScopedAttachmentStoreTests` inside the
`comms-media-store-smoke` job against that job's existing scoped runtime identity,
not the Admin-identity S3 fixture. The script requires exactly one passing test
and zero skipped tests in its TRX result, including 20 content-route reads,
unchanged real-bucket listing and captured-log checks. It was not executed
locally because this development environment has neither a Docker daemon nor
the Compose plugin. At implementation head
`2733e45a1eb947d86def28fac219d7fbfee3ccb1`, the actual
[scoped-store smoke job](https://github.com/anurmatov/phleet/actions/runs/37008644755/job/110842789594)
passed and its captured job log contains:

```text
PASS X9 scoped-store content route: one executed, zero failures/skips
```

This is X9's scoped-identity integration evidence, not XAC9's maintainer-owned
provider/container acceptance. The earlier `Build and Test` observation was
pending at that head; all 11 checks subsequently passed at final implementation
head `a79b89e6` ([final-head CI](https://github.com/anurmatov/phleet/actions/runs/37009908046)).

[D3 transport transcripts](journal-provider-probes.md) prove Codex headers and
record the credential-blocked Gemini probe with its flag still false and the
initial credential-blocked Claude 2.1.280 attempt followed by its passing
authorized bound/unbound AC8 proof. Exact-head control-cancel and listener
readiness also passed ([operator receipt](https://github.com/anurmatov/phleet/pull/402#issuecomment-5953413719)).
Full AC13 reply/queue/provider acceptance, XAC9 model-driven attachment reads
and MCP timeout/network/credential checks, plus remaining XAC6 upgrade/provider
rows stay **unrun**. Transport transcripts and scoped-store CI do not substitute
for those rows; the maintainer's merge decision does not convert them to passes.
