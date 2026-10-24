"""Regression tests for the public-scrub rules (#476). Run: python3 -m unittest discover -s tests/integration/workflow-outcomes -p 'test_*.py'"""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import assertions  # noqa: E402


# Samples are assembled at runtime so this file's own diff never trips the scrub it tests.
INTERNAL, CORP, ARPA = '.' + 'internal', '.' + 'CORP', '.Home' + '.Arpa'


def hosts(text):
    return [value for rule, value in assertions.line_findings(text, [], set()) if rule == 'private_hostname']


class PrivateHostnameRule(unittest.TestCase):
    def test_lowercase_private_url_is_found(self):
        self.assertEqual(hosts('BaseUrl = "http://orchestrator' + INTERNAL + ':5000"'), ['orchestrator' + INTERNAL])

    def test_uppercase_private_url_is_found(self):
        self.assertEqual(hosts('see HTTP://BUILD-01' + CORP + '/status'), ['BUILD-01' + CORP])
        self.assertEqual(hosts('host Mixed' + ARPA + ' answered'), ['Mixed' + ARPA])

    def test_method_call_is_not_a_hostname(self):
        self.assertEqual(hosts('await rt.UntilAsync(() => rt.Local(batch.BatchId).Completed);'), [])
        self.assertEqual(hosts('var x = store.local(key);'), [])

    def test_reserved_placeholder_is_not_flagged(self):
        self.assertEqual(hosts('"http://orchestrator.example:5000"'), [])


if __name__ == '__main__':
    unittest.main()
