# Gov’t Mule: complete local inventory and v3 verification

The frozen Gov’t Mule inventory passed a real local import and repeat on October 17, 2026. Of 381 selected Archive items, 359 became recordings under one reviewed artist and 22 remained held. The running v3 API returned 285 shows, 6,533 tracks and 32 years. Both final workflows completed their rebuilds with zero failed items.

The [per-item evidence](evidence/govt-mule-full-artist.json) retains every selected identifier, projected creator/title/date, outcome, selected date, hold evidence and published source UUID. It also records catalog reference digests, current set UUIDs, workflow results and request provenance. This document explains what those checks establish for the engineer and operator executing the [next-work plan](README.md).

## Current-code repeat after performer reviews

After migration 15 and the rules-version-8 performer implementation, two normal named-artist refreshes rechecked all 381 retained items against live metadata. The first reported 359 publications under the new rules version; the repeat reported 359 unchanged recordings. Both kept the same 22 holds, reported zero failures, continued through four runs and completed their rebuilds. First run IDs were `01a148ea-74e9-74fd-9237-67cc531f3944` and `01a148eb-6746-7b94-960f-99112c0e8c62`.

The current API passed all 321 GETs on each pass. A new pre-v8 SQL snapshot let this check compare every one of the 359 source/show references, **359 ordered set UUIDs** and **6,533 ordered track UUIDs**, including complete MP3 and FLAC URLs. They were identical before v8 and after both runs. All 381 projection hashes and decisions also matched; only the interpretation rules version changed. This set comparison supplements the earlier evidence’s stated limit.

The [additive v8 evidence](evidence/govt-mule-v8-repeat.json) retains both execution chains, all per-item outcomes, comparison digests and exact hash definitions. It leaves the original v7 artifact intact. The dedicated worker was stopped after verification; the isolated API remains available. These repeats did not add per-item reviews to Mule, change approval, rerun the broad search or test new media bodies.

## Inventory and artist approval

The artist-specific Archive query was captured at **2026-10-17 07:37:49 UTC**:

```text
collection:taperssection AND mediatype:audio AND (creator:"Gov't Mule" OR creator:"Govt Mule")
```

Five advanced-search pages returned 383 query hits and an empty terminal page. Repeating the first page returned the same items. Review selected 381 items using four normalized creator keys: `GOV'T MULE`, `GOVT MULE`, `GOV’T MULE`, and `GOV'T. MULE`. Case variation shares a key. Two items billed as `Gov't Mule & Friends` were excluded from this artist inventory and retained explicitly in the evidence.

The search supplied **298 distinct date values**. That is an inventory statistic, not a count of verified performances. Multiple recordings can share a date, metadata can disagree, and held recordings do not contribute shows. The final **285 shows** count comes from published catalog assignments and the v3 responses.

A direct read-only production registry inspection covered all artist rows and Archive provider labels, including empty and auto-created entries. It found no matching Gov’t Mule identity; other Mule names were distinct artists. The retained intake-candidate relation was absent in that production schema. Artist-list API absence was not used as proof. Local materialization still performed its locked registry check before creating the artist.

One local candidate was approved with the four reviewed keys. The materialized artist UUID is `2d1d7746-1e91-94e9-e86b-1cbb37a56a8a`. It has no provider collection mapping. All 381 items resolved to that candidate; approval did not bypass recording checks.

## Version-7 workflows and listener behavior

The test used PostgreSQL database `relisten_govt_mule_20261017`, Redis at `127.0.0.1:16382`, Temporal at `127.0.0.1:17235`, and the API at `http://127.0.0.1:13824`. It preserved the existing local catalog and Pavement services. It made no production catalog writes and registered no schedules. The ingestion worker was stopped after verification.

The first complete import used the frozen item selection with the real `TapersSectionArtistWorkflow`; later refreshes used the normal named artist CLI command. These were current metadata fetches and catalog writes, not fixture responses. The final rules-version-7 runs share workflow ID `ingest/artist/2d1d7746-1e91-94e9-e86b-1cbb37a56a8a`:

| Execution | First run ID | Result |
| --- | --- | --- |
| First version-7 refresh | `01a148d7-3058-78ed-8381-6748aad1eace` | 381 observed; 359 published; 22 held; zero failures |
| Normal CLI repeat | `01a148d7-d3ff-77d7-9df3-8bbf29097b5a` | 381 observed; 359 unchanged; 22 held; zero failures |

Each execution continued through four runs and completed the rebuild. Every item’s catalog revision equals its rebuilt revision, and no item has a retained error. `Discovered=0` in these artist results means they consumed an existing item selection; they did not run collection discovery.

`Published` and `Unchanged` count processing outcomes, not rows inserted.

Each final HTTP verification pass made **321 successful GET requests**: the default artist list, artist by slug, normalized artist, years list, 32 year details and all 285 show details. Checks confirmed artist visibility and counts; year/show references; source ownership; nonempty tracks; track order; UUID links; finite positive durations; and MP3 URLs nested in show responses.

All 359 final source/show/track identities, dates and complete media URLs matched across the final repeat. All 381 interpretation objects, statuses and projection hashes also matched. The preservation check includes the original 353 published references and the six newly eligible recordings described below. Current ordered set UUIDs are retained separately; earlier HTTP manifests did not save set UUIDs, so this record does not claim a historical set-UUID comparison.

A bounded request to the first MP3 for `gm1994-08-07.flac16` returned **HTTP 206**, `audio/mpeg`, and 4,096 bytes. It establishes sampled media access, not complete-file validation or a listening audit.

## Policy corrections and remaining holds

The live inventory exposed two confirmed parsing errors. The fixes preserve explicit attribution checks:

- A title such as `Gov't Mule 2001-05-12 Live at …` put a valid date inside the old performer capture. The parser now removes only that complete date slot before checking the remaining reviewed label. This admitted `govtmule2001-05-12.SonyECM-MS957.flac16` and the equivalent dated-title case `govtmule2025-09-06.SAsl230mp.flac24`.
- Four descriptions used “In Memory of” inside recorded song references. Explicit jam/tease annotations, or a numbered song line corroborated by a retained file title, no longer imply that the whole performance is a tribute. Representative items are `GovtMule2023-05-19.m21.flac24` and `govtmule2019-07-03.m20.flac24`. The actual `Set 2 - Tribute To Levon Helm` context in `govtmule2012-06-02` remains held.

Rules version 7 also stops treating an identifier’s `.2496` recording-format suffix as a bare performance year. Full and partial ISO identifier dates remain diagnostic evidence. Structured metadata can still supply a known year; the fix adds no future-year cutoff. This diagnostic correction changed no published assignments.

The remaining 22 items have these primary reasons: **14 date conflicts, four performer-label review cases, three multi-act contexts and one explicit tribute context**. Some items have more than one reason. Examples include conflicting supplied performance dates; `Government Mule` or event/soundcheck text outside the reviewed title labels; co-headlining or double-bill descriptions; and the Levon Helm tribute set. The evidence retains every held item and its relevant values. These are review outcomes, not transport failures, and no additional aliases or expanded billing were accepted automatically.

The artist-specific advanced-search traversal does not verify the collection-wide scrape protocol. Production rollout, backup coverage and collection discovery remain separate checks in the [intake operations plan](../2026-10-16-taperssection-intake/verification-and-operations.md).
