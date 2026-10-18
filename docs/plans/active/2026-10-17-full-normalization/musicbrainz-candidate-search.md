# MusicBrainz candidate search

## Recommendation and current boundary

`NormalizationMusicBrainzInputService` now supports **typed search-page commands** alongside fixed MBID lookups. Both share commands, attempts, response receipts, cache provenance and provider admission. The pure search decoder retains observed alternatives and citations. The local operator freezes a bounded page plan before driving at most one acquisition step per invocation. It does not hydrate hits or accept catalog identities.

`MusicBrainzLookupRequest` still requires an MBID. Migration 47 adds an immutable `lookup`/`search` command discriminator, and typed entrypoints reject a UUID belonging to the other kind before deserialization. Existing lookup JSON and exact terminal replay remain compatible. The [lookup adapter](musicbrainz-acquisition-contract.md) retains the shared transport/recovery contract; the [operator guide](../../../../tools/RelistenNormalization/musicbrainz-search.md) covers the frozen plan and output artifacts. Search-specific HTTP integration is not claimed by the synthetic fixtures; root's separately bounded provider probe remains required.

`NormalizationCandidateService.FindCompositionFactsAsync` and `FindRepertoireFactsAsync` retrieve accepted **local** candidates in a primary repeatable-read snapshot. They are not external MusicBrainz searches. `NormalizationCatalogBindingService.BindRecordingAsync` binds legacy/Archive/Phish recording observations; it cannot bind a MusicBrainz recording. Its Setlist.fm binding can use the configured `artists.musicbrainz_id` with exact performer/resource checks, but that is not a universal reviewed catalog-artist-to-MusicBrainz identity decision.

## Typed query and page APIs

The implemented provider-specific page contracts are:

```csharp
// No raw Lucene, arbitrary URL, includes, sort or authentication parameters.
record MusicBrainzSearchQuery(
    MusicBrainzLookupKind Kind, // Work, Recording, Artist
    string Text,
    MusicBrainzSearchArtistConstraint? RelatedArtist = null);

record MusicBrainzSearchArtistConstraint(
    MusicBrainzSearchArtistUse Use, // RecordingPerformer or WorkAuthor
    string? Name,
    Guid? ExternalArtistMbid); // exactly one; never a local UUID

record MusicBrainzSearchPageRequest(
    MusicBrainzSearchQuery Query,
    int Offset,
    MusicBrainzCacheMode CacheMode,
    TimeSpan? MaximumCacheAge = null,
    MusicBrainzSearchPlanIdentity? PlanIdentity = null);

Task<MusicBrainzSearchAcquisition> AcquireSearchAsync(
    Guid commandUuid, MusicBrainzSearchPageRequest request, CancellationToken ct = default);
Task<MusicBrainzSearchAcquisition> ResumeSearchAsync(Guid commandUuid, CancellationToken ct = default);
Task<MusicBrainzSearchAcquisition?> GetSearchAsync(Guid commandUuid, CancellationToken ct = default);
```

`MusicBrainzSearchAcquisition` has the existing state, next-time, result and attempts contract, with its typed search request and frozen search descriptor (builder version, rendered query, kind, limit, offset and resource). Reuse `MusicBrainzAcquisitionResult` for retained input/fetch/generation references. Existing lookup entrypoints reject a search command UUID before attempting lookup deserialization, and vice versa. Command UUIDs are unique across both kinds. Exact historical terminal replay remains independent of current query-builder defaults.

Use a fixed page size of **50**, offsets 0/50/100/150 and at most four pages per query variant. These are initial engineering bounds to evaluate, not measured retrieval recall. Require nonempty text, at most 512 Unicode scalar values per term, at most 4 KiB rendered query and at most 64 KiB serialized command. Reject control characters, unsupported combinations, excess terms and invalid MBIDs before admission; do not silently shorten a title. Artist search forbids a related-artist constraint. Work search permits only `WorkAuthor`; recording search permits only `RecordingPerformer`.

