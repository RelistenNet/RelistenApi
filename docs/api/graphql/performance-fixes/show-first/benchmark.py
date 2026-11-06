#!/usr/bin/env python3
"""Read-only show-first qualification against an already-running local API."""

import argparse
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
REVIEW = HERE.parents[1] / "review"
spec = importlib.util.spec_from_file_location("catalog_benchmark", REVIEW / "benchmark.py")
bench = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bench)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--application-commit", required=True)
    parser.add_argument("--url", default="http://127.0.0.1:3823")
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--compare", type=Path)
    args = parser.parse_args()
    if args.runs < 2:
        parser.error("Use at least two requests: first and warm")

    result = {
        "applicationCommit": args.application_commit,
        "checkoutCommit": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "schemaMigration": 20,
        "policy": f"Existing real local catalog; {args.runs} serial requests per case, first separate. GraphQL-Cost reporting enabled. No concurrent tests or SQL profiling. Search excluded.",
        "graph": [],
        "rest": [],
    }
    previous = json.loads(args.compare.read_text()) if args.compare else None

    def record(section, name, path, payload=None):
        row, data = bench.measure(name, lambda: bench.request(args.url, path, payload), args.runs)
        row["path"] = path
        if payload:
            row["request"] = payload
        if previous:
            old = next(r for r in previous[section] if r["name"] == name)
            row["samePayload"] = row["successful"] and {
                s["payloadHash"] for s in row["samples"]
            } == {s["payloadHash"] for s in old["samples"]}
        result[section].append(row)
        args.output.write_text(json.dumps(result, indent=2) + "\n")
        if not row["successful"]:
            raise RuntimeError(f"{name} failed: {row['samples'][-1]}")
        if previous and not row["samePayload"]:
            raise RuntimeError(f"{name} payload or operation cost changed")
        return data

    record("graph", "exact-dashboard", "/api/v4/graphql", {
        "query": (HERE.parent / "dashboard/query.graphql").read_text()
    })
    rows = (REVIEW / "queries/song-history-rows.graphql").read_text()
    cases = [
        ("scarlet", "song_62mhvjyd"),
        ("drums", "song_pxnavq74"),
        ("rare-darling-corey", "song_j5y7dtxx"),
        ("rare-golden-road", "song_6k6e90sr"),
        ("rare-phish-big-pimpin", "song_095gm0b5"),
        ("sparse-viola", "song_j6736ex2"),
        ("old-cryptical", "song_arn8bnn9"),
        ("old-day-job", "song_90fbxy8a"),
    ]
    for name, song in cases:
        variables = {"song": song, "first": 10}
        data = record("graph", name, "/api/v4/graphql", {"query": rows, "variables": variables})
        if name in {"scarlet", "drums", "rare-darling-corey", "old-cryptical"}:
            cursor = data["data"]["song"]["shows"]["pageInfo"]["endCursor"]
            if cursor:
                record("graph", name + "-next", "/api/v4/graphql", {
                    "query": rows, "variables": variables | {"after": cursor}
                })
    record("graph", "scarlet-count", "/api/v4/graphql", {
        "query": rows, "variables": {"song": "song_62mhvjyd", "first": 10, "count": True}
    })
    prior = json.loads((HERE.parent / "dashboard/rest.json").read_text())
    for case in prior["results"]:
        record("rest", case["name"], case["path"])
    for name, song in cases[2:]:
        record("rest", name, "/api/v4/songs/" + song)
    print("All responses successful" + ("; payloads and operation costs unchanged" if previous else ""))


if __name__ == "__main__":
    main()
