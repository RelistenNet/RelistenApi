#!/usr/bin/env python3
"""Retain bounded public Archive metadata for local import review; never write a catalog.

The manifest contains proposed billing evidence, not approved performer decisions.
Keep raw provider responses outside the repository. Publication must use the normal
review, artist admission, recording writer and rebuild contracts.
"""

import argparse
import concurrent.futures
import datetime
import hashlib
import json
import pathlib
import re
import time
import urllib.parse
import urllib.request


PROJECT_LABELS = {
    "Jerry Garcia Band": ["Jerry Garcia Band"],
    "Legion of Mary": ["Legion Of Mary"],
    "Garcia/Saunders": ["Garcia & Saunders", "Garcia/Saunders", "Jerry Garcia & Merl Saunders"],
    "Garcia/Kahn": ["Garcia & Kahn", "Garcia/Kahn", "Jerry Garcia & John Kahn"],
    "Reconstruction": ["Reconstruction"],
    "Jerry Garcia Acoustic Band": ["Jerry Garcia Acoustic Band"],
}


def fetch(url):
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Relisten-local-acceptance/1.0"})
            with urllib.request.urlopen(request, timeout=45) as response:
                return response.read()
        except Exception:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def save_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")


def search_sample(query, count):
    parameters = [("q", query), ("output", "json"), ("rows", str(count)), ("sort[]", "identifier asc")]
    parameters.extend(("fl[]", field) for field in ("identifier", "creator", "title", "date", "year"))
    url = "https://archive.org/advancedsearch.php?" + urllib.parse.urlencode(parameters)
    raw = fetch(url)
    response = json.loads(raw)["response"]
    return response["docs"], {"url": url, "reported_total": response["numFound"], "sha256": hashlib.sha256(raw).hexdigest()}


def select_jerry(rows, count):
    # Sample the retained inventory evenly instead of selecting only its earliest years.
    rows = sorted(rows, key=lambda row: row["identifier"])
    sample_count = min(count, len(rows))
    selected = {rows[round(index * (len(rows) - 1) / max(sample_count - 1, 1))]["identifier"] for index in range(sample_count)}
    # Include previously reviewed examples and explicit same-day/partial-date evidence.
    selected.update(row["identifier"] for row in rows if re.search(r"early|late|xx|compilation|jgab", row["identifier"], re.I))
    priority = [row for row in rows if row["identifier"] in selected]
    # Preserve explicit cases first, then evenly sampled ordinary recordings, within the bound.
    priority.sort(key=lambda row: (not bool(re.search(r"early|late|xx|compilation|jgab", row["identifier"], re.I)), row["identifier"]))
    return priority[:count]


def fields(metadata, field):
    value = metadata.get(field)
    if isinstance(value, str):
        yield "metadata." + field, value
    elif isinstance(value, list):
        for index, entry in enumerate(value):
            if isinstance(entry, str):
                yield f"metadata.{field}[{index}]", entry


def evidence(metadata):
    matches = []
    for field in ("creator", "title", "description"):
        for locator, value in fields(metadata, field):
            # An early billing heading is useful for review. A later historical mention
            # is not sufficient to assign a recording automatically.
            inspected = value[:1500] if field == "description" else value
            for project, labels in PROJECT_LABELS.items():
                for label in labels:
                    match = re.search(re.escape(label), inspected, re.I)
                    if match:
                        matches.append({"project": project, "locator": locator,
                                        "value": match.group(),
                                        "start": len(value[:match.start()].encode("utf-16-le")) // 2,
                                        "length": len(match.group().encode("utf-16-le")) // 2,
                                        "rule": "proposed_exact_project_billing"})
    return matches


def retain(row, output):
    identifier = row["identifier"]
    raw_path = output / "raw" / (identifier + ".json")
    try:
        raw = raw_path.read_bytes() if raw_path.exists() else fetch("https://archive.org/metadata/" + urllib.parse.quote(identifier, safe=""))
        raw_path.write_bytes(raw)
        item = json.loads(raw)
        metadata = item.get("metadata", {})
        description = "\n".join(value for _, value in fields(metadata, "description"))
        return {"identifier": identifier, "creator": metadata.get("creator"),
                "title": metadata.get("title"), "date": metadata.get("date"), "year": metadata.get("year"),
                "vbr_mp3_files": sum(file.get("format") == "VBR MP3" for file in item.get("files", [])),
                "metadata_sha256": hashlib.sha256(raw).hexdigest(),
                "proposed_billing_evidence": evidence(metadata),
                "performance_words": sorted(set(re.findall(r"\b(?:early|late|afternoon|matinee)\b", description[:1500], re.I))),
                "needs_combined_review": bool(re.search(r"early\s*(?:and|&)\s*late|late\s*(?:and|&)\s*early|acoustic\s*(?:and|&)\s*electric|compilation", description[:1500] + " " + str(metadata.get("title", "")), re.I))}
    except Exception as error:
        return {"identifier": identifier, "error": str(error)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sample", choices=("jerry", "aadam"))
    parser.add_argument("--count", type=int, default=300)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--jerry-inventory", type=pathlib.Path)
    args = parser.parse_args()
    if not 2 <= args.count <= 500:
        parser.error("Use a bounded sample between 2 and 500 items.")
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "raw").mkdir(exist_ok=True)
    if args.sample == "jerry":
        if not args.jerry_inventory:
            parser.error("Jerry needs an explicitly retained inventory file.")
        rows = select_jerry(json.loads(args.jerry_inventory.read_text()), args.count)
        search = {"inventory_sha256": hashlib.sha256(args.jerry_inventory.read_bytes()).hexdigest()}
    else:
        rows, search = search_sample("collection:aadamjacobs AND mediatype:audio", args.count)
    save_json(args.output / "selection.json", {"sample": args.sample, "search": search, "identifiers": [row["identifier"] for row in rows]})
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
        retained = list(executor.map(lambda row: retain(row, args.output), rows))
    save_json(args.output / "manifest.json", {"sample": args.sample, "prepared_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                                              "approval": "none; proposed evidence requires review", "items": retained})
    print(json.dumps({"sample": args.sample, "selected": len(rows), "retained": sum("error" not in item for item in retained),
                      "with_playable_files": sum(item.get("vbr_mp3_files", 0) > 0 for item in retained), "output": str(args.output)}))


if __name__ == "__main__":
    main()
