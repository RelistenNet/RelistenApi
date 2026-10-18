"""Offline capture contracts. These tests never use kubectl or PostgreSQL."""
import base64
import gzip
import importlib.util
import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('capture', Path(__file__).with_name('capture_shadow_catalog.py'))
capture = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(capture)


def line(value):
    return (json.dumps(value, ensure_ascii=False) + '\n').encode('utf-8')


def fixture(shard, rows):
    yield line({'kind': 'snapshot', 'version': capture.VERSION, 'snapshot': '123:123:',
                'read_only': 'on', 'isolation': 'repeatable read', 'in_recovery': True,
                'client_encoding': 'UTF8', 'relation': capture.DOMAINS[shard['domain']][0],
                'lower_id': shard['lower'], 'upper_id': shard['upper'],
                'expected_metadata_rows': shard['expected']})
    yield line({'kind': 'artist', 'row': {'id': 1, 'uuid': 'artist-one', 'name': 'Example'},
                'configured_provider_mappings': []})
    yield line({'kind': 'totals', 'rows': {'artists': 1}})
    for row in rows:
        yield line(row)
    yield line({'kind': 'snapshot_complete', 'shard_rows': len(rows)})


class TestCaptureShadowCatalog(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.output = Path(self.directory.name)
        self.shard = dict(id='0000-files-1-5', domain='files', lower=1, upper=5, expected=2, tail=False)
        self.expected = {
            1: {'id': 1, 'uuid': 'one', 'artist_id': 1, 'artist_uuid': 'artist-one',
                'source_id': None, 'source_uuid': None, 'source_set_id': None,
                'source_set_uuid': None, 'set_parent_source_id': None, 'set_parent_source_uuid': None},
            2: {'id': 2, 'uuid': 'two', 'artist_id': 1, 'artist_uuid': 'artist-one'},
        }
        self.rows = [dict(kind='track_sample', row=dict(id=1, uuid='one', artist_id=1,
                      source_id=None, source_set_id=None, title='Écho 🎵\u2028next\nlast'))]

    def execute(self, rows=None, expected=None, attempt=1, runner=None):
        data = list(fixture(self.shard, self.rows if rows is None else rows))

        def read(_path, _secret, accept):
            for item in data:
                accept(item)

        with patch.object(capture, 'read_query', side_effect=runner or read), \
                patch.object(capture, 'MIN_FREE_BYTES', 0):
            return capture.capture_shard(self.output, self.shard, self.expected if expected is None else expected,
                                         'do-not-retain-this-secret', 0, attempt)

    def test_streamed_gzip_keeps_exact_unicode_and_parentless_file_and_reports_missing(self):
        result = self.execute()
        original = b''.join(fixture(self.shard, self.rows))
        self.assertEqual(gzip.decompress((self.output / result['gzipPath']).read_bytes()), original)
        receipt = json.loads((self.output / result['receipt']).read_bytes())
        self.assertEqual(receipt['reconciliation']['missing'], [{'id': 2, 'uuid': 'two'}])
        self.assertEqual(receipt['reconciliation']['actualRows'], 1)
        self.assertEqual(receipt['reconciliation']['coordinateDrift'], [])
        self.assertEqual(receipt['raw']['sha256'], capture.hashlib.sha256(original).hexdigest())
        self.assertFalse(any(b'do-not-retain-this-secret' in path.read_bytes() for path in self.output.iterdir()))

    def test_changed_uuid_artist_and_parent_uuid_are_durable_drift(self):
        expected = {1: dict(id=1, uuid='old', artist_id=1, artist_uuid='old-artist',
                           source_id=9, source_uuid='old-parent', source_set_id=8,
                           source_set_uuid='old-set', set_parent_source_id=9,
                           set_parent_source_uuid='old-parent')}
        rows = [dict(kind='track_sample', row=dict(id=1, uuid='new', artist_id=1,
                     source_id=9, source_set_id=8), source_context={'uuid': 'new-parent'},
                     set_context={'uuid': 'new-set', 'source_id': 10},
                     set_source_context={'uuid': 'other-parent'})]
        result = self.execute(rows, expected)
        receipt = json.loads((self.output / result['receipt']).read_bytes())['reconciliation']
        self.assertEqual(len(receipt['uuidDrift']), 1)
        self.assertEqual({entry['field'] for entry in receipt['coordinateDrift']},
                         {'artist_uuid', 'source_uuid', 'source_set_uuid',
                          'set_parent_source_id', 'set_parent_source_uuid'})

    def test_extra_row_has_native_id_and_uuid_not_an_inferred_identity(self):
        rows = [dict(kind='track_sample', row=dict(id=3, uuid='extra', artist_id=1))]
        result = self.execute(rows)
        receipt = json.loads((self.output / result['receipt']).read_bytes())['reconciliation']
        self.assertEqual(receipt['extra'], [{'id': 3, 'uuid': 'extra'}])
        self.assertEqual(len(receipt['missing']), 2)

    def test_commit_failure_after_footer_never_publishes_completed_shard(self):
        def failing(_path, _secret, accept):
            for item in fixture(self.shard, self.rows):
                accept(item)
            raise capture.CaptureError('query_process_failed')

        with self.assertRaisesRegex(capture.CaptureError, 'query_process_failed'):
            self.execute(runner=failing)
        self.assertEqual(len(list(self.output.glob('*.partial.jsonl.gz'))), 1)
        self.assertFalse(list(self.output.glob('*.receipt.json')))
        failure = json.loads(next(self.output.glob('*.failure.json')).read_bytes())
        self.assertEqual(failure['compressed']['bytes'], capture.actual_gzip_bytes(self.output))
        self.assertGreater(failure['compressed']['bytes'], 0)

    def test_missing_footer_or_wrong_count_preserves_failure_prefix(self):
        for scenario in ('missing', 'wrong'):
            with self.subTest(scenario=scenario):
                def failing(_path, _secret, accept):
                    values = list(fixture(self.shard, self.rows))
                    if scenario == 'missing':
                        values.pop()
                    else:
                        values[-1] = line({'kind': 'snapshot_complete', 'shard_rows': 99})
                    for value in values:
                        accept(value)

                with self.assertRaises(capture.CaptureError):
                    self.execute(attempt=1 if scenario == 'missing' else 2, runner=failing)
        self.assertFalse(list(self.output.glob('*.receipt.json')))

    def test_compressed_budget_and_free_floor_are_refusals_not_truncation(self):
        for remaining, free, code in [(3, 100, 'compressed_job_capacity_exceeded'),
                                      (100, 3, 'free_disk_floor_reached')]:
            path = self.output / (code + '.gz')
            writer = capture.BoundedCompressedFile(path, remaining, floor=4)
            with patch.object(capture.shutil, 'disk_usage', return_value=type('Disk', (), {'free': free})()):
                with self.assertRaisesRegex(capture.CaptureError, code):
                    writer.write(b'abcd')
            writer.close()
            self.assertEqual(path.stat().st_size, 0)

    def test_actual_growth_is_bounded_and_not_silently_truncated(self):
        with patch.object(capture, 'MAX_ACTUAL_ROWS_PER_SHARD', 1):
            rows = self.rows + [dict(kind='track_sample', row=dict(id=2, uuid='two', artist_id=1))]
            with self.assertRaisesRegex(capture.CaptureError, 'actual_shard_rows_capacity_exceeded'):
                self.execute(rows)
        self.assertFalse(list(self.output.glob('*.receipt.json')))

    def test_atomic_ledger_survives_abandoned_temporary_file(self):
        target = self.output / 'manifest.json'
        abandoned = self.output / 'manifest.json.tmp'
        abandoned.write_bytes(b'old interrupted attempt')
        capture.save_json(target, {'state': 'partial'})
        capture.save_json(target, {'state': 'complete'})
        self.assertEqual(json.loads(target.read_bytes()), {'state': 'complete'})
        self.assertEqual(abandoned.read_bytes(), b'old interrupted attempt')

    def test_post_rename_receipt_failure_counts_retained_final_gzip(self):
        save = capture.save_json

        def save_with_failure(path, value):
            if path.name.endswith('.receipt.json'):
                raise OSError('synthetic receipt allocation failure')
            save(path, value)

        with patch.object(capture, 'save_json', side_effect=save_with_failure):
            with self.assertRaisesRegex(capture.CaptureError, 'OSError'):
                self.execute()
        failure = json.loads(next(self.output.glob('*.failure.json')).read_bytes())
        self.assertFalse(failure['prefix'].endswith('.partial.jsonl.gz'))
        self.assertEqual(failure['compressed'], capture.fingerprint(self.output / failure['prefix']))

    def test_receipt_recovery_and_replay_do_not_query_or_overwrite_original(self):
        result = self.execute()
        before = {path.name: path.read_bytes() for path in self.output.iterdir()}
        manifest = dict(plan=[self.shard], completedShards=[],
                        attempts=[dict(shardId=self.shard['id'], attempt=1, state='started')])
        capture.recover_receipts(self.output, manifest)
        capture.recover_receipts(self.output, manifest)
        self.assertEqual(manifest['completedShards'], [result])
        capture.verify_completed(self.output, manifest['completedShards'])
        self.assertEqual(before, {path.name: path.read_bytes() for path in self.output.iterdir()})
        (self.output / result['gzipPath']).write_bytes(b'altered')
        with self.assertRaisesRegex(capture.CaptureError, 'completed_shard_changed'):
            capture.verify_completed(self.output, manifest['completedShards'])

    def test_sparse_roster_uses_real_ids_and_captures_new_gap_and_tail_ids(self):
        roster = sqlite3.connect(':memory:')
        self.addCleanup(roster.close)
        for _, table, _ in capture.DOMAINS.values():
            roster.execute(f'CREATE TABLE {table}(id INTEGER PRIMARY KEY,uuid TEXT)')
        roster.executemany('INSERT INTO source_track VALUES(?,?)', [(2, 'a'), (999999, 'b'), (1000001, 'c')])
        census = {table: dict(count=0, minimum_id=None, maximum_id=None)
                  for table, _, _ in capture.DOMAINS.values()}
        census['source_tracks'] = dict(count=6, minimum_id=1, maximum_id=1000003)
        shards = [row for row in capture.plan_shards(roster, census, size=2) if row['domain'] == 'files']
        self.assertEqual([(row['lower'], row['upper'], row['expected']) for row in shards],
                         [(2, 1000001, 2), (1000001, 1000002, 1), (1, 2, 0), (1000002, 1000004, 0)])
        self.assertEqual(sum(row['expected'] for row in shards), 3)

    def test_resume_refuses_missing_range_before_any_new_capture(self):
        roster = sqlite3.connect(':memory:')
        self.addCleanup(roster.close)
        for _, table, _ in capture.DOMAINS.values():
            roster.execute(f'CREATE TABLE {table}(id INTEGER PRIMARY KEY,uuid TEXT)')
        roster.execute("INSERT INTO source_track VALUES(1,'original')")
        census = {table: dict(count=0, minimum_id=None, maximum_id=None)
                  for table, _, _ in capture.DOMAINS.values()}
        census['source_tracks'] = dict(count=1, minimum_id=1, maximum_id=1)
        header = json.loads(next(fixture(self.shard, self.rows)))
        path = self.output / 'initial-census-000.json'
        capture.save_json(path, {'header': header, 'tables': census})
        manifest = dict(initialCensus=census, initialCensusReceipt={
            'path': path.name, **capture.fingerprint(path)}, plan=capture.plan_shards(roster, census))
        capture.validate_plan(self.output, roster, manifest)
        manifest['plan'] = []
        with self.assertRaisesRegex(capture.CaptureError, 'planned_ranges_changed'):
            capture.validate_plan(self.output, roster, manifest)

    def test_read_only_proof_and_row_order_are_required_for_completion(self):
        for scenario in ('writable', 'duplicate', 'wrong_artist'):
            with self.subTest(scenario=scenario):
                def invalid(_path, _secret, accept):
                    values = list(fixture(self.shard, self.rows))
                    if scenario == 'writable':
                        header = json.loads(values[0]); header['read_only'] = 'off'
                        values[0] = line(header)
                    elif scenario == 'duplicate':
                        values.insert(-1, values[-2])
                    else:
                        row = json.loads(values[-2]); row['row']['artist_id'] = 2
                        values[-2] = line(row)
                    for value in values:
                        accept(value)
                with self.assertRaises(capture.CaptureError):
                    self.execute(attempt=('writable', 'duplicate', 'wrong_artist').index(scenario) + 1,
                                 runner=invalid)
        self.assertFalse(list(self.output.glob('*.receipt.json')))

    def test_credentials_never_enter_argv_or_inherited_pg_redirects(self):
        secret = 'sensitive-value'
        with patch.dict(os.environ, {'PGHOSTADDR': 'wrong', 'PGSERVICE': 'wrong',
                                    'PGPASSFILE': '/wrong', 'OTHER_SETTING': 'retained'}):
            environment = capture.query_environment(secret)
        self.assertEqual(environment['PGPASSWORD'], secret)
        self.assertEqual(environment['OTHER_SETTING'], 'retained')
        self.assertFalse({'PGHOSTADDR', 'PGSERVICE', 'PGPASSFILE'} & environment.keys())
        with patch.object(capture.subprocess, 'run', return_value=type('Result', (), {
                'stdout': base64.b64encode(secret.encode())})()) as command:
            self.assertEqual(capture.credentials(), secret)
            self.assertNotIn(secret, repr(command.call_args))
        with patch.object(capture.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'kubectl', output=secret)):
            with self.assertRaisesRegex(capture.CaptureError, '^credential_acquisition_failed$'):
                capture.credentials()

    def test_physical_lf_stream_handles_split_unicode_and_external_deadline(self):
        original_popen = subprocess.Popen
        payload = b''.join(fixture(self.shard, self.rows))
        child = ('import os,time; data=' + repr(payload) + '; '
                 '[os.write(1,data[i:i+3]) for i in range(0,len(data),3)]')

        def launch(*_args, **kwargs):
            return original_popen([sys.executable, '-c', child], **kwargs)

        observed = []
        with patch.object(capture.subprocess, 'Popen', side_effect=launch):
            capture.read_query(self.output / 'unused.sql', 'not-in-argv', observed.append)
        self.assertEqual(b''.join(observed), payload)
        child = 'import time;time.sleep(10)'
        with patch.object(capture.subprocess, 'Popen', side_effect=launch):
            with self.assertRaisesRegex(capture.CaptureError, 'query_external_deadline'):
                capture.read_query(self.output / 'unused.sql', 'not-in-argv', lambda _: None, deadline=0.02)


if __name__ == '__main__':
    unittest.main()
