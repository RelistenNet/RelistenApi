#!/usr/bin/env python3
"""Read-only real-catalog checks for the master contract integration."""

import argparse
import importlib.util
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("catalog_benchmark", HERE.parent / "review/benchmark.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application-commit", required=True)
    parser.add_argument("--url", default="http://127.0.0.1:3823")
    parser.add_argument("--output", type=Path, default=HERE / "verification.json")
    args = parser.parse_args()
    result = {
        "applicationCommit": args.application_commit,
        "masterCommit": "24b185192",
        "policy": "Existing real local catalog; serial read-only HTTP after tests, first dashboard sample separate from four warm samples. Cost reporting enabled. Fresh contract verification; old artist payload hashes intentionally do not apply.",
        "checks": [],
    }

    def read(path, payload=None):
        sample, data = bench.request(args.url, path, payload)
        if not bench.succeeded(sample):
            raise RuntimeError(sample)
        return sample, data

    query = (HERE.parent / "performance-fixes/dashboard/query.graphql").read_text()
    result["dashboard"], _ = bench.measure(
        "exact-dashboard", lambda: read("/api/v4/graphql", {"query": query}), 5
    )
    artist = "artist_3qmp7zjbg1rpebhtyfy43096vj"
    sample, graph = read("/api/v4/graphql", {"query": """
        query($artist: ID!) {
          artist(id: $artist) { features upstreamSources { provider upstreamIdentifier } }
        }
        """, "variables": {"artist": artist}})
    rest_sample, rest = read("/api/v4/artists/" + artist)
    projected = graph["data"]["artist"]
    assert isinstance(projected["features"], str) and projected["features"] == rest["features"]
    assert 0 <= int(projected["features"]) < 2**64
    assert [s["upstreamIdentifier"] for s in projected["upstreamSources"]] == [
        s["upstream_identifier"] for s in rest["upstream_sources"]
    ]
    result["checks"].append({"name": "compact-artist", "graph": sample, "rest": rest_sample,
                             "features": projected["features"], "providers": projected["upstreamSources"]})

    for collection, resource, imported in [("songs", "song", "importedSong"),
                                            ("venues", "artistVenue", "importedVenue")]:
        query = f"""query($artist: ID!) {{ artist(id: $artist) {{ {collection}(first: 100) {{
          nodes {{ {resource} {{ id }} {imported} {{ id }} }} totalCount
        }} }} }}"""
        sample, graph = read("/api/v4/graphql", {"query": query, "variables": {"artist": artist}})
        rest_sample, rest = read("/api/v4/artists/" + artist + "/" + collection)
        page = graph["data"]["artist"][collection]
        assert page["totalCount"] == len(rest)
        assert all(row[imported] is None for row in page["nodes"])
        rest_resource = "song" if collection == "songs" else "artist_venue"
        assert [row[resource]["id"] for row in page["nodes"]] == [row[rest_resource]["id"] for row in rest[:100]]
        result["checks"].append({"name": "approved-artist-" + collection,
                                 "totalCount": len(rest), "graph": sample, "rest": rest_sample})

    sample, graph = read("/api/v4/graphql", {"query": """
        { venues(first: 20) { nodes { id name globalShowCount } totalCount } }
        """})
    rest_sample, rest = read("/api/v4/venues")
    page = graph["data"]["venues"]
    assert page["totalCount"] == len(rest)
    assert page["nodes"] == [{"id": row["id"], "name": row["name"],
                              "globalShowCount": row["global_show_count"]} for row in rest[:20]]
    result["checks"].append({"name": "global-venues", "totalCount": len(rest),
                             "zeroShowVenues": sum(row["global_show_count"] == 0 for row in rest),
                             "graph": sample, "rest": rest_sample})
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print("All master contract checks passed")


if __name__ == "__main__":
    main()
