"""Additional platform tests must not permit missing/skipped required server checks."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec=importlib.util.spec_from_file_location('server_results',Path(__file__).with_name('verify_results.py'))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)

class ServerResultTests(unittest.TestCase):
    def fixture(self,root,skips,passed=175,total=178):
        tree=ET.Element(m.NS+'TestRun');summary=ET.SubElement(tree,m.NS+'ResultSummary')
        ET.SubElement(summary,m.NS+'Counters',total=str(total),passed=str(passed),failed='0')
        results=ET.SubElement(tree,m.NS+'Results')
        for name in skips:ET.SubElement(results,m.NS+'UnitTestResult',testName=name,outcome='NotExecuted')
        for index in range(passed):ET.SubElement(results,m.NS+'UnitTestResult',testName='passed_'+str(index),outcome='Passed')
        path=Path(root)/'result.trx';ET.ElementTree(tree).write(path);return path
    def test_only_exact_windows_socket_skips_are_accepted(self):
        with tempfile.TemporaryDirectory() as root:
            m.verify_suite(self.fixture(root,m.WINDOWS_SKIPS),178,m.WINDOWS_SKIPS)
            altered=set(m.WINDOWS_SKIPS);altered.pop();altered.add('required-integration')
            with self.assertRaises(RuntimeError):m.verify_suite(self.fixture(root,altered),178,m.WINDOWS_SKIPS)
    def test_all_skipped_and_old_smaller_suites_are_refused(self):
        with tempfile.TemporaryDirectory() as root:
            for skips,passed,total in [([],168,168),(['skipped_'+str(i) for i in range(178)],0,178)]:
                with self.assertRaises(RuntimeError):m.verify_suite(self.fixture(root,skips,passed,total),178,m.WINDOWS_SKIPS)
    def test_linux_requires_every_integration_case_and_package_requires_one_pass(self):
        with tempfile.TemporaryDirectory() as root:
            m.verify_suite(self.fixture(root,[],passed=178),178)
            with self.assertRaises(RuntimeError):m.verify_suite(self.fixture(root,m.WINDOWS_SKIPS),178)
            m.verify_suite(self.fixture(root,[],passed=1,total=1),1)

if __name__=='__main__':unittest.main()
