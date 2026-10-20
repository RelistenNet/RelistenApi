"""Project reviewed C# decisions into the existing read-only catalog readers.

PostgreSQL owns decisions. This module retrieves candidate observations and applies
the core resolver's evaluated results; it contains no identity-matching policy.
The source SQLite files remain immutable, including their original wording.
"""

import copy
import hashlib
import json
import threading
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlsplit
from urllib.request import Request, urlopen


class ReviewedKnowledgeUnavailable(ValueError):
    """The reader cannot obtain the authoritative reviewed interpretation."""


def observation(reference):
    return dict(domain=reference["domain"], namespace=reference["namespace"],
                accountStableKey=reference["stableKey"], **{
                    name: reference.get(name) for name in
                    ("literal", "artistUuid", "provider", "locality", "date")})


class ReviewedKnowledgeClient:
    """Use the private operator host's authenticated, shared resolution policy."""

    def __init__(self, review_url):
        parsed = urlsplit(review_url)
        if (parsed.scheme not in {"http", "https"} or not parsed.hostname
                or parsed.username or parsed.password or parsed.query or parsed.fragment
                or parsed.path not in {"", "/"}):
            raise ValueError("Review URL must be an HTTP(S) origin")
        self.origin = review_url.rstrip("/")
        self.lock = threading.RLock()
        self.current = None
        self.results = {}

    def _request(self, path, value=None, timeout=20):
        headers = {"Accept": "application/json"}
        body = None
        if value is not None:
            headers.update({"Content-Type": "application/json", "Origin": self.origin,
                            "X-CSRF-Token": self._request("/api/session")["csrfToken"]})
            body = json.dumps(value, separators=(",", ":")).encode()
        try:
            with urlopen(Request(self.origin + path, data=body, headers=headers), timeout=timeout) as response:
                raw = response.read(8 * 1024**2 + 1)
            if len(raw) > 8 * 1024**2:
                raise ReviewedKnowledgeUnavailable("Reviewed knowledge response exceeds its limit")
            return json.loads(raw)
        except (HTTPError, URLError, TimeoutError, json.JSONDecodeError) as error:
            raise ReviewedKnowledgeUnavailable("Reviewed knowledge is unavailable; retry the catalog page") from error

    def snapshot(self):
        with self.lock:
            current = self._request("/api/knowledge")
            if not isinstance(current.get("rules"), list) or not isinstance(current.get("sha256"), str):
                raise ReviewedKnowledgeUnavailable("Reviewed knowledge response is incomplete")
            if self.current is None or current["sha256"] != self.current["sha256"]:
                self.results.clear()
            self.current = current
            return current

    def evaluate(self, references, snapshot=None):
        with self.lock:
            snapshot = snapshot or self.snapshot()
            observations = [observation(reference) for reference in references]
            keys = [hashlib.sha256(json.dumps(item, sort_keys=True).encode()).hexdigest()
                    for item in observations]
            missing = [(key, item) for key, item in zip(keys, observations)
                       if (snapshot["sha256"], key) not in self.results]
            for start in range(0, len(missing), 100):
                batch = missing[start:start + 100]
                reply = self._request("/api/evaluate", {"observations": [item for _, item in batch]})
                if reply.get("snapshotSha256") != snapshot["sha256"] or len(reply.get("items", [])) != len(batch):
                    raise ReviewedKnowledgeUnavailable("Reviewed knowledge changed; reload this page")
                for (key, supplied), returned in zip(batch, reply["items"]):
                    if returned.get("observation") != supplied:
                        raise ReviewedKnowledgeUnavailable("Reviewed knowledge returned another observation")
                    self.results[(snapshot["sha256"], key)] = returned["evaluation"]
            return [copy.deepcopy(self.results[(snapshot["sha256"], key)]) for key in keys]

    def music_changes(self, metadata, snapshot):
        """Read complete sparse C# changes, pinned across every response page."""
        key = ("music", snapshot["sha256"], metadata["inputManifestSha256"], metadata["outputManifestSha256"])
        with self.lock:
            if key in self.results:
                return self.results[key]
            items, total = [], None
            while total is None or len(items) < total:
                query = dict(inputManifestSha256=key[2], outputManifestSha256=key[3],
                             knowledgeSha256=key[1], offset=len(items))
                page = self._request("/api/music/review-projection?" + urlencode(query), timeout=60)
                if (page.get("inputManifestSha256") != key[2] or page.get("outputManifestSha256") != key[3]
                        or page.get("knowledgeSha256") != key[1] or page.get("offset") != len(items)
                        or not isinstance(page.get("items"), list) or not isinstance(page.get("total"), int)
                        or not 0 <= page["total"] <= 200000 or len(page["items"]) > 100
                        or (total is not None and total != page["total"])):
                    raise ReviewedKnowledgeUnavailable("The song correction projection changed; reload this page")
                total = page["total"]
                if not page["items"] and len(items) < total:
                    raise ReviewedKnowledgeUnavailable("The song correction projection is incomplete")
                items.extend(page["items"])
                if len(items) > total:
                    raise ReviewedKnowledgeUnavailable("The song correction projection exceeded its declared population")
            self.results[key] = items
            return items

    def evaluate_one(self, reference):
        snapshot = self.snapshot()
        if not any(rule["definition"]["domain"] == reference["domain"] for rule in snapshot["rules"]):
            return None
        return self.evaluate([reference], snapshot)[0]


