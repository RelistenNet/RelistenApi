"""Reader contract checks using already-evaluated C# decisions, not a second matcher."""

import json
import sqlite3
import unittest

from reviewed_knowledge import apply_composition_review, apply_venue_review_counts, apply_venue_reviews, venue_review_decision


class EvaluatedKnowledge:
    def __init__(self, evaluations):
        self.evaluations = evaluations

    def snapshot(self):
        return {"sha256": "review-image", "rules": [
            {"state": "accepted", "definition": {"domain": "venues", "selector": {
                "namespace": "legacy_venue", "accountStableKey": key}}}
            for key in self.evaluations]}

    def evaluate(self, observations, snapshot):
        return [self.evaluations[item["stableKey"]] for item in observations]

    def evaluate_one(self, observation):
        return self.evaluations[observation["stableKey"]]


def target(key, namespace="legacy_venue"):
    return {"domain": "venues" if namespace == "legacy_venue" else "compositions",
            "namespace": namespace, "stableKey": key}


def evaluated(selected=None, excluded=(), outcome="reviewed_correspondence"):
    return {"outcome": outcome, "snapshotSha256": "review-image", "target": selected,
            "excludedTargets": list(excluded), "appliedRules": [{"ruleUuid": "review", "revision": 2}],
            "conflictingTargets": []}


