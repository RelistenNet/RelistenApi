# Observe factual owners without creating work cycles

A recording's accepted credits depend on a person's identity. Matching that
person can also read those credits as artist context. Both reads need freshness
checks, but they cannot both become edges in the acyclic work-dependency graph.

`NormalizationSubjectObservation` records a subject UUID, desired generation,
editorial revision and published work UUID. Capture all four values with the
candidate facts in the same primary database snapshot. This is a factual read
set, separate from the causal dependencies required by typed references.

## Admit and publish the original observations

`BeginWorkAsync` and `BeginContributedWorkAsync` accept the trailing optional
`expectedOwnerObservations` parameter. Known-owner observations and candidate
pool membership are independent reads. A trusted decision can observe its selected
person and source owners without searching for candidates. Such a read needs no
invented candidate epoch. A matching projection must still supply every candidate
universe it actually consumes to fence new rivals and changed membership. The
domain reader owns this complete read set; an owner's domain alone cannot identify
which facts were consumed. Publication declares its candidate write scopes
separately from these read dependencies.

Admission locks the sorted union of the primary owner, causal dependencies and
observed owners, after catalog clocks, candidate scopes and input resources.
It compares every original observation before changing configuration. Missing
owners, inconsistent duplicates or changed stamps reject the request without
committing a partial replacement. Observations create no `work_dependencies`
rows, so self-observation and reciprocal factual context are valid.

The ticket retains the original observations for audit and exact request replay.
Publication locks the same complete owner set and compares the observations
before invoking the domain callback. A different published work UUID is stale
even if desired generation and editorial revision stayed the same.

Self-observation needs one distinction. Admission may change its own owner
configuration. Keep the original self-observation in the ticket, then use the
ticket's configured primary-owner state for publication. Another review or
publication still fails that primary-owner check.

## Preserve contributor ownership

`NormalizationInputContribution.OwnerObservationSubjectUuids` replaces one
contributor's complete observed-owner key set. The contribution state exposes
those keys. The caller must supply original observation stamps for the complete
contributor union, including retained peers; admission cannot read fresh stamps
to justify facts that a caller did not read.

Observation membership participates in configuration hashing, contribution
revision checks, baseline adoption and empty tombstones. Observation values
participate in exact work-request hashing. An omitted or empty observation set
retains the earlier request/ticket representation. Recording-only extraction
cannot acknowledge these additional facts: an incomplete union fails admission,
and its proposal format rejects tickets containing owner observations.

## Let unchanged processing settle

Persisted pending detection compares an observed owner's desired generation and
editorial revision, together with the consumed candidate-universe generations.
It deliberately ignores a change only to that owner's published work UUID.

For example, person matching can observe recording credits, while recording
credits causally depend on the person. A fresh unchanged person completion can
make the recording pending through its causal dependency. Once that recording
also completes unchanged, its new receipt must not alone make the person
pending again. Accepted matching changes still advance candidate generations;
review and configuration changes still advance editorial or desired revisions.

A receipt-only change does not schedule a published observer, including one
without a candidate pool. Consumers must select clocks that invalidate the
semantic facts and relationship membership they use. Known target observations
do not replace the epochs required by a matching projection. Exact in-flight
owner checks remain strict; the durable pending query lets unchanged processing
settle.

## Verify and deploy

Migration 31 adds observational membership and published desired/editorial
watermarks, plus contributor ownership. Successful publication records current
watermarks under the held locks. Callback failure or cancellation rolls back
facts and watermarks together; exact replay keeps the original ticket.

Public work-service tests cover real reciprocal person/recording context,
self-observation, each stale stamp, editorial-only changes, receipt-only pending
behavior, contribution unions, baseline adoption, rollback and retry. Typed
facts-reader tests must additionally prove that their selected projection and
all clocks share one snapshot.

The [pool-independent owner verification](../../../research/2026-10-17-full-normalization/scale/evidence/owner-observations-without-pools-v1.json)
records all 29 passing owner-observation cases, including unchanged admission
replay and stale-owner checks with no candidate search. It retains the earlier
failing baseline separately from the corrected combined run.

Apply migration 31 before consuming these tickets. Before enabling automatic
matching, drain older work that lacks the required read set. Stop normalization
coordinators and publication before reverting to a binary that ignores owner
observations. Retain the tables, evidence and contribution ownership. This
increment enables no automatic policy, public read flag or production job.
