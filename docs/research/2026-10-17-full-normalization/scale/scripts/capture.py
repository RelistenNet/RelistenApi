#!/usr/bin/env python3
"""Capture exact SQL and raw replica evidence without printing database credentials.

The target directory must be new. Failed output stays as a partial file; only a
completion marker allows the process to publish the completed snapshot filename.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import subprocess


def capture(directory: Path, sql_path: Path | None = None) -> None:
    directory.mkdir(parents=True, exist_ok=False)
    sql = sql_path or Path(__file__).resolve().parents[1] / "sql/cross-artist-sample.sql"
    query = directory / "executed.sql"
    query.write_bytes(sql.read_bytes())
    receipt = {
        "sql_sha256": hashlib.sha256(query.read_bytes()).hexdigest(),
        "sql_snapshot": str(query.resolve()),
        "endpoint": "relisten-db-ro.tail09dbf.ts.net:5432",
        "database": "app",
        "read_only": True,
        "connection_statement_timeout_ms": 180000,
    }
    (directory / "query.json").write_text(json.dumps(receipt, indent=2) + "\n")
    encoded = subprocess.run(
        ["kubectl", "--context", "relisten3-k3s", "-n", "default", "get", "secret",
         "relisten-db-app", "-o", "jsonpath={.data.password}"],
        check=True, stdout=subprocess.PIPE,
    ).stdout
    env = os.environ.copy()
    env.update(PGPASSWORD=base64.b64decode(encoded, validate=True).decode(),
               PGSSLMODE="require", PGCONNECT_TIMEOUT="15",
               PGOPTIONS="-c default_transaction_read_only=on -c statement_timeout=180000")
    partial = directory / "snapshot.partial.jsonl"
    try:
        with partial.open("x", encoding="utf-8") as output:
            subprocess.run(
                ["psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-h",
                 "relisten-db-ro.tail09dbf.ts.net", "-p", "5432", "-U", "app", "-d", "app",
                 "-f", str(query)], check=True, env=env, stdout=output,
            )
    finally:
        env.pop("PGPASSWORD", None)
    with partial.open(encoding="utf-8") as stream:
        last = None
        for line in stream:
            if line.strip():
                last = json.loads(line)
    if not last or last.get("kind") != "snapshot_complete":
        raise ValueError("The replica query did not complete; preserve the partial file.")
    partial.rename(directory / "snapshot.jsonl")
    print(f"Completed read-only snapshot: {directory}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--sql", type=Path)
    args = parser.parse_args()
    capture(args.directory, args.sql)
