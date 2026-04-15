"""
Analyze collection creators and classify them as performers, tapers, or ambiguous.

Reads from data/ directory (output of fetch_collection.py and fetch_relisten_artists.py)
and produces classification files in review/ directory.

Usage:
    python3 analyze_creators.py
"""

import json
import re
import unicodedata
from collections import defaultdict
from pathlib import Path

from rapidfuzz import fuzz

DATA_DIR = Path(__file__).parent / "data"
REVIEW_DIR = Path(__file__).parent / "review"

# Minimum items for auto-classification as performer or taper.
# Below this threshold, classify as ambiguous unless exact match.
MIN_ITEMS_FOR_AUTO = 3

# Fuzzy match threshold for matching against existing Relisten artists
FUZZY_MATCH_THRESHOLD = 90

# What percentage of titles must contain/not-contain the creator
# for auto-classification
TITLE_MATCH_RATIO_PERFORMER = 0.8
TITLE_MATCH_RATIO_TAPER = 0.8

# Patterns that suggest a taper/uploader rather than a performer
TAPER_PATTERNS = [
    r"\bteam\b",
    r"\bblog\b",
    r"\btaper\b",
    r"\brecord(?:ing|er|ed)\b",
    r"\bdpa\d+\b",
    r"\bschoeps\b",
    r"\bneumannb",
    r"\bmk\d+\b",
    r"\baud\b",
    r"\bsbd\b",
    r"\bflac\b",
    r"\b(?:24|16)bit\b",
    r"\bmatrix\b",
]


def normalize_name(name: str) -> str:
    """Normalize an artist/creator name for comparison."""
    s = name.strip().lower()
    # Remove leading "the "
    s = re.sub(r"^the\s+", "", s)
    # Normalize unicode
    s = unicodedata.normalize("NFKD", s)
    # Remove punctuation except spaces
    s = re.sub(r"[^\w\s]", "", s)
    # Collapse whitespace
    s = re.sub(r"\s+", " ", s).strip()
    return s


def title_contains_creator(title: str, creator: str) -> bool:
    """Check if the title contains the creator name (fuzzy)."""
    title_lower = title.lower()
    creator_lower = creator.lower()

    # Exact substring
    if creator_lower in title_lower:
        return True

    # Normalized substring
    if normalize_name(creator) in normalize_name(title):
        return True

    # Check if title starts with something close to the creator
    title_prefix = title_lower[: len(creator_lower) + 10]
    if fuzz.partial_ratio(creator_lower, title_prefix) >= 85:
        return True

    return False


def looks_like_taper_name(creator: str) -> bool:
    """Check if a creator name matches taper/equipment patterns."""
    creator_lower = creator.lower()
    for pattern in TAPER_PATTERNS:
        if re.search(pattern, creator_lower):
            return True

    # Check for username-like patterns (no spaces, camelCase, etc.)
    if " " not in creator.strip() and len(creator) > 3:
        # Could be a username like "vwmule" or "flatresponse"
        if creator.islower() or (creator[0].islower() and any(c.isupper() for c in creator[1:])):
            return True

    return False


def analyze_creator(
    creator: str,
    items: list[dict],
    relisten_artists: dict[str, dict],
    relisten_normalized: dict[str, dict],
    collection_id: str,
) -> dict:
    """Analyze a single creator and produce a classification record."""
    record = {
        "creator_raw": creator,
        "collection": collection_id,
        "item_count": len(items),
        "sample_titles": [item.get("title", "") for item in items[:5]],
        "sample_identifiers": [item.get("identifier", "") for item in items[:5]],
    }

    normalized = normalize_name(creator)

    # Check 1: Exact match against existing Relisten artists
    if creator in relisten_artists:
        match = relisten_artists[creator]
        record.update(
            {
                "classification": "matched",
                "matched_artist_name": match["name"],
                "matched_artist_uuid": match["uuid"],
                "confidence": 1.0,
                "reason": "exact_match_existing_artist",
            }
        )
        return record

    # Check 2: Normalized match against existing Relisten artists
    if normalized in relisten_normalized:
        match = relisten_normalized[normalized]
        record.update(
            {
                "classification": "matched",
                "matched_artist_name": match["name"],
                "matched_artist_uuid": match["uuid"],
                "confidence": 0.95,
                "reason": "normalized_match_existing_artist",
            }
        )
        return record

    # Check 3: Fuzzy match against existing Relisten artists
    best_score = 0
    best_match = None
    for artist_name, artist in relisten_artists.items():
        score = fuzz.ratio(normalized, normalize_name(artist_name))
        if score > best_score:
            best_score = score
            best_match = artist

    if best_score >= FUZZY_MATCH_THRESHOLD and best_match:
        record.update(
            {
                "classification": "matched",
                "matched_artist_name": best_match["name"],
                "matched_artist_uuid": best_match["uuid"],
                "confidence": best_score / 100.0,
                "reason": f"fuzzy_match_existing_artist (score={best_score})",
            }
        )
        return record

    # Check 4: Does the creator name appear in taper patterns?
    if looks_like_taper_name(creator):
        record.update(
            {
                "classification": "likely_taper",
                "confidence": 0.7,
                "reason": "name_matches_taper_pattern",
            }
        )
        return record

    # Check 5: Title analysis — does the creator appear in their item titles?
    titles_with_creator = sum(
        1 for item in items if title_contains_creator(item.get("title", ""), creator)
    )
    title_match_ratio = titles_with_creator / len(items) if items else 0

    record["title_match_ratio"] = round(title_match_ratio, 3)

    if len(items) >= MIN_ITEMS_FOR_AUTO:
        if title_match_ratio >= TITLE_MATCH_RATIO_PERFORMER:
            record.update(
                {
                    "classification": "likely_performer",
                    "confidence": min(0.9, title_match_ratio),
                    "reason": f"creator_in_titles ({titles_with_creator}/{len(items)})",
                }
            )
            return record

        if title_match_ratio <= (1 - TITLE_MATCH_RATIO_TAPER):
            record.update(
                {
                    "classification": "likely_taper",
                    "confidence": min(0.9, 1 - title_match_ratio),
                    "reason": f"creator_not_in_titles ({titles_with_creator}/{len(items)})",
                }
            )
            return record

    # Check 6: Too few items or ambiguous title match
    record.update(
        {
            "classification": "ambiguous",
            "confidence": 0.0,
            "reason": f"insufficient_signal (items={len(items)}, title_match={title_match_ratio:.0%})",
        }
    )
    return record


