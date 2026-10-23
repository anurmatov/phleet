"""Actual setup block over owned temporary files only; never run setup or a runtime client."""
import pathlib, re, subprocess, tempfile, os
root = pathlib.Path(__file__).resolve().parents[2]
setup = (root / 'setup.sh').read_text()
block = setup.split('# OpenShell receiver defaults:', 1)[1].split('\n', 1)[1].split('# End OpenShell receiver defaults.', 1)[0]
helper = 'write_env_var() {' + setup.split('write_env_var() {', 1)[1].split('\n}\n', 1)[0] + '\n}\n'
example = (root / '.env.example').read_text()
compose = (root / 'docker-compose.example.yml').read_text()
keys = dict(re.findall(r'^(FLEET_MEMORY_IDENTITY_[A-Z_]+)=(.*)$', example, re.M))
assert len(keys) == 12
for key, value in keys.items():
    assert '${' + key + ':-' + value + '}' in compose, key
    assert key + '=' + value in block, key
    assert key in setup.split('# ── Canonical paths', 1)[0], key
with tempfile.TemporaryDirectory(prefix='identity-config-') as temp:
    base = pathlib.Path(temp)
    env = base / '.env'
    env.write_text('KEEP=synthetic\n')
    hygiene = setup.split('# The recorded host .env,', 1)[1].split('\n', 1)[1].split('# ── Canonical paths', 1)[0]
    script = 'set -euo pipefail\n' + hygiene + '\n[[ -z ${FLEET_MEMORY_IDENTITY_ENABLED+x} ]]\n' + helper + '\n' + block
    environment = dict(os.environ, ENV_FILE=str(env), FLEET_BASE_DIR=str(base), DRY_RUN='false', YELLOW='', NC='', FLEET_MEMORY_IDENTITY_ENABLED='true')
    subprocess.run(['bash', '-c', script], env=environment, check=True, capture_output=True)
    actual = dict(line.split('=', 1) for line in env.read_text().splitlines())
    assert all(actual[key] == value for key, value in keys.items())
    assert (base / 'identity/memory').stat().st_mode & 0o777 == 0o700
    env.write_text(env.read_text().replace('FLEET_MEMORY_IDENTITY_ENABLED=false', 'FLEET_MEMORY_IDENTITY_ENABLED=true'))
    before = env.read_bytes()
    subprocess.run(['bash', '-c', script], env=environment, check=True, capture_output=True)
    assert env.read_bytes() == before
    env.write_text('KEEP=synthetic\n')
    environment['DRY_RUN'] = 'true'
    subprocess.run(['bash', '-c', script], env=environment, check=True, capture_output=True)
    assert env.read_text() == 'KEEP=synthetic\n'
print('PASS: 12 config-key mappings, fresh defaults, preserved values, dry-run and directory mode')
