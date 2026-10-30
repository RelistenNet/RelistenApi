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
year = json.loads((out / 'year-by-number.json').read_text())
for name, path in [('year-by-id', '/artists/' + artist_id + '/years/' + year['id']),
                   ('artist-shows', '/artists/' + artist_id + '/shows')]:
    capture(name, path, 'Browse: ' + name)

entries = json.loads((out / 'artist-venues.json').read_text())
venue_entry = next(entry for entry in entries if entry['artist_venue'] and entry['artist_venue']['venue']['id'] == 'venue_wwrsykhn')
venue_id = venue_entry['artist_venue']['venue']['id']
capture('venue', '/venues/' + venue_id, 'Global venue: canonical metadata and aliases')
capture('artist-venue', '/artists/' + artist_id + '/venues/' + venue_id, 'Artist venue: show membership and count')
venue_shows = capture('artist-venue-shows', '/artists/' + artist_id + '/venues/' + venue_id + '/shows', 'Artist venue shows')
capture('venue-shows', '/venues/' + venue_id + '/shows', 'Global venue shows across approved artists')
capture('venue-shows-filtered', '/venues/' + venue_id + '/shows?artist_id=' + artist_id, 'Global venue shows filtered to the same artist')
shared_id = query('''SELECT v.id FROM norm.venues v
JOIN norm.show_venues sv ON sv.venue_id=v.id JOIN shows sh ON sh.uuid=sv.show_uuid
JOIN features f ON f.artist_id=sh.artist_id WHERE v.merged_into IS NULL AND f.norm_catalogue
GROUP BY v.id HAVING count(DISTINCT sh.artist_id)>1 ORDER BY count(*) DESC,v.id LIMIT 1''')
if shared_id:
    shared_venue = capture('shared-venue', '/venues/' + shared_id, 'Shared venue: one place across artists')
    shared_shows = capture('shared-venue-shows', '/venues/' + shared_id + '/shows', 'Shared venue: shows across approved artists')
    assert shared_venue['global_show_count'] == len(shared_shows)
    shared_artist = next(show['artist_id'] for show in shared_shows if show['artist_id'] == artist_id)
    shared_relationship = capture('shared-artist-venue', '/artists/' + shared_artist + '/venues/' + shared_id, 'Shared venue: artist count beside global count')
    shared_artist_shows = capture('shared-artist-venue-shows', '/artists/' + shared_artist + '/venues/' + shared_id + '/shows', 'Shared venue: the artist collection')
    assert shared_relationship['artist_show_count'] == len(shared_artist_shows)
    assert shared_relationship['venue'] == shared_venue
    shared_show = capture('shared-venue-show', '/shows/' + shared_artist_shows[0]['id'], 'Shared venue: a show with the same relationship and counts')
    assert shared_show['artist_venue'] == shared_relationship
    if shared_show['year_id']:
        shared_year = capture('shared-venue-year', '/artists/' + shared_artist + '/years/' + shared_show['year_id'], 'Shared venue: year summaries keep the full artist and global counts')
        shared_year_shows = [show for show in shared_year['shows'] if show['artist_venue'] and show['artist_venue']['venue']['id'] == shared_id]
        assert shared_year_shows and all(show['artist_venue'] == shared_relationship for show in shared_year_shows)
imported_id = next(show['imported_venue']['id'] for show in venue_shows if show['imported_venue'])
capture('imported-venue', '/artists/' + artist_id + '/imported-venues/' + imported_id, 'Imported venue: original observations and exact show scope')
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
capture('unmatched-imported-venue', '/artists/' + artist_id + '/imported-venues/' + raw_entry['imported_venue']['id'], 'Unmatched venue fallback in an approved artist')
disabled_track = json.loads((out / 'disabled-track.json').read_text())
disabled_entries = capture('disabled-artist-venues', '/artists/' + disabled_track['artist_id'] + '/venues', 'Unnormalized artist: imported venue browsing')
disabled_imported = next(entry['imported_venue'] for entry in disabled_entries if entry['imported_venue'])
capture('disabled-imported-venue', '/artists/' + disabled_track['artist_id'] + '/imported-venues/' + disabled_imported['id'], 'Unnormalized artist: imported venue and shows')
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
Path('docs/plans/active/2026-10-29-v4-api/review/requests.http').write_text('\n'.join(requests))
(out / 'scenarios.json').write_text(json.dumps(scenarios, indent=2) + '\n')
(out / 'request-timings.json').write_text(json.dumps(timings, indent=2) + '\n')
print('Captured real browse, venue, source, show and track responses and headers in', out)
for row in scenarios:
    body = json.loads((out / (row['name'] + '-track.json')).read_text())
    print(row['name'], row['artist'], body['normalization']['state'], row['track_id'])
PY
