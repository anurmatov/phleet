# Sign-in

One sign-in for the dashboard, the phone app and chat (#530). The orchestrator owns it: users,
devices and credentials live in its database, and the chat service (Fleet.Comms) asks the
orchestrator who each caller is. Everything is reached through one address, the dashboard's.

```
phone ──┐                          ┌─ /api/…, /ws  ─▶ fleet-orchestrator   sign-in, devices, agents, runs
        ├─▶ dashboard address ─────┤                        ▲  "who is this caller? may they chat with agent X?"
browser ┘   (fleet-dashboard nginx) └─ /v1/…        ─▶ fleet-comms          conversations
```

## Who is a user

One user, `owner`, created at startup when there is none. When `FLEET_PRIMARY_HUMAN_USER_ID` is set,
the owner is that Telegram person, so their chat messages get the same priority as their Telegram
messages ([primary-human priority](primary-human-priority.md)). There are no screens for adding
people yet; a second person later is a new row, with no change to anything below.

## Three credentials

| Credential | Who holds it | How it is obtained | What it may do |
|---|---|---|---|
| Session cookie `fleet_session` (HttpOnly, SameSite=Strict, 30 days, extended while used) | a browser | the dashboard's sign-in page, once per browser, with `ORCHESTRATOR_AUTH_TOKEN` | everything, including Devices and `/api/config/*` |
| Device access token (bearer, 15 minutes) | the phone app | minted from the device's id and secret, which it got with a one-time enrollment code | the operator API and chat. **Not** `/api/config/*`, and not other devices — only revoking itself |
| Service bearer `ORCHESTRATOR_AUTH_TOKEN` | automation and internal services | `.env` | what it always could, plus signing a browser in |

`ORCHESTRATOR_AUTH_TOKEN` is never shipped to a client: it is not in the dashboard's JavaScript and
not in the phone's setup code.

A browser's **write** must also carry `X-Fleet-Request: 1`, on the orchestrator and on the chat
service. With the cookie SameSite=Strict, that closes cross-site request forgery: a page on another
site cannot add the header. A bearer is never ambient, so a bearer request needs nothing extra.

## Where sign-in is required

The dashboard's nginx marks every request it forwards (`X-Fleet-Edge`, always overwritten). A marked
request to `/api/*` or `/ws*` needs one of the three credentials, **reads included**. An unmarked
request — a service on the Docker network calling `fleet-orchestrator:3600` directly — keeps the
older rule (writes need the service bearer, reads do not), so internal services need no new
credential. `/mcp`, `/health` and `/internal/*` are unchanged.

With `ORCHESTRATOR_AUTH_TOKEN` empty the ordinary API stays open, and the sign-in page issues a
browser session without asking so chat still has a credential to present; that open-mode cookie
never substitutes for `ORCHESTRATOR_CONFIG_TOKEN`, and HumanInput still requires configured auth.

Control and log WebSockets recheck their original credential independently of traffic every
20 seconds, with a five-second lookup deadline, and abort on expiry, revocation or unavailable
auth; browser upgrades also require the same Origin, using the edge's forwarded scheme.

Push registrations created by a signed-in device carry its server-derived device identity,
and revocation removes those registrations in the same transaction as its tokens; apps may
change only their own registrations, while explicit service-created rows remain service-managed.
The migration invalidates pre-sign-in push registrations because their owner cannot be established:
clients must register again after enrollment, and rolling back the column cannot restore those rows.
Delivery also excludes revoked owners before each attempt, although a push already handed to
Apple cannot be recalled.

## Routes

| Route | Credential | Answer |
|---|---|---|
| `GET /api/auth/session` | any | who is signed in, the kind (`browser` or `app`), and whether sign-in is required; `401` when signed out |
| `POST /api/auth/browser-session` `{token, deviceName?}` | none (the body carries the service bearer) | the session cookie; `401` |
| `DELETE /api/auth/browser-session` | cookie | signs this browser out and revokes it |
| `POST /api/auth/enrollments` `{deviceName?}` | cookie or service bearer | `{enrollmentCode, expiresInSeconds}` — 15 minutes, one use |
| `POST /api/auth/devices` `{enrollmentCode, deviceName?}` | none | `{deviceId, deviceSecret}`; `401`; `409 device_limit` (20 active devices) |
| `POST /api/auth/token` `{deviceId, deviceSecret}` | none | `{accessToken, expiresInSeconds}`; `401` |
| `GET /api/auth/devices` | cookie or service bearer | every browser and app, revoked ones included |
| `POST /api/auth/devices/{deviceId}/revoke` | cookie or service bearer for any device; an app for itself only | `204` |
| `GET /api/chat/agents` | any | `{available, agents: [{agentName, displayName}]}` |
| `POST /internal/auth/check` | the chat service's own token | who a forwarded credential names, and whether an agent accepts chat |

The routes that take no credential admit 30 requests per minute per client address. Every failure on
them is the same `401`. No route returns or logs a secret after the response that issues it, and
secrets are stored only as hashes (Argon2id for device secrets, one key derivation per lookup).

## Connecting a phone

**Credentials → Devices → Connect a phone** in the dashboard asks the orchestrator for an enrollment
code and shows a QR code ([phone setup code](phone-setup-code.md)) holding the address, an optional
reverse-proxy service token, and that code. The phone registers itself with the code, keeps the
device id and secret in its Keychain, and mints a 15-minute access token whenever it needs one.
Revoke a lost phone from the same Devices list.

## The chat service's question

For every request, the chat service forwards the caller's `Authorization` header or session cookie
to `POST /internal/auth/check`, under a `cj1.auth-check.fleet-comms` token signed with the key the two
services share (`FLEET_COMMS_JOURNAL_KEY`). The answer names the person, the device and the kind;
with `agent` in the question it also says whether that agent accepts chat. The chat service
remembers a definite answer for at most 10 seconds and asks again, fresh, on an independent
20-second timer for an open stream, even while replies are arriving continuously. An orchestrator that cannot be reached is `503`, never a pass. A token
the orchestrator does not accept is answered `403`, which the chat service logs as the two services
holding different keys.

## Upgrading

See [Upgrading to one sign-in](comms-deployment.md#upgrading-to-one-sign-in-530). In short: run
`./upgrade.sh`, sign in to the dashboard once with `ORCHESTRATOR_AUTH_TOKEN`, reprovision the agents,
and connect the phone again from Devices.
