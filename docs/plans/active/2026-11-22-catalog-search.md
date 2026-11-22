# Catalog search on Typesense: implementation plan

> **For agentic workers:** Codex is the primary implementer and reviewer. Claude adds independent subagent reviews per task. Steps use checkbox (`- [ ]`) syntax for tracking. Follow the repository `AGENTS.md`, including its readability standard.

**Goal:** The app and MCP use the User Service's merged search. It shows a prioritized **All** view by default and lets the listener scope to one kind (artists, songs, venues, shows, sources, reviews, playlists). The User Service calls the Typesense-backed `/api/v4/search` for the catalog half.

**Approach:** Postgres stays the source of truth, and Typesense is a read model we can rebuild at any time. A daily full build writes fresh collections and swaps Typesense aliases to them. After artist imports and song normalization, the worker reindexes that artist in place. The next successful daily build fixes missed updates and venue edits. The API ports the prototype's parser and ranker to C#.

**Spec:** [docs/design/2026-11-21-catalog-search-journeys.md](../../design/2026-11-21-catalog-search-journeys.md). Prototype code, results and reports: `~/code/relisten/relisten-search-workspace/` (start with `README.md`).

## Decisions so far

| Decision | Who and when |
| --- | --- |
| Typesense, one node in production. About 4 minutes of degraded search after an unexpected pod death is acceptable. | Alec, 2026-11-22 |
| Planned Typesense changes (upgrades, config) follow a short runbook: start a second node, build into it, point the API at it, stop the old one. | Alec, 2026-11-22 |
| The new search replaces `/api/v4/search` (no `/v2`). | Alec, 2026-11-22 |
| Explore leads a short query with the artist or song. Add Tracks leads with playable performances once a recognized artist, place or date is present. | Alec, 2026-11-22 |
| A passage needs a marked segue at every boundary. Adjacency alone isn't a passage. | Alec, 2026-11-22 |
| Rare staleness is acceptable: a search result can lag the catalog until the next reindex or daily build. | Alec, 2026-11-22 |
| The Reviews scope appears only for artists that have reviews. | Alec, 2026-11-22 |
| Orphaned tracks' play history isn't folded into their replacements. | Alec, 2026-11-22 |
| Playlist search ships with catalog search, before production and before the app UI. | Alec, 2026-11-22 |
| The app calls one merged search in the User Service (anonymous and signed-in), so the layout renders once instead of jumping when the second result set arrives. | Alec, 2026-11-22 |
| Playlist indexing: one workflow instance per playlist, woken by its writes, with about a 60 s debounce; no polling and no shared instance. | Alec, 2026-11-22 |
| Playlist search includes the artist names and song titles in each playlist. | Alec, 2026-11-22 |
| No catalog-results cache for now. Measure real usage first. | Alec, 2026-11-22 |
| Reviews and taper notes are separate result groups, never searchable text on show or performance documents. | Prototype round 1: putting review text on shows lost 17 cases |
| `boost_artist_ids` only reorders results. It never filters or overrides an artist, place or date the listener chose. | Prototype round 2 |

## What we're deliberately not building

A review of an earlier draft added protection against races and failures that would cost a lot and rarely matter. Because the daily full build repairs any drift, these are out:
- A generation manifest table, per-generation acknowledgements, and dirty-mark rows written inside catalog transactions.
- Write fencing, settle windows, and re-verify sweeps for in-flight Typesense writes.
- Per-pod adoption checks before dropping old collections. Typesense resolves aliases on the server, so every API pod switches at the same moment.
- A fallback to the old substring search, and cursors bound to a build. During an outage, search returns a clear "unavailable" state and the app offers a retry.

If production shows one of these is a real problem, we add the specific fix then.

## Correctness bar for the port

The prototype's per-case results are saved in the workspace (`round-2/latency/runs/`, `round-2/round2_runs/`). The C# port is correct when the same local restore and the same play export give the **same pass or fail on each case** as the frozen prototype, submitted and while typing. An equal total isn't enough, because one fix can hide one regression. Typeahead p95 should stay near the prototype's 80–85 ms.

