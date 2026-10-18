#!/usr/bin/env python3
"""Prepare cross-artist actor comparisons and attach their contextual proposals.

Candidate retrieval uses names/aliases to find questions, never to accept identity.
Questions preserve exact credited occurrences and bounded original prose, contacts,
roles and captured venue/date context. The C# people-context command owns Luna,
cache admission, evidence validation and spend. This module makes no API calls.
"""

import argparse
from collections import Counter, defaultdict
from contextlib import closing
import gzip
import hashlib
import itertools
import json
import math
from pathlib import Path
import re
import shutil
import sqlite3
from uuid import UUID

from people_directory import encoded, name_key, retained_actor_facts


class PeopleContextContinuationPreparation:
    """Authenticate a sealed run and copy only its unrecorded batches.

    The plan defines a new bounded job. It neither resumes the old ledger nor
    interprets recovered decisions as completed input batches. Input bytes remain
    unchanged so the execution command can verify the same pending population.
    """

    METHOD = "luna-people-context-v1"
    MINIMUM_FREE_BYTES = 5905580032

    def __init__(self, original_run, output, job_uuid):
        self.original_run = Path(original_run).resolve()
        self.output = Path(output).resolve()
        self.job_uuid = str(UUID(str(job_uuid)))
        if UUID(self.job_uuid).int == 0:
            raise ValueError("Choose a nonempty new job UUID")

    def prepare(self):
        if self.output.exists():
            raise FileExistsError(self.output)
        plan, pending_bytes = self._validated_plan()
        if shutil.disk_usage(self.output.parent).free < self.MINIMUM_FREE_BYTES:
            raise RuntimeError(
                "Preserve the 5.5 GiB host disk reserve before preparing continuation"
            )
        self.output.mkdir()
        Path(plan["pendingInputsPath"]).write_bytes(pending_bytes)
        # The plan is the completion marker; an interrupted copy cannot admit a job.
        (self.output / "plan.json").write_text(encoded(plan) + "\n", encoding="utf-8")
        return plan

    def _validated_plan(self):
        manifest_bytes = (self.original_run / "manifest.json").read_bytes()
        continuation_bytes = (self.original_run / "continuation.json").read_bytes()
        manifest = json.loads(manifest_bytes)
        continuation = json.loads(continuation_bytes)
        if (
            manifest["state"] not in {"cancelled", "completed_partial", "completed"}
            or manifest["method"] != self.METHOD
            or UUID(manifest["jobUuid"]) == UUID(self.job_uuid)
            or continuation["jobUuid"] != manifest["jobUuid"]
        ):
            raise ValueError("Use a sealed contextual run and a distinct new job UUID")

        input_path = self.original_run / "inputs.jsonl"
        input_bytes = input_path.read_bytes()
        results_bytes = (self.original_run / "results.jsonl").read_bytes()
        input_hash = hashlib.sha256(input_bytes).hexdigest()
        results_hash = hashlib.sha256(results_bytes).hexdigest()
        if (
            manifest["inputSha256"] != input_hash
            or manifest["resultsSha256"] != results_hash
            or continuation["inputSha256"] != input_hash
            or Path(continuation["inputPath"]).resolve() != input_path
        ):
            raise ValueError("Original inputs or results differ from their retained hashes")

        batches = self._read_inputs(input_bytes)
        completed = self._read_completed(results_bytes, batches)
        pending = set(batches) - completed
        recorded_questions = sum(
            len(batches[key][0]["questions"]) for key in completed
        )
        pending_questions = sum(len(batches[key][0]["questions"]) for key in pending)
        if (
            not pending
            or manifest["inputBatches"] != len(batches)
            or manifest["questions"] != recorded_questions + pending_questions
            or manifest["completedBatches"] != len(completed)
            or self._batch_ids(continuation["completedBatchIds"]) != completed
            or self._batch_ids(continuation["pendingBatchIds"]) != pending
            or continuation["recordedQuestions"] != recorded_questions
            or continuation["pendingQuestions"] != pending_questions
        ):
            raise ValueError("Continuation must exactly partition the original input batches")

        # Preserve line endings, Unicode and JSON formatting from the authenticated
        # input. Result arrival order must not reorder the pending population.
        pending_bytes = b"".join(
            line for key, (_, line) in batches.items() if key in pending
        )
        pending_path = self.output / "pending-inputs.jsonl"
        plan = {
            "version": "people-context-continuation-v1",
            "newJobUuid": self.job_uuid,
            "originalRun": str(self.original_run),
            "originalJobUuid": manifest["jobUuid"],
            "originalManifestSha256": hashlib.sha256(manifest_bytes).hexdigest(),
            "continuationSha256": hashlib.sha256(continuation_bytes).hexdigest(),
            "originalInputSha256": input_hash,
            "originalResultsSha256": results_hash,
            "pendingInputsPath": str(pending_path),
            "pendingInputsSha256": hashlib.sha256(pending_bytes).hexdigest(),
            "pendingBatches": len(pending),
            "pendingQuestions": pending_questions,
            "recordedBatches": len(completed),
            "recordedQuestions": recorded_questions,
            "capUsd": 10,
            "maximumTotalAttempts": 508,
            "maximumDurationSeconds": 1800,
            "minimumFreeBytes": self.MINIMUM_FREE_BYTES,
            "method": self.METHOD,
        }
        return plan, pending_bytes

    @staticmethod
    def _batch_ids(values):
        if not isinstance(values, list) or any(
            not isinstance(key, str) or not key.strip() for key in values
        ):
            raise ValueError("Expected nonempty batch IDs")
        if len(set(values)) != len(values):
            raise ValueError("Batch IDs must be unique")
        return set(values)

    @staticmethod
    def _read_inputs(data):
        batches = {}
        question_ids = set()
        for line in data.splitlines(keepends=True):
            batch = json.loads(line.decode("utf-8"))
            batch_id = batch["batchId"]
            questions = batch["questions"]
            if (
                not isinstance(batch_id, str)
                or not batch_id.strip()
                or batch_id in batches
                or not isinstance(questions, list)
                or not 1 <= len(questions) <= 10
            ):
                raise ValueError("Use unique batch IDs with 1–10 questions per batch")
            for question in questions:
                question_id = question["questionId"]
                if (
                    not isinstance(question_id, str)
                    or not question_id.strip()
                    or question_id in question_ids
                ):
                    raise ValueError(
                        "Question IDs must be nonempty and unique across inputs"
                    )
                question_ids.add(question_id)
            batches[batch_id] = (batch, line)
        if not 1 <= len(batches) <= 500 or len(question_ids) > 5000:
            raise ValueError("Use 1–500 original batches and at most 5,000 questions")
        return batches

    @staticmethod
    def _read_completed(data, batches):
        completed = set()
        for line in data.splitlines():
            row = json.loads(line.decode("utf-8"))
            batch_id = row["input"]["batchId"]
            if (
                batch_id not in batches
                or batch_id in completed
                or row["input"] != batches[batch_id][0]
                or row["result"]["batchId"] != batch_id
            ):
                raise ValueError("A result must retain its exact original input and batch ID")
            completed.add(batch_id)
        return completed