The pure `MusicBrainzCandidateQueryBuilder` accepts a typed seed with cited title/name and explicit related-artist context. Work-author variants require cited role wording of writer, composer or lyricist; other author contexts remain issues. The operator verifies the exact complete seed input references and literal spans with `NormalizationEvidenceService.GetInputsAsync` and `NormalizationRetainedDocument`. These checks establish retained wording, not the truth of caller-supplied performer/author interpretation or external identity correspondence. It emits a frozen versioned plan whose variants retain the citations used. The page transport validates its typed literal request and fixed profile; it does not certify that a caller's words came from a source. A processing coordinator must validate seed assertions against the exact retained decode/extraction before enrolling a proposal, as the existing recording/setlist processors do. Keep source receipt/readsets with that processing attempt, not in a provider transport Work subject. Missing title/name evidence yields an explicit unresolved seed without HTTP; a slug, filename fragment or catalog UUID is not an invented replacement name.

For one title/name observation, emit the broad variant first, then up to two supported contextual variants in deterministic ordinal order. More supported variants produce an explicit `QueryVariantLimit` issue; their original assertions remain in the exact request artifact while the plan explicitly lists only the chosen query variants; no automatic resolver may mistake that plan for complete retrieval. Repeated source assertions remain separately cited even when identical query bytes share a transport resource. Additional retained titles/aliases require explicit seed entries, not a hidden rewrite of the primary title.

## Deterministic construction

Use only these fixed fields:

- Artist name: `artist:"<literal name>"`.
- Recording title: `recording:"<literal title>"`, optionally `AND artistname:"<literal performer>"` or `AND arid:<external artist MBID>`.
- Work title: `work:"<literal title>"`, optionally `AND artist:"<literal author>"` or `AND arid:<external author MBID>`.

