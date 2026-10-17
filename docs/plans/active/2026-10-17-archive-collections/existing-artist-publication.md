# Publish missing Archive recordings for an existing artist

An operator can approve a retained Archive candidate for one existing Relisten artist. Intake then publishes that artist's missing recordings through the normal recording writer and artist workflow. An exact recording already in the native catalog keeps its source identity and native importer. Collection membership never grants publication permission.

This contract is for the engineer and reviewer/operator. It extends [reviewed performer assignment](performer-contract.md) and [collection resolution](contracts.md). Apply migrations 16 and 17 before using it. Migration 17 adds the durable `is_existing_artist` flag to artist candidates; earlier candidates default to `false` and retain their existing ownership rules.

## Approve the catalog identity once

Use a candidate from retained discovery or create one with `ProposeProjectAsync`. Supply the artist's actual name and slug, plus finite reviewed labels that identify that performer. A broad collection label such as `Aadam Jacobs` is not an artist alias. A broad provider creator such as `Jerry Garcia` can route to a project only through that item's reviewed performer decision.

The operator service is:

```csharp
Task<ArtistId> BindExistingArtistAsync(
    Guid candidateId,
    ArtistId artist,
    string reviewBasis,
    CancellationToken token = default,
    DateTimeOffset? deadline = null);
```

The operation locks the artist registry and candidate, reloads the catalog identity, and requires exactly one live registry match equal to the requested artist. The retained review basis must be nonempty. It records the current name, slug, numeric ID and UUID, sets `is_existing_artist=true`, and approves the candidate. It does not change artist features, flags or provider mappings. Repeating the same binding is safe. Selecting another artist or converting an intake-created artist into a native artist is rejected. A later catalog rename or missing identity holds intake publication; it does not prevent the native artist from being edited. After a rename, another explicit bind to the same UUID and numeric ID refreshes the saved name and slug, provided its retained labels do not match another artist. A hold never authorizes changing the bound identity.

For a clear creator label, an approved binding permits later clear recordings without another item review. A retained per-item performer decision still requires its own approval and applicable supporting evidence. Artist approval cannot waive unresolved artist billing, unsupported dates or unavailable audio. Unclear coverage of several performances on one date remains publishable under the default show, with internal uncertainty evidence. Revoking either approval preserves accepted playback.

```csharp
var candidate = await intake.ProposeProjectAsync(
    artist.name, artist.slug, [artist.name], approved: false);
await intake.BindExistingArtistAsync(
    candidate, new ArtistId(artist.uuid), "Reviewed exact catalog performer");
// Use the existing named artist refresh and its normal admission ID.
await client.RefreshTapersSectionArtistAsync(
    new RefreshTapersSectionArtist(new ArtistId(artist.uuid)));
```

The returned import handle must be awaited and its typed result inspected. A completed workflow can contain held or failed items. When the provider creator is broad, retain the immutable observation and call `ArchivePerformerReviewService.ReviewAsync` before the refresh, as described in the performer contract.

## Recording ownership

Artist identity ownership and recording ownership are separate:

| Case | Owner and behavior |
| --- | --- |
| Intake-created artist | Candidate membership reserves its reviewed identity and claims assigned intake items before first publication, including paused or diagnostically conflicted candidates. |
| Existing artist, unpublished intake item | Candidate approval authorizes a possible missing recording; it does not claim a native source or reserve the artist's labels against native registry operations. |
| Exact native Archive source | The shared resolver accepts the exact source and artist UUIDs. The intake row releases any unpublished candidate claim. The native importer can continue updating or deleting that recording under its ordinary rules. |
| Missing recording committed by intake | The recording and accepted publication references commit atomically. `published_source_uuid` establishes durable item ownership even if approval pauses or metadata changes. Ordinary Archive import and deletion checks exclude it. |

Native reuse requires one exact item identifier with an Archive.org source link. A source from another provider, a conflicting artist, several matching sources or a changed accepted identity does not permit adoption. Missing or conflicting accepted references remain held for repair.

Publication uses `ingest/artist/{artistUuid}`, the same admission identity as ordinary native imports. Within each item transaction, native and intake writers lock the same retained item row before checking ownership. Intake repeats exact native resolution under that lock: if a native writer committed first, intake reuses the source; if intake committed first, its published ownership makes the native writer skip. The source writer's existing UUID formulas remain unchanged. There is no competing writer for an accepted item and no transfer of an existing source to intake.

## Publication, holds and recovery

The item activity fetches current metadata before its catalog transaction. Under candidate → intake item → shared resolution locks, intake checks approval, evidence and current source identity, writes an eligible missing recording, and binds the shared accepted reference. A source or binding failure rolls back all source, set, track and publication writes. The immutable observation remains available for retry.

Every successful owned publication advances a durable catalog revision. Artist refresh rebuilds shows, years and caches, then acknowledges the captured revisions. A failed rebuild leaves dirty work in PostgreSQL. A later refresh repairs that committed work even if approval was paused meanwhile. Recovery for an existing artist never changes its `AutoCreated`, featured flags or native provider mappings. Explicit collection selection continues to follow that native artist's visibility policy.

When the artist and date are supported but the exact same-day performance is unclear, intake publishes playable recordings under the stable date default. An existing recording keeps its primary show. Combined or unresolved assignment evidence stays internal and cannot supply performance-specific enrichment. Title, track and review updates still apply. The public API does not yet expose coverage uncertainty.

A genuine date conflict with another recording already assigned to the performance still holds the update. Intake preserves the last accepted publication observation, date, source and playback identities. It commits any retained assignment evidence and advances the dirty catalog revision without changing accepted publication references, so paused recovery also repairs derived enrichment. Repeating a held attempt can advance this diagnostic revision again. The workflow reports a held item rather than a transport failure.

Collection reads use the shared exact accepted reference after the normal catalog rebuild. Several membership rules or collections selecting the same item still refer to one canonical recording. Counts of retained observations, reused sources, new publications and holds must be reported separately.

## Deployment and verification

Stop older intake and native Archive workers before binding existing artists. Older ownership predicates interpret every mapped candidate as an intake-created artist and can suppress native recording refreshes. Apply the additive migration and deploy matching workers before resuming writes. Retain the new flag, observations, review decisions and accepted references on rollback; do not reverse ownership by dropping records. An older worker cannot safely resume these writes without a reviewed compatibility change.

`TestReviewedPerformers` verifies explicit binding, missing publication, exact native reuse, unchanged native flags, collection visibility, broad-creator review, conflicting metadata and stable playback through listener contracts. `TestTapersSectionPersistence` runs publication rollback/retry, paused rebuild recovery and native import/deletion protection for both intake-created and existing artists. These synthetic tests use disposable catalogs and provider fixtures. The separately retained Aadam sample verifies actual upstream selection and publication; discovery counts alone do not establish playable coverage.
