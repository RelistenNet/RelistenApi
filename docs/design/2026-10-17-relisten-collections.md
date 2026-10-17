# Relisten collections, recording membership, and performer identity

Relisten needs a first-class **collection**: a named way to browse recordings across performing artists. A collection can include selected recordings or all recordings by explicitly selected artists. This supports both the Aadam Jacobs archive and a Jerry Garcia destination while preserving the artist, show, source, and track identities used for playback.

This document is for the Relisten engineer and reviewer/operator. Collection identity, both membership rules, exact existing-source reuse, configured discovery and read-only v3 routes are implemented in this checkout; focused service/workflow, registered-worker end-to-end and actual HTTP checks have passed. The [implementation contract](../plans/active/2026-10-17-archive-collections/contracts.md) defines their schema and interfaces. The [execution sequence](../plans/active/2026-10-17-archive-collections/README.md) tracks verification and the remaining performer/publication work. The existing [Taper’s Section intake](../plans/active/2026-10-16-taperssection-intake/README.md) remains the recording writer for reviewed new artists.

## What each identity means

An **artist** identifies the performer or performing project to which a recording is assigned. Jerry Garcia Band, Legion of Mary, and Garcia/Saunders remain distinct performing projects. A collection may bring them together for browsing. It does not make their names interchangeable.

A **recording** is the existing Relisten `Source`, with its ordered sets and playable `SourceTrack` rows. A **show** is the existing artist-and-display-date aggregate. Collection membership does not change either identity rule, infer an independent performance, or resolve an early/late show.

A **Relisten collection** has its own stable UUID, name, slug, and description. It groups catalog identities without becoming their performing artist or ingestion owner. Both an artist and a recording can belong to several collections. There is no single parent artist or required collection hierarchy.

An **upstream collection** is an Archive.org discovery boundary such as `aadamjacobs` or `taperssection`. Its provider-qualified identifier describes where an item was found. A Relisten collection can be backed by an upstream collection or maintained editorially. Keep that mapping separate from artist identity and artist-to-provider importer mappings.

## Two membership rules

