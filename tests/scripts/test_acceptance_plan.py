import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('acceptance_plan', Path(__file__).resolve().parents[2] / 'scripts/acceptance_plan.py')
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)
H = 'a' * 40


def plan(**changes):
    p = dict(owner='agent3', access=['envref:TEST_CREDENTIAL_A'], isolation='isolated_network',
             max_duration=21600, pre_stages=['setup', 'initial'], wait_seconds=3660,
             post_stages=[], prerequisites=['envref:TEST_CREDENTIAL_A'], baseline='metadata',
             cleanup='owned resources', evidence='<protected-path>')
    return p | changes


class PlanTests(unittest.TestCase):
    def test_duration_and_menu(self):
        self.assertEqual(('exceeds', 'production'), module.check_plan(plan(max_duration=14400)))
        self.assertEqual(('ok', 'isolated'), module.check_plan(plan()))
        for p in [plan(wait_seconds=61), plan(access=['unknown:item']), plan(pre_stages=[]),
                  plan(max_duration=True), plan(wait_seconds=True), plan(owner='')]:
            with self.subTest(p=p):
                self.assertEqual(('invalid', 'production'), module.check_plan(p))

    def test_isolation(self):
        for iso in ('ci_only', 'throwaway_container', 'isolated_network', 'host_isolated'):
            self.assertEqual(('ok', 'isolated'), module.check_plan(plan(isolation=iso)))
        for iso in ('canary', 'production_touching'):
            self.assertEqual(('ok', 'production'), module.check_plan(plan(isolation=iso)))
        self.assertEqual(('missing', 'production'), module.check_plan(None))

    def test_canonical_hash_and_body(self):
        p = plan()
        body = 'text\n## Acceptance plan\n```json\n' + json.dumps(p) + '\n```\n'
        self.assertEqual(p, module.extract_plan(body))
        self.assertEqual(module.plan_sha(p), module.plan_sha(dict(reversed(list(p.items())))))
        self.assertNotEqual(module.plan_sha(p), module.plan_sha(plan(wait_seconds=0)))
        with self.assertRaises(ValueError):
            module.extract_plan(body + '```json\n{}\n```')
        with self.assertRaises(ValueError):
            module.load_json('{"owner":"a","owner":"b"}')

    def test_prior_rounds(self):
        for fix, batch, receipts, expected in [('3','3',True,(3,3)), (None,None,False,(0,0)),
                ('3','4',True,None), ('3','6',True,None), ('3','-1',True,None),
                ('3','x',True,None), (None,None,True,None), ('01','0',True,None)]:
            with self.subTest(fix=fix,batch=batch):
                self.assertEqual(expected, module.prior_rounds(fix,batch,receipts))

    def test_clean_requires_each_attempt(self):
        self.assertTrue(module.prior_clean([]))
        self.assertTrue(module.prior_clean([dict(attempt='one', cleanup='verified')]))
        self.assertFalse(module.prior_clean([dict(attempt='one', cleanup='verified'), dict(attempt='two', cleanup='residue')]))
        self.assertFalse(module.prior_clean([dict(attempt='one', cleanup='deferred')]))


class DesignTests(unittest.TestCase):
    def test_strict_results(self):
        for r in [dict(IssueNumber=441), '  {"IssueNumber":441}  ']:
            self.assertEqual('yes', module.design_match(json.dumps(dict(workflowId='wf-1', runId='r-1', result=r)),441,'wf-1','r-1')[0])
        raw = ['null', '"APPROVED:441"', '{"IssueNumber":"441"}', '{"IssueNumber":442}',
               '{"IssueNumber":0}', '{"IssueNumber":-441}', '{"IssueNumber":441.0}',
               '{"IssueNumber":4.41e2}', '{"IssueNumber":true}', '{"IssueNumber":441,"Extra":1}',
               '{"IssueNumber":441,"IssueNumber":441}', '{"issueNumber":441}',
               '["{\\"IssueNumber\\":441}"]', '{"IssueNumber":9223372036854775808}',
               'NaN', json.dumps(json.dumps('{"IssueNumber":441}'))]
        for r in raw:
            text = '{"workflowId":"wf-1","runId":"r-1","result":' + r + '}'
            with self.subTest(r=r):
                self.assertEqual('no', module.design_match(text,441,'wf-1','r-1')[0])
        for text in ['', 'APPROVED:441', '{"IssueNumber":441}', 'Error getting workflow result: missing',
                     '{"workflowId":"wf-1","runId":"r-2","result":{"IssueNumber":441}}',
                     '{"workflowId":"wf-2","runId":"r-1","result":{"IssueNumber":441}}',
                     '{"workflowId":"wf-1","result":{"IssueNumber":441}}',
                     '{"workflowId":"wf-1","runId":"r-1","result":{"IssueNumber":441},"x":0}']:
            with self.subTest(text=text):
                self.assertEqual('no', module.design_match(text,441,'wf-1','r-1')[0])

    def test_issue_argument(self):
        for issue in ('0','x','-1','01','9223372036854775808'):
            with self.assertRaises(ValueError):
                module.positive_issue(issue)


