"""The approved native identity fixture must execute against CI's candidate image."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]


class NativeMcpCiTests(unittest.TestCase):
    def test_candidate_image_job_runs_native_identity_fixture_without_skip(self):
        workflow = (ROOT / '.github/workflows/ci.yml').read_text()
        job = workflow.split('\n  agent-image:\n', 1)[1].split('\n  minio-init-dependencies:', 1)[0]
        command = 'PROXY_IMAGE=fleet:secrets-ci AGENT_IMAGE=fleet:agent-ci bash tests/secret-proxy/mcp-headers.sh'
        self.assertIn(command, job)
        fixture = job.index(command)
        self.assertLess(job.index('docker build -t fleet:agent-ci .'), fixture)
        self.assertLess(job.index('docker build -t fleet:secrets-ci'), fixture)
        self.assertLess(job.index('uses: actions/setup-dotnet@v4'), fixture)
        self.assertIn("dotnet-version: '10.0.x'", job[:fixture])
        step = job[:fixture].rsplit('      - name:', 1)[1] + command
        self.assertNotIn('continue-on-error:', step)
        self.assertNotIn('if:', step)
        self.assertIn('timeout-minutes:', step)
        self.assertEqual(command, job[fixture:].splitlines()[0])
