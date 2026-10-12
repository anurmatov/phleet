#!/usr/bin/env python3
"""Acceptance-plan validation and receipt rendering. No infrastructure actions.

Only reserve-harness-retry writes to GitHub; all other commands inspect explicit
files. There is no default capability map and no built-in deployment identity.
"""
import argparse
import fnmatch
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

WAIT_MENU = (0, 60, 300, 900, 1800, 3660, 7260, 14460)
BUCKETS = (3600, 7200, 14400, 21600)
ISOLATED = ('ci_only', 'throwaway_container', 'isolated_network', 'host_isolated')
ISOLATION = ISOLATED + ('canary', 'production_touching')
HEAD = r'[0-9a-f]{40}'
HASH = r'[0-9a-f]{64}'
HARNESS = re.compile(r'<!-- fleet-acceptance-harness-retry head=('+HEAD+r') attempt=([0-9a-f]{12}) -->')
RECEIPT = re.compile(r'<!-- fleet-acceptance-receipt attempt=([0-9a-f]{12}) head=('+HEAD+r') -->')


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate_member')
        result[key] = value
    return result


def load_json(text):
    def invalid_constant(_):
        raise ValueError('non_json_number')
    return json.loads(text, object_pairs_hook=unique_object, parse_constant=invalid_constant)


def read_json(path):
    return load_json(Path(path).read_text(encoding='utf-8'))


def extract_plan(body):
    section = re.search(r'^## Acceptance plan[ \t]*\r?\n(.*?)(?=^## |\Z)', body, re.M | re.S)
    if section is None:
        return None
    blocks = re.findall(r'^```json[ \t]*\r?\n(.*?)^```[ \t]*\r?$', section[1], re.M | re.S)
    if len(blocks) != 1:
        raise ValueError('plan_block_count')
    return load_json(blocks[0])


def plan_sha(plan):
    return hashlib.sha256(json.dumps(plan, sort_keys=True, separators=(',', ':'), ensure_ascii=False, allow_nan=False).encode('utf-8')).hexdigest()


def known_access(item):
    if not isinstance(item, str):
        return False
    if item == 'docker:local':
        return True
    kind, sep, value = item.partition(':')
    if not sep or not value or any(c.isspace() for c in value):
        return False
    if kind == 'provider':
        return value in ('claude', 'codex', 'gemini')
    return kind in ('ssh', 'docker', 'envref', 'mcp', 'tool', 'network')


def string_list(value, minimum=0, maximum=None):
    return (isinstance(value, list) and len(value) >= minimum
            and (maximum is None or len(value) <= maximum)
            and all(isinstance(x, str) and x.strip() for x in value))


def check_plan(plan):
    if plan is None:
        return 'missing', 'production'
    if not isinstance(plan, dict):
        return 'invalid', 'production'
    required_text = ('owner', 'baseline', 'cleanup', 'evidence')
    valid = (all(isinstance(plan.get(k), str) and plan[k].strip() for k in required_text)
             and string_list(plan.get('access')) and all(known_access(x) for x in plan['access'])
             and plan.get('isolation') in ISOLATION
             and type(plan.get('max_duration')) is int and plan['max_duration'] in BUCKETS
             and type(plan.get('wait_seconds')) is int and plan['wait_seconds'] in WAIT_MENU
             and string_list(plan.get('pre_stages'),1,4) and string_list(plan.get('post_stages'),0,4)
             and string_list(plan.get('prerequisites')))
    if not valid:
        return 'invalid', 'production'
    worst = (len(plan['pre_stages']) + len(plan['post_stages'])) * 3720 + plan['wait_seconds'] + 3720 + 300
    if worst > plan['max_duration']:
        return 'exceeds', 'production'
    return 'ok', 'isolated' if plan['isolation'] in ISOLATED else 'production'


def positive_issue(value):
    if not re.fullmatch(r'[1-9][0-9]*', str(value)) or int(value) > 9223372036854775807:
        raise ValueError('issue_invalid')
    return int(value)


