# Aadam: 299 playable recordings, stable on repeat

The bounded local Aadam Jacobs sample published **299 recordings under 252 artists** on October 17, 2026. Repeating all 257 reviewed artist workflows returned 299 unchanged recordings, the same eight held items and no failures. Both passes completed every artist rebuild. The running API passed 3,344 HTTP GETs across the two passes, preserving all show, source, set and track UUIDs, relationships, dates and complete playback URLs.

The [sanitized evidence](evidence/aadam-smoke.json) records selection hashes, execution-bound workflow results, ownership checks, HTTP summaries and the deliberate discovery cancellation. Complete metadata, identity manifests and request receipts remain outside Git in `/tmp/relisten-acceptance-20261017/aadam`. Use the [sample tools](live-samples/README.md) to repeat the service and HTTP checks.

## What was imported

The retained selection contains 320 current Archive items, each with VBR MP3 files. Billing review approved 307 items across 257 artist targets. Thirteen event, joint-billing or alternate-name cases remained outside that reviewed batch. The normal evaluator held another eight: four artist/title ambiguities, three tribute-context cases and one conflicting date. Those holds remained unchanged on repeat.

Publication used retained observations, explicit performer reviews and ordinary Temporal artist admission. No recording writer was called directly. The published `aadam-selector-smoke` collection includes provider-backed item memberships and **no artist-wide memberships**. Discovery can retain additional Aadam membership evidence; only the reviewed sample was admitted for publication.

Wire demonstrates missing-recording publication for an existing artist. The helper first created an ordinary local Wire identity through `ArtistService.Save`, then bound its reviewed candidate through `BindExistingArtistAsync`. Four of its five items published; one title ambiguity stayed held. Wire kept its UUID, ordinary `featured=0` flag and zero provider mappings. This is an explicitly created local fixture, not evidence that Wire already existed in production.

All 299 published sources retain intake ownership. Read-only snapshots confirmed unchanged artist identities, flags, mappings and source ownership after repeat. No artist/provider mapping was invented for the shared Aadam collection.

## What listeners could read

Each HTTP pass traversed collection pages and artist filters, then read artist details, normalized data, every selected show UUID, each date's performance array and the retained v2 date destination. The responses contained **298 shows, 299 sets and 3,098 ordered tracks**. Pagination returned each recording once; collection counts and source/set/track associations agreed with the complete show responses. Public performance fields were present, and internal assignment keys were absent.

The first and repeat identity digests match exactly. One sampled MP3 request returned HTTP 206 for 4,096 bytes. This checks sampled media access; it does not establish the condition of every audio file. One show has explicit late-performance billing. This Aadam selection contains no partial-date show or same-day split; the separate performance tests and Jerry proof cover those cases.

## Discovery resumed and stopped with its checkpoint intact

The existing configured selector resumed from its earlier 100-item checkpoint. Its next page contained 2,984 identifiers, so the transaction retained **3,084 collection memberships** before advancing. A one-page limit does not cap continuation pages at 100 items.

After 1,900 additional identifiers were acknowledged, the operator deliberately canceled that exact Temporal execution through the supported client. Its final continued run reached `Canceled`; no discovery workflow remains active. PostgreSQL retains 2,000 acknowledged identifiers and **1,084 pending identifiers**, with `search_complete=true` and no error. The scan's stored `running` state means it can resume; it does not indicate an active Temporal execution. An interrupted batch may already contain retained observations, which a later resume can safely repeat.

This proves checkpoint recovery and bounded publication. Discovery was not completed, and retained memberships are not a playable-recording count. The remaining metadata work was left durable rather than expanding this acceptance run to every item.

The selected items had no qualified pre-existing local source to reuse and no artist-specific Archive collection mapping. Native-source reuse and concurrent writer ownership are covered separately by the [service-contract tests](../../../../RelistenApiTests/TapersSection/TestReviewedPerformers.cs). The live sample establishes actual new publication, including an ordinary existing-artist fixture, without claiming native reuse or a complete Aadam import.