MusicBrainz documents those [artist](https://musicbrainz.org/doc/MusicBrainz_API/Search/ArtistSearch), [recording](https://musicbrainz.org/doc/MusicBrainz_API/Search/RecordingSearch) and [work](https://musicbrainz.org/doc/MusicBrainz_API/Search/WorkSearch) fields. Work `artist`/`arid` searches relationships generally; a hit still needs lookup evidence to establish the actual relationship role.

Quote literal values, escape Lucene metacharacters before quoting and then URI-encode the rendered query once. Use a fixed, tested escaping implementation based on [Lucene's documented syntax](https://lucene.apache.org/core/3_4_0/queryparsersyntax.html#Escaping%20Special%20Characters). Keep Unicode, accents, punctuation and word order. Do not add fuzzy suffixes, wildcards, boosts, guessed aliases or stripped parentheticals. Query-builder version changes must change the frozen profile/hash. A future broader retrieval strategy needs separate evaluated coverage, not silent syntax changes.

A catalog performer may constrain recording search. It must not constrain work search as an author unless there is explicit author-role evidence. A taper/transferrer, cover performer, arranger or original performer is not an author term. Unresolved literal author names may retrieve alternatives but do not become canonical people. A MusicBrainz artist MBID remains external and may identify a Person, Group or another provider type; neither its UUID shape nor its namespace permits conversion to a catalog artist UUID or canonical person UUID.

## Resource identity and reuse of acquisition

Each page uses provider `musicbrainz.org`, resource type `work-search`, `recording-search` or `artist-search`, and capture version `musicbrainz-<kind>-search-json-v1`. Its key is SHA-256 of a canonical UTF-8 descriptor containing profile/query-builder version, kind, exact rendered query, fixed limit and offset. Persist that full descriptor and constructed URL with the command/attempt. A hash is not a substitute for comparing the frozen descriptor on replay.

The URL is fixed to `https://musicbrainz.org/ws/2/<kind>?query=<escaped>&limit=50&offset=<offset>&fmt=json`. Search parameters and allowed page bounds follow the [official API search contract](https://musicbrainz.org/doc/MusicBrainz_API/Search). No lookup `inc` profile is implied. The public search receipt returns that original descriptor, including on historical terminal replay; it must not reconstruct it with the current builder. Source artist UUID, command UUID, operator plan identity and cache age do not belong in the resource key: identical external pages can be reused across local claims. The processing receipt separately retains each claim's original context.

Migration 47, `AddMusicBrainzSearchProfiles`, adds an immutable `request_kind` discriminator to `normalization.musicbrainz_commands`, defaulting existing rows to `lookup`, with `lookup`/`search` values and retention-trigger protection. Store the concrete request in the existing immutable `request` JSON. No second gate, response store or transport-attempt table is needed. Adapt the provider-specific internal command projection/cache-policy access so shared phases operate on the fixed resource/profile and cache policy before returning a typed lookup or search result. Preserve old lookup serialization, request equality and version/replay behavior. This is a bounded extension of the existing provider class, not a generic HTTP framework.

Reuse the same admission order, 1.25-second minimum gap, Retry-After, deadlines, three attempts per page command, response bounds, saved-response recovery and original-generation capture CAS. Lookup and search traffic must contend on the **same database gate**, including different callers and the operator CLI. The [provider rate guidance](https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting) applies to the combined traffic. Each invocation still drives at most one GET and returns Pending without sleeping.

Validate a search response's small envelope before capture: object root, no provider error, nonnegative integer count/offset, matching requested offset and a kind-specific array no larger than the requested limit. Root `id` validation belongs to lookups only. Retain structural issues that prevent interpreting results; do not publish a fake empty search on malformed JSON. HTTP failures retain response receipts without replacing an earlier successful page head. Successful zero-result pages are real retained inputs. Cache reuse requires the exact search profile, original admitted adapter receipt and explicit age, with no new capture/head advance. A stale capture remains historical and cannot supply a fresh page result.

## Page observations and bounded coverage

`ProviderDecodeResult.MusicBrainzSearchPages` is optional and omitted when absent. `NormalizationProviderDecoder.MusicBrainzSearch.cs` handles the fixed search profiles; the decoder fingerprint is `provider-observations-v5`. A page observation carries original resource/revision, cited provider `created` value when supplied, cited count and offset, original result order and issues. Each hit retains its object citation, qualified external entity reference, observed score and supported summary fields: title/name, disambiguation/type, recording artist credits, aliases/ISWCs or work artist summaries when actually supplied. Use existing entity/credit/identity value types where the representation supports them; retain unfamiliar fields through object citations rather than copying arbitrary JSON. Search summaries must not masquerade as complete lookup observations. The pure decoder can read page facts from the actual input resource/profile, but cannot recover a query from its hashed resource key. The plan coordinator must verify the original descriptor and successful fetch association through the typed acquisition receipt before attributing a page to a query. A caller-supplied query label or response offset alone is insufficient provenance.

Keep missing versus empty collections, invalid-member diagnostics, duplicate hits and all source positions. Aggregate by MBID only for distinct hydration requests; retain every originating hit/citation. Scores are retrieval metadata, never probabilities, acceptance confidence or tie-break authority. Full relationship hydration uses the existing fixed recording/work/artist lookup commands and decoders, with its own original receipts.

A frozen search plan records each expected variant, page offset/command UUID and original capture reference. Stable child command UUIDs are saved before dispatch; a resumed plan must not mint new commands for an already attempted page. Each variant has at most 200 returned slots and 12 possible sends under its four-page/three-attempt limits. Up to three variants bound one seed to 600 slots and 36 sends. These are ceilings, not automatic instructions to exhaust every request. Hydration is not implemented: operator receipts retain every observed distinct external entity as unhydrated and state `HydrationImplemented=false`. More than 20 distinct entities additionally produces `HydrationLimit`; it does not imply that the first 20 were selected or acquired. Do not add a scheduler in this increment.

Report page structural validity, observed end-of-results and blocked coverage separately. A short page inconsistent with count, offset gap, duplicate across pages, changing counts, malformed member or exhausted bound makes coverage incomplete. Preserve earlier pages if a later page is Pending/Failed; never report a complete negative result from that prefix. Offset paging has no transactional provider snapshot. Even stable counts and a reached end cannot prove no rival was added, renamed or reindexed meanwhile. MusicBrainz's [search architecture](https://musicbrainz.org/doc/Development/Search_Architecture) uses an asynchronously updated search index. Set `ExternalUniverseComplete=false` in all search-plan receipts; `ObservedEnd=true` describes only the returned query window. This unavoidable live-search limit is not a universal prohibition on automatic positive matches. A future independently evaluated rule may accept corroborated positive correspondence from retained facts, its explicit retrieval coverage and checked contradictions. A single hit, top rank, score gap or absence of returned rivals cannot alone authorize acceptance. The rule must state which coverage limitations prevent its particular positive decision; it must not require an unavailable global completeness proof for every match. Retain an empty result and its query/time/coverage, without making it permanent work absence. Later source/context changes or an explicit new retrieval version can justify a new plan. External search has no current notification/watermark contract here; do not invent a polling interval or claim the local pending scan detects new MusicBrainz rivals. Provider timestamps, local input heads and local candidate epochs cannot manufacture a global MusicBrainz index-generation fence.

## From candidates to an evidenced catalog-to-work proposal

The next resolver must distinguish three links:

1. **Local claim → external recording/work.** Preserve the retained repertoire or extracted file/setlist passage and exact source context. A title/performer search hit is a candidate, including same-title alternatives; it does not assign its MBID to the claim. A preexisting cited qualified work ID is direct work evidence. A cited external recording identity, or independently supported/evaluated catalog-to-recording correspondence, may establish a recording bridge. Archive identifiers, Phish track IDs and provider song IDs are not MusicBrainz recording IDs merely because they identify audio or songs.
2. **External recording → external work.** Hydrate the selected external recording and preserve each cited work/performance relationship, its type, direction and attributes. Multiple works, medleys and uncertain relationship semantics stay separate. This establishes an external relationship, not a Relisten source/work assignment.
3. **External work → accepted global composition/repertoire.** Hydrate work/author candidates and compare against `FindCompositionFactsAsync`/`FindRepertoireFactsAsync` results. Preserve the original local candidate readset, raw/source input clocks, owner observations and candidate-universe epochs through Work admission and publication. Do not reread fresh clocks after selecting candidates. Local epochs fence new local rivals; external search pages provide only the explicitly limited observations above.

A durable `MusicBrainzCatalogWorkProposal` should retain the local claim receipt, all compared external alternatives/page receipts, any supported bridge receipt, hydrated relationship citations, local fact snapshot fingerprint/readset and unresolved contradictions. This is a new domain proposal contract; the current recording proposal store and binder do not implement it. Search acquisition itself needs no such table. Implement that processor only with its concrete proposal storage and supported bridge semantics, rather than treating a transport success as accepted normalization.

`IdentityCandidateMatcher.CompareCompositions` accepts canonical-person author UUIDs and explicitly asserted complete authorship. MusicBrainz lookup observations currently expose `CompleteAuthorship=null`; search cannot upgrade it. Person/group resolution and role interpretation require their own supported evidence. Keep writer/composer/lyricist, arranger and performer claims distinct; no cover-performer-to-composer conversion.

`NormalizedMusicService.SaveCompositionLink` currently permits `qualified_work_identity` or `writer_evidence` for automatic writes and rejects title-only links. The qualified-ID check confirms that the target composition owns that ID; it does **not** establish that a searched local claim refers to it. Writer evidence additionally requires compatible complete source author assertions, which these MusicBrainz profiles do not supply. Consequently, search plus the existing writer gates cannot resolve a general catalog claim that has neither a qualified work ID nor complete authorship. The objective requires a subsequent evaluated-correspondence acceptance contract; treating every such claim as permanently unresolved would leave cross-artist composition resolution unfinished.

### Required evaluated-correspondence publication proof

Add a narrowly typed, durable `EvaluatedCatalogWorkCorrespondenceReceipt`, issued by the resolver processor after evaluating the retained evidence. It records:

- The exact local repertoire/claim identity and original extraction/proposal receipt, plus the proposed global composition identity and expected entity revisions.
- The evidenced local→external recording/work correspondence, any external recording→work relationship, original page/lookup captures and citations, and every compared alternative or unexecuted coverage limitation.
- The original raw-input dependencies and local candidate snapshot/readset: catalog context, owner observations, candidate-universe epochs and other dependency families actually used in the decision. These are original observations, never clocks refreshed after selecting the result.
- The evaluated policy version, verified evaluation receipt/rule, decision features and checked contradictions, including the rule's treatment of external retrieval limits and person/group/role uncertainty.

The resolver processor stores that immutable receipt under its supported execution contract. Callers supply its identifier, not an authority flag or a self-authored proof object. Extend the typed repertoire-to-composition publication contract with a separate evaluated-correspondence path. Inside the existing atomic domain publication callback, load and authenticate the durable receipt, verify its issuing processor and authorized evaluated policy/rule, and require its exact claim/target/evidence to agree with the write. Work must consume the complete original receipt readset and reject stale source, candidate, owner or editorial state. A receipt for another claim, target or source revision cannot be repurposed, and its existence cannot freshen historical inputs.

Keep direct qualified-ID and complete-writer paths intact; the new path does not fabricate a source MBID or `CompleteAuthorship=true`. Its positive evidence must establish correspondence under an independently evaluated rule, including contradictions and explicit query coverage. `ExternalUniverseComplete=false` can therefore coexist with a valid positive proof, while title/rank/absence alone still cannot qualify. The development implementation of this processor-issued receipt and dedicated publication path is described in [composition resolution](composition-resolution.md). Its only supported rule is a disposable-fixture classifier; no production correspondence rule is evaluated or activated. Root runtime verification of that increment is recorded separately. Independent cross-artist labels, evaluated positive capabilities and policy activation remain required before automatic composition matching/backfill is enabled; search alone is not completion of that objective.

## Files, coupled changes and verification

Implemented files include `NormalizationMusicBrainzSearch.cs`, `MusicBrainzCandidateQueryBuilder.cs`, the search decoder/observation partials, typed search service/store partials, and migration 47. The existing acquisition implementation now shares its internal protocol state across typed public projections. The [local operator](../../../../tools/RelistenNormalization/musicbrainz-search.md) freezes variants, descriptors and deterministic page UUIDs in `plan.json` before acquisition, then reconstructs progress from durable commands/captures. Each page request also carries the plan UUID and exact original request hash, so a changed context cannot silently reuse an admitted child's identity. Prior receipts are validated against that same plan; after interruption, the frozen plan file is an explicit recovery input. This is a bounded operator protocol, not a recurring job service.

The new [composition resolution contract](composition-resolution.md) describes the development processor, bounded hydration and authenticated publication path. Actual labeled evaluation, production policy capability and scheduling remain required following work. Apply migration 47 before enabling the current acquisition binary, including lookup-only callers, alongside the existing input/acquisition schema. Pause all MusicBrainz callers before rolling back to binaries that cannot understand search commands, and retain headers, attempts, responses and input heads. No acquisition, acceptance or backfill schedule starts automatically.

`TestNormalizationMusicBrainzSearchAcquisition` covers pure query construction and PostgreSQL/fake-HTTP page behavior; `TestNormalizationProviderDecoderMusicBrainzSearch` covers synthetic retained page shapes and citations; `TestMusicBrainzSearchBatch` covers the public frozen-plan/export/resumption contract. Source fixtures do not establish provider retrieval recall, external identity accuracy or an active matching policy. Build/runtime verification is recorded separately after the shared serial checks.

The solution builds and all 291 focused search, lookup and provider-decoding tests
pass, including 124 new search cases. Fresh correctness and readability reviews
and the repository-wide formatter check pass. A bounded local provider probe made
four transport attempts across three search commands. All timed out before response
headers; original timeout receipts and explicit resumption remain retained. Successful
live search-response verification is still required. No search capture, accepted
identity or catalog link was produced by this probe.

Required behavior tests:

- Same-title work alternatives, broad versus performer/author-constrained retrieval, role misuse rejection, Person/Group versus local UUID collisions, exact Unicode/metacharacter escaping and deterministic resource keys.
- Lookup/search cross-kind command conflicts and global admission; same query reuse across local artists; changed query/page/profile conflict; unchanged cache age semantics; saved response/capture recovery and stale original CAS.
- Zero results versus missing/malformed arrays; exact per-hit citations; duplicate IDs retained; invalid members/unknown roles; search summaries never becoming accepted lookup facts or complete authorship.
- Four-page and hydration bounds, later-page failure, moving counts/overlapping pages and frozen resume commands; no acceptance based only on one hit, rank or absence of rivals, even when an observed end is reached.
- Exact external recording→work relationship plus absent local bridge, unrelated catalog file, same-title rival and contradictory author assertions: all remain proposals without acceptance. For the subsequent proof increment, an independently evaluated positive correspondence may succeed with `ExternalUniverseComplete=false` when its rule supports the retained corroboration/coverage and no blocking contradiction exists. Forged caller authority, wrong-scope receipts, unsupported policies and changed source/target/readsets must reject atomically; supported proof must stale on local source, owner, rival or editorial changes.

Official documentation was checked on 2026-10-17. No search API response was acquired while implementing these synthetic contracts. Search-specific response fixtures and exact envelope/profile behavior must be verified with a separately bounded development acquisition before runtime claims. The indexed-syntax documentation fetch returned HTTP 429; no repeated fetch was made. The escaping proposal cites Apache's primary syntax documentation and still requires concrete query contract tests against the chosen MusicBrainz profile.