def design_match(text, issue, workflow_id, run_id):
    try:
        wrapper = load_json(text)
        if not isinstance(wrapper, dict) or set(wrapper) != {'workflowId','runId','result'}:
            return 'no', 'wrapper_invalid'
        if wrapper['workflowId'] != workflow_id or wrapper['runId'] != run_id:
            return 'no', 'run_mismatch'
        value = wrapper['result']
        if isinstance(value, str):
            value = load_json(value.strip())
        if not isinstance(value, dict) or list(value) != ['IssueNumber']:
            return 'no', 'result_shape'
        n = value['IssueNumber']
        if type(n) is not int or not 1 <= n <= 9223372036854775807 or n != issue:
            return 'no', 'issue_mismatch'
        return 'yes', 'matched'
    except (ValueError, TypeError, UnicodeError):
        return 'no', 'invalid_json'


def prior_rounds(fix, batch, has_receipts):
    if fix is None and batch is None and not has_receipts:
        return 0,0
    if fix is None or batch is None or not re.fullmatch(r'0|[1-9][0-9]*',fix) or not re.fullmatch(r'0|[1-9][0-9]*',batch):
        return None
    f,b = int(fix),int(batch)
    return (f,b) if 0 <= b <= 5 and b <= f else None


def prior_clean(receipts):
    if not isinstance(receipts,list):
        return False
    attempts = {}
    for receipt in receipts:
        if not isinstance(receipt,dict) or not isinstance(receipt.get('attempt'),str) or not receipt['attempt']:
            return False
        attempts.setdefault(receipt['attempt'],False)
        if receipt.get('cleanup') in ('verified','ALREADY_CLEAN'):
            attempts[receipt['attempt']] = True
    return all(attempts.values())


def ci_rollup(checks,statuses):
    latest = {}
    for row in checks:
        name = row['name']
        if name not in latest or row['id'] > latest[name]['id']:
            latest[name] = row
    states = {}
    for row in statuses:
        name = row['context']
        if name not in states or row['id'] > states[name]['id']:
            states[name] = row
    bad = {'failure','cancelled','timed_out','action_required','stale','startup_failure'}
    failures = sorted({name for name,row in latest.items() if row.get('conclusion') in bad}
                      | {name for name,row in states.items() if row['state'] in ('failure','error')})
    if failures:
        return 'failure',failures
    active = [row for row in latest.values() if row.get('conclusion') not in ('skipped','neutral')]
    if (not active and not states) or any(row.get('status') != 'completed' for row in active) or any(row['state']=='pending' for row in states.values()):
        return 'pending',[]
    # Unexpected terminal values are not success evidence.
    if any(row.get('conclusion') != 'success' for row in active) or any(row['state'] != 'success' for row in states.values()):
        return 'pending',[]
    return 'success',[]


def change_class(paths):
    if not paths:
        return 'code'
    for path in paths:
        parts = PurePosixPath(path).parts
        if (path.startswith('/') or '..' in parts or '\\' in path
                or 'projects' in parts or 'roles' in parts or 'CLAUDE.md' in parts
                or parts[0] in ('src','deploy','scripts','.github')
                or any(fnmatch.fnmatchcase(p,'*seed*.json') for p in parts)):
            return 'code'
        if not (path.startswith('docs/') or parts[-1]=='README.md' or path=='CHANGELOG.md'):
            return 'code'
    return 'doc_only'


def access_allowed(items,config,capabilities):
    if not isinstance(config,dict) or not all(known_access(x) for x in items):
        return False
    refs = config.get('envRefs',[])
    if not isinstance(refs,list):
        return False
    for item in items:
        kind,_,value = item.partition(':')
        if item == 'docker:local':
            ok = config.get('mountDockerSock') is True
        elif kind in ('ssh','docker'):
            mapping = capabilities.get('items',{}).get('ssh:'+value) if isinstance(capabilities,dict) and capabilities.get('version') == 1 else None
            ok = (isinstance(mapping,dict) and string_list(mapping.get('anyEnvRef'),1)
                  and any(ref in refs for ref in mapping['anyEnvRef'])
                  and isinstance(mapping.get('sshTarget'),str) and bool(mapping['sshTarget']))
        elif kind == 'envref':
            ok = value in refs
        elif kind == 'provider':
            ok = config.get('provider') == value
        elif kind == 'network':
            ok = value in config.get('networks',[])
        elif kind == 'mcp':
            ok = any(isinstance(x,dict) and x.get('mcpName')==value for x in config.get('mcpEndpoints',[]))
        else:
            ok = any(isinstance(x,dict) and x.get('toolName')==value and x.get('isEnabled') is True for x in config.get('tools',[]))
        if not ok:
            return False
    return True


