#!/usr/bin/env python3
"""Validate a complete all-artist census and write sanitized totals and profiles.

Pass the exact SQL snapshot saved before the query, not the current query file.
Raw JSONL and SQL snapshots stay outside Git. Output contains catalog identities,
configured artist providers and counts, without descriptions or credit text.
"""

import argparse
import csv
import hashlib
import json
from pathlib import Path


RELATIONS = {
    "sources": ("sources", "sources"),
    "shows": ("shows", "shows"),
    "source_sets": ("source_sets", "source_sets"),
    "source_tracks": ("tracks_total", "tracks"),
    "venues": ("venue_rows", "venues"),
    "setlist_songs": ("song_rows", "songs"),
    "setlist_shows": ("setlist_shows", "setlist_shows"),
    "setlist_songs_plays": ("setlist_songs_plays", "setlist_songs_plays"),
}
TRACK_STATES = ("active_tracks", "orphaned_tracks", "unknown_orphan_state")
ACTIVE_TRACK_MEASURES = (
    "active_missing_title", "compound_title_candidates", "with_mp3_md5",
    "with_flac_md5", "with_positive_duration", "active_without_source",
    "active_source_artist_mismatch", "active_without_matching_set",
    "active_without_media_reference", "active_with_catalog_playback_links",
)
TRACK_MEASURES = TRACK_STATES + ACTIVE_TRACK_MEASURES
SOURCE_MEASURES = (
    "with_taper_text", "with_transferrer_text", "with_lineage_text",
    "with_source_text", "with_lineage_or_source_text", "with_notes_text",
    "with_description", "partial_date_sources", "without_show",
)
VENUE_MEASURES = (
    "venues_with_coordinates", "venues_with_location",
    "venues_with_provider_identifier", "venues_with_past_names",
)
MEASURES = TRACK_MEASURES + SOURCE_MEASURES + VENUE_MEASURES
COUNT_FIELDS = set(MEASURES) | {field for field, _ in RELATIONS.values()} | {
    "active_title_keys", "song_title_keys", "songs_with_provider_identifier",
}
SECTIONS = {"snapshot", "schema", "totals", "unattributed", "snapshot_complete"}


def read_snapshot(census_path):
    raw = census_path.read_bytes()
    rows = [json.loads(line) for line in raw.splitlines() if line.strip()]
    if not rows or rows[-1].get("kind") != "snapshot_complete":
        raise ValueError("The census is incomplete; preserve its output and rerun.")
    if rows[0].get("kind") != "snapshot":
        raise ValueError("The census must begin with its snapshot identity.")

    sections, profiles = {}, []
    for row in rows:
        kind = row["kind"]
        if kind == "artist":
            profiles.append(row["profile"])
        elif kind not in SECTIONS:
            raise ValueError(f"Unknown census section: {kind}")
        elif kind in sections:
            raise ValueError(f"Duplicate census section: {kind}")
        else:
            sections[kind] = row
    if sections.keys() != SECTIONS:
        raise ValueError(f"Missing census sections: {sorted(SECTIONS - sections.keys())}")
    snapshot = sections["snapshot"]
    if snapshot["read_only"] != "on" or snapshot["in_recovery"] is not True:
        raise ValueError("Expected a read-only replica snapshot.")
    return raw, sections, profiles


def count(value, label):
    if type(value) is not int or value < 0:
        raise ValueError(f"Expected a nonnegative integer count for {label}.")
    return value


def validate_profiles(sections, profiles):
    totals = sections["totals"]["rows"]
    unattributed = sections["unattributed"]["rows"]
    for table in ("artists", *RELATIONS):
        count(totals[table], f"totals.{table}")
    if len(profiles) != totals["artists"]:
        raise ValueError("Artist profiles do not cover every catalog artist.")
    for identity in ("id", "uuid"):
        if (
            any(p.get(identity) is None for p in profiles)
            or len({p[identity] for p in profiles}) != len(profiles)
        ):
            raise ValueError(f"Artist {identity} identities are missing or not unique.")

    # Validate each artist before aggregating so offsetting errors cannot cancel out.
    for profile in profiles:
        artist = profile["id"]
        for field in COUNT_FIELDS:
            count(profile.get(field, 0), f"artist {artist}.{field}")
        if sum(profile.get(field, 0) for field in TRACK_STATES) != profile.get("tracks_total", 0):
            raise ValueError(f"Track states do not partition artist {artist}'s tracks.")
        for fields, bound in (
            (ACTIVE_TRACK_MEASURES + ("active_title_keys",), "active_tracks"),
            (SOURCE_MEASURES, "sources"),
            (VENUE_MEASURES, "venue_rows"),
            (("song_title_keys", "songs_with_provider_identifier"), "song_rows"),
        ):
            for field in fields:
                if profile.get(field, 0) > profile.get(bound, 0):
                    raise ValueError(f"Artist {artist}.{field} exceeds {bound}.")
                # SQL omits a whole aggregate only for artists with no corresponding rows.
                if profile.get(bound, 0) > 0 and field not in profile:
                    raise ValueError(f"Artist {artist} is missing {field}.")

    reconciled = {"artists": len(profiles)}
    for table, (field, unassigned) in RELATIONS.items():
        missing_artist = count(unattributed[unassigned], f"unattributed.{unassigned}")
        attributed = sum(p.get(field, 0) for p in profiles)
        if attributed + missing_artist != totals[table]:
            raise ValueError(
                f"All-artist profiles do not reconcile for {table}: "
                f"{attributed} + {missing_artist} != {totals[table]}."
            )
        reconciled[table] = attributed + missing_artist
    return reconciled


