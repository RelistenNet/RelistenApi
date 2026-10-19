#!/usr/bin/env python3
"""Read-only listener profiles from exported direct C# support and exact witnesses.

The overlay stores each occurrence once. Profiles refer to fact buckets, never to
name matches or graph closure. The original directory owns credit roles, recording
homes and evidence; this projection owns only proposed membership and its receipts.
A replaced base generation disables the overlay while raw directory reads continue.
"""

import argparse
from collections import Counter
from contextlib import closing
import gzip
import hashlib
from itertools import zip_longest
import json
from pathlib import Path
import shutil
import sqlite3
from urllib.parse import urlencode

VERSION = 'shared-people-directory-v1'
METHOD = 'direct_name_or_explicit_alias_and_attributed_account'
MEMBERSHIP_RULE = (
    'Each included bucket has direct C# identity support with this profile’s anchor: '
    'a name or explicit alias and an attributed account agree. All exact mapped '
    'recording occurrences in those buckets are included. This does not take '
    'transitive matches. Conflicting or uncertain candidates are not included; '
    'a star containing conflicting members requires review. These profiles are provisional.'
)


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(',', ':'))


def sha256(path):
    digest = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def rows(path):
    with gzip.open(path, 'rt', encoding='utf-8') as stream:
        for text in stream:
            yield json.loads(text)


def profile_id(anchor):
    return 'shared-person:' + anchor


