#!/usr/bin/env python3
"""Compare existing title predictions with separately retained song/show presence.

This is an exposed development comparison, not ordered-setlist or per-file gold.
The benchmark never matches names or dates: C# owns both title interpretation and
proposed reference alignment. Source SQLite images are opened read-only. Original
assignments, current targets and identity-resolved comparisons remain separate.
"""
import argparse
from collections import Counter, defaultdict
from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import sys
import time

from shadow_reader import _unpack_text
from file_title_membership import FileTitleMembership

ARTISTS = {
    "ca53d281-0041-ae33-a050-c87702d93b0c": "Phish",
    "77a58ff9-2e01-c59c-b8eb-cff106049b72": "Grateful Dead",
}
VERSION = "artist-setlist-reference-benchmark-v2"
MAX_LINE = 32 * 1024**2


def readonly(path):
    db = sqlite3.connect(Path(path).resolve(strict=True).as_uri() + "?mode=ro", uri=True)
    db.row_factory = sqlite3.Row
    db.execute("PRAGMA query_only=ON")
    return db


def sha(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for block in iter(lambda: stream.read(1024**2), b""):
            digest.update(block)
    return digest.hexdigest()


def artifact(path):
    path = Path(path).resolve(strict=True)
    return {"path": str(path), "bytes": path.stat().st_size, "sha256": sha(path)}


class Benchmark:
    def __init__(self, request, output):
        self.request = request
        self.output = Path(output)
        self.started = time.monotonic()
        self.maximum_seconds = request.get("maximumSeconds", 600)
        self.maximum_bytes = request.get("maximumOutputBytes", 64 * 1024**2)
        self.written = 0
        self.chunk_pins = {}
        self.files = {}
        self.references = defaultdict(list)
        self.coverage = Counter()
        self.shows = {}
        self.show_population = defaultdict(lambda: {"files": 0, "sources": set()})
        self.active = {}
        self.mappings = {}
        self.known = {}
        self.identity_aliases = {}
        self.replay = None
        self.replayed_files = {}
        self.replay_songs = {}

    def check(self):
        if time.monotonic() - self.started > self.maximum_seconds:
            raise ValueError("Benchmark deadline exceeded; no completion manifest was published")

    def write(self, stream, row):
        encoded = (json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n").encode()
        if self.written + len(encoded) > self.maximum_bytes:
            raise ValueError("Benchmark output bound exceeded; no completion manifest was published")
        stream.write(encoded)
        self.written += len(encoded)

    def selected_records(self, run, domain):
        """Read only current logical coordinates; never construct or migrate a reader."""
        run = Path(run).resolve(strict=True)
        indices = [run / ".shadow-reader-v3.sqlite3", run / ".shadow-reader-v2.sqlite3"]
        index = next((path for path in indices if path.exists()), None)
        if index is None:
            raise ValueError("Existing reader index is required")
        selected = defaultdict(dict)
        with closing(readonly(index)) as db:
            db.create_function("key_text", 1, _unpack_text, deterministic=True)
            rows = db.execute("""SELECT a.uuid artist,
                coalesce(h.input_uuid,substr(key_text(i.data),-36)) input_uuid,c.path,r.line_no,c.bytes
                FROM records r JOIN headers h ON h.id=r.header_id
                JOIN input_keys i ON i.id=h.input_id JOIN artists a ON a.id=h.artist_id JOIN domains d ON d.id=r.domain_id
                JOIN chunks c ON c.id=r.chunk_id WHERE a.uuid IN (?,?)
                AND d.name=? AND c.state='indexed' ORDER BY c.path,r.line_no""", (*ARTISTS, domain))
            for row in rows:
                if domain == "file_contents" and row["input_uuid"] not in self.active:
                    continue
                selected[row["path"]][row["line_no"]] = (row["artist"], row["input_uuid"], row["bytes"])
        for chunk, coordinates in selected.items():
            self.check()
            path = (run / chunk).resolve(strict=True)
            if not path.is_relative_to(run):
                raise ValueError("A reader chunk escapes its configured run")
            before = artifact(path)
            if any(size != before["bytes"] for _, _, size in coordinates.values()):
                raise ValueError("A selected immutable chunk changed size")
            self.chunk_pins[str(path)] = before
            with gzip.open(path, "rb") as stream:
                line = 0
                while coordinates:
                    # Unselected large rows are drained without allocating a full body.
                    part = stream.readline(MAX_LINE + 1)
                    if not part:
                        raise ValueError("A selected reader coordinate is missing")
                    line += 1
                    selected_row = coordinates.pop(line, None)
                    if len(part) > MAX_LINE and selected_row is not None:
                        raise ValueError("A selected witness exceeds the line bound")
                    while part and not part.endswith(b"\n"):
                        part = stream.readline(MAX_LINE + 1)
                    if selected_row is None:
                        continue
                    row = json.loads(part)
                    artist, uuid, _ = selected_row
                    if row.get("artistUuid") != artist or row.get("inputUuid") != uuid or row.get("domain") != domain:
                        raise ValueError("Reader header disagrees with its original witness")
                    yield row, {"run": str(run), "chunk": chunk, "line": line,
                                "sha256": before["sha256"], "inputUuid": uuid}
            if path.stat().st_size != before["bytes"]:
                raise ValueError("A selected immutable chunk changed during reading")

    def load_population(self):
        with closing(readonly(self.request["metadata"])) as db:
            for row in db.execute("SELECT uuid,artist_uuid FROM show WHERE artist_uuid IN (?,?)", tuple(ARTISTS)):
                self.shows[row["uuid"]] = row["artist_uuid"]
            rows = db.execute("""SELECT t.uuid,t.artist_uuid,s.uuid source_uuid,s.show_uuid
                FROM source_track t LEFT JOIN source s ON s.id=t.source_id
                WHERE t.artist_uuid IN (?,?) AND t.is_orphaned=0""", tuple(ARTISTS))
            homes = {}
            for row in rows:
                if row["show_uuid"] is not None and self.shows.get(row["show_uuid"]) != row["artist_uuid"]:
                    raise ValueError("Captured active file has a foreign show owner")
                home = (row["artist_uuid"], row["show_uuid"], row["source_uuid"])
                self.active[row["uuid"]] = homes.setdefault(home, home)
                if row["show_uuid"]:
                    population = self.show_population[row["show_uuid"]]
                    population["files"] += 1
                    population["sources"].add(row["source_uuid"])
        with closing(readonly(self.request["normalization"])) as db:
            meta = {r[0]: json.loads(r[1]) for r in db.execute("SELECT key,value FROM metadata")}
            if meta.get("state") != "completed":
                raise ValueError("A completed C# normalization projection is required")
            self.prediction_generation = meta
            self.metadata = meta
            for row in db.execute("SELECT entity_key,legacy_uuid,title,artist_uuid FROM groups WHERE scope='catalogue' AND artist_uuid IN (?,?)", tuple(ARTISTS)):
                self.known[row["entity_key"]] = dict(row)
            rows = db.execute("""SELECT o.original_key,o.artist_uuid,o.disposition original_disposition,
                m.target_key,m.disposition,m.relation
                FROM originals o JOIN members m ON m.scope=o.scope AND m.original_key=o.original_key
                WHERE o.scope='catalogue' AND o.artist_uuid IN (?,?)""", tuple(ARTISTS))
            for row in rows:
                self.mappings.setdefault(row["original_key"], []).append(dict(row))
            # A compound or a competing review target is not identity evidence.
            # The scorer consumes the exact image's one-target correspondences;
            # it never learns equivalences from the expected show memberships.
            for original, mappings in self.mappings.items():
                if len(mappings) != 1:
                    continue
                mapping = mappings[0]
                target = self.known.get(mapping["target_key"], {})
                if (mapping["original_disposition"] == "proposed" and mapping["disposition"] == "proposed"
                        and mapping["relation"] == "identity_alias"
                        and target.get("artist_uuid") == mapping["artist_uuid"]):
                    self.identity_aliases[(mapping["artist_uuid"], original)] = mapping["target_key"]

    def load_references(self):
        raw = {}
        path = Path(self.request["rawSetlists"])
        with gzip.open(path, "rb") as stream:
            for line, value in enumerate(stream, 1):
                self.check()
                if len(value) > MAX_LINE:
                    raise ValueError("Reference line exceeds the bound")
                record = json.loads(value)
                row = record.get("row") or {}
                if record.get("kind") != "setlist_sample" or row.get("artist_id") not in (4, 9):
                    continue
                artist = list(ARTISTS)[0 if row["artist_id"] == 4 else 1]
                members = {m["song"]["uuid"] for m in record["unordered_memberships"] if m.get("song")}
                raw[row["uuid"]] = (artist, members, {"path": str(path.resolve()), "line": line})
        self.reference_accounts = len(raw)
        used = set()
        for record, origin in self.selected_records(self.request["referenceRun"], "setlists"):
            # Native provider account arrays are not imported legacy reference rows.
            if record.get("inputKind") == "provider_input":
                continue
            proposal = record.get("proposal") or {}
            uuid = proposal.get("legacySetlistUuid")
            if uuid not in raw:
                continue
            artist, expected, source = raw[uuid]
            if artist != record["artistUuid"]:
                raise ValueError("Reference artist disagrees with original account")
            members = proposal.get("memberships") or []
            if {m["legacySongUuid"] for m in members if m.get("legacySongUuid")} != expected:
                raise ValueError("Reference projection disagrees with original memberships")
            used.add(uuid)
            association = proposal.get("association") or {}
            show = proposal.get("showUuid")
            basis = association.get("basis", "missing_alignment")
            candidates = association.get("candidates") or []
            aligned = (show is not None and self.shows.get(show) == artist and
                       association.get("showUuid") == show and association.get("reason") is None and
                       (basis.startswith("unique_exact_artist_date") and len(candidates) == 1 or
                        basis == "existing_provider_performance_key"))
            item = {"setlistUuid": uuid, "artistUuid": artist, "showUuid": show,
                    "bindingBasis": basis, "expected": sorted({m["songEntityKey"] for m in members if m.get("songEntityKey")}),
                    "source": source, "bindingWitness": origin}
            if aligned:
                self.references[show].append(item)
            else:
                self.unaligned.append(item)
        self.unprojected = sorted(set(raw) - used)

    def load_replayed_files(self):
        if not self.request.get("fileTitleReplay"):
            return
        # The reader and benchmark consume one authenticated typed image. No
        # title matching or reference-driven repair happens in this scorer.
        self.replay = FileTitleMembership(self.request["fileTitleReplay"], self)
        source = dict(scope="catalogue", run=self.request["fileRun"])
        if not self.replay.reader_ids(source):
            raise ValueError("The replay does not capture the benchmark reader")
        for artist in ARTISTS:
            for row in self.replay.covered_files("catalogue", artist, self.request["fileRun"]):
                self.check()
                uuid = row["fileUuid"]
                if row["fileKind"] != "legacy" or uuid not in self.active:
                    continue
                if self.active[uuid] != (artist, row["showUuid"], row["sourceUuid"]):
                    raise ValueError("A replayed prediction differs from captured file ownership")
                if uuid in self.replayed_files:
                    raise ValueError("A captured file appears twice in the selected replay reader")
                self.replayed_files[uuid] = dict(targets=[], titleId=row.get("titleId"),
                    title=row.get("originalTitle"), disposition=row.get("interpretationDisposition"))
            for row in self.replay.iter_memberships("catalogue", artist, self.request["fileRun"]):
                self.check()
                uuid = row["fileUuid"]
                if row["fileKind"] != "legacy" or uuid not in self.active:
                    continue
                replayed = self.replayed_files.get(uuid)
                if replayed is None:
                    raise ValueError("A replay membership is outside its covered file population")
                replayed["targets"].append(row["targetKey"])
                replayed.update(titleId=row.get("titleId"), title=row.get("originalTitle"),
                                disposition=row["interpretationDisposition"])
                self.replay_songs.setdefault(row["targetKey"], dict(entity_key=row["targetKey"],
                    artist_uuid=artist, title=row.get("targetTitle"), legacy_uuid=row["legacySongUuid"]))

    def load_predictions(self):
        seen = set()
        for record, origin in self.selected_records(self.request["fileRun"], "file_contents"):
            uuid = record["inputUuid"]
            artist, show, source = self.active[uuid]
            proposal = record.get("proposal") or {}
            home = proposal.get("home") or {}
            if home.get("showUuid") != show or home.get("sourceUuid") != source or record["artistUuid"] != artist:
                raise ValueError("Predicted file home disagrees with captured ownership")
            seen.add(uuid)
            targets = set()
            original_targets = set()
            unresolved = 0
            interpretation = proposal.get("titleInterpretation") or {}
            for segment in interpretation.get("segments", []):
                song = segment.get("song")
                if segment.get("outcome") != "proposed" or not song:
                    unresolved += segment.get("outcome") != "not_applicable"
                    continue
                key = song["entityKey"]
                original_targets.add(key)
                mapped = self.mappings.get(key)
                if mapped is None:
                    unresolved += 1
                    continue
                proposed = [m["target_key"] for m in mapped if m["disposition"] == "proposed"]
                targets.update(proposed)
                unresolved += not proposed
            if not interpretation:
                unresolved += 1
            self.coverage[(artist, "files_with_original_song_prediction" if original_targets else "files_without_original_song_prediction")] += 1
            self.coverage[(artist, "files_with_prediction")] += 1
            self.coverage[(artist, "unresolved_segments")] += unresolved
            replayed = self.replayed_files.get(uuid)
            covered = replayed is not None
            resolved_targets = set(replayed["targets"]) if covered else targets
            if self.replay is not None:
                self.coverage[(artist, "files_with_replayed_title" if covered else "files_using_legacy_prediction_fallback")] += 1
                if covered:
                    self.coverage[(artist, "replayed_files_with_proposed_targets" if resolved_targets else "replayed_files_without_proposed_targets")] += 1
            if show:
                aggregate = self.files.setdefault(show, {"fileCount": 0, "sources": set(),
                    "predicted": set(), "rawPredicted": set(), "resolvedPredicted": set(),
                    "unresolvedSegments": 0, "reviewFiles": [], "resolvedReviewFiles": []})
                aggregate["fileCount"] += 1
                aggregate["sources"].add(source)
                aggregate["predicted"].update(targets)
                aggregate["rawPredicted"].update(original_targets)
                aggregate["resolvedPredicted"].update(resolved_targets)
                aggregate["unresolvedSegments"] += unresolved
                references = {tuple(item["expected"]) for item in self.references.get(show, [])}
                if len(references) == 1 and targets - set(next(iter(references))):
                    aggregate["reviewFiles"].append({"fileUuid": uuid, "sourceUuid": source,
                        "title": interpretation.get("originalTitle"), "predicted": sorted(targets),
                        "unresolvedSegments": unresolved, "witness": origin})
                resolved_references = [{self.identity_aliases.get((artist, key), key) for key in item["expected"]}
                                       for item in self.references.get(show, [])]
                expected_sets = {tuple(sorted(keys)) for keys in resolved_references}
                if len(expected_sets) == 1 and resolved_targets - set(next(iter(expected_sets))):
                    aggregate["resolvedReviewFiles"].append(dict(fileUuid=uuid, sourceUuid=source,
                        title=replayed["title"] if covered else interpretation.get("originalTitle"),
                        predicted=sorted(resolved_targets), witness=origin,
                        replayTitleId=replayed["titleId"] if covered else None,
                        predictionBasis="shared_file_title_replay" if covered else "legacy_prediction_fallback"))
        for uuid in set(self.active) - seen:
            self.coverage[(self.active[uuid][0], "files_without_prediction")] += 1

    def identity_comparison(self, artist, references, predicted, has_predictions, resolved):
        """Score one show without borrowing expected memberships for prediction."""
        def identity(key):
            return self.identity_aliases.get((artist, key), key) if resolved else key

        expected_sets = {tuple(sorted({identity(key) for key in item["expected"]})) for item in references}
        expected = set(next(iter(expected_sets))) if len(expected_sets) == 1 else set()
        predicted = {identity(key) for key in predicted}
        status = ("missing_reference" if not references else "conflicting_reference" if len(expected_sets) != 1
                  else "empty_reference" if not expected else "missing_predictions" if not has_predictions else "compared")
        return {"status": status, "expected": sorted(expected), "predicted": sorted(predicted),
                "missing": sorted(expected - predicted), "extra": sorted(predicted - expected),
                "intersection": len(expected & predicted)}

    @staticmethod
    def count_comparison(count, comparison):
        count["shows"] += 1
        count[comparison["status"]] += 1
        if comparison["status"] == "compared":
            for name in ("expected", "predicted", "missing", "extra"):
                count[name] += len(comparison[name])
            count["intersection"] += comparison["intersection"]
            count["exact_presence_agreement"] += not comparison["missing"] and not comparison["extra"]

    @staticmethod
    def summarize(count):
        return {**{name: count[name] for name in ("shows", "compared", "expected", "predicted", "intersection",
                                                "missing", "extra", "exact_presence_agreement")}, **count,
                "songPresencePrecision": count["intersection"] / count["predicted"] if count["predicted"] else None,
                "songPresenceRecall": count["intersection"] / count["expected"] if count["expected"] else None}

    def publish(self):
        totals = {artist: Counter() for artist in ARTISTS}
        identity_totals = {artist: {name: Counter() for name in ("rawIdentity", "resolvedIdentity")} for artist in ARTISTS}

        def song_details(keys, current=False):
            result = []
            for key in keys:
                song = self.known.get(key, {})
                if current:
                    song = self.replay_songs.get(key, song)
                result.append(dict(entityKey=key, title=song.get("title"), legacySongUuid=song.get("legacy_uuid")))
            return result

        errors = self.output / "errors.jsonl"
        items = self.output / "shows.jsonl"
        with errors.open("wb") as error_stream, items.open("wb") as show_stream:
            for show, artist in sorted(self.shows.items()):
                self.check()
                files = self.files.get(show, {})
                references = self.references.get(show, [])
                sets = {tuple(item["expected"]) for item in references}
                predicted = files.get("predicted", set())
                reference = set(next(iter(sets))) if len(sets) == 1 else set()
                status = ("missing_reference" if not references else "conflicting_reference" if len(sets) != 1
                          else "empty_reference" if not reference else "missing_predictions" if not files else "compared")
                missing, extra = sorted(reference - predicted), sorted(predicted - reference)
                row = {"key": [artist, show], "artistUuid": artist, "showUuid": show, "status": status,
                       "referenceAccounts": references, "expected": sorted(reference), "predicted": sorted(predicted),
                       "missing": missing, "extra": extra, "missingSongs": song_details(missing),
                       "extraSongs": song_details(extra), "fileCount": files.get("fileCount", 0),
                       "sourceCount": len(files.get("sources", set())),
                       "capturedActiveFiles": self.show_population[show]["files"],
                       "missingFilePredictions": self.show_population[show]["files"] - files.get("fileCount", 0),
                       "capturedRecordingSources": len(self.show_population[show]["sources"]),
                       "unresolvedSegments": files.get("unresolvedSegments", 0)}
                for name, resolved in (("rawIdentity", False), ("resolvedIdentity", True)):
                    predictions = files.get("resolvedPredicted", predicted) if resolved else files.get("rawPredicted", set())
                    comparison = self.identity_comparison(artist, references, predictions,
                                                          has_predictions=bool(files), resolved=resolved)
                    comparison["missingSongs"] = song_details(comparison["missing"], current=resolved)
                    comparison["extraSongs"] = song_details(comparison["extra"], current=resolved)
                    row[name] = comparison
                    self.count_comparison(identity_totals[artist][name], comparison)
                compared_keys = predicted | files.get("resolvedPredicted", set()) | files.get("rawPredicted", set()) | {key for item in references for key in item["expected"]}
                row["identityResolutions"] = [{"originalKey": key, "targetKey": self.identity_aliases[(artist, key)]}
                    for key in sorted(compared_keys) if (artist, key) in self.identity_aliases and self.identity_aliases[(artist, key)] != key]
                self.write(show_stream, row)
                count = totals[artist]
                count["shows"] += 1; count[status] += 1
                if status == "compared":
                    count["expected"] += len(reference); count["predicted"] += len(predicted)
                    count["intersection"] += len(reference & predicted)
                    count["missing"] += len(missing); count["extra"] += len(extra)
                    count["exact_presence_agreement"] += not missing and not extra
                    count["provisional_prediction_memberships"] += sum(not self.known.get(key, {}).get("legacy_uuid") for key in predicted)
                resolved = row["resolvedIdentity"]
                if missing or extra or status != "compared" or resolved["missing"] or resolved["extra"] or resolved["status"] != "compared":
                    row["filesForReview"] = files.get("resolvedReviewFiles" if self.replay else "reviewFiles", [])
                    if self.replay:
                        row["legacyFilesForReview"] = files.get("reviewFiles", [])
                    self.write(error_stream, row)
            for reference in self.unaligned:
                self.write(error_stream, {"key": [reference["artistUuid"], "setlist", reference["setlistUuid"]],
                                          "status": "unaligned_reference", "reference": reference})
            for uuid in self.unprojected:
                self.write(error_stream, {"key": ["unprojected_reference", uuid], "status": "unprojected_reference"})
        results = []
        for artist, name in ARTISTS.items():
            count = totals[artist]
            results.append({"artistUuid": artist, "artist": name, **count,
                "activeFiles": sum(v[0] == artist for v in self.active.values()),
                **{key: value for (owner, key), value in self.coverage.items() if owner == artist},
                "songPresencePrecision": count["intersection"] / count["predicted"] if count["predicted"] else None,
                "songPresenceRecall": count["intersection"] / count["expected"] if count["expected"] else None,
                **{name: self.summarize(counts) for name, counts in identity_totals[artist].items()}})
        return {"artists": results, "referenceAccounts": self.reference_accounts,
                "unalignedReferenceAccounts": len(self.unaligned), "unprojectedReferenceAccounts": len(self.unprojected),
                "outputs": [artifact(items), artifact(errors)]}

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False)
        self.unaligned = []
        try:
            self.load_population()
            print(f"Population: {len(self.shows)} shows, {len(self.active)} active files", file=sys.stderr, flush=True)
            self.load_references()
            print(f"References: {self.reference_accounts} accounts, {len(self.references)} aligned shows", file=sys.stderr, flush=True)
            self.load_replayed_files()
            self.load_predictions()
            print(f"Predictions: {len(self.files)} shows", file=sys.stderr, flush=True)
            report = self.publish()
            report.update(version=VERSION, state="completed", elapsedSeconds=time.monotonic() - self.started,
                inputs={name: artifact(self.request[name]) for name in ["metadata", "normalization", "rawSetlists"]},
                request=self.request, source=artifact(__file__), predictionGeneration=self.prediction_generation, selectedChunks=list(self.chunk_pins.values()),
                limitations=["Development-exposed whole-artist song-presence comparison, not release accuracy.",
                    "Raw identity scores use original proposed segment song keys; existing top-level scores retain post-consolidation targets.",
                    "Resolved identity scores map current predicted targets and original references through same-artist one-target proposed identity_alias correspondences only.",
                    "Identity correspondence proposals are development policy, not independent identity truth or accepted music.",
                    "Reference membership IDs were not provided to per-recording title predictions; candidate dictionary is shared.",
                    "Existing C# unique-exact-artist-date alignments are proposals; no Python date matching.",
                    "Missing/extra song presence may reflect incomplete recordings or reference omissions, not a false file label.",
                    "No order, repeats, timings, composition identity or accepted music is evaluated.",
                    "Show-level union can hide disagreement among recordings; file/source coverage is reported separately.",
                    "This whole-artist report excludes the separate native Phish per-file reference comparison."])
            if self.replay is not None:
                report["fileTitleReplay"] = dict(receipt=artifact(self.replay.receipt), generation=self.replay.stats(),
                    reviewed=False, predictionBasis="shared_file_title_replay_with_explicit_uncovered_legacy_fallback")
                report["limitations"].append("Resolved identity predictions replace covered legacy file segments with the shared frozen file-title replay; raw and existing top-level metrics remain the original baseline.")
            encoded = (json.dumps(report, ensure_ascii=False, indent=2) + "\n").encode()
            if self.written + len(encoded) > self.maximum_bytes:
                raise ValueError("Completion manifest exceeds remaining output allowance")
            (self.output / "manifest.json").write_bytes(encoded)
            return report
        except Exception:
            (self.output / "failure.json").write_text(json.dumps({"version": VERSION, "state": "failed",
                "elapsedSeconds": time.monotonic() - self.started, "completionPublished": False}) + "\n")
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--file-title-replay", type=Path, help="Completed replay projection receipt used by the reader")
    args = parser.parse_args()
    request = json.loads(args.request.read_text())
    if args.file_title_replay:
        request["fileTitleReplay"] = str(args.file_title_replay.resolve(strict=True))
    report = Benchmark(request, args.output).run()
    print(json.dumps({"state": report["state"], "artists": report["artists"], "output": str(args.output)}))


if __name__ == "__main__":
    main()
