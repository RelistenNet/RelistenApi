# Provider outcome and finalization audit — October 16, 2026

The audit found two finalization bugs: failed Jerry venue-repair finalization
could hide committed repairs from listener responses, and Setlist.fm could retain
wrong tour boundaries after a show’s date changed. The fixes reuse the existing
catalog rebuild and tour-date operations. A provider workflow records progress
in Temporal; its activities perform HTTP requests and database writes.

## Scope and outcome rules

The review covered Phish.in, Phish.net, Setlist.fm, Phantasy Tour, Jerry, and Local,
including their workflows, activity adapters, importer methods, and shared artist
coordinators. Independent reviewers validated the findings before implementation.

An item counts once after its activity settles. Applied and unchanged items count
as completed; skips and exhausted item failures have separate totals. These are
operation counts, not inserted-row counts. Continuation must preserve the fixed
selection or page cursor and accumulated outcomes after active items settle.

The review found no comparable counting or skip-handling bug in the other
providers. Phantasy Tour’s date repair already waits for complete enumeration.
Jerry orphan-venue cleanup already requires zero skipped or failed repairs.
Phish.in, Phish.net, and Local retain their progress and summaries across
continuation. The changes preserve these behaviors.

## Jerry: publish successful repairs before reporting finalizer failure

A venue repair updates the imported setlist. Listener responses use a separate
derived show row, which receives that venue only during catalog rebuilding.
Previously, if `Jerry.FinishVenueRepair` failed after successful item repairs,
the parent failed before rebuilding. The corrected venue remained hidden from
listener responses and cached output.

`JerryVenueRepairWorkflow` now rebuilds shows, years, and cache after an ordinary
provider-child failure, then reports the original failure. Cancellation and
expiration still stop without rebuilding. If rebuilding itself fails, the
operation reports that rebuild failure. The same policy already applies to
ordinary artist refreshes. The coordinators share the nested-expiration check
so they apply the same deadline policy.

## Setlist.fm: derive tour boundaries after complete enumeration

Concurrent item imports extend tour dates atomically. This protects another
item’s work, but extension alone cannot correct a boundary inward. If a tour has
shows on January 1 and January 10 and upstream corrects January 1 to January 5,
the old implementation updates the show while retaining January 1 as the tour
start indefinitely.

The new `SetlistFm.RefreshTourDates` activity runs after all listing pages and
their items settle. It uses the existing transaction and
`TourService.RefreshDatesFromSetlists` to derive ranges from committed setlists.
Partial item failures still permit this refresh. A failed listing, cancellation,
or expiration prevents finalization. The result is an attempt-local count of
changed tour rows and does not change item totals or listener API contracts.
The data-service method now returns `Task<int>` with this affected-row count;
its existing callers can continue to await it without using the result.

The query updates nonempty tours for the whole artist. It preserves metadata for
tours with no imported setlists, which can be valid for providers with upstream
tour dates. A read-only production mapping check at 20:52 UTC found only Guerilla
Toss using Setlist.fm, alongside Archive. No current Setlist.fm artist also uses
Phish.in, Phantasy Tour, Jerry, or Local. This is current configuration, not a
database constraint. A future tour-authoritative provider must account for this
artist-wide ownership before combining mappings.

## Verification and delivery

Focused verification passed:

- Six new Jerry workflow cases cover failed finalization and replay, cancellation,
  expiry, skipped/failed repair cleanup, and empty selection. Together with the
  existing artist and batch workflow fixtures, 28 tests passed.
- Six Setlist.fm cases cover complete and partial imports across continuation,
  failed enumeration, cancellation/expiry, and a real PostgreSQL/Temporal date
  correction followed by an unchanged refresh with stable setlist IDs and UUIDs.
- A fresh implementation review found no actionable correctness, readability, or
  unnecessary-complexity issue. The full solution build passed.

The full local suite passed **294 tests**, with zero failures and 24 existing
opt-in tour fixture skips, in 2 minutes 51 seconds. CSharpier checked 372 files;
whitespace and local documentation links passed. These changes are pushed
to [API PR #92](https://github.com/RelistenNet/RelistenApi/pull/92) without merging.
They have not been deployed; production remains on `0a5b85e`. Use the existing
[pause-and-drain procedure](../production-cutover.md) before changing active
workflow code. Deployment uses the existing services and schema.
