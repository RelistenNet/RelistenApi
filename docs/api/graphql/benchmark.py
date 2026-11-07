#!/usr/bin/env python3
"""Sample representative read-only catalog requests against an existing API.

Writes results to --output or stdout; it never changes catalog data or schemas.
The first sample is separate from warm observations. These are not load-test percentiles.
"""

import argparse
import hashlib
import json
from pathlib import Path
import statistics
import sys
import time
import urllib.error
import urllib.request

HERE = Path(__file__).resolve().parent
GD = "artist_3qmp7zjbg1rpebhtyfy43096vj"
SCARLET = "song_62mhvjyd"
DRUMS = "song_pxnavq74"


def request(base, path, payload=None):
    headers = {"Content-Type": "application/json"}
    if payload is not None:
        headers["GraphQL-Cost"] = "report"
    req = urllib.request.Request(
        base + path,
        data=None if payload is None else json.dumps(payload).encode(),
        headers=headers,
    )
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=25) as response:
            body, status = response.read(), response.status
    except urllib.error.HTTPError as error:
        body, status = error.read(), error.code
    except (TimeoutError, OSError) as error:
        return {
            "ms": (time.perf_counter() - started) * 1000,
            "error": type(error).__name__,
        }
    elapsed = (time.perf_counter() - started) * 1000
    result = {"ms": elapsed, "status": status, "bytes": len(body)}
    try:
        data = json.loads(body)
    except (ValueError, UnicodeDecodeError):
        return result | {"error": "non_json_response"}
    if isinstance(data, dict) and data.get("errors"):
        result["errors"] = data["errors"]
    if status >= 400:
        result["errorBody"] = data
    if isinstance(data, dict) and "extensions" in data:
        result["cost"] = data["extensions"].get("operationCost")
    result["payloadHash"] = hashlib.sha256(
        json.dumps(data, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return result


def succeeded(sample):
    return sample.get("status") == 200 and not (
        sample.get("error") or sample.get("errors")
    )


def summarize(samples):
    successful = all(succeeded(sample) for sample in samples)
    # A timeout/error's short response time must not look like fast successful work.
    warm = [sample["ms"] for sample in samples[1:]] if successful else []
    return {
        "samples": samples,
        "successful": successful,
        "warmMedianMs": statistics.median(warm) if warm else None,
        "warmRangeMs": [min(warm), max(warm)] if warm else None,
    }


def measure(name, call, runs):
    samples = []
    for _ in range(runs):
        sample = call()
        samples.append(sample)
        if not succeeded(sample):
            break
    result = {
        "name": name,
        "firstMs": samples[0]["ms"],
        **summarize(samples),
    }
    print(
        json.dumps(
            {k: result[k] for k in ["name", "firstMs", "warmMedianMs", "warmRangeMs"]}
        ),
        flush=True,
        file=sys.stderr,
    )
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:3823")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--variables", type=Path, help="JSON overrides for the dashboard's catalog IDs"
    )
    args = parser.parse_args()
    if args.runs < 2:
        parser.error("Use at least two requests: first and warm")

    variables = {
        "artist": GD,
        "scarlet": SCARLET,
        "drums": DRUMS,
        "venue": "venue_yn1268r4",
        "recording": "source_0d6jj9kcqwayrky1qpbwb2m6a1",
    }
    if args.variables:
        variables.update(json.loads(args.variables.read_text()))
    artist, song, source = (
        variables["artist"], variables["scarlet"], variables["recording"]
    )
    graph_cases = [
        (
            "dashboard",
            (HERE / "operations/dashboard.graphql").read_text(),
            variables,
        ),
        ("shallow-source", "query($id:ID!){source(id:$id){id}}", {"id": source}),
        (
            "shallow-resolved-source",
            "query($id:String!){resolve(references:[{kind:SOURCE,id:$id}])"
            "{target{...on Source{id}}}}",
            {"id": source},
        ),
        (
            "wide-show-page",
            "query($id:ID!){artist(id:$id){shows(first:500)"
            "{nodes{id}pageInfo{endCursor hasNextPage}}}}",
            {"id": artist},
        ),
        (
            "ranking-headers",
            "query($id:ID!){artistRankings(artistIds:[$id],limit:500){artistId}}",
            {"id": artist},
        ),
    ]
    cases = [
        (name, "/api/v4/graphql", {"query": query, "variables": values})
        for name, query, values in graph_cases
    ] + [
        ("rest-artist-songs", "/api/v4/artists/" + artist + "/songs", None),
        ("rest-song-history", "/api/v4/songs/" + song, None),
        ("rest-source", "/api/v4/sources/" + source, None),
        ("rest-recent-performed", "/api/v4/shows/recently-performed?shows=25", None),
        ("rest-recent-added", "/api/v4/shows/recently-added?limit=200", None),
    ]
    result = {
        "url": args.url,
        "samplePolicy": (
            f"{args.runs} serial requests per case; first separate from warm samples"
        ),
        "cases": [],
    }
    for name, path, payload in cases:
        row = measure(name, lambda: request(args.url, path, payload), args.runs)
        result["cases"].append(row)
    text = json.dumps(result, indent=2) + "\n"
    if args.output:
        args.output.write_text(text)
    else:
        print(text, end="")
    if not all(row["successful"] for row in result["cases"]):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
