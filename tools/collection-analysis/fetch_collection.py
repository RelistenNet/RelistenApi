"""
Fetch all items from an Archive.org collection and save to a local JSON file.

Usage:
    python3 fetch_collection.py taperssection
    python3 fetch_collection.py aadamjacobs
"""

import json
import sys
import time
from pathlib import Path

import requests

ARCHIVE_SCRAPE_URL = "https://archive.org/services/search/v1/scrape"
FIELDS = [
    "identifier",
    "title",
    "creator",
    "date",
    "year",
    "collection",
    "publicdate",
    "downloads",
    "item_size",
    "subject",
]
PAGE_SIZE = 1000
DATA_DIR = Path(__file__).parent / "data"


def fetch_collection(collection_id: str) -> list[dict]:
    """Fetch all items from an archive.org collection using cursor-based search."""
    all_docs = []
    cursor = None
    page = 1

    while True:
        params = {
            "q": f"collection:{collection_id}",
            "fields": ",".join(FIELDS),
            "count": PAGE_SIZE,
        }
        if cursor:
            params["cursor"] = cursor

        print(f"Fetching page {page}...", end=" ", flush=True)
        resp = requests.get(ARCHIVE_SCRAPE_URL, params=params, timeout=120)
        resp.raise_for_status()
        data = resp.json()

        if "error" in data:
            raise RuntimeError(data["error"])

        docs = data.get("items", [])
        num_found = data.get("total", 0)
        print(f"got {len(docs)} items (total: {num_found})")

        if not docs:
            break

        all_docs.extend(docs)

        cursor = data.get("cursor")
        if not cursor:
            break

        page += 1
        time.sleep(1)  # be polite to archive.org

    return all_docs


def main():
    if len(sys.argv) != 2:
        print(f"Usage: {sys.argv[0]} <collection_id>")
        print("Example: python3 fetch_collection.py taperssection")
        sys.exit(1)

    collection_id = sys.argv[1]

    DATA_DIR.mkdir(exist_ok=True)

    print(f"Fetching collection: {collection_id}")
    items = fetch_collection(collection_id)

    output_path = DATA_DIR / f"{collection_id}_items.json"
    with open(output_path, "w") as f:
        json.dump(
            {
                "collection": collection_id,
                "fetched_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "total_items": len(items),
                "items": items,
            },
            f,
            indent=2,
        )

    print(f"Saved {len(items)} items to {output_path}")

    # Print quick creator summary
    creators: dict[str, int] = {}
    for item in items:
        creator = item.get("creator", "UNKNOWN")
        if isinstance(creator, list):
            creator = "; ".join(creator)
        creators[creator] = creators.get(creator, 0) + 1

    print(f"\nUnique creators: {len(creators)}")
    print("\nTop 20 creators:")
    for creator, count in sorted(creators.items(), key=lambda x: -x[1])[:20]:
        print(f"  {count:5d}  {creator}")


if __name__ == "__main__":
    main()