def owner_valid(owner,implementer,reviewers,config,status):
    return (isinstance(owner,str) and bool(owner.strip()) and owner.strip().lower() not in
            {x.strip().lower() for x in [implementer,*reviewers]}
            and config.get('isEnabled') is True and status.get('status') == 'running')


def attempt_hash(attempt):
    return hashlib.sha256(attempt.encode('utf-8')).hexdigest()[:12]


def harness_marker(head,attempt):
    if not re.fullmatch(HEAD,head) or not re.fullmatch(r'[0-9a-f]{12}',attempt):
        raise ValueError('marker_invalid')
    return f'<!-- fleet-acceptance-harness-retry head={head} attempt={attempt} -->'


def harness_evidence(comments,head):
    markers = []
    receipt_spent = False
    for row in comments:
        if not isinstance(row,dict) or not isinstance(row.get('body'),str):
            return 'unknown'
        body = row['body']
        matches = list(HARNESS.finditer(body))
        if body.count('<!-- fleet-acceptance-harness-retry') != len(matches):
            return 'unknown'
        markers.extend(m[0] for m in matches if m[1] == head)
        # Structured receipts are supplied by the PIN reader from protected artifacts;
        # the public receipt marker alone never claims a harness retry.
        receipt = row.get('receipt')
        if isinstance(receipt,dict) and receipt.get('head') == head:
            if type(receipt.get('harness_retry')) is not bool:
                return 'unknown'
            receipt_spent |= receipt['harness_retry']
        receipt_markers = list(RECEIPT.finditer(body))
        if body.count('<!-- fleet-acceptance-receipt') != len(receipt_markers):
            return 'unknown'
        for match in receipt_markers:
            if match[2] == head:
                flags = re.findall(r'\bharness_retry=(true|false)\b', body)
                if len(flags) != 1:
                    return 'unknown'
                receipt_spent |= flags[0] == 'true'
    if len(markers) > 1:
        return 'unknown'
    return 'yes' if markers or receipt_spent else 'no'


def gh_json(args):
    completed = subprocess.run(['gh',*args],capture_output=True,text=True,timeout=60,check=True)
    return load_json(completed.stdout)


def list_comments(repo,pr):
    pages = gh_json(['api','--paginate','--slurp',f'repos/{repo}/issues/{pr}/comments?per_page=100'])
    if not isinstance(pages,list) or not all(isinstance(p,list) for p in pages):
        raise ValueError('pagination_invalid')
    rows = [row for page in pages for row in page]
    ids = [row['id'] for row in rows]
    if len(set(ids)) != len(ids):
        raise ValueError('pagination_duplicate')
    return rows


def post_comment(repo,pr,body):
    return gh_json(['api',f'repos/{repo}/issues/{pr}/comments','-f',f'body={body}'])['id']


def reserve_retry(repo,pr,head,attempt):
    marker = harness_marker(head,attempt)
    try:
        before = harness_evidence(list_comments(repo,pr),head)
        if before == 'yes':
            return 'already_spent',None
        if before != 'no':
            return 'conflict',None
        posted = post_comment(repo,pr,marker)
        after = list_comments(repo,pr)
        matches = [row for row in after if any(m[1]==head for m in HARNESS.finditer(row['body']))]
        if (harness_evidence(after,head)=='yes' and len(matches)==1
                and matches[0]['id']==posted and matches[0]['body']==marker):
            return 'reserved',posted
        return 'conflict',None
    except (ValueError,KeyError,TypeError,RuntimeError,subprocess.SubprocessError):
        return 'conflict',None


def ssh_targets(text):
    result = {}
    if text in ('','none'):
        return result
    for pair in text.split(','):
        key,sep,value = pair.partition('=')
        if not sep or not key or not value or key in result or '@' not in value:
            raise ValueError('ssh_targets_invalid')
        result[key]=value
    return result


