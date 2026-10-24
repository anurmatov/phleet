#!/usr/bin/env python3
"""Suite resolution, verdicts, receipts and the public scrub for run.sh (#476).

Exit codes everywhere: 0 PASS, 1 FAIL, 2 INVALID, 3 BLOCKED. A suite is PASS only when at least
one test ran and nothing failed or skipped; a missing prerequisite is BLOCKED and never PASS.

Subcommands (run.sh is the only intended caller):
  resolve    --fixtures F [--manifest M] --repo R SUITE|all   -> one tab-separated line per suite
  trx        --suite S --ac A --project P --trx FILE --rc N   -> one JSON part (dotnet)
  npm        --suite S --ac A --script X --log FILE --rc N    -> one JSON part (npm)
  blocked    --suite S --ac A --reason TEXT [--project P]     -> one JSON part (prerequisite missing)
  error      --suite S --ac A --reason TEXT [--project P]     -> one JSON part (ran badly: a FAIL)
  invalid    --suite S --ac A --reason TEXT [--project P]     -> one JSON part (bad input)
  scrub      --repo R --base REF --suite S --ac A             -> one JSON part (public scrub)
  summarize  --parts FILE --suites "a b" --head SHA --dirty 0|1 --out FILE
"""
import argparse
import ipaddress
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

PASS, FAIL, INVALID, BLOCKED = 0, 1, 2, 3
VERDICTS = {PASS: 'PASS', FAIL: 'FAIL', INVALID: 'INVALID', BLOCKED: 'BLOCKED'}
# Worst first: a failure outranks a bad request, which outranks a missing prerequisite.
SEVERITY = (FAIL, INVALID, BLOCKED, PASS)

SUITE_RE = re.compile(r'^[a-z][a-z0-9-]{0,40}$')
ENV_RE = re.compile(r'^[A-Z][A-Z0-9_]{0,100}$')
KINDS = ('dotnet', 'npm', 'scrub')
TRX_NS = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'


class Invalid(Exception):
    pass


def emit(obj):
    print(json.dumps(obj, sort_keys=True))


def worst(codes):
    for code in SEVERITY:
        if code in codes:
            return code
    return INVALID


def load_strict(path):
    """JSON with duplicate keys refused: a duplicated key silently drops one of its values."""
    def pairs(items):
        out = {}
        for key, value in items:
            if key in out:
                raise Invalid(f'duplicate key {key!r} in {path}')
            out[key] = value
        return out
    try:
        with open(path, encoding='utf-8') as stream:
            return json.load(stream, object_pairs_hook=pairs)
    except FileNotFoundError:
        raise Invalid(f'file not found: {path}')
    except json.JSONDecodeError as exc:
        raise Invalid(f'invalid JSON in {path}: {exc.msg} at line {exc.lineno}')


def str_list(value, what, pattern=None):
    if not isinstance(value, list) or not all(isinstance(v, str) and v for v in value):
        raise Invalid(f'{what} must be a list of non-empty strings')
    if pattern is not None:
        for v in value:
            if not pattern.match(v):
                raise Invalid(f'{what}: invalid entry {v!r}')
    return value


def project_path(repo, fixtures, ref):
    """A project key from fixtures.json, or a repo-relative .csproj path (manifest overrides)."""
    path = fixtures['projects'].get(ref, ref)
    if not isinstance(path, str) or path.startswith('/') or '..' in Path(path).parts or not path.endswith('.csproj'):
        raise Invalid(f'project {ref!r} must be a fixtures key or a repo-relative .csproj path')
    if not (Path(repo) / path).is_file():
        raise Invalid(f'project file not found: {path}')
    return path


