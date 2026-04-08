# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions, or pull requests.**

Phleet uses GitHub's private vulnerability reporting. To file a report:

1. Go to the [Security tab](https://github.com/anurmatov/phleet/security) of this repository.
2. Click **Report a vulnerability** (or use the direct link: https://github.com/anurmatov/phleet/security/advisories/new).
3. Fill in as much detail as you can: affected component (`Fleet.Agent` / `Fleet.Orchestrator` / `Fleet.Temporal` / `Fleet.Memory` / `Fleet.Bridge` / `fleet-dashboard`), reproduction steps, and impact assessment.

You will receive an initial acknowledgement within a few days. I'll keep you in the loop on the fix and disclosure timeline from there.

## Supported versions

Phleet is pre-1.0 and iterates on `main`. Only the latest commit on `main` is supported for security fixes. Pinning to a tag or a specific image digest is recommended for production use.

## Scope

In scope:

- Any component under `src/`
- `setup.sh`, `entrypoint.sh`, `gh-auth.sh`
- Default `docker-compose.example.yml` and `seed.example.json` configurations
- Build/CI configuration in `.github/workflows/`

Out of scope:

- User-supplied `seed.json` misconfigurations that reduce security (e.g. granting `acceptEdits` + `Bash` to untrusted roles)
- Vulnerabilities in upstream dependencies (report those to the respective upstream; we'll bump versions when fixes land)
- Social engineering, physical access, or DoS against hosted demo instances (there are none)

## What counts as a secret

When reporting or contributing, please do not include any of the following in issues, PRs, logs, or screenshots:

- Telegram bot tokens, Telegram chat IDs, or Telegram user IDs
- GitHub App private keys or installation IDs
- `ORCHESTRATOR_AUTH_TOKEN`, database passwords, or MinIO keys
- Claude / Codex OAuth credentials (`~/.claude/.credentials.json`, `~/.codex/auth.json`)
- Any content of `./fleet/.env` or `./fleet/*-credentials.json`

Secret scanning with push protection is enabled on this repository — commits containing known secret patterns will be blocked at push time.
