#!/usr/bin/env python3
"""Exercise the real inline M6 checker, not a copied assertion or runtime evidence."""
from pathlib import Path
import sys
import os
import subprocess
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

    def test_dual_stack_s3_wildcards_are_accepted(self):
        for host in ["::", "[::]"]:
            with self.subTest(host=host):
                self.check([f"{host}:8333", f"{host}:18333", "127.0.0.1:8888", "127.0.0.11:34803"])

    def test_mixed_s3_wildcards_are_accepted(self):
        for first, second in [("0.0.0.0", "::"), ("[::]", "0.0.0.0")]:
            with self.subTest(first=first, second=second):
                self.check([f"{first}:8333", f"{second}:18333"])

    def test_other_listeners_remain_rejected(self):
        for address in ["127.0.0.12:34803", "0.0.0.0:8080", "192.0.2.1:8888", ":::9333",
                        "[::]:8080", ":::18334", "[2001:db8::1]:9333", "::1:8888"]:
            with self.subTest(address=address), self.assertRaises(AssertionError):
                self.check(["0.0.0.0:8333", "0.0.0.0:18333", address])

    def test_both_application_bindings_remain_mandatory(self):
        for port in [8333, 18333]:
            with self.subTest(missing=port), self.assertRaises(AssertionError):
                other = 18333 if port == 8333 else 8333
                self.check([f"0.0.0.0:{other}", f"127.0.0.11:{port}"])

    def probe_peer(self, refused=""):
        peer = script.split("docker run --rm --network \"$NETWORK\" --entrypoint /bin/sh \"$SEAWEEDFS_IMAGE\" -c '\n", 1)[1].split("'", 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            nc = Path(directory) / "nc"
            calls = Path(directory) / "calls"
            nc.write_text("""#!/bin/sh
printf '%s\\n' "$*" >> "$NC_CALLS"
case "$4:$5" in
  192.0.2.9:8333|192.0.2.9:18333) [ "$5" != "$NC_REFUSE" ] ;;
  *) exit 1 ;;
esac
""")
            nc.chmod(0o755)
            result = subprocess.run(["/bin/sh", "-c", peer, "test-peer", "192.0.2.9"],
                                    env={**os.environ, "PATH": directory + ":" + os.environ["PATH"],
                                         "NC_CALLS": str(calls), "NC_REFUSE": refused}, capture_output=True)
            return result.returncode, calls.read_text()

    def test_peer_probes_both_s3_ports_over_ipv4_and_all_management_ports(self):
        code, calls = self.probe_peer()
        self.assertEqual(0, code)
        for port in [8333, 18333]:
            self.assertIn(f"-z -w 1 192.0.2.9 {port}\n", calls)
        for port in [9333, 19333, 8080, 18080, 8888, 18888, 8181, 9101, 6060, 2022, 7333]:
            self.assertIn(f"-z -w 1 comms-seaweedfs {port}\n", calls)

    def test_ipv6_only_s3_does_not_pass_peer_probe(self):
        for port in [8333, 18333]:
            with self.subTest(ipv4_refused=port):
                code, _ = self.probe_peer(str(port))
                self.assertNotEqual(0, code)


if __name__ == "__main__":
    unittest.main()
