"""Export original rendition context for exact, scoped composition targets.

Music normalization owns title correspondences; existing reader indexes own
current record locators. This exporter reads those indexes without creating or
upgrading them. It preserves recording, file and setlist scopes, and copies no
claims from live research. Original text is stored once by its UTF-8 hash.
"""

import argparse
from collections import defaultdict
from contextlib import closing
import gzip
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import uuid

from music_catalogue import CatalogueBuildPaused
from music_consolidation import sha256_file
from shadow_reader import _has_domain, _unpack_text
from shadow_music import recording_account_key


VERSION = "composition-target-context-v1"
MAXIMUM_LINE = 32 * 1024**2


def encoded(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()


def key_of(value):
    return value["scope"], str(uuid.UUID(value["artistUuid"])), value["songEntityKey"]


class ReaderSnapshot:
    """Use the captured reader's existing compact tables in a read transaction."""

    def __init__(self, source):
        self.source = source
        self.run = Path(source["run"]).resolve(strict=True)
        self.database = Path(source["index"]).resolve(strict=True)
        self.db = sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)
        self.db.row_factory = sqlite3.Row
        self.db.create_function("key_text", 1, _unpack_text, deterministic=True)
        self.db.create_function("has_domain", 2, _has_domain, deterministic=True)
        self.db.execute("PRAGMA temp_store=MEMORY")
        self.db.execute("BEGIN")
        if self.db.execute("SELECT 1 FROM chunks WHERE state!='indexed' LIMIT 1").fetchone():
            self.close()
            raise ValueError("Context requires a complete available reader image")

    def close(self):
        self.db.close()

    def select(self, targets):
        self.db.execute("CREATE TEMP TABLE wanted(target INTEGER,artist TEXT,key TEXT,digest BLOB)")
        self.db.executemany("INSERT INTO wanted VALUES(?,?,?,?)", ((number, target["key"]["artistUuid"], key,
            hashlib.blake2b(key.encode(), digest_size=16).digest()) for number, target in enumerate(targets)
            if target["key"]["scope"] == self.source["scope"] for key in target["keys"]))
        # Exact memberships, not titles or dates, retrieve candidate witnesses.
        query = """SELECT DISTINCT w.target,w.key,r.id,r.input_id,d.name domain,c.path,c.bytes,c.mtime_ns,
            r.line_no,h.input_uuid,a.uuid artist_uuid,k.value input_kind,
            key_text((SELECT data FROM input_keys WHERE id=r.input_id)) input_key,NULL ordinal,NULL native_key
            FROM wanted w CROSS JOIN entities e ON e.digest=w.digest AND key_text(e.data)=w.key
            CROSS JOIN input_entities m INDEXED BY entity_lookup ON m.entity_id=e.id
            CROSS JOIN records r ON r.input_id=m.input_id AND has_domain(m.domains,r.domain_id)
            CROSS JOIN headers h ON h.id=r.header_id CROSS JOIN artists a ON a.id=h.artist_id
            CROSS JOIN terms k ON k.id=h.kind_id CROSS JOIN domains d ON d.id=r.domain_id
            CROSS JOIN chunks c ON c.id=r.chunk_id
            WHERE a.uuid=w.artist AND (d.name='setlists' OR (d.name='file_contents' AND k.value!='provider_input'))"""
        yield from self.db.execute(query)
        if self.db.execute("SELECT 1 FROM sqlite_master WHERE name='native_files'").fetchone():
            yield from self.db.execute("""SELECT DISTINCT w.target,w.key,r.id,r.input_id,'file_contents' domain,
                c.path,c.bytes,c.mtime_ns,r.line_no,h.input_uuid,a.uuid artist_uuid,k.value input_kind,
                key_text((SELECT data FROM input_keys WHERE id=r.input_id)) input_key,
                f.file_ordinal ordinal,key_text(n.data) native_key
                FROM wanted w CROSS JOIN entities e ON e.digest=w.digest AND key_text(e.data)=w.key
                CROSS JOIN native_file_entities m INDEXED BY native_file_entity_lookup ON m.entity_id=e.id
                CROSS JOIN native_files f ON f.id=m.file_id CROSS JOIN records r ON r.id=f.parent_id
                CROSS JOIN headers h ON h.id=r.header_id CROSS JOIN artists a ON a.id=h.artist_id
                CROSS JOIN terms k ON k.id=h.kind_id CROSS JOIN chunks c ON c.id=r.chunk_id
                CROSS JOIN entities n ON n.id=f.native_key_id WHERE a.uuid=w.artist""")

    def sibling(self, input_id, domain):
        return self.db.execute("""SELECT r.id,r.input_id,d.name domain,c.path,c.bytes,c.mtime_ns,r.line_no,
            h.input_uuid,a.uuid artist_uuid,k.value input_kind,
            key_text((SELECT data FROM input_keys WHERE id=r.input_id)) input_key FROM records r
            JOIN domains d ON d.id=r.domain_id JOIN chunks c ON c.id=r.chunk_id
            JOIN headers h ON h.id=r.header_id JOIN artists a ON a.id=h.artist_id
            JOIN terms k ON k.id=h.kind_id WHERE r.input_id=? AND d.name=?""", (input_id, domain)).fetchone()

    def source_record(self, source_uuid):
        literal = "source_sample:" + source_uuid
        row = self.db.execute("SELECT id FROM input_keys WHERE digest=? AND key_text(data)=?",
            (hashlib.blake2b(literal.encode(), digest_size=16).digest(), literal)).fetchone()
        return self.sibling(row[0], "people") if row else None


