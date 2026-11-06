#!/usr/bin/env python3
"""Alternate old/new API requests using the frozen before.json inputs."""

import argparse
import json
from pathlib import Path
import sys

sys.dont_write_bytecode = True
from benchmark import HERE, bench


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", default="http://127.0.0.1:3824")
    parser.add_argument("--current", default="http://127.0.0.1:3823")
    parser.add_argument("--baseline-commit", required=True)
    parser.add_argument("--current-commit", required=True)
    parser.add_argument("--runs", type=int, default=5)
    parser.add_argument("--sections", nargs="+", choices=["graph", "rest"], default=["graph", "rest"])
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.runs < 2:
        parser.error("Use at least two requests: first and warm")

    before = json.loads((HERE / "before.json").read_text())
    result = {
        "baselineApplicationCommit": args.baseline_commit,
        "currentApplicationCommit": args.current_commit,
        "schemaMigration": 20,
        "policy": f"Same real local catalog; {args.runs} requests per endpoint per case, alternating baseline/current order. First sample separate. Frozen before.json requests, including cursors and operation-cost reporting. No concurrent tests or intentional SQL work. Search excluded.",
        "graph": [],
        "rest": [],
    }
    for section in args.sections:
        for case in before[section]:
            samples = {"baseline": [], "current": []}
            for run in range(args.runs):
                for label in (["baseline", "current"] if run % 2 == 0 else ["current", "baseline"]):
                    sample, _ = bench.request(
                        args.baseline if label == "baseline" else args.current,
                        case["path"], case.get("request"),
                    )
                    samples[label].append(sample)
            row = {"name": case["name"], "path": case["path"]}
            for label, values in samples.items():
                row[label] = bench.summarize(values)
            row["samePayload"] = all(bench.succeeded(s) for ss in samples.values() for s in ss) and {
                s["payloadHash"] for ss in samples.values() for s in ss
            } == {s["payloadHash"] for s in case["samples"]}
            result[section].append(row)
            args.output.write_text(json.dumps(result, indent=2) + "\n")
            print(json.dumps({
                "section": section, "name": row["name"],
                "baselineMs": row["baseline"]["warmMedianMs"],
                "currentMs": row["current"]["warmMedianMs"],
                "samePayload": row["samePayload"],
            }), flush=True)
            if not row["samePayload"]:
                raise RuntimeError(f"{row['name']} failed or changed its payload/operation cost")


if __name__ == "__main__":
    main()