class EvidenceTests(unittest.TestCase):
    def test_harness_markers_and_receipts(self):
        marker = module.harness_marker(H,'b'*12)
        self.assertEqual('no', module.harness_evidence([],H))
        self.assertEqual('yes', module.harness_evidence([dict(body=marker)],H))
        self.assertEqual('unknown', module.harness_evidence([dict(body=marker),dict(body=marker)],H))
        self.assertEqual('unknown', module.harness_evidence([dict(body='<!-- fleet-acceptance-harness-retry malformed -->')],H))
        self.assertEqual('yes', module.harness_evidence([dict(body='',receipt=dict(head=H,harness_retry=True))],H))

    def test_reservation_conflict_and_crash_evidence(self):
        marker = module.harness_marker(H,'b'*12)
        with patch.object(module,'list_comments',side_effect=[[],[dict(id=7,body=marker)]]) as listing, patch.object(module,'post_comment',return_value=7):
            self.assertEqual(('reserved',7), module.reserve_retry('owner/repo',1,H,'b'*12))
        with patch.object(module,'list_comments',return_value=[dict(id=7,body=marker)]), patch.object(module,'post_comment') as post:
            self.assertEqual(('already_spent',None), module.reserve_retry('owner/repo',1,H,'c'*12))
            post.assert_not_called()
        with patch.object(module,'list_comments',side_effect=RuntimeError('listing')):
            self.assertEqual(('conflict',None), module.reserve_retry('owner/repo',1,H,'b'*12))


class CiTests(unittest.TestCase):
    def test_rollup(self):
        success = dict(name='test',id=2,status='completed',conclusion='success')
        old = dict(name='test',id=1,status='completed',conclusion='failure')
        cases = [([],[],'pending'), ([dict(success,conclusion='skipped')],[],'pending'),
                 ([dict(success,conclusion='neutral')],[],'pending'), ([old],[],'failure'),
                 ([old,success],[],'success'), ([success],[dict(context='lint',id=1,state='pending')],'pending'),
                 ([dict(success,status='in_progress',conclusion=None)],[],'pending'),
                 ([success],[dict(context='lint',id=1,state='error')],'failure')]
        for checks,statuses,want in cases:
            self.assertEqual(want,module.ci_rollup(checks,statuses)[0])

    def test_change_class(self):
        self.assertEqual('doc_only',module.change_class(['docs/x.md','README.md','CHANGELOG.md']))
        for path in ['src/Fleet.Orchestrator/projects/x/context.md','projects/x/context.md',
                     'roles/a/system.md','CLAUDE.md','seed.json','docs/projects/x/README.md',
                     'docs/example-seed.json','../docs/x.md']:
            self.assertEqual('code',module.change_class([path]))
        self.assertEqual('code',module.change_class([]))


class AccessTests(unittest.TestCase):
    def test_capabilities(self):
        cap = dict(version=1, items={'ssh:host-a':dict(anyEnvRef=['TEST_CREDENTIAL_A'],sshTarget='user-x@192.0.2.10')})
        config = dict(envRefs=['TEST_CREDENTIAL_A'],tools=[dict(toolName='mcp__s__t',isEnabled=True)],
                      provider='codex',networks=['isolated'],mcpEndpoints=[dict(mcpName='s')],mountDockerSock=True)
        self.assertTrue(module.access_allowed(['ssh:host-a','docker:host-a','tool:mcp__s__t','envref:TEST_CREDENTIAL_A','provider:codex','network:isolated','mcp:s','docker:local'],config,cap))
        self.assertFalse(module.access_allowed(['ssh:host-a'],dict(config,envRefs=[]),cap))
        self.assertFalse(module.access_allowed(['ssh:host-unknown'],config,cap))
        self.assertFalse(module.access_allowed(['ssh:host-a'],config,None))
        self.assertTrue(module.access_allowed(['envref:TEST_CREDENTIAL_A'],config,None))
        self.assertFalse(module.access_allowed(['tool:mcp__s__t'],dict(config,tools=[dict(toolName='mcp__s__t',isEnabled=False)]),cap))

    def test_independence_and_liveness(self):
        self.assertTrue(module.owner_valid('agent3', 'agent1', ['agent2'], dict(isEnabled=True),dict(status='running')))
        for owner in ['agent1','AGENT2']:
            self.assertFalse(module.owner_valid(owner,'agent1',['agent2'],dict(isEnabled=True),dict(status='running')))
        self.assertFalse(module.owner_valid('agent3','agent1',[],dict(isEnabled=False),dict(status='running')))


