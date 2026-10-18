#!/usr/bin/env python3
"""Capture the fixed full artist/source/show metadata frame without plaintext export."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import threading
import uuid

from metadata_frame_validation import FrameCaptureError, MetadataFrameValidator

VERSION = "full-catalog-metadata-frame-v1"
SEED_VERSION = "full-metadata-track-lookup-seeds-v1"
ENDPOINT = "relisten-db-ro.tail09dbf.ts.net"
DATABASE = "app"
MAX_SEED_BYTES = 16 * 1024 * 1024
MAX_SEEDS = 100_000
MAX_LINE_BYTES = 64 * 1024
MAX_OUTPUT_BYTES = 2 * 1024 * 1024 * 1024
MAX_ROWS = 5_000_000
LOOKUP_CHUNK = 256
QUERY_TIMEOUT_MS = 900_000
PROCESS_DEADLINE_SECONDS = 1800
SQL_PATH = Path(__file__).resolve().parents[1] / "sql/full-catalog-metadata-frame.sql"
NAMESPACES = {"catalog-2026-10-17", "local-catalog"}
HASH = re.compile(r"[0-9a-f]{64}\Z")
RELATIVE_METADATA_PATH = re.compile(r"(?:[A-Za-z0-9._-]+/)*[A-Za-z0-9._-]+\Z")


def require(condition, code):
    if not condition:
        raise FrameCaptureError(code)


def fingerprint(path, maximum_bytes=None):
    size, digest = 0, hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            size += len(block)
            require(maximum_bytes is None or size <= maximum_bytes, "input_capacity_exceeded")
            digest.update(block)
    return {"bytes": size, "sha256": digest.hexdigest()}


def artifact(path, relative):
    return {"path": relative, **fingerprint(path)}


def _closed_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate_seed_property")
        result[key] = value
    return result


def _invalid_seed_constant(_value):
    raise FrameCaptureError("invalid_seed_json")


def read_seed_plan(path, expected_sha256):
    """Authenticate the operator's closed metadata plan; never open its authority paths."""
    require(isinstance(expected_sha256, str) and HASH.fullmatch(expected_sha256), "invalid_seed_pin")
    with Path(path).open("rb") as stream:
        raw = stream.read(MAX_SEED_BYTES + 1)
    require(len(raw) <= MAX_SEED_BYTES, "seed_plan_capacity_exceeded")
    require(hashlib.sha256(raw).hexdigest() == expected_sha256, "seed_plan_changed")
    try:
        plan = json.loads(raw, object_pairs_hook=_closed_object,
                          parse_constant=_invalid_seed_constant)
    except (ValueError, UnicodeError, RecursionError):
        raise FrameCaptureError("invalid_seed_json") from None
    require(isinstance(plan, dict) and set(plan) == {"version", "seeds", "authorities"}, "invalid_seed_contract")
    require(plan["version"] == SEED_VERSION, "unsupported_seed_version")
    seeds, authorities = plan["seeds"], plan["authorities"]
    require(isinstance(seeds, list) and len(seeds) <= MAX_SEEDS, "seed_plan_capacity_exceeded")
    require(isinstance(authorities, list) and 1 <= len(authorities) <= 1000, "invalid_seed_authorities")
    for authority in authorities:
        require(isinstance(authority, dict) and set(authority) == {"path", "bytes", "sha256"}, "invalid_seed_authority")
        relative = authority["path"]
        require(isinstance(relative, str) and len(relative) <= 1024
                and RELATIVE_METADATA_PATH.fullmatch(relative)
                and all(part not in {".", ".."} for part in relative.split("/")), "invalid_seed_authority")
        require(type(authority["bytes"]) is int and authority["bytes"] >= 0
                and isinstance(authority["sha256"], str) and HASH.fullmatch(authority["sha256"]), "invalid_seed_authority")
    seen = set()
    for seed in seeds:
        require(isinstance(seed, dict) and set(seed) == {"catalog_namespace", "track_id", "track_uuid"}, "invalid_seed_coordinate")
        require(isinstance(seed["catalog_namespace"], str)
                and seed["catalog_namespace"] in NAMESPACES, "unsupported_catalog_namespace")
        native, stable = seed["track_id"], seed["track_uuid"]
        require(native is None or type(native) is int and 0 < native < 2**63, "invalid_seed_coordinate")
        if stable is not None:
            try:
                parsed = uuid.UUID(stable) if isinstance(stable, str) else None
            except ValueError:
                parsed = None
            require(parsed is not None and parsed.int != 0 and str(parsed) == stable, "invalid_seed_coordinate")
        require(native is not None or stable is not None, "invalid_seed_coordinate")
        key = (seed["catalog_namespace"], native, stable)
        require(key not in seen, "duplicate_seed_coordinate")
        seen.add(key)
    return raw, plan


