# Phone setup code

The dashboard can show a QR code that the [iOS app](https://github.com/anurmatov/phleet-ios) scans
to set up its control-plane connection: **Credentials → Phone → Connect a phone**.

The control plane is the app's Fleet and Runs tabs. It talks to this orchestrator's operator API
through the dashboard's address (`/api/…`, `/ws`). It is a different surface from the app's Chat
tab, which enrolls a device against `Fleet.Comms` and needs no setup code.

## What the code is

One JSON object, as text:

```json
{"phleet":"control-setup","v":1,"origin":"https://fleet.example.test","accessClientId":"…","accessClientSecret":"…","operatorToken":"…"}
```

| Field | Meaning |
|---|---|
| `phleet` | Always `control-setup`. A reader refuses anything else by name |
| `v` | Format version, `1`. A reader refuses a version it does not know |
| `origin` | The HTTPS address the **phone** will use to reach the dashboard. Not always the address the page was opened at |
| `accessClientId`, `accessClientSecret` | An access-proxy service token, both or neither. Left out when the dashboard is reached without a proxy |
| `operatorToken` | `ORCHESTRATOR_AUTH_TOKEN`. Left out for a read-only phone |

A part that is not given is **left out**, never sent as an empty string.

## What the dashboard does and does not do

- It builds the code in the page from values the page already holds (its own address and the
  operator token it was built with) plus the service token you paste in.
- It sends the code nowhere and stores nothing. The service token you paste is gone when the
  section closes.
- It refuses an `http://` address rather than upgrading it, because the app refuses one too.
- It hides the code after two minutes.

## Limits

- **A code with the operator token is full control of the fleet.** Anyone who photographs it can
  change anything the dashboard can. Show it only where the screen is not overlooked, and prefer a
  read-only code (untick the operator token) for a phone that only needs to watch.
- **The code is not single-use and does not expire.** It is a way to type less, not an enrollment:
  the values in it stay valid until you rotate `ORCHESTRATOR_AUTH_TOKEN` or delete the service
  token at the proxy.
- **A lost phone is handled at the proxy.** Delete that phone's service token. Without a proxy in
  front, the only remedy is rotating the operator token.
- **The orchestrator's read routes need no token.** Behind an access proxy a service token alone
  can read agents, workflows, instructions, project contexts, memory and agent logs. It cannot
  read credential files or config values, and cannot change anything.

Tests: `src/fleet-dashboard/src/phoneSetup.test.ts`.
