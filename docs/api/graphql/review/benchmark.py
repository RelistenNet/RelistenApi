#!/usr/bin/env python3
"""Read-only HTTP qualification against existing local API processes.

Current API defaults to 3823; the pre-GraphQL REST baseline defaults to 3824.
No schemas, indexes, catalog rows, or remote APIs are modified by this script.
One first sample plus three serial warm samples are recorded for each case.
"""

import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
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
        }, None
    elapsed = (time.perf_counter() - started) * 1000
    result = {"ms": elapsed, "status": status, "bytes": len(body)}
    try:
        data = json.loads(body)
    except (ValueError, UnicodeDecodeError):
        return result | {"error": "non_json_response"}, None
    if isinstance(data, dict) and data.get("errors"):
        result["errors"] = data["errors"]
    if status >= 400:
        result["errorBody"] = data
    if isinstance(data, dict) and "extensions" in data:
        result["cost"] = data["extensions"].get("operationCost")
    result["payloadHash"] = hashlib.sha256(
        json.dumps(data, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()
    return result, data


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
    samples, last = [], None
    for _ in range(runs):
        sample, last = call()
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
    )
    return result, last


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--current", default="http://127.0.0.1:3823")
    parser.add_argument("--baseline", default="http://127.0.0.1:3824")
    parser.add_argument("--runs", type=int, default=4)
    parser.add_argument(
        "--current-commit", help="Commit built into the running current API"
    )
    parser.add_argument("--output", type=Path, default=HERE / "results.json")
    args = parser.parse_args()
    if args.runs < 2:
        parser.error("--runs must be at least 2 (one first and one warm sample)")
    result = {
        "currentCommit": args.current_commit,
        "checkoutCommit": subprocess.check_output(
            ["git", "rev-parse", "HEAD"], text=True
        ).strip(),
        "baselineCommit": "5087f5a39",
        "environment": "macOS ARM64, .NET10, existing local PostgreSQL catalog, serial HTTP, warmed host",
        "samplePolicy": f"first sample then {args.runs - 1} warm samples; no p95 claim",
        "graph": [],
        "rest": [],
    }
    # Use recording-backed matches from the public REST contract to select real files.
    track_sample, track_data = request(
        args.current, "/api/v4/songs/" + SCARLET + "/tracks"
    )
    if not succeeded(track_sample):
        parser.error("Cannot select fixture track IDs: " + json.dumps(track_sample))
    ids = [t["id"] for t in track_data["tracks"][:250]]
    common = {
        "artist": GD,
        "song": SCARLET,
        "venue": "venue_yn1268r4",
        "from": "1995-07-02T00:00:00Z",
        "through": "1995-07-02T23:59:59Z",
        "source": "source_0d6jj9kcqwayrky1qpbwb2m6a1",
        "q": "Oakland",
        "ids": ids,
    }
    cases = [
        ("artist-navigation", "artist-navigation", {}),
        ("artist-shows", "artist-shows", {}),
        ("artist-shows-count", "artist-shows", {"count": True}),
        ("song-metadata", "song-metadata", {}),
        ("scarlet-rows-25", "song-history-rows", {}),
        ("scarlet-rows-count-25", "song-history-rows", {"count": True}),
        ("scarlet-timings-25", "song-history-timings", {}),
        ("scarlet-playback-1", "song-history-playback", {"first": 1}),
        ("scarlet-playback-10", "song-history-playback", {"first": 10}),
        ("scarlet-playback-25", "song-history-playback", {"first": 25}),
        ("drums-rows-25", "song-history-rows", {"song": DRUMS}),
        ("drums-playback-25", "song-history-playback", {"song": DRUMS}),
        (
            "tweezer-reprise-playback-25",
            "song-history-playback",
            {"song": "song_699vmgsp"},
        ),
        ("song-source-comparison", "song-source-comparison", {}),
        ("venue-history-25", "venue-history", {}),
        (
            "venue-history-filtered",
            "venue-history-filtered",
            {"from": "1994-01-01T00:00:00Z", "through": "1995-12-31T23:59:59Z"},
        ),
        ("recording-playback", "recording-playback", {}),
        ("playlist-250", "playlist-hydration", {}),
        ("catalog-search", "catalog-search", {}),
    ]
    first_cursor = None
    for name, document, overrides in cases:
        variables = common | overrides
        query = (HERE / "queries" / (document + ".graphql")).read_text()
        row, data = measure(
            name,
            lambda q=query, v=variables: request(
                args.current, "/api/v4/graphql", {"query": q, "variables": v}
            ),
            args.runs,
        )
        row["document"] = document + ".graphql"
        row["variables"] = variables
        if row["successful"] and data and data.get("data"):
            song = data["data"].get("song")
            if name == "scarlet-rows-25":
                first_cursor = song["shows"]["pageInfo"]["endCursor"]
            if name == "scarlet-playback-25":
                row["fileCount"] = sum(
                    len(n["tracks"]["nodes"]) for n in song["shows"]["nodes"]
                )
            if name == "drums-playback-25":
                row["fileCount"] = sum(
                    len(n["tracks"]["nodes"]) for n in song["shows"]["nodes"]
                )
        result["graph"].append(row)
        args.output.write_text(json.dumps(result, indent=2))
    if first_cursor:
        q = (HERE / "queries" / "song-history-rows.graphql").read_text()
        v = common | {"after": first_cursor}
        row, _ = measure(
            "scarlet-rows-next-25",
            lambda: request(
                args.current, "/api/v4/graphql", {"query": q, "variables": v}
            ),
            args.runs,
        )
        row["document"] = "song-history-rows.graphql"
        row["variables"] = v
        result["graph"].append(row)
    paths = {
        "artist-shows": "/api/v4/artists/" + GD + "/shows",
        "recent-performed": "/api/v4/artists/"
        + GD
        + "/shows/recently-performed?shows=25",
        "recent-updated": "/api/v4/artists/" + GD + "/shows/recently-updated?shows=25",
        "top": "/api/v4/artists/" + GD + "/shows/top?limit=25",
        "anniversary": "/api/v4/artists/" + GD + "/shows/on-date?month=12&day=31",
        "artist-songs": "/api/v4/artists/" + GD + "/songs",
        "artist-venues": "/api/v4/artists/" + GD + "/venues",
        "venue-detail": "/api/v4/artists/" + GD + "/venues/venue_yn1268r4",
        "scarlet-song": "/api/v4/songs/" + SCARLET,
        "scarlet-song-top": "/api/v4/songs/" + SCARLET + "?include_tracks=top",
        "tour-search": "/api/v4/search?q=summer&artists=false&shows=false&songs=false&sources=false&venues=false",
        "song-venue-search": "/api/v4/search?q=Oakland&artist_id="
        + GD
        + "&artists=false&shows=false&sources=false&tours=false",
    }
    for name, path in paths.items():
        # Alternate baseline/current to reduce cache-order bias; first samples remain separate.
        samples = {"baseline": [], "current": []}
        hashes = {}
        for run in range(args.runs):
            for label in (
                ["baseline", "current"] if run % 2 == 0 else ["current", "baseline"]
            ):
                sample, _ = request(
                    args.baseline if label == "baseline" else args.current, path
                )
                samples[label].append(sample)
                hashes[label] = sample.get("payloadHash")
        row = {
            "name": name,
            "path": path,
        }
        for label, ss in samples.items():
            row[label] = summarize(ss)
        row["samePayload"] = (
            hashes["baseline"] == hashes["current"]
            if row["baseline"]["successful"]
            and row["current"]["successful"]
            and hashes["baseline"] is not None
            and hashes["current"] is not None
            else None
        )
        result["rest"].append(row)
        args.output.write_text(json.dumps(result, indent=2))
        print(
            json.dumps(
                {
                    "rest": name,
                    "baselineMs": row["baseline"]["warmMedianMs"],
                    "currentMs": row["current"]["warmMedianMs"],
                    "samePayload": row["samePayload"],
                }
            ),
            flush=True,
        )
    args.output.write_text(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