class SharedPeopleOverlayBuilder:
    """Build a new immutable membership overlay; never write the base directory."""

    def __init__(self, database, shared_run, mapping_run, output,
                 minimum_free_bytes=int(5.5 * 1024**3), maximum_bytes=512 * 1024**2):
        self.database, self.shared_run, self.mapping_run, self.output = (
            Path(value).resolve() for value in (database, shared_run, mapping_run, output)
        )
        self.minimum_free_bytes, self.maximum_bytes = minimum_free_bytes, maximum_bytes
        self.pins = {}

    def _pin(self, path, expected=None):
        actual = sha256(path)
        if expected is not None and actual != expected:
            raise ValueError('Shared people artifact hash differs: ' + str(path))
        self.pins[Path(path)] = actual
        return actual

    def _check_space(self, path):
        if shutil.disk_usage(self.output.parent).free < self.minimum_free_bytes:
            raise ValueError('Shared people overlay disk floor reached')
        if path.exists() and path.stat().st_size > self.maximum_bytes:
            raise ValueError('Shared people overlay exceeds its byte budget')

    def _inputs(self):
        if Path(str(self.database) + '-wal').exists():
            raise ValueError('Shared people overlay requires a sealed base directory')

        shared_path = self.shared_run / 'manifest.json'
        mapping_path = self.mapping_run / 'manifest.json'
        self._pin(shared_path)
        self._pin(mapping_path)
        shared = json.loads(shared_path.read_text())
        mapping = json.loads(mapping_path.read_text())
        if (
            shared.get('version') != 'shared-people-export-v1'
            or shared.get('state') != 'completed'
            or shared.get('acceptedIdentity') is not False
            or shared.get('qualifiedForAutomaticAcceptance') is not False
            or shared.get('policy') != METHOD
        ):
            raise ValueError('Shared people export is not a completed provisional support export')
        if (
            mapping.get('version') != 'current-people-occurrence-credit-map-v1'
            or mapping.get('state') != 'completed'
            or mapping.get('acceptedIdentity') is not False
            or mapping.get('matchingPerformed') is not False
            or Path(mapping['sharedRun']).resolve() != self.shared_run
            or Path(mapping['database']).resolve() != self.database
            or Path(mapping['sourceRun']).resolve() != Path(shared['sourceRun']).resolve()
        ):
            raise ValueError('Occurrence mapping belongs to a different shared people generation')

        self._pin(self.database, mapping['databaseSha256'])
        declared = {Path(pin['path']).resolve(): pin['sha256'] for pin in mapping['inputs']}
        if (
            declared.get(shared_path) != self.pins[shared_path]
            or declared.get(self.database) != self.pins[self.database]
        ):
            raise ValueError('Occurrence mapping does not pin this export and base directory')

        outputs = {item['path']: item for item in shared['outputs']}
        for name in ('occurrences.jsonl.gz', 'buckets.jsonl.gz', 'hypotheses.jsonl.gz'):
            self._pin(self.shared_run / name, outputs[name]['sha256'])
        if mapping['output']['path'] != 'mappings.jsonl.gz':
            raise ValueError('Unexpected occurrence mapping artifact')
        self._pin(self.mapping_run / 'mappings.jsonl.gz', mapping['output']['sha256'])
        return shared, mapping

    @staticmethod
    def _schema(db):
        db.executescript('''
            CREATE TABLE metadata(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE buckets(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE occurrences(id TEXT PRIMARY KEY,bucket TEXT NOT NULL,source TEXT NOT NULL,
                artist TEXT,person_key TEXT NOT NULL,name TEXT NOT NULL,aliases TEXT NOT NULL,origin TEXT NOT NULL);
            CREATE INDEX occurrences_bucket ON occurrences(bucket);
            CREATE TABLE mapping_issues(occurrence TEXT PRIMARY KEY,unmapped_credits INTEGER NOT NULL);
            CREATE TABLE members(occurrence TEXT NOT NULL,kind TEXT NOT NULL,id TEXT NOT NULL,
                PRIMARY KEY(occurrence,kind,id));
            CREATE INDEX members_id ON members(kind,id);
            CREATE TABLE edges(anchor TEXT NOT NULL,candidate TEXT NOT NULL,payload TEXT NOT NULL,
                PRIMARY KEY(anchor,candidate));
            CREATE TABLE profile_buckets(anchor TEXT NOT NULL,bucket TEXT NOT NULL,PRIMARY KEY(anchor,bucket));
            CREATE INDEX profile_buckets_bucket ON profile_buckets(bucket);
            CREATE TABLE blocked_profiles(anchor TEXT PRIMARY KEY,reason TEXT NOT NULL);
            CREATE TABLE profiles(anchor TEXT PRIMARY KEY,id TEXT UNIQUE NOT NULL,visible INTEGER NOT NULL,
                summary TEXT NOT NULL);
        ''')

    @staticmethod
    def _credit_member(db, item, mapping):
        row = db.execute(
            'SELECT * FROM base.credits WHERE id=?', (item['retainedCreditId'],)
        ).fetchone()
        if row is None:
            raise ValueError('Mapped credit is absent from the retained directory')

        origin = item['retainedOrigin']
        original_run = row['origin_run']
        if not original_run:
            original_run = json.loads(db.execute(
                "SELECT value FROM base.metadata WHERE key='sourceRun'"
            ).fetchone()[0])
        actual = (
            row['source_uuid'], row['artist_uuid'], row['credit_key'], row['person_key'],
            original_run, row['chunk'], row['line'],
        )
        expected = (
            mapping['sourceUuid'], mapping['artistUuid'], item['retainedCreditKey'],
            item['retainedPersonKey'], origin['run'], origin['chunk'], origin['line'],
        )
        if actual != expected:
            raise ValueError('Mapped credit no longer has its exact retained ownership')
        return 'credit', str(row['id'])

    @staticmethod
    def _source_actor_member(db, person, occurrence, mapping):
        row = db.execute(
            'SELECT * FROM base.source_actors WHERE id=?',
            (mapping['sourceActorObservationId'],),
        ).fetchone()
        if row is None:
            raise ValueError('Mapped source actor is absent from the retained directory')

        origin = occurrence['origin']
        actual = (
            row['source_uuid'], row['artist_uuid'], row['person_key'], row['actor_reference'],
            row['origin_run'], row['chunk'], row['line'],
        )
        expected = (
            person['sourceUuid'], person['artistUuid'], person['entityKey'],
            person.get('sourceReferenceUuid'), origin['run'], origin['chunk'], origin['line'],
        )
        if actual != expected:
            raise ValueError('Mapped source actor no longer has its exact retained ownership')
        return 'source_actor', str(row['id'])

    def _mapped_members(self, db, occurrence, mapping):
        person = occurrence['person']
        actual = (
            mapping['occurrenceKey'], mapping['sourceUuid'], mapping['artistUuid'],
            mapping['personEntityKey'], mapping['peopleOrigin'],
        )
        expected = (
            occurrence['occurrenceKey'], person['sourceUuid'], person['artistUuid'],
            person['entityKey'], occurrence['origin'],
        )
        if actual != expected or mapping.get('acceptedIdentity') is not False:
            raise ValueError('Mapped occurrence differs from its exported actor')
        if [item['currentCreditKey'] for item in mapping['creditMappings']] != occurrence['creditKeys']:
            raise ValueError('Mapped credit census differs from the exported actor')

        members = []
        for item in mapping['creditMappings']:
            if item['state'] not in ('mapped', 'unmapped', 'ambiguous'):
                raise ValueError('Unknown occurrence mapping state')
            if item['state'] == 'mapped':
                members.append(self._credit_member(db, item, mapping))

        if occurrence['creditKeys']:
            if mapping['scope'] != 'credit_occurrence':
                raise ValueError('Credit occurrence has a role-free mapping')
            return members

        if mapping['scope'] != 'role_free_actor_observation':
            raise ValueError('Role-free actor mapping differs')
        if mapping['sourceActorState'] not in ('mapped', 'unmapped', 'ambiguous'):
            raise ValueError('Unknown source actor mapping state')
        if mapping['sourceActorState'] == 'mapped':
            members.append(self._source_actor_member(db, person, occurrence, mapping))
        return members

    def _occurrences(self, db, shared, mapping):
        count = 0
        known_buckets = {row[0] for row in db.execute('SELECT id FROM buckets')}
        referenced_buckets = {bucket for row in db.execute('SELECT anchor,candidate FROM edges') for bucket in row}
        for occurrence, mapped in zip_longest(rows(self.shared_run / 'occurrences.jsonl.gz'), rows(self.mapping_run / 'mappings.jsonl.gz')):
            if occurrence is None or mapped is None:
                raise ValueError('Occurrence mapping is incomplete')
            person = occurrence['person']
            if occurrence['bucketId'] not in known_buckets:
                raise ValueError('Occurrence names an unknown fact bucket')
            if mapped['occurrenceKey'] != occurrence['occurrenceKey'] or mapped['peopleOrigin'] != occurrence['origin']:
                raise ValueError('Occurrence mapping ledger order or original differs')
            count += 1
            if occurrence['bucketId'] not in referenced_buckets:
                continue
            members = self._mapped_members(db, occurrence, mapped)
            db.execute('INSERT INTO occurrences VALUES(?,?,?,?,?,?,?,?)', (occurrence['occurrenceKey'], occurrence['bucketId'],
                person['sourceUuid'], person['artistUuid'], person['entityKey'], person['name'], encoded(person.get('aliases') or []), encoded(occurrence['origin'])))
            db.executemany('INSERT INTO members VALUES(?,?,?)', [(occurrence['occurrenceKey'], kind, identifier) for kind, identifier in members])
            missing_credits = sum(item['state'] != 'mapped' for item in mapped['creditMappings'])
            db.execute('INSERT INTO mapping_issues VALUES(?,?)',(occurrence['occurrenceKey'],missing_credits))
            if count % 1000 == 0:
                self._check_space(Path(str(self.output) + '.building'))
        if count != shared['occurrences'] or count != mapping['counts']['occurrences']:
            raise ValueError('Mapped occurrence denominator differs')
        return count

    @staticmethod
    def _profiles(db):
        seen = set()
        for row in db.execute('SELECT DISTINCT anchor FROM profile_buckets ORDER BY anchor').fetchall():
            anchor = row[0]
            conflict = db.execute('''SELECT 1 FROM edges e
                JOIN profile_buckets l ON l.bucket=e.anchor AND l.anchor=?
                JOIN profile_buckets r ON r.bucket=e.candidate AND r.anchor=?
                WHERE json_extract(e.payload,'$.hasConflict')=1 LIMIT 1''', (anchor,anchor)).fetchone()
            if conflict:
                db.execute('INSERT INTO blocked_profiles VALUES(?,?)',(anchor,'conflicting_supported_members_require_review'))
                continue
            occurrences = db.execute('''SELECT o.* FROM occurrences o JOIN profile_buckets p ON p.bucket=o.bucket
                WHERE p.anchor=? ORDER BY o.id''', (anchor,)).fetchall()
            mapped = db.execute('''SELECT DISTINCT m.kind,m.id,o.source,o.artist FROM profile_buckets p
                JOIN occurrences o ON o.bucket=p.bucket JOIN members m ON m.occurrence=o.id WHERE p.anchor=?''', (anchor,)).fetchall()
            if not mapped:
                continue
            membership = tuple(sorted({(row['kind'],row['id']) for row in mapped}))
            names = Counter(row['name'] for row in occurrences)
            names = sorted(names, key=lambda name: (-names[name], -len(name), name.casefold(), name))
            aliases = set()
            for occurrence in occurrences:
                for alias in json.loads(occurrence['aliases']):
                    aliases.add(alias if isinstance(alias, str) else alias['wording'])
            missing = db.execute('''SELECT count(*) FROM occurrences o JOIN profile_buckets p ON p.bucket=o.bucket
                WHERE p.anchor=? AND NOT EXISTS(SELECT 1 FROM members m WHERE m.occurrence=o.id)''', (anchor,)).fetchone()[0]
            missing_credits = db.execute('''SELECT coalesce(sum(i.unmapped_credits),0) FROM mapping_issues i
                JOIN occurrences o ON o.id=i.occurrence JOIN profile_buckets p ON p.bucket=o.bucket WHERE p.anchor=?''', (anchor,)).fetchone()[0]
            summary = dict(id=profile_id(anchor),displayName=names[0],names=names,aliases=sorted(aliases,key=str.casefold),
                recordingCount=len({row['source'] for row in mapped}),artistCount=len({row['artist'] for row in mapped}),
                creditCount=len({row['id'] for row in mapped if row['kind']=='credit'}),
                sourceObservationCount=len({row['id'] for row in mapped if row['kind']=='source_actor'}),
                occurrenceCount=len(occurrences),unmappedOccurrenceCount=missing,unmappedCreditCount=missing_credits,status='provisional',acceptedIdentity=False,
                basis='direct_bucket_support')
            db.execute('INSERT INTO profiles VALUES(?,?,?,?)', (anchor,profile_id(anchor),int(membership not in seen),encoded(summary)))
            seen.add(membership)

    def build(self):
        if self.output.exists() or Path(str(self.output) + '.building').exists():
            raise FileExistsError(self.output)
        self.output.parent.mkdir(parents=True, exist_ok=True)
        temporary = Path(str(self.output) + '.building')
        self._check_space(temporary)
        shared, mapping = self._inputs()
        with closing(sqlite3.connect(temporary)) as db:
            db.row_factory = sqlite3.Row
            db.execute('ATTACH DATABASE ? AS base', (self.database.as_uri() + '?mode=ro',))
            self._schema(db)
            for bucket in rows(self.shared_run / 'buckets.jsonl.gz'):
                db.execute('INSERT INTO buckets VALUES(?,?)', (bucket['bucketId'], encoded(bucket)))
            if db.execute('SELECT count(*) FROM buckets').fetchone()[0] != shared['bucketCount']:
                raise ValueError('Shared fact bucket denominator differs')
            comparisons = 0
            for edge in rows(self.shared_run / 'hypotheses.jsonl.gz'):
                if (edge['method'] != METHOD or edge['acceptedIdentity'] is not False
                        or edge['qualifiedForAutomaticAcceptance'] is not False
                        or (edge['supported'] and (edge['hasConflict'] or edge['uncertain'] or not edge['accountAgrees']))):
                    raise ValueError('Shared support edge contradicts its declared policy')
                for key in ('anchorBucketId','candidateBucketId'):
                    bucket = db.execute('SELECT payload FROM buckets WHERE id=?',(edge[key],)).fetchone()
                    if bucket is None:
                        raise ValueError('Shared edge names an unknown fact bucket')
                    if edge['supported'] and not json.loads(bucket[0])['projectionComplete']:
                        raise ValueError('Supported edge has incomplete actor facts')
                db.execute('INSERT INTO edges VALUES(?,?,?)',(edge['anchorBucketId'],edge['candidateBucketId'],encoded(edge)))
                if edge['supported']:
                    db.executemany('INSERT OR IGNORE INTO profile_buckets VALUES(?,?)',
                        [(edge['anchorBucketId'], bucket) for bucket in (edge['anchorBucketId'], edge['candidateBucketId'])])
                comparisons += 1
            if comparisons != shared['comparisons']:
                raise ValueError('Shared comparison denominator differs')
            count = self._occurrences(db, shared, mapping)
            self._profiles(db)
            for path, digest in self.pins.items():
                if sha256(path) != digest:
                    raise ValueError('Shared people input changed during projection')
            metadata = dict(version=VERSION,state='completed',database=str(self.database),databaseSha256=mapping['databaseSha256'],
                sharedRun=str(self.shared_run),mappingRun=str(self.mapping_run),occurrences=count,
                profiles=db.execute('SELECT count(*) FROM profiles').fetchone()[0],
                reviewRequiredProfiles=db.execute('SELECT count(*) FROM blocked_profiles').fetchone()[0],
                inputs=[dict(path=str(path),sha256=digest) for path,digest in self.pins.items()],acceptedIdentity=False)
            db.executemany('INSERT INTO metadata VALUES(?,?)',[(key,encoded(value)) for key,value in metadata.items()])
            db.commit()
        self._check_space(temporary)
        temporary.rename(self.output)
        return metadata


