# Collection Creator Analysis

Tooling to analyze Archive.org collection creators (taperssection, aadamjacobs) and produce a canonical mapping file for the Relisten collection importer.

## Goal

Produce a `mappings.json` file that maps every `creator` string from these collections to one of:
- An existing Relisten artist (by UUID)
- A new artist to create (with canonical name)
- A blocked entry (taper name, low quality, etc.)

This mapping seeds the `collection_artist_mappings` table before the first import.

## Quick Start

```bash
cd tools/collection-analysis
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt

# Step 1: Pull all items from archive.org
python3 fetch_collection.py taperssection
python3 fetch_collection.py aadamjacobs

# Step 2: Pull existing Relisten artists from production
python3 fetch_relisten_artists.py

# Step 3: Run the analysis
python3 analyze_creators.py

# Step 4: Review the output
# - review/auto_matched.json    → creators matched to existing artists
# - review/likely_performers.json → creators that look like real artists
# - review/likely_tapers.json   → creators that look like taper/uploader names
# - review/ambiguous.json       → needs manual review
# - review/stats.json           → summary statistics

# Step 5: After manual review, generate final mapping
python3 generate_mappings.py
# → mappings.json (ready to seed into database)
```

## Analysis Guidelines

See `ANALYSIS_GUIDELINES.md` for the heuristics and decision framework.
