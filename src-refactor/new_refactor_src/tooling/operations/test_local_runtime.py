import json
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TOOL = Path(__file__).with_name('local-runtime.py')


class RuntimeInspectionTests(unittest.TestCase):
    def test_retry_preserves_active_leases_and_resets_only_terminal_delivery(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'runtime.db'
            with sqlite3.connect(path) as db:
                db.executescript('''
                    CREATE TABLE messaging_messages(seq INTEGER);
                    CREATE TABLE messaging_inbox(consumer TEXT,seq INTEGER,acknowledged INTEGER,attempts INTEGER,available INTEGER,token TEXT);
                    CREATE TABLE messaging_failures(consumer TEXT,seq INTEGER,error TEXT,failed_at INTEGER);
                    CREATE TABLE search_pending_index(media_guid TEXT,revision INTEGER,applied INTEGER,attempts INTEGER,last_error TEXT,available INTEGER,lease_until INTEGER);
                    INSERT INTO messaging_inbox VALUES('test',1,2,8,999,NULL),('test',2,0,1,999,'active');
                    INSERT INTO messaging_failures VALUES('test',1,'poison',1);
                    INSERT INTO search_pending_index VALUES('11111111111111111111111111111111',1,0,2,'offline',999,9999999999999);
                ''')
            result = subprocess.run([sys.executable, str(TOOL), str(path), '--retry-queue', 'test', '1', '--retry-index', '11111111111111111111111111111111'], capture_output=True, text=True, check=True)
            report = json.loads(result.stdout)
            self.assertEqual(report['terminalFailures'], [])
            self.assertEqual(report['indexFailures'][0]['attempts'], 2)
            with sqlite3.connect(path) as db:
                self.assertEqual(db.execute('SELECT acknowledged,attempts,available FROM messaging_inbox WHERE seq=1').fetchone(), (0, 0, 0))
                self.assertEqual(db.execute('SELECT token,attempts FROM messaging_inbox WHERE seq=2').fetchone(), ('active', 1))

    def test_inspection_does_not_create_a_missing_database(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'missing.db'
            result = subprocess.run([sys.executable, str(TOOL), str(path)], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(path.exists())


if __name__ == '__main__':
    unittest.main()