class SharedPeopleOverlay:
    """Authenticate one sealed overlay against the base file before serving it.

    File stamps are tied to the directory's already-open read snapshot. A later
    replacement cannot relabel an old snapshot or reuse membership on a new base.
    The base hash is checked once, not on every listener request.
    """

    def __init__(self, database, overlay):
        self.database, self.path = Path(database).resolve(), Path(overlay).resolve()
        self.state = dict(state='unavailable')
        self.base_stamp = self.overlay_stamp = None
        if not self.path.exists():
            self.state['reason'] = 'overlay_missing'
            return
        before = self._stamp(self.database)
        with closing(sqlite3.connect(self.path.as_uri() + '?mode=ro', uri=True)) as db:
            metadata = {key: json.loads(value) for key,value in db.execute('SELECT key,value FROM metadata')}
        if metadata.get('version') != VERSION or metadata.get('state') != 'completed':
            raise ValueError('Shared people overlay is incomplete or unsupported')
        if (Path(metadata['database']).resolve() != self.database
                or Path(str(self.database) + '-wal').exists()
                or sha256(self.database) != metadata['databaseSha256'] or before != self._stamp(self.database)):
            self.state = dict(state='stale',reason='base_generation_changed')
            return
        self.base_stamp, self.overlay_stamp = before, self._stamp(self.path)
        self.state = dict(state='ready',profiles=metadata['profiles'],
            reviewRequiredProfiles=metadata['reviewRequiredProfiles'],
            sharedRun=metadata['sharedRun'],mappingRun=metadata['mappingRun'])

    @staticmethod
    def _stamp(path):
        try:
            value = path.stat()
            return (value.st_ino,value.st_size,value.st_mtime_ns)
        except FileNotFoundError:
            return None

    def current_state(self, db):
        if self.state['state'] != 'ready':
            return dict(self.state)
        expected = (self.base_stamp,None)
        if db.generation_stamp != expected or self._stamp(self.path) != self.overlay_stamp:
            return dict(state='stale',reason='base_or_overlay_generation_changed')
        return dict(self.state)

    def reader(self, db, name_key):
        if self.current_state(db)['state'] != 'ready':
            return None
        db.execute('ATTACH DATABASE ? AS shared_people', (self.path.as_uri() + '?mode=ro',))
        db.execute("SELECT value FROM shared_people.metadata WHERE key='version'").fetchone()
        if self.current_state(db)['state'] != 'ready':
            return None
        return SharedPeopleReader(db,name_key)