The prototype scored 155/160 on the public cases, 27/30 on Codex's holdout and 31/33 on Claude's holdout. Alec runs Claude's holdout; its cases aren't shared with the implementer.

---

## Phase 1: catalog search backend

### Task 1: Index build and the Typesense client

**Outcome:** `dotnet run --project RelistenIngestionWorker -- --search-build=true` builds the four collections (entities, performances, recordings, reviews) from the local restore, swaps the aliases, and reports document counts. This matches the prototype's data: about 4.67M documents and 0 rejected.

**Scope:**
- **Local Typesense:** add `relisten-typesense` (Typesense 30.2) to `local-dev/docker-compose.yml`, and start it from `start-local-databases.sh` and `test-local.sh`.
- **Client:** a small Typesense client over `HttpClient` with kept-alive connections (the prototype showed connection reuse matters). It covers create collection, JSONL import, upsert, delete, alias swap and multi-search.
- **Projection:** port `round-2/project.py`. Key rules:
  - Index only playable, non-orphaned files (`is_orphaned = false`, with an mp3 or flac URL).
  - One performance document per show and song, or per show and normalized raw title where no song mapping exists. Each performance document stores all of its files and a preview file.
- **Ranking signals:**
  - 365-day starts per track, read the same way as `PlayRollupReader`.
  - Plays recorded on orphaned rows are not carried over to the replacement track. Re-imports are rare going forward, so losing that history is acceptable (Alec, 2026-11-22).
  - Review mentions per performance.
  - The build accepts an optional play-export path (`--search-plays-export`) because the local restore has no play history. Production reads the rollups.
- **Build:** run as a Temporal activity on the database queue. It builds new collections, checks for zero rejections, swaps the aliases, and drops the previous collections an hour later.
- **Tests:** a projection test against the seeded catalog: orphaned and unplayable files are excluded, and both Garden `Lawn Boy` cuts stay distinct. Plus a build test: a failed import leaves the aliases on the old collections.

### Task 2: Query port and the endpoint

**Outcome:** `/api/v4/search` serves the new contract from Typesense, and the existing eval harness, pointed at it, reproduces the prototype's per-case results.

**Scope:**
- **Port** the frozen pipeline: `hill_rank.py`, `round2_search.py`, `hill_sequences.py`, `latency/latency_search.py` with `latency/profile.json`, and `search_aliases.json`. Keep only the accepted behavior, not the experiment switches. Structure:
  - interpretation: typed constraints and competing readings;
  - request building;
  - a pure ranker;
  - one coordinator that owns the extra requests.