**Selected-recording membership** includes only named recordings. Use this for the Aadam Jacobs collection. If the archive contains one Björk recording, the collection includes that recording. It does not include the rest of Björk’s catalog. The collection’s artist list can be derived from the performers of its resolved recordings; that list is not an artist-wide inclusion rule. Archive describes Aadam’s collection as his recordings across many performers. [Collection metadata](https://archive.org/metadata/aadamjacobs)

**Artist-wide membership** includes all currently published recordings assigned to a reviewed artist, including eligible recordings published later. Use this when an editor deliberately includes an entire performing project in a collection. A Jerry Garcia collection can include Jerry Garcia Band and other selected projects this way. Adding a project requires an explicit membership decision; a mention of Jerry in credits or a shared musician does not add it automatically.

A collection may combine both rules. Its playable contents are the union of explicitly included recordings and recordings from its artist-wide members, deduplicated by canonical source UUID. Removing an artist-wide membership leaves separately included recordings in place. Removing a recording’s explicit membership does not exclude it if another active inclusion rule includes it. An exclusion feature is outside this initial model; do not silently give removal that meaning.

Membership changes do not copy recordings, move them between artists, alter playback URLs, or remove catalog data. Collection show views retain each show’s artist identity; a shared date does not combine different artists’ shows. Counts describe distinct catalog entities, not the sum of overlapping membership paths.

## Jerry Garcia and JGB

The Jerry Garcia collection is a browsing destination across reviewed projects. The performer of each recording remains specific. The existing production artist `JGB` (`a3ffe2d0-a626-cd1d-56bf-13c617eb94be`) represents the Melvin Seals continuation, as confirmed against its [Archive collection metadata](https://archive.org/metadata/JGB). Preserve its 480 sources, including recordings dated before 1995; this increment does not audit or automatically repair those assignments. A historical `Jerry Garcia Band` project is a distinct identity, not a rename or alias of that production artist. The official Garcia catalog distinguishes Jerry Garcia Band, Legion of Mary, the acoustic band, Garcia/Saunders, and other projects. [Official show catalog](https://jerrygarcia.com/shows/)

Retain a provider’s original `creator: Jerry Garcia` value. Resolve the performer of that item from explicit billing in the current metadata and corroborating evidence where available. A recording explicitly billed as historical Jerry Garcia Band may be assigned to that distinct historical project while preserving the broad original creator label. Another item with the same creator label may belong to a different project. A date alone cannot establish the project or distinguish early and late shows.

The current intake approval maps reviewed creator labels to one artist. That cannot express this one-label-to-several-projects case. Do not add `Jerry Garcia` as a global alias for JGB or use approval of the umbrella collection to bypass performer checks.

The bounded Jerry increment uses a [retained per-item performer review](../plans/active/2026-10-17-archive-collections/performer-contract.md) targeting an explicit approved new-project candidate. It names a supporting immutable observation, field evidence, review basis and alternatives. It does not add a broad creator label to the candidate’s automatic matching vocabulary. Publication continues through the existing candidate, artist-admission, recording-writer and rebuild contracts; it must not introduce candidate-null writes or a separate admission system. Preserve original metadata and keep performer-review approval independent from shared source-resolution status. An unresolved performer stays pending. Known compilations and reviewed items spanning several projects remain held until their contents have a separately designed representation. Context checks cannot identify every mixed item; the reviewer must assess the retained billing and contents. The [bounded local proof](../plans/active/2026-10-17-archive-collections/jerry-projects-smoke.md) publishes three projects and holds one mixed item through normal artist workflows; a larger import still requires its own review.

Membership and performer decisions have different effects: one selects where a recording can be browsed; the other determines its catalog artist. Correcting a published artist assignment can affect source and track identities under current catalog rules. It requires a reviewed repair, not an ordinary collection update or automatic reassignment.

The precise Jerry collection membership remains an editorial decision. Including the Grateful Dead or only Garcia’s other projects must be decided explicitly before that collection is published. No person-to-band membership graph or automatic expansion is required for this increment.

## What Aadam can reuse

The existing Archive intake service retains observations, checks dates and playback eligibility, creates reviewed artists safely, writes recordings atomically, preserves publication references, and recovers incomplete rebuilds. Its partial-date rules, metadata precedence over identifiers, and internal date notes apply to Aadam recordings too. The recording writer already feeds the existing listener APIs.

Aadam is not supported end to end yet. Configured discovery now validates an Archive audio collection per selector, atomically retains pages and membership, and shares item identity and selected observations across overlapping selectors. Exact existing-source reuse links an Archive item to one canonical recording without another import or an ownership change. These services and their operator workflow are specified in the [collection contract](../plans/active/2026-10-17-archive-collections/contracts.md). The [live first-page check](../plans/active/2026-10-17-archive-collections/aadam-first-page-smoke.md) retained 100 unresolved Aadam items without canonical writes; full live continuation and terminal pagination remain unverified.

Two publication requirements remain:

1. **Missing recordings for existing artists.** Native imports may already supply Aadam items, but a missing recording requires a reviewed assignment and an explicitly supported write owner. Current intake still defers pre-existing artists. Collection discovery retains such items unresolved rather than bypassing that restriction.
2. **Per-item performer decisions when labels are insufficient.** A reviewed unambiguous label can identify a performer. A taper name, broad artist label, mixed bill or conflicting billing needs item-level evidence. Collection membership alone does not establish the performer. The bounded new-project Jerry path does not by itself authorize a full Aadam publication run.

Artist approval remains an explicit operator decision and can be done in bulk. No minimum catalog size, fuzzy name acceptance, or automatic trust of an entire archive replaces that review. Once approved, later clear recordings should publish automatically under the applicable assignment policy. Retained unresolved items remain available for operator review.

## Data responsibilities and flow

PostgreSQL owns collection identities, membership, observed provider facts, performer decisions, publication references, and rebuild state. Temporal coordinates discovery and refresh. Losing workflow history must not erase those records. Redis remains execution/cache infrastructure rather than the owner of membership or review decisions.

Migration 14 implements collection identity, selectors, both inclusion bases and shared item resolution. The [schema/service contract](../plans/active/2026-10-17-archive-collections/contracts.md#ownership-and-stored-records) gives exact names, constraints and read behavior. Their responsibilities are:

- **Collections:** stable UUID, name, slug, description, and publication state.
- **Upstream selectors:** provider-qualified collection identifiers linked to Relisten collections, with separate discovery progress. Editorial collections need no selector.
- **Artist-wide memberships:** collection and artist UUIDs, explicit review basis, and active/retired state.
- **Recording memberships:** collection and canonical source UUID for an existing recording, with supporting membership evidence. Unresolved provider-backed membership instead identifies the provider-qualified item and gains its source reference after resolution. Editorial membership can name a canonical source directly; it does not need an invented upstream item. Provider membership must be retainable before import.
- **Performer reviews:** the bounded Jerry path adds explicit candidate/observation evidence and approval independently from source-resolution status. The bounded three-project local proof is verified; collection membership does not supply that approval.

Keep independent membership bases when the same recording is included both editorially and by a provider. Retiring one basis must not remove the other. Provider retirement is explicit operator work in this increment: absence from a listing never retires membership, including after a complete scan. Rediscovery records positive evidence but preserves an explicitly inactive provider membership until reviewed reactivation. Neither membership retirement nor disappearance upstream authorizes source deletion.

Configured provider discovery currently retains each item and its membership, captures selected metadata, and attempts exact existing-source resolution. It stops at an unresolved retained item when no supported source exists. Existing-source linking and editorial source selection require no import. New-project publication remains a separate approved artist workflow that writes and rebuilds canonical data before collection reads include it. Collection reads join current catalog state without a Redis cache; membership-only changes need no artist rebuild or cache invalidation protocol.

Source reuse requires an unambiguous provider/item match consistent with the retained artist assignment. Conflicting or duplicate catalog matches remain unresolved for repair. A match by artist and date, title, filename, or audio resemblance is insufficient. Reuse the current item-level Archive identity across collection selectors so rediscovery cannot create another recording.

If an accepted canonical source disappears or no longer matches its retained item/performer/provider identity, keep membership and evidence but omit it from playable results. A metadata conflict alone retains a still-valid accepted reference while review proceeds. Do not substitute a same-date recording. Playable collection reads require current catalog playback rows; they do not probe upstream URLs and cannot promise continued remote media availability.

The component that already imports an existing source keeps ownership of its refresh and deletion policy. Collection indexing only maintains membership. A supported importer owns newly published recordings and must participate in the existing artist admission, publication, ownership-guard, and rebuild contracts. Extending intake to existing artists must define that ownership before enabling writes; it must not disable the current guards to make an overlapping import succeed.

## Interfaces and compatibility

Existing artist, show, source, and playback contracts continue to describe the canonical catalog. A collection’s UUID never substitutes for an artist UUID. Adding or removing membership must leave artist/show/source/track IDs and playback URLs unchanged. Collection membership alone does not change default artist-list visibility.

The implemented anonymous read API provides `/api/v3/collections`, collection detail by UUID or slug, artist facets, and canonical recording references. Paged results are deduplicated and use resource-bound seek cursors, limits of 1–200 and `no-store` responses. Artist facets label explicit artist-wide inclusion. Unresolved operator state is not public. The [read contract](../plans/active/2026-10-17-archive-collections/contracts.md#public-read-api) specifies exact fields, ordering, filters and errors. Operators maintain collection identity and membership through database/service review; the only new CLI operation starts configured discovery. No collection schedules or editing screen are included.

Date precision and disagreements remain in retained interpretations and operator status. This design does not add date-note fields to listener APIs. Broader normalization can later consume the same evidence and performer identities; shared venues, song search, occurrence alignment, and correction submission are separate features.

## Proof and recovery

Collection service/workflow, registered-worker end-to-end and actual read-API checks have passed; the [contract verification record](../plans/active/2026-10-17-archive-collections/contracts.md#verification-rollout-and-remaining-work) gives counts and limits. The bounded performer check also passed through normal workflows and actual APIs. Use these behaviors to guide wider review and release checks:

- Adding a selected Aadam recording includes that source and its actual artist without including the artist’s unrelated catalog.
- Adding an artist-wide member includes its present and later published sources. An overlapping explicit membership returns each source once.
- The same Archive item discovered through two collections links to one canonical source with stable track IDs and URLs.
- An already imported source retains its native owner. Collection removal, failed discovery, and collection rollback leave playback intact.
- Unresolved items retain evidence without creating a public performer or a playable source. A paused approval prevents new publication while preserving playback and recoverable rebuilds.
- A broad `Jerry Garcia` creator can resolve to different projects for different items, with separate retained bases. Conflicts cannot silently change a published artist.
- Membership and assignment records survive database backup/restore and loss of workflow state. Repeating discovery, assignment, and rebuilds converges without duplicate rows.

Use additive migrations and back up the new records with the catalog. Keep collection indexing and publication separately pausable. Rollback retires new work and retains evidence and membership records; it does not undo imports or reparent sources. Existing intake ownership protections remain necessary during rollback. Production pagination, backup coverage, deployment, and pilot checks remain those in the [intake runbook](../plans/active/2026-10-16-taperssection-intake/verification-and-operations.md).

## Remaining decisions before wider publication

The collection schema, inclusion/reuse services, public read contract and configured-discovery workflow are implemented; service/workflow, registered-worker end-to-end and actual HTTP evidence is retained. Missing-recording publication for existing artists still needs its own write-ownership contract. The bounded Jerry evidence set and explicit new-project performer path are verified locally; a full creator-wide import remains separate work. Its collection stays unpublished until the project membership list is decided, including whether to include Grateful Dead. The historical Jerry Garcia Band identity must remain separate from the existing Melvin Seals continuation. Production rollout and a full Aadam import are subsequent decisions.
