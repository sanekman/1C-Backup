import json
import os
from pathlib import Path
import subprocess
import unittest


class ComposeVersionTests(unittest.TestCase):
    repository = Path(__file__).resolve().parents[2]

    def config(self, file, version):
        environment = dict(os.environ, STORAGE_CONFIG_DIRECTORY='/tmp/onec-config',
                           STORAGE_DATA_DIRECTORY='/tmp/onec-data', STORAGE_DOMAIN='relay.example.com',
                           ACME_EMAIL='admin@example.com')
        environment.pop('STORAGE_VERSION', None)
        if version is not None:
            environment['STORAGE_VERSION'] = version
        return subprocess.run(['docker', 'compose', '-f', file, 'config', '--format', 'json'],
                              cwd=self.repository, env=environment, capture_output=True, text=True)

    def test_gateway_and_hub_build_the_selected_version(self):
        version = '9.8.7-alpha.6'
        for file, service in [('deploy/storage/compose.yaml', 'gateway'),
                              ('deploy/storage/compose.hub.yaml', 'hub')]:
            with self.subTest(file=file):
                result = self.config(file, version)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(result.stdout)['services'][service]['build']['args']['VERSION'], version)

    def test_missing_version_stops_before_build_instead_of_using_dev(self):
        for file in ['deploy/storage/compose.yaml', 'deploy/storage/compose.hub.yaml']:
            with self.subTest(file=file):
                result = self.config(file, None)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Set STORAGE_VERSION', result.stderr)


if __name__ == '__main__':
    unittest.main()
