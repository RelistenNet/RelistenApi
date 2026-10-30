#!/usr/bin/env bash
# Select actual restored records and capture responses without changing catalog data.
set -Eeuo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cd "$repo_dir"
export PGPASSWORD=local_dev_password
python3 - <<'PY'
import json, subprocess, uuid
from pathlib import Path
out = Path('local-dev/v4-review/.output')
out.mkdir(parents=True, exist_ok=True)
samples = {
    'raw': '67db5dda-0911-d7c9-da65-abb112174f49',
    'matched': '790f8388-13ca-ea8f-5bf0-61e4cc13ac0d',
    'partial': 'eb36cd68-6ff7-7ee2-d064-eade0285b785',
    'long-medley': '304a4538-182e-88e6-8f4c-cf4178f5d336',
    'partial-medley': '3137d381-2091-4ee1-4618-2688e56a28df',
}
pg = ['psql', '-X', '-v', 'ON_ERROR_STOP=1', '-At', '-h', '127.0.0.1', '-p', '15432', '-U', 'relisten', '-d', 'relisten_db', '-c']
def query(sql):
    return subprocess.check_output(pg + [sql], text=True).strip()
def typed(prefix, raw):
    # Review-tool formatting only. Production C#/TypeScript codecs share published vectors.
    value = uuid.UUID(raw).int
    alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    suffix = ''
    for _ in range(26):
        suffix = alphabet[value & 31] + suffix
        value >>= 5
    return prefix + '_' + suffix
samples['disabled'] = query('''SELECT st.uuid FROM source_tracks st JOIN artists a ON a.id=st.artist_id
LEFT JOIN features f ON f.artist_id=a.id WHERE NOT COALESCE(f.norm_catalogue,false)
AND NOT st.is_orphaned LIMIT 1''')
requests = ['@base = http://127.0.0.1:43823', '', '### OpenAPI', 'GET {{base}}/api-docs/v4/swagger.json', '']
timings = {}
def capture(name, path, label):
    elapsed = subprocess.check_output([
        'curl', '--fail', '--silent', '--show-error', '--max-time', '45',
        '-D', str(out / (name + '.headers')),
        'http://127.0.0.1:43823/api/v4' + path,
        '-o', str(out / (name + '.json')), '--write-out', '%{time_total}'
    ], text=True)
    timings[name] = float(elapsed)
    requests.extend(['### ' + label, 'GET {{base}}/api/v4' + path, ''])
    return json.loads((out / (name + '.json')).read_text())

def metadata(detail):
    return {key: value for key, value in detail.items() if key not in ('shows', 'tracks')}


scenarios = []
# Walk the same typed references a client uses; no legacy lookup parameters.
artist_uuid = query("SELECT a.uuid FROM artists a JOIN sources src ON src.artist_id=a.id JOIN source_tracks st ON st.source_id=src.id WHERE st.uuid='790f8388-13ca-ea8f-5bf0-61e4cc13ac0d'")
artist_id = typed('artist', artist_uuid)
browse = [('artists', '/artists'), ('artist', '/artists/' + artist_id),
          ('years', '/artists/' + artist_id + '/years'),
          ('year-by-number', '/artists/' + artist_id + '/years/by-number/1985'),
          ('artist-venues', '/artists/' + artist_id + '/venues'),
          ('imported-venues', '/artists/' + artist_id + '/imported-venues')]
for name, path in browse:
    capture(name, path, 'Browse: ' + name)
# Discovery lists reuse the same summaries; exact-date/random reads include playback.
discovery = [
    ('artist-today', '/artists/' + artist_id + '/shows/today?month=7&day=7'),
    ('artist-anniversary', '/artists/' + artist_id + '/shows/on-date?month=7&day=7'),
    ('exact-date-show', '/artists/' + artist_id + '/shows/by-date/1978-07-07'),
    ('random-show', '/artists/' + artist_id + '/shows/random'),
    ('top-shows', '/artists/' + artist_id + '/shows/top?limit=5'),
    ('today-shows', '/shows/today'),
    ('anniversary-shows', '/shows/on-date?month=7&day=7'),
]
for kind in ['recently-added', 'recently-performed', 'recently-updated']:
    query_string = '?limit=5' if kind == 'recently-added' else '?shows=5'
    discovery.extend([
        ('artist-' + kind, '/artists/' + artist_id + '/shows/' + kind + query_string),
        (kind + '-shows', '/shows/' + kind + query_string),
    ])
