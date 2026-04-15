# Creator Analysis Guidelines

## The Problem

Archive.org's `creator` field on collection items is inconsistent. It can be:
- The performing artist ("Phish", "Gov't Mule")
- The taper/recorder ("Jamie Burks", "Bob Jacobson, NYLifer, Jake")
- A venue or event ("Optronica ATA")
- A combination or variant of the above

We need to classify each unique creator string and decide what to do with it.

## Classification Categories

### 1. `matched` — Maps to an existing Relisten artist
The creator name matches (exactly or with normalization) an existing artist in Relisten.
These are high confidence and should be imported.

Examples:
- "Phish" → existing artist Phish
- "Gov't Mule" → existing artist Gov't Mule (after normalization)

### 2. `new_artist` — Real performer, should create a new artist
The creator appears to be a legitimate performing artist but doesn't exist in Relisten yet.
These will be auto-created with the CollectionDerived flag.

### 3. `blocked` — Should not be imported
The creator is a taper, uploader, event name, or otherwise not a performing artist.

### 4. `ambiguous` — Needs manual review
Can't confidently classify automatically. Flag for human review.

## Heuristics

### Signals that a creator is a PERFORMER (import):

1. **Title pattern match**: The item title contains the creator name followed by "Live at" or a date.
   - `"Thurston Moore Band Live at Empty Bottle 2019-12-12"` with creator `"Thurston Moore Band"` → performer

2. **Consistent titling**: Most/all items for this creator follow the standard LMA title pattern:
   `{Artist} Live at {Venue} {Date}` or `{Artist} {Date}`

3. **Existing LMA collection**: The creator name matches an existing Archive.org LMA artist collection
   (even if not in Relisten). Check by looking at the item's `collection` field for artist-named collections.

4. **Multiple items with same creator**: A creator with 5+ items that all have consistent titling
   is very likely a performer.

5. **Creator appears in other LMA collections**: If items by this creator also appear in
   artist-specific collections (visible in the `collection` array on each item), strong performer signal.

### Signals that a creator is a TAPER/UPLOADER (block):

1. **Creator doesn't appear in title**: If the item title names a different artist than the creator field,
   the creator is likely the taper.
   - Title: `"The Black Keys 2019-10-12 dpa4060"`, creator: `"Bob Jacobson, NYLifer, Jake"` → creator is taper

2. **Items span many different artists**: If a creator's items have wildly different artist names
   in the titles, the creator is a taper recording different bands.

3. **Equipment in creator name**: Names containing microphone models, recorder brands, or technical
   terms (e.g., "dpa4060", "Schoeps") are taper identifiers.

4. **Username-like patterns**: Handles like "vwmule", "flatresponse", "HighStandDave" are usernames.

5. **"Team" or "Blog" in name**: "Team Dirty South", "Funk It Blog" are taping crews, not artists.

### Signals that need MANUAL REVIEW:

1. **Low item count (1-2) with ambiguous title**: Could be either a one-off taper or an obscure artist.

2. **Creator name is a real name** (e.g., "Chris Davis"): Could be a performer or a taper.
   Need to check titles.

3. **Creator matches an artist but items don't**: Creator is "Jerry Garcia" but items
   are from a tribute band.

## Thresholds

- **Auto-match confidence**: If normalized creator name matches an existing Relisten artist name
  with >= 0.9 similarity, auto-classify as `matched`.
- **Performer confidence**: If >= 80% of a creator's items have titles starting with the creator name,
  classify as `new_artist`.
- **Taper confidence**: If >= 80% of a creator's items have titles that DON'T contain the creator name,
  classify as `blocked`.
- **Minimum items for auto-classification**: Creators with < 3 items should be `ambiguous` unless
  they exactly match an existing artist.

## Output Format

Each creator gets a record in the review files:

```json
{
  "creator_raw": "Gov't Mule",
  "collection": "taperssection",
  "item_count": 95,
  "classification": "matched",
  "matched_artist_name": "Gov't Mule",
  "matched_artist_uuid": "...",
  "confidence": 0.95,
  "reason": "exact_match_existing_artist",
  "sample_titles": [
    "Gov't Mule Live at The Fillmore 2024-03-15",
    "Gov't Mule Live at Red Rocks 2023-08-12"
  ],
  "sample_identifiers": [
    "govtmule2024-03-15.flac24",
    "govtmule2023-08-12.mk4"
  ]
}
```

## Final Mapping Format

The `mappings.json` output file has this structure:

```json
{
  "metadata": {
    "generated_at": "2026-04-11T...",
    "taperssection_total_creators": 1247,
    "aadamjacobs_total_creators": 1189,
    "matched": 150,
    "new_artist": 800,
    "blocked": 200,
    "ambiguous": 97
  },
  "mappings": [
    {
      "collection": "taperssection",
      "creator_raw": "Phish",
      "action": "match",
      "artist_uuid": "...",
      "canonical_name": "Phish",
      "manually_verified": true
    },
    {
      "collection": "taperssection",
      "creator_raw": "Jamie Burks",
      "action": "block",
      "block_reason": "taper name, not a performing artist",
      "manually_verified": true
    },
    {
      "collection": "taperssection",
      "creator_raw": "Strange Pleasures",
      "action": "create",
      "canonical_name": "Strange Pleasures",
      "manually_verified": false
    }
  ]
}
```
