# Reply-ownership mutation procedure

The verification plan for the reply-ownership work requires that every load-bearing ownership rule
has a mutation which turns the green suite red, shipped "as a documented procedure with recorded
before/after results". This file is that procedure.

A test that has only ever been green is a claim, not a guard. These mutations are the evidence that
each rule is actually load-bearing: break the rule, watch the named test fail, restore it.

## Running it

Each mutation is a single local edit. Apply it, run the named filter, confirm **RED**, then revert:

```bash
# apply the edit from the table below, then:
dotnet test tests/Fleet.Agent.Tests/Fleet.Agent.Tests.csproj -c Release --filter "<filter>"
git checkout -- <file>     # or restore the saved original
```

A mutation that fails to COMPILE has not been demonstrated — it tests the compiler, not the suite.
Rewrite it until it builds and then fails the named test; two rows below needed exactly that.

Restore every file before committing. Running the whole table back to back is straightforward to
script: apply → test → restore, one mutation at a time, never two at once (two mutations can mask
each other, and a pair that cancels out reads as a surviving mutation).

## The table

Rows are the mutations from the verification plan, in its order. "Test that must fail" is the test
the plan names; the filter column is what actually selects it.

| # | Mutation | Edit | Test that must fail | Result |
|---|---|---|---|---|
| M1 | Remove the `NullMessageSink` default (D-1) | `MessageSinkHolder` ctor assigns `null!` instead of `new NullMessageSink(counter)` | T9 | RED (test) — `T9_HostWithoutABotToken_...` |
| M2 | Re-attach relay publication to `AgentTransport` and drop `RelayCompletionPublisher` | early-return from `RelayCompletionPublisher.OnTaskCompleted` | T10 | RED (test) — `T10_...`, `RelayAnswer_IsPublishedWithoutATelegramTransport` |
| M3 | Remove the `RuntimeWiringService` guard | `if (false)` in place of the `IsAttached` check | T11 | RED (test) — all three `StartAsync_Throws*` |
| M4 | Delete one reserved-key guard | first `IsReservedKey` guard in `AgentTransport` → `if (false)` | T3, T12 | RED (test) — `SendTextAsync_DoesNotTouchTheBotClientForAReservedKey`, `ReservedKeyGuards_StayInTheTransportAndStayAtFour` |
| M5 | Subscribe both new singletons to relay publication | `RelayCompletionPublisher` subscribes twice | T7, T13b | RED (test) — `OneCompletion_PublishesExactlyOnce`, `T13b_...` |
| M6 | Add a client event for a `/status` reply | `PublishEvent` at the top of `TaskManager.HandleStatus` | T4 | **GREEN on the first attempt — see the finding below.** RED (test) after the test was strengthened: `T4_...("/status")` |
| M7 | Make admission per-conversation instead of global | `hasRunningTask` reads this chat's state only | T16 | RED (test) — `T16_...` |
| M8 | Map `/halt` to `TurnCancelReason.Operator` | `HandleStop` marks each task before cancelling | T20 | RED (test) — `T20_...` |
| M9 | Re-point the `attempt` increment at the provider `--resume` path | increment + `turn.started` on every `IsProcessExit` | T22, T23 | RED (test) — `T22_...`, `T23_...` |
| M10 | Replace the pair with a bare `AddHostedService<RelayCompletionPublisher>()` | in `AgentHostRegistration` | T11 (DI resolution) | RED (test) — `T9_Control...`, `T10_...`, `T13b_...`, `T15_...` |
| M11 | Register `RelayCompletionPublisher` twice — `AddSingleton` **and** `AddHostedService<T>()` | in `AgentHostRegistration` | T7, T13b | RED (test) — `T13b_...` |
| M12 | Remove the `try`/`catch` around `new TelegramBotClient` | in `AgentTransport`'s constructor | T13 | RED (test) — `MalformedToken_LeavesTheBotNullInsteadOfThrowing`, `TransportAttachesItselfAsTheSink_EvenWithAMalformedToken` |
| M13 | Give `NullMessageSink` a non-zero `GetLastSentMessageId` | override returning `99` | T8b | RED (test) — `NullSink_InheritsTheZeroDefault`, `UnattachedHolder_YieldsZeroLastSentMessageId`, `WithNoSinkAttached_ContextBuffersWithZeroMessageId` |
| M14 | Have `CompletionContextBuffer` default `telegramMessageId` to `0` unconditionally | drop the `GetLastSentMessageId` read | T8b | RED (test) — `ContextBuffer_ReadsTelegramMessageIdThroughTheSink` |
| M15 | Add a new `Sink.*` site without a table entry | a send in `GroupBehavior`, which has a sink and no sends | T1 | RED (test) — `EverySinkCallSite_IsAccountedFor` |
| M16 | Skip the `ConversationEventBus` oversize synthesizer | `if (false)` around the terminal replacement | T1b | RED (test) — `OversizeTerminalEvent_IsReplacedByOutcomeUnknown` |
| M17 | Emit a client event from a completion handler | `_events.Publish` in `CompletionContextBuffer` | T3 (via the T1 inventory) | RED (test) — `OnlyDesignatedFiles_PublishConversationEvents` |