def scrub(text,targets,evidence=''):
    # Longest first avoids leaking a longer literal after replacing its prefix.
    literals = []
    for host,target in targets.items():
        user,_,addr = target.partition('@')
        literals.extend([(host,'<host>'),(target,'<user>@<addr>'),(user,'<user>'),(addr,'<addr>')])
    if evidence:
        literals.append((evidence,'<private-path>'))
    for old,new in sorted(literals,key=lambda x:len(x[0]),reverse=True):
        text=text.replace(old,new)
    text=re.sub(r'\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b','<envref>',text)
    text=re.sub(r'/(?:home|Users|root|workspace|tmp)/(?:[^\s\"\'<>;,)]*)','<private-path>',text)
    text=re.sub(r'\[(?:[0-9a-fA-F:]+)\]','<addr>',text)
    text=re.sub(r'(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.]*(?![\w:])','<addr>',text)
    text=re.sub(r'(?<![\w.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![\w.])','<addr>',text)
    return text


def render_comment(receipt,targets_text,deny_patterns=()):
    if not isinstance(receipt,dict) or not re.fullmatch(HEAD,str(receipt.get('head',''))):
        raise ValueError('receipt_invalid')
    attempt = receipt.get('attempt')
    if not isinstance(attempt,str) or not attempt or type(receipt.get('harness_retry')) is not bool:
        raise ValueError('receipt_invalid')
    if receipt.get('result') not in ('passed','failed','blocked','head_changed'):
        raise ValueError('result_invalid')
    targets=ssh_targets(targets_text)
    selected={key:receipt.get(key) for key in ('result','head','image_digests','exit_codes','http_counts','cleanup','commands')}
    selected['harness_retry']=receipt['harness_retry']
    text=json.dumps(selected,sort_keys=True,ensure_ascii=False,allow_nan=False)
    text=text.replace(attempt,'<attempt>')
    text=scrub(text,targets,receipt.get('evidence',''))
    # The same deny patterns are used again AFTER substitution. Literal identities
    # are checked even when embedded in an unrelated word.
    literals=[x for host,target in targets.items() for x in (host,target,*target.split('@'))]
    patterns=[r'\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+\b',r'/(?:home|Users|root|workspace|tmp)/',
              r'(?<![\w.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![\w.])',
              r'(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.]*(?![\w:])',*deny_patterns]
    if any(x in text for x in [attempt,*literals]) or any(re.search(p,text) for p in patterns):
        raise ValueError('render_denied')
    digest=hashlib.sha256(json.dumps(receipt,sort_keys=True,separators=(',',':'),ensure_ascii=False,allow_nan=False).encode()).hexdigest()
    retry='true' if receipt['harness_retry'] else 'false'
    return (f'acceptance receipt sha256={digest} harness_retry={retry}: {text}\n'
            f'<!-- fleet-acceptance-receipt attempt={attempt_hash(attempt)} head={receipt["head"]} -->')


def parser():
    p=argparse.ArgumentParser(description=__doc__)
    commands=p.add_subparsers(dest='command',required=True)
    for name in ('plan-check','plan-evidence'):
        s=commands.add_parser(name)
        g=s.add_mutually_exclusive_group(required=True)
        g.add_argument('--plan-file');g.add_argument('--body-file')
        if name=='plan-evidence':
            s.add_argument('--design-match',choices=['yes','no'],required=True)
    s=commands.add_parser('body-sha');s.add_argument('--body-file',required=True)
    s=commands.add_parser('design-result');s.add_argument('--issue',required=True,type=positive_issue)
    s.add_argument('--workflow-id',required=True);s.add_argument('--run-id',required=True);s.add_argument('--result-file',required=True)
    s=commands.add_parser('prior-rounds');s.add_argument('--fix');s.add_argument('--batch');s.add_argument('--receipts',choices=['yes','no'],required=True)
    s=commands.add_parser('prior-clean');s.add_argument('--receipts-file',required=True)
    s=commands.add_parser('change-class');s.add_argument('--paths-file',required=True)
    s=commands.add_parser('ci-rollup');s.add_argument('--checks-file',required=True);s.add_argument('--statuses-file',required=True)
    s=commands.add_parser('access-check');s.add_argument('--plan-file',required=True);s.add_argument('--config-file',required=True)
    s.add_argument('--status-file',required=True);s.add_argument('--target-agent',required=True);s.add_argument('--reviewers',required=True);s.add_argument('--capabilities',required=True)
    s=commands.add_parser('harness-evidence');s.add_argument('--comments-file',required=True);s.add_argument('--head',required=True)
    s=commands.add_parser('reserve-harness-retry');s.add_argument('--repo',required=True);s.add_argument('--pr',required=True,type=positive_issue);s.add_argument('--head',required=True);s.add_argument('--attempt-hash',required=True)
    s=commands.add_parser('render-comment');s.add_argument('--receipt',required=True);s.add_argument('--ssh-targets',required=True)
    s.add_argument('--deny-pattern',action='append',default=[])
    return p


