#!/usr/bin/env python3
"""Capture real C2 HTTP responses using the normal Development native sign-in flow.

Only the local persona substitutes for Google. Auth, tokens, account policies,
EF persistence, library revisions, and replay are the actual service behavior.
"""
import base64
import json
import html
import re
import tempfile
from urllib.parse import urljoin
from pathlib import Path
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
OUT = Path(__file__).resolve().parent / '.output'
AUTH = 'https://auth.relisten.localhost:5443'
ACCOUNTS = 'https://accounts.relisten.localhost:5443'
OUT.mkdir(exist_ok=True, mode=0o700)


def typed_id(prefix):
    value = (int(time.time() * 1000) << 80) | (uuid.uuid4().int & ((1 << 80)-1))
    value = (value & ~(15 << 76)) | (7 << 76)
    alphabet = '0123456789abcdefghjkmnpqrstvwxyz'
    return prefix + '_' + ''.join(alphabet[(value >> (5*i)) & 31] for i in reversed(range(26)))


def sign_in(persona='google-alice'):
    return json.loads(subprocess.check_output([
        str(ROOT / 'RelistenUserService/SmokeTests/native-sign-in.sh'), 'c2-review', persona
    ], text=True))


def request(path, *, token=None, payload=None, method=None, expected=200, capture=None):
    # Keep credentials in stdin/curl config, out of process arguments and response captures.
    config = f'url = "{ACCOUNTS}{path}"\nsilent\nshow-error\n'
    if token:
        config += f'header = "Authorization: Bearer {token}"\n'
    args = ['curl', '--config', '-', '--write-out', '\n%{http_code}']
    if payload is not None:
        body = OUT / 'request.json'
        body.write_text(json.dumps(payload))
        args += ['--header', 'Content-Type: application/json', '--data-binary', '@'+str(body)]
    if method:
        args += ['--request', method]
    raw = subprocess.check_output(args, input=config, text=True)
    body, http_code = raw.rsplit('\n', 1)
    assert int(http_code) == expected, f'{path}: expected {expected}, received {http_code}: {body}'
    result = json.loads(body) if body else None
    if capture:
        (OUT / f'{capture}.json').write_text(json.dumps(result, indent=2)+'\n')
    return result


if len(sys.argv) == 2:
    print(json.dumps(request(sys.argv[1], token=(OUT / 'access-token').read_text()), indent=2))
    sys.exit(0)

tokens = sign_in()
access = tokens['access_token']
# The ID token is readable locally; access tokens remain signed/encrypted protocol values.
claims = json.loads(base64.urlsafe_b64decode(tokens['id_token'].split('.')[1] + '==='))
profile = request('/v1/me', token=access, capture='profile')
assert profile['user_id'] == claims['sub']
assert profile['native_session_id'].startswith('session_')
assert profile['username'] == 'c2_listener'  # provider link must attach to the migrated account
# Reset only this script's two review memberships through the public API, so
# repeated captures still demonstrate additions and a nonempty changes page.
request('/v1/library/favorite-mutations:batch', token=access, payload=dict(
    contract_version=1, mutations=[dict(mutation_id=typed_id('mutation'),
        target=dict(kind=kind,id=target), desired_state='not_favorite')
        for kind,target in [('venue','venue_fp4h900f'),('song','song_62mhvjyd')]]))
before = request('/v1/library/snapshot', token=access, capture='snapshot-before')
command = dict(contract_version=1, command_id=typed_id('command'),
               expected_username_version=profile['username_version'], username=profile['username'])
updated = request('/v1/me', token=access, method='PATCH', payload=command, capture='username')
assert request('/v1/me', token=access, method='PATCH', payload=command) == updated

batch = dict(contract_version=1, mutations=[
    dict(mutation_id=typed_id('mutation'), favorite_id=typed_id('favorite'),
         target=dict(kind=kind, id=target), desired_state='favorite')
    for kind, target in [('venue', 'venue_fp4h900f'), ('song', 'song_62mhvjyd')]
])
added = request('/v1/library/favorite-mutations:batch', token=access, payload=batch, capture='favorite-results')
assert request('/v1/library/favorite-mutations:batch', token=access, payload=batch) == added
snapshot = request('/v1/library/snapshot', token=access, capture='snapshot')
changes = request('/v1/library/changes?after='+before['next_cursor'], token=access, capture='changes')
assert snapshot['library_revision'] == before['library_revision'] + len(batch['mutations'])
assert changes['library_revision'] == snapshot['library_revision']
assert [
    (change['revision'], change['change_type'], change['favorite_id'], change['target'])
    for change in changes['changes']
] == [
    (before['library_revision'] + index + 1, 'favorite_added',
     result['canonical_favorite_id'], result['target'])
    for index, result in enumerate(added['results'])
]
assert all(result['changed'] for result in added['results'])
assert all(any(f['target'] == m['target'] for f in snapshot['favorites']) for m in batch['mutations'])
changed = json.loads(json.dumps(batch))
changed['mutations'][0]['target']['id'] = 'venue_00000000'
conflict = request('/v1/library/favorite-mutations:batch', token=access, payload=changed, expected=409, capture='conflict')
assert conflict['conflicting_mutation_ids'] == [batch['mutations'][0]['mutation_id']]
wrong_target = dict(contract_version=1, mutations=[dict(
    mutation_id=typed_id('mutation'), favorite_id=typed_id('favorite'),
    target=dict(kind='venue', id='song_62mhvjyd'), desired_state='favorite')])