def cmd_resolve(args):
    fixtures = load_strict(args.fixtures)
    if fixtures.get('schemaVersion') != 1 or not isinstance(fixtures.get('suites'), dict) \
            or not isinstance(fixtures.get('projects'), dict):
        raise Invalid('fixtures.json: schemaVersion 1 with "projects" and "suites" objects required')
    suites = {}
    for name, spec in fixtures['suites'].items():
        if not SUITE_RE.match(name) or not isinstance(spec, dict) or spec.get('kind') not in KINDS:
            raise Invalid(f'fixtures.json: invalid suite {name!r}')
        suites[name] = dict(spec)
        suites[name]['requires'] = list(str_list(spec.get('requires', []), f'{name}.requires', ENV_RE))

    if args.manifest:
        manifest = load_strict(args.manifest)
        if not isinstance(manifest, dict) or manifest.get('schemaVersion') != 1:
            raise Invalid('manifest: schemaVersion 1 required')
        extra = str_list(manifest.get('requires', []), 'manifest.requires', ENV_RE)
        overrides = manifest.get('suites', {})
        if not isinstance(overrides, dict):
            raise Invalid('manifest.suites must be an object')
        for name, override in overrides.items():
            if name not in suites or not isinstance(override, dict):
                raise Invalid(f'manifest: unknown suite {name!r}')
            unknown = set(override) - {'projects', 'requires'}
            if unknown:
                raise Invalid(f'manifest: suite {name!r} may override only projects/requires, not {sorted(unknown)}')
            if 'projects' in override:
                if suites[name]['kind'] != 'dotnet':
                    raise Invalid(f'manifest: suite {name!r} has no projects to override')
                suites[name]['projects'] = str_list(override['projects'], f'manifest.{name}.projects')
            # A manifest can only ADD prerequisites; it can never remove one.
            suites[name]['requires'] += str_list(override.get('requires', []), f'manifest.{name}.requires', ENV_RE)
        for spec in suites.values():
            spec['requires'] += extra

    if args.suite == 'all':
        names = list(suites)
    elif args.suite in suites:
        names = [args.suite]
    else:
        raise Invalid(f'unknown suite {args.suite!r}; known: all {" ".join(suites)}')

    for name in names:
        spec = suites[name]
        requires = sorted(set(spec['requires']))
        if spec['kind'] == 'dotnet':
            refs = str_list(spec.get('projects', []), f'{name}.projects')
            detail = ','.join(project_path(args.repo, fixtures, ref) for ref in refs)
        elif spec['kind'] == 'npm':
            prefix = spec.get('prefix', '')
            scripts = str_list(spec.get('scripts', []), f'{name}.scripts')
            if not prefix or prefix.startswith('/') or '..' in Path(prefix).parts:
                raise Invalid(f'{name}.prefix must be repo-relative')
            detail = prefix + '|' + ','.join(scripts) + '|' + str(spec.get('minNode', '22.18'))
        else:
            detail = '-'
        print('\t'.join([name, str(spec.get('ac', '-')), spec['kind'], ','.join(requires) or '-', detail]))
    return PASS


def part(args, **fields):
    base = {'suite': args.suite, 'ac': args.ac}
    base.update(fields)
    return base


def cmd_trx(args):
    record = part(args, kind='dotnet', project=args.project, rc=args.rc, trx=args.trx,
                  filter=f'WorkflowOutcomeSuite={args.suite}')
    try:
        root = ET.parse(args.trx).getroot()
    except (OSError, ET.ParseError) as exc:
        emit(dict(record, status='error', reason=f'no readable TRX: {type(exc).__name__}'))
        return PASS
    counters = root.find(f'.//{TRX_NS}ResultSummary/{TRX_NS}Counters')
    if counters is None:
        emit(dict(record, status='error', reason='TRX has no Counters'))
        return PASS

    def count(name):
        try:
            return int(counters.get(name, '0'))
        except ValueError:
            return -1
    outcomes = [r.get('outcome', '') for r in root.iter(f'{TRX_NS}UnitTestResult')]
    # The summary under-reports skips (an xUnit Skip leaves notExecuted=0 and only shortens
    # `executed`), so take the worst of the counters, total-executed and the per-test rows.
    failed = max(count('failed') + count('error') + count('timeout') + count('aborted'),
                 sum(1 for o in outcomes if o in ('Failed', 'Error', 'Timeout', 'Aborted')))
    skipped = max(count('notExecuted') + count('inconclusive') + count('notRunnable'),
                  count('total') - count('executed'),
                  sum(1 for o in outcomes if o in ('NotExecuted', 'Inconclusive', 'NotRunnable', 'Pending')))
    emit(dict(record, status='ran', total=count('total'), executed=count('executed'), passed=count('passed'),
              failed=failed, skipped=skipped,
              # Cross-check against the per-test rows: a summary that disagrees with them is not evidence.
              rows=len(outcomes), rowsNotPassed=sum(1 for o in outcomes if o != 'Passed')))
    return PASS