for name, path in discovery:
    capture(name, path, 'Discovery: ' + name)
year = json.loads((out / 'year-by-number.json').read_text())
for name, path in [('year-by-id', '/artists/' + artist_id + '/years/' + year['id']),
                   ('artist-shows', '/artists/' + artist_id + '/shows')]:
    capture(name, path, 'Browse: ' + name)

entries = json.loads((out / 'artist-venues.json').read_text())
venue_entry = next(entry for entry in entries if entry['artist_venue'] and entry['artist_venue']['venue']['id'] == 'venue_wwrsykhn')
venue_id = venue_entry['artist_venue']['venue']['id']
venue = capture('venue', '/venues/' + venue_id, 'Global venue: metadata, aliases and shows')
artist_venue = capture('artist-venue', '/artist-venues/' + venue_entry['artist_venue']['id'], 'Direct artist venue: relationship and shows')
artist_venue_by_pair = capture('artist-venue-by-pair', '/artists/' + artist_id + '/venues/' + venue_id, 'Artist and venue pair resolves to the same detail')
assert artist_venue == artist_venue_by_pair
assert metadata(artist_venue) == venue_entry['artist_venue']
assert artist_venue['venue'] == metadata(venue)
venue_shows = artist_venue['shows']
shared_id = query('''SELECT v.id FROM norm.venues v
JOIN norm.show_venues sv ON sv.venue_id=v.id JOIN shows sh ON sh.uuid=sv.show_uuid
JOIN features f ON f.artist_id=sh.artist_id WHERE v.merged_into IS NULL AND f.norm_catalogue
GROUP BY v.id HAVING count(DISTINCT sh.artist_id)>1 ORDER BY count(*) DESC,v.id LIMIT 1''')
if shared_id:
    shared_venue = capture('shared-venue', '/venues/' + shared_id, 'Shared venue: metadata and shows across approved artists')
    shared_artist = next(show['artist_id'] for show in shared_venue['shows'] if show['artist_id'] == artist_id)
    shared_relationship = capture('shared-artist-venue', '/artists/' + shared_artist + '/venues/' + shared_id, 'Shared venue: artist collection beside global metadata')
    shared_direct = capture('shared-artist-venue-by-id', '/artist-venues/' + shared_relationship['id'], 'Direct relationship agrees with the artist and venue lookup')
    assert shared_direct == shared_relationship
    assert shared_relationship['venue'] == metadata(shared_venue)
    assert all(show['artist_id'] == shared_artist for show in shared_relationship['shows'])
    shared_show = capture('shared-venue-show', '/shows/' + shared_relationship['shows'][0]['id'], 'Shared venue: a show with the same compact relationship and counts')
    assert shared_show['artist_venue'] == metadata(shared_relationship)
    if shared_show['year_id']:
        shared_year = capture('shared-venue-year', '/artists/' + shared_artist + '/years/' + shared_show['year_id'], 'Shared venue: year summaries keep the full artist and global counts')
        shared_year_shows = [show for show in shared_year['shows'] if show['artist_venue'] and show['artist_venue']['venue']['id'] == shared_id]
        assert shared_year_shows and all(show['artist_venue'] == metadata(shared_relationship) for show in shared_year_shows)
imported_id = next(show['imported_venue']['id'] for show in venue_shows if show['imported_venue'])
capture('imported-venue', '/imported-venues/' + imported_id, 'Imported venue: original observations, and shows')
capture('user-imported-venue', '/imported-venues/imported_venue_78331pxdqjdg434rrgm1d2pnrj', 'Requested imported venue: direct lookup with shows')
for name, raw in samples.items():
    row = json.loads(query(f'''SELECT row_to_json(x) FROM (SELECT st.title,a.name artist,sh.uuid show_uuid
    FROM source_tracks st JOIN sources src ON src.id=st.source_id JOIN shows sh ON sh.id=src.show_id
    JOIN artists a ON a.id=src.artist_id WHERE st.uuid='{raw}'::uuid AND NOT st.is_orphaned) x'''))
    row.update(name=name, track_id=typed('track', raw), show_id=typed('show', row['show_uuid']))
    scenarios.append(row)
    for kind, path in [('track', '/source-tracks/' + row['track_id']), ('show', '/shows/' + row['show_id'])]:
        capture(name + '-' + kind, path, f'{name} {kind}: {row["artist"]} — {row["title"]}')
    track = json.loads((out / (name + '-track.json')).read_text())
    capture(name + '-source', '/sources/' + track['source_id'], 'Direct source for ' + name + ': sets and playable tracks')

