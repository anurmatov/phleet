"""Public artifact scan. Deployment-specific deny lists are deliberately external."""
import ipaddress
import json
from pathlib import Path
import re
import unittest

ROOT=Path(__file__).resolve().parents[2]
NETWORKS=(ipaddress.ip_network('192.0.2.0/24'),ipaddress.ip_network('2001:db8::/32'))


def check_text(text):
    addresses=re.findall(r'(?<![\w.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![\w.])',text)
    addresses+=re.findall(r'(?<![\w:])(?:[0-9a-fA-F]{0,4}:){2,}[0-9a-fA-F:.]*(?![\w:])',text)
    for address in addresses:
        try:
            ip=ipaddress.ip_address(address)
        except ValueError:
            continue  # Not an IP token (e.g. a pattern or a template filter).
        if not any(ip.version==n.version and ip in n for n in NETWORKS):
            raise ValueError('non_documentation_address')
    for target in re.findall(r'[\w<>.-]+@[\w<>.:\[\]-]+',text):
        if target not in ('<user>@<addr>','user-x@192.0.2.10'):
            raise ValueError('target_not_placeholder')
    for env in re.findall(r'envref:([A-Za-z0-9_<>]+)',text):
        if env not in ('<NAME>','TEST_CREDENTIAL_A','TEST_CREDENTIAL_B'):
            raise ValueError('envref_not_placeholder')


class PublicPlaceholderTests(unittest.TestCase):
    def test_public_sources(self):
        for path in [ROOT/'scripts/acceptance_plan.py',ROOT/'docs/pr-acceptance.md']:
            if path.exists():
                check_text(path.read_text())
        for path in (ROOT/'tests/scripts/fixtures').glob('*.json'):
            text=path.read_text()
            check_text(text)
            for address in re.findall(r'(?<![\w.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![\w.])',text):
                if address != '192.0.2.10':
                    raise ValueError('fixture_address_not_placeholder')
            # All explicit identity fields and capability-map keys are constrained.
            def walk(value):
                if isinstance(value,dict):
                    for key,v in value.items():
                        if key in ('host','hostname') and v not in ('host-a','host-b'):
                            raise ValueError('host_not_placeholder')
                        if key=='user' and v != 'user-x':
                            raise ValueError('user_not_placeholder')
                        if key=='address' and v not in ('192.0.2.10','2001:db8::10'):
                            raise ValueError('address_not_placeholder')
                        if key.startswith('ssh:') and key not in ('ssh:host-a','ssh:host-b'):
                            raise ValueError('host_not_placeholder')
                        if key=='anyEnvRef' and not all(x in ('TEST_CREDENTIAL_A','TEST_CREDENTIAL_B') for x in v):
                            raise ValueError('envref_not_placeholder')
                        walk(v)
                elif isinstance(value,list):
                    for v in value: walk(v)
            walk(json.loads(path.read_text()))
        seed=json.loads((ROOT/'seed.example.json').read_text())
        definitions=seed.get('workflowDefinitions',[])
        for definition in definitions:
            if definition.get('name') in ('UwePrImplementationWorkflow','UwePrAcceptanceWorkflow','UweAcceptanceCleanupWorkflow'):
                check_text(json.dumps(definition))

    def test_non_documentation_fixture_fails(self):
        with self.assertRaises(ValueError):
            check_text('{"sshTarget":"user-x@198.51.100.9"}')