class PeopleContextPreparation:
    """Select a broad development population without interpreting matching answers."""

    def __init__(self, database, output, maximum=5000):
        self.database = Path(database).resolve()
        self.output = Path(output).resolve()
        self.maximum = maximum
        self.groups = {}
        self.samples = defaultdict(list)
        self.date_pairs = []
        self.candidates = {}
        self.actors = {}
        self.issues = Counter()

    def prepare(self):
        if self.output.exists():
            raise FileExistsError(self.output)
        if not 1 <= self.maximum <= 5000:
            raise ValueError("Choose 1–5000 contextual questions")
        if shutil.disk_usage(self.output.parent).free < 4.5 * 1024**3:
            raise RuntimeError(
                "Preserve the host disk reserve before preparing context"
            )
        self._read_directory()
        self._retrieve_candidates()
        witnesses = {
            row["id"]: row
            for pair in self.candidates.values()
            for row in (pair["left"], pair["right"])
        }
        self._read_actor_context(witnesses)
        self._read_venue_context()
        self._bound_context()
        self._add_contact_candidates()
        questions = self._select_questions()
        self.output.mkdir()
        with (self.output / "batches.jsonl").open("w", encoding="utf-8") as stream:
            for offset in range(0, len(questions), 10):
                stream.write(
                    encoded(
                        {
                            "batchId": f"people-context-v1-{offset // 10:04}",
                            "questions": questions[offset : offset + 10],
                        }
                    )
                    + "\n"
                )
        manifest = {
            "state": "prepared",
            "version": "people-context-preparation-v1",
            "directory": str(self.database),
            "sourceRun": str(self.core_run),
            "groups": len(self.groups),
            "retrievedPairs": len(self.candidates),
            "questions": len(questions),
            "sourceWitnesses": len(
                {
                    actor["sourceUuid"]
                    for question in questions
                    for actor in (question["left"], question["right"])
                }
            ),
            "artists": len(
                {
                    actor["artistUuid"]
                    for question in questions
                    for actor in (question["left"], question["right"])
                }
            ),
            "cohorts": dict(
                Counter(question["retrievalBasis"] for question in questions)
            ),
            "crossArtistQuestions": sum(
                question["left"]["artistUuid"] != question["right"]["artistUuid"]
                for question in questions
            ),
            "issues": dict(self.issues),
            "batchesSha256": hashlib.sha256(
                (self.output / "batches.jsonl").read_bytes()
            ).hexdigest(),
            "developmentExposure": "All selected witnesses are development inputs, not an independent accuracy evaluation. Candidate hints and model rationales do not accept people or prove exhaustive coverage.",
        }
        (self.output / "manifest.json").write_text(encoded(manifest) + "\n")
        return manifest

    def _read_directory(self):
        with closing(
            sqlite3.connect(self.database.as_uri() + "?mode=ro", uri=True)
        ) as db:
            db.row_factory = sqlite3.Row
            metadata = {
                row["key"]: json.loads(row["value"])
                for row in db.execute("SELECT * FROM metadata")
            }
            self.core_run = Path(metadata["sourceRun"])
            self.groups = {
                row["id"]: dict(row) for row in db.execute("SELECT * FROM groups")
            }
            self.names = defaultdict(set)
            for row in db.execute("SELECT group_id,lookup FROM names"):
                self.names[row["group_id"]].add(row["lookup"])
            previous_group = None
            dates = {}
            date_count = 0
            rows = db.execute("""SELECT c.*,a.name artist_name,h.literal_date
                FROM credits c JOIN artists a ON a.uuid=c.artist_uuid
                LEFT JOIN source_homes h ON h.source_uuid=c.source_uuid
                ORDER BY c.group_id,c.source_uuid,c.id""")
            for source in rows:
                row = dict(source)
                group = row["group_id"]
                if group != previous_group:
                    dates, date_count = {}, 0
                    previous_group = group
                samples = self.samples[group]
                if len(samples) < 4 and not any(
                    sample["artist_uuid"] == row["artist_uuid"] for sample in samples
                ):
                    samples.append(row)
                date = row["literal_date"] or ""
                if re.fullmatch(r"\d{4}-(?!00)\d{2}-(?!00)\d{2}", date):
                    other = dates.get(date)
                    if (
                        other
                        and other["artist_uuid"] != row["artist_uuid"]
                        and date_count < 2
                    ):
                        self.date_pairs.append((other, row))
                        date_count += 1
                    else:
                        dates.setdefault(date, row)
        print(
            encoded(
                {
                    "phase": "directory",
                    "groups": len(self.groups),
                    "sampledActors": sum(map(len, self.samples.values())),
                    "sameDateCandidates": len(self.date_pairs),
                }
            ),
            flush=True,
        )

    @staticmethod
    def _order(value):
        return hashlib.sha256(str(value).encode()).hexdigest()

    def _add(self, left, right, basis, priority):
        if left["source_uuid"] == right["source_uuid"]:
            return
        key = tuple(sorted((left["id"], right["id"])))
        current = self.candidates.get(key)
        if current is None or priority > current["priority"]:
            self.candidates[key] = {
                "left": left,
                "right": right,
                "basis": basis,
                "priority": priority,
            }

    def _pair(self, left_group, right_group, basis, priority):
        choices = list(
            itertools.product(self.samples[left_group], self.samples[right_group])
        )
        if choices:
            left, right = min(
                choices,
                key=lambda pair: (
                    pair[0]["artist_uuid"] == pair[1]["artist_uuid"],
                    pair[0]["id"],
                    pair[1]["id"],
                ),
            )
            self._add(left, right, basis, priority)

    def _retrieve_candidates(self):
        aliases = defaultdict(set)
        tokens = defaultdict(set)
        for group, names in self.names.items():
            for wording in names:
                aliases[wording].add(group)
                for token in set(re.findall(r"[^\W_]+", wording)):
                    if len(token) >= 3:
                        tokens[token].add(group)
        for groups in aliases.values():
            if 1 < len(groups) <= 30:
                for left, right in itertools.combinations(sorted(groups), 2):
                    self._pair(left, right, "explicit_alias_overlap", 8)
        lexical = []
        for group, names in self.names.items():
            rivals = Counter()
            for token in {
                token for name in names for token in re.findall(r"[^\W_]+", name)
            }:
                bucket = tokens.get(token, ())
                if 1 < len(bucket) <= 200:
                    for rival in bucket:
                        if rival != group:
                            rivals[rival] += math.log(
                                1 + len(self.groups) / len(bucket)
                            )
            for rival, score in rivals.most_common(4):
                lexical.append((self._order((group, rival)), group, rival, score))
        # Spread the development questions across names instead of spending the
        # entire trial on the most common taper or a single artist's variants.
        for _, left, right, score in sorted(lexical)[:8500]:
            self._pair(left, right, "name_variant_candidate", min(4, score / 10))
        multi = [
            (self._order(group), group)
            for group, samples in self.samples.items()
            if len(samples) > 1
        ]
        for _, group in sorted(multi)[:2500]:
            for left, right in itertools.combinations(self.samples[group][:3], 2):
                self._add(left, right, "same_name_cross_artist", 3)
        for left, right in sorted(
            self.date_pairs,
            key=lambda pair: self._order((pair[0]["id"], pair[1]["id"])),
        )[:1500]:
            self._add(left, right, "same_date_candidate", 6)
        print(
            encoded({"phase": "retrieval", "pairs": len(self.candidates)}), flush=True
        )

    @staticmethod
    def _excerpt(field, raw, start, end):
        return {
            "field": field,
            "quote": raw[start:end],
            "startCodePoint": start,
            "endCodePoint": end,
        }

    def _actor(self, row, record):
        fields = record.get("evidence", {}).get("originalInputs", {})
        credit = {
            "name": row["name"],
            "role": row["role"],
            "evidence": json.loads(row["evidence"]),
            "interpretation_basis": row["interpretation_basis"],
        }
        anchor = credit["evidence"]
        raw = fields.get(anchor["field"]) or ""
        start, end = anchor["startCodePoint"], anchor["endCodePoint"]
        if raw[start:end] != anchor["quote"]:
            self.issues["unsupported_credit_span"] += 1
            return None
        evidence = [anchor]
        evidence.append(
            self._excerpt(
                anchor["field"], raw, max(0, start - 600), min(len(raw), end + 600)
            )
        )
        for field in (
            "taper",
            "transferrer",
            "description",
            "source",
            "lineage",
            "taper_notes",
        ):
            value = fields.get(field) or ""
            if value:
                evidence.append(self._excerpt(field, value, 0, min(len(value), 700)))
                if field in ("description", "taper_notes") and len(value) > 700:
                    evidence.append(
                        self._excerpt(
                            field, value, max(700, len(value) - 700), len(value)
                        )
                    )
        facts = retained_actor_facts(record, credit)
        facts = {
            key: facts[key]
            for key in (
                "version",
                "aliases",
                "contacts",
                "profiles",
                "kind",
                "relationships",
                "unresolved",
            )
            if key in facts
        }
        unique = {
            (item["field"], item["startCodePoint"], item["endCodePoint"]): item
            for item in evidence
            if item["quote"]
        }
        return {
            "groupId": row["group_id"],
            "creditId": row["id"],
            "creditKey": row["credit_key"],
            "sourceUuid": row["source_uuid"],
            "artistUuid": row["artist_uuid"],
            "artistName": row["artist_name"],
            "name": row["name"],
            "roles": [row["role"]],
            "literalDate": row["literal_date"],
            "evidence": list(unique.values()),
            "actorFacts": facts or None,
        }

    def _read_actor_context(self, witnesses):
        by_chunk = defaultdict(lambda: defaultdict(list))
        for row in witnesses.values():
            run = Path(row["origin_run"]) if row["origin_run"] else self.core_run
            by_chunk[run / row["chunk"]][row["line"]].append(row)
        for index, (chunk, wanted) in enumerate(sorted(by_chunk.items())):
            with gzip.open(chunk, "rt", encoding="utf-8") as stream:
                for line, text in enumerate(stream, 1):
                    if line in wanted:
                        record = json.loads(text)
                        for row in wanted[line]:
                            if record["inputUuid"] != row["source_uuid"]:
                                raise ValueError(
                                    "Retained source locator changed ownership"
                                )
                            actor = self._actor(row, record)
                            if actor:
                                self.actors[row["id"]] = actor
            if index % 25 == 0:
                print(
                    encoded(
                        {
                            "phase": "actor_context",
                            "chunks": index + 1,
                            "actors": len(self.actors),
                        }
                    ),
                    flush=True,
                )

    def _read_venue_context(self):
        sources = defaultdict(list)
        for actor in self.actors.values():
            sources[actor["sourceUuid"]].append(actor)
        manifest = json.loads((self.core_run / "manifest.json").read_text())
        paths = [
            Path(path)
            for path in manifest["population"]["snapshotPaths"]
            if "-sources-" in Path(path).name
        ]
        for index, path in enumerate(paths):
            with gzip.open(path, "rt", encoding="utf-8") as stream:
                for text in stream:
                    record = json.loads(text)
                    if record.get("kind") != "source_sample":
                        continue
                    selected = sources.get(record["row"]["uuid"])
                    if not selected:
                        continue
                    venue = record.get("venue_context") or {}
                    for actor in selected:
                        for key in ("name", "location"):
                            value = venue.get(key)
                            if isinstance(value, str) and value:
                                actor["evidence"].append(
                                    self._excerpt(
                                        "catalog_venue_" + key, value, 0, len(value)
                                    )
                                )
                        if actor["literalDate"]:
                            value = actor["literalDate"]
                            actor["evidence"].append(
                                self._excerpt("catalog_date", value, 0, len(value))
                            )
            print(encoded({"phase": "venue_context", "shards": index + 1}), flush=True)

    def _bound_context(self):
        # Keep the exact credit and surrounding clause first, then compact catalog
        # context. Remaining original excerpts fill a fixed request budget. Omitted
        # prose remains in its source; absence in this view is not negative evidence.
        for credit_id, actor in list(self.actors.items()):
            evidence = actor["evidence"]
            required = evidence[:2] + [
                item for item in evidence if item["field"].startswith("catalog_")
            ]
            actor["evidence"] = required
            size = lambda: len(
                json.dumps(actor, ensure_ascii=True, separators=(",", ":")).encode()
            )
            if size() > 6500:
                self.issues["actor_context_too_large"] += 1
                del self.actors[credit_id]
                continue
            for item in evidence[2:]:
                if item in required:
                    continue
                actor["evidence"].append(item)
                if size() > 6500:
                    actor["evidence"].pop()

    def _add_contact_candidates(self):
        hints = defaultdict(list)
        for credit_id, actor in self.actors.items():
            facts = actor["actorFacts"] or {}
            for contact in facts.get("contacts", []):
                if contact.get("lookupKey"):
                    hints[
                        ("contact", contact.get("contactType"), contact["lookupKey"])
                    ].append(credit_id)
            for profile in facts.get("profiles", []):
                if profile.get("lookupKey"):
                    hints[
                        (
                            "profile",
                            profile.get("provider"),
                            profile.get("namespace"),
                            profile["lookupKey"],
                        )
                    ].append(credit_id)
        rows = {
            row["id"]: row
            for pair in self.candidates.values()
            for row in (pair["left"], pair["right"])
        }
        for hint, ids in hints.items():
            if 1 < len(ids) <= 30:
                for left, right in itertools.combinations(sorted(set(ids)), 2):
                    self._add(rows[left], rows[right], hint[0] + "_hint_overlap", 9)

    def _select_questions(self):
        cohorts = defaultdict(list)
        for key, pair in self.candidates.items():
            left, right = (
                self.actors.get(pair["left"]["id"]),
                self.actors.get(pair["right"]["id"]),
            )
            if not left or not right:
                continue
            basis = pair["basis"]
            if basis == "same_date_candidate":
                locations = [
                    next(
                        (
                            item["quote"]
                            for item in actor["evidence"]
                            if item["field"] == "catalog_venue_location"
                        ),
                        "",
                    )
                    for actor in (left, right)
                ]
                if not all(locations) or name_key(locations[0]) == name_key(
                    locations[1]
                ):
                    continue
                basis = "same_date_different_captured_location"
            if basis == "same_name_cross_artist":
                contacts = [
                    {
                        contact.get("lookupKey")
                        for contact in (actor["actorFacts"] or {}).get("contacts", [])
                        if contact.get("lookupKey")
                    }
                    for actor in (left, right)
                ]
                if all(contacts) and not contacts[0].intersection(contacts[1]):
                    basis = "same_name_distinct_contact_hints"
            question = {
                "questionId": "people-context:" + self._order(key)[:24],
                "retrievalBasis": basis,
                "left": left,
                "right": right,
            }
            cohorts[basis].append((self._order(key), question))
        quotas = {
            "same_date_different_captured_location": 350,
            "same_name_distinct_contact_hints": 350,
            "contact_hint_overlap": 500,
            "profile_hint_overlap": 250,
            "explicit_alias_overlap": 550,
            "same_name_cross_artist": 1400,
            "name_variant_candidate": 1600,
        }
        selected = []
        remainder = []
        for basis, values in cohorts.items():
            ordered = sorted(values)
            take = quotas.get(basis, 0)
            selected.extend(question for _, question in ordered[:take])
            remainder.extend(ordered[take:])
        # Fill unused cohorts from actual available questions, never invented controls.
        selected = selected[: self.maximum]
        selected.extend(
            question
            for _, question in sorted(remainder)[: self.maximum - len(selected)]
        )
        return selected