venue_show = capture('venue-journey-show', '/shows/' + venue_shows[0]['id'], 'Venue journey: open a listed show')
recording = next(source for source in venue_show['sources'] if any(group['tracks'] for group in source['sets']))
venue_source = capture('venue-journey-source', '/sources/' + recording['id'], 'Venue journey: open a recording with active tracks')
journey_track = next(track for group in venue_source['sets'] for track in group['tracks'])
capture('venue-journey-track', '/source-tracks/' + journey_track['id'], 'Venue journey: open one playable track')

raw_entry = next(entry for entry in entries if entry['imported_venue'])
raw_inventory = json.loads((out / 'imported-venues.json').read_text())
raw_by_id = {venue['id']: venue for venue in raw_inventory}
assert all('fallback_show_count' not in entry for entry in entries)
assert all(entry['imported_venue'] == raw_by_id[entry['imported_venue']['id']] for entry in entries if entry['imported_venue'])
raw_detail = capture('unmatched-imported-venue', '/imported-venues/' + raw_entry['imported_venue']['id'], 'Imported browse target: complete collection including any normalized shows')
assert raw_entry['imported_venue'] == metadata(raw_detail)
disabled_track = json.loads((out / 'disabled-track.json').read_text())
disabled_entries = capture('disabled-artist-venues', '/artists/' + disabled_track['artist_id'] + '/venues', 'Unnormalized artist: imported venue browsing')
disabled_imported = next(entry['imported_venue'] for entry in disabled_entries if entry['imported_venue'])
capture('disabled-imported-venue', '/imported-venues/' + disabled_imported['id'], 'Unnormalized artist: imported venue and shows')
matched = next(row for row in scenarios if row['name'] == 'matched')
etag = next(line.split(': ', 1)[1] for line in (out / 'matched-track.headers').read_text().splitlines() if line.lower().startswith('etag:'))
requests += ['### Revalidate captured matched track (304 unless data changed)',
             'GET {{base}}/api/v4/source-tracks/' + matched['track_id'], 'If-None-Match: ' + etag, '']
historical = typed('show', '3bed435e-8cd2-764e-3a30-764b82e01298')
requests += ['### Wrong resource kind (422)', f'GET {{{{base}}}}/api/v4/source-tracks/{historical}', '',
             '### Valid unknown identity (404)', 'GET {{base}}/api/v4/shows/show_00000000000000000000000000', '']
kaiser = 'show_127p0pfc1sgb4p9hdf4agq5dch'
capture('kaiser-show', '/shows/' + kaiser, 'Oakland venue: imported label and canonical aliases')
capture('historical-show', '/shows/' + historical, 'Venue aliases: imported show label with a canonical venue link')
# Song discovery preserves exact recording matches separately from setlist evidence.
song_id = 'song_62mhvjyd'
song_entries = capture('artist-songs', '/artists/' + artist_id + '/songs', 'Song browse: canonical songs plus imported fallbacks')
song = capture('song', '/songs/' + song_id, 'Scarlet Begonias: metadata, distinct recording-backed shows')
assert next(entry['song'] for entry in song_entries if entry['song'] and entry['song']['id'] == song_id) == metadata(song)
song_track_detail = capture('song-tracks', '/songs/' + song_id + '/tracks', 'Scarlet Begonias: metadata and exact files as complete track resources')
song_tracks = song_track_detail['tracks']
assert 'shows' not in song_track_detail
assert metadata(song_track_detail) == metadata(song)
assert len({track['id'] for track in song_tracks}) == len(song_tracks)
assert {track['show_id'] for track in song_tracks} == {show['id'] for show in song['shows']}
capture('user-song', '/songs/song_mbkeejfd', 'Requested song: direct metadata and show collection')
requests += ['### Requested song: complete direct track resources',
             'GET {{base}}/api/v4/songs/song_mbkeejfd/tracks', '']
for track in song_tracks:
    assert any(part['state'] == 'matched' and part['song']['id'] == song_track_detail['id']
               for part in track['normalization']['parts'])
