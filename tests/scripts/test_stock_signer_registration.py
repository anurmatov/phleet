"""#503 C15/C18/C19 config and runbook assertions for the fixed stock-memory signer registration.

Synthetic text checks only: they pin the shipped example and the documented operator contract. They never start a
gateway or signer and are not evidence of real negotiation, reachability or startup behaviour (operator C11/C18).
"""
import re
import tomllib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
EXAMPLE = ROOT / "deploy" / "openshell" / "gateway.toml.example"
RUNBOOK = ROOT / "docs" / "openshell-stock.md"
FIELDS = r"(\[\[openshell\.supervisor\.middleware\]\]|(name|grpc_endpoint|audience|tls_ca_cert_path|max_payload_bytes|timeout|allow_insecure_transport)\s+=.*)"


def section(text, heading):
    start = text.index(heading)
    end = text.find("\n## ", start + len(heading))
    return text[start:] if end < 0 else text[start:end]


class SignerRegistrationExampleTests(unittest.TestCase):
    def setUp(self):
        self.text = EXAMPLE.read_text()

    def test_registration_is_dormant_by_default(self):
        # Registering makes the signer a startup dependency of the whole gateway: the shipped example never enables it.
        self.assertNotIn("supervisor", tomllib.loads(self.text).get("openshell", {}))
        self.assertIn("ALL sandboxes", self.text)

    def test_uncommented_registration_is_the_fixed_fail_closed_form(self):
        enabled = re.sub(r"^# " + FIELDS + "$", r"\1", self.text, flags=re.M)
        (registration,) = tomllib.loads(enabled)["openshell"]["supervisor"]["middleware"]
        self.assertEqual(registration["name"], "stock-memory")
        self.assertRegex(registration["grpc_endpoint"], r"^https://[a-z0-9.-]+\.[a-z]+:\d+$")  # a DNS name, never loopback/IP
        self.assertNotEqual(registration["audience"], "urn:openshell:extension:middleware:stock-memory")
        self.assertTrue(registration["tls_ca_cert_path"].startswith("/var/lib/openshell/"))  # under the default STATE_DIR
        self.assertEqual((registration["max_payload_bytes"], registration["timeout"]), (262144, "2s"))
        self.assertIs(registration["allow_insecure_transport"], False)


class SignerRunbookTests(unittest.TestCase):
    def setUp(self):
        # Whitespace-normalized, so a rewrapped line never breaks an assertion.
        self.text = " ".join(section(RUNBOOK.read_text(), "## Managed signer policy binding (#503)").split())

    def test_kestrel_listener_keys_are_exact_and_https_http2_only(self):
        for key in ('`Kestrel:Endpoints:Signer:Url = "https://<authorized-listen-address>:5443"`',
                    '`Kestrel:Endpoints:Signer:Protocols = "Http2"`',
                    "`Kestrel:Endpoints:Signer:Certificate:Path`", "`Kestrel:Endpoints:Signer:Certificate:KeyPath`"):
            self.assertIn(key, self.text)
        self.assertIn("Do not add an HTTP/HTTP1 or development-certificate fallback", self.text)
        self.assertIn("0600 file owned by the signer", self.text)
        self.assertIn("0700 outside directory", self.text)
        self.assertIn("UNRUN", self.text)  # no live readiness claim

    def test_named_operator_stops_are_documented(self):
        for category in ("signer_gateway_startup_blocked", "signer_gateway_audience_mismatch", "signer_reachability_unverified",
                         "signer_listener_unverified", "coverage_incomplete: signer_tls_ca", "signer_tls_ca_not_covered",
                         "subscription_snapshot_scope_unverified", "subscription_state_unverified",
                         "managed_signer_policy_missing", "managed_signer_authority_unavailable"):
            self.assertIn(category, self.text)

    def test_startup_dependency_is_gateway_wide_and_signer_first(self):
        self.assertIn("**entire gateway**", self.text)
        self.assertIn("for ALL its sandboxes, managed or manual", self.text)
        self.assertIn("Do not restart in a loop or deregister automatically", self.text)
        self.assertIn("**Signer first.**", self.text)
        self.assertLess(self.text.index("start the owned signer first"), self.text.index("then only the named"))
        self.assertIn("BOTH the gateway container network and the host-network supervisors", self.text)
        self.assertIn("exactly, case included", self.text)

    def test_maintenance_order_preserves_checkpoints_custody_and_the_hold(self):
        steps = ["**Quiesce.**", "**Choose the checkpoint mode.**", "**Reserve the paths.**", "**Seal S0.**",
                 "**Change while stopped.**", "**Seal S1**", "**Re-verify BOTH immediately before restart.**",
                 "**Signer first.**", "**Authenticate.**", "**Resume only eligible records.**"]
        positions = [self.text.index(step) for step in steps]
        self.assertEqual(positions, sorted(positions))
        self.assertIn("Rotation runs in a NEW window", self.text)
        self.assertIn("remove ONLY the fixed registration. Keep the keys, CA", self.text)
        self.assertIn("keep managed Start/apply held", self.text)
        self.assertIn("never stripped or resumed unsigned", self.text)
        self.assertIn("missing, corrupt or stale S0 stops the window even when S1 is valid", self.text)


if __name__ == "__main__":
    unittest.main()
