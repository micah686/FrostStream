"""Prevent false-positive deployment evidence from SPA fallback or wrong identity."""
import importlib.util
import io
import json
import unittest
from email.message import Message
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('packaged_lite', Path(__file__).with_name('check-packaged-lite.py'))
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class PackagedLiteTests(unittest.TestCase):
    def responses(self, wrong_api_html=False, groups=None, edition='Lite'):
        capabilities = {'deploymentMode': edition, 'accessManagement': {'enabled': False},
                        'backups': {'provider': 'SQLite'}, 'integrations': {'search': True}}
        identity = {'mode': 'single-user', 'authenticated': True,
                    'profile': {'name': 'Admin', 'groups': ['owner', 'admins'] if groups is None else groups}}
        payloads = [b'<html>app</html>', b'', json.dumps(capabilities).encode(),
                    json.dumps(identity).encode(), b'{"mode":"single-user"}']
        responses = []
        for index, body in enumerate(payloads):
            response = io.BytesIO(body)
            response.status = 200
            response.headers = Message()
            response.headers['Content-Type'] = 'application/json' if index >= 2 else 'text/html'
            if wrong_api_html and index == 2:
                response = io.BytesIO(b'<html>app</html>')
                response.status = 200
                response.headers = Message()
                response.headers['Content-Type'] = 'text/html'
            responses.append(response)
        return responses

    def test_accepts_actual_local_admin_contract(self):
        with patch.object(smoke.urllib.request, 'urlopen', side_effect=self.responses()):
            self.assertEqual(len(smoke.check('http://fixture')), 5)

    def test_rejects_spa_fallback_for_api(self):
        with patch.object(smoke.urllib.request, 'urlopen', side_effect=self.responses(wrong_api_html=True)):
            with self.assertRaisesRegex(ValueError, 'expected JSON'):
                smoke.check('http://fixture')

    def test_rejects_identity_without_admin_group(self):
        with patch.object(smoke.urllib.request, 'urlopen', side_effect=self.responses(groups=['owner'])):
            with self.assertRaisesRegex(ValueError, 'Admin privileges'):
                smoke.check('http://fixture')

    def test_rejects_wrong_edition(self):
        with patch.object(smoke.urllib.request, 'urlopen', side_effect=self.responses(edition='Full')):
            with self.assertRaisesRegex(ValueError, 'edition'):
                smoke.check('http://fixture')
