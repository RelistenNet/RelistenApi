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
scenarios = []
# Walk the same typed references a client uses; no legacy lookup parameters.
artist_uuid = query("SELECT a.uuid FROM artists a JOIN sources src ON src.artist_id=a.id JOIN source_tracks st ON st.source_id=src.id WHERE st.uuid='790f8388-13ca-ea8f-5bf0-61e4cc13ac0d'")
artist_id = typed('artist', artist_uuid)
browse = [('artists', '/artists'), ('artist', '/artists/' + artist_id),
          ('years', '/artists/' + artist_id + '/years'),
          ('year-by-number', '/artists/' + artist_id + '/years/by-number/1985')]
for name, path in browse:
    subprocess.run(['curl', '--fail', '--silent', '--show-error', '--max-time', '45', '-D', str(out / (name + '.headers')),
                    'http://127.0.0.1:43823/api/v4' + path, '-o', str(out / (name + '.json'))], check=True)
    requests += ['### Browse: ' + name, 'GET {{base}}/api/v4' + path, '']
year = json.loads((out / 'year-by-number.json').read_text())
for name, path in [('year-by-id', '/artists/' + artist_id + '/years/' + year['id']),
                   ('artist-shows', '/artists/' + artist_id + '/shows')]:
    subprocess.run(['curl', '--fail', '--silent', '--show-error', '--max-time', '45', '-D', str(out / (name + '.headers')),
                    'http://127.0.0.1:43823/api/v4' + path, '-o', str(out / (name + '.json'))], check=True)
    requests += ['### Browse: ' + name, 'GET {{base}}/api/v4' + path, '']
for name, raw in samples.items():
    row = json.loads(query(f'''SELECT row_to_json(x) FROM (SELECT st.title,a.name artist,sh.uuid show_uuid
    FROM source_tracks st JOIN sources src ON src.id=st.source_id JOIN shows sh ON sh.id=src.show_id
    JOIN artists a ON a.id=src.artist_id WHERE st.uuid='{raw}'::uuid AND NOT st.is_orphaned) x'''))
    row.update(name=name, track_id=typed('track', raw), show_id=typed('show', row['show_uuid']))
    scenarios.append(row)
    requests += [f'### {name}: {row["artist"]} — {row["title"]}', f'GET {{{{base}}}}/api/v4/source-tracks/{row["track_id"]}', '',
                 f'### Full show for {name}', f'GET {{{{base}}}}/api/v4/shows/{row["show_id"]}', '']
    for kind, path in [('track', '/source-tracks/' + row['track_id']), ('show', '/shows/' + row['show_id'])]:
        subprocess.run(['curl', '--fail', '--silent', '--show-error', '--max-time', '45', '-D', str(out / f'{name}-{kind}.headers'),
                        'http://127.0.0.1:43823/api/v4' + path, '-o', str(out / f'{name}-{kind}.json')], check=True)
matched = next(row for row in scenarios if row['name'] == 'matched')
etag = next(line.split(': ', 1)[1] for line in (out / 'matched-track.headers').read_text().splitlines() if line.lower().startswith('etag:'))
requests += ['### Revalidate captured matched track (304 unless data changed)',
             'GET {{base}}/api/v4/source-tracks/' + matched['track_id'], 'If-None-Match: ' + etag, '']
historical = typed('show', '3bed435e-8cd2-764e-3a30-764b82e01298')
requests += ['### Venue name history: The Crown on 1998-11-14', f'GET {{{{base}}}}/api/v4/shows/{historical}', '',
             '### Wrong resource kind (422)', f'GET {{{{base}}}}/api/v4/source-tracks/{historical}', '',
             '### Valid unknown identity (404)', 'GET {{base}}/api/v4/shows/show_00000000000000000000000000', '']
kaiser = 'show_127p0pfc1sgb4p9hdf4agq5dch'
requests += ['### Oakland venue: Henry J. Kaiser name periods on 1985-02-18',
             'GET {{base}}/api/v4/shows/' + kaiser, '']
subprocess.run(['curl', '--fail', '--silent', '--show-error', '--max-time', '45', '-D', str(out / 'kaiser-show.headers'),
                'http://127.0.0.1:43823/api/v4/shows/' + kaiser, '-o', str(out / 'kaiser-show.json')], check=True)
subprocess.run(['curl', '--fail', '--silent', '--show-error', '--max-time', '45', '-D', str(out / 'historical-show.headers'),
                'http://127.0.0.1:43823/api/v4/shows/' + historical, '-o', str(out / 'historical-show.json')], check=True)
Path('docs/plans/active/2026-10-29-v4-api/review/requests.http').write_text('\n'.join(requests))
(out / 'scenarios.json').write_text(json.dumps(scenarios, indent=2) + '\n')
print('Captured real track/show responses and headers in', out)
for row in scenarios:
    body = json.loads((out / (row['name'] + '-track.json')).read_text())
    print(row['name'], row['artist'], body['normalization']['state'], row['track_id'])
PY