imported_songs = capture('imported-songs', '/artists/' + artist_id + '/imported-songs', 'Full imported song inventory, including mapped and unused rows')
imported_song_id = typed('imported_song', '73a1f1ec-0e2c-2b68-839f-946193fb3565')
imported_song = capture('imported-song', '/imported-songs/' + imported_song_id, 'Imported Scarlet Begonias: independent setlist-backed shows')
assert next(raw for raw in imported_songs if raw['id'] == imported_song_id) == metadata(imported_song)
unmatched_song = next(entry['imported_song'] for entry in song_entries if entry['imported_song'] and entry['imported_song']['id'] == typed('imported_song', '6f107d7d-037b-5e8d-e856-2662838bce2f'))
capture('unmatched-imported-song', '/imported-songs/' + unmatched_song['id'], 'Unmapped Jam: imported fallback, no title inference')
zero_song = capture('zero-match-song', '/songs/song_aaatqpek', 'Canonical song with no recording matches remains visible')
assert zero_song['matched_track_count'] == 0 and zero_song['matched_show_count'] == 0
assert zero_song['shows'] == []
zero_tracks = capture('zero-match-song-tracks', '/songs/song_aaatqpek/tracks', 'Zero-match song: metadata and empty files')
assert zero_tracks['tracks'] == [] and 'shows' not in zero_tracks
assert metadata(zero_tracks) == metadata(zero_song)
raw_artist = typed('artist', '2e38a274-2d4d-787f-bfcf-73fb2c79cc4a')
raw_songs = capture('disabled-artist-songs', '/artists/' + raw_artist + '/songs', 'Ryan Adams: same browse route without approved normalization')
raw_song_inventory = capture('disabled-imported-songs', '/artists/' + raw_artist + '/imported-songs', 'Ryan Adams: full imported song inventory')
assert raw_songs and all(entry['kind'] == 'imported_song' and entry['song'] is None for entry in raw_songs)
assert [entry['imported_song'] for entry in raw_songs] == raw_song_inventory
raw_song_detail = capture('disabled-imported-song', '/imported-songs/' + raw_songs[0]['imported_song']['id'], 'Ryan Adams: imported song navigation remains usable')
file = song_tracks[0]
direct_file = capture('song-journey-track', '/source-tracks/' + file['id'], 'Song journey: direct playable file is identical')
assert direct_file == file
song_source = capture('song-journey-source', '/sources/' + file['source_id'], 'Song journey: exact recording')
assert next(track for group in song_source['sets'] for track in group['tracks'] if track['id'] == file['id']) == file
song_show = capture('song-journey-show', '/shows/' + file['show_id'], 'Song journey: full show hierarchy contains the same recording')
assert next(source for source in song_show['sources'] if source['id'] == file['source_id']) == song_source

# Complete the recording navigation with tours, embedded eras, and source reviews.
phish_uuid = query("SELECT uuid FROM artists WHERE slug='phish'")
phish_id = typed('artist', phish_uuid)
phish_tours = capture('phish-tours', '/artists/' + phish_id + '/tours', 'Phish: compact tour inventory and catalog show counts')
phish_tour = next(tour for tour in phish_tours if tour['name'] == 'Fall Tour 1997')
tour_detail = capture('phish-tour', '/tours/' + phish_tour['id'], 'Phish Fall Tour 1997: direct tour metadata and complete shows')
assert metadata(tour_detail) == phish_tour
assert tour_detail['show_count'] == len(tour_detail['shows'])
assert all(show['artist_id'] == phish_id and show['tour'] == phish_tour for show in tour_detail['shows'])
phish_eras = capture('phish-eras', '/artists/' + phish_id + '/eras', 'Phish: ordered era metadata, no public era identity')
assert phish_eras and all(set(era) == {'order', 'name'} for era in phish_eras)
review_source_uuid = query("SELECT src.uuid FROM sources src JOIN source_reviews r ON r.source_id=src.id GROUP BY src.id ORDER BY count(*) DESC,src.uuid LIMIT 1")
review_source_id = typed('source', review_source_uuid)
source_reviews = capture('source-reviews', '/sources/' + review_source_id + '/reviews', 'Recording reviews: complete upstream text and ratings')
assert source_reviews and all(review['source_id'] == review_source_id and review['id'].startswith('source_review_') for review in source_reviews)
assert len({review['id'] for review in source_reviews}) == len(source_reviews)