class SharedPeopleReader:
    """Query exact mapped members inside the caller's base read transaction."""

    def __init__(self, db, name_key):
        self.db, self.name_key = db, name_key

    def summaries(self):
        return [json.loads(row[0]) for row in self.db.execute('SELECT summary FROM shared_people.profiles WHERE visible=1')]

    def for_group(self, identifier):
        members = "SELECT 'credit' kind,cast(id AS TEXT) id FROM credits WHERE group_id=?"
        values = [identifier]
        if self.db.execute("SELECT 1 FROM sqlite_master WHERE name='source_actors'").fetchone():
            members += " UNION ALL SELECT 'source_actor',id FROM source_actors WHERE group_id=?"
            values.append(identifier)
        query = '''WITH group_members AS (''' + members + ''')
            SELECT DISTINCT p.summary FROM group_members g
            JOIN shared_people.members m ON m.kind=g.kind AND m.id=g.id
            JOIN shared_people.occurrences o ON o.id=m.occurrence
            JOIN shared_people.profile_buckets b ON b.bucket=o.bucket
            JOIN shared_people.profiles p ON p.anchor=b.anchor WHERE p.visible=1'''
        return [json.loads(row[0]) for row in self.db.execute(query,values)]

    def _membership(self, anchor, kind):
        return [row[0] for row in self.db.execute('''SELECT DISTINCT m.id FROM shared_people.profile_buckets p
            JOIN shared_people.occurrences o ON o.bucket=p.bucket JOIN shared_people.members m ON m.occurrence=o.id
            WHERE p.anchor=? AND m.kind=? ORDER BY m.id''',(anchor,kind))]

    def detail(self, identifier):
        row = self.db.execute('SELECT anchor,summary FROM shared_people.profiles WHERE id=?',(identifier,)).fetchone()
        if row is None:
            return None
        anchor,result = row[0],json.loads(row[1])
        result.update(anchorBucketId=anchor,anchorCreditId=None,membershipRule=MEMBERSHIP_RULE,
                      support=[],conflicts=[],insufficientEvidence=[],relatedSupport=[],overlappingHypotheses=[])
        for edge_row in self.db.execute('SELECT payload FROM shared_people.edges WHERE anchor=? ORDER BY candidate',(anchor,)):
            edge = json.loads(edge_row[0])
            bucket_names = []
            for bucket in (anchor,edge['candidateBucketId']):
                occurrence = self.db.execute('SELECT name FROM shared_people.occurrences WHERE bucket=? ORDER BY id LIMIT 1',(bucket,)).fetchone()
                bucket_names.append(occurrence[0] if occurrence else bucket)
            item = dict(id=anchor + ':' + edge['candidateBucketId'],leftName=bucket_names[0],rightName=bucket_names[1],
                relation='same_person' if edge['supported'] else 'different_people' if edge['hasConflict'] else 'insufficient_evidence',
                reason='Direct name or explicit alias and attributed account support' if edge['supported'] else 'Conflicting actor facts' if edge['hasConflict'] else 'Insufficient direct support',
                evidence=[],basis='direct_bucket_support',evidenceUrl='/api/shared-person-support?' + urlencode(dict(id=identifier,candidate=edge['candidateBucketId'])))
            result['support' if edge['supported'] else 'conflicts' if edge['hasConflict'] else 'insufficientEvidence'].append(item)
        credit_ids = self._membership(anchor,'credit')
        credits = []
        # SQLite's parameter ceiling is independent of the supported population.
        # Chunk only the lookup, then return every exact member.
        for start in range(0,len(credit_ids),500):
            part = credit_ids[start:start+500]
            credits.extend(dict(row) for row in self.db.execute('SELECT c.*,a.name artist_name FROM credits c LEFT JOIN artists a ON a.uuid=c.artist_uuid WHERE c.id IN ('+','.join('?' for _ in part)+')',part))
        actors = []
        for identifier in self._membership(anchor,'source_actor'):
            actors.append(dict(self.db.execute('SELECT s.*,a.name artist_name FROM source_actors s LEFT JOIN artists a ON a.uuid=s.artist_uuid WHERE s.id=?',(identifier,)).fetchone()))
        result['_sourceActors'] = actors
        return result,credits

    def support(self, identifier, candidate):
        if not identifier.startswith('shared-person:'):
            return None
        anchor = identifier[len('shared-person:'):]
        row = self.db.execute('SELECT payload FROM shared_people.edges WHERE anchor=? AND candidate=?',(anchor,candidate)).fetchone()
        if row is None:
            return None
        buckets = []
        for bucket in (anchor,candidate):
            payload = json.loads(self.db.execute('SELECT payload FROM shared_people.buckets WHERE id=?',(bucket,)).fetchone()[0])
            representative = self.db.execute('SELECT * FROM shared_people.occurrences WHERE id=?',(payload['representativeOccurrence'],)).fetchone()
            original = dict(representative) if representative else None
            if original:
                original['origin'] = json.loads(original['origin'])
                original['aliases'] = json.loads(original['aliases'])
                member = self.db.execute(
                    'SELECT kind,id FROM shared_people.members WHERE occurrence=? ORDER BY kind,id LIMIT 1',
                    (original['id'],),
                ).fetchone()
                if member:
                    route = '/api/credit?' if member['kind'] == 'credit' else '/api/source-actor?'
                    original['sourceEvidenceUrl'] = route + urlencode(dict(id=member['id']))
            buckets.append(dict(bucket=payload,representative=original))
        return dict(support=json.loads(row[0]),buckets=buckets,acceptedIdentity=False)


