#!/usr/bin/env python3
"""Capture curated, complete catalog fields from the production read-only replica.

The SQL defines the sample, not a canonical truth set. This command creates new
files only, validates read-only replica provenance, and never persists a password.
"""

import argparse
import base64
import collections
import hashlib
import json
import os
from pathlib import Path
import subprocess


def run_capture(output_directory, origin, sql_path):
    sql_bytes = sql_path.read_bytes()
    output_directory.mkdir(parents=True, exist_ok=True)
    destinations = [output_directory / name for name in ('evidence.jsonl', 'manifest.json', 'executed.sql')]
    if any(path.exists() for path in destinations):
        raise ValueError('Output files already exist; choose a new output directory.')

    if origin == 'production':
        secret = subprocess.run(
            ['kubectl', '--context', 'relisten3-k3s', '-n', 'default', 'get', 'secret',
             'relisten-db-app', '-o', 'jsonpath={.data.password}'],
            capture_output=True, check=True,
        )
        password = base64.b64decode(secret.stdout, validate=True).decode()
        host, port, user, database = 'relisten-db-ro.tail09dbf.ts.net', '5432', 'app', 'app'
    else:
        password = 'local_dev_password'
        host, port, user, database = '127.0.0.1', '15432', 'relisten', 'relisten_jerry_20261017'
    if not password:
        raise ValueError('The database password is empty.')

    environment = os.environ.copy()
    environment.update(PGPASSWORD=password, PGSSLMODE='require' if origin == 'production' else 'disable',
                       PGOPTIONS='-c default_transaction_read_only=on')
    query = subprocess.run(
        ['psql', '-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1',
         '-h', host, '-p', port, '-U', user, '-d', database],
        input=sql_bytes, capture_output=True, env=environment, check=True,
    )
    rows = [json.loads(line) for line in query.stdout.splitlines() if line.strip()]
    snapshot, *evidence = rows
    if snapshot.get('kind') != 'snapshot' or snapshot.get('transaction_read_only') != 'on' or snapshot.get('is_replica') is not (origin == 'production'):
        raise ValueError('Capture did not confirm a read-only replica snapshot.')
    ids = [row['id'] for row in evidence]
    if not evidence or len(ids) != len(set(ids)):
        raise ValueError('Capture is empty or contains duplicate evidence IDs.')

    # Preserve the JSON values returned by PostgreSQL, including whitespace in text.
    evidence_bytes = b''.join(json.dumps(row, ensure_ascii=False, sort_keys=True).encode() + b'\n' for row in evidence)
    manifest = {
        **snapshot,
        'database': database,
        'origin': origin,
        'endpoint': f'{host}:{port}',
        'selection': 'Curated development inputs selected by executed.sql. Not a random or representative accuracy sample.',
        'record_counts': {'evidence': len(evidence), 'artists': len({row['artist_id'] for row in evidence})},
        'counts_by_kind': dict(collections.Counter(row['kind'] for row in evidence)),
        'files': [
            {'path': 'evidence.jsonl', 'sha256': hashlib.sha256(evidence_bytes).hexdigest()},
            {'path': 'executed.sql', 'sha256': hashlib.sha256(sql_bytes).hexdigest()},
        ],
    }
    for path, content in zip(destinations, (evidence_bytes, (json.dumps(manifest, indent=2) + '\n').encode(), sql_bytes)):
        with path.open('xb') as handle:
            handle.write(content)
    print(json.dumps({'snapshot_utc': snapshot['snapshot_utc'], **manifest['record_counts'], 'counts_by_kind': manifest['counts_by_kind']}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output_directory', type=Path)
    parser.add_argument('--origin', choices=('production', 'local'), default='production')
    parser.add_argument('--sql', type=Path)
    args = parser.parse_args()
    try:
        run_capture(args.output_directory, args.origin, args.sql or Path(__file__).with_name('capture-local.sql' if args.origin == 'local' else 'capture.sql'))
    except subprocess.CalledProcessError as error:
        # Do not include subprocess output: the secret lookup shares this handler.
        raise SystemExit(f'Capture command {error.cmd[0]} failed with exit code {error.returncode}.') from None
    except (ValueError, OSError) as error:
        raise SystemExit(str(error)) from None
