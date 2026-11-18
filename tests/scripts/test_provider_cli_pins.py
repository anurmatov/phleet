"""Keep the image, live CI install and native fixture on the reviewed CLI versions."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]


class ProviderCliPinsTests(unittest.TestCase):
    def test_image_and_native_fixture_use_exact_target_versions(self):
        docker = (ROOT / 'Dockerfile').read_text()
        fixture = (ROOT / 'tests/secret-proxy/mcp-headers.sh').read_text()
        for image_arg, fixture_var, version in (
            ('CLAUDE_CODE_VERSION', 'CLAUDE_PIN', '2.1.327'),
            ('CODEX_CLI_VERSION', 'CODEX_PIN', '0.170.0'),
        ):
            self.assertRegex(docker, rf'(?m)^ARG {image_arg}={re.escape(version)}$')
            self.assertRegex(fixture, rf'(?m)^{fixture_var}={re.escape(version)}$')
        self.assertIn('ARG GEMINI_CLI_VERSION=0.40.1', docker)

    def test_active_workflow_installs_match_image_pins(self):
        docker = (ROOT / 'Dockerfile').read_text()
        pins = {
            '@anthropic-ai/claude-code': re.search(r'ARG CLAUDE_CODE_VERSION=(\S+)', docker)[1],
            '@openai/codex': re.search(r'ARG CODEX_CLI_VERSION=(\S+)', docker)[1],
        }
        installs = []
        for workflow in (ROOT / '.github/workflows').glob('*.y*ml'):
            for package, version in re.findall(r'(@anthropic-ai/claude-code|@openai/codex)@([\d.]+)', workflow.read_text()):
                installs.append(package)
                self.assertEqual(pins[package], version, str(workflow))
        self.assertIn('@openai/codex', installs)