def _section(template, name):
    start, end = "-- BEGIN " + name + "\n", "-- END " + name + "\n"
    require(template.count(start) == 1 and template.count(end) == 1, "invalid_fixed_sql_template")
    require(template.index(start) < template.index(end), "invalid_fixed_sql_template")
    before, tail = template.split(start)
    body, after = tail.split(end)
    return before, body, after


def render_sql(template_bytes, seeds):
    """Repeat only the fixed lookup SELECT, with safe literal JSON, within one transaction."""
    try:
        template = template_bytes.decode("utf-8")
    except UnicodeError:
        raise FrameCaptureError("invalid_fixed_sql_template") from None
    before, lookup, after = _section(template, "LOOKUPS")
    token = ":'track_lookup_seeds_json'"
    require(lookup.count(token) == 1, "invalid_fixed_sql_template")
    chunks = [seeds[index:index + LOOKUP_CHUNK] for index in range(0, len(seeds), LOOKUP_CHUNK)] or [[]]
    rendered = []
    for chunk in chunks:
        payload = json.dumps(chunk, ensure_ascii=True, sort_keys=True, separators=(",", ":"))
        # JSON serialization is not SQL escaping. Quote its apostrophes as a PostgreSQL
        # literal even though the closed namespace/ID/UUID grammar currently excludes them.
        literal = "'" + payload.replace("'", "''") + "'"
        rendered.append(lookup.replace(token, literal))
    return (before + "\n".join(rendered) + after).encode("utf-8")


def _new_json(path, value):
    with Path(path).open("xb") as stream:
        stream.write((json.dumps(value, indent=2, allow_nan=False) + "\n").encode("utf-8"))
        stream.flush()
        os.fsync(stream.fileno())


def _new_bytes(path, value):
    with Path(path).open("xb") as stream:
        stream.write(value)
        stream.flush()
        os.fsync(stream.fileno())


def _publish_new(partial, completed):
    # Hard-link publication is atomic and refuses an existing destination on Unix. Plain
    # rename would silently replace it, even though this capture originally owned a new dir.
    os.link(partial, completed)
    Path(partial).unlink()


def _credentials():
    response = subprocess.run(
        ["kubectl", "--context", "relisten3-k3s", "-n", "default", "get", "secret",
         "relisten-db-app", "-o", "jsonpath={.data.password}"],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=30,
    ).stdout
    require(len(response) <= 8192, "credential_transport_failed")
    try:
        return base64.b64decode(response, validate=True).decode("utf-8")
    except (ValueError, UnicodeError):
        raise FrameCaptureError("credential_transport_failed") from None


