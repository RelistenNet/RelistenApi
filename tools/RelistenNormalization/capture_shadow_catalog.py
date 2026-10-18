#!/usr/bin/env python3
"""Capture complete catalog raw fields as immutable gzip shards on the RO replica.

First domains (files can continue in the same job):
  python3 tools/RelistenNormalization/capture_shadow_catalog.py --output LOCAL_JOB \
      --domains sources,venues,songs,setlists
  python3 tools/RelistenNormalization/capture_shadow_catalog.py --output LOCAL_JOB \
      --resume --domains files

Only this operator contacts PostgreSQL. Credentials travel from the current kube
secret to the child environment; neither credentials nor server stderr are saved.
A completed shard is independently consumable by the catalog extractor/reader.
Snapshots are per shard, never a claim of one whole-catalog historical snapshot.
"""
import argparse
import base64
import fcntl
import gzip
import hashlib
import json
import os
from pathlib import Path
import selectors
import shutil
import sqlite3
import subprocess
import sys
import time
import uuid

VERSION = 'shadow-raw-catalog-v1'
ROOT = Path(__file__).resolve().parents[2]
METADATA = ROOT / 'local-dev/normalization-data/2026-10-18-domain-metadata-frame/full-domain-capture-v1/manifest.json'
METADATA_SHA = 'c50a6b3a76c6ed685e568ca997dcf8eaa7fb0018d528fece4364aa9abf800e02'
ENDPOINT = 'relisten-db-ro.tail09dbf.ts.net'
MAX_COMPRESSED_BYTES = 3 * 1024**3
MIN_FREE_BYTES = 4 * 1024**3
DEADLINE_SECONDS = 1200
ROSTER_ROWS_PER_SHARD = 50000
MAX_LINE_BYTES = 8 * 1024**2
MAX_ACTUAL_ROWS_PER_SHARD = 100000
MAX_DRIFT_ENTRIES_PER_SHARD = 200000
DOMAINS = {
    'sources': ('sources', 'source', 'source_sample'),
    'venues': ('venues', 'venue', 'venue_sample'),
    'songs': ('setlist_songs', 'setlist_song', 'song_sample'),
    'setlists': ('setlist_shows', 'setlist_show', 'setlist_sample'),
    'files': ('source_tracks', 'source_track', 'track_sample'),
}
PROSE = "ARRAY['description','lineage','source','taper','taper_notes','transferrer']"


class CaptureError(Exception):
    """A fixed diagnostic code, safe to put in receipts without source/secret text."""


def require(condition, code):
    if not condition:
        raise CaptureError(code)


def fingerprint(path):
    digest = hashlib.sha256()
    size = 0
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024**2), b''):
            digest.update(block)
            size += len(block)
    return {'bytes': size, 'sha256': digest.hexdigest()}


def save_json(path, value):
    """Replace only this job's mutable progress ledger, never captured evidence."""
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    with temporary.open('x', encoding='utf-8') as stream:
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write('\n')
        stream.flush()
        os.fsync(stream.fileno())
    temporary.replace(path)


def credentials():
    try:
        result = subprocess.run(
            ['kubectl', '--context', 'relisten3-k3s', '-n', 'default', 'get',
             'secret', 'relisten-db-app', '-o', 'jsonpath={.data.password}'],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=True, timeout=30,
        )
        password = base64.b64decode(result.stdout, validate=True).decode('utf-8')
        require(password and '\x00' not in password and '\n' not in password,
                'credential_unusable')
        return password
    except (subprocess.SubprocessError, ValueError, UnicodeError):
        raise CaptureError('credential_acquisition_failed') from None


