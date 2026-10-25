"""Corrupted or truncated baseline installers must never reach the execution stage."""
import hashlib
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import legacy_installer as baseline


class BaselineDownloadTests(unittest.TestCase):
    def fetch(self, directory, payload, *, expected=b'MZ-fixture'):
        response = io.BytesIO(payload)
        response.status = 200
        opener = Mock(return_value=response)
        with patch.object(baseline, 'SIZE', len(expected)), patch.object(baseline, 'SHA256', hashlib.sha256(expected).hexdigest()):
            result = baseline.download(directory, opener=opener, token='fixture-token')
        return result, opener

    def test_checksum_and_size_must_match_before_a_baseline_is_returned(self):
        for payload in [b'MZ-corrupt', b'MZ-short', b'MZ-fixture-extra']:
            with self.subTest(payload=payload), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaises(ValueError):
                    self.fetch(temporary, payload)
                self.assertEqual(list(Path(temporary).iterdir()), [])

    def test_verified_cached_baseline_is_reused_without_a_network_request(self):
        with tempfile.TemporaryDirectory() as temporary:
            path, opener = self.fetch(temporary, b'MZ-fixture')
            opener.assert_called_once()
            request = opener.call_args.args[0]
            self.assertTrue(request.full_url.startswith('https://api.github.com/repositories/1413316996/'))
            self.assertNotIn('fixture-token', request.full_url)
            self.assertEqual(request.headers['Authorization'], 'Bearer fixture-token')
            with patch.object(baseline, 'SIZE', 10), patch.object(baseline, 'SHA256', hashlib.sha256(b'MZ-fixture').hexdigest()):
                self.assertEqual(baseline.download(temporary, opener=Mock(side_effect=AssertionError('Unexpected request'))), path)
            self.assertEqual(path.read_bytes(), b'MZ-fixture')

    def test_existing_corrupt_baseline_is_preserved_and_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / ('1C-Backup-' + baseline.VERSION + '-windows-x64-setup.exe')
            path.write_bytes(b'pre-existing')
            with self.assertRaises(ValueError):
                baseline.download(temporary, opener=Mock(side_effect=AssertionError('Unexpected request')))
            self.assertEqual(path.read_bytes(), b'pre-existing')