class PeopleContextProjection:
    """Attach a completed batch run without merging names or duplicating input prose.

    Relationships own compact decisions and retained JSONL coordinates. The original
    run owns witness context and token receipts. Existing credits remain the source
    of recording ownership; a changed or missing occurrence rejects attachment.
    """

    def __init__(self, database, run):
        self.database = Path(database).resolve()
        self.run = Path(run).resolve()

    def apply(self):
        manifest = json.loads((self.run / "manifest.json").read_text())
        if manifest["state"] not in (
            "completed",
            "completed_partial",
            "cancelled",
            "snapshot",
        ):
            raise ValueError(
                "Attach only settled results or an immutable result snapshot"
            )
        results = self.run / "results.jsonl"
        if (
            hashlib.sha256(results.read_bytes()).hexdigest()
            != manifest["resultsSha256"]
        ):
            raise ValueError("Contextual results changed after completion")
        continuation = self._continuation_context(manifest)
        decisions, states, questions = self._read_decisions(results)
        with closing(sqlite3.connect(self.database)) as db, db:
            db.execute("""CREATE TABLE IF NOT EXISTS context_questions(
                job_uuid TEXT NOT NULL, question_id TEXT NOT NULL,
                PRIMARY KEY(job_uuid,question_id))""")
            db.executemany(
                "INSERT OR IGNORE INTO context_questions VALUES(?,?)",
                [(manifest["jobUuid"], question) for question in questions],
            )
            processed = db.execute(
                "SELECT count(*) FROM context_questions WHERE job_uuid=?",
                (manifest["jobUuid"],),
            ).fetchone()[0]
            db.execute("""CREATE TABLE IF NOT EXISTS context_relationships(
                id TEXT PRIMARY KEY,left_group TEXT NOT NULL,right_group TEXT NOT NULL,
                left_credit INTEGER NOT NULL,right_credit INTEGER NOT NULL,
                relation TEXT NOT NULL,reason TEXT NOT NULL,evidence TEXT NOT NULL,
                retrieval_basis TEXT NOT NULL,run TEXT NOT NULL,line INTEGER NOT NULL,
                cache_key TEXT NOT NULL,job_uuid TEXT NOT NULL,basis TEXT NOT NULL)""")
            if "basis" not in {
                row[1] for row in db.execute("PRAGMA table_info(context_relationships)")
            }:
                db.execute(
                    "ALTER TABLE context_relationships ADD COLUMN basis TEXT NOT NULL DEFAULT 'validated_batch'"
                )
            db.execute(
                "CREATE INDEX IF NOT EXISTS context_left ON context_relationships(left_group)"
            )
            db.execute(
                "CREATE INDEX IF NOT EXISTS context_right ON context_relationships(right_group)"
            )
            for question, decision, line, cache_key, basis in decisions:
                for side in ("left", "right"):
                    actor = question[side]
                    credit = db.execute(
                        "SELECT group_id,source_uuid,artist_uuid,name,role,credit_key,evidence FROM credits WHERE id=?",
                        (actor["creditId"],),
                    ).fetchone()
                    # The typed context excerpt carries field/span coordinates;
                    # a raw credit may also include a JSON pointer to that field.
                    evidence = json.loads(credit[6]) if credit else {}
                    if (
                        credit is None
                        or credit[:4]
                        != (
                            actor["groupId"],
                            actor["sourceUuid"],
                            actor["artistUuid"],
                            actor["name"],
                        )
                        or credit[4] not in actor["roles"]
                        or credit[5] != actor.get("creditKey")
                        or not any(
                            all(
                                evidence.get(key) == excerpt.get(key)
                                for key in (
                                    "field",
                                    "quote",
                                    "startCodePoint",
                                    "endCodePoint",
                                )
                            )
                            for excerpt in actor["evidence"]
                        )
                    ):
                        raise ValueError(
                            "Contextual witness no longer matches its credited source"
                        )
                db.execute(
                    """INSERT OR REPLACE INTO context_relationships
                    VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)""",
                    (
                        decision["questionId"],
                        question["left"]["groupId"],
                        question["right"]["groupId"],
                        question["left"]["creditId"],
                        question["right"]["creditId"],
                        decision["relation"],
                        decision["reason"],
                        encoded(decision["evidence"]),
                        question["retrievalBasis"],
                        str(self.run),
                        line,
                        cache_key,
                        manifest["jobUuid"],
                        basis,
                    ),
                )
            previous = db.execute(
                "SELECT value FROM metadata WHERE key='peopleContext'"
            ).fetchone()
            previous = json.loads(previous[0]) if previous else {}
            same_job = previous.get("jobUuid") == manifest["jobUuid"]
            planned = manifest.get(
                "plannedQuestions", manifest.get("questions", processed)
            )
            if same_job:
                planned = max(planned, previous.get("plannedQuestions") or 0)
            relations = dict(
                db.execute(
                    "SELECT relation,count(*) FROM context_relationships GROUP BY relation"
                )
            )
            summary = {
                "run": str(self.run),
                "state": manifest["state"],
                "executionState": (
                    previous.get("executionState", previous.get("state"))
                    if "originalJobAccountedUsdAfter" in manifest and same_job
                    else manifest["state"]
                ),
                "originalRun": manifest.get("originalRun"),
                "jobUuid": manifest["jobUuid"],
                "decisions": sum(relations.values()),
                "appliedDecisions": len(decisions),
                "processedQuestions": processed,
                "plannedQuestions": planned,
                "batchStates": dict(states),
                "relations": relations,
                "accountedUsd": manifest.get(
                    "originalJobAccountedUsdAfter", manifest["accountedUsd"]
                ),
                "knownTokenCostUsd": (
                    previous.get("knownTokenCostUsd", 0)
                    if "originalJobAccountedUsdAfter" in manifest and same_job
                    else manifest["knownActualUsd"]
                ),
                "recoveredDecisions": manifest.get("recoveredDecisions", 0),
                "rejectedQuestions": manifest.get("rejectedQuestions", 0),
                "costBasis": "Configured token rates; not an invoice",
                "basis": "Contextual proposals; no canonical identities or transitive merges",
            }
            if continuation:
                self._add_continuation_summary(
                    db, summary, previous, manifest, continuation
                )
            db.execute(
                "INSERT OR REPLACE INTO metadata VALUES('peopleContext',?)",
                (encoded(summary),),
            )
        return summary

    def _continuation_context(self, manifest):
        """Verify the new run's receipt before combining two jobs' coverage."""
        run = self.run
        if not any(
            (run / name).exists()
            for name in ("continuation-plan.json", "continuation-reference.json")
        ):
            if "originalRun" not in manifest:
                return None
            run = Path(manifest["originalRun"]).resolve()
            if not any(
                (run / name).exists()
                for name in ("continuation-plan.json", "continuation-reference.json")
            ):
                return None

        plan_bytes = (run / "continuation-plan.json").read_bytes()
        plan = json.loads(plan_bytes)
        reference = json.loads((run / "continuation-reference.json").read_bytes())
        run_manifest_bytes = (run / "manifest.json").read_bytes()
        current = json.loads(run_manifest_bytes)
        if (
            reference["planSha256"] != hashlib.sha256(plan_bytes).hexdigest()
            or any(
                reference[key] != plan[key]
                for key in ("newJobUuid", "originalJobUuid", "originalManifestSha256")
            )
            or plan["newJobUuid"] != current["jobUuid"]
            or manifest["jobUuid"] != current["jobUuid"]
            or current["state"] not in {"cancelled", "completed_partial", "completed"}
            or current["method"] != plan["method"]
            or (
                run != self.run
                and manifest["originalManifestSha256"]
                != hashlib.sha256(run_manifest_bytes).hexdigest()
            )
        ):
            raise ValueError("Continuation receipt does not match its sealed run")

        original_run = Path(plan["originalRun"])
        preparation = PeopleContextContinuationPreparation(
            original_run, Path(plan["pendingInputsPath"]).parent, plan["newJobUuid"]
        )
        expected_plan, pending_bytes = preparation._validated_plan()
        if plan != expected_plan:
            raise ValueError("Continuation plan differs from its authenticated original scope")

        inputs = (run / "inputs.jsonl").read_bytes()
        results = (run / "results.jsonl").read_bytes()
        if (
            inputs != pending_bytes
            or current["inputSha256"] != plan["pendingInputsSha256"]
            or current["resultsSha256"] != hashlib.sha256(results).hexdigest()
        ):
            raise ValueError("Continuation input or results differ from their sealed run")
        batches = preparation._read_inputs(inputs)
        completed = preparation._read_completed(results, batches)
        if (
            current["inputBatches"] != len(batches)
            or current["questions"] != plan["pendingQuestions"]
            or current["completedBatches"] != len(completed)
        ):
            raise ValueError("Continuation counts differ from its recorded inputs and results")
        if run != self.run:
            # Recovery may recover decisions, but cannot expand the paid input population.
            preparation._read_completed((self.run / "results.jsonl").read_bytes(), batches)
        return {
            "plan": plan,
            "original": json.loads((original_run / "manifest.json").read_bytes()),
            "current": current,
            "run": str(run),
        }

    @staticmethod
    def _add_continuation_summary(db, summary, previous, manifest, context):
        plan = context["plan"]
        original = context["original"]
        current = context["current"]
        original_job = plan["originalJobUuid"]
        current_job = plan["newJobUuid"]
        processed = db.execute(
            """SELECT count(DISTINCT question_id) FROM context_questions
               WHERE job_uuid IN (?,?)""",
            (original_job, current_job),
        ).fetchone()[0]

        # Recovery attachments have zero new spend. Always take both jobs' costs
        # from their authenticated primary manifests, never from the latest attachment.
        original_summary = {
            "jobUuid": original_job,
            "run": plan["originalRun"],
            "state": original["state"],
            "accountedUsd": original["accountedUsd"],
            "knownTokenCostUsd": original["knownActualUsd"],
        }
        current_summary = {
            "jobUuid": current_job,
            "run": context["run"],
            "state": current["state"],
            "accountedUsd": current["accountedUsd"],
            "knownTokenCostUsd": current["knownActualUsd"],
        }
        if previous.get("jobUuid") == original_job:
            original_recovery = {
                key: previous.get(key, 0)
                for key in ("recoveredDecisions", "rejectedQuestions")
            }
        else:
            original_recovery = previous.get("originalJob", {}).get("recovery", {})
        current_recovery = (
            previous.get("currentJob", {}).get("recovery", {})
            if previous.get("jobUuid") == current_job
            else {}
        )
        if "originalJobAccountedUsdAfter" in manifest:
            current_recovery = {
                key: manifest[key]
                for key in ("recoveredDecisions", "rejectedQuestions")
            }
        original_summary["recovery"] = original_recovery
        current_summary["recovery"] = current_recovery
        summary.update(
            originalRun=plan["originalRun"],
            executionState=current["state"],
            processedQuestions=processed,
            plannedQuestions=plan["recordedQuestions"] + plan["pendingQuestions"],
            originalJob=original_summary,
            currentJob=current_summary,
            accountedUsd=original["accountedUsd"] + current["accountedUsd"],
            knownTokenCostUsd=original["knownActualUsd"] + current["knownActualUsd"],
        )
        for key in ("recoveredDecisions", "rejectedQuestions"):
            summary[key] = original_recovery.get(key, 0) + current_recovery.get(key, 0)

    @staticmethod
    def _read_decisions(results):
        decisions = []
        states = Counter()
        observed_questions = set()
        with results.open(encoding="utf-8") as stream:
            for line, text in enumerate(stream, 1):
                row = json.loads(text)
                result = row["result"]
                observed_questions.update(
                    q["questionId"] for q in row["input"]["questions"]
                )
                states[result["state"]] += 1
                if result["state"] != "proposed":
                    continue
                questions = {q["questionId"]: q for q in row["input"]["questions"]}
                for decision in result["decisions"]:
                    question = questions[decision["questionId"]]
                    decisions.append(
                        (
                            question,
                            decision,
                            line,
                            result["cacheKey"],
                            row.get("recovery", {}).get("basis", "validated_batch"),
                        )
                    )
        return decisions, states, observed_questions


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database")
    parser.add_argument("--output", required=True)
    parser.add_argument("--maximum", type=int, default=5000)
    parser.add_argument(
        "--attach", action="store_true", help="Attach completed results from --output"
    )
    parser.add_argument(
        "--continue-from", help="Prepare pending inputs from a sealed original run"
    )
    parser.add_argument("--job-uuid", help="New job UUID for continuation preparation")
    args = parser.parse_args()
    if args.continue_from:
        if not args.job_uuid or args.attach or args.database:
            parser.error(
                "--continue-from requires --job-uuid and cannot use --attach or --database"
            )
        result = PeopleContextContinuationPreparation(
            args.continue_from, args.output, args.job_uuid
        ).prepare()
    else:
        if not args.database or args.job_uuid:
            parser.error(
                "--database is required; --job-uuid is only for --continue-from"
            )
        result = (
            PeopleContextProjection(args.database, args.output).apply()
            if args.attach
            else PeopleContextPreparation(
                args.database, args.output, args.maximum
            ).prepare()
        )
    print(encoded(result), flush=True)


if __name__ == "__main__":
    main()