def _start_query(query):
    # A caller's PGHOSTADDR/PGSERVICE must not redirect this fixed replica command.
    env = {key: value for key, value in os.environ.items() if not key.startswith("PG")}
    env.update(PGPASSWORD=_credentials(), PGSSLMODE="require", PGCONNECT_TIMEOUT="15",
               PGCLIENTENCODING="UTF8",
               PGOPTIONS=f"-c default_transaction_read_only=on -c statement_timeout={QUERY_TIMEOUT_MS}")
    try:
        # stderr is deliberately discarded: PostgreSQL errors can echo a source value. The
        # receipt retains only a safe reason code and process exit status, never raw errors.
        return subprocess.Popen(
            ["psql", "-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-h", ENDPOINT,
             "-p", "5432", "-U", "app", "-d", DATABASE, "-f", str(query)],
            env=env, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
    finally:
        env.pop("PGPASSWORD", None)


def _stop_query(process):
    if process is None or process.poll() is not None:
        return
    process.terminate()
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def _consume_query(process, partial, validator, timed_out):
    digest, size, rows = hashlib.sha256(), 0, 0
    with partial.open("xb") as output:
        while True:
            line = process.stdout.readline(MAX_LINE_BYTES + 1)
            if not line:
                break
            require(len(line) <= MAX_LINE_BYTES, "metadata_line_capacity_exceeded")
            require(size + len(line) <= MAX_OUTPUT_BYTES, "metadata_byte_capacity_exceeded")
            require(rows < MAX_ROWS, "metadata_row_capacity_exceeded")
            # Reject malformed plaintext, including unknown nested properties, before it
            # reaches even the partial evidence file. No failed line is logged or retained.
            validator.accept(line)
            output.write(line)
            digest.update(line)
            size += len(line)
            rows += 1
        output.flush()
        os.fsync(output.fileno())
    exit_code = process.wait()
    require(not timed_out.is_set(), "query_deadline_exceeded")
    require(exit_code == 0, "query_process_failed")
    summary = validator.finish()
    return summary, {"bytes": size, "sha256": digest.hexdigest(), "rows": rows}


def _source_artifacts():
    import metadata_frame_validation
    return [{"path": str(path), **fingerprint(path, 1024 * 1024)} for path in
            (Path(__file__).resolve(), Path(metadata_frame_validation.__file__).resolve())]


def _recheck_inputs(seed_path, seed_pin, source_artifacts, template_pin):
    require(fingerprint(seed_path, MAX_SEED_BYTES)["sha256"] == seed_pin, "seed_plan_changed")
    require(fingerprint(SQL_PATH, 1024 * 1024) == template_pin, "sql_template_changed")
    for item in source_artifacts:
        require(fingerprint(item["path"], item["bytes"]) == {"bytes": item["bytes"], "sha256": item["sha256"]}, "capture_source_changed")


def _completion_report(directory, frame_uuid, plan, sources, summary, frame):
    coordinate_count = sum(seed["track_id"] is not None for seed in plan["seeds"]) + sum(
        seed["track_uuid"] is not None for seed in plan["seeds"])
    header = summary["header"]
    report = {
        "version": VERSION, "state": "completed_metadata_only", "frameUuid": frame_uuid,
        "capturedAt": header["captured_at"], "endpoint": ENDPOINT + ":5432", "database": DATABASE,
        "readOnly": True, "isolation": "repeatable read", "snapshot": header["snapshot"],
        "header": header,
        "lookupSeeds": {**artifact(directory / "lookup-seeds.json", "lookup-seeds.json"),
                        "seedCount": len(plan["seeds"]), "coordinateCount": coordinate_count,
                        "authorityAuthentication": "externally_pinned_plan;references_not_reopened"},
        "executedSql": artifact(directory / "executed.sql", "executed.sql"),
        "sqlTemplate": artifact(directory / "sql-template.sql", "sql-template.sql"),
        "metadataFrame": {"path": "frame.jsonl", **frame, "rowsByKind": summary["rowsByKind"]},
        "metadataIndex": artifact(directory / "validation.sqlite", "validation.sqlite"),
        "sameSnapshotCounts": summary["sameSnapshotCounts"],
        "joinDiagnostics": summary["joinDiagnostics"], "seedOutcomeCounts": summary["seedOutcomeCounts"],
        "bounds": {"maximumLineBytes": MAX_LINE_BYTES, "maximumOutputBytes": MAX_OUTPUT_BYTES,
                   "maximumRows": MAX_ROWS, "queryTimeoutMs": QUERY_TIMEOUT_MS,
                   "processDeadlineSeconds": PROCESS_DEADLINE_SECONDS},
        "executableSources": sources, "runtime": {"python": platform.python_version(),
                                                  "implementation": platform.python_implementation()},
        "qualifications": {"rawTextExported": False, "semanticParsing": False,
                           "contentDerivedMetadataComputed": True,
                           "serverSourceProcessing": "nullness_length_md5_only",
                           "policyActivationPermitted": False, "independenceEstablished": False,
                           "lookupCoverage": "supplied_seed_plan_only"},
    }
    return report


def _publish_completion(directory, report):
    _new_json(directory / "manifest.partial.json", report)
    _publish_new(directory / "frame.partial.jsonl", directory / "frame.jsonl")
    directory_fd = os.open(directory, os.O_RDONLY)
    try:
        os.fsync(directory_fd)
    finally:
        os.close(directory_fd)
    # This successful link is the terminal publication. All required sync and cleanup
    # precedes it: a later cleanup failure must not relabel an authoritative frame failed.
    # Keep the harmless partial-manifest link rather than unlink after publication.
    os.link(directory / "manifest.partial.json", directory / "manifest.json")


def capture(directory, seed_path, expected_seed_sha256):
    """Capture one complete fixed metadata snapshot; an interrupted attempt needs a new dir."""
    directory, seed_path = Path(directory), Path(seed_path)
    require(not os.path.lexists(directory), "output_directory_exists")
    seed_raw, plan = read_seed_plan(seed_path, expected_seed_sha256)
    with SQL_PATH.open("rb") as template_stream:
        template = template_stream.read(1024 * 1024 + 1)
    require(len(template) <= 1024 * 1024, "input_capacity_exceeded")
    query = render_sql(template, plan["seeds"])
    template_pin = {"bytes": len(template), "sha256": hashlib.sha256(template).hexdigest()}
    sources = _source_artifacts()
    directory.mkdir(parents=True, exist_ok=False)
    frame_uuid = str(uuid.uuid4())
    process = validator = watchdog = None
    timed_out = threading.Event()
    try:
        _new_json(directory / "request.json", {
            "version": VERSION, "frameUuid": frame_uuid,
            "endpoint": ENDPOINT + ":5432", "database": DATABASE,
            "seedSha256": expected_seed_sha256, "sqlTemplate": template_pin,
            "executableSources": sources,
        })
        _new_bytes(directory / "lookup-seeds.json", seed_raw)
        _new_bytes(directory / "sql-template.sql", template)
        _new_bytes(directory / "executed.sql", query)
        validator = MetadataFrameValidator(directory / "validation.sqlite", plan["seeds"])
        process = _start_query(directory / "executed.sql")

        def expire():
            if process.poll() is None:
                timed_out.set()
                try:
                    process.kill()
                except ProcessLookupError:
                    pass

        watchdog = threading.Timer(PROCESS_DEADLINE_SECONDS, expire)
        watchdog.daemon = True
        watchdog.start()
        summary, frame = _consume_query(process, directory / "frame.partial.jsonl", validator, timed_out)
        watchdog.cancel()
        validator.close()
        validator = None
        _recheck_inputs(seed_path, expected_seed_sha256, sources, template_pin)
        report = _completion_report(directory, frame_uuid, plan, sources, summary, frame)
        _publish_completion(directory, report)
        return report
    except BaseException as failure:
        try:
            _stop_query(process)
        except (OSError, subprocess.SubprocessError):
            pass
        code = failure.code if isinstance(failure, FrameCaptureError) else (
            "interrupted" if isinstance(failure, KeyboardInterrupt) else "capture_failed")
        try:
            _new_json(directory / "failure.json", {
                "version": VERSION, "state": "failed", "frameUuid": frame_uuid,
                "reasonCode": code, "processExitCode": None if process is None else process.poll(),
            })
        except OSError:
            pass
        if isinstance(failure, KeyboardInterrupt):
            raise
        raise FrameCaptureError(code) from None
    finally:
        if watchdog is not None:
            watchdog.cancel()
        if process is not None and process.stdout is not None:
            try:
                process.stdout.close()
            except OSError:
                pass
        if validator is not None:
            try:
                validator.close()
            except Exception:
                # Cleanup must not replace the safe, retained failure reason.
                pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--seeds", type=Path, required=True)
    parser.add_argument("--seeds-sha256", required=True)
    args = parser.parse_args()
    try:
        report = capture(args.directory, args.seeds, args.seeds_sha256)
        print(json.dumps({"state": report["state"], "frameUuid": report["frameUuid"],
                          "counts": report["sameSnapshotCounts"], "directory": str(args.directory)}))
        return 0
    except (FrameCaptureError, OSError):
        print("Metadata capture failed; inspect the bounded failure receipt if present.", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Metadata capture interrupted; retained partial output is incomplete.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
