"""Synthetic TRX -> JSON part -> final receipt regressions for the actual runner."""
import contextlib
import io
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from types import SimpleNamespace

import assertions as checker


class TrxReceiptTests(unittest.TestCase):
    counters = dict(total='1', executed='1', passed='1', failed='0', error='0', timeout='0',
                    aborted='0', notExecuted='0', inconclusive='0', notRunnable='0')

    def summarize(self, part):
        with tempfile.TemporaryDirectory() as directory:
            parts, receipt = Path(directory) / 'parts.jsonl', Path(directory) / 'receipt.json'
            parts.write_text(json.dumps(part) + '\n', encoding='utf-8')
            with contextlib.redirect_stdout(io.StringIO()):
                rc = checker.cmd_summarize(SimpleNamespace(parts=str(parts), suites='ownership',
                    head='a' * 40, dirty='0', out=str(receipt)))
            result = json.loads(receipt.read_text(encoding='utf-8'))
            self.assertEqual(rc, result['rc'])
            return rc

    def trx(self, counters=None, outcomes=('Passed',), rc=0):
        with tempfile.TemporaryDirectory() as directory:
            root = ET.Element(checker.TRX_NS + 'TestRun')
            results = ET.SubElement(root, checker.TRX_NS + 'Results')
            for outcome in outcomes:
                row = ET.SubElement(results, checker.TRX_NS + 'UnitTestResult')
                if outcome is not None:
                    row.set('outcome', outcome)
            summary = ET.SubElement(root, checker.TRX_NS + 'ResultSummary')
            ET.SubElement(summary, checker.TRX_NS + 'Counters', self.counters if counters is None else counters)
            path = Path(directory) / 'results.trx'
            ET.ElementTree(root).write(path, encoding='utf-8')
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(checker.PASS, checker.cmd_trx(SimpleNamespace(suite='ownership', ac='AC18',
                    project='synthetic.csproj', rc=rc, trx=str(path))))
            return json.loads(output.getvalue())

    def test_valid_all_passed_receipt(self):
        counts = dict(self.counters, total='2', executed='2', passed='2')
        self.assertEqual(checker.PASS, self.summarize(self.trx(counts, ('Passed', 'Passed'))))

    def test_every_non_passed_unknown_empty_or_missing_outcome_fails(self):
        for outcome in ('Failed', 'Error', 'Timeout', 'Aborted', 'NotExecuted', 'Inconclusive',
                        'NotRunnable', 'Pending', 'InProgress', 'Warning', 'Unknown', 'passed', '', None):
            with self.subTest(outcome=outcome):
                self.assertEqual(checker.FAIL, self.summarize(self.trx(outcomes=(outcome,))))

    def test_missing_summary_counters_fail(self):
        for key in self.counters:
            with self.subTest(key=key):
                counts = dict(self.counters)
                del counts[key]
                self.assertEqual(checker.FAIL, self.summarize(self.trx(counts)))

    def test_negative_or_malformed_summary_counters_fail(self):
        for key in self.counters:
            for value in ('-1', '', 'not-a-number'):
                with self.subTest(key=key, value=value):
                    self.assertEqual(checker.FAIL, self.summarize(self.trx(dict(self.counters, **{key: value}))))

    def test_inconsistent_summary_counters_fail(self):
        for key in ('total', 'executed', 'passed'):
            for value in ('0', '2'):
                with self.subTest(key=key, value=value):
                    self.assertEqual(checker.FAIL, self.summarize(self.trx(dict(self.counters, **{key: value}))))

    def test_missing_negative_or_malformed_part_counters_fail(self):
        part = self.trx()
        for key in ('total', 'executed', 'passed', 'rows', 'rowsNotPassed', 'failed', 'skipped'):
            for value in (None, -1, '1', True):
                with self.subTest(key=key, value=value):
                    changed = dict(part)
                    if value is None:
                        del changed[key]
                    else:
                        changed[key] = value
                    self.assertEqual(checker.FAIL, self.summarize(changed))

    def test_direct_contradictory_part_is_not_a_pass(self):
        part = self.trx()
        self.assertEqual(checker.FAIL, self.summarize(dict(part, passed=0, rowsNotPassed=1)))

    def test_zero_tests_failure_skip_and_nonzero_exit_still_fail(self):
        self.assertEqual(checker.FAIL, self.summarize(self.trx(
            dict(self.counters, total='0', executed='0', passed='0'), ())))
        for key in ('failed', 'notExecuted'):
            with self.subTest(key=key):
                self.assertEqual(checker.FAIL, self.summarize(self.trx(dict(self.counters, **{key: '1'}))))
        self.assertEqual(checker.FAIL, self.summarize(self.trx(rc=1)))


if __name__ == '__main__':
    unittest.main()