NODE_SUMMARY = re.compile(r'^\s*(?:#|ℹ)\s*(tests|pass|fail|cancelled|skipped|todo)\s+(\d+)\s*$')
VITEST_SUMMARY = re.compile(r'^\s*Tests\s+(.*)$')


def cmd_npm(args):
    record = part(args, kind='npm', script=args.script, rc=args.rc, log=args.log)
    if args.script != 'test':
        emit(dict(record, status='ran', total=None, failed=0 if args.rc == 0 else 1, skipped=0))
        return PASS
    counts = {}
    try:
        text = Path(args.log).read_text(encoding='utf-8', errors='replace')
    except OSError:
        emit(dict(record, status='error', reason='no test log'))
        return PASS
    for line in text.splitlines():
        m = NODE_SUMMARY.match(line)
        if m:
            counts[m.group(1)] = int(m.group(2))
            continue
        m = VITEST_SUMMARY.match(line)
        if m:
            for number, word in re.findall(r'(\d+)\s+(passed|failed|skipped|todo)', m.group(1)):
                counts[{'passed': 'pass', 'failed': 'fail'}.get(word, word)] = int(number)
            counts['tests'] = sum(counts.get(k, 0) for k in ('pass', 'fail', 'skipped', 'todo'))
    if 'tests' not in counts:
        emit(dict(record, status='error', reason='no recognisable test summary in npm output'))
        return PASS
    emit(dict(record, status='ran', total=counts['tests'], passed=counts.get('pass', 0),
              failed=counts.get('fail', 0) + counts.get('cancelled', 0),
              skipped=counts.get('skipped', 0) + counts.get('todo', 0)))
    return PASS


def cmd_mark(args):
    emit(part(args, kind=args.kind, project=args.project, status=args.command, reason=args.reason))
    return PASS


# ── Public scrub (AC24) ───────────────────────────────────────────────────────
# Generic private-data shapes only. Deployment-specific names belong in an operator denylist that
# is supplied through the environment and never committed. Networks are built from integers so
# this file carries no private address literal of its own.
PRIVATE_NETS = [ipaddress.ip_network(n) for n in (
    (0x0A000000, 8), (0xAC100000, 12), (0xC0A80000, 16),   # RFC1918
    (0x64400000, 10),                                      # carrier-grade NAT
    (0xA9FE0000, 16),                                      # link-local
)]
IPV4 = re.compile(r'(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])')
# Long integers shaped like chat/user ids. Not inside GUIDs, hashes, versions or identifiers.
LONG_NUMBER = re.compile(r'(?<![\w.\-])-?\d{9,}(?![\w.\-])')
ALLOWED_NUMBERS = {
    '268435456',             # late-result queue max-length-bytes (256 MiB)
    '2147483647', '4294967295', '9007199254740991', '9223372036854775807',
    '72057594037927936', '72057598332895232',   # 2^56 and 2^56+2^32: the reserved key band
}
DATE_STAMP = re.compile(r'^20\d{2}(0[1-9]|1[0-2])(0[1-9]|[12]\d|3[01])(\d{6})?$')


def synthetic_number(digits):
    """Obvious fixture values: one repeated digit, a counting run, or a round number."""
    return (len(set(digits)) == 1 or digits in '12345678901234567890' or digits in '98765432109876543210'
            or digits.endswith('00000'))
EMAIL = re.compile(r'[A-Za-z0-9._%+-]+@((?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,})\b')
ALLOWED_EMAIL_DOMAINS = ('example.com', 'example.org', 'example.net', 'example', 'invalid', 'test', 'localhost')
ALLOWED_EMAILS = {'noreply@anthropic.com', 'git@github.com'}
REPO_URL = re.compile(r'(?:github\.com|raw\.githubusercontent\.com|api\.github\.com/repos)[/:]([A-Za-z0-9_.-]+)(?:/([A-Za-z0-9_.-]+))?', re.I)
OTHER_GIT_HOST = re.compile(r'\b(?:gitlab\.com|bitbucket\.org|dev\.azure\.com|[a-z0-9-]+\.ghe\.com)[/:][A-Za-z0-9_.-]+', re.I)
PUBLIC_REPOS = {'anurmatov/phleet'}
PUBLIC_OWNERS = {'owner', 'org', 'example', 'actions', 'temporalio', 'dotnet', 'microsoft', 'docker',
                 'minio', 'cli', 'openai', 'anthropics', 'google-gemini', 'nodejs', 'chrislusf'}
