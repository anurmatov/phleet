"""verify-workflow-outcomes.sh plan (#476): only an explicit healthy /health object is clear."""
import http.server
import json
import os
import subprocess
import tempfile
import threading
import unittest
from datetime import datetime, timezone
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'verify-workflow-outcomes.sh'


def coverage():
    now = datetime.now(timezone.utc).isoformat()
    return {'enabled': True, 'degraded': False, 'namespaces': [{
        'namespace': 'namespace-a', 'state': 'healthy', 'retentionSeconds': 604800, 'enrolledAt': now,
        'lastRecentPassAt': now, 'lastFullPassAt': now, 'knownPending': 0, 'gaps': []}]}


class Stub(http.server.BaseHTTPRequestHandler):
    health = (200, '{"state":"healthy","incidents":[]}')

    def log_message(self, *args):
        pass

    def do_GET(self):
        code, body = (200, json.dumps(coverage())) if self.path.endswith('/coverage') else Stub.health
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(body.encode())


class PlanHealthTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = http.server.HTTPServer(('127.0.0.1', 0), Stub)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.tmp = tempfile.TemporaryDirectory()
        cls.token = Path(cls.tmp.name) / 'token'
        cls.token.write_text('placeholder-operator-token')

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.tmp.cleanup()

    def plan(self, code, body):
        Stub.health = (code, body)
        env = dict(os.environ, WORKFLOW_OUTCOMES_ORCHESTRATOR_URL=f'http://127.0.0.1:{self.server.server_port}',
                   WORKFLOW_OUTCOMES_OPERATOR_TOKEN_FILE=str(self.token))
        return subprocess.run(['bash', str(SCRIPT), 'plan', '--namespace', 'namespace-a'],
                              env=env, capture_output=True, text=True, timeout=60)

    def test_explicit_healthy_object_is_clear(self):
        result = self.plan(200, '{"state":"healthy","incidents":[]}')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('No coverage gap reported', result.stdout)

    def test_anything_else_is_not_clear(self):
        for code, body in [(200, '{}'), (200, '[]'), (200, 'null'), (200, '"healthy"'), (200, '{"state":"degraded"}'),
                           (200, '{"state":true}'), (200, '{"state":"Healthy"}'), (200, 'not json'), (200, ''),
                           (500, '{"state":"healthy"}')]:
            with self.subTest(code=code, body=body):
                result = self.plan(code, body)
                self.assertNotEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertNotIn('No coverage gap reported', result.stdout)


if __name__ == '__main__':
    unittest.main()