def apply_venue_reviews(db, catalog_sql, client, ref_factory):
    """Replace browse membership only after the shared resolver names a target.

    Exact keys/literals retrieve candidates. Context, precedence, rejection and
    conflicting rules are evaluated in C#, never reimplemented in this reader.
    Target references name the retained target account's physical-place group.
    """
    if client is None:
        return catalog_sql
    snapshot = client.snapshot()
    rules = [row for row in snapshot["rules"] if row["state"] != "retracted"
             and row["definition"]["domain"] == "venues"
             and row["definition"]["selector"]["namespace"] == "legacy_venue"]
    if not rules:
        return catalog_sql
    db.execute("CREATE TEMP VIEW expert_original_venues AS " + catalog_sql)
    candidates = {}
    for rule in rules:
        selector = rule["definition"]["selector"]
        if selector.get("accountStableKey") is not None:
            rows = db.execute("SELECT * FROM expert_original_venues WHERE uuid=?", (selector["accountStableKey"],))
        else:
            rows = db.execute("SELECT * FROM expert_original_venues WHERE coalesce(original_name,name)=?", (selector["literal"],))
        for row in rows:
            candidates[row["uuid"]] = dict(row)
    rows = list(candidates.values())
    decisions = client.evaluate([ref_factory(row) for row in rows], snapshot)
    db.execute("CREATE TEMP TABLE expert_venue_changes(uuid TEXT PRIMARY KEY,target_uuid TEXT,detach INTEGER,decision TEXT)")
    db.execute("CREATE TEMP TABLE expert_changed_groups(place_key TEXT PRIMARY KEY)")
    db.execute("CREATE TEMP TABLE expert_target_groups(place_key TEXT PRIMARY KEY)")
    for row, decision in zip(rows, decisions):
        target = decision.get("target")
        selected = None
        if target and target["namespace"] == "legacy_venue":
            selected = db.execute("SELECT * FROM expert_original_venues WHERE uuid=?", (target["stableKey"],)).fetchone()
            if selected is None:
                raise ReviewedKnowledgeUnavailable("The reviewed venue target is absent from this catalog")
        detached = decision["outcome"] == "conflict"
        for excluded in decision.get("excludedTargets", []):
            if excluded["namespace"] != "legacy_venue":
                continue
            other = db.execute("SELECT place_key FROM expert_original_venues WHERE uuid=?", (excluded["stableKey"],)).fetchone()
            detached |= other is not None and other[0] == row["place_key"]
        if selected is None and not detached:
            continue
        db.execute("INSERT INTO expert_venue_changes VALUES(?,?,?,?)", (
            row["uuid"], selected["uuid"] if selected is not None else None, int(detached), json.dumps(decision)))
        groups = [row["place_key"], selected["place_key"] if selected is not None else "expert-account:" + row["uuid"]]
        db.executemany("INSERT OR IGNORE INTO expert_changed_groups VALUES(?)", ((key,) for key in groups))
        if selected is not None:
            db.execute("INSERT OR IGNORE INTO expert_target_groups VALUES(?)", (selected["place_key"],))
    # The original view includes correction JSON joins over the full directory.
    # A second live join to that view can expand per recording. Retain only the
    # selected target rows once, with an index for the browse projection.
    db.execute("""CREATE TEMP TABLE expert_venue_targets AS SELECT * FROM expert_original_venues
        WHERE uuid IN (SELECT target_uuid FROM expert_venue_changes WHERE target_uuid IS NOT NULL)""")
    db.execute("CREATE UNIQUE INDEX expert_venue_target_uuid ON expert_venue_targets(uuid)")
    columns = [item[0] for item in db.execute("SELECT * FROM expert_original_venues LIMIT 0").description]
    overrides = {
        "place_key": "CASE WHEN e.target_uuid IS NOT NULL THEN t.place_key WHEN e.detach=1 THEN 'expert-account:'||v.uuid ELSE v.place_key END",
        "normalization_key": "CASE WHEN e.target_uuid IS NOT NULL THEN coalesce(t.normalization_key,t.place_key) WHEN e.detach=1 THEN NULL WHEN g.place_key IS NOT NULL THEN coalesce(v.normalization_key,v.place_key) ELSE v.normalization_key END",
        "name": "CASE WHEN e.target_uuid IS NOT NULL THEN t.name ELSE v.name END",
        "normalized_location": "CASE WHEN e.target_uuid IS NOT NULL THEN coalesce(nullif(t.normalized_location,''),t.location) ELSE v.normalized_location END",
    }
    projection = ",".join(overrides.get(name, 'v."' + name + '"') + ' AS "' + name + '"' for name in columns)
    return ("SELECT " + projection + " FROM expert_original_venues v LEFT JOIN expert_venue_changes e ON e.uuid=v.uuid "
            "LEFT JOIN expert_venue_targets t ON t.uuid=e.target_uuid LEFT JOIN expert_target_groups g ON g.place_key=v.place_key")