GITHUB_NON_OWNERS = {'orgs', 'users', 'settings', 'apps', 'login', 'features', 'about', 'marketplace'}
PRIVATE_HOST = re.compile(r'\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:internal|lan|corp|intranet|home\.arpa|ts\.net|local)(?![\w.-])', re.I)
HOME_PATH = re.compile(r'(?<![\w.])/(?:home|Users)/([^/\s"\'`<>]+)/')
PLACEHOLDER_USERS = {'user', 'you', 'runner', 'operator', '<user>', '$USER', '${USER}', 'me'}
TOKENS = [
    ('bearer_token', re.compile(r'\bBearer\s+([A-Za-z0-9._~+/=-]{16,})')),
    ('github_token', re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,})')),
    ('api_key', re.compile(r'\bsk-(?:ant-)?[A-Za-z0-9_-]{20,}')),
    ('slack_token', re.compile(r'\bxox[abprs]-[A-Za-z0-9-]{10,}')),
    ('aws_key', re.compile(r'\bAKIA[0-9A-Z]{16}\b')),
    ('telegram_bot_token', re.compile(r'\b\d{8,10}:[A-Za-z0-9_-]{35}\b')),
    ('jwt', re.compile(r'\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.')),
    ('private_key', re.compile('-----BEGIN ' + r'(?:[A-Z]+ )*' + 'PRIVATE KEY-----')),
]
PLACEHOLDER_TOKEN = re.compile(r'^(?:\$|<|\{|x{6,}|\.{3})|token|example|placeholder|your-|changeme', re.I)
EXAMPLES_DIR = 'docs/examples/workflow-outcomes/'


def mask(value):
    value = str(value)
    return (value[:3] + '…' if len(value) > 3 else '…') + f'(len={len(value)})'


def env_list(name):
    return [v.strip() for v in os.environ.get(name, '').split(',') if v.strip()]


def path_findings(path):
    out = []
    lower = path.lower()
    name = lower.rsplit('/', 1)[-1]
    if lower.startswith('fleet/'):
        out.append('runtime_data_dir')
    if re.match(r'^\.env(\..+)?$', name) and name != '.env.example':
        out.append('env_file')
    if name.endswith(('.pem', '.key', '.p12', '.pfx')) or name.startswith('id_rsa') or name.startswith('id_ed25519'):
        out.append('key_material_file')
    if re.match(r'^(ownership|classifiers|rollout)([.-][\w-]+)?\.json$', name) \
            and not lower.startswith(EXAMPLES_DIR) and not lower.startswith('tests/'):
        out.append('deployment_policy_file')
    return out


def line_findings(text, denylist, allowed_repos):
    found = []
    for address in IPV4.findall(text):
        try:
            ip = ipaddress.ip_address(address)
        except ValueError:
            continue
        if any(ip in net for net in PRIVATE_NETS):
            found.append(('private_ipv4', address))
    for number in LONG_NUMBER.findall(text):
        digits = number.lstrip('-')
        if digits not in ALLOWED_NUMBERS and not DATE_STAMP.match(digits) and not synthetic_number(digits):
            found.append(('long_numeric_id', number))
    for m in EMAIL.finditer(text):
        address, domain = m.group(0), m.group(1).lower()
        if address.lower() in ALLOWED_EMAILS or domain in ALLOWED_EMAIL_DOMAINS \
                or domain.endswith(tuple('.' + d for d in ALLOWED_EMAIL_DOMAINS)) \
                or domain.endswith('users.noreply.github.com'):
            continue
        found.append(('email_address', address))
    for m in REPO_URL.finditer(text):
        owner = m.group(1).lower()
        repo = (m.group(2) or '').lower().removesuffix('.git')
        full = f'{owner}/{repo}' if repo else owner
        if owner in GITHUB_NON_OWNERS or full in PUBLIC_REPOS or owner in PUBLIC_OWNERS \
                or full in allowed_repos or f'{owner}/*' in allowed_repos:
            continue
        found.append(('repo_reference', full))
    for m in OTHER_GIT_HOST.finditer(text):
        found.append(('repo_host', m.group(0)))
    for m in PRIVATE_HOST.finditer(text):
        found.append(('private_hostname', m.group(0)))
    for m in HOME_PATH.finditer(text):
        if m.group(1) not in PLACEHOLDER_USERS:
            found.append(('home_path', m.group(0)))
    for rule, pattern in TOKENS:
        for m in pattern.finditer(text):
            value = m.group(1) if m.groups() else m.group(0)
            if rule == 'bearer_token' and PLACEHOLDER_TOKEN.search(value):
                continue
            found.append((rule, value))
    lowered = text.lower()
    for index, (kind, entry) in denylist:
        if (kind == 're' and entry.search(text)) or (kind == 'literal' and entry in lowered):
            # Never echo an operator denylist entry or what it matched.
            found.append((f'denylist:#{index}', None))
    return found


