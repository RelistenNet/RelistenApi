"""Public shared-person pages use producer support and exact retained ownership.

The fixtures model completed C# bucket exports and the authenticated occurrence
map. They deliberately do not decide identity from names, contacts, or aliases.
"""

from contextlib import closing
import gzip
import hashlib
import json
import sqlite3
import threading
from http.server import ThreadingHTTPServer
import unittest
from urllib.parse import parse_qs, urlencode, urlsplit
from urllib.request import urlopen

from people_directory import PeopleDirectory, group_id, handler_for, name_key
from shared_people_directory import SharedPeopleOverlayBuilder, profile_id
import test_people_directory as directory_fixture


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class TestSharedPeopleDirectory(unittest.TestCase):
    def setUp(self):
        self.fixture = directory_fixture.TestPeopleDirectory()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root.resolve()
        self.database = self.fixture.database.resolve()
        self.shared = self.root / 'shared'
        self.mapping = self.root / 'mapping'
        self.output = self.root / 'shared.sqlite3'
        self.occurrences = []
        self.buckets = []
        self.edges = []
        self.mappings = []

    def prepare(self, buckets, role_free=None):
        """Create retained public credit rows, then map producer occurrences to them."""
        inputs = []
        for bucket, name, count in buckets:
            for index in range(count):
                source = bucket + '-' + str(index)
                artist = 'artist-' + str(index % 3)
                role = 'taper' if index % 2 == 0 else 'transferrer'
                inputs.append((source, artist, [self.fixture.credit(source, artist, name, role)]))
        (self.root / 'performances').mkdir()
        self.write_rows(self.root / 'performances/1.jsonl.gz', [
            dict(inputKind='source_sample', inputUuid=source, outcome='proposed',
                 proposal=dict(sourceUuid=source, showUuid='show:' + source, literalDate='1970-xx-xx',
                               performanceType='early', performanceLabel='Early show'))
            for source, _, _ in inputs
        ])
        self.fixture.build(inputs)
        self.shared.mkdir()
        self.mapping.mkdir()
        with closing(sqlite3.connect(self.database)) as db:
            db.row_factory = sqlite3.Row
            credits = {row['source_uuid']: dict(row) for row in db.execute('SELECT * FROM credits')}
        people_path = self.root / 'people.jsonl.gz'
        actors = []
        for bucket, name, count in buckets:
            members = []
            for index in range(count):
                source = bucket + '-' + str(index)
                credit = credits[source]
                actor = dict(entityKey='current-person:' + source, name=name, kind=None,
                             aliases=[], sourceUuid=source, artistUuid=credit['artist_uuid'],
                             sourceReferenceUuid='reference:' + source)
                actors.append(actor)
                origin = dict(run=str(self.root), chunk=people_path.name, line=len(actors), sha256='pending')
                occurrence = dict(occurrenceKey='occurrence:' + source, bucketId=bucket, person=actor,
                                  creditKeys=['current-credit:' + source], origin=origin)
                self.occurrences.append(occurrence)
                members.append(occurrence)
                self.mappings.append(dict(occurrenceKey=occurrence['occurrenceKey'], sourceUuid=source,
                    artistUuid=credit['artist_uuid'], personEntityKey=actor['entityKey'], peopleOrigin=origin,
                    creditOrigin=dict(run=str(self.root), chunk=credit['chunk'], line=credit['line'],
                                      sha256=digest(self.root / credit['chunk'])),
                    scope='credit_occurrence', acceptedIdentity=False,
                    creditMappings=[dict(currentCreditKey=occurrence['creditKeys'][0], state='mapped',
                        retainedCreditId=credit['id'], retainedCreditKey=credit['credit_key'],
                        retainedPersonKey=credit['person_key'], witnessSha256='witness:' + source,
                        retainedOrigin=dict(run=credit['origin_run'] or str(self.root), chunk=credit['chunk'],
                                            line=credit['line'], sha256=digest(self.root / credit['chunk'])))]))
            self.buckets.append(dict(bucketId=bucket, representativeOccurrence=members[0]['occurrenceKey'],
                facts=dict(uuid=None, kind=None, names=[dict(wording=name, isHandle=False, provider=None)],
                           externalIdentities=[], artistUuids=[]), projectionComplete=True, evidence=[],
                occurrences=count, distinctSources=count, distinctArtists=min(3, count)))
        if role_free:
            self._add_role_free(role_free, actors, people_path)
        with gzip.open(people_path, 'wt') as stream:
            for actor in actors:
                aliases = [dict(wording=wording, evidence=[]) for wording in actor['aliases']]
                record = dict(inputKind='source_sample', inputUuid=actor['sourceUuid'],
                    artistUuid=actor['artistUuid'], proposal=dict(people=[actor]),
                    evidence=dict(originalInputs=dict(description='Retained fixture'),
                        sourceActorAliases=dict(assertions=[dict(actorReferenceUuid=actor['sourceReferenceUuid'],
                                                               name=actor['name'], aliases=aliases)])))
                stream.write(json.dumps(record) + '\n')
        for occurrence in self.occurrences:
            occurrence['origin']['sha256'] = digest(people_path)
        if role_free:
            with closing(sqlite3.connect(self.database)) as db, db:
                db.execute('INSERT INTO metadata VALUES(?,?)', ('sourceRefreshProgress',
                    json.dumps(dict(pins={str(people_path): digest(people_path)}))))
        self.seal()

    def _add_role_free(self, target, actors, people_path):
        bucket, name = target
        source = 'role-free-source'
        actor = dict(entityKey='alias-actor', name=name, aliases=['Night Owl'], kind=None,
                     sourceUuid=source, artistUuid='artist-alias', sourceReferenceUuid='alias-reference')
        actors.append(actor)
        origin = dict(run=str(self.root), chunk=people_path.name, line=len(actors), sha256='pending')
        self.occurrences.append(dict(occurrenceKey='role-free-occurrence', bucketId=bucket,
                                     person=actor, creditKeys=[], origin=origin))
        actor_id = 'retained-source-actor'
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute('''CREATE TABLE source_actors(id TEXT PRIMARY KEY,group_id TEXT,source_uuid TEXT,
                artist_uuid TEXT,name TEXT,person_key TEXT,actor_reference TEXT,aliases TEXT,
                chunk TEXT,line INTEGER,origin_run TEXT)''')
            db.execute('INSERT INTO artists VALUES(?,?)', ('artist-alias', 'Alias Artist'))
            db.execute('INSERT INTO source_actors VALUES(?,?,?,?,?,?,?,?,?,?,?)',
                (actor_id, group_id(name_key(name)), source, actor['artistUuid'], name, actor['entityKey'],
                 actor['sourceReferenceUuid'], json.dumps([dict(wording='Night Owl', evidence=[])]),
                 origin['chunk'], origin['line'], origin['run']))
        self.mappings.append(dict(occurrenceKey='role-free-occurrence', sourceUuid=source,
            artistUuid=actor['artistUuid'], personEntityKey=actor['entityKey'], peopleOrigin=origin,
            creditOrigin=origin, scope='role_free_actor_observation', sourceActorState='mapped',
            sourceActorObservationId=actor_id, creditMappings=[], acceptedIdentity=False))
        item = next(item for item in self.buckets if item['bucketId'] == bucket)
        item['occurrences'] += 1
        item['distinctSources'] += 1
        item['distinctArtists'] += 1

    def edge(self, anchor, candidate, supported=True, conflict=False, uncertain=False):
        self.edges.append(dict(anchorBucketId=anchor, candidateBucketId=candidate,
            supported=supported, hasConflict=conflict, uncertain=uncertain, accountAgrees=supported,
            features=[], method='direct_name_or_explicit_alias_and_attributed_account',
            acceptedIdentity=False, qualifiedForAutomaticAcceptance=False))

    @staticmethod
    def write_rows(path, rows):
        with gzip.open(path, 'wt') as stream:
            for row in rows:
                stream.write(json.dumps(row) + '\n')
        return dict(path=path.name, bytes=path.stat().st_size, sha256=digest(path))

    def seal(self):
        outputs = [self.write_rows(self.shared / name, rows) for name, rows in (
            ('occurrences.jsonl.gz', self.occurrences), ('buckets.jsonl.gz', self.buckets),
            ('hypotheses.jsonl.gz', self.edges))]
        manifest = dict(version='shared-people-export-v1', state='completed', sourceRun=str(self.root),
            manifestSha=digest(self.root / 'manifest.json'), sourceCount=len({r['person']['sourceUuid'] for r in self.occurrences}),
            occurrences=len(self.occurrences), bucketCount=len(self.buckets), comparisons=len(self.edges),
            outputs=outputs, policy='direct_name_or_explicit_alias_and_attributed_account',
            acceptedIdentity=False, qualifiedForAutomaticAcceptance=False)
        (self.shared / 'manifest.json').write_text(json.dumps(manifest))
        output = self.write_rows(self.mapping / 'mappings.jsonl.gz', self.mappings)
        mapping = dict(version='current-people-occurrence-credit-map-v1', state='completed',
            database=str(self.database), databaseSha256=digest(self.database), sharedRun=str(self.shared),
            sourceRun=str(self.root), counts=dict(occurrences=len(self.mappings)),
            inputs=[dict(path=str(self.shared / 'manifest.json'), sha256=digest(self.shared / 'manifest.json')),
                    dict(path=str(self.database), sha256=digest(self.database))],
            output=output, acceptedIdentity=False, matchingPerformed=False)
        (self.mapping / 'manifest.json').write_text(json.dumps(mapping))

    def build(self):
        SharedPeopleOverlayBuilder(self.database, self.shared, self.mapping, self.output,
                                   minimum_free_bytes=0).build()
        return PeopleDirectory(self.database, 'http://files.test/', 'http://core.test/',
                               shared_people_database=self.output)

    def serve(self, directory):
        server = ThreadingHTTPServer(('127.0.0.1', 0), handler_for(directory))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        base = 'http://127.0.0.1:' + str(server.server_port)

        def get(path):
            with urlopen(base + path) as response:
                return json.load(response)

        return get

    def detail(self, directory, bucket, **parameters):
        return directory.shared_person(profile_id(bucket), parameters)

    @staticmethod
    def sources(page):
        return {recording['sourceUuid'] for recording in page['recordings']}

    def test_direct_support_does_not_close_chains_or_absorb_conflicting_or_uncertain_namesakes(self):
        self.prepare([('a', 'Aster', 1), ('b', 'Beryl', 1), ('c', 'Cedar', 1), ('d', 'Aster', 1), ('e', 'Aster', 1)])
        self.edge('a', 'b')
        self.edge('b', 'c')
        self.edge('a', 'd', supported=False, conflict=True)
        self.edge('a', 'e', supported=False, uncertain=True)
        self.seal()
        directory = self.build()
        page = self.detail(directory, 'a')
        self.assertEqual(self.sources(page), {'a-0', 'b-0'})
        self.assertEqual(self.sources(self.detail(directory, 'b')), {'b-0', 'c-0'})
        self.assertFalse(page['acceptedIdentity'])
        self.assertTrue(page['conflicts'])
        self.assertTrue(page['insufficientEvidence'])
        get = self.serve(directory)
        found = get('/api/people?q=Aster')
        self.assertTrue(found['personHypotheses'])
        public_page = get('/api/shared-person?' + urlencode(dict(id=profile_id('a'))))
        proof = get(public_page['support'][0]['evidenceUrl'])
        self.assertEqual((proof['support']['anchorBucketId'], proof['support']['candidateBucketId']), ('a', 'b'))
        self.assertEqual([item['representative']['source'] for item in proof['buckets']], ['a-0', 'b-0'])
        self.assertFalse(proof['acceptedIdentity'])

    def test_conflicting_supported_members_hold_the_profile_without_choosing_a_side(self):
        self.prepare([('a', 'Aster', 1), ('b', 'Beryl', 1), ('c', 'Cedar', 1)])
        self.edge('a', 'b')
        self.edge('a', 'c')
        self.edge('b', 'c', supported=False, conflict=True)
        self.seal()
        directory = self.build()
        self.assertIsNone(self.detail(directory, 'a'))
        self.assertEqual(directory.search({'q': 'Aster'})['personHypotheses'], [])
        raw = directory.detail(group_id(name_key('Aster')), {})
        self.assertEqual(raw['recording_count'], 1)
        self.assertEqual(raw['recordings'][0]['sourceUuid'], 'a-0')

    def test_identical_membership_search_is_deduplicated_but_each_anchor_still_opens(self):
        self.prepare([('a', 'Aster', 1), ('b', 'Beryl', 1)])
        self.edge('a', 'b')
        self.edge('b', 'a')
        self.seal()
        directory = self.build()
        found = directory.search({'q': 'Aster'})['personHypotheses']
        self.assertEqual(len(found), 1)
        self.assertEqual(self.sources(self.detail(directory, 'a')), {'a-0', 'b-0'})
        self.assertEqual(self.sources(self.detail(directory, 'b')), {'a-0', 'b-0'})
        self.assertNotEqual(profile_id('a'), profile_id('b'))
        raw = directory.detail(group_id(name_key('Aster')), {})
        self.assertEqual(raw['recording_count'], 1)
        self.assertTrue(raw['personHypotheses'])

    def test_unmapped_bucket_does_not_duplicate_identical_mapped_membership_in_search(self):
        self.prepare([('a', 'Aster', 1), ('b', 'Beryl', 1), ('c', 'Cedar', 1)])
        self.edge('a', 'b')
        self.edge('a', 'c')
        self.edge('b', 'a')
        unmapped = next(row for row in self.mappings if row['sourceUuid'] == 'c-0')
        unmapped['creditMappings'] = [dict(
            currentCreditKey='current-credit:c-0', state='unmapped',
            reason='no_surviving_full_witness', witnessSha256='unmapped-witness',
        )]
        self.seal()
        directory = self.build()

        found = directory.search({})
        self.assertEqual(found['personHypothesesTotal'], 1)
        self.assertEqual(len(found['personHypotheses']), 1)
        first = self.detail(directory, 'a')
        second = self.detail(directory, 'b')
        self.assertEqual(self.sources(first), {'a-0', 'b-0'})
        self.assertEqual(self.sources(second), {'a-0', 'b-0'})
        self.assertEqual(first['unmappedOccurrenceCount'], 1)
        self.assertEqual(second['unmappedOccurrenceCount'], 0)
        self.assertNotEqual(first['id'], second['id'])

    def test_full_supported_buckets_expose_hundreds_of_exact_recordings_with_filters_and_source_files(self):
        self.prepare([('a', 'Aster', 125), ('b', 'Night Owl', 5)])
        self.edge('a', 'a')
        self.edge('a', 'b')
        self.seal()
        base_bytes = self.database.read_bytes()
        directory = self.build()
        page = self.detail(directory, 'a', limit='100')
        self.assertEqual((page['recordingCount'], page['creditCount'], page['artistCount'], page['total']), (130, 130, 3, 130))
        self.assertEqual(len(page['recordings']), 100)
        remaining = self.detail(directory, 'a', offset='100', limit='100')
        self.assertEqual(len(remaining['recordings']), 30)
        self.assertEqual(len(self.sources(page) | self.sources(remaining)), 130)
        filtered = self.detail(directory, 'a', artist='artist-1', role='transferrer', limit='100')
        self.assertTrue(filtered['recordings'])
        for recording in filtered['recordings']:
            self.assertEqual(recording['capturedHome']['showUuid'], 'show:' + recording['sourceUuid'])
            self.assertEqual(recording['capturedHome']['literalDate'], '1970-xx-xx')
            self.assertEqual(recording['capturedHome']['performanceLabel'], 'Early show')
            self.assertEqual(parse_qs(urlsplit(recording['playableFilesUrl']).query)['entityKey'], ['source:' + recording['sourceUuid']])
            self.assertTrue(all(credit['role'] == 'transferrer' and credit['artist_uuid'] == 'artist-1' for credit in recording['credits']))
        self.assertEqual(self.database.read_bytes(), base_bytes)
        self.assertLess(self.output.stat().st_size, 1024 * 1024)

    def test_role_free_alias_member_has_observation_and_recording_without_an_invented_role(self):
        self.prepare([('a', 'Aster', 1)], role_free=('a', 'Aster'))
        self.edge('a', 'a')
        self.seal()
        directory = self.build()
        page = self.detail(directory, 'a')
        self.assertEqual((page['recordingCount'], page['creditCount']), (2, 1))
        recording = next(recording for recording in page['recordings'] if recording['sourceUuid'] == 'role-free-source')
        self.assertEqual(recording['credits'], [])
        self.assertEqual(len(recording['sourceObservations']), 1)
        observed = recording['sourceObservations'][0]
        self.assertEqual(observed['id'], 'retained-source-actor')
        self.assertNotIn('role', observed)
        self.assertNotIn('kind', observed)
        original = self.serve(directory)(observed['sourceEvidenceUrl'])
        self.assertEqual(original['actor']['entityKey'], 'alias-actor')
        self.assertEqual(original['assertion']['aliases'][0]['wording'], 'Night Owl')
        self.assertNotIn('role', original['actor'])
        self.assertEqual({item['role'] for item in page['roles']}, {'taper'})
        self.assertNotIn('role-free-source', self.sources(self.detail(directory, 'a', role='taper')))
        self.assertTrue(directory.search({'q': 'Night Owl'})['personHypotheses'])

    def test_unmapped_occurrence_does_not_invent_a_recording_or_drop_supported_mapped_members(self):
        self.prepare([('a', 'Aster', 3)])
        self.edge('a', 'a')
        self.mappings[-1]['creditMappings'] = [dict(currentCreditKey=self.occurrences[-1]['creditKeys'][0],
            state='unmapped', reason='no_surviving_full_witness', witnessSha256='unmapped-witness')]
        self.seal()
        directory = self.build()
        self.assertEqual(self.sources(self.detail(directory, 'a')), {'a-0', 'a-1'})
        self.assertEqual(self.detail(directory, 'a')['recordingCount'], 2)
        self.assertEqual(self.detail(directory, 'a')['unmappedOccurrenceCount'], 1)

    def test_changed_input_or_incomplete_manifest_cannot_publish_an_overlay(self):
        self.prepare([('a', 'Aster', 2)])
        self.edge('a', 'a')
        self.seal()
        (self.shared / 'hypotheses.jsonl.gz').write_bytes(b'changed')
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())
        self.output = self.root / 'incomplete.sqlite3'
        self.seal()
        path = self.mapping / 'manifest.json'
        manifest = json.loads(path.read_text())
        manifest['state'] = 'building'
        path.write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())

    def test_mapping_an_unknown_credit_or_wrong_source_never_publishes_membership(self):
        self.prepare([('a', 'Aster', 2)])
        self.edge('a', 'a')
        self.mappings[0]['creditMappings'][0]['retainedCreditId'] = 99999
        self.seal()
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())
        self.output = self.root / 'wrong-source.sqlite3'
        self.mappings[0]['creditMappings'][0]['retainedCreditId'] = self.mappings[1]['creditMappings'][0]['retainedCreditId']
        self.seal()
        with self.assertRaises(ValueError):
            self.build()
        self.assertFalse(self.output.exists())

    def test_stale_overlay_leaves_raw_groups_and_existing_context_profiles_available(self):
        self.prepare([('a', 'Aster', 1), ('b', 'Beryl', 1)])
        with closing(sqlite3.connect(self.database)) as db, db:
            directory_fixture.create_context_table(db)
            ids = dict(db.execute('SELECT source_uuid,id FROM credits'))
            directory_fixture.add_context(db, 'old-context', ids['a-0'], ids['b-0'], 'same_person')
        self.edge('a', 'b')
        self.seal()
        directory = self.build()
        self.assertIsNotNone(self.detail(directory, 'a'))
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute('UPDATE artists SET name=? WHERE uuid=?', ('Changed generation', 'artist-0'))
        self.assertIsNone(self.detail(directory, 'a'))
        self.assertEqual(directory.stats()['sharedPeopleOverlay']['state'], 'stale')
        raw = directory.detail(group_id(name_key('Aster')), {})
        self.assertEqual(raw['recording_count'], 1)
        profiles = directory.search({'q': 'Aster'})['personHypotheses']
        self.assertTrue(profiles)
        self.assertTrue(all(person['id'] != profile_id('a') for person in profiles))
        self.assertEqual(profiles[0]['recordingCount'], 2)


if __name__ == '__main__':
    unittest.main()
