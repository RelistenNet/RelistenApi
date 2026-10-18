#!/usr/bin/env python3
"""Retain a bounded, hashed GeoNames download for offline shadow geography.

The files contain general public geography. No Relisten values are sent upstream.
Existing outputs are refused so a refresh cannot change a running method's inputs.
"""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import urllib.request

BASE = 'https://download.geonames.org/export/dump/'
FILES = {'cities500.zip': 32 * 1024 * 1024,
         'admin1CodesASCII.txt': 2 * 1024 * 1024,
         'readme.txt': 256 * 1024}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    inventory = []
    for name, limit in FILES.items():
        url = BASE + name
        request = urllib.request.Request(url, headers={'User-Agent': 'RelistenNormalization/1.0'})
        with urllib.request.urlopen(request, timeout=60) as response:
            body = response.read(limit + 1)
            if len(body) > limit:
                raise ValueError(f'{name} exceeds its download bound')
            modified = response.headers.get('Last-Modified')
        (args.output / name).write_bytes(body)
        inventory.append(dict(path=name, url=url, bytes=len(body),
                              sha256=hashlib.sha256(body).hexdigest(), lastModified=modified))
    manifest = dict(version='shadow-geonames-cities500-v1',
                    capturedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                    provider='GeoNames', sourceUrl=BASE,
                    license='https://creativecommons.org/licenses/by/4.0/',
                    attribution='Geographic data from GeoNames, CC BY 4.0; original downloads retained unchanged.',
                    scope='Cities500 and administrative divisions; locality references, not physical venue or historical boundary evidence.',
                    files=inventory)
    path = args.output / 'manifest.json'
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n')
    print(f'{path}#sha256={hashlib.sha256(path.read_bytes()).hexdigest()}')


if __name__ == '__main__':
    main()
