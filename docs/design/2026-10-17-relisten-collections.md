# Relisten collections, recording membership, and performer identity

Relisten needs a first-class **collection**: a named way to browse recordings across performing artists. A collection can include selected recordings or all recordings by explicitly selected artists. This supports both the Aadam Jacobs archive and a Jerry Garcia destination while preserving the artist, show, source, and track identities used for playback.

This document is for the Relisten engineer and reviewer/operator. It defines the agreed model and the boundaries an implementation must preserve. Collections and per-recording performer resolution are planned work. The existing [Taper’s Section intake](../plans/active/2026-10-16-taperssection-intake/README.md) publishes eligible recordings for reviewed new artists; it does not yet implement these additions. The [execution sequence](../plans/active/2026-10-17-archive-collections/README.md) is separate from this design.

## What each identity means

An **artist** identifies the performer or performing project to which a recording is assigned. Jerry Garcia Band, Legion of Mary, and Garcia/Saunders remain distinct performing projects. A collection may bring them together for browsing. It does not make their names interchangeable.

A **recording** is the existing Relisten `Source`, with its ordered sets and playable `SourceTrack` rows. A **show** is the existing artist-and-display-date aggregate. Collection membership does not change either identity rule, infer an independent performance, or resolve an early/late show.

A **Relisten collection** has its own stable UUID, name, slug, and description. It groups catalog identities without becoming their performing artist or ingestion owner. Both an artist and a recording can belong to several collections. There is no single parent artist or required collection hierarchy.

An **upstream collection** is an Archive.org discovery boundary such as `aadamjacobs` or `taperssection`. Its provider-qualified identifier describes where an item was found. A Relisten collection can be backed by an upstream collection or maintained editorially. Keep that mapping separate from artist identity and artist-to-provider importer mappings.

## Two membership rules

