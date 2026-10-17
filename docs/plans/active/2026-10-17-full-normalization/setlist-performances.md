# Create or select a performance without a recording

A supported setlist can describe an engagement before Relisten has its audio.
`ShowPerformanceService.CreateOrSelectPerformanceAsync` creates or selects the
catalog `Show` for that engagement without creating a source or moving existing
recordings. Accepted editions can reference its actual show UUID.

This operation changes catalog identity. It does not accept a setlist edition,
select an edition for listening, or establish that a provider account describes
one distinct engagement. The caller must make those decisions from retained
provider evidence or an explicit operator review.

## Require an identity and an explicit target

Pass an artist with its current catalog ID and UUID, a
`PerformanceEngagementIdentity`, a `PerformanceEngagementTarget`, and the literal
`displayDate`. An optional `PerformanceEngagementPresentation(type, label)`
asserts presentation metadata; category and label do not identify the engagement.
Omitting presentation preserves an existing show's type and label. A new show
defaults to `Unknown` and a null label. Supply `Unknown` and null explicitly to
clear known presentation.

The identity has two supported forms:

- `Provider(providerName, resourceType, providerKey)` identifies one engagement
  within a provider's resource namespace. A page containing several engagements
  or an undifferentiated artist/date account is not sufficient.
- `Reviewed(key)` uses a stable `review:`-prefixed key chosen by a trusted
  operator. Keep that key across later date and label corrections.

The target is required:

- `CreateNew()` explicitly asserts a supported new engagement. If the identity
  key is already registered, the operation reuses its show UUID. For a new key,
  it creates a new UUID without claiming another show's date default.
- `Existing(showUuid)` asserts correspondence with that existing same-artist
  show. It preserves the UUID and adds the supported identity key. If the key
  already identifies another show, the operation fails without changing either.

A new provider key on a known date does not, by itself, prove another concert.
When correspondence is unclear, keep the edition unassigned. Do not call
`CreateNew()` just to give every account a show UUID. An explicit selection may
link another provider's account to the existing default when evidence supports
that correspondence.

```csharp
var show = await performances.CreateOrSelectPerformanceAsync(
    artist,
    new PerformanceEngagementIdentity.Provider("phish.in", "show", "12345"),
    new PerformanceEngagementTarget.Existing(existingShowUuid),
    "1975-03-07",
    new PerformanceEngagementPresentation(PerformanceType.Late, "Late concert"),
    cancellationToken: ct);
```

Retain the evidence supporting the identity, distinctness or correspondence
before calling. The existing `show_performance_keys` table retains the key-to-show
association; it is not an evidence ledger. Keep supporting evidence with the
provider account and its accepted edition or operator decision. This method does
not manufacture reviewed authority from a supplied prefix.

## Identity, metadata and transaction behavior

Provider keys use the exact qualified tuple, including punctuation and Unicode.
The existing `phish.in`/`show` identity keeps the importer's `phish.in:<id>` encoding.
Other provider tuples use the `engagement:` JSON tuple encoding defined by the
model. `identity.PerformanceKey` supplies the same durable key to a later
`AssignSourceAsync` call when evidence establishes that recording's assignment.
Do not reconstruct or change that encoding in another component.

The operation shares the artist mutation lock used by recording assignments and
show rebuilds. Concurrent calls for the same identity reuse one UUID. Selecting
a show from another artist fails. The operation joins an ambient catalog
transaction, or creates its own, and commits the key and show changes together.
Cancellation or failure rolls both back.

The supplied date is accepted metadata on every call. Supplied presentation
updates both category and label; omitted presentation preserves them. Later
supported metadata updates the registered show while retaining its UUID. This
method does not add provider revision ordering or an operator review ledger to the existing performance API. Callers must reject obsolete or
unsupported updates before invoking it.

Partial dates with a known year, such as `1975-XX-XX` or `1975-03-XX`, remain
literal in `display_date`. The existing sortable `date` uses a placeholder month
or day. That placeholder does not claim a known performance date. Fully unknown
years and unparseable dates cannot currently create a `Show`: the catalog requires
a sortable date. Preserve those literal dates and their evidence in unassigned
normalized editions. Do not invent a year.

## Publish and read the edition separately

After the catalog transaction commits, use the existing checked setlist binding
and accepted music contracts to publish the edition with the supported `ShowUuid`.
Creation changes the artist's catalog revision, so a proposal prepared before it
must refresh its binding and original read set. A new show does not authorize
publishing an older proposal with fresh clocks attached.

`NormalizedMusicService` continues to validate edition/show artist ownership.
`NormalizationReadService.ListArtistSetlistsAsync` can return an assigned edition
whose show has no recording. Edition selection is a separate accepted-state
operation. A source-free show survives `RebuildShows` and `RebuildYears`; legacy
playback browsing still requires source information and does not acquire empty
playback entries from this operation.

Rebuild derived artist data and invalidate the artist cache after the surrounding
catalog transaction commits. Unclear recording coverage continues to use the
existing default/current show through `AssignDefaultSourceAsync`; this operation
changes no recording assignment.

## Verification

`TestSetlistOnlyPerformances` checks source-free creation through rebuild and
normalized edition reads, explicit selection without source moves, separate
same-day creation, Phish importer-key compatibility, metadata corrections,
concurrent retry, provider namespaces, partial/unknown dates, later recording
attachment, outer-transaction rollback and cancellation while waiting for the
shared artist lock. A lock-wait case also verifies that caller mutation of the
artist DTO cannot change the locked identity. Existing `TestShowPerformances` covers the recording/default assignment behavior
this operation preserves. A dedicated
case checks alias selection without presentation and explicit clearing of known
presentation.

No schema migration is required. Automatic engagement resolution and policy
activation remain subject to the normalization evaluation strategy.