def load_denylist():
    path = os.environ.get('WORKFLOW_OUTCOMES_SCRUB_DENYLIST', '')
    if not path:
        return [], False
    try:
        lines = Path(path).read_text(encoding='utf-8').splitlines()
    except OSError:
        raise Invalid('WORKFLOW_OUTCOMES_SCRUB_DENYLIST names a file that cannot be read')
    entries = []
    for number, raw in enumerate(lines, 1):
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('re:'):
            try:
                entries.append((number, ('re', re.compile(line[3:], re.I))))
            except re.error:
                raise Invalid(f'denylist line {number}: invalid regular expression')
        else:
            entries.append((number, ('literal', line.lower())))
    return entries, True


def git(repo, *argv):
    return subprocess.run(['git', '-C', repo, *argv], capture_output=True, text=True, errors='replace')


def cmd_scrub(args):
    record = part(args, kind='scrub', base=args.base)
    denylist, supplied = load_denylist()
    resolved = git(args.repo, 'rev-parse', '--verify', '--quiet', args.base + '^{commit}')
    if resolved.returncode != 0:
        emit(dict(record, status='blocked', reason=f'base ref {args.base!r} does not resolve; fetch it or set WORKFLOW_OUTCOMES_SCRUB_BASE'))
        return PASS
    diff = git(args.repo, 'diff', '--no-color', '--no-ext-diff', '--unified=0', '--diff-filter=ACMR',
               f'{args.base}...HEAD')
    if diff.returncode != 0:
        emit(dict(record, status='blocked', reason='git diff failed'))
        return PASS
    allowed_repos = {r.lower() for r in env_list('WORKFLOW_OUTCOMES_SCRUB_ALLOW_REPOS')}
    allowed_binary = set(env_list('WORKFLOW_OUTCOMES_SCRUB_ALLOW_BINARY'))
    findings, files, lines, current, number = [], set(), 0, None, 0
    for raw in diff.stdout.splitlines():
        if raw.startswith('+++ '):
            current = raw[6:] if raw.startswith('+++ b/') else None
            if current:
                files.add(current)
                for rule in path_findings(current):
                    findings.append({'file': current, 'line': 0, 'rule': rule, 'excerpt': current})
            continue
        if raw.startswith('Binary files ') and ' and b/' in raw:
            path = raw.split(' and b/', 1)[1].rsplit(' differ', 1)[0]
            files.add(path)
            if path not in allowed_binary:
                findings.append({'file': path, 'line': 0, 'rule': 'binary_unscanned', 'excerpt': path})
            continue
        if raw.startswith('@@'):
            m = re.match(r'^@@ -\d+(?:,\d+)? \+(\d+)', raw)
            number = int(m.group(1)) if m else 0
            continue
        if raw.startswith('+') and current:
            lines += 1
            for rule, value in line_findings(raw[1:], denylist, allowed_repos):
                findings.append({'file': current, 'line': number, 'rule': rule,
                                 'excerpt': '<redacted>' if value is None else mask(value)})
            number += 1
    emit(dict(record, status='ran', baseSha=resolved.stdout.strip(), filesScanned=len(files), linesScanned=lines,
              operatorDenylist=supplied, findings=findings, failed=len(findings), skipped=0, total=lines,
              notes=['AC24 also requires an independent manual scrub; this check is generic patterns only.']))
    return PASS