**Selected-recording membership** includes only named recordings. Use this for the Aadam Jacobs collection. If the archive contains one Björk recording, the collection includes that recording. It does not include the rest of Björk’s catalog. The collection’s artist list can be derived from the performers of its resolved recordings; that list is not an artist-wide inclusion rule. Archive describes Aadam’s collection as his recordings across many performers. [Collection metadata](https://archive.org/metadata/aadamjacobs)

**Artist-wide membership** includes all currently published recordings assigned to a reviewed artist, including eligible recordings published later. Use this when an editor deliberately includes an entire performing project in a collection. A Jerry Garcia collection can include JGB and other selected projects this way. Adding a project requires an explicit membership decision; a mention of Jerry in credits or a shared musician does not add it automatically.

A collection may combine both rules. Its playable contents are the union of explicitly included recordings and recordings from its artist-wide members, deduplicated by canonical source UUID. Removing an artist-wide membership leaves separately included recordings in place. Removing a recording’s explicit membership does not exclude it if another active inclusion rule includes it. An exclusion feature is outside this initial model; do not silently give removal that meaning.

Membership changes do not copy recordings, move them between artists, alter playback URLs, or remove catalog data. Collection show views retain each show’s artist identity; a shared date does not combine different artists’ shows. Counts describe distinct catalog entities, not the sum of overlapping membership paths.

## Jerry Garcia and JGB

The Jerry Garcia collection is a browsing destination across reviewed projects. The performer of each recording remains specific. The official Garcia catalog distinguishes JGB, Legion of Mary, the acoustic band, Garcia/Saunders, and other projects. [Official show catalog](https://jerrygarcia.com/shows/)

Retain a provider’s original `creator: Jerry Garcia` value. Resolve the performer of that item from explicit billing in the current metadata and corroborating evidence where available. A recording explicitly billed as Jerry Garcia Band may be assigned to JGB while preserving the broad original creator label. Another item with the same creator label may belong to a different project. A date alone cannot establish the project or distinguish early and late shows.

The current intake approval maps reviewed creator labels to one artist. That cannot express this one-label-to-several-projects case. Do not add `Jerry Garcia` as a global alias for JGB or use approval of the umbrella collection to bypass performer checks.

Add a retained performer decision for each item before enabling broad Jerry imports. It must identify the selected artist, the observation and field evidence supporting the assignment, how the decision was made, and unresolved alternatives or conflict. Preserve original metadata independently from that decision. An unresolved performer stays pending. Compilations and items spanning several projects remain held until there is a separately designed way to represent their contents.

Membership and performer decisions have different effects: one selects where a recording can be browsed; the other determines its catalog artist. Correcting a published artist assignment can affect source and track identities under current catalog rules. It requires a reviewed repair, not an ordinary collection update or automatic reassignment.

The precise Jerry collection membership remains an editorial decision. Including the Grateful Dead or only Garcia’s other projects must be decided explicitly before that collection is published. No person-to-band membership graph or automatic expansion is required for this increment.

## What Aadam can reuse

The existing Archive intake service retains observations, checks dates and playback eligibility, creates reviewed artists safely, writes recordings atomically, preserves publication references, and recovers incomplete rebuilds. Its partial-date rules, metadata precedence over identifiers, and internal date notes apply to Aadam recordings too. The recording writer already feeds the existing listener APIs.

Aadam is not supported end to end yet. It needs three extensions in addition to collection membership:

1. **Configured discovery.** The search query and row validation currently require `taperssection`. Accept a configured upstream collection, validate returned membership against it, and keep independent discovery progress for each selector. Share item identity and retained observations when selectors overlap.
2. **Existing-source reuse and existing-artist publication.** An Aadam item may already be imported through an artist’s native collection. Link the exact Archive item to that canonical source without importing it again or changing its ingestion owner. For a missing recording by an existing artist, require a reviewed assignment and an explicit supported publication path. Current intake deliberately defers pre-existing artists.
3. **Per-item performer decisions when labels are insufficient.** Reuse a reviewed unambiguous label when it identifies the performer. A taper name, broad artist label, mixed bill, or conflicting billing needs item-level evidence. Collection membership alone does not establish the performer.

Artist approval remains an explicit operator decision and can be done in bulk. No minimum catalog size, fuzzy name acceptance, or automatic trust of an entire archive replaces that review. Once approved, later clear recordings should publish automatically under the applicable assignment policy. Retained unresolved items remain available for operator review.

## Data responsibilities and flow

PostgreSQL owns collection identities, membership, observed provider facts, performer decisions, publication references, and rebuild state. Temporal coordinates discovery and refresh. Losing workflow history must not erase those records. Redis remains execution/cache infrastructure rather than the owner of membership or review decisions.

The implementation needs these logical records; final migration names and indexes belong in the collections implementation contract:

- **Collections:** stable UUID, name, slug, description, and publication state.
- **Upstream selectors:** provider-qualified collection identifiers linked to Relisten collections, with separate discovery progress. Editorial collections need no selector.
- **Artist-wide memberships:** collection and artist UUIDs, explicit review basis, and active/retired state.
- **Recording memberships:** collection and canonical source UUID for an existing recording, with supporting membership evidence. Unresolved provider-backed membership instead identifies the provider-qualified item and gains its source reference after resolution. Editorial membership can name a canonical source directly; it does not need an invented upstream item. Provider membership must be retainable before import.
- **Performer assignments:** the item’s selected performing artist and retained evidence, separate from collection membership and original creator labels.

Keep independent membership bases when the same recording is included both editorially and by a provider. Retiring one basis must not remove the other. Changes to an upstream listing can update provider membership only after complete, successful enumeration or direct confirming evidence. A failed request, partial traversal, or limited run is not removal evidence. Neither membership retirement nor disappearance upstream authorizes source deletion.

For provider-backed membership, the processing order is: discover an item and retain its collection membership; capture its metadata; find an exact existing source or resolve its performer; publish only when assignment and playback checks pass; rebuild affected artist catalogs; refresh collection views. Existing-source linking skips recording publication entirely. An editorial selection of an existing canonical source also requires no import. Collection-only changes invalidate collection reads without rewriting artists or recordings.

Source reuse requires an unambiguous provider/item match consistent with the retained artist assignment. Conflicting or duplicate catalog matches stay pending for repair. A match by artist and date, title, filename, or audio resemblance is insufficient. Reuse the current item-level Archive identity across collection selectors so rediscovery cannot create another recording.

If a linked canonical source becomes unavailable or disappears, retain the membership and evidence but do not return it as playable. Hold a missing or conflicting link for repair; do not substitute a same-date recording. The membership record cannot promise continued upstream media availability.

The component that already imports an existing source keeps ownership of its refresh and deletion policy. Collection indexing only maintains membership. A supported importer owns newly published recordings and must participate in the existing artist admission, publication, ownership-guard, and rebuild contracts. Extending intake to existing artists must define that ownership before enabling writes; it must not disable the current guards to make an overlapping import succeed.

## Interfaces and compatibility

Existing artist, show, source, and playback contracts continue to describe the canonical catalog. A collection’s UUID never substitutes for an artist UUID. Adding or removing membership must leave artist/show/source/track IDs and playback URLs unchanged. Collection membership alone does not change default artist-list visibility.

No collection API, command, schedule, or schema is implemented by this documentation update. A future collection read contract must expose collection identity, distinguish artist-wide membership from an artist facet derived from selected recordings, and return canonical source/artist/show references for playable results. Pagination must operate on deduplicated results. Specify exact routes, DTOs, ordering, cursor behavior, counts, cache invalidation, and unresolved-item visibility before implementing that public interface.

Date precision and disagreements remain in retained interpretations and operator status. This design does not add date-note fields to listener APIs. Broader normalization can later consume the same evidence and performer identities; shared venues, song search, occurrence alignment, and correction submission are separate features.

## Proof and recovery

Verify the model through collection service contracts and, when defined, collection read endpoints. A collection implementation is ready when these behaviors hold:

- Adding a selected Aadam recording includes that source and its actual artist without including the artist’s unrelated catalog.
- Adding an artist-wide member includes its present and later published sources. An overlapping explicit membership returns each source once.
- The same Archive item discovered through two collections links to one canonical source with stable track IDs and URLs.
- An already imported source retains its native owner. Collection removal, failed discovery, and collection rollback leave playback intact.
- Unresolved items retain evidence without creating a public performer or a playable source. A paused approval prevents new publication while preserving playback and recoverable rebuilds.
- A broad `Jerry Garcia` creator can resolve to different projects for different items, with separate retained bases. Conflicts cannot silently change a published artist.
- Membership and assignment records survive database backup/restore and loss of workflow state. Repeating discovery, assignment, and rebuilds converges without duplicate rows.

Use additive migrations and back up the new records with the catalog. Keep collection indexing and publication separately pausable. Rollback retires new work and retains evidence and membership records; it does not undo imports or reparent sources. Existing intake ownership protections remain necessary during rollback. Production pagination, backup coverage, deployment, and pilot checks remain those in the [intake runbook](../plans/active/2026-10-16-taperssection-intake/verification-and-operations.md).

## Decisions still needed before implementation

The collection model and the separation of membership from performer assignment are agreed. The implementation must still specify its relational migration and service/write contracts, including safe reuse of existing sources and publication for existing artists. Public browsing needs the exact read contract described above. Jerry needs a bounded, independently reviewed evidence set, assignment rules, and an explicit project membership list. Settle these details in their respective work packages before enabling the behavior; they are not incidental importer choices.
