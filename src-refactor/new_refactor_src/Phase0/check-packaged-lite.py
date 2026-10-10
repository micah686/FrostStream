#!/usr/bin/env python3
"""Read-only checks through the packaged frontend; not a media-workflow gate."""
import argparse
import json
import time
import urllib.error
import urllib.request
from pathlib import Path


def check(base):
    results = []
    for path in ('/', '/healthz', '/api/system/capabilities', '/api/auth/me', '/api/auth/config'):
        with urllib.request.urlopen(base + path, timeout=10) as response:
            content_type = response.headers.get_content_type()
            body = response.read()
            if path.startswith('/api/'):
                if content_type != 'application/json':
                    raise ValueError(f'{path}: expected JSON, received {content_type}')
                payload = json.loads(body)
                if path.endswith('/capabilities'):
                    if payload['deploymentMode'] != 'Lite' or payload['accessManagement']['enabled']:
                        raise ValueError('Unexpected Lite edition/access-management capabilities')
                    if payload['backups']['provider'] != 'SQLite' or not payload['integrations']['search']:
                        raise ValueError('Missing SQLite backup/search capability')
                elif path.endswith('/me'):
                    if payload['mode'] != 'single-user' or payload['authenticated'] is not True:
                        raise ValueError('Packaged Lite did not authenticate the local identity')
                    if payload['profile']['name'] != 'Admin' or 'admins' not in payload['profile']['groups']:
                        raise ValueError('Local identity is missing Admin privileges')
                elif payload['mode'] != 'single-user':
                    raise ValueError('Unexpected Lite authentication mode')
            elif path == '/' and (content_type != 'text/html' or b'<html' not in body.lower()):
                raise ValueError('Frontend did not serve the application HTML')
            results.append({'path': path, 'status': response.status, 'contentType': content_type})
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-url', default='http://127.0.0.1:25000')
    parser.add_argument('--wait-seconds', type=float, default=120)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.wait_seconds < 0:
        parser.error('--wait-seconds must be nonnegative')
    started = time.monotonic()
    report = {'scope': 'packaged Lite frontend/API/authentication smoke only', 'passed': False}
    while True:
        try:
            report['checks'] = check(args.base_url.rstrip('/'))
            report['passed'] = True
            break
        except (OSError, ValueError, KeyError, TypeError) as error:
            report['lastError'] = str(error)
            if time.monotonic() - started >= args.wait_seconds:
                break
            time.sleep(1)
    report['elapsedSeconds'] = round(time.monotonic() - started, 3)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps(report, indent=2))
    raise SystemExit(0 if report['passed'] else 1)


if __name__ == '__main__':
    main()