class CombinedPeopleHypotheses:
    """Expose both authorities without unioning their membership decisions."""

    def __init__(self, context, broad):
        self.context,self.broad = context,broad
        self.visible = context.visible + ([dict(id=item['id']) for item in broad.summaries()] if broad else [])

    def search(self, query, offset, limit):
        context = self.context.search(query,0,len(self.context.visible))['personHypotheses']
        key = self.context.name_key(query)
        broad = self.broad.summaries() if self.broad else []
        results = context + [item for item in broad if not key or any(key in self.context.name_key(name) for name in item['names']+item['aliases'])]
        results.sort(key=lambda item:(-item['recordingCount'],-item['artistCount'],item['id']))
        return dict(personHypotheses=results[offset:offset+limit],personHypothesesTotal=len(results),personHypothesesOffset=offset,personHypothesesLimit=limit)

    def for_group(self, identifier):
        return self.context.for_group(identifier) + (self.broad.for_group(identifier) if self.broad else [])

    def detail(self, identifier):
        return (self.broad.detail(identifier) if self.broad and identifier.startswith('shared-person:') else self.context.detail(identifier))

    def credit_count(self):
        identifiers = {str(credit) for person in self.context.visible for credit in person['members']}
        if self.broad:
            identifiers.update(row[0] for row in self.broad.db.execute("""SELECT DISTINCT m.id
                FROM shared_people.members m JOIN shared_people.occurrences o ON o.id=m.occurrence
                JOIN shared_people.profile_buckets b ON b.bucket=o.bucket
                JOIN shared_people.profiles p ON p.anchor=b.anchor WHERE m.kind='credit'"""))
        return len(identifiers)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database',required=True)
    parser.add_argument('--shared-run',required=True)
    parser.add_argument('--mapping-run',required=True)
    parser.add_argument('--output',required=True)
    parser.add_argument('--minimum-free-bytes',type=int,default=int(5.5*1024**3))
    parser.add_argument('--maximum-bytes',type=int,default=512*1024**2)
    args = parser.parse_args()
    print(encoded(SharedPeopleOverlayBuilder(args.database,args.shared_run,args.mapping_run,args.output,
        args.minimum_free_bytes,args.maximum_bytes).build()),flush=True)


if __name__ == '__main__':
    main()