def suite_verdict(parts):
    """FAIL outranks INVALID outranks BLOCKED; PASS needs >0 tests and no failure or skip."""
    if not parts:
        return INVALID, ['no results recorded']
    failing, invalid, blocked = [], [], []
    for p in parts:
        where = p.get('project') or p.get('script') or p.get('kind')
        status = p.get('status')
        if status == 'error':
            failing.append(f'{where}: {p.get("reason")}')
        elif status == 'invalid':
            invalid.append(f'{where}: INVALID {p.get("reason")}')
        elif status == 'blocked':
            blocked.append(f'{where}: BLOCKED {p.get("reason")}')
        elif status == 'ran':
            if p.get('rc', 0) != 0:
                failing.append(f'{where}: exit code {p.get("rc")}')
            if (p.get('failed') or 0) > 0:
                failing.append(f'{where}: {p.get("failed")} failed')
            if (p.get('skipped') or 0) > 0:
                failing.append(f'{where}: {p.get("skipped")} skipped (a skip is not evidence)')
            if p.get('kind') == 'dotnet' and p.get('rows') != p.get('total'):
                failing.append(f'{where}: TRX summary disagrees with its result rows')
        else:
            failing.append(f'{where}: unknown status {status!r}')
    reasons = failing + invalid + blocked
    if failing:
        return FAIL, reasons
    if invalid:
        return INVALID, reasons
    if blocked:
        return BLOCKED, reasons
    counted = [p for p in parts if p.get('kind') in ('dotnet', 'npm') and p.get('total') is not None]
    if counted and sum(p['total'] for p in counted) <= 0:
        return FAIL, [f'no test carries [Trait("WorkflowOutcomeSuite", "{parts[0]["suite"]}")] in any mapped project']
    return PASS, reasons


def cmd_summarize(args):
    parts = []
    if Path(args.parts).exists():
        for line in Path(args.parts).read_text(encoding='utf-8').splitlines():
            if line.strip():
                parts.append(json.loads(line))
    suites, codes = [], []
    for name in args.suites.split():
        mine = [p for p in parts if p.get('suite') == name]
        code, reasons = suite_verdict(mine)
        codes.append(code)
        suites.append({'suite': name, 'ac': mine[0].get('ac') if mine else None, 'verdict': VERDICTS[code],
                       'rc': code, 'reasons': reasons, 'parts': mine})
    overall = worst(codes)
    receipt = {
        'receipt': 'workflow-outcomes-integration', 'schemaVersion': 1,
        'head': args.head, 'dirty': args.dirty == '1',
        'finishedAt': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'verdict': VERDICTS[overall], 'rc': overall, 'suites': suites,
    }
    Path(args.out).write_text(json.dumps(receipt, indent=2, sort_keys=True) + '\n', encoding='utf-8')
    print(json.dumps(receipt, indent=2, sort_keys=True))
    return overall


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)

    p = sub.add_parser('resolve')
    p.add_argument('--fixtures', required=True)
    p.add_argument('--manifest')
    p.add_argument('--repo', required=True)
    p.add_argument('suite')

    def common(name):
        q = sub.add_parser(name)
        q.add_argument('--suite', required=True)
        q.add_argument('--ac', default='-')
        return q

    p = common('trx')
    p.add_argument('--project', required=True)
    p.add_argument('--trx', required=True)
    p.add_argument('--rc', type=int, required=True)
    p = common('npm')
    p.add_argument('--script', required=True)
    p.add_argument('--log', required=True)
    p.add_argument('--rc', type=int, required=True)
    for name in ('blocked', 'error', 'invalid'):
        p = common(name)
        p.add_argument('--reason', required=True)
        p.add_argument('--project')
        p.add_argument('--kind', default='dotnet')
    p = common('scrub')
    p.add_argument('--repo', required=True)
    p.add_argument('--base', required=True)
    p = sub.add_parser('summarize')
    p.add_argument('--parts', required=True)
    p.add_argument('--suites', required=True)
    p.add_argument('--head', required=True)
    p.add_argument('--dirty', required=True)
    p.add_argument('--out', required=True)

    args = parser.parse_args(argv)
    handler = {'resolve': cmd_resolve, 'trx': cmd_trx, 'npm': cmd_npm, 'blocked': cmd_mark, 'error': cmd_mark,
               'invalid': cmd_mark, 'scrub': cmd_scrub, 'summarize': cmd_summarize}[args.command]
    try:
        return handler(args)
    except Invalid as exc:
        print(f'INVALID: {exc}', file=sys.stderr)
        return INVALID


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