request('/v1/library/favorite-mutations:batch', token=access, payload=wrong_target, expected=422, capture='invalid-target')
request('/v1/me', token=access, method='PATCH', payload=dict(command,
    command_id='019a0000-0000-7000-8000-000000000009'), expected=422, capture='invalid-command')
request('/v1/library/changes?after=old-format-cursor', token=access, expected=410, capture='cursor-reset')
request('/v1/library-evil', token=access, expected=404)
request('/v1/library/snapshot', expected=401)

# A different provider subject must have its own account and library.
other = sign_in('google-shared')
other_profile = request('/v1/me', token=other['access_token'])
assert other_profile['user_id'] != profile['user_id']
assert request('/v1/library/snapshot', token=other['access_token'])['favorites'] == []
request('/v1/logout', token=other['access_token'], method='POST', expected=204)

# Leave Alice signed in for the review commands. The ignored token file is owner-only.
credential_path = OUT / 'access-token'
credential_path.write_text(access)
credential_path.chmod(0o600)
(OUT / 'request.json').unlink(missing_ok=True)
print(f'C2 real HTTP checks passed. Responses: {OUT}')
print(f"Migrated account: {profile['user_id']}; {len(snapshot['favorites'])} saved targets.")

# Exercise the web service exactly as the existing web relay does, without changing
# the web/mobile clients. Correlation and session cookies stay in a temporary jar.
WEB = 'https://web.relisten.localhost:5173'
with tempfile.TemporaryDirectory(prefix='relisten-c2-browser-') as temporary:
    jar = Path(temporary) / 'cookies'
    headers = Path(temporary) / 'headers'

    def browser(url, *, form=None, payload=None, csrf=None, origin=None, expected=200):
        relay = url.startswith(WEB) or url.startswith('/')
        if url.startswith('/'):
            url = WEB + url
        browser_url = url
        if relay:
            url = ACCOUNTS + url.removeprefix(WEB)
        config = f'url = "{url}"\nsilent\nshow-error\n'
        if relay:
            config += f'header = "X-Relisten-Web-Origin: {WEB}"\n'
        if csrf:
            config += f'header = "X-Relisten-CSRF: {csrf}"\n'
        if origin:
            config += f'header = "Origin: {origin}"\n'
        args = ['curl','--config','-', '--cookie',str(jar),'--cookie-jar',str(jar),
                '--dump-header',str(headers),'--write-out','\n%{http_code}']
        if form:
            for key, value in form.items():
                args += ['--data-urlencode', f'{key}={value}']
        if payload is not None:
            body_path = Path(temporary) / 'request.json'
            body_path.write_text(json.dumps(payload))
            args += ['--header','Content-Type: application/json','--data-binary','@'+str(body_path)]
        raw = subprocess.check_output(args, input=config, text=True)
        body, code = raw.rsplit('\n',1)
        assert int(code) == expected, f'Browser {url}: {code} {body}'
        response_headers = headers.read_text()
        if '/v1/' in url:
            assert 'no-store' in response_headers.lower()
        match = re.search(r'^location: (.+)$', response_headers, re.M | re.I)
        return body, urljoin(browser_url, match[1].strip()) if match else None

    _, authorize = browser('/auth/session/start?return_to=/c2-review', expected=302)
    _, persona_url = browser(authorize, expected=302)
    form_html, _ = browser(persona_url)
    return_url = html.unescape(re.search(r'name="return_url" value="([^"]+)"',form_html)[1])
    _, authorize = browser(AUTH+'/development/sign-in',
        form=dict(return_url=return_url, persona='google-alice'), origin=AUTH, expected=302)
    _, callback = browser(authorize, expected=302)
    browser(callback, expected=302)
    web_profile = json.loads(browser('/v1/me')[0])
    assert web_profile['user_id'] == profile['user_id']
    assert 'native_session_id' not in web_profile
    (OUT / 'browser-profile.json').write_text(json.dumps(web_profile, indent=2)+'\n')
    csrf = json.loads(browser('/api/user/v1/csrf')[0])['request_token']
    browser('/v1/library/favorite-mutations:batch', payload=batch, origin=WEB, expected=403)
    browser('/v1/library/favorite-mutations:batch', payload=batch, csrf=csrf,
            origin='https://wrong.example', expected=403)
    assert json.loads(browser('/v1/library/favorite-mutations:batch', payload=batch,
        csrf=csrf, origin=WEB)[0]) == added
    assert json.loads(browser('/v1/library/snapshot')[0])['favorites'] == snapshot['favorites']
    browser('/auth/session/logout', form={'logout':'1'}, csrf=csrf, origin=WEB)
    browser('/v1/me', expected=401)
print('Browser code exchange, typed session, CSRF/origin rejection, library replay, and logout passed.')