- **Query evidence:** the in-memory names and aliases for known-item lookup. It's built from Postgres at API startup and rebuilt when the alias points at a new build. Loading takes about 10 seconds; the song-matching SQL alone took 8.6 s on the full restore.
  - So that deploys don't send search traffic to cold pods, a Kubernetes **startup probe** waits for the first evidence load.
  - The probe passes after at most 60 seconds even if loading hasn't finished, so a slow Postgres never blocks browse.
  - Readiness stays database-only after startup.
  - Post-swap refreshes are jittered by 0–60 seconds.

  (Decision 2026-11-22, from Alec's stated preference for zero-downtime planned deploys.)
- **Contract:**
  - `GET /api/v4/search?q=&scope=all|artists|songs|venues|shows|sources|reviews&mode=explore|add_tracks&artist_id=&country=&boost_artist_ids=&ignore=&typing=&page=&request_id=`
  - The response echoes `q` and `request_id`, and includes the chosen interpretation as removable chips, `counts` per scope, and `availability` (`full` or `unavailable`).
  - For `scope=all`, it returns `top` plus `sections`; a single scope returns paged `results`.
  - A performance result carries a `performance_key`, `show_id`, and its preview's `source_id`, `track_id`, set, position and duration.
- **Drill-in:** `GET /api/v4/search/performance?key=` lists every playable file of a performance, grouped by source.
- **Add safety:** `catalog/resolve` gains a `playable` flag, and the User Service add-entry path rejects unplayable tracks. Identity resolution for saved references is unchanged.
- **One existing caller:** the User Service MCP `find` tool (`RelistenUserService/Mcp/CatalogBrowseTools.cs`) calls `/api/v4/search` with a relative path, and reads the old response shape. The apps don't call it. **Decision (Alec, 2026-11-22):** move MCP `find` to the new search with a small response adapter, and delete the old substring reader if nothing else uses it. The GraphQL `search` field reads the catalog directly and is unchanged.
- **Eval:** add an HTTP adapter to the prototype harness in the workspace and compare per-case results with the frozen runs. While typing, compare primary results only: live All typeahead skips recordings and reviews, but still honors an explicitly selected Sources or Reviews scope. Check evidence results on submit and in their selected scope. Add a small CI test with a few seed-catalog cases, so a broken endpoint fails CI.
- **Tests:** the parser and ranker edge cases the prototype found decisive, for example:
  - `billy strings` vs the Winterland passage;
  - `tokyo twist` (the city beats plays);
  - `jrad help on the way` with the Grateful Dead boosted;
  - `hampton 97 halleys`.

### Task 3: Keep it current

**Outcome:** a newly imported show is searchable within a few minutes. Anything missed is fixed by the daily build.

**Scope:**
- **Reindex after an artist import:** an activity after the venue match in `CatalogSteps.RebuildArtist`, behind `Workflow.Patched`. It's best-effort and never fails the import. It upserts the artist's current documents, deletes the documents that are no longer produced, and refreshes the venues it touched.
- **Reindex after normalization:** song normalization targets one artist, so reindex that artist at the end of the run (`Workflow.Patched`). Venue normalization doesn't reindex; the daily build covers it.
- **Daily build:** a Temporal schedule. Each per-artist reindex records `search_artist_reindexes(artist_id, requested_at)`. After the alias swap, a separate activity reindexes the artists recorded since the build started, so an import during the build isn't lost. Row `updated_at` can't be used for this, because importers set it to the provider's time.
- **Tests:**
  - an orphaned track disappears after a reindex;
  - a new show appears;
  - an import during a build is present after the swap;
  - an old workflow history replays without the new activity.

## Phase 1b: playlist search and the merged search

Playlist indexing is independent of the catalog: its own collection on the same Typesense node, its own updates, and catalog builds never touch it.

### Task 5: Playlist index

**Outcome:** every playlist is in a `user_playlists` collection (behind an alias). An edit reaches search within about a minute, and nothing runs while nobody edits.

**Scope:**
- **Documents:** one per playlist. Fields:
  - searchable: name, description, entry notes, guide segment text, the artist names and song titles of its tracks, and the curator's username (only for published playlists);
  - for filtering and ranking: `owner_id`, `published`, `taken_down`, `archived`, `history_generation`, `revision`, follow count and `updated_at`.
  - The playlist's own name and description get higher weight than the artist and song names, so "Dead Mondays" ranks above a playlist with a single Dead track.
- **Who writes:** the ingestion worker. It already connects to both the accounts database (for account deletion) and the catalog database, so attaching artist and song names is a join, not a cross-service call.
- **Updates, woken by writes instead of polling (Alec, 2026-11-22): one workflow instance per playlist.**
  - After each committed playlist write (create, edit, publish, unpublish, archive, moderator takedown), the User Service signal-with-starts `PlaylistSearchSyncWorkflow` with workflow ID `playlist-search-sync-{playlistId}`.
  - The instance waits about 60 seconds after the latest signal, up to five minutes after the first, then reindexes the playlist from current database state (upsert, or delete if it no longer exists). If activity retries run out, the workflow carries the edits into a fresh run after two minutes.
  - Many playlists edited at once means many short, independent instances. There's no shared instance whose history grows and no single bottleneck.
  - A failed signal doesn't fail the save; the nightly rebuild picks it up.
  - Takedown doesn't need to change `updated_at`, because it signals its playlist directly. That also keeps the owner's playlist ordering intact.
- **Nightly rebuild:** a full rebuild of the playlist collection with the same alias swap as the catalog. It catches hard deletes, follow-count changes, renamed catalog artists and songs, and missed signals.
- **Account deletion:** the existing purge also deletes the user's documents.
- **Tests:**
  - an edit is searchable after the sync runs;
  - a burst of edits to one playlist produces one reindex;
  - edits to two playlists run as two independent instances;
  - a takedown updates the document;
  - purge removes the user's documents;
  - a failed signal doesn't fail the save.

### Task 6: Merged search in the User Service

**Outcome:** the app makes one call. `GET /v1/search` returns catalog and playlist results in one response, for anonymous and signed-in users.

**Scope:**
- **One endpoint, two audiences:**
  - anonymous: catalog plus published, not-taken-down playlists;
  - signed in: catalog plus the user's own playlists plus public ones.
- **How it merges:** the User Service runs the catalog search (`/api/v4/search`) and the playlist search in parallel, and returns one response. Playlists appear as a section in All and as a `playlists` scope with paged results.
- **Same parameters as the catalog contract**, including `scope=playlists`, `typing` and `request_id`.
- **Partial failure:** if either half fails, the response still returns the other, with per-half availability. The app never shows "no results" for a half that's unavailable.
- **Playlist authorization:** Typesense returns candidate IDs only. The User Service loads those playlists from its database and keeps only the ones the caller may see. It drops any hit whose indexed `history_generation` or revision doesn't match the database, so a query can't match text that has since been deleted. Cards and excerpts are rendered from the database, never from the index. Filtering can make a page shorter than the requested size.
- **Caching:** no catalog-results cache yet (measure real usage first). Signed-in responses are `private, no-store`.
- **MCP `find`** calls this merged search in-process with the MCP user's identity, instead of calling `/api/v4/search`, so MCP sees playlists too.
- **Eval:** add playlist cases to the eval against seeded playlists:
  - a playlist found by its name;
  - one found by an artist it contains (for example `grateful dead`), ranked below the artist itself in All;
  - one found by curator username;
  - a private playlist visible only to its owner;
  - an unpublished playlist gone right away.
- **Tests:** the visibility rules above, partial failure of each half, and MCP `find` returning playlists for its user.

### Task 7: Production (catalog and playlist search together)

- Ships catalog and playlist search together. Deploy order: Typesense, first catalog and playlist builds, the Catalog API, then the User Service.
- Deploy Typesense in `relisten-flux` (1 replica, a persistent volume, memory sized from Task 1's measurements, plus an API key secret).
- Set `--db-compaction-interval=86400` and `--snapshot-interval-seconds=300` to match the local rebuild measurements.
- Run the first build. Ship the endpoint and replay a day of anonymized queries against it to check p95 under real load.
- Enable the schedule.
- Write the short runbook for planned Typesense changes, and note recovery after a pod death in `docs/ingestion/operations.md`.

## Phase 2: search UI in the app

Its own plan in `relisten-mobile`, once Task 2's contract merges:
- **Search screen:** calls the merged `/v1/search`. All by default, scope chips (including Playlists), and removable interpretation chips. Before typing, it shows recently played tracks, tracks from shows already in the playlist, and more versions of songs in the playlist.
- **Typing:** a 120 ms debounce, `typing=true` while typing, and stale responses dropped by `request_id`.
- **Boost:** `boost_artist_ids` from favorites, playlist artists and recently played.
- **Adding tracks:** **+** adds the exact track, with Added plus Undo, and the drill-in shows other cuts and tapes.
- **Unavailable:** shows a retry, not "no results".
- **Acceptance:** the phone journeys in the design doc.

## Phase 3: follow-ups the eval already names

- **Curated aliases:** `panic`, `bisco`, `kglw`, and generated initialisms, each with an eval case.
- **Nicknames:** mined from reviews into reviewed performance aliases.
- **Taper notes:** parsed into lineage fields.
- **Play reporting:** a `start_reason` field (tap, auto-advance, playlist, search).
