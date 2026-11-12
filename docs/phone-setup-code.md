# Phone setup code

The dashboard shows a QR code that the [iOS app](https://github.com/anurmatov/phleet-ios) scans to
connect: **Credentials → Devices → Connect a phone**. One scan sets up everything — the Fleet, Runs
and Chat tabs use the one device credential it produces ([sign-in](sign-in.md)).

## What the code is

One JSON object, as text:

```json
{"phleet":"setup","v":2,"origin":"https://fleet.example.test","accessClientId":"…","accessClientSecret":"…","enrollmentCode":"…"}
```

| Field | Meaning |
|---|---|
| `phleet` | Always `setup`. A reader refuses anything else by name |
| `v` | Format version, `2`. A reader refuses a version it does not know |
| `origin` | The HTTPS address the **phone** will use to reach the dashboard. Not always the address the page was opened at |
| `accessClientId`, `accessClientSecret` | An access-proxy service token, both or neither. Left out when the dashboard is reached without a proxy |
| `enrollmentCode` | A one-time code from the orchestrator. The phone exchanges it for its own device id and secret |

A part that is not given is **left out**, never sent as an empty string.

## What the dashboard does and does not do

- It asks the orchestrator for a fresh enrollment code (`POST /api/auth/enrollments`) each time you
  connect a phone, and builds the code in the page with your address and the service token you
  paste in.
- It sends the code nowhere and stores nothing. The service token you paste is gone when the
  section closes.
- It refuses an `http://` address rather than upgrading it, because the app refuses one too.
- It writes the code in plain ASCII: a character outside ASCII becomes a JSON `\u` escape, so a
  reader gets the exact value whichever character set its scanner assumes.
- It refuses a code too long for one QR code (2,331 bytes) as a problem with the form.

## Limits

- **The enrollment code is single-use and expires after 15 minutes.** Someone who photographs the
  screen in that window can enroll a phone of their own. It then appears in the Devices list, where
  you can revoke it.
- **The code carries no fleet-wide secret.** `ORCHESTRATOR_AUTH_TOKEN` is never in it, so a lost
  phone is revoked on its own from **Devices**, and nothing else needs rotating.
- **A code from an older dashboard** (`phleet: "control-setup"`, v1, which carried the operator
  token) is no longer produced. Make a new one from Devices.

Tests: `src/fleet-dashboard/src/phoneSetup.test.ts`.