def make_summary(raw, sql_path, sections, profiles, reconciled):
    aggregate = {key: sum(p.get(key, 0) for p in profiles) for key in MEASURES}
    return {
        "snapshot": sections["snapshot"],
        "completed_at": sections["snapshot_complete"]["completed_at"],
        "census_sha256": hashlib.sha256(raw).hexdigest(),
        "sql_sha256": hashlib.sha256(sql_path.read_bytes()).hexdigest(),
        "sql_snapshot": sql_path.name,
        "coverage": "Every catalog artist and all rows in the named relations; no artist or row sampling.",
        "totals": sections["totals"]["rows"],
        "unattributed": sections["unattributed"]["rows"],
        "reconciled_profile_totals": reconciled,
        "field_presence": aggregate,
        "artist_coverage": {
            "with_sources": sum(p.get("sources", 0) > 0 for p in profiles),
            "with_active_tracks": sum(p.get("active_tracks", 0) > 0 for p in profiles),
            "with_catalog_playback_links": sum(p.get("active_with_catalog_playback_links", 0) > 0 for p in profiles),
            "with_repertoire_rows": sum(p.get("song_rows", 0) > 0 for p in profiles),
            "active_without_repertoire": sum(p.get("active_tracks", 0) > 0 and not p.get("song_rows", 0) for p in profiles),
        },
        "limits": [
            "Active tracks means is_orphaned=false; this flag alone does not establish playable catalog data.",
            "Catalog playback links require matching source, artist, set and show associations and a media reference. They do not prove codec eligibility, file integrity or remote availability.",
            "Field-presence and playback-link measures aggregate artist-attributed rows; independent totals and unattributed rows cover the entire named relations.",
            "Link-defect counts can overlap and must not be added as a partition of active tracks.",
            "Field presence and syntactically valid MD5 strings are evidence availability, not validated identities or audio fingerprints.",
            "Configured artist providers do not establish which provider owns each recording.",
            "Compound-title patterns retrieve candidates; they do not establish songs or occurrences.",
            "This is the imported live catalog, not a census of every upstream item. New local Jerry/Aadam imports are separate evidence.",
            "This census begins the full normalization analysis; identity resolution, extraction accuracy and provider evidence recovery require further work.",
        ],
    }


def write_reports(output_directory, summary, schema, profiles):
    output_directory.mkdir(parents=True, exist_ok=True)
    (output_directory / "census-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (output_directory / "catalog-schema.json").write_text(json.dumps(schema, indent=2) + "\n", encoding="utf-8")
    fields = ["id", "uuid", "name", "slug", "musicbrainz_artist_id", "providers"]
    fields += sorted((set().union(*(p.keys() for p in profiles)) | COUNT_FIELDS) - set(fields))
    with (output_directory / "artist-profiles.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields, lineterminator="\n")
        writer.writeheader()
        for profile in profiles:
            record = dict(profile)
            for field in COUNT_FIELDS:
                record.setdefault(field, 0)
            record["providers"] = json.dumps(record["providers"], separators=(",", ":"))
            writer.writerow(record)


def summarize(census_path, sql_path, output_directory):
    # Read the explicit query snapshot before writing any derived artifacts.
    if not sql_path.is_file():
        raise ValueError("Pass the SQL snapshot saved before this census query.")
    raw, sections, profiles = read_snapshot(census_path)
    reconciled = validate_profiles(sections, profiles)
    summary = make_summary(raw, sql_path, sections, profiles, reconciled)
    write_reports(output_directory, summary, sections["schema"]["rows"], profiles)
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("census", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--sql", type=Path, required=True, help="Exact SQL snapshot saved before the query.")
    args = parser.parse_args()
    summary = summarize(args.census, args.sql, args.output)
    print(json.dumps({"totals": summary["totals"], "artist_coverage": summary["artist_coverage"]}, indent=2))


if __name__ == "__main__":
    main()
