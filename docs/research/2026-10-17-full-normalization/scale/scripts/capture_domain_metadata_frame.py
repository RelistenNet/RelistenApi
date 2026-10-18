#!/usr/bin/env python3
"""Capture or verify the complete, metadata-only venue/music/recording frame."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import stat
import subprocess
import sys
import threading
import uuid

# Reuse fixed credential transport, immutable publication and byte hashing. The
# original capture's SQL, seed contract, validation and bounds stay unchanged.
import capture_metadata_frame as transport
import metadata_frame_validation as scalars
from domain_metadata_frame_validation import (
    DomainMetadataFrameValidator, FrameCaptureError, MAXIMUM_INDEX_BYTES,
    MAXIMUM_LINE_BYTES, MAXIMUM_ROWS, VERSION, require,
)

ENDPOINT, DATABASE = transport.ENDPOINT, transport.DATABASE
SQL_PATH = Path(__file__).resolve().parents[1] / "sql/full-domain-metadata-frame.sql"
MAXIMUM_OUTPUT_BYTES = 16 * 1024**3
MAXIMUM_MANIFEST_BYTES = 1024**2
QUERY_TIMEOUT_MS = 2_700_000
PROCESS_DEADLINE_SECONDS = 5400
MAXIMUM_SOURCE_BYTES = 1024**2
MINIMUM_FREE_FILESYSTEM_BYTES = 4 * 1024**3
STORAGE_CHECK_INTERVAL_ROWS = 1000
PSQL_FETCH_COUNT = 1000
PSQL_STREAM_OPTIONS = ("-X", "-q", "-At", "-v", "ON_ERROR_STOP=1", "-v", f"FETCH_COUNT={PSQL_FETCH_COUNT}")
HASH = re.compile(r"[0-9a-f]{64}\Z")


def _source_artifacts():
    import domain_metadata_frame_validation
    return [{"path": str(path), **transport.fingerprint(path, MAXIMUM_SOURCE_BYTES)} for path in (
        Path(__file__).resolve(), Path(domain_metadata_frame_validation.__file__).resolve(),
        Path(transport.__file__).resolve(), Path(scalars.__file__).resolve(),
    )]


def _existing_storage(location):
    """Find the filesystem for a destination before its directory exists."""
    path = Path(location).absolute()
    while True:
        try:
            metadata = path.stat()
            require(stat.S_ISDIR(metadata.st_mode), "local_storage_location_invalid")
            return path, metadata.st_dev
        except FileNotFoundError:
            require(path.parent != path, "local_storage_location_missing")
            path = path.parent


def _check_storage(locations):
    """Keep a fixed local reserve on every output filesystem; never delete evidence.

    This is an observed free-space floor, not a disk reservation. Other processes
    can consume space between checks; allocation failures still fail the attempt.
    """
    checked = set()
    try:
        for location in locations:
            path, device = _existing_storage(location)
            if device in checked:
                continue
            capacity = os.statvfs(path)
            require(capacity.f_bavail * capacity.f_frsize >= MINIMUM_FREE_FILESYSTEM_BYTES,
                    "local_storage_reserve_exhausted")
            # Some filesystems report -1 for an unknown inode count. Only an
            # explicit zero means that no inode is available to this operator.
            require(capacity.f_favail != 0, "local_storage_inodes_exhausted")
            checked.add(device)
    except OSError:
        raise FrameCaptureError("local_storage_check_failed") from None


def _start_query(query):
    # An inherited service, hostaddr or password file must not redirect this fixed
    # replica command. No password or raw PostgreSQL stderr enters retained evidence.
    env = {key: value for key, value in os.environ.items() if not key.startswith("PG")}
    env.update(PGPASSWORD=transport._credentials(), PGSSLMODE="require", PGCONNECT_TIMEOUT="15",
               PGCLIENTENCODING="UTF8",
               PGOPTIONS=f"-c default_transaction_read_only=on -c statement_timeout={QUERY_TIMEOUT_MS}")
    try:
        # psql otherwise buffers an entire SELECT in libpq before stdout yields a
        # row. Fetch bounded cursor batches inside the SQL's existing transaction.
        return subprocess.Popen(
            ["psql", *PSQL_STREAM_OPTIONS, "-h", ENDPOINT,
             "-p", "5432", "-U", "app", "-d", DATABASE, "-f", str(query)],
            env=env, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        )
    finally:
        env.pop("PGPASSWORD", None)


def _consume_lines(stream, output, validator, storage_locations=()):
    digest, size, rows = hashlib.sha256(), 0, 0
    while True:
        if rows % STORAGE_CHECK_INTERVAL_ROWS == 0:
            _check_storage(storage_locations)
        line = stream.readline(MAXIMUM_LINE_BYTES + 1)
        if not line:
            break
        require(len(line) <= MAXIMUM_LINE_BYTES, "metadata_line_capacity_exceeded")
        require(size + len(line) <= MAXIMUM_OUTPUT_BYTES, "metadata_byte_capacity_exceeded")
        require(rows < MAXIMUM_ROWS, "metadata_row_capacity_exceeded")
        # Check every nested property before retaining even a partial line. Safe
        # fixed error codes must not reveal malformed plaintext sent by a query.
        validator.accept(line)
        if output is not None:
            output.write(line)
        digest.update(line)
        size += len(line)
        rows += 1
    _check_storage(storage_locations)
    return {"bytes": size, "sha256": digest.hexdigest(), "rows": rows}


def _recheck_inputs(sources, template):
    require(transport.fingerprint(SQL_PATH, MAXIMUM_SOURCE_BYTES) == template, "sql_template_changed")
    for item in sources:
        require(transport.fingerprint(item["path"], MAXIMUM_SOURCE_BYTES)
                == {key: item[key] for key in ("bytes", "sha256")}, "capture_source_changed")


def _completion_report(directory, frame_uuid, sources, summary, frame):
    return {
        "version": VERSION, "state": "completed_metadata_only", "frameUuid": frame_uuid,
        "endpoint": ENDPOINT + ":5432", "database": DATABASE,
        "readOnly": True, "isolation": "repeatable read", "header": summary["header"],
        "sameSnapshotCounts": summary["sameSnapshotCounts"],
        "joinDiagnostics": summary["joinDiagnostics"],
        "metadataFrame": {"path": "frame.jsonl", **frame, "rowsByKind": summary["rowsByKind"]},
        "metadataIndex": transport.artifact(directory / "validation.sqlite", "validation.sqlite"),
        "sqlTemplate": transport.artifact(directory / "sql-template.sql", "sql-template.sql"),
        "executedSql": transport.artifact(directory / "executed.sql", "executed.sql"),
        "request": transport.artifact(directory / "request.json", "request.json"),
        "executableSources": sources,
        "bounds": {"maximumLineBytes": MAXIMUM_LINE_BYTES, "maximumRows": MAXIMUM_ROWS,
                   "maximumOutputBytes": MAXIMUM_OUTPUT_BYTES, "maximumIndexBytes": MAXIMUM_INDEX_BYTES,
                   "queryTimeoutMs": QUERY_TIMEOUT_MS, "processDeadlineSeconds": PROCESS_DEADLINE_SECONDS,
                   "minimumFreeFilesystemBytes": MINIMUM_FREE_FILESYSTEM_BYTES,
                   "storageCheckIntervalRows": STORAGE_CHECK_INTERVAL_ROWS,
                   "psqlFetchRows": PSQL_FETCH_COUNT},
        "runtime": {"python": platform.python_version(), "implementation": platform.python_implementation()},
        "qualifications": {
            "rawTextExported": False, "semanticParsing": False,
            "contentDerivedMetadataComputed": True, "serverSourceProcessing": "nullness_length_md5_only",
            "policyActivationPermitted": False, "independenceEstablished": False,
            "historicalBindingsAuthenticated": False, "exposureHistory": "unavailable",
            "parentAuthority": "this_snapshot_only", "playAssociations": "unordered_multiset",
            "tourEraYearParents": "native_coordinates_only;not_reconciled_in_this_frame",
        },
    }


def capture(directory):
    """Publish one complete snapshot in a new directory; never resume a partial capture."""
    directory = Path(directory)
    require(not os.path.lexists(directory), "output_directory_exists")
    with SQL_PATH.open("rb") as stream:
        query = stream.read(MAXIMUM_SOURCE_BYTES + 1)
    require(len(query) <= MAXIMUM_SOURCE_BYTES, "input_capacity_exceeded")
    template_pin = {"bytes": len(query), "sha256": hashlib.sha256(query).hexdigest()}
    sources = _source_artifacts()
    storage_locations = (directory,)
    _check_storage(storage_locations)
    directory.mkdir(parents=True, exist_ok=False)
    frame_uuid = str(uuid.uuid4())
    process = validator = watchdog = None
    timed_out = threading.Event()
    try:
        transport._new_json(directory / "request.json", {
            "version": VERSION, "frameUuid": frame_uuid, "endpoint": ENDPOINT + ":5432",
            "database": DATABASE, "sqlTemplate": template_pin, "executableSources": sources,
        })
        transport._new_bytes(directory / "sql-template.sql", query)
        transport._new_bytes(directory / "executed.sql", query)
        _check_storage(storage_locations)
        validator = DomainMetadataFrameValidator(directory / "validation.sqlite")
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
        with (directory / "frame.partial.jsonl").open("xb") as output:
            frame = _consume_lines(process.stdout, output, validator, storage_locations)
            output.flush()
            os.fsync(output.fileno())
        exit_code = process.wait()
        require(not timed_out.is_set(), "query_deadline_exceeded")
        require(exit_code == 0, "query_process_failed")
        _check_storage(storage_locations)
        summary = validator.finish()
        watchdog.cancel()
        validator.close()
        validator = None
        _recheck_inputs(sources, template_pin)
        for filename in ("sql-template.sql", "executed.sql"):
            require(transport.fingerprint(directory / filename, MAXIMUM_SOURCE_BYTES) == template_pin,
                    "retained_sql_changed")
        require(transport.fingerprint(directory / "frame.partial.jsonl", MAXIMUM_OUTPUT_BYTES)
                == {key: frame[key] for key in ("bytes", "sha256")}, "retained_frame_changed")
        report = _completion_report(directory, frame_uuid, sources, summary, frame)
        _check_storage(storage_locations)
        transport._publish_completion(directory, report)
        return report
    except BaseException as failure:
        try:
            transport._stop_query(process)
        except (OSError, subprocess.SubprocessError):
            pass
        code = failure.code if isinstance(failure, FrameCaptureError) else (
            "interrupted" if isinstance(failure, KeyboardInterrupt) else "capture_failed")
        try:
            transport._new_json(directory / "failure.json", {
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
                pass


def _read_manifest(directory, expected_sha256):
    require(type(expected_sha256) is str and HASH.fullmatch(expected_sha256), "invalid_manifest_pin")
    with (directory / "manifest.json").open("rb") as stream:
        raw = stream.read(MAXIMUM_MANIFEST_BYTES + 1)
    require(len(raw) <= MAXIMUM_MANIFEST_BYTES, "manifest_capacity_exceeded")
    require(hashlib.sha256(raw).hexdigest() == expected_sha256, "manifest_changed")
    try:
        report = json.loads(raw, object_pairs_hook=scalars.unique_object, parse_constant=scalars.invalid_constant)
    except (ValueError, UnicodeError, RecursionError):
        raise FrameCaptureError("invalid_manifest") from None
    require(type(report) is dict and report.get("version") == VERSION
            and report.get("state") == "completed_metadata_only", "incomplete_capture")
    return report


def _verify_artifact(directory, metadata, filename, maximum):
    require(type(metadata) is dict and metadata.get("path") == filename, "invalid_artifact_path")
    require(type(metadata.get("bytes")) is int and 0 <= metadata["bytes"] <= maximum
            and type(metadata.get("sha256")) is str and HASH.fullmatch(metadata["sha256"]), "invalid_artifact_metadata")
    require(transport.fingerprint(directory / filename, maximum)
            == {key: metadata[key] for key in ("bytes", "sha256")}, "artifact_changed")


def verify(directory, expected_manifest_sha256, index_path):
    """Revalidate a pinned completed stream offline into a separate, new SQLite index.

    Artifact pins authenticate capture bytes, not the continued existence of original
    source-code paths. Verification never reopens SQL, credentials or a database.
    """
    directory, index_path = Path(directory), Path(index_path)
    require(not os.path.lexists(index_path), "index_exists")
    require(not index_path.resolve().is_relative_to(directory.resolve()), "verification_index_inside_capture")
    storage_locations = (directory, index_path.parent)
    _check_storage(storage_locations)
    report = _read_manifest(directory, expected_manifest_sha256)
    artifacts = (
        ("metadataFrame", "frame.jsonl", MAXIMUM_OUTPUT_BYTES),
        ("metadataIndex", "validation.sqlite", MAXIMUM_INDEX_BYTES),
        ("sqlTemplate", "sql-template.sql", MAXIMUM_SOURCE_BYTES),
        ("executedSql", "executed.sql", MAXIMUM_SOURCE_BYTES),
        ("request", "request.json", MAXIMUM_MANIFEST_BYTES),
    )
    for key, filename, maximum in artifacts:
        _verify_artifact(directory, report.get(key), filename, maximum)
    require(report["executedSql"]["sha256"] == report["sqlTemplate"]["sha256"], "sql_template_execution_mismatch")
    sources = _source_artifacts()
    _check_storage(storage_locations)
    validator = DomainMetadataFrameValidator(index_path)
    try:
        with (directory / "frame.jsonl").open("rb") as stream:
            frame = _consume_lines(stream, None, validator, storage_locations)
        _check_storage(storage_locations)
        summary = validator.finish()
        require(frame == {key: report["metadataFrame"][key] for key in ("bytes", "sha256", "rows")}, "frame_metadata_mismatch")
        require(summary == {key: report[key] for key in (
            "header", "sameSnapshotCounts", "joinDiagnostics")}
            | {"rowsByKind": report["metadataFrame"]["rowsByKind"]}, "frame_summary_mismatch")
        # No source path from the untrusted manifest is executed or reopened.
        for item in sources:
            require(transport.fingerprint(item["path"], MAXIMUM_SOURCE_BYTES)
                    == {key: item[key] for key in ("bytes", "sha256")}, "verification_source_changed")
        for key, filename, maximum in artifacts:
            _verify_artifact(directory, report[key], filename, maximum)
        require(transport.fingerprint(directory / "manifest.json", MAXIMUM_MANIFEST_BYTES)["sha256"]
                == expected_manifest_sha256, "manifest_changed")
        _check_storage(storage_locations)
        return {"version": VERSION, "state": "verified_metadata_only", "manifestSha256": expected_manifest_sha256,
                "counts": summary["sameSnapshotCounts"], "joinDiagnostics": summary["joinDiagnostics"],
                "verificationSources": sources, "verificationIndex": transport.artifact(index_path, str(index_path))}
    except FrameCaptureError:
        raise
    except (KeyError, TypeError, ValueError, OSError):
        raise FrameCaptureError("verification_failed") from None
    finally:
        validator.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    capture_parser = commands.add_parser("capture", help="Capture all nine fixed rosters in one replica snapshot")
    capture_parser.add_argument("directory", type=Path)
    verify_parser = commands.add_parser("verify", help="Reconcile a pinned completed capture without database access")
    verify_parser.add_argument("directory", type=Path)
    verify_parser.add_argument("--manifest-sha256", required=True)
    verify_parser.add_argument("--index", required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.command == "capture":
            report = capture(args.directory)
            result = {"state": report["state"], "frameUuid": report["frameUuid"],
                      "counts": report["sameSnapshotCounts"]}
        else:
            result = verify(args.directory, args.manifest_sha256, args.index)
        print(json.dumps(result, allow_nan=False))
        return 0
    except (FrameCaptureError, OSError):
        print("Domain metadata operation failed; inspect the bounded failure receipt if present.", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("Domain metadata operation interrupted; partial output is incomplete.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