def load_collection(collection_id: str) -> list[dict]:
    """Load a fetched collection from data/ directory."""
    path = DATA_DIR / f"{collection_id}_items.json"
    if not path.exists():
        print(f"ERROR: {path} not found. Run fetch_collection.py {collection_id} first.")
        raise SystemExit(1)

    with open(path) as f:
        data = json.load(f)
    return data["items"]


def load_relisten_artists() -> list[dict]:
    """Load fetched Relisten artists from data/ directory."""
    path = DATA_DIR / "relisten_artists.json"
    if not path.exists():
        print(f"ERROR: {path} not found. Run fetch_relisten_artists.py first.")
        raise SystemExit(1)

    with open(path) as f:
        data = json.load(f)
    return data["artists"]


def group_by_creator(items: list[dict]) -> dict[str, list[dict]]:
    """Group items by creator string."""
    groups: dict[str, list[dict]] = defaultdict(list)
    for item in items:
        creator = item.get("creator", "UNKNOWN")
        if isinstance(creator, list):
            creator = "; ".join(creator)
        groups[creator].append(item)
    return dict(groups)


def main():
    REVIEW_DIR.mkdir(exist_ok=True)

    # Load existing Relisten artists
    artists_list = load_relisten_artists()
    relisten_by_name: dict[str, dict] = {}
    relisten_by_normalized: dict[str, dict] = {}
    for artist in artists_list:
        relisten_by_name[artist["name"]] = artist
        relisten_by_normalized[normalize_name(artist["name"])] = artist

    print(f"Loaded {len(relisten_by_name)} existing Relisten artists")

    all_records: list[dict] = []

    for collection_id in ["taperssection", "aadamjacobs"]:
        path = DATA_DIR / f"{collection_id}_items.json"
        if not path.exists():
            print(f"Skipping {collection_id} (not fetched yet)")
            continue

        items = load_collection(collection_id)
        by_creator = group_by_creator(items)
        print(f"\n{collection_id}: {len(items)} items, {len(by_creator)} unique creators")

        for creator, creator_items in sorted(by_creator.items(), key=lambda x: -len(x[1])):
            record = analyze_creator(
                creator, creator_items, relisten_by_name, relisten_by_normalized, collection_id
            )
            all_records.append(record)

    # Split into review files by classification
    matched = [r for r in all_records if r["classification"] == "matched"]
    likely_performers = [r for r in all_records if r["classification"] == "likely_performer"]
    likely_tapers = [r for r in all_records if r["classification"] == "likely_taper"]
    ambiguous = [r for r in all_records if r["classification"] == "ambiguous"]

    for name, records in [
        ("auto_matched", matched),
        ("likely_performers", likely_performers),
        ("likely_tapers", likely_tapers),
        ("ambiguous", ambiguous),
    ]:
        path = REVIEW_DIR / f"{name}.json"
        with open(path, "w") as f:
            json.dump(records, f, indent=2, default=str)
        print(f"Wrote {len(records)} records to {path}")

    # Summary stats
    stats = {
        "total_creators": len(all_records),
        "matched": len(matched),
        "likely_performer": len(likely_performers),
        "likely_taper": len(likely_tapers),
        "ambiguous": len(ambiguous),
        "total_items_matched": sum(r["item_count"] for r in matched),
        "total_items_likely_performer": sum(r["item_count"] for r in likely_performers),
        "total_items_likely_taper": sum(r["item_count"] for r in likely_tapers),
        "total_items_ambiguous": sum(r["item_count"] for r in ambiguous),
        "by_collection": {},
    }

    for collection_id in ["taperssection", "aadamjacobs"]:
        coll_records = [r for r in all_records if r["collection"] == collection_id]
        stats["by_collection"][collection_id] = {
            "total_creators": len(coll_records),
            "matched": len([r for r in coll_records if r["classification"] == "matched"]),
            "likely_performer": len(
                [r for r in coll_records if r["classification"] == "likely_performer"]
            ),
            "likely_taper": len(
                [r for r in coll_records if r["classification"] == "likely_taper"]
            ),
            "ambiguous": len([r for r in coll_records if r["classification"] == "ambiguous"]),
        }

    stats_path = REVIEW_DIR / "stats.json"
    with open(stats_path, "w") as f:
        json.dump(stats, f, indent=2)

    print(f"\nSummary: {json.dumps(stats, indent=2)}")


if __name__ == "__main__":
    main()
