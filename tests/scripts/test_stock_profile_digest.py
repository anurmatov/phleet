"""#503 W2: the operator producer of the canonical stock profile digest (scripts/stock-profile-digest.sh).

The fixture digest is the same constant StockProfileDigestTests asserts for the orchestrator checker, so the pin an
operator produces is exactly the value managed admission computes.
"""
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "stock-profile-digest.sh"
FIXTURES = ROOT / "tests" / "fixtures" / "stock-profile"
FIXTURE_DIGEST = "bc2fa12d386f87e19cab2e7faccee65a771c07eed68a0a483476a97ba2faf84c"


def digest(data=None, path=None):
    args = ["bash", str(SCRIPT)] + ([str(path)] if path else [])
    return subprocess.run(args, input=data, capture_output=True)


class StockProfileDigestTests(unittest.TestCase):
    def test_fixture_and_its_reordered_export_share_the_checker_digest(self):
        for name in ("export.json", "export-reordered.json"):
            with self.subTest(name=name):
                from_file = digest(path=FIXTURES / name)
                from_stdin = digest(data=(FIXTURES / name).read_bytes())
                self.assertEqual(from_file.returncode, 0, from_file.stderr)
                self.assertEqual(from_file.stdout.decode().strip(), FIXTURE_DIGEST)
                self.assertEqual(from_stdin.stdout.decode().strip(), FIXTURE_DIGEST)
        self.assertNotEqual((FIXTURES / "export.json").read_bytes(), (FIXTURES / "export-reordered.json").read_bytes())

    def test_a_real_field_change_changes_the_digest(self):
        changed = (FIXTURES / "export.json").read_bytes().replace(b'"/usr/bin/codex"', b'"/usr/bin/other"')
        result = digest(data=changed)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotEqual(result.stdout.decode().strip(), FIXTURE_DIGEST)

    def test_uncanonicalizable_exports_refuse_without_a_digest(self):
        deep = b'{"a":' + b"[" * 64 + b"1" + b"]" * 64 + b"}"
        for data in (b'{"a":1,"a":1}', b'{"a":1.5}', b'{"a":1e3}', b'{"a":9223372036854775808}', b'{"a":NaN}',
                     b'[{"a":1}]', b"profile: synthetic\n", b'{"a":"\\ud800"}', b"\xff", deep):
            with self.subTest(data=data[:24]):
                result = digest(data=data)
                self.assertEqual(result.returncode, 1, result.stdout)
                self.assertEqual(result.stdout, b"")
                self.assertIn(b"REFUSED: profile export is not canonicalizable", result.stderr)
        ok = digest(data=b'{"a":' + b"[" * 63 + b"1" + b"]" * 63 + b"}")
        self.assertEqual(ok.returncode, 0, ok.stderr)  # 64 containers is the limit, as in the checker


if __name__ == "__main__":
    unittest.main()
