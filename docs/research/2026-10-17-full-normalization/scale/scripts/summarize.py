#!/usr/bin/env python3
"""Validate a completed capture and report evidence coverage, never label accuracy."""
import argparse
from collections import Counter, defaultdict
import hashlib
import json
from pathlib import Path

SAMPLES = {
    "source": ("sources", "source_profile", "rows", 8),
    "track": ("tracks", "track_profile", "active", 8),
    "venue": ("venues", "venue_profile", "rows", 4),
    "song": ("songs", "song_profile", "rows", 32),
    "setlist": ("setlist_shows", "setlist_profile", "shows", 8),
}


def summarize(directory: Path) -> dict:
    query = json.loads((directory / "query.json").read_text())
    sql_hash = hashlib.sha256((directory / "executed.sql").read_bytes()).hexdigest()
    if query["sql_sha256"] != sql_hash:
        raise ValueError("Executed SQL does not match its capture receipt.")
    snapshot = directory / "snapshot.jsonl"
    artists, profiles, samples = {}, defaultdict(dict), defaultdict(Counter)
    source_strings, venue_keys, song_keys, families = defaultdict(Counter), Counter(), Counter(), Counter()
    sample_ids, kinds = defaultdict(set), Counter()
    first, last, totals = None, None, None
    sampled_memberships = 0
    track_context = Counter()
    with snapshot.open(encoding="utf-8") as stream:
        for line in stream:
            record = json.loads(line)
            kind = record["kind"]
            first = first or record
            last = record
            kinds[kind] += 1
            if kind == "artist":
                artist_id = record["row"]["id"]
                if artist_id in artists:
                    raise ValueError("Duplicate artist in capture.")
                artists[artist_id] = record
            elif kind.endswith("_profile"):
                artist_id = record["row"]["artist_id"]
                if artist_id in profiles[kind]:
                    raise ValueError("Duplicate artist profile.")
                profiles[kind][artist_id] = record["row"]
            elif kind.endswith("_sample"):
                row = record["row"]
                if row["id"] in sample_ids[kind]:
                    raise ValueError("Duplicate sampled row.")
                sample_ids[kind].add(row["id"])
                samples[kind][row["artist_id"]] += 1
                if kind == "setlist_sample":
                    sampled_memberships += len(record.get("unordered_memberships", []))
                if kind == "track_sample":
                    for context in ("source_context", "set_context", "show_context"):
                        track_context["missing_" + context] += int(record.get(context) is None)
                if kind == "source_sample":
                    for field in ("taper", "transferrer", "lineage"):
                        if row.get(field):
                            source_strings[field][row[field]] += 1
                if kind == "venue_sample":
                    venue_keys[(row["name"], row["location"])] += 1
                if kind == "song_sample":
                    song_keys[row["name"]] += 1
                if record.get("family"):
                    families[record["family"]] += 1
            elif kind == "totals":
                totals = record["rows"]
    if first is None or first["kind"] != "snapshot" or last["kind"] != "snapshot_complete":
        raise ValueError("A complete snapshot is required.")
    if first["read_only"] != "on" or first["in_recovery"] is not True:
        raise ValueError("Capture must come from a read-only replica.")
    if totals is None or len(artists) != totals["artists"]:
        raise ValueError("Artist coverage does not reconcile.")
    coverage = {}
    for domain, (relation, profile_kind, count_field, cap) in SAMPLES.items():
        population = profiles[profile_kind]
        selected = samples[domain + "_sample"]
        if set(population) - set(artists):
            raise ValueError(f"Unattributed {domain} population needs explicit investigation.")
        expected = {artist_id: min(row[count_field], cap) for artist_id, row in population.items() if row[count_field]}
        if dict(selected) != expected:
            raise ValueError(f"Incomplete {domain} selection: expected a capped sample for every eligible artist.")
        profile_total = sum(row["rows" if domain == "track" else count_field] for row in population.values())
        if profile_total != totals[relation]:
            raise ValueError(f"Full {domain} population does not reconcile.")
        tiers, providers = defaultdict(Counter), defaultdict(Counter)
        for artist_id, artist in artists.items():
            count = population.get(artist_id, {}).get(count_field, 0)
            tier = "none" if count == 0 else "1" if count == 1 else "2-10" if count <= 10 else "11-100" if count <= 100 else "101-1000" if count <= 1000 else "1001+"
            tiers[tier]["artists"] += 1
            tiers[tier]["sampled_artists"] += int(artist_id in selected)
            tiers[tier]["sampled_rows"] += selected.get(artist_id, 0)
            for name in {mapping["provider_name"] for mapping in artist["configured_provider_mappings"]}:
                providers[name]["configured_artists"] += 1
                providers[name]["eligible_artists"] += int(count > 0)
                providers[name]["sampled_artists"] += int(artist_id in selected)
        coverage[domain] = {
            "relation": relation, "population_rows": profile_total,
            "eligible_rows": sum(row[count_field] for row in population.values()),
            "per_artist_cap": cap, "sampled_rows": sum(selected.values()),
            "eligible_artists": len(expected), "sampled_artists": len(selected),
            "artists_without_eligible_rows": len(set(artists) - set(expected)),
            "missing_eligible_artists": 0, "size_strata": dict(tiers),
            "configured_provider_coverage": dict(providers),
        }
    memberships = sum(row["memberships"] for row in profiles["setlist_profile"].values())
    if memberships != totals["setlist_memberships"]:
        raise ValueError("Setlist membership population does not reconcile.")
    source_fields = Counter()
    track_fields = Counter()
    for profile in profiles["source_profile"].values():
        source_fields.update({k: v for k, v in profile.items() if k not in ("artist_id", "rows") and not k.startswith("max_")})
    for profile in profiles["track_profile"].values():
        track_fields.update({k: v for k, v in profile.items() if k not in ("artist_id", "rows") and not k.startswith("max_")})
    return {
        "snapshot": first, "completed_at": last["completed_at"],
        "sql_sha256": sql_hash, "raw_sha256": hashlib.sha256(snapshot.read_bytes()).hexdigest(),
        "raw_file": str(snapshot.resolve()), "raw_bytes": snapshot.stat().st_size,
        "totals": totals, "coverage": coverage,
        "full_population_source_format_counts": dict(source_fields),
        "full_population_track_format_counts": dict(track_fields),
        "selected_evidence_repetition": {
            "source_fields": {k: {"populated_rows": sum(c.values()), "distinct_exact_strings": len(c)} for k, c in source_strings.items()},
            "venue_name_location_exact_keys": len(venue_keys), "song_name_exact_keys": len(song_keys),
            "source_and_track_show_families": len(families),
            "source_and_track_family_repeated_rows": sum(n - 1 for n in families.values()),
        },
        "record_kinds": dict(kinds),
        "sampled_setlist_memberships": sampled_memberships,
        "sampled_track_context": dict(track_context),
        "limits": [
            "Raw inputs are unlabeled. Counts do not establish extraction accuracy or identity.",
            "Equal per-artist caps deliberately oversample sparse artists; unweighted estimates do not describe a random recording.",
            "Configured provider mappings describe artist configuration, not ownership of individual recordings.",
            "Track samples include active rows with missing source/set context; active is not proof of playable catalog data.",
            "Setlist memberships have no stored ordering; song-ID ordering in JSON is only stable serialization.",
            "Exact string repetition and artist/show family keys are review aids, not exhaustive identity or leakage detection.",
            "This production snapshot does not include newer local Jerry and Aadam imports already retained in evaluation evidence.",
        ],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = summarize(args.directory)
    with args.output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, sort_keys=True)
        stream.write("\n")
    print(json.dumps({name: {key: row[key] for key in ("sampled_rows", "eligible_artists", "sampled_artists")} for name, row in report["coverage"].items()}, indent=2))
