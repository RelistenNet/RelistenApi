# Batch current person-target proofs

Source alias processing now reads current person-target proofs in bounded
batches. It retains each selected original person, all identity members and
every owner observation. Publication still rereads those proofs independently.
No public interface, stored contract, producer version or identity changes.

An original person can redirect through several merges to a current terminal
identity. A target proof includes the complete reverse component: every original
that resolves to that terminal, its revision, redirects and Work owner clocks.
Two selected originals can share a component while keeping distinct alias
ownership. The reader must return both complete proofs.

## Read and publication responsibilities

`SourceActorReviewTargets.ReadManyAsync` resolves requested originals, reads each
distinct terminal component, then assembles targets in first-request order.
Acquisition uses chunks of 64 roots. Each root retains its independent
100,000-member limit and overflow sentinel; a shared aggregate limit cannot
discard another root's members. The singleton reader delegates to this code.

The trusted source pass authenticates claim authority and active supports before
collecting carried or reviewed targets. It reads all collected targets in its
current read-only repeatable-read transaction. Stage preparation can reuse its
selected original's proof from that same transaction. Unexamined machine winners
remain lazy, preserving useful progress before a later independent failure.

The accepted writer acquires fresh targets in its own publication transaction
under Work's existing owner protections. It compares complete proofs and restores
only the primary admission observation, as before. Saved targets are evidence,
not a current cache. No target state is cached across stages or transactions.

At 1,000 distinct targets, one batch read needs at most 16 resolution commands
and 16 component commands instead of 2,000 singleton commands. This is a count
derived from the code, not a measured throughput result. Other source validation,
storage and finalization work remains. Query buffering can hold 64 bounded
components; this change does not establish a memory budget.

## Verification and remaining work

The [verification receipt](../../../research/2026-10-17-full-normalization/scale/evidence/source-target-batch-v1.json)
records 406 passing source lifecycle checks and two passing small-scale controls,
for 408 distinct cases. Three new cases are included in the 406, and also pass
against the preceding unbatched API assembly. They verify exact initial proofs,
ordered topology, current revision advancement, original ownership, recovery and
replay. They also verify refusal after a shared terminal owner or redirect changes.
Managed support publication advances person revisions, so later preparations
must retain current revisions rather than repeat the original review's values.

Four explicit large/restored-history cases were not run in this packet. A new
100,001-member overflow fixture was also not run. Full-size distinct-person,
repeated-claim and 1,001-source lifecycles, final-code full-suite verification and
catalog cost checks remain required. Automatic matching remains inactive.