class TestReviewedReaderProjection(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(":memory:")
        self.db.row_factory = sqlite3.Row
        self.addCleanup(self.db.close)
        self.db.executescript("""
            CREATE TABLE venues(uuid TEXT PRIMARY KEY,legacy_id INTEGER,name TEXT,original_name TEXT,
                place_key TEXT,normalization_key TEXT,normalized_location TEXT);
            INSERT INTO venues VALUES('garden',1,'Madison Square Garden','Madison Square Garden','garden-group','garden-group','New York, New York');
            INSERT INTO venues VALUES('msg',2,'MSG','MSG','msg-group','msg-group','NY, NY');
            INSERT INTO venues VALUES('theater',3,'Theater at Madison Square Garden','Theater at Madison Square Garden','theater-group','theater-group','New York, New York');
            ALTER TABLE venues ADD COLUMN location TEXT;
            UPDATE venues SET location=normalized_location;
            CREATE TABLE recordings(source_uuid TEXT,show_uuid TEXT,source_venue_id INTEGER,show_venue_id INTEGER);
            INSERT INTO recordings VALUES('recording-a','show-a',1,1);
            INSERT INTO recordings VALUES('recording-b','show-b',2,2);
            INSERT INTO recordings VALUES('recording-c','show-c',3,3);
            CREATE TABLE normalization_counts(place_key TEXT,source_count INTEGER,show_count INTEGER);
            INSERT INTO normalization_counts VALUES('garden-group',1,1),('msg-group',1,1),('theater-group',1,1);
            """)

    @staticmethod
    def reference(row):
        return {"domain": "venues", "namespace": "legacy_venue", "stableKey": row["uuid"]}

    def project(self, client):
        sql = apply_venue_reviews(self.db, "SELECT * FROM venues", client, self.reference)
        self.db.execute("CREATE TEMP VIEW catalog_venues AS " + sql)
        counts = apply_venue_review_counts(self.db, "SELECT * FROM normalization_counts")
        self.db.execute("CREATE TEMP VIEW effective_counts AS " + counts)

    def test_review_joins_recordings_but_preserves_originals_and_theater(self):
        self.project(EvaluatedKnowledge({"msg": evaluated(target("garden"))}))
        names = [tuple(row) for row in self.db.execute(
            "SELECT uuid,name,original_name FROM catalog_venues WHERE place_key='garden-group' ORDER BY uuid")]
        self.assertEqual(names, [("garden", "Madison Square Garden", "Madison Square Garden"),
                                 ("msg", "Madison Square Garden", "MSG")])
        self.assertEqual(tuple(self.db.execute("SELECT source_count,show_count FROM effective_counts WHERE place_key='garden-group'").fetchone()), (2, 2))
        self.assertEqual(self.db.execute("SELECT name FROM venues WHERE uuid='msg'").fetchone()[0], "MSG")
        self.assertEqual(self.db.execute("SELECT place_key FROM catalog_venues WHERE uuid='theater'").fetchone()[0], "theater-group")
        self.assertEqual(venue_review_decision(self.db, "msg")["appliedRules"][0]["revision"], 2)

    def test_rejection_removes_only_the_excluded_group_and_keeps_audio(self):
        self.db.execute("UPDATE venues SET place_key='garden-group',normalization_key='garden-group' WHERE uuid='msg'")
        self.project(EvaluatedKnowledge({"msg": evaluated(excluded=[target("garden")], outcome="excluded_targets")}))
        self.assertEqual(self.db.execute("SELECT place_key FROM catalog_venues WHERE uuid='msg'").fetchone()[0], "expert-account:msg")
        self.assertEqual(tuple(self.db.execute("SELECT source_count,show_count FROM effective_counts WHERE place_key='garden-group'").fetchone()), (1, 1))
        self.assertEqual(self.db.execute("SELECT count(*) FROM recordings").fetchone()[0], 3)

    def test_previously_ungrouped_target_also_browses_the_reviewed_members(self):
        self.db.execute("UPDATE venues SET normalization_key=NULL WHERE uuid='garden'")
        self.project(EvaluatedKnowledge({"msg": evaluated(target("garden"))}))
        self.assertEqual([row[0] for row in self.db.execute(
            "SELECT normalization_key FROM catalog_venues WHERE place_key='garden-group' ORDER BY uuid")],
            ["garden-group", "garden-group"])

    def test_reviewed_target_without_normalized_locality_uses_its_own_original(self):
        self.db.execute("UPDATE venues SET normalized_location=NULL,location='New York, NY' WHERE uuid='garden'")
        self.project(EvaluatedKnowledge({"msg": evaluated(target("garden"))}))
        row = self.db.execute("SELECT normalized_location,location FROM catalog_venues WHERE uuid='msg'").fetchone()
        self.assertEqual(tuple(row), ("New York, NY", "NY, NY"))

    def test_composition_correction_keeps_rivals_and_machine_result_inspectable(self):
        original = {"candidates": ["work-a", "work-b"], "modelSelection": {"preferredWorkKey": "work-a", "selectedWorkKey": "work-a", "rationale": "model reasoning"}}
        client = EvaluatedKnowledge({"song": evaluated(target("work-b", "composition_work"))})
        changed = apply_composition_review(original, client, {"stableKey": "song"})
        self.assertEqual(changed["preferredWorkKey"], "work-b")
        self.assertEqual(changed["modelSelection"]["selectedWorkKey"], "work-a")
        self.assertEqual(changed["modelSelection"]["rationale"], "model reasoning")
        self.assertIsNone(changed["modelSelection"]["preferredWorkKey"])
        self.assertEqual(changed["candidates"], ["work-a", "work-b"])
        self.assertEqual(original["modelSelection"]["preferredWorkKey"], "work-a")
        self.assertEqual(changed["reviewedDecision"]["appliedRules"][0]["revision"], 2)

    def test_rejecting_one_work_does_not_withhold_the_song_or_its_files(self):
        original = {"candidates": ["work-a", "work-b"], "files": ["playable-file"], "modelSelection": {"preferredWorkKey": "work-a"}}
        client = EvaluatedKnowledge({"song": evaluated(excluded=[target("work-a", "composition_work")], outcome="excluded_targets")})
        changed = apply_composition_review(original, client, {"stableKey": "song"})
        self.assertIsNone(changed["modelSelection"]["preferredWorkKey"])
        self.assertEqual(changed["files"], ["playable-file"])
        self.assertEqual(changed["candidates"], original["candidates"])




class TestMusicReviewProjectionClient(unittest.TestCase):
    def test_paged_changes_are_pinned_cached_and_refreshed_after_a_saved_decision(self):
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        import threading
        from urllib.parse import parse_qs, urlsplit
        from reviewed_knowledge import ReviewedKnowledgeClient

        state = {"sha": "before", "requests": [], "mismatch": False}
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                route = urlsplit(self.path)
                if route.path == "/api/knowledge":
                    result = {"sha256": state["sha"], "rules": []}
                else:
                    query = {key: values[0] for key, values in parse_qs(route.query).items()}
                    state["requests"].append(query)
                    offset = int(query["offset"])
                    total = 101 if state["sha"] == "before" else 1
                    result = dict(inputManifestSha256=query["inputManifestSha256"],
                                  outputManifestSha256=query["outputManifestSha256"],
                                  knowledgeSha256="wrong" if state["mismatch"] else state["sha"],
                                  total=total, offset=offset,
                                  items=[dict(originalKey=str(index)) for index in range(offset, min(total, offset + 100))])
                body = json.dumps(result).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            def log_message(self, *args):
                pass
        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        client = ReviewedKnowledgeClient(f"http://127.0.0.1:{server.server_port}")
        metadata = dict(inputManifestSha256="input", outputManifestSha256="output")
        self.assertEqual(len(client.music_changes(metadata, client.snapshot())), 101)
        self.assertEqual([query["offset"] for query in state["requests"]], ["0", "100"])
        self.assertEqual(len(client.music_changes(metadata, client.snapshot())), 101)
        self.assertEqual(len(state["requests"]), 2)
        state["sha"] = "saved"
        self.assertEqual(client.music_changes(metadata, client.snapshot()), [dict(originalKey="0")])
        state["sha"] = "withdrawn"
        state["mismatch"] = True
        from reviewed_knowledge import ReviewedKnowledgeUnavailable
        with self.assertRaises(ReviewedKnowledgeUnavailable):
            client.music_changes(metadata, client.snapshot())


if __name__ == "__main__":
    unittest.main()