class CompositionTargetContextExporter:
    """Publish a complete context bundle, or leave no completion manifest."""

    def __init__(self, selection_plan, normalization, raw_manifest, destination, *,
                 plan_sha256, raw_manifest_sha256, minimum_free_bytes=int(5.5 * 1024**3),
                 maximum_bytes=256 * 1024**2, maximum_read_bytes=8 * 1024**3):
        self.plan_path = Path(selection_plan).resolve(strict=True)
        self.normalization = Path(normalization).resolve(strict=True)
        self.raw_path = Path(raw_manifest).resolve(strict=True)
        self.destination = Path(destination).resolve()
        self.plan_sha, self.raw_sha = plan_sha256, raw_manifest_sha256
        self.minimum_free_bytes, self.maximum_bytes = minimum_free_bytes, maximum_bytes
        self.maximum_read_bytes = maximum_read_bytes
        self.pins, self.manifests, self.chunk_pins = {}, {}, {}
        self.read_bytes, self.output_bytes, self.witness_bytes = 0, 0, 0
        self.texts, self.requests, self.recordings = set(), defaultdict(dict), defaultdict(set)
        self.seen_witnesses = defaultdict(set)
        self.readers = []
        self.source_documents = {}

    def export(self):
        self._inputs()
        self.destination.mkdir(parents=True, exist_ok=False)
        self._budget()
        try:
            with gzip.open(self.destination / "texts.jsonl.gz.writing", "wb") as self.text_stream:
                self._select_witnesses()
                self._read_requests()
                self._select_recordings()
                self._read_requests()
                self._artists()
            targets_path = self.destination / "targets.jsonl.gz.writing"
            with gzip.open(targets_path, "wb") as stream:
                for target in self.targets:
                    witnesses = target["witnesses"]
                    target["coverage"].update(fileWitnesses=sum(w["kind"] in {"catalog_file", "native_file"} for w in witnesses),
                        recordingContexts=sum(w["kind"] in {"catalog_recording", "native_recording"} for w in witnesses),
                        setlistWitnesses=sum(w["kind"] in {"legacy_setlist_membership", "native_passage"} for w in witnesses),
                        missingArtistMbid=target["artist"]["musicBrainzId"] is None, omittedWitnesses=0, complete=True)
                    value = {key: target[key] for key in ("key", "artist", "coverage", "witnesses")}
                    self._write(stream, value)
            self._verify_pins()
            for name in ("targets", "texts"):
                (self.destination / (name + ".jsonl.gz.writing")).rename(self.destination / (name + ".jsonl.gz"))
            manifest = {"version": VERSION, "state": "completed", "complete": True,
                "selectionPlan": self.plan_artifact, "hypothesisManifest": self.hypothesis_artifact,
                "musicInputManifest": self.hypothesis["inputs"]["musicInputManifest"],
                "musicOutputManifest": self.hypothesis["inputs"]["musicOutputManifest"],
                "catalogue": self.music["catalogue"], "normalization": self.normalization_artifact,
                "targets": self._artifact(self.destination / "targets.jsonl.gz", len(self.targets), relative=True),
                "texts": self._artifact(self.destination / "texts.jsonl.gz", len(self.texts), relative=True),
                "sourceManifests": list(self.manifests.values()), "witnessChunks": list(self.chunk_pins.values()),
                "targetCount": len(self.targets), "exporterVersion": VERSION, "omittedWitnesses": 0}
            (self.destination / "manifest.json.writing").write_bytes(encoded(manifest) + b"\n")
            self._budget()
            (self.destination / "manifest.json.writing").rename(self.destination / "manifest.json")
            return manifest
        except BaseException:
            # This directory was newly created by this invocation. A failed export
            # owns its partial files; source files and completed bundles never change.
            for path in self.destination.iterdir():
                path.unlink()
            raise
        finally:
            for reader in self.readers:
                reader.close()

    def _inputs(self):
        self.plan = self._manifest(self.plan_path, self.plan_sha)
        self.plan_artifact = self._artifact(self.plan_path, 1)
        if self.plan.get("version") != "composition-selection-plan-v1":
            raise ValueError("Use a pinned composition selection plan")
        self.hypothesis_artifact = self.plan["hypothesisManifest"]
        self.hypothesis = self._manifest_artifact(self.hypothesis_artifact)
        if self.hypothesis.get("complete") is not True:
            raise ValueError("Context requires a completed hypothesis image")
        self.music = self._manifest_artifact(self.hypothesis["inputs"]["musicInputManifest"])
        output = self._manifest_artifact(self.hypothesis["inputs"]["musicOutputManifest"])
        if output["inputManifestSha256"] != self.hypothesis["inputs"]["musicInputManifest"]["sha256"]:
            raise ValueError("The music input and output generations differ")
        self._pin(self.music["catalogue"])
        self.normalization_artifact = self._artifact(self.normalization, 1)
        self.pins[self.normalization] = self.normalization_artifact["sha256"]
        self.raw = self._manifest(self.raw_path, self.raw_sha)
        self.targets = []
        seen = set()
        with closing(sqlite3.connect(self.normalization.as_uri() + "?mode=ro&immutable=1", uri=True)) as db:
            metadata = {key: json.loads(value) for key, value in db.execute("SELECT key,value FROM metadata")}
            if (metadata.get("state") != "completed" or metadata["inputManifestSha256"] != self.hypothesis["inputs"]["musicInputManifest"]["sha256"]
                    or metadata["outputManifestSha256"] != self.hypothesis["inputs"]["musicOutputManifest"]["sha256"]
                    or metadata["catalogueSha256"] != self.music["catalogue"]["sha256"]):
                raise ValueError("Context requires the exact normalized music image")
            with closing(sqlite3.connect(Path(self.music["catalogue"]["path"]).as_uri() + "?mode=ro&immutable=1", uri=True)) as catalogue:
                for item in self.plan["songs"]:
                    scope, artist, song = key_of(item)
                    if (scope, artist, song) in seen:
                        raise ValueError("A context target is repeated")
                    seen.add((scope, artist, song))
                    if not db.execute("SELECT 1 FROM groups WHERE scope=? AND artist_uuid=? AND entity_key=?", (scope, artist, song)).fetchone():
                        raise ValueError("A context target does not belong to this scoped artist")
                    keys = {row[0] for row in db.execute("SELECT DISTINCT m.original_key FROM members m JOIN originals o "
                        "ON o.scope=m.scope AND o.original_key=m.original_key WHERE m.scope=? AND m.target_key=? "
                        "AND o.artist_uuid=? AND m.disposition='proposed'", (scope, song, artist))}
                    name = catalogue.execute("SELECT name FROM artists WHERE scope=? AND uuid=?", (scope, artist)).fetchone()
                    self.targets.append({"key": dict(scope=scope, artistUuid=artist, songEntityKey=song), "keys": keys,
                        "artist": {"name": name[0] if name else None, "musicBrainzId": None, "observations": []},
                        "coverage": {"missingRecordingContexts": 0}, "witnesses": []})
        if not 1 <= len(self.targets) <= 100_000:
            raise ValueError("Use 1–100000 distinct scoped targets")

    def _select_witnesses(self):
        for source in self.music["sources"]:
            if source["scope"] not in {t["key"]["scope"] for t in self.targets}:
                continue
            reader = ReaderSnapshot(source)
            self.readers.append(reader)
            self.source_documents[reader.run] = self._manifest(reader.run / "manifest.json")
            for row in reader.select(self.targets):
                self._request(reader, row, row["target"], row["key"], row["ordinal"], row["native_key"])
            self._budget()

    def _request(self, reader, row, target, key=None, ordinal=None, native_key=None, recording=None):
        locator = (reader.run, row["path"], row["line_no"])
        request_key = (target, ordinal, native_key, recording)
        request = self.requests[locator].setdefault(request_key, {"reader": reader, "row": dict(row),
            "target": target, "ordinal": ordinal, "nativeKey": native_key, "recording": recording, "keys": set()})
        if key:
            request["keys"].add(key)

    def _read_requests(self):
        requests, self.requests = self.requests, defaultdict(dict)
        by_chunk = defaultdict(dict)
        for (run, chunk, line), selected in requests.items():
            by_chunk[(run, chunk)][line] = selected
        for (run, chunk), lines in sorted(by_chunk.items()):
            path = (run / chunk).resolve(strict=True)
            if not path.is_relative_to(run) or path.is_symlink():
                raise ValueError("A retained context chunk escapes its run")
            if str(path) in self.chunk_pins:
                raise ValueError("A context phase would decode the same immutable chunk twice")
            before = sha256_file(path)
            count, found = 0, set()
            with gzip.open(path, "rb") as stream:
                while line := stream.readline(MAXIMUM_LINE + 1):
                    count += 1
                    self.read_bytes += len(line)
                    if len(line) > MAXIMUM_LINE or self.read_bytes > self.maximum_read_bytes or not line.endswith(b"\n"):
                        raise ValueError("Context input exceeds the complete-line or read-byte bound")
                    if count in lines:
                        record = json.loads(line)
                        for request in lines[count].values():
                            row = request["row"]
                            if (row["bytes"], row["mtime_ns"]) != (path.stat().st_size, path.stat().st_mtime_ns):
                                raise ValueError("A selected reader locator changed")
                            if (record["domain"], record["artistUuid"], record["inputKey"]) != (row["domain"], row["artist_uuid"], row["input_key"]):
                                raise ValueError("A selected context changes domain or artist ownership")
                            origin = dict(run=str(run), chunk=chunk, line=count, sha256=before, jsonPointer="")
                            self._project_record(record, request, origin)
                        found.add(count)
            if found != set(lines) or sha256_file(path) != before:
                raise ValueError("A selected context is missing or changed")
            self.pins[path] = before
            self.chunk_pins[str(path)] = self._artifact(path, count, run=run)
            self._budget()

    def _project_record(self, record, request, origin):
        if request["recording"]:
            return self._recording(record, request, origin)
        if record["domain"] == "setlists":
            return self._setlists(record, request, origin)
        return self._file(record, request, origin)

    def _file(self, record, request, origin):
        number = request["target"]
        proposal = record.get("proposal") or {}
        ordinal = request["ordinal"]
        if ordinal is not None:
            child = proposal["files"][ordinal]
            if child["fileOrdinal"] != ordinal or child["entityKey"] != request["nativeKey"]:
                raise ValueError("A native file changed its retained ordinal or key")
            keys = request["keys"] & set(child.get("entityKeys", []))
            if not keys:
                raise ValueError("A native file lacks its indexed song membership")
            pointer = "/proposal/files/" + str(ordinal)
            home = {"fileUuid": child.get("catalogTrackUuid"), "sourceUuid": child.get("catalogSourceUuid"), "showUuid": child.get("catalogShowUuid")}
            recording_key = recording_account_key(record["inputUuid"], child)
            witness = self._witness("native_file", origin, pointer, keys, **home, nativeFileKey=child["entityKey"], recordingKey=recording_key)
            self._observed_texts(witness, (child.get("file") or {}).get("text", []), pointer + "/file/text")
            self._literal(witness, "title", (child.get("file", {}).get("title") or {}).get("value"), pointer + "/file/title/value")
            self.recordings[(request["reader"], request["row"]["input_id"], recording_key, json.dumps(child["recordingOrigin"], sort_keys=True))].add(number)
        else:
            keys = request["keys"] & set(record.get("entityKeys", []))
            if not keys:
                raise ValueError("A file lacks its indexed song membership")
            home = proposal.get("home") or {}
            if home.get("fileUuid") != record["inputUuid"]:
                raise ValueError("A catalog file changes its original file UUID")
            witness = self._witness("catalog_file", origin, "/proposal", keys,
                **{name: home.get(name) for name in ("fileUuid", "sourceUuid", "showUuid")},
                recordingKey="source:" + home["sourceUuid"] if home.get("sourceUuid") else None)
            self._literal(witness, "title", (proposal.get("titleInterpretation") or {}).get("originalTitle"), "/proposal/titleInterpretation/originalTitle")
            self._literal(witness, "catalog_display_date", home.get("literalDate"), "/proposal/home/literalDate")
            if proposal.get("titleInterpretation") is not None:
                self._claim(witness, "retained_repertoire_interpretation", proposal["titleInterpretation"], "/proposal/titleInterpretation")
            if home.get("sourceUuid"):
                self.recordings[(None, (self.targets[number]["key"]["scope"], home["sourceUuid"]), None, None)].add(number)
            else:
                witness["missingFields"].append("catalog_source_uuid")
                self.targets[number]["coverage"]["missingRecordingContexts"] += 1
        self._add(number, witness)

    def _select_recordings(self):
        for (reader, identity, recording_key, origin_json), targets in self.recordings.items():
            if reader is not None:
                row = reader.sibling(identity, "performances")
                if row:
                    for number in targets:
                        self._request(reader, row, number, recording=json.dumps([recording_key, json.loads(origin_json)]))
                else:
                    for number in targets:
                        self.targets[number]["coverage"]["missingRecordingContexts"] += 1
            else:
                # Catalog ownership is explicit in a file. The source sample's
                # people outcome retains all original fields even with zero actors.
                scope, source_uuid = identity
                candidates = [(candidate, candidate.source_record(source_uuid)) for candidate in self.readers
                              if candidate.source["scope"] == scope]
                matches = [(candidate, row) for candidate, row in candidates if row is not None]
                if not matches:
                    for number in targets:
                        self.targets[number]["coverage"]["missingRecordingContexts"] += 1
                for candidate, row in matches:
                    for number in targets:
                        if row["artist_uuid"] != self.targets[number]["key"]["artistUuid"]:
                            raise ValueError("A recording context changes the file's catalog artist")
                        self._request(candidate, row, number, recording=source_uuid)

    def _recording(self, record, request, origin):
        identity = request["recording"]
        if record["domain"] == "people":
            if record["inputUuid"] != identity:
                raise ValueError("A recording context changes its source UUID")
            witness = self._witness("catalog_recording", origin, "/evidence/originalInputs", [],
                sourceUuid=identity, recordingKey="source:" + identity)
            fields = (record.get("evidence") or {}).get("originalInputs")
            if not isinstance(fields, dict):
                raise ValueError("A source outcome lacks its retained input fields")
            for field, value in fields.items():
                self._literal(witness, field, value, "/evidence/originalInputs/" + field)
        else:
            recording_key, desired_origin = json.loads(identity)
            accounts = record.get("proposal") or []
            accounts = accounts if isinstance(accounts, list) else [accounts]
            matches = [(index, account) for index, account in enumerate(accounts) if account.get("origin") == desired_origin]
            if len(matches) != 1:
                raise ValueError("A native file has no unique original recording scope")
            index, account = matches[0]
            pointer = "/proposal" + ("/" + str(index) if isinstance(record.get("proposal"), list) else "") + "/recording"
            recording = account.get("recording") or {}
            witness = self._witness("native_recording", origin, pointer, [], recordingKey=recording_key)
            self._observed_texts(witness, recording.get("text", []), pointer + "/text")
            for index, performer in enumerate(recording.get("performers", [])):
                self._claim(witness, "provider_performer", performer, pointer + "/performers/" + str(index))
            if not recording:
                witness["missingFields"].append("native_recording_observation")
        self._add(request["target"], witness)

    def _setlists(self, record, request, origin):
        proposals = record.get("proposal") or []
        accounts = proposals if isinstance(proposals, list) else [proposals]
        for account_number, account in enumerate(accounts):
            pointer = "/proposal" + ("/" + str(account_number) if isinstance(proposals, list) else "")
            if record["inputKind"] == "setlist_sample":
                for ordinal, member in enumerate(account.get("memberships", [])):
                    if member.get("songEntityKey") not in request["keys"]:
                        continue
                    witness = self._witness("legacy_setlist_membership", origin, pointer + "/memberships/" + str(ordinal),
                        [member["songEntityKey"]], setlistKey=account["entityKey"], showUuid=account.get("showUuid"), ordered=False)
                    self._literal(witness, "title", member.get("title"), pointer + "/memberships/" + str(ordinal) + "/title")
                    self._literal(witness, "setlist_date", account.get("literalDate"), pointer + "/literalDate")
                    self._claim(witness, "legacy_membership", member, pointer + "/memberships/" + str(ordinal))
                    witness["missingFields"].extend(["provider_annotations_not_retained", "musical_order_not_retained"])
                    self._add(request["target"], witness)
            else:
                for ordinal, passage in enumerate(account.get("occurrences", [])):
                    if passage.get("songEntityKey") not in request["keys"]:
                        continue
                    passage_pointer = pointer + "/occurrences/" + str(ordinal)
                    witness = self._witness("native_passage", origin, passage_pointer, [passage["songEntityKey"]],
                        setlistKey=account["entityKey"], showUuid=account.get("showUuid"), ordered=account.get("ordered"))
                    observation = passage.get("observation") or {}
                    self._literal(witness, "title", passage.get("title"), passage_pointer + "/title")
                    self._observed_texts(witness, observation.get("annotations", []), passage_pointer + "/observation/annotations")
                    for index, claim in enumerate(observation.get("performerAnnotations", [])):
                        self._claim(witness, "performance_annotation", claim, passage_pointer + "/observation/performerAnnotations/" + str(index))
                    for index, reference in enumerate(observation.get("footnoteNumbers", [])):
                        self._claim(witness, "footnote_reference", reference, passage_pointer + "/observation/footnoteNumbers/" + str(index))
                    provider = account.get("providerAccount") or {}
                    for index, footnote in enumerate(provider.get("footnotes", [])):
                        footnote_pointer = pointer + "/providerAccount/footnotes/" + str(index)
                        self._observed_text(witness, footnote["text"], footnote_pointer + "/text")
                        if footnote.get("number") is not None:
                            self._claim(witness, "footnote_number", footnote["number"], footnote_pointer + "/number")
                    for index, section in enumerate(provider.get("sets", [])):
                        if passage.get("setOrdinal") is not None and section.get("ordinal") == passage["setOrdinal"]:
                            self._observed_texts(witness, section.get("annotations") or [], pointer + "/providerAccount/sets/" + str(index) + "/annotations")
                    self._observed_texts(witness, provider.get("text", []), pointer + "/providerAccount/text")
                    for index, performer in enumerate(provider.get("performers", [])):
                        self._claim(witness, "provider_performer", performer, pointer + "/providerAccount/performers/" + str(index))
                    self._add(request["target"], witness)

    @staticmethod
    def _witness(kind, origin, pointer, keys, **coordinates):
        return dict(kind=kind, origin={**origin, "jsonPointer": pointer}, matchingRepertoireKeys=sorted(keys),
                    texts=[], claims=[], missingFields=[], **coordinates)

    def _literal(self, witness, field, value, pointer, format=None):
        if value is None:
            witness["missingFields"].append(field)
            return
        if not isinstance(value, str):
            raise ValueError("A retained text field is not a string or null")
        digest = hashlib.sha256(value.encode()).hexdigest()
        if digest not in self.texts:
            self._write(self.text_stream, dict(sha256=digest, value=value))
            self.texts.add(digest)
        witness["texts"].append(dict(field=field, textSha256=digest,
            origin={**witness["origin"], "jsonPointer": pointer}, format=format))

    def _observed_texts(self, witness, values, pointer):
        for index, item in enumerate(values):
            self._observed_text(witness, item, pointer + "/" + str(index))

    def _observed_text(self, witness, item, pointer):
        text = item.get("text") or {}
        self._literal(witness, item["field"], text.get("value"), pointer + "/text/value", item.get("format"))
        if text.get("evidence") is not None:
            self._claim(witness, "provider_text_evidence", text["evidence"], pointer + "/text/evidence")

    @staticmethod
    def _claim(witness, kind, observation, pointer):
        witness["claims"].append(dict(kind=kind, observation=observation,
            origin={**witness["origin"], "jsonPointer": pointer}))

    def _add(self, number, witness):
        identity = encoded([witness["kind"], witness["origin"]])
        if identity not in self.seen_witnesses[number]:
            self.witness_bytes += len(encoded(witness))
            self.seen_witnesses[number].add(identity)
            self.targets[number]["witnesses"].append(witness)
            self._budget()

    def _artists(self):
        plans = {item["id"]: item for item in self.raw["plan"]}
        shard = next(item for item in self.raw["completedShards"] if plans[item["shardId"]]["domain"] == "sources")
        path = (self.raw_path.parent / shard["gzipPath"]).resolve(strict=True)
        if not any(path == Path(item).resolve() for document in self.source_documents.values()
                   for item in document.get("population", {}).get("snapshotPaths", [])):
            raise ValueError("Artist headers are outside the retained music source population")
        before = sha256_file(path)
        if before != shard["compressed"]["sha256"]:
            raise ValueError("The captured artist-header shard changed")
        desired = defaultdict(list)
        for target in self.targets:
            desired[target["key"]["artistUuid"]].append(target)
        count, header = 0, True
        with gzip.open(path, "rb") as stream:
            while line := stream.readline(MAXIMUM_LINE + 1):
                count += 1
                self.read_bytes += len(line)
                if len(line) > MAXIMUM_LINE or self.read_bytes > self.maximum_read_bytes or not line.endswith(b"\n"):
                    raise ValueError("Artist headers exceed the context read bound")
                if header:
                    row = json.loads(line)
                    header = row.get("kind") != "source_sample"
                    if row.get("kind") == "artist" and row["row"]["uuid"] in desired:
                        artist = row["row"]
                        for target in desired[artist["uuid"]]:
                            value = artist.get("musicbrainz_id")
                            # Legacy captures use empty strings for missing IDs. Keep the
                            # literal cell below, but expose only a parsed identifier here.
                            try:
                                identifier = str(uuid.UUID(value)) if isinstance(value, str) else None
                            except ValueError:
                                identifier = None
                            if identifier is not None and (identifier != value.lower() or uuid.UUID(identifier).int == 0):
                                identifier = None
                            target["artist"]["musicBrainzId"] = identifier
                            for field in ("name", "musicbrainz_id"):
                                target["artist"]["observations"].append(dict(field=field, value=artist.get(field),
                                    origin=dict(run=str(self.raw_path.parent), chunk=shard["gzipPath"], line=count,
                                                sha256=before, jsonPointer="/row/" + field)))
        if sha256_file(path) != before:
            raise ValueError("Artist headers changed during export")
        self.pins[path] = before
        self.chunk_pins[str(path)] = self._artifact(path, count, run=self.raw_path.parent)

    def _manifest(self, path, digest=None):
        path = Path(path).resolve(strict=True)
        raw = path.read_bytes()
        actual = hashlib.sha256(raw).hexdigest()
        if digest and actual != digest:
            raise ValueError("A context input manifest differs from its supplied pin")
        self.pins[path] = actual
        self.manifests[str(path)] = self._artifact(path, 1)
        return json.loads(raw)

    def _manifest_artifact(self, artifact):
        self._pin(artifact)
        return self._manifest(artifact["path"], artifact["sha256"])

    def _pin(self, artifact):
        path = Path(artifact["path"]).resolve(strict=True)
        if path.stat().st_size != artifact["bytes"] or sha256_file(path) != artifact["sha256"]:
            raise ValueError("A frozen context input changed")
        self.pins[path] = artifact["sha256"]

    def _artifact(self, path, rows, run=None, relative=False):
        result = dict(path=path.name if relative else str(path.resolve()), bytes=path.stat().st_size,
                      sha256=sha256_file(path), rows=rows)
        if run:
            result["run"] = str(run)
        return result

    def _verify_pins(self):
        self._budget()
        for path, digest in self.pins.items():
            if sha256_file(path) != digest:
                raise ValueError("A retained context source changed before completion")

    def _write(self, stream, value):
        data = encoded(value) + b"\n"
        self.output_bytes += len(data)
        self._budget()
        stream.write(data)

    def _budget(self):
        if shutil.disk_usage(self.destination.parent).free < self.minimum_free_bytes:
            raise CatalogueBuildPaused("Context export stopped to preserve free space")
        if max(self.output_bytes, self.witness_bytes) > self.maximum_bytes or (self.destination.exists() and sum(p.stat().st_size for p in self.destination.iterdir()) > self.maximum_bytes):
            raise CatalogueBuildPaused("Context export exceeds its byte bound; no complete context was published")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selection-plan", required=True)
    parser.add_argument("--plan-sha256", required=True)
    parser.add_argument("--normalization", required=True)
    parser.add_argument("--raw-manifest", required=True)
    parser.add_argument("--raw-manifest-sha256", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--minimum-free-bytes", type=int, default=int(5.5 * 1024**3))
    parser.add_argument("--maximum-bytes", type=int, default=256 * 1024**2)
    args = parser.parse_args()
    result = CompositionTargetContextExporter(args.selection_plan, args.normalization, args.raw_manifest, args.output,
        plan_sha256=args.plan_sha256, raw_manifest_sha256=args.raw_manifest_sha256,
        minimum_free_bytes=args.minimum_free_bytes, maximum_bytes=args.maximum_bytes).export()
    print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
