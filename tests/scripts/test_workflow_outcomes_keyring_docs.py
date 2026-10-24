"""Check directory backup/reset guidance against source volume declarations; never access a volume."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[2]
GUIDANCE = 'docs/runbooks/workflow-outcomes.md#12-backup-keyring-restore-and-key-rotation'


class KeyringDocumentationTests(unittest.TestCase):
    def test_directory_state_claims_explain_the_named_keyring_exception(self):
        readme = (ROOT / 'README.md').read_text()
        claude = (ROOT / 'CLAUDE.md').read_text()
        sections = {
            'readme setup': readme.split('### 2. Run the setup wizard', 1)[1].split('### 3.', 1)[0],
            'readme start-stop': readme.split('### Start/stop the stack later', 1)[1].split('### Upgrade', 1)[0],
            'readme configuration': readme.split('## Configuration', 1)[1].split('### Size warnings', 1)[0],
            'claude quick-start': claude.split('## Quick Start', 1)[1].split('2. Fill in', 1)[0],
            'claude runtime state': claude.split('The tracked repo root stays clean:', 1)[1].split('Configuration priority', 1)[0],
        }
        for name, text in sections.items():
            with self.subTest(section=name):
                self.assertIn('workflow_outcomes_keys', text)
                self.assertIn(GUIDANCE, text)
                self.assertRegex(text.lower(), r'archiv')
                self.assertRegex(text.lower(), r'remov')
                self.assertRegex(text.lower(), r'neither captures nor resets')
                self.assertNotIn('rm -rf fleet/', text)
                self.assertNotIn('no named Docker volumes', text)

    def test_linked_guidance_requires_paired_backup_restore_and_key_retention(self):
        path, anchor = GUIDANCE.split('#')
        text = (ROOT / path).read_text()
        heading = '12. Backup, keyring restore and key rotation'
        self.assertEqual(anchor, re.sub(r'[^a-z0-9 -]', '', heading.lower()).replace(' ', '-'))
        section = text.split('## ' + heading, 1)[1].split('## 13.', 1)[0]
        self.assertIn('Back up together', section)
        self.assertIn('fleet-mysql', section)
        self.assertIn('workflow_outcomes_keys', section)
        self.assertIn('**Restore:**', section)
        self.assertIn('Never prune old keys', section)
        self.assertIn('unresolved indefinitely', section)

    def test_documented_keyring_is_still_a_separate_named_volume_in_source(self):
        compose = (ROOT / 'docker-compose.example.yml').read_text()
        self.assertIn('- workflow_outcomes_keys:/var/lib/phleet/workflow-outcomes/keys', compose)
        self.assertRegex(compose.split('\nvolumes:', 1)[1], r'(?m)^  workflow_outcomes_keys:\s*$')


if __name__ == '__main__':
    unittest.main()
