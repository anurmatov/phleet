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
        # A gemini agent runs the Antigravity CLI: a release binary, pinned with its checksums.
        self.assertIn('ARG AGY_CLI_VERSION=1.5.14', docker)
        self.assertNotIn('@google/gemini-cli', docker)

    def test_the_antigravity_cli_is_pinned_by_checksum_and_cannot_update_itself(self):
        docker = (ROOT / 'Dockerfile').read_text()
        pinned = dict(re.findall(r'(amd64|arm64)\) AGY_ASSET=agy_cli_linux_\w+\.tar\.gz; AGY_SHA256=([0-9a-f]{64}) ;;', docker))
        self.assertEqual({'amd64', 'arm64'}, set(pinned))
        self.assertNotEqual(pinned['amd64'], pinned['arm64'])
        self.assertIn('sha256sum -c -', docker)
        self.assertRegex(docker, r'(?m)^ENV AGY_CLI_DISABLE_AUTO_UPDATE=true$')
        self.assertIn('[ "$ACTUAL_AGY_VERSION" = "$AGY_CLI_VERSION" ]', docker)
        # The harness that checks the binary's behaviour reads its pin from the Dockerfile.
        harness = (ROOT / 'tests/antigravity-cli/run.sh').read_text()
        self.assertIn("sed -n 's/^ARG AGY_CLI_VERSION=//p'", harness)
        self.assertNotRegex(harness, r'[0-9a-f]{64}')

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
