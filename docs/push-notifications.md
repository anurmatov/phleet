# Push notifications

Off by default. When on, the orchestrator tells a registered phone when something needs a person:

| Event | Sent when | Sent again when |
|---|---|---|
| **A gate is waiting** | A running workflow enters a phase one of its registered signals names | The run leaves the gate and returns, or the gate reference changes |
| **A workflow failed** | A run closes `Failed` or `TimedOut` | Never for the same run |
| **An agent went down** | An agent that was alive has sent no heartbeat for the grace period (5 minutes by default) | It recovers and goes down again |
| **An agent replied in chat** (#530) | An agent's answer commits and no client has that conversation open (Fleet.Comms with conversations) | Never for the same answer; a device that did not accept it is retried for 10 minutes |

The phone is the [iOS app](https://github.com/anurmatov/phleet-ios), signed in as a device
([sign-in](sign-in.md)). Notifications go through Apple's push service (APNs), which the orchestrator calls
directly; there is no extra service.

## Setting it up

You need an Apple developer account with the app's identifier, and an APNs authentication key
(a `.p8` file) for it. Apple shows the key identifier next to the key and lets you download the
file once.

In `.env`:

```
FLEET_PUSH_ENABLED=true
FLEET_PUSH_APNS_KEY_BASE64=<base64 of the .p8 file>
FLEET_PUSH_APNS_KEY_ID=<the key's ten-character identifier>
FLEET_PUSH_APNS_TEAM_ID=<your Apple team identifier>
FLEET_PUSH_APNS_TOPIC=<the app's bundle identifier>
```

To produce the base64 value: `base64 -i AuthKey_XXXXXXXXXX.p8 | tr -d '\n'`.

Recreate the orchestrator. Then, in the app, turn notifications on from the connection sheet; the
app registers its device with `PUT /api/push/devices/{deviceId}`.

`GET /api/push` answers `{"enabled": true, "devices": 1}` once both are done. It never shows a
device token.

| Env var | Default | Purpose |
|---|---|---|
| `FLEET_PUSH_ENABLED` | `false` | Turns the sender on |
| `FLEET_PUSH_APNS_KEY_BASE64` | empty | The `.p8` file, as base64 (PEM text also works) |
| `FLEET_PUSH_APNS_KEY_ID` | empty | The key identifier |
| `FLEET_PUSH_APNS_TEAM_ID` | empty | The Apple team identifier |
| `FLEET_PUSH_APNS_TOPIC` | empty | The app's bundle identifier |

Two more settings exist as orchestrator options only (`Push__IntervalSeconds`, default 30, and
`Push__AgentDownGraceSeconds`, default 300). `Push__ApnsHost` selects Apple's host and defaults to
production, `api.push.apple.com`.

## What a notification says

A notification passes through Apple and sits on a lock screen, so it carries only what identifies
the thing:

- a gate: the signal's name and the repository with its pull request or issue number
  ("Merge approval waiting", "example/project · PR #42")
- a failure: the same subject and the workflow type
- an agent: its name
- a chat reply: the agent's display name and "New reply"

It never carries task text, a comment, a failure message, a reply's text or any output. Alongside the
visible text it carries the identifiers the app needs to open the right screen: namespace, workflow
identifier, run identifier, signal name, agent name, or conversation identifier.

Every notification also carries a **count** for the app's icon: how many runs are waiting for a
person, one for each signal a run is waiting for, which is how the app counts them. It is set
when the notification is sent, so one that is sent again carries the count as it is then. When
the count changes and nothing else is going out — a gate was answered from the dashboard, or its
run was cancelled — a notification with the count and nothing else is sent: no words, no sound,
at Apple's low priority. A number of waiting runs says nothing about any of them.

A chat reply is reported by the chat service, not detected here: when an answer commits and no
client is attached, Fleet.Comms calls `POST /internal/push/chat-reply` with its own
`cj1.chat-push.fleet-comms` token. The route is internal, never proxied, and does nothing while push
is off. The notification goes out at once rather than on the next 30-second pass.

## Rules it follows

- **Each event is announced once.** The phone also collapses a repeat of the same event into one
  notification.
- **Nothing is announced at start-up.** The first look after the orchestrator starts only records
  what is already true, so a restart does not re-announce every gate that has been waiting since
  yesterday. The cost: something that happened while the orchestrator was down is not announced.
  The same applies to a Temporal namespace that was unreachable at start-up: the orchestrator
  keeps trying it, and the first look after it connects only records what is already waiting.
- **The count is corrected by the next change, not by a restart.** The first look records the
  count without sending it, like everything else at start-up, so an icon that was wrong when the
  orchestrator went down stays wrong until a gate starts or stops waiting, or the app is opened:
  the app sets its own icon from what it reads.
- **A run someone cancelled or terminated is not a failure.** Whoever stopped it already knows.
- **An agent stopped through the API is not "down".** The orchestrator sees a stopped agent and a
  crashed one the same way, so `POST /api/agents/{name}/stop` records that the stop was asked for,
  and the sender stays quiet until that agent has run again. That record is in memory: after an
  orchestrator restart nothing already down is announced anyway.
- **Only an agent seen alive can go down.** An agent that was never provisioned, or that this
  orchestrator process never heard from, is not reported.
- **A device Apple says is gone is forgotten.** When Apple answers that a token is no longer
  registered, the device row is deleted. Any other failure keeps the device: forgetting a phone
  because Apple was busy, or because the key is wrong, would turn notifications off for good.

## Limits

- **Production only by default.** Builds distributed through TestFlight or the App Store use
  Apple's production environment, which is the default host. A build run from Xcode uses the
  sandbox and receives nothing unless the key covers the sandbox and `Push__ApnsHost` is set to
  `api.sandbox.push.apple.com`. One orchestrator sends to one of the two.
- **Registering needs the operator token.** It is a write, so a phone with a read-only connection
  cannot register.
- **At most ten devices.**
- **Delivery is Apple's.** "Accepted" means Apple took the notification, not that the phone showed
  it. Focus modes, notification settings and an offline phone are outside this. A count alone is
  sent at low priority, which Apple may hold back to save the phone's battery, so the icon can
  trail the fleet by a while.
- **The count is the fleet's, the same for every device.** It does not know what one phone has
  already answered or looked at, and it counts only the namespaces the orchestrator can reach.
- **A refused send is tried again, per device.** A device that did not accept a notification
  (Apple busy or unreachable) is sent it again on later passes while the event is still true, up
  to ten sends in all: about five minutes at the default interval. A device that accepted is not
  sent it again, and a gate decided in the meantime is not announced late. What is owed is held in
  memory, so a restart owes nothing. If the failure query is unavailable, that pass is skipped.
- **The key signs for your whole Apple team.** It lives only in `.env` and the orchestrator's
  environment. No endpoint returns it and it is never logged. Do not give it to an agent as an Env
  Ref.

## Misconfiguration

With `FLEET_PUSH_ENABLED=true` and a value missing or unusable, the orchestrator logs
`push_misconfigured` once, with the name of what is wrong and never its value, and the feature
stays off. The key is checked by signing with it once, so a key that would only fail at the first
notification is caught here:

| Logged | Meaning |
|---|---|
| `push_apns_key_missing`, `push_apns_key_id_missing`, `push_apns_team_id_missing`, `push_apns_topic_missing` | That setting is empty |
| `push_apns_host_invalid` | `Push__ApnsHost` is not a bare host name |
| `the APNs key …` | The key is not base64 of a `.p8` file, not PEM text, not a P-256 key, or a public key that cannot sign |

While off, `GET /api/push` answers `"enabled": false` and `PUT /api/push/devices/{deviceId}`
answers `503 push_disabled` and stores nothing, so the app can say that notifications are not
available rather than claim they are on. Both follow the same check as the sender.

## Troubleshooting

| Log line | What to check |
|---|---|
| `Push not sent: … (InvalidProviderToken)` | The key identifier or team identifier does not match the key |
| `Push not sent: … (TopicDisallowed)` or `(BadTopic)` | The bundle identifier is wrong, or the key is restricted to another app |
| `Push device … is no longer registered (BadDeviceToken)` | The build uses the other APNs environment than this host (see Limits) |
| `Push not sent: … (transport)` | The orchestrator cannot reach Apple's host over HTTP/2 on port 443 |
| `Push not sent: … given up after 10 attempts` | Apple refused the same notification to that device every time; read the reason |
| `Push notifier pass failed` | The failure query or the database was unavailable for that pass |

## Files and tests

- `src/Fleet.Orchestrator/Services/Push/` — the provider token, the client, the event detector,
  the message wording, the background service
- `src/Fleet.Orchestrator/Endpoints/PushEndpoints.cs` — device registration, and the chat service's
  reply route
- `src/Fleet.Comms/ChatReplyNotifier.cs` — the chat service's side of a reply notification
- Migration `AddPushDevices` — the `push_devices` table
- `tests/Fleet.Orchestrator.Tests/Push/` — no network: the HTTP handler is a double and the key is
  generated per test

## Exact-request approvals

The registered `request-input` phase uses the existing gate detector and APNs
payload. Notification text never contains Question, Context, answer or Comment. Source gate
registration is not proof of physical delivery or exact-request native navigation;
see [human input requests](human-input-requests.md) for the companion,
confirmation, adoption and real-phone acceptance boundaries.
