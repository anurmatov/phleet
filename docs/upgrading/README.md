# Upgrade notes

Read the note that applies before upgrading an existing installation. Day-to-day upgrading is `./upgrade.sh`, described in the [setup guide](../setup.md#upgrade-after-pulling-new-code).

## Mandatory signed MCP identity

Before strict binaries, migrate on `2a338668893527017fbaf77781906e9a9bb141ab` (PR610) or a later
pre-hardening descendant, preserve the shared key and collect signed native read receipts for
all eligible active/external callers. Remap config-token Authorization to `X-Fleet-Config-Token`
before reprovisioning. Migrate or stop running unsupported-provider callers, preserve stopped
callers and capture compatible images/config/key together. Upgrade refuses a missing/invalid key
before changes and does not reprovision agents. There is no unsigned mode; rolling back to old
binaries restores unsigned exposure. Follow the full [migration and undo procedure](../agent-identity.md#existing-install-migration).

## Temporal and Qdrant leave the agents' network

Temporal (server, UI, database) and Qdrant move off `fleet-net` to networks no agent joins.
`./upgrade.sh` recreates them on a generated stack. A deployment with its own compose file makes the
change by hand, and anything that reached Temporal, its UI, its database or Qdrant by Docker name
from `fleet-net` stops working. Steps, checks and rollback:
[docs/agent-identity.md](../agent-identity.md#moving-an-existing-install).

## Loopback ports and the Qdrant pin

Before upgrading past the loopback-ports change, read **[docs/upgrading/internal-ports-loopback.md](internal-ports-loopback.md)**: Temporal, its UI, fleet-memory and fleet-bridge stop being reachable from other machines, and Qdrant is pinned, so `upgrade.sh` refuses to move Qdrant by more than one minor release, or backwards.

## Memory search re-index

The first start of fleet-memory after the memory-search change embeds every memory again, once: one embedding call per section (a memory over 1,200 characters is several sections), one after the other. The service answers during it, and later starts embed only what changed. How to estimate the cost for your store, what search does meanwhile, and how to go back to an earlier image are in **[docs/memory-search.md](../memory-search.md#what-a-start-costs)**.

## Pi provider removed

Before upgrading from a release with Pi, [move every Pi agent to Claude or Codex and reprovision](remove-pi.md).

## Project context cards removed

Project cards, per-assignment `full` / `card` modes and project routes were removed in
#346. An install that ran the card build must follow
**[docs/upgrading/remove-project-context-cards.md](remove-project-context-cards.md)**
before deploying.

## Managed sandbox runtime removed

An install that ran the stock OpenShell (managed sandbox runtime) build must follow
**[docs/upgrading/remove-managed-runtime.md](remove-managed-runtime.md)** before
deploying: that integration was removed.
