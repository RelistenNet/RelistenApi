#!/usr/bin/env python3
"""Retain individual GeoNames RDF localities omitted from a frozen cities500 reference.

The output is a new immutable reference manifest. Catalog data stays local; only
explicit GeoNames IDs are requested. Matching reads retained source claims offline.
"""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request

MAX_FILE_BYTES = 128 * 1024
MAX_TOTAL_BYTES = 2 * 1024 * 1024
MIN_FREE_BYTES = 4 * 1024**3


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        raise ValueError('GeoNames RDF acquisition does not follow redirects')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--base-manifest', required=True)
    parser.add_argument('--geoname-id', required=True, type=int, action='append')
    args = parser.parse_args()
    if not 1 <= len(args.geoname_id) <= 32 or len(set(args.geoname_id)) != len(args.geoname_id):
        parser.error('Supply 1 to 32 distinct GeoNames IDs')
    if any(identity <= 0 for identity in args.geoname_id):
        parser.error('GeoNames IDs must be positive integers')
    base, separator, expected_hash = args.base_manifest.rpartition('#sha256=')
    base_path = Path(base).resolve()
    base_bytes = base_path.read_bytes()
    if not separator or hashlib.sha256(base_bytes).hexdigest() != expected_hash:
        parser.error('The base manifest differs from its supplied SHA-256')
    if json.loads(base_bytes)['version'] != 'shadow-geonames-cities500-v1':
        parser.error('The base must be a cities500 v1 manifest')
    if shutil.disk_usage(args.output.parent).free < MIN_FREE_BYTES + MAX_TOTAL_BYTES:
        raise ValueError('Locality acquisition requires 4 GiB free plus its download bound')
    args.output.mkdir(exist_ok=False)
    inventory = []
    total_bytes = 0
    opener = urllib.request.build_opener(NoRedirect)
    for identity in args.geoname_id:
        url = f'https://sws.geonames.org/{identity}/about.rdf'
        request = urllib.request.Request(url, headers={'User-Agent': 'RelistenNormalization/1.0'})
        with opener.open(request, timeout=30) as response:
            body = response.read(MAX_FILE_BYTES + 1)
            if len(body) > MAX_FILE_BYTES:
                raise ValueError('GeoNames RDF exceeds its per-record bound')
            media_type = response.headers.get_content_type()
        total_bytes += len(body)
        if total_bytes > MAX_TOTAL_BYTES:
            raise ValueError('GeoNames RDF acquisition exceeds its total bound')
        if shutil.disk_usage(args.output).free < MIN_FREE_BYTES + len(body):
            raise ValueError('Locality acquisition reached its free-space floor')
        name = f'{identity}.rdf'
        (args.output / name).write_bytes(body)
        inventory.append(dict(path=name, url=url, capturedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                              mediaType=media_type, bytes=len(body), sha256=hashlib.sha256(body).hexdigest()))
    manifest = dict(version='shadow-geonames-localities-v1',
                    baseManifest=f'{base_path}#sha256={expected_hash}',
                    attribution='GeoNames, CC BY 4.0: https://www.geonames.org/',
                    license='https://creativecommons.org/licenses/by/4.0/', files=inventory)
    path = args.output / 'manifest.json'
    manifest_bytes = (json.dumps(manifest, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    if shutil.disk_usage(args.output).free < MIN_FREE_BYTES + len(manifest_bytes):
        raise ValueError('Locality manifest would cross its free-space floor')
    path.write_bytes(manifest_bytes)
    print(f'{path}#sha256={hashlib.sha256(path.read_bytes()).hexdigest()}')


if __name__ == '__main__':
    main()
