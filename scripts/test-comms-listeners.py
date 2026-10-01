#!/usr/bin/env python3
"""Exercise the real inline M6 checker, not a copied assertion or runtime evidence."""
from pathlib import Path
import sys
import tempfile
import unittest

script = (Path(__file__).resolve().parent / "smoke-comms-media-store.sh").read_text()
checker = script.split('python3 - "$ENV.listeners" <<\'PY\'\n', 1)[1].split('\nPY\n', 1)[0]


class ListenerBoundaryTests(unittest.TestCase):
    def check(self, addresses):
        with tempfile.NamedTemporaryFile(mode="w") as listeners:
            for address in addresses:
                listeners.write(f"tcp 0 0 {address} 0.0.0.0:* LISTEN\n")
            listeners.flush()
            previous = sys.argv
            try:
                sys.argv = ["checker", listeners.name]
                exec(compile(checker, "smoke-comms-media-store.sh:M6", "exec"), {})
            finally:
                sys.argv = previous

    def test_only_docker_dns_is_exempt(self):
        self.check(["0.0.0.0:8333", "0.0.0.0:18333", "127.0.0.1:9333", "127.0.0.11:34803"])

    def test_other_listeners_remain_rejected(self):
        for address in ["127.0.0.12:34803", "0.0.0.0:8080", "192.0.2.1:8888", ":::9333"]:
            with self.subTest(address=address), self.assertRaises(AssertionError):
                self.check(["0.0.0.0:8333", "0.0.0.0:18333", address])

    def test_both_application_bindings_remain_mandatory(self):
        for port in [8333, 18333]:
            with self.subTest(missing=port), self.assertRaises(AssertionError):
                other = 18333 if port == 8333 else 8333
                self.check([f"0.0.0.0:{other}", f"127.0.0.11:{port}"])


if __name__ == "__main__":
    unittest.main()
