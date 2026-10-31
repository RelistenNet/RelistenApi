#!/usr/bin/env python3
"""Compare two local API builds on the same static catalog using GET requests only."""

import argparse
import json
from pathlib import Path
import statistics
import time
import urllib.request


parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("baseline_url", help="Original build, e.g. http://127.0.0.1:43824")
parser.add_argument("candidate_url", help="Adapter build, e.g. http://127.0.0.1:43823")
parser.add_argument("--samples", type=int, default=15)
args = parser.parse_args()
if args.samples < 1:
    parser.error("--samples must be positive")

output = Path(__file__).parent / ".output" / "legacy-recordings"
output.mkdir(parents=True, exist_ok=True)
cases = [
    ("large", "grateful-dead", "1983-10-15", "bc2acb62-6b75-7a1e-7b41-842e0aaf4140", "show_5w5b5p4tvnf8f7pgc45r5ayga0"),
    ("partial", "grateful-dead", "1970-05-15", "c7ef7038-1510-178a-9d33-5b09940ca27c", "show_67xxr3g58g2y59tctv16a0s8kw"),
    ("raw", "spafford", "2026-09-15", "766d2473-0616-8946-b59d-4d1b568d93a4", "show_3pdmj761gph53bb7ad3db8v4x4"),
]
report = []
for label, artist, date, uuid, typed_id in cases:
    paths = [
        ("v2", f"/api/v2/artists/{artist}/shows/{date}"),
        ("v3", f"/api/v3/shows/{uuid}"),
        ("v4", f"/api/v4/shows/{typed_id}"),
    ]
    for version, path in paths:
        samples = [[], []]
        bodies = [None, None]
        headers = [None, None]
        for iteration in range(4 + args.samples):
            # Alternate order to reduce bias from background machine activity.
            for index in ([0, 1] if iteration % 2 == 0 else [1, 0]):
                base = [args.baseline_url, args.candidate_url][index]
                started = time.perf_counter()
                with urllib.request.urlopen(base.rstrip("/") + path) as response:
                    bodies[index] = response.read()
                    headers[index] = {
                        name: response.headers.get(name)
                        for name in ["Content-Type", "Cache-Control", "ETag"]
                    }
                if iteration >= 4:
                    samples[index].append((time.perf_counter() - started) * 1000)
        name = f"{label}-{version}"
        payloads = [json.loads(body) for body in bodies]
        for side, payload in zip(["baseline", "candidate"], payloads):
            (output / f"{name}-{side}.json").write_text(json.dumps(payload, indent=2) + "\n")
        item = {
            "name": name,
            "path": path,
            "json_equal": payloads[0] == payloads[1],
            "headers_equal": headers[0] == headers[1],
            "bytes": [len(body) for body in bodies],
            "median_ms": [round(statistics.median(values), 2) for values in samples],
            "samples_ms": samples,
        }
        report.append(item)
        print(json.dumps({key: value for key, value in item.items() if key != "samples_ms"}), flush=True)
(output / "comparison.json").write_text(json.dumps({
    "baseline_url": args.baseline_url,
    "candidate_url": args.candidate_url,
    "warmups": 4,
    "samples": args.samples,
    "results": report,
}, indent=2) + "\n")
raise SystemExit(0 if all(item["json_equal"] and item["headers_equal"] for item in report) else 1)
