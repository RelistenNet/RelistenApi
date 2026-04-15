"""
Generate the final mappings.json from reviewed classification files.

Before running this, review the files in review/ and make edits:
- Move records between files if the auto-classification was wrong
- Edit classification fields as needed
- Add canonical_name overrides where the raw creator isn't the best name

Usage:
    python3 generate_mappings.py
"""

import json
import time
from pathlib import Path

REVIEW_DIR = Path(__file__).parent / "review"
OUTPUT_PATH = Path(__file__).parent / "mappings.json"


def load_review_file(filename: str) -> list[dict]:
    """Load a review JSON file."""
    path = REVIEW_DIR / filename
    if not path.exists():
        return []
    with open(path) as f:
        return json.load(f)


def main():
    matched = load_review_file("auto_matched.json")
    performers = load_review_file("likely_performers.json")
    tapers = load_review_file("likely_tapers.json")
    ambiguous = load_review_file("ambiguous.json")

    mappings = []

    # Matched → match action
    for record in matched:
        mappings.append(
            {
                "collection": record["collection"],
                "creator_raw": record["creator_raw"],
                "action": "match",
                "artist_uuid": record.get("matched_artist_uuid"),
                "canonical_name": record.get("matched_artist_name", record["creator_raw"]),
                "manually_verified": record.get(
                    "manually_verified", record.get("confidence", 0) >= 0.95
                ),
                "item_count": record["item_count"],
            }
        )

    # Likely performers → create action
    for record in performers:
        mappings.append(
            {
                "collection": record["collection"],
                "creator_raw": record["creator_raw"],
                "action": "create",
                "canonical_name": record.get("canonical_name", record["creator_raw"]),
                "manually_verified": record.get("manually_verified", False),
                "item_count": record["item_count"],
            }
        )

    # Likely tapers → block action
    for record in tapers:
        mappings.append(
            {
                "collection": record["collection"],
                "creator_raw": record["creator_raw"],
                "action": "block",
                "block_reason": record.get("reason", "classified as taper/uploader"),
                "manually_verified": record.get("manually_verified", False),
                "item_count": record["item_count"],
            }
        )

    # Ambiguous → flag for review (keep as ambiguous in output)
    for record in ambiguous:
        mappings.append(
            {
                "collection": record["collection"],
                "creator_raw": record["creator_raw"],
                "action": "ambiguous",
                "canonical_name": record["creator_raw"],
                "manually_verified": record.get("manually_verified", False),
                "item_count": record["item_count"],
                "note": record.get("reason", "needs manual review"),
            }
        )

    # Sort by collection then by item count descending
    mappings.sort(key=lambda m: (m["collection"], -m["item_count"]))

    # Build summary
    metadata = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "total_creators": len(mappings),
        "by_action": {
            "match": len([m for m in mappings if m["action"] == "match"]),
            "create": len([m for m in mappings if m["action"] == "create"]),
            "block": len([m for m in mappings if m["action"] == "block"]),
            "ambiguous": len([m for m in mappings if m["action"] == "ambiguous"]),
        },
        "by_collection": {},
    }

    for collection in set(m["collection"] for m in mappings):
        coll = [m for m in mappings if m["collection"] == collection]
        metadata["by_collection"][collection] = {
            "total": len(coll),
            "match": len([m for m in coll if m["action"] == "match"]),
            "create": len([m for m in coll if m["action"] == "create"]),
            "block": len([m for m in coll if m["action"] == "block"]),
            "ambiguous": len([m for m in coll if m["action"] == "ambiguous"]),
            "total_items": sum(m["item_count"] for m in coll),
        }

    output = {"metadata": metadata, "mappings": mappings}

    with open(OUTPUT_PATH, "w") as f:
        json.dump(output, f, indent=2)

    print(f"Generated {OUTPUT_PATH}")
    print(f"  Total mappings: {len(mappings)}")
    print(f"  Match: {metadata['by_action']['match']}")
    print(f"  Create: {metadata['by_action']['create']}")
    print(f"  Block: {metadata['by_action']['block']}")
    print(f"  Ambiguous: {metadata['by_action']['ambiguous']}")

    # Flag high-priority review items
    high_priority = [
        m
        for m in mappings
        if m["action"] == "ambiguous" and m["item_count"] >= 5
    ]
    if high_priority:
        print(f"\n  High-priority review ({len(high_priority)} creators with 5+ items):")
        for m in sorted(high_priority, key=lambda x: -x["item_count"])[:20]:
            print(f"    {m['item_count']:5d}  {m['creator_raw']}  ({m['collection']})")


if __name__ == "__main__":
    main()