### Rows covered by a standing test rather than a transient mutation

Three rows of the plan's table describe removing a registration. Those are not run as mutations
because the corresponding tests already perform the removal themselves, on the real registration
graph, on every run — a permanent guard rather than a one-off:

| Plan row | Standing test |
|---|---|
| Register `CompletionContextBuffer` but not `RelayCompletionPublisher` | `ProgramRegistrationGraphTests.T11_AGraphMissingTheRelayPublisher_FailsStartupNamingIt` |
| Register a second adapter for one channel id | `ProgramRegistrationGraphTests.T15_TwoAdaptersForOneChannelId_FailStartup` |
| Remove `AgentTransport` from the graph | `ProgramRegistrationGraphTests.T9_HostWithoutABotToken_...` and its `T9_Control...` positive control |

### Rows with no local edit that expresses them

Three rows describe adding a code path that does not exist, or a refactor rather than an edit. They
are recorded honestly here rather than silently dropped:

| Plan row | Why, and what guards it |
|---|---|
| Synthesize a client event for an MCP send | There is no MCP-send emission site to mutate — the absence IS the property. `ConversationIntakeTests` covers the behaviour (T5b); M17 demonstrates that publishing from any non-designated file fails the inventory. |
| Add a client event for `/run` output | Same shape as M6, in the same file class. M6 is the representative run. |
| Relocate `_lastSentMessageIds` out of `AgentTransport` | A relocation is a refactor, not an edit. `EmissionOwnershipInventoryTests.LastSentMessageIds_LivesOnlyInTheTransport` fails on any second reader, and M13/M14 cover the value's two failure directions. |

## Recorded run — 2026-09-16

Every mutation above was applied, tested and reverted on this branch, one at a time. **17 of 17
turn the suite red.** Baseline before and after the run: `dotnet test Fleet.sln -c Release` —
1305 passed, 0 failed, 0 skipped.

Two mutations needed a second form before they were meaningful, and both are worth knowing about:

- **M12** as first written (`try` → `if (true)`) failed to COMPILE rather than failing a test. A
  mutation that does not build tests the compiler, not the suite. Re-run as a clean removal of the
  `try`/`catch`, it fails `MalformedToken_LeavesTheBotNullInsteadOfThrowing` as the plan says.
- **M17** likewise. Re-run in compiling form, it fails the inventory test.

### Finding — M6 survived, and why that mattered

The first form of M6 published a `turn.progress` from `TaskManager.HandleStatus` using the
identity the runtime synthesises for a Telegram-sourced command. **The suite stayed green.**

That turned out not to be a missing guard but a missing test. Two independent mechanisms stop that
event: `SynthesizeIdentity` stamps a Telegram-sourced command with a runtime-owned `channelId`, and
`ConversationEventBus` returns `not_routed` for runtime-owned channels before it ever looks the
conversation up. So the naive mutation cannot reach a client — but the T4 test as first written
could not tell the difference between "the command surface published nothing" and "the bus dropped
what it published". It was passing for the wrong reason.

The test was strengthened to also drive the command surface with the CLIENT conversation's runtime
key, which is the shape a real leak takes, and M6 re-run with a client-channel identity — the
faithful form of "a client event for a `/status` reply". It now fails.

This is exactly the protocol below, and it is the argument for running the table rather than
shipping it: a mutation that survives is the specification of an assertion nobody wrote.

## What to do when a mutation survives

A mutation that leaves the suite green is a finding, not a formality. It means the rule it breaks is
either untested or tested by an assertion that does not depend on it. Add the missing assertion
before restoring the file — the mutation is the specification of what the test should have checked.
