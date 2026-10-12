#!/usr/bin/env python3
"""Inspect local authoritative runtime state; explicitly retry retained failures."""
import argparse
import json
import sqlite3
import time
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('database', type=Path)
    parser.add_argument('--retry-queue', nargs=2, metavar=('CONSUMER', 'SEQUENCE'))
    parser.add_argument('--retry-index', type=uuid.UUID, metavar='MEDIA_GUID')
    args = parser.parse_args()
    # mode=rw refuses to create a mistaken/empty database.
    with sqlite3.connect(args.database.resolve().as_uri() + '?mode=rw', uri=True, timeout=5) as db:
        db.row_factory = sqlite3.Row
        if args.retry_queue:
            consumer, seq = args.retry_queue
            db.execute('BEGIN IMMEDIATE')
            changed = db.execute('UPDATE messaging_inbox SET acknowledged=0,attempts=0,available=0,token=NULL WHERE consumer=? AND seq=? AND acknowledged=2', (consumer, int(seq))).rowcount
            if changed:
                db.execute('DELETE FROM messaging_failures WHERE consumer=? AND seq=?', (consumer, int(seq)))
            db.commit()
        if args.retry_index:
            db.execute('UPDATE search_pending_index SET available=0,attempts=0,last_error=NULL WHERE media_guid=? AND lease_until<=?', (args.retry_index.hex, int(time.time()*1000)))
            db.commit()
        report = {
            'messages': db.execute('SELECT count(*) FROM messaging_messages').fetchone()[0],
            'pendingDeliveries': db.execute('SELECT count(*) FROM messaging_inbox WHERE acknowledged=0').fetchone()[0],
            'terminalFailures': [dict(row) for row in db.execute('SELECT * FROM messaging_failures ORDER BY failed_at DESC LIMIT 100')],
            'pendingIndexEntities': db.execute('SELECT count(*) FROM search_pending_index WHERE revision>applied').fetchone()[0],
            'indexFailures': [dict(row) for row in db.execute('SELECT media_guid,revision,applied,attempts,last_error,available FROM search_pending_index WHERE revision>applied AND attempts>0 ORDER BY available LIMIT 100')],
        }
        print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