def main(argv=None):
    a=parser().parse_args(argv)
    try:
        if a.command in ('plan-check','plan-evidence'):
            try:
                plan=extract_plan(Path(a.body_file).read_text(encoding='utf-8')) if a.body_file else read_json(a.plan_file)
                result,iso=check_plan(plan)
            except (OSError,ValueError,UnicodeError):
                plan=None;result,iso='invalid','production'
            sha=plan_sha(plan) if plan is not None else 'none'
            if a.command=='plan-evidence':
                print('PLAN_APPROVAL: '+('design' if result=='ok' and a.design_match=='yes' else 'none'))
            else:
                print('PLAN: '+result);print('ISO: '+iso)
            print('PLAN_SHA256: '+sha)
        elif a.command=='body-sha':
            print('BODY_SHA256: '+hashlib.sha256(Path(a.body_file).read_bytes()).hexdigest())
        elif a.command=='design-result':
            if not a.workflow_id.strip() or not a.run_id.strip():
                raise ValueError('run_required')
            try:
                result,reason=design_match(Path(a.result_file).read_text(encoding='utf-8'),a.issue,a.workflow_id,a.run_id)
            except (OSError,UnicodeError):
                result,reason='no','result_unavailable'
            print('DESIGN_MATCH: '+result);print('DESIGN_REASON: '+reason)
        elif a.command=='prior-rounds':
            result=prior_rounds(a.fix,a.batch,a.receipts=='yes')
            print('PRIOR_ROUNDS: '+('ok' if result is not None else 'invalid'))
            if result is not None:
                print(f'FIX_ROUND: {result[0]}\nBATCH_ROUND: {result[1]}')
        elif a.command=='prior-clean':
            print('PRIOR_ATTEMPTS_CLEAN: '+('yes' if prior_clean(read_json(a.receipts_file)) else 'no'))
        elif a.command=='change-class':
            print('CHANGE_CLASS: '+change_class(Path(a.paths_file).read_text().splitlines()))
        elif a.command=='ci-rollup':
            result,failures=ci_rollup(read_json(a.checks_file),read_json(a.statuses_file))
            print('CI: '+result);print('CI_FAILING: '+(', '.join(failures) or 'none'))
        elif a.command=='access-check':
            plan,config,status=read_json(a.plan_file),read_json(a.config_file),read_json(a.status_file)
            try:
                capabilities=read_json(a.capabilities)
            except (OSError,ValueError,UnicodeError):
                capabilities=None
            ok=owner_valid(plan['owner'],a.target_agent,a.reviewers.split(','),config,status) and access_allowed(plan['access'],config,capabilities)
            print('OWNER: '+(plan['owner'] if ok else 'invalid'))
        elif a.command=='harness-evidence':
            if not re.fullmatch(HEAD,a.head):
                raise ValueError('head_invalid')
            print('HARNESS_RETRY_SPENT: '+harness_evidence(read_json(a.comments_file),a.head))
        elif a.command=='reserve-harness-retry':
            if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',a.repo):
                raise ValueError('repo_invalid')
            result,comment=reserve_retry(a.repo,a.pr,a.head,a.attempt_hash)
            print('RESERVATION: '+result+(f' {comment}' if comment is not None else ''))
        elif a.command=='render-comment':
            receipt=read_json(a.receipt)
            output=render_comment(receipt,a.ssh_targets,a.deny_pattern)
            # Bind the posted receipt hash to the original file bytes, not re-serialized JSON.
            digest=hashlib.sha256(Path(a.receipt).read_bytes()).hexdigest()
            output=re.sub(r'sha256=[0-9a-f]{64}',f'sha256={digest}',output,count=1)
            print(output)
        return 0
    except (OSError,ValueError,TypeError,KeyError,UnicodeError):
        # Do not echo untrusted input, paths or credential-shaped values.
        print('acceptance validation failed',file=sys.stderr)
        return 1


if __name__=='__main__':
    sys.exit(main())
