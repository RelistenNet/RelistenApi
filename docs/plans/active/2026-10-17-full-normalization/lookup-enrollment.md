# Retain recording lookup outcomes and processing handoffs

`NormalizationRecordingLookupService` records catalog association separately
from extraction. A missing recording can settle its lookup without clearing
successful extraction or accepted normalization data. A later catalog change
makes that lookup pending through the [original lookup clocks](lookup-fences.md).
For a bound recording, publication also commits a durable processing handoff.

These are internal .NET service contracts. This increment adds no HTTP route,
scheduled worker, automatic matching policy or public read enablement. Legacy,
Archive and Phish handoffs use the [shared complete-input processor](provider-processing.md).
A stored handoff alone does not mean extraction completed.

## Entry points and ownership

Use these `NormalizationRecordingLookupService` methods:

- `LookupLegacyAsync(attemptUuid, sourceUuid, expectedArtistUuid?, ct)` looks up
  one stored recording. An absent recording uses an actual UUID presence probe;
  an existing recording uses a retained catalog snapshot and its producer clocks.
- `LookupRetainedAsync(attemptUuid, inputReference, expectedArtistUuid?, ct)` pins
  one actual retained Archive item or Phish show. An unsupported representation
  or missing input is an error; the capture coordinator must obtain that input.
- `GetAsync(lookupUuid, ct)` returns the last published historical outcome. Use
  `NormalizationWorkService` to inspect whether its owner is now pending.
- `RetryAsync(newAttemptUuid, lookupUuid, ct)` starts another attempt for changed
  facts. Retained requests select the current input head once; replay resumes
  that saved request even if a newer head arrives.

Migration 34 adds registrations, attempts and handoffs. A registration's unique
key contains the mode, exact resource and nullable expected artist. Two expected
artists therefore have independent lookup outcomes for the same recording.
Input revisions and processing versions belong to immutable attempts, rather
than changing the registration's identity.

The lookup owns `(lookups, recording, lookupUuid)`. WorkService owns admission,
read-set checks, pending detection and publication watermarks. The lookup store
owns immutable requests, first-wins preparations, terminal outcomes and delivery
receipts. Its publication callback uses WorkService's supplied transaction; it
does not acquire work locks through a second protocol.

## Preparation and publication

Preparation preserves the original input revision, catalog dependencies,
identifier and UUID lookup clocks, and configuration compare-and-swap value.
A missing legacy recording instead preserves its original absence probe.
Save the complete preparation before admission; concurrent callers resume the
saved winner. A later arrival cannot replace its absence clock with a fresh one.

Legacy retries may reuse their registration's published capture when its
revision is still current and checked binding validates its **original** producer
clocks. They obtain fresh lookup and downstream configuration values. A changed
catalog or input revision requires a complete refresh. This prevents independent
lookups for the same recording from endlessly refreshing unchanged bytes and
invalidating each other. The evidence service still advances the resource
generation for every admitted fetch; this reuse does not change that contract.

Publication checks the complete saved read set. It commits the outcome and
lookup watermark together. For `Bound`, the same transaction also creates a
handoff with a stable downstream attempt UUID. A negative outcome has no
processing handoff and cannot modify the extraction owner's watermark.
Unconsumed extra owner inputs or dependencies leave the lookup unpublished.

`Published` means the lookup outcome became current. It can describe `Missing`,
`Ambiguous` or `ProviderConflict`, as well as `Bound`; it does not assert that a
normalized identity was accepted. `Stale` preserves changed observations for
audit and leaves the prior publication current and pending.

## Dispatch and recovery

Call `NormalizationRecordingLookupDispatcher.DispatchAsync(handoffUuid, ct)`
after publication commits. The dispatcher freezes the initiating handoff in the
processing header, then calls the legacy or retained-provider entry point of the
[shared recording processor](provider-processing.md). That processor preserves
the original initiating evidence and prepares the complete owner input set.
Legacy delivery uses the retained capture instead of fetching it again. The extraction owner
and its `relisten / recording / sourceUuid` contributor remain separate from
the lookup owner.

The dispatcher acknowledges delivery only against a durable terminal processing
receipt for the exact source, attempt, version and original initiating evidence.
The complete preparation may also contain peer inputs. A returned dispatch result
confirms delivery; its processing receipt can still report `Stale` or `Unresolved`.
An unresolved attempt that stops before preparation must still identify the
actual initiating handoff. Retained provider processing never substitutes legacy
wording for that handoff's input.

A retained handoff's target version remains null because it was created before
consumer admission. The processing header fixes the actual consumer version.
Legacy handoffs retain their originally prepared target version and admission
values. Historical terminal receipts keep exact replay. An older legacy header
left without preparation can acknowledge only its verified version-change
`Unresolved` receipt; it cannot claim published extraction.

`GetPendingHandoffsAsync` supports a mode filter, bounded pages and a stable UUID
cursor. Operators can process either mode in bounded pages. Enrollment and delivery are explicit service
operations; clocks alone do not schedule them.

| Interruption or change | Recovery |
| --- | --- |
| Before preparation is saved | Repeat preparation; never attach fresh clocks to an old orphan receipt. |
| After preparation or admission | Reuse the attempt UUID and its saved request/read set. |
| During publication callback | Roll back outcome, handoff and watermark together; resume the attempt. |
| After lookup publication, before dispatch | Read the committed pending handoff and dispatch its stable target UUID. |
| After extraction publication, before acknowledgement | Replay that same processing attempt and acknowledge its durable receipt. |
| Evidence changes | Retain the old outcome; use a new lookup attempt UUID. |
| Unfinished attempt from another processing version | Preserve it and require a new attempt UUID; terminal historical results retain exact replay. |

No network call or extraction runs while a lookup publication transaction is
open. Cancellation leaves unfinished work resumable. A terminal attempt is
immutable; reusing its UUID with a changed request conflicts.

## Verification and rollout

The lookup store and public service integration fixtures exercise real disposable
PostgreSQL storage. They cover source arrival before admission/publication,
negative lookup invalidation, retained input changes, independent artist scopes,
original producer fences, stale retries, atomic callback rollback, cancellation,
delivery crashes and exact replay. They also check that lookup and extraction
acknowledgements remain separate and dispatch does not refresh the capture.
These tests establish recovery contracts, not matching accuracy or throughput.

Apply migrations through 34 for lookup registration and through 35 for current
processing and dispatch. Stop incompatible
normalization workers before changing processing versions. Do not drop retained
lookup history on binary rollback: stop enrollment and dispatch, retain the
tables, evidence and clocks, and keep existing playback operational. Local and
setlist coordinators, automatic resolution, evaluation,
full backfill and deployment rehearsal remain required by the [main plan](README.md).
