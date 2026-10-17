# Real catalog inputs for normalization development

These files preserve real imported values for developing and reviewing normalization contracts. They are a deliberately selected challenge set. Their size and selection do not support claims about catalog-wide accuracy.

The capture contains 130 records across 24 artists:

- `production/evidence.jsonl`: 106 records across 16 artists, captured from the production read-only replica at 2026-10-17 16:50:48 UTC. Includes 34 sources, 32 tracks, 20 venues, 16 repertoire rows and four provider setlists.
- `venue-supplement/evidence.jsonl`: eight Fox and Orpheum venue records across four of those artists, captured at 16:51:52 UTC. This provides venue families separate from the Fillmore and Madison Square Garden development examples.
- `local/evidence.jsonl`: 16 records across eight additional artists, captured from `relisten_jerry_20261017` at 16:52:08 UTC. Includes eight sources and eight tracks: four Jerry Garcia projects and four artists represented in the Aadam Jacobs collection. These are real Archive.org inputs previously imported during local acceptance work. Local row IDs use `local:` to prevent collisions with production IDs.

The production cohort includes Phish, Grateful Dead, Widespread Panic, Furthur, Disco Biscuits, moe., Tedeschi Trucks Band, Ratdog, Ween, Godspeed You Black Emperor!, Billy Strings and Yes. Choice And Company, 3fifths, Math the Band and Claudia Jane supply examples from artists with one to three production sources at the census snapshot. The local cohort adds Jerry Garcia Band, Legion of Mary, Garcia/Saunders, Reconstruction, Wire, The Eternals, Cheer-Accident and Tiny Fireflies.

## What the inputs can establish

Every record has its catalog row ID, artist, provider context, upstream identifier and exact selected fields. Each snapshot has a manifest with SHA-256 hashes and the exact executed SQL. Record IDs identify source rows within a database; the manifest identifies the captured revision. Text is not shortened, cleaned or rewritten. A missing field in this selection does not mean that the catalog or upstream provider has no such evidence.

Source fields contain imported assertions. For example, a named `taper` is evidence of a credited role, not independent proof of who operated the recorder. Equipment in Claudia Jane's `transferrer` field illustrates why a nonempty credit field does not always name a person. Equal names across artists are candidates for person resolution. They do not alone establish one identity.

The venue examples include repeated names in different cities, alternate locality spellings, unknown locality, and the Felt Forum within Madison Square Garden. These require decisions about physical venues and rooms. Provider labels follow importer identifier conventions; artist-provider associations remain available on production records where a row lacks explicit provider ownership.

Repertoire rows include repeated titles within an artist and titles shared across artists. They contain no writer or work identifiers. A bounded inspection of 1,800 source descriptions and 12,000 track titles across 12 artists found no usable explicit composition credits. These shared-title examples therefore support unresolved composition candidates, not confirmed cross-artist work identities. The next composition evidence acquisition must obtain explicit writer or work assertions from upstream records.

The production setlist relation stores song membership without sequence or repeat counts. `song_memberships` is sorted by song ID for reproducible JSON only. `order_available: false` records that limitation. Track positions describe files within source sets; they do not recover a canonical song-performance order.

The Aadam descriptions provide named taper, transfer and mastering assertions that are absent from the corresponding structured source credit fields. Collection membership supplies discovery context. A collection name does not establish a recording credit. Local records retain observation IDs, input hashes and provider URLs when available, so a reader can connect an imported description to the retained upstream observation.

## Capture again

Run from the repository root. Each command requires a new destination directory and refuses to overwrite captured files.

```sh
python3 docs/research/2026-10-17-full-normalization/evaluation/capture.py /tmp/normalization-production
python3 docs/research/2026-10-17-full-normalization/evaluation/capture.py /tmp/normalization-venues --sql docs/research/2026-10-17-full-normalization/evaluation/capture-venues.sql
python3 docs/research/2026-10-17-full-normalization/evaluation/capture.py /tmp/normalization-local --origin local
```

The production command gets the current database password from the documented Kubernetes secret and passes it in the child process environment. It never writes the password to an artifact or reports subprocess output on failure. Both SQL and connection settings require read-only transactions. The capture rejects production results unless PostgreSQL confirms a replica and read-only transaction. Each query uses a 30-second statement timeout and one repeatable-read snapshot.

Production source selection takes at most two eligible sources per named artist, plus one source with selected recurring credit strings. Prose fields must be at most 2,000 characters and must not contain an email `@` marker; the script selects other records instead of redacting text. Track selection takes two eligible files per artist and prefers compounds or nonmusic titles. The source sample is not chronological coverage. Venue, repertoire and local source IDs are explicit curated choices in the SQL. This sampling policy must accompany any evaluation result derived from these inputs.