def query_environment(password):
    # PGHOSTADDR, PGSERVICE and PGPASSFILE must not redirect the fixed RO target.
    environment = {key: value for key, value in os.environ.items() if not key.startswith('PG')}
    environment.update(PGPASSWORD=password, PGSSLMODE='require', PGCONNECT_TIMEOUT='15',
                       PGCLIENTENCODING='UTF8',
                       PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=1200000')
    return environment


def read_query(sql_path, password, accept, *, deadline=DEADLINE_SECONDS):
    """Read bounded physical LF records without buffering a SELECT or retaining stderr."""
    environment = query_environment(password)
    process = subprocess.Popen(
        ['psql', '-X', '-qAt', '-v', 'ON_ERROR_STOP=1', '-v', 'FETCH_COUNT=1000',
         '-h', ENDPOINT, '-p', '5432', '-U', 'app', '-d', 'app', '-f', str(sql_path)],
        env=environment, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        start_new_session=True,
    )
    environment.pop('PGPASSWORD', None)
    started = time.monotonic()
    pending = bytearray()
    try:
        with selectors.DefaultSelector() as selector:
            selector.register(process.stdout, selectors.EVENT_READ)
            while selector.get_map():
                remaining = deadline - (time.monotonic() - started)
                require(remaining > 0, 'query_external_deadline')
                for key, _ in selector.select(min(remaining, 0.5)):
                    block = os.read(key.fileobj.fileno(), 65536)
                    if not block:
                        selector.unregister(key.fileobj)
                        continue
                    pending.extend(block)
                    while (end := pending.find(b'\n')) >= 0:
                        require(end + 1 <= MAX_LINE_BYTES, 'raw_line_capacity_exceeded')
                        line = bytes(pending[:end + 1])
                        del pending[:end + 1]
                        accept(line)
                    require(len(pending) <= MAX_LINE_BYTES, 'raw_line_capacity_exceeded')
        require(not pending, 'unterminated_jsonl')
        remaining = deadline - (time.monotonic() - started)
        require(remaining > 0, 'query_external_deadline')
        try:
            code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            raise CaptureError('query_external_deadline') from None
        require(code == 0, 'query_process_failed')
    finally:
        if process.poll() is None:
            process.kill()
        process.wait()
        process.stdout.close()


def transaction_header(extra=''):
    return f"""BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL statement_timeout='20min';
SET LOCAL lock_timeout='2s';
SET LOCAL TIME ZONE 'UTC';
SELECT jsonb_build_object('kind','snapshot','version','{VERSION}',
 'snapshot',pg_current_snapshot()::text,'captured_at',clock_timestamp(),
 'read_only',current_setting('transaction_read_only'),
 'isolation',current_setting('transaction_isolation'),
 'in_recovery',pg_is_in_recovery(),'client_encoding',current_setting('client_encoding')
 {extra});
"""


def census_sql():
    selects = []
    for table in ['artists', *[item[0] for item in DOMAINS.values()]]:
        selects.append(f"SELECT jsonb_build_object('kind','census','table','{table}',"
                       f"'count',count(*),'minimum_id',min(id),'maximum_id',max(id)) FROM {table};")
    return transaction_header() + '\n'.join(selects) + "\nSELECT jsonb_build_object('kind','snapshot_complete');\nCOMMIT;\n"


def validate_header(row):
    require(row.get('kind') == 'snapshot' and row.get('version') == VERSION,
            'snapshot_header_invalid')
    require(row.get('read_only') == 'on' and row.get('isolation') == 'repeatable read'
            and row.get('in_recovery') is True and row.get('client_encoding') == 'UTF8'
            and isinstance(row.get('snapshot'), str), 'snapshot_read_only_proof_invalid')


def capture_census(output, name, password):
    sql_path = output / (name + '.sql')
    with sql_path.open('x', encoding='utf-8') as stream:
        stream.write(census_sql())
    rows = []

    def accept(line):
        require(len(rows) < 16, 'census_capacity_exceeded')
        rows.append(json.loads(line))

    read_query(sql_path, password, accept)
    require(len(rows) == 8 and rows[-1].get('kind') == 'snapshot_complete', 'census_incomplete')
    validate_header(rows[0])
    values = {row['table']: row for row in rows[1:-1] if row.get('kind') == 'census'}
    require(set(values) == {'artists', *[item[0] for item in DOMAINS.values()]}, 'census_tables_invalid')
    save_json(output / (name + '.json'), {'header': rows[0], 'tables': values})
    return values


def open_roster(manifest_path, expected_sha):
    require(fingerprint(manifest_path)['sha256'] == expected_sha, 'metadata_manifest_changed')
    manifest = json.loads(manifest_path.read_bytes())
    require(manifest.get('state') == 'completed_metadata_only', 'metadata_not_complete')
    index = manifest_path.parent / manifest['metadataIndex']['path']
    require(fingerprint(index) == {key: manifest['metadataIndex'][key] for key in ('bytes', 'sha256')},
            'metadata_index_changed')
    connection = sqlite3.connect(index.resolve().as_uri() + '?mode=ro', uri=True)
    connection.row_factory = sqlite3.Row
    for table in [item[1] for item in DOMAINS.values()]:
        source_table = next(item[0] for item in DOMAINS.values() if item[1] == table)
        count = connection.execute(f'SELECT count(*) FROM {table}').fetchone()[0]
        require(count == manifest['sameSnapshotCounts'][source_table], 'metadata_roster_count_changed')
    return connection, manifest


def plan_shards(roster, census, size=ROSTER_ROWS_PER_SHARD):
    """Use actual roster IDs; gaps in native sequences do not create empty loops."""
    plan = []
    for domain, (table, metadata_table, _) in DOMAINS.items():
        ids = roster.execute(f'SELECT id FROM {metadata_table} ORDER BY id')
        batch = []
        previous_upper = None
        maximum = None
        for record in ids:
            identifier = record[0]
            if len(batch) == size:
                plan.append(dict(domain=domain, lower=batch[0] if previous_upper is None else previous_upper,
                                 upper=identifier, expected=len(batch), tail=False))
                previous_upper = identifier
                batch = []
            batch.append(identifier)
            maximum = identifier
        if batch:
            plan.append(dict(domain=domain, lower=batch[0] if previous_upper is None else previous_upper,
                             upper=maximum + 1, expected=len(batch), tail=False))
        # Include newly inserted IDs below the old minimum as well as the usual tail.
        current = census[table]
        minimum = roster.execute(f'SELECT min(id) FROM {metadata_table}').fetchone()[0]
        if minimum is not None and current['minimum_id'] is not None and current['minimum_id'] < minimum:
            for lower in range(current['minimum_id'], minimum, size):
                plan.append(dict(domain=domain, lower=lower, upper=min(lower + size, minimum), expected=0, tail=True))
        if current['maximum_id'] is not None and (maximum is None or current['maximum_id'] > maximum):
            first = maximum + 1 if maximum is not None else current['minimum_id']
            for lower in range(first, current['maximum_id'] + 1, size):
                plan.append(dict(domain=domain, lower=lower, upper=min(lower + size, current['maximum_id'] + 1),
                                 expected=0, tail=True))
    for ordinal, shard in enumerate(plan):
        shard['id'] = f"{ordinal:04d}-{shard['domain']}-{shard['lower']}-{shard['upper']}"
    return plan


def payload_sql(domain, lower, upper):
    table, _, kind = DOMAINS[domain]
    if domain == 'sources':
        body = "to_jsonb(s),'show_context',to_jsonb(sh),'venue_context',to_jsonb(v)"
        relation = 'sources s LEFT JOIN shows sh ON sh.id=s.show_id LEFT JOIN venues v ON v.id=s.venue_id'
        alias = 's'
    elif domain == 'files':
        body = (f"to_jsonb(t),'source_context',to_jsonb(s)-{PROSE},"
                "'source_context_projection','catalog_coordinates_without_credit_prose_v1',"
                f"'set_context',to_jsonb(st),'set_source_context',to_jsonb(ss)-{PROSE},"
                "'show_context',to_jsonb(sh),'set_show_context',to_jsonb(ssh)")
        relation = ('source_tracks t LEFT JOIN sources s ON s.id=t.source_id '
                    'LEFT JOIN source_sets st ON st.id=t.source_set_id '
                    'LEFT JOIN sources ss ON ss.id=st.source_id '
                    'LEFT JOIN shows sh ON sh.id=s.show_id LEFT JOIN shows ssh ON ssh.id=ss.show_id')
        alias = 't'
    elif domain == 'setlists':
        body = ("to_jsonb(s),'venue_context',to_jsonb(v),'membership_order','unavailable','unordered_memberships',"
                "coalesce((SELECT jsonb_agg(jsonb_build_object('song_id',p.played_setlist_song_id,'song',to_jsonb(song),'membership',to_jsonb(p)) "
                "ORDER BY song.id) FROM setlist_songs_plays p LEFT JOIN setlist_songs song "
                "ON song.id=p.played_setlist_song_id WHERE p.played_setlist_show_id=s.id),'[]'::jsonb)")
        relation, alias = table + ' s LEFT JOIN venues v ON v.id=s.venue_id', 's'
    else:
        relation, alias, body = table + ' s', 's', 'to_jsonb(s)'
    return (f"SELECT jsonb_build_object('kind','{kind}','row',{body}) FROM {relation} "
            f"WHERE {alias}.id >= {int(lower)} AND {alias}.id < {int(upper)} ORDER BY {alias}.id;\n")


def shard_sql(shard):
    domain, lower, upper = shard['domain'], shard['lower'], shard['upper']
    table = DOMAINS[domain][0]
    header = transaction_header(f",'relation','{table}','lower_id',{lower},'upper_id',{upper},"
                                f"'expected_metadata_rows',{shard['expected']},'setlist_order','unordered_membership'")
    artists = """SELECT jsonb_build_object('kind','artist','row',to_jsonb(a),
 'configured_provider_mappings',coalesce((SELECT jsonb_agg(jsonb_build_object(
 'provider_id',u.id,'provider_name',u.name,'upstream_identifier',aus.upstream_identifier)
 ORDER BY u.id,aus.upstream_identifier) FROM artists_upstream_sources aus
 JOIN upstream_sources u ON u.id=aus.upstream_source_id WHERE aus.artist_id=a.id),'[]'::jsonb))
 FROM artists a ORDER BY a.id;
SELECT jsonb_build_object('kind','totals','rows',jsonb_build_object('artists',(SELECT count(*) FROM artists)));
"""
    footer = ("SELECT jsonb_build_object('kind','snapshot_complete','completed_at',clock_timestamp(),"
              f"'shard_rows',(SELECT count(*) FROM {table} WHERE id >= {lower} AND id < {upper}));\nCOMMIT;\n")
    return header + artists + payload_sql(domain, lower, upper) + footer


class BoundedCompressedFile:
    """Enforce the job's remaining compressed-byte allowance before every write."""
    def __init__(self, path, remaining, floor=None):
        self.stream = path.open('xb')
        self.remaining = remaining
        self.floor = MIN_FREE_BYTES if floor is None else floor
        self.bytes = 0
        self.parent = path.parent

    def write(self, data):
        require(self.bytes + len(data) <= self.remaining, 'compressed_job_capacity_exceeded')
        require(shutil.disk_usage(self.parent).free >= self.floor + len(data), 'free_disk_floor_reached')
        result = self.stream.write(data)
        self.bytes += result
        return result

    def flush(self):
        self.stream.flush()

    def close(self):
        self.stream.close()


class ShardValidation:
    """Reconcile IDs, UUIDs and retained native coordinates without interpreting prose."""
    def __init__(self, shard, expected):
        self.shard = shard
        self.expected = expected
        self.seen = set()
        self.header = None
        self.footer = None
        self.artists = {}
        self.artist_total = None
        self.rows = 0
        self.extra = []
        self.uuid_drift = []
        self.coordinate_drift = []
        self.last_id = None

    def accept(self, line):
        try:
            row = json.loads(line)
        except (ValueError, UnicodeError):
            raise CaptureError('invalid_jsonl') from None
        kind = row.get('kind')
        require(self.footer is None, 'records_after_snapshot_footer')
        if self.header is None:
            validate_header(row)
            require(row.get('relation') == DOMAINS[self.shard['domain']][0]
                    and row.get('lower_id') == self.shard['lower']
                    and row.get('upper_id') == self.shard['upper']
                    and row.get('expected_metadata_rows') == self.shard['expected'], 'shard_header_mismatch')
            self.header = row
        elif kind == 'artist':
            require(self.artist_total is None and row['row']['id'] not in self.artists, 'artist_inventory_invalid')
            self.artists[row['row']['id']] = row['row']['uuid']
        elif kind == 'totals':
            require(self.artist_total is None and self.rows == 0, 'totals_order_invalid')
            self.artist_total = row['rows']['artists']
            require(len(self.artists) == self.artist_total, 'artist_inventory_incomplete')
        elif kind == 'snapshot_complete':
            require(self.artist_total is not None and row['shard_rows'] == self.rows, 'shard_footer_count_mismatch')
            self.footer = row
        else:
            require(kind == DOMAINS[self.shard['domain']][2] and self.artist_total is not None,
                    'unexpected_raw_row')
            value = row['row']
            identifier = value['id']
            require(isinstance(identifier, int) and self.shard['lower'] <= identifier < self.shard['upper']
                    and (self.last_id is None or identifier > self.last_id), 'raw_id_order_or_range_invalid')
            self.last_id = identifier
            self.rows += 1
            require(self.rows <= MAX_ACTUAL_ROWS_PER_SHARD, 'actual_shard_rows_capacity_exceeded')
            require(value.get('artist_id') in self.artists, 'raw_artist_missing')
            expected = self.expected.get(identifier)
            actual_uuid = value['uuid']
            if expected is None:
                self.extra.append({'id': identifier, 'uuid': actual_uuid})
            else:
                self.seen.add(identifier)
                if expected['uuid'] != actual_uuid:
                    self.uuid_drift.append({'id': identifier, 'expected': expected['uuid'], 'actual': actual_uuid})
                for field in ('artist_id', 'source_id', 'source_set_id', 'show_id', 'venue_id'):
                    if field in expected and value.get(field) != expected[field]:
                        self.coordinate_drift.append({'id': identifier, 'uuid': actual_uuid, 'field': field,
                                                      'expected': expected[field], 'actual': value.get(field)})
                parents = {'artist_uuid': self.artists.get(value.get('artist_id'))}
                for field, context in [('source_uuid', 'source_context'), ('source_set_uuid', 'set_context'),
                                       ('show_uuid', 'show_context'), ('venue_uuid', 'venue_context')]:
                    parents[field] = (row.get(context) or {}).get('uuid')
                if 'set_parent_source_id' in expected:
                    parents['set_parent_source_id'] = (row.get('set_context') or {}).get('source_id')
                    parents['set_parent_source_uuid'] = (row.get('set_source_context') or {}).get('uuid')
                for field, actual in parents.items():
                    if field in expected and actual != expected[field]:
                        self.coordinate_drift.append({'id': identifier, 'uuid': actual_uuid, 'field': field,
                                                      'expected': expected[field], 'actual': actual})

            require(len(self.extra) + len(self.uuid_drift) + len(self.coordinate_drift) <=
                    MAX_DRIFT_ENTRIES_PER_SHARD, 'shard_drift_capacity_exceeded')

    def complete(self):
        require(self.footer is not None, 'shard_incomplete')
        missing = [{'id': key, 'uuid': value['uuid']} for key, value in self.expected.items() if key not in self.seen]
        return dict(header=self.header, footer=self.footer, actualRows=self.rows,
                    expectedRows=len(self.expected), matchedRows=len(self.seen), missing=missing,
                    extra=self.extra, uuidDrift=self.uuid_drift, coordinateDrift=self.coordinate_drift,
                    rawContentDrift='Not compared: roster authenticates coordinates, not original raw field bytes.')


def capture_shard(output, shard, expected, password, spent, attempt):
    stem = shard['id'] + f'-attempt-{attempt:03d}'
    sql_path = output / (stem + '.sql')
    with sql_path.open('x', encoding='utf-8') as stream:
        stream.write(shard_sql(shard))
    partial = output / (stem + '.partial.jsonl.gz')
    validation = ShardValidation(shard, expected)
    raw_hash = hashlib.sha256()
    raw_bytes = 0
    bounded = BoundedCompressedFile(partial, MAX_COMPRESSED_BYTES - spent)
    compressed = None
    try:
        compressed = gzip.GzipFile(filename='', mode='wb', fileobj=bounded, mtime=0)

        def accept(line):
            nonlocal raw_bytes
            validation.accept(line)
            compressed.write(line)
            raw_hash.update(line)
            raw_bytes += len(line)

        read_query(sql_path, password, accept)
        reconciliation = validation.complete()
        compressed.close()
        compressed = None
        bounded.flush()
        os.fsync(bounded.stream.fileno())
        bounded.close()
        final = output / (stem + '.jsonl.gz')
        require(not final.exists(), 'immutable_shard_exists')
        partial.rename(final)
        receipt_path = output / (stem + '.receipt.json')
        receipt = dict(state='completed', shard=shard, gzipPath=final.name,
                       compressed=fingerprint(final), raw={'bytes': raw_bytes, 'sha256': raw_hash.hexdigest()},
                       sql={'path': sql_path.name, **fingerprint(sql_path)}, reconciliation=reconciliation)
        save_json(receipt_path, receipt)
        return completed_summary(receipt_path, receipt)
    except BaseException as error:
        # Gzip may itself hit the bound while writing its trailer; retain that prefix too.
        if compressed is not None:
            try:
                compressed.close()
            except (OSError, CaptureError):
                pass
        bounded.close()
        code = str(error) if isinstance(error, CaptureError) else type(error).__name__
        failure = dict(state='failed', shardId=shard['id'], diagnostic=code,
                       prefix=(partial.name if partial.exists() else final.name),
                       compressed=fingerprint(partial if partial.exists() else final), rawBytesValidated=raw_bytes)
        save_json(output / (stem + '.failure.json'), failure)
        raise CaptureError(code) from None


def completed_summary(receipt_path, receipt):
    reconciliation = receipt['reconciliation']
    return {'state': 'completed', 'shardId': receipt['shard']['id'], 'receipt': receipt_path.name,
            'receiptFingerprint': fingerprint(receipt_path), 'gzipPath': receipt['gzipPath'],
            'compressed': receipt['compressed'], 'actualRows': reconciliation['actualRows'],
            'missingRows': len(reconciliation['missing']), 'extraRows': len(reconciliation['extra']),
            'uuidDriftRows': len(reconciliation['uuidDrift']),
            'coordinateDriftEntries': len(reconciliation['coordinateDrift'])}


def recover_receipts(output, manifest):
    """A receipt is the shard commit record if the progress update was interrupted."""
    completed = {item['shardId'] for item in manifest['completedShards']}
    planned = {item['id']: item for item in manifest.get('plan') or []}
    for attempt in manifest['attempts']:
        shard_id = attempt['shardId']
        if shard_id in completed:
            continue
        stem = shard_id + f"-attempt-{attempt['attempt']:03d}"
        receipt_path = output / (stem + '.receipt.json')
        if not receipt_path.exists():
            continue
        receipt = json.loads(receipt_path.read_bytes())
        require(receipt.get('state') == 'completed' and receipt.get('shard') == planned[shard_id],
                'recovery_receipt_mismatch')
        require(receipt['gzipPath'] == stem + '.jsonl.gz', 'recovery_path_mismatch')
        require(receipt['sql']['path'] == stem + '.sql' and
                fingerprint(output / receipt['sql']['path']) ==
                {key: receipt['sql'][key] for key in ('bytes', 'sha256')}, 'recovery_sql_changed')
        require(fingerprint(output / receipt['gzipPath']) == receipt['compressed'], 'recovery_gzip_changed')
        manifest['completedShards'].append(completed_summary(receipt_path, receipt))
        attempt['state'] = 'completed'
        completed.add(shard_id)


def actual_gzip_bytes(output):
    return sum(path.stat().st_size for path in output.glob('*.jsonl.gz'))


def verify_completed(output, records):
    for record in records:
        require(fingerprint(output / record['receipt']) == record['receiptFingerprint'], 'completed_receipt_changed')
        require(fingerprint(output / record['gzipPath']) == record['compressed'], 'completed_shard_changed')


def validate_plan(output, roster, manifest):
    receipt = manifest['initialCensusReceipt']
    require(Path(receipt['path']).name == receipt['path'], 'census_receipt_path_invalid')
    path = output / receipt['path']
    require(fingerprint(path) == {key: receipt[key] for key in ('bytes', 'sha256')}, 'initial_census_changed')
    census = json.loads(path.read_bytes())
    validate_header(census['header'])
    require(census['tables'] == manifest['initialCensus'], 'initial_census_mismatch')
    require(plan_shards(roster, census['tables']) == manifest['plan'], 'planned_ranges_changed')


def refresh_manifest(output, manifest):
    completed = {record['shardId'] for record in manifest['completedShards']}
    manifest['pendingShards'] = [shard['id'] for shard in manifest.get('plan') or [] if shard['id'] not in completed]
    manifest['compressedBytesAllCompletedAndFailedPrefixes'] = actual_gzip_bytes(output)
    manifest['snapshots'] = 'Separate read-only repeatable-read transaction per shard; not one shared snapshot.'
    manifest['complete'] = manifest.get('plan') is not None and not manifest['pendingShards']
    manifest['state'] = ('initializing' if manifest.get('plan') is None else
                         'completed_declared_ranges' if manifest['complete'] else 'partial')
    save_json(output / 'manifest.json', manifest)


def initialize_job(output, args, roster, metadata):
    request = dict(version=VERSION, endpoint=ENDPOINT, database='app',
                   source={'path': str(Path(__file__).resolve()), **fingerprint(__file__)},
                   metadataManifest={'path': str(args.metadata_manifest.resolve()),
                                     **fingerprint(args.metadata_manifest)},
                   metadataSnapshot=metadata['header']['snapshot'],
                   rosterCounts=metadata['sameSnapshotCounts'],
                   bounds=dict(compressedBytes=MAX_COMPRESSED_BYTES, freeDiskFloor=MIN_FREE_BYTES,
                               statementAndExternalDeadlineSeconds=DEADLINE_SECONDS,
                               maximumRosterRowsPerShard=ROSTER_ROWS_PER_SHARD,
                               maximumActualRowsPerShard=MAX_ACTUAL_ROWS_PER_SHARD,
                               maximumDriftEntriesPerShard=MAX_DRIFT_ENTRIES_PER_SHARD))
    if args.resume:
        require(json.loads((output / 'request.json').read_bytes()) == request, 'resume_request_changed')
        manifest = json.loads((output / 'manifest.json').read_bytes())
        verify_completed(output, manifest['completedShards'])
        if manifest.get('plan') is not None:
            validate_plan(output, roster, manifest)
        recover_receipts(output, manifest)
    else:
        save_json(output / 'request.json', request)
        manifest = dict(version=VERSION, completedShards=[], attempts=[], plan=None, complete=False)
        refresh_manifest(output, manifest)
    return manifest


def capture_selected_shards(output, args, roster, password, manifest):
    done = {record['shardId'] for record in manifest['completedShards']}
    processed = 0
    for shard in manifest['plan']:
        if shard['domain'] not in args.domains or shard['id'] in done:
            continue
        if args.max_shards is not None and processed >= args.max_shards:
            break
        table = DOMAINS[shard['domain']][1]
        if shard['domain'] == 'files':
            query = (f'SELECT t.*, st.source_id AS set_parent_source_id, '
                     f'st.source_uuid AS set_parent_source_uuid FROM {table} t '
                     'LEFT JOIN source_set st ON st.id=t.source_set_id '
                     'WHERE t.id >= ? AND t.id < ? ORDER BY t.id')
        else:
            query = f'SELECT * FROM {table} WHERE id >= ? AND id < ? ORDER BY id'
        rows = roster.execute(query, (shard['lower'], shard['upper']))
        expected = {row['id']: dict(row) for row in rows}
        require(len(expected) == shard['expected'], 'planned_roster_changed')
        attempt = 1 + sum(item['shardId'] == shard['id'] for item in manifest['attempts'])
        # Persist admission before starting any subprocess. An interrupted attempt
        # keeps its own filenames and is never overwritten or called completed.
        manifest['attempts'].append(dict(shardId=shard['id'], attempt=attempt, state='started'))
        refresh_manifest(output, manifest)
        try:
            result = capture_shard(output, shard, expected, password, actual_gzip_bytes(output), attempt)
            manifest['completedShards'].append(result)
            manifest['attempts'][-1]['state'] = 'completed'
            processed += 1
            print(json.dumps({'completedShard': shard['id'], 'rows': result['actualRows'],
                              'gzip': str(output / result['gzipPath'])}), flush=True)
        except BaseException:
            manifest['attempts'][-1]['state'] = 'failed_or_interrupted'
            raise
        finally:
            refresh_manifest(output, manifest)


def record_ending_census(output, password, manifest):
    ordinal = len(list(output.glob('ending-census-*.sql')))
    ending = capture_census(output, f'ending-census-{ordinal:03d}', password)
    manifest['endingCensus'] = ending
    manifest['currentCensusDiffersFromInitial'] = ending != manifest['initialCensus']
    manifest['pendingIncrementalTables'] = [table for table in ending
        if ending[table] != manifest['initialCensus'][table]]
    refresh_manifest(output, manifest)



def run(args):
    output = args.output.resolve()
    if not args.resume:
        output.mkdir(parents=True, exist_ok=False)
    require(output.is_dir(), 'job_directory_missing')
    with (output / 'operator.lock').open('a+') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise CaptureError('job_already_running') from None
        roster, metadata = open_roster(args.metadata_manifest, args.metadata_manifest_sha256)
        try:
            manifest = initialize_job(output, args, roster, metadata)
            require(actual_gzip_bytes(output) < MAX_COMPRESSED_BYTES, 'compressed_job_capacity_exceeded')
            require(shutil.disk_usage(output).free >= MIN_FREE_BYTES, 'free_disk_floor_reached')
            password = credentials()
            if manifest.get('plan') is None:
                initial_ordinal = len(list(output.glob('initial-census-*.sql')))
                initial = capture_census(output, f'initial-census-{initial_ordinal:03d}', password)
                manifest['initialCensus'] = initial
                census_path = output / f'initial-census-{initial_ordinal:03d}.json'
                manifest['initialCensusReceipt'] = {'path': census_path.name, **fingerprint(census_path)}
                manifest['plan'] = plan_shards(roster, initial)
                refresh_manifest(output, manifest)
            try:
                capture_selected_shards(output, args, roster, password, manifest)
                record_ending_census(output, password, manifest)
            finally:
                password = None
                refresh_manifest(output, manifest)
        finally:
            roster.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--resume', action='store_true', help='Continue this exact job; never overwrite existing attempts.')
    parser.add_argument('--domains', default=','.join(DOMAINS),
                        help='Comma-separated sources,venues,songs,setlists,files; default runs in that order.')
    parser.add_argument('--max-shards', type=int, help='Publish this many shards then stop successfully with pending work.')
    parser.add_argument('--metadata-manifest', type=Path, default=METADATA)
    parser.add_argument('--metadata-manifest-sha256', default=METADATA_SHA)
    args = parser.parse_args()
    args.domains = args.domains.split(',')
    if (not args.domains or len(set(args.domains)) != len(args.domains)
            or any(name not in DOMAINS for name in args.domains)):
        parser.error('unknown or repeated domain')
    if args.max_shards is not None and args.max_shards <= 0:
        parser.error('--max-shards must be positive')
    try:
        run(args)
    except (CaptureError, KeyboardInterrupt) as error:
        print(json.dumps({'state': 'refused_or_partial', 'diagnostic':
                          str(error) if isinstance(error, CaptureError) else 'KeyboardInterrupt'}), file=sys.stderr)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main())
