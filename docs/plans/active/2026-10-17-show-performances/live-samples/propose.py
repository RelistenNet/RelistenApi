#!/usr/bin/env python3
"""Write unapproved batch-review proposals from a retained metadata manifest.

No approval, database operation or network request occurs. Review the audit text,
project identities and held items before editing a copy of the proposed JSON plan.
"""

import argparse
import collections
import json
import pathlib
import re
import unicodedata


PROJECTS = {
    "Jerry Garcia Band": ("jerry-garcia-band", ["Jerry Garcia Band"]),
    "Legion of Mary": ("legion-of-mary", ["Legion of Mary"]),
    # Preserve the labels already approved in the three-recording local proof.
    "Garcia/Saunders": ("garcia-saunders", ["Garcia/Saunders", "Garcia & Saunders"]),
    "Garcia/Kahn": ("garcia-kahn", ["Garcia/Kahn", "Garcia & Kahn", "Jerry Garcia & John Kahn"]),
    "Reconstruction": ("reconstruction", ["Reconstruction"]),
    "Jerry Garcia Acoustic Band": ("jerry-garcia-acoustic-band", ["Jerry Garcia Acoustic Band"]),
}

# These bills need more than a structured creator value to establish one artist.
AADAM_REVIEW = {
    "Various Artists", "A Child's Christmas at Hideout", "Carter Family Family Show",
    "Thax After Dark", "Thax Douglas 1957 Show", "Guitar Trio",
    "Cheer-Accident with Lovely Little Girls", "Frank Catalano, Jimmy Chamberlin and Percy Jones",
    "Hushdrops and Dag Juhlin", "Rick Rizzo and Janet Bean", "Rick Rizzo and Nicholas Tremulis",
    "Rick Rizzo, Janet Bean, and Ryan Weinstein", "Breeders",
}


def slug(name):
    text = unicodedata.normalize("NFKD", name).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def retained_field(raw, locator):
    field = locator.removeprefix("metadata.")
    if "[" in field:
        name, index = field.removesuffix("]").split("[")
        return raw["metadata"][name][int(index)]
    return raw["metadata"][field]


def jerry_proposal(item, raw):
    projects = set(evidence["project"] for evidence in item["proposed_billing_evidence"])
    if len(projects) != 1:
        return None, "missing or multiple project names"
    if item["needs_combined_review"] or re.search(r"interview|early[-_/]late|late[-_/]early", item["identifier"], re.I):
        return None, "combined recording, compilation or interview"
    project = next(iter(projects))
    approved_labels = {label.upper() for label in PROJECTS[project][1]}
    evidence = [entry for entry in item["proposed_billing_evidence"] if entry["value"].upper() in approved_labels]
    if not evidence:
        return None, "project label is outside the retained reviewed vocabulary"
    chosen = min(evidence, key=lambda entry: 0 if entry["locator"].startswith("metadata.title") else entry["start"] + 1)
    if chosen["locator"].startswith("metadata.description") and chosen["start"] > 200:
        return None, "project name appears later in the description; verify billing"
    field = retained_field(raw, chosen["locator"])
    # The audit preserves nearby raw context. Operators must still distinguish a
    # billing heading from an incidental mention before setting approval true.
    start = field.lower().find(chosen["value"].lower())
    context = field[max(0, start - 80):start + len(chosen["value"]) + 140]
    return {"project": project, "projectSlug": PROJECTS[project][0],
            "evidence": [{key: value for key, value in chosen.items() if key != "project"}],
            "context": context}, None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=pathlib.Path)
    args = parser.parse_args()
    manifest = json.loads((args.directory / "manifest.json").read_text())
    projects = {}
    proposed = []
    held = []
    audit = []
    for item in manifest["items"]:
        identifier = item["identifier"]
        if item.get("error") or not item.get("vbr_mp3_files"):
            held.append({"identifier": identifier, "reason": "metadata unavailable or no VBR MP3"})
            continue
        raw = json.loads((args.directory / "raw" / (identifier + ".json")).read_text())
        if manifest["sample"] == "jerry":
            proposal, reason = jerry_proposal(item, raw)
            if reason:
                held.append({"identifier": identifier, "reason": reason})
                continue
            name = proposal["project"]
            project_slug, labels = PROJECTS[name]
            evidence, context = proposal["evidence"], proposal["context"]
        else:
            name = item["creator"]
            if not isinstance(name, str) or name in AADAM_REVIEW:
                held.append({"identifier": identifier, "reason": "unresolved event, joint billing or alternate artist name"})
                continue
            project_slug, labels = slug(name), [name]
            evidence = [{"locator": "metadata.creator", "value": name, "rule": "proposed_structured_performer"}]
            context = item["title"]
        projects.setdefault(project_slug, {"name": name, "slug": project_slug, "labels": labels, "approved": False})
        if projects[project_slug]["name"] != name:
            raise ValueError("Two proposed identities share a slug; review before creating either artist.")
        proposed.append({"identifier": identifier, "projectSlug": project_slug, "evidence": evidence, "approved": False, "alternatives": []})
        audit.append({"identifier": identifier, "project": name, "billing_context": context})

    if manifest["sample"] == "aadam" and "wire" in projects:
        projects["wire"]["createExistingArtistFixture"] = True
    plan = {"reviewBasis": "REPLACE after reviewing the exact billing and project identities for this bounded local sample.",
            "projects": list(projects.values()), "items": proposed}
    for name, value in (("proposed-review-plan.json", plan), ("held-for-review.json", held), ("billing-audit.json", audit)):
        (args.directory / name).write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    print(json.dumps({"sample": manifest["sample"], "unapproved_projects": len(projects), "unapproved_items": len(proposed),
                      "held": len(held), "project_counts": dict(collections.Counter(item["projectSlug"] for item in proposed))}))


if __name__ == "__main__":
    main()