# Artist snapshots replace complete owned collections; playback stays lazy.
artist_catalog = capture('artist-catalog', '/artists/' + artist_id + '/catalog', 'Approved artist: complete catalog snapshot with imported fallback')
raw_catalog = capture('raw-artist-catalog', '/artists/' + raw_artist + '/catalog', 'Raw artist: the same complete snapshot contract')
expected_catalog_fields = {'artist', 'years', 'shows', 'tours', 'eras', 'imported_venues',
                           'imported_songs', 'venue_entries', 'song_entries', 'artist_venues', 'venues', 'songs'}
assert set(artist_catalog) == set(raw_catalog) == expected_catalog_fields
for field, name in [('artist', 'artist'), ('years', 'years'), ('shows', 'artist-shows'),
                    ('venue_entries', 'artist-venues'), ('song_entries', 'artist-songs'),
                    ('imported_venues', 'imported-venues'), ('imported_songs', 'imported-songs')]:
    assert artist_catalog[field] == json.loads((out / (name + '.json')).read_text())
assert raw_catalog['song_entries'] == raw_songs and raw_catalog['imported_songs'] == raw_song_inventory
assert raw_catalog['artist_venues'] == raw_catalog['venues'] == raw_catalog['songs'] == []
assert all('sources' not in show for body in (artist_catalog, raw_catalog) for show in body['shows'])
for name, body in [('artist-catalog', artist_catalog), ('raw-artist-catalog', raw_catalog)]:
    validator = next(line.split(': ', 1)[1] for line in (out / (name + '.headers')).read_text().splitlines() if line.lower().startswith('etag:'))
    response_code = subprocess.check_output(['curl', '--silent', '--show-error', '-o', '/dev/null',
                    '-w', '%{http_code}', '-H', 'If-None-Match: ' + validator,
                    'http://127.0.0.1:43823/api/v4/artists/' + body['artist']['id'] + '/catalog'], text=True)
    assert response_code == '304'

# Saved references preserve input order while returned entities are deduplicated patches.
references = [
    {'kind': 'artist', 'scheme': 'legacy_slug', 'id': 'grateful-dead'},
    {'kind': 'show', 'scheme': 'v4', 'id': matched['show_id']},
    {'kind': 'source', 'scheme': 'v4', 'id': file['source_id']},
    {'kind': 'source_set', 'scheme': 'v4', 'id': file['source_set_id']},
    {'kind': 'track', 'scheme': 'legacy_uuid', 'id': samples['matched']},
    {'kind': 'track', 'scheme': 'v4', 'id': typed('track', samples['matched'])},
    {'kind': 'track', 'scheme': 'v4', 'id': typed('track', samples['matched'])},
    {'kind': 'year', 'scheme': 'v4', 'id': year['id']},
    {'kind': 'tour', 'scheme': 'legacy_slug', 'id': phish_tour['slug'], 'artist_id': phish_id},
    {'kind': 'imported_venue', 'scheme': 'v4', 'id': imported_id},
    {'kind': 'imported_song', 'scheme': 'legacy_uuid', 'id': '73a1f1ec-0e2c-2b68-839f-946193fb3565'},
    {'kind': 'setlist_show', 'scheme': 'legacy_integer', 'id': query("SELECT ss.id FROM setlist_shows ss JOIN artists a ON a.id=ss.artist_id WHERE a.uuid='" + artist_uuid + "' ORDER BY ss.id LIMIT 1"), 'artist_id': artist_id},
    {'kind': 'source_review', 'scheme': 'v4', 'id': source_reviews[0]['id']},
    {'kind': 'venue', 'scheme': 'v4', 'id': venue_id},
    {'kind': 'song', 'scheme': 'v4', 'id': song_id},
    {'kind': 'artist_venue', 'scheme': 'v4', 'id': artist_venue['id']},
    {'kind': 'show', 'scheme': 'v4', 'id': 'show_00000000000000000000000000'},
    {'kind': 'track', 'scheme': 'v4', 'id': typed('track', samples['partial'])},
]
merged_venue = query('SELECT id FROM norm.venues WHERE merged_into IS NOT NULL ORDER BY id LIMIT 1')
if merged_venue:
    references.append({'kind': 'venue', 'scheme': 'v4', 'id': merged_venue})
retired_id = query('SELECT id FROM norm.public_ids WHERE retired_at IS NOT NULL ORDER BY id LIMIT 1')
if retired_id:
    references.append({'kind': retired_id.split('_')[0], 'scheme': 'v4', 'id': retired_id})