def apply_venue_review_counts(db, counts_sql):
    if not db.execute("SELECT 1 FROM sqlite_temp_master WHERE name='expert_changed_groups'").fetchone():
        return counts_sql
    # Materialize changed memberships before joining recording owners. A correlated
    # IN query over the projected directory otherwise repeats its JSON joins for
    # every recording. UNION counts one source once when both venue references agree.
    db.execute("""CREATE TEMP TABLE expert_changed_venue_members AS SELECT legacy_id,place_key
        FROM catalog_venues WHERE place_key IN (SELECT place_key FROM expert_changed_groups)""")
    db.execute("CREATE INDEX expert_changed_venue_owner ON expert_changed_venue_members(legacy_id)")
    db.execute("""CREATE TEMP TABLE expert_changed_venue_counts AS
        SELECT place_key,count(*) source_count,count(DISTINCT show_uuid) show_count FROM (
            SELECT m.place_key,r.source_uuid,r.show_uuid FROM expert_changed_venue_members m
                JOIN recordings r ON r.source_venue_id=m.legacy_id
            UNION
            SELECT m.place_key,r.source_uuid,r.show_uuid FROM expert_changed_venue_members m
                JOIN recordings r ON r.show_venue_id=m.legacy_id
        ) GROUP BY place_key""")
    db.execute("CREATE UNIQUE INDEX expert_changed_venue_count_key ON expert_changed_venue_counts(place_key)")
    return ("SELECT h.* FROM (" + counts_sql + ") h WHERE NOT EXISTS(SELECT 1 FROM expert_changed_groups g WHERE g.place_key=h.place_key) "
            "UNION ALL SELECT g.place_key,coalesce(c.source_count,0),coalesce(c.show_count,0) "
            "FROM expert_changed_groups g LEFT JOIN expert_changed_venue_counts c ON c.place_key=g.place_key")


def venue_review_decision(db, identifier):
    if not db.execute("SELECT 1 FROM sqlite_temp_master WHERE name='expert_venue_changes'").fetchone():
        return None
    row = db.execute("SELECT decision FROM expert_venue_changes WHERE uuid=?", (identifier,)).fetchone()
    return json.loads(row[0]) if row else None


def apply_composition_review(result, client, source_ref):
    if result is None or client is None:
        return result
    decision = client.evaluate_one(source_ref)
    if decision is None or decision["outcome"] == "no_match":
        return result
    result = copy.deepcopy(result)
    result["reviewedDecision"] = decision
    model = result.get("modelSelection")
    excluded = {target["stableKey"] for target in decision.get("excludedTargets", [])
                if target["namespace"] == "composition_work"}
    preferred = model.get("preferredWorkKey") if model else None
    if preferred in excluded or decision["outcome"] == "conflict":
        result["modelSelection"] = {**(model or {}), "preferredWorkKey": None, "preferencePermitted": False}
    target = decision.get("target")
    if target and target["namespace"] == "composition_work":
        result["preferredWorkKey"] = target["stableKey"]
        result["preferredBasis"] = "reviewed"
        if model and model.get("preferredWorkKey") != target["stableKey"]:
            result["modelSelection"] = {**model, "preferredWorkKey": None, "preferencePermitted": False}
    return result
