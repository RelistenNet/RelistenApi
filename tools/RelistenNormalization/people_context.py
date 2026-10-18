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

from people_directory import encoded, name_key, retained_actor_facts


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
        if manifest["state"] not in ("completed", "completed_partial", "snapshot"):
            raise ValueError(
                "Attach only settled results or an immutable result snapshot"
            )
        results = self.run / "results.jsonl"
        if (
            hashlib.sha256(results.read_bytes()).hexdigest()
            != manifest["resultsSha256"]
        ):
            raise ValueError("Contextual results changed after completion")
        decisions, states = self._read_decisions(results)
        with closing(sqlite3.connect(self.database)) as db, db:
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
                        "SELECT group_id,source_uuid,artist_uuid FROM credits WHERE id=?",
                        (actor["creditId"],),
                    ).fetchone()
                    if credit != (
                        actor["groupId"],
                        actor["sourceUuid"],
                        actor["artistUuid"],
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
            processed = manifest.get("questions") or 0
            planned = manifest.get("plannedQuestions", processed)
            if same_job:
                processed = max(processed, previous.get("processedQuestions") or 0)
                planned = max(planned, previous.get("plannedQuestions") or 0)
            relations = dict(
                db.execute(
                    "SELECT relation,count(*) FROM context_relationships GROUP BY relation"
                )
            )
            summary = {
                "run": str(self.run),
                "state": manifest["state"],
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
            db.execute(
                "INSERT OR REPLACE INTO metadata VALUES('peopleContext',?)",
                (encoded(summary),),
            )
        return summary

    @staticmethod
    def _read_decisions(results):
        decisions = []
        states = Counter()
        with results.open(encoding="utf-8") as stream:
            for line, text in enumerate(stream, 1):
                row = json.loads(text)
                result = row["result"]
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
        return decisions, states


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--maximum", type=int, default=5000)
    parser.add_argument(
        "--attach", action="store_true", help="Attach completed results from --output"
    )
    args = parser.parse_args()
    print(
        encoded(
            PeopleContextProjection(args.database, args.output).apply()
            if args.attach
            else PeopleContextPreparation(
                args.database, args.output, args.maximum
            ).prepare()
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
