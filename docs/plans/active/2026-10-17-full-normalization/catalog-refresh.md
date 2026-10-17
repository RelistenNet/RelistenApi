# Refresh stored recording evidence

Use the catalog refresh path when normalization must consume current stored
credits or titles. Seeding fills missing evidence; rebinding updates catalog
associations. Neither operation refreshes an existing recording's wording.

Read [catalog binding](catalog-binding.md) for ownership, complete input sets and
publication. The services below supply a bounded recording snapshot and reject
work that would pair old facts with newer catalog revisions. They do not start a
backfill, select a matching policy or accept an identity.

## Capture current facts and their dependencies

`NormalizationLegacyInputService.RefreshRecordingAsync(sourceUuid, cancellationToken)`
returns a `NormalizationLegacyRecordingSnapshot`, or null when the source no
longer exists. The result contains the exact admitted capture receipt and the
catalog revisions read with those facts.

The service reads the retained resource generation before querying the catalog.
One primary, read-only repeatable-read transaction then selects the recording,
artist, show, venues, sets, related tracks and the clocks of their existing
context owners. Capture runs after that transaction closes, using the prefetched
resource generation. A concurrent refresh can win admission; the loser throws
`NormalizationStaleInputException` with its own retained receipt. It must not
return a winner's input paired with the loser's catalog clocks.

The v2 representation is `legacy-recording-snapshot-v2`. It preserves wording,
identities and incomplete relationships while excluding operational timestamps
and rating fields. The fetch receipt records observation time. Equal facts reuse
one immutable revision; each admitted refresh still advances the resource
generation. This keeps content caching useful without allowing an older response
to overwrite a newer admitted fetch.

The original `SeedRecordingAsync` contract and v1 decoding remain supported.
Seeding never replaces an existing input, including one produced by refresh.
Both paths enforce the recording snapshot's track, set and byte limits. They
reject ambient catalog transactions and observe cancellation.

## Bind the exact refreshed snapshot

`NormalizationCatalogBindingService.BindRefreshedRecordingAsync(snapshot,
recording, expectedArtistUuid, cancellationToken)` accepts the decoded recording
from that refresh. It requires an admitted v2 receipt, matching resource and input
revision, and a nonempty producer catalog read set.

Within the same repeatable-read transaction used for association lookup, the
binder verifies the retained receipt and original producer generations. A changed
generation raises `NormalizationConflictException`. Retry the full refresh and
decode; an association-only bind cannot make old wording current. The returned
catalog dependencies include the producer's context owners without advancing
their generations.

Missing derived context stays unknown. Valid source-level credits remain usable
when a show or a sibling file is unresolved. Only positive returned associations
authorize processing that catalog relationship. A raw venue or foreign context
observation still needs its own domain binding and freshness checks; a recording
refresh does not authorize publishing every decoded context as current.

## Admit the consumed revisions atomically

Pass the binding snapshot's complete `Inputs` list as `expectedInputRevisions`
to `BeginWorkAsync` or `BeginContributedWorkAsync`. Pass its catalog dependencies
too. Work admission locks the retained resources and checks their current revision
UUIDs before changing configuration or contributor ownership. A mismatch raises
`NormalizationConflictException` without a partial update.

Admission and publication check different intervals. If the current input revision
UUID changes after binding but before admission, admission rejects the old binding. An unchanged-content fetch can retain the same revision UUID;
its new generation is captured by admission. If the generation changes after
admission, publication rejects the old ticket. Exact work replay retains its
original read set and result.

For contributed work, the expected revision list describes the incoming
contributor's consumed inputs. The returned ticket can contain other contributors' inputs. Consume that complete
ticket before extraction and matching; do not replace an artist's repertoire
with proposals computed from only the incoming recording. The
[complete-input recording processor](provider-processing.md) uses a separate
batch admission method to pin all prepared legacy/Archive/Phish inputs and their
original contributor membership. Local/setlist coordinators, dependency
reconciliation and the automatic resolver remain required. The
[recording processor and page runner](recording-processing.md) retain completed
extractions separately from acceptance.

## Failure and recovery

A source disappearing, a capture losing admission or a dependency changing does
not withdraw prior accepted relationships. Evidence remains available, and public
playback continues to use current catalog joins. Resume from a new refresh when
the facts changed; retry an exact committed work operation with its original
UUID. Reviewed corrections retain their existing authority.

A resumable coordinator must save the fetch UUID and original producer read set
before admitting work. `NormalizationEvidenceService.GetCaptureAsync(fetchUuid)`
recovers that receipt and its immutable revision, including historical or
nonadmitted fetches; an unknown UUID returns null. It does not substitute the
current input or establish freshness. Keep the saved producer generations and
use the checked binder again. Reading the current input after restart could pair
a different revision with the attempt's original facts.

Verification covers changed credits/titles, unchanged-content reuse, ignored
operational updates, foreign context owners, snapshot limits, missing sources,
cancellation, competing refreshes, stale binding/admission/publication and typed
credit publication with an unresolved sibling file. These checks establish the
refresh contract; they do not measure person or song matching accuracy.