resolver_request = {'references': references}
(out / 'resolver-request.json').write_text(json.dumps(resolver_request, indent=2) + '\n')
elapsed = subprocess.check_output(['curl', '--fail', '--silent', '--show-error', '--max-time', '45',
            '-D', str(out / 'resolver.headers'), '-H', 'Content-Type: application/json',
            '--data-binary', '@' + str(out / 'resolver-request.json'),
            'http://127.0.0.1:43823/api/v4/catalog/resolve', '-o', str(out / 'resolver.json'),
            '--write-out', '%{time_total}'], text=True)
timings['resolver'] = float(elapsed)
resolver = json.loads((out / 'resolver.json').read_text())
assert len(resolver['references']) == len(references)
for original, resolved in zip(references, resolver['references']):
    assert all(resolved[key] == value for key, value in original.items())
assert resolver['references'][5] == resolver['references'][6]
assert resolver['references'][4]['target'] == resolver['references'][5]['target']
assert resolver['references'][16]['availability'] == 'unavailable'
if merged_venue:
    assert next(result for result in resolver['references'] if result['id'] == merged_venue)['availability'] == 'redirected'
if retired_id:
    assert resolver['references'][-1]['availability'] == 'retired'
for collection in resolver['entities'].values():
    assert len({entity['id'] for entity in collection}) == len(collection)
assert all('sources' not in show for show in resolver['entities']['shows'])
assert all('sets' not in source and 'links' not in source for source in resolver['entities']['sources'])
assert all('tracks' not in group for group in resolver['entities']['source_sets'])
assert all('normalization' in track for track in resolver['entities']['tracks'])
resolver_headers = (out / 'resolver.headers').read_text().lower()
assert 'cache-control: no-store' in resolver_headers and 'etag:' not in resolver_headers
requests += ['### Resolve saved typed and legacy references (upsert-only patches, no-store)',
             'POST {{base}}/api/v4/catalog/resolve', 'Content-Type: application/json', '',
             json.dumps(resolver_request, indent=2), '']

# Checked-in samples are selected excerpts; .output retains the complete responses.
review = Path('docs/plans/active/2026-10-29-v4-api/review/responses')
review.mkdir(parents=True, exist_ok=True)
def save_example(name, body):
    (review / (name + '.json')).write_text(json.dumps(body, indent=2) + '\n')

def detail_excerpt(body):
    result = dict(body)
    if 'shows' in body:
        result['shows'] = body['shows'][:1]
    if 'tracks' in body:
        result['tracks'] = body['tracks'][:1]
    return result

for name in ('shared-artist-venue', 'shared-venue',
             'disabled-imported-venue', 'song', 'song-tracks',
             'imported-song', 'user-song', 'user-imported-venue'):
    if name.startswith('shared-') and not shared_id:
        continue
    body = json.loads((out / (name + '.json')).read_text())
    save_example(name + '-detail-excerpt', detail_excerpt(body))
for name, body in [('artist-catalog', artist_catalog), ('raw-artist-catalog', raw_catalog)]:
    excerpt = {key: value[:1] if isinstance(value, list) else value for key, value in body.items()}
    save_example(name + '-excerpt', excerpt)
resolver_excerpt = {'references': resolver['references'],
                    'entities': {kind: entities[:1] for kind, entities in resolver['entities'].items()}}
save_example('resolver-excerpt', resolver_excerpt)
for name in ['artist-anniversary', 'artist-recently-added']:
    save_example(name + '-excerpt', json.loads((out / (name + '.json')).read_text())[:2])
save_example('phish-tours-excerpt', phish_tours[:2])
save_example('phish-tour-detail-excerpt', detail_excerpt(tour_detail))
save_example('phish-eras', phish_eras)
save_example('source-reviews-excerpt', source_reviews[:1])
save_example('zero-match-song', zero_song)
save_example('imported-song-metadata-excerpt', metadata(imported_song))
save_example('song-track', next(track for track in song_tracks if track['id'] == 'track_2n09qd0gcm6jye7trwhpa1rys7'))

Path('docs/plans/active/2026-10-29-v4-api/review/requests.http').write_text('\n'.join(requests))
(out / 'scenarios.json').write_text(json.dumps(scenarios, indent=2) + '\n')
(out / 'request-timings.json').write_text(json.dumps(timings, indent=2) + '\n')
print('Captured real browse, venue, source, show and track responses and headers in', out)
for row in scenarios:
    body = json.loads((out / (row['name'] + '-track.json')).read_text())
    print(row['name'], row['artist'], body['normalization']['state'], row['track_id'])
PY