class RenderTests(unittest.TestCase):
    def test_scrub_and_hash(self):
        receipt=json.loads((Path(__file__).parent/'fixtures/receipt.example.json').read_text())
        text=module.render_comment(receipt,'host-a=user-x@192.0.2.10')
        for value in ['host-a','user-x','192.0.2.10','TEST_CREDENTIAL_A','/home/user-x','wf-test-a1']:
            self.assertNotIn(value,text)
        for value in ['<user>@<addr>','<envref>','<private-path>', 'attempt='+module.attempt_hash('wf-test-a1')]:
            self.assertIn(value,text)
        self.assertIn('<addr>',module.scrub('https://[2001:db8::10]/x',{}))
        self.assertNotIn('host-a',module.scrub('prefixhost-asuffix',{'host-a':'user-x@192.0.2.10'}))
        with self.assertRaises(ValueError):
            module.render_comment(receipt,'host-a=user-x@192.0.2.10',deny_patterns=['result'])

class CliTests(unittest.TestCase):
    def cli(self, *args):
        import subprocess,sys
        return subprocess.run([sys.executable,str(Path(__file__).resolve().parents[2]/'scripts/acceptance_plan.py'),*args],text=True,capture_output=True)

    def test_help_and_missing_arguments(self):
        self.assertEqual(0,self.cli('--help').returncode)
        for args in [('design-result',),('design-result','--issue','0'),('access-check',),('render-comment',)]:
            self.assertNotEqual(0,self.cli(*args).returncode)

    def test_cli_design_utf8_missing_and_wrapper(self):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'result.json'
            args=('design-result','--issue','441','--workflow-id','wf-1','--run-id','r-1','--result-file',str(path))
            self.assertIn('DESIGN_MATCH: no',self.cli(*args).stdout)
            path.write_bytes(b'\xff')
            self.assertIn('DESIGN_MATCH: no',self.cli(*args).stdout)
            path.write_text('{"workflowId":"wf-1","runId":"r-1","result":{"IssueNumber":441}}')
            self.assertIn('DESIGN_MATCH: yes',self.cli(*args).stdout)
            path.write_text('"{\\"IssueNumber\\":441}"')
            self.assertIn('DESIGN_MATCH: no',self.cli(*args).stdout)

    def test_renderer_file_hash_and_denial_empty_stdout(self):
        import hashlib
        path=Path(__file__).parent/'fixtures/receipt.example.json'
        args=('render-comment','--receipt',str(path),'--ssh-targets','host-a=user-x@192.0.2.10')
        result=self.cli(*args)
        self.assertEqual(0,result.returncode)
        self.assertIn(hashlib.sha256(path.read_bytes()).hexdigest(),result.stdout)
        denied=self.cli(*args,'--deny-pattern','result')
        self.assertNotEqual(0,denied.returncode)
        self.assertEqual('',denied.stdout)

    def test_cli_plan_missing_and_iso(self):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'plan.json'
            result=self.cli('plan-check','--plan-file',str(path))
            self.assertIn('PLAN: invalid',result.stdout)
            self.assertIn('ISO: production',result.stdout)
            for iso,want in [('isolated_network','isolated'),('canary','production'),('production_touching','production')]:
                path.write_text(json.dumps(plan(isolation=iso)))
                result=self.cli('plan-check','--plan-file',str(path))
                self.assertIn('PLAN: ok',result.stdout)
                self.assertIn('ISO: '+want,result.stdout)

    def test_cli_body_hash_is_exact_bytes(self):
        import hashlib
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'body';path.write_bytes(b'body with no final newline')
            self.assertEqual('BODY_SHA256: '+hashlib.sha256(path.read_bytes()).hexdigest(),self.cli('body-sha','--body-file',str(path)).stdout.strip())

    def test_access_missing_explicit_map_argument_fails(self):
        self.assertNotEqual(0,self.cli('access-check','--plan-file','x','--config-file','x','--status-file','x','--target-agent','agent1','--reviewers','agent2').returncode)

    def test_harness_receipt_is_durable_and_malformed_unknown(self):
        body='acceptance harness_retry=true\n<!-- fleet-acceptance-receipt attempt='+('b'*12)+' head='+H+' -->'
        self.assertEqual('yes',module.harness_evidence([dict(body=body)],H))
        self.assertEqual('unknown',module.harness_evidence([dict(body=body.replace('true','invalid'))],H))
