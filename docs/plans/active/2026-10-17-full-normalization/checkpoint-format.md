# Store complete people checkpoints without repeating evidence

People resolution and acceptance use the private `people-checkpoint-v2` storage
format to share repeated evidence within each checkpoint. A recording with
several credited roles can compare the same prolific person many times. The
stored value shares identical evidence and original observations, while public
service readers return the existing expanded DTOs for every claim.

The 32 MiB limit remains unchanged. It applies separately to a resolution
preparation, proposal batch, acceptance plan or acceptance stage. An application
is an aggregate of independently bounded rows; its plans and stages do not share
one combined budget. This storage change does not enable a matching policy or
establish catalog backfill capacity.

## Meaning and ownership stay the same

The codec shares only exact values within one stored checkpoint. Each claim
keeps its query, limits, complete retrieval result, query-specific scores and
reasons, comparisons, issues and file scope. Complete matching facts and bounded
raw witnesses remain available. Identical evidence can be shared across different
queries without making those queries identical.

Original owner observations retain their subject UUID, desired generation,
editorial revision and published Work UUID. Lists retain their order, including
the distinction between null and empty. A different clock or evidence value
remains a different value. No current database read, global people epoch or
candidate UUID can substitute for an original observation.

Sharing stops at the checkpoint. A later acceptance stage may legitimately
reread derived candidate facts after an identity publication. Its new facts and
clocks belong to that separate stage; original provider inputs and extraction
receipts remain unchanged. Contributor ownership and support retirement retain
their existing contracts.

`PeopleCheckpointCodec` owns the encoding and decoding. Resolution and acceptance
stores use it for their checkpoint columns and return the same public model
types. API schemas, candidate-facts and semantic processing versions, accepted
actor-fact hashes, candidate-query fingerprints and Work request hashes do not
change. Separate Work tickets, stage admission tickets, results and publication
receipts keep their existing JSON representation and transaction ownership.

## Saving, comparing and recovering

The store locks the existing attempt or application row and checks for a winning
checkpoint or replay before applying the new-write size guard. An existing
immutable winner is returned without encoding an ignored competing request.
Only a new checkpoint takes compact-format write admission. Encoding performs
bounded work while that existing row lock is held; it adds no new lock type or
lock order.

Checkpoint comparisons use their decoded meaning. Storage-table order or index
numbers cannot make equal facts conflict. A valid historical checkpoint near its
old size limit remains readable and comparable even if normalizing it would add
new descriptor overhead. A publication callback that only records publication
state does not reapply a new-format write limit to an already saved checkpoint.

Admission counts deterministic codec UTF-8 bytes. PostgreSQL `jsonb::text` can
add whitespace or change escaping, so its text length is a separate storage
measurement, not the admission rule. Repeated evidence can reduce encoded size;
genuinely distinct facts and their references still count toward the unchanged
limit. Unique overflow fails without dropping claims, witnesses or owner stamps.
Candidate semantic-facts limits remain independently enforced.

Malformed or unused references, conflicting inline data and unsupported format
versions fail rather than produce partial facts. Cancellation, stale admission,
publication rollback and retry retain the existing saved-state rules. Accepted
writes and their publication receipt still commit together. The codec cannot
refresh stale work or grant authority to a proposal.

## Historical rows and mixed formats

Readers accept both historical expanded JSON and `people-checkpoint-v2`.
Existing rows remain immutable. An old preparation can be followed by a compact
batch, and an old plan can be followed by a compact stage. Each is decoded before
semantic comparison or publication checks. Omitted historical optional fields
keep their existing model defaults; compatibility does not invent missing data.

Configured-checkpoint lookup still requires the exact admitted contributor
revision vector, including untouched contributors and tombstones. It recognizes
both payload formats, then decodes the selected preparation or stage before the
existing ownership checks. A matching owner generation alone remains insufficient.
Historical payloads missing the membership headers needed for that proof remain
unsupported. No fresh Work or owner clocks are substituted.

No database migration or rewrite of old checkpoint rows is required. The format
uses the existing JSONB columns and immutability rules.

## Source actor candidates

Source actor resolution uses the same storage principle through its separate
`SourceActorResolutionCheckpoint` codec. New preparations and batches use
`source-actor-resolution-checkpoint-v2`; readers also accept the original v1
format. This change addresses repeated comparisons after a source has published
many aliases for one person. Each query previously stored that person's complete
matching facts again, which could exceed the checkpoint limit on a later run.

V2 stores exact complete candidate fact bodies and owner-observation lists once
per checkpoint. Every query retains its ordered retrieval result, candidate
order, scores, reasons, limits, issues and read set. A changed fact or owner clock
requires a separate block. Decoding rejects conflicting candidate identities,
missing or unused references, incomplete owner observations and hidden inline
data. Sharing does not authorize a match or omit evidence.

Configured-input recovery recognizes both formats and still authenticates the
original contributed Work request and membership vector. Historical v1 rows
stay unchanged. Logical equality can compare a valid v1 row at the old size
limit even when v2 framing would exceed that limit; a new v2 write must still
fit within 32 MiB. No schema, processing version, matching policy, public DTO or
Work request hash changes.

The fixtures include actual v1 public-service values and two files generated
through the unchanged v1 writer at exactly 32 MiB. Those two boundary files pad
one query hash solely to test storage compatibility; they cannot establish a
matching or identity decision. The separate restored-database probe tests
published replay, recovery after admission but before batch storage, new v2
work configured from v1 history, and refusal of an unrelated ordinary admission.
The [source publication plan](source-actor-publication.md) owns the large
publication and catalog-capacity requirements.

## Deployment and rollback

The current binary reads both formats and immediately writes compact checkpoints.
There is no writer-format feature flag or reader-only mode. Pause all people
resolution, acceptance and source actor writers before deployment. Deploy
compatible binaries to every API, worker, operator and resume path that reads
these checkpoints, then resume writers only after those paths are compatible. Registering a service
starts no processing, schedule or identity policy.

Before rollback, pause people and source actor writers. Roll back only to a
release that can read all retained formats and handle the saved attempts under
their processing-version rules. A binary that cannot read compact checkpoints is not a safe rollback
for that data. Retain compatible readers, original attempts, Work receipts,
accepted identities and credit support. Do not rewrite immutable rows to make an
older binary accept them, or delete an identity left by interrupted acceptance.

## Verification boundaries

Verify complete roundtrip through all four checkpoint roots, mixed-format reads,
first-winner behavior and exact replay. Include query-specific score differences,
Unicode citations, every original owner observation, null/empty values,
malformed references and near-limit JSONB reads. Repeated evidence must fit
without weakening the rejection of genuinely distinct overflow.

Through the public services, verify a multi-input, multi-role recording that
exceeds the old expanded representation's budget. Interrupt after preparation,
after plan and after admission; resume with the original UUIDs. Check accepted
UUIDs, revisions, support ownership and receipts, and keep stale-owner,
contributor-only retirement and transaction rollback coverage. Report expanded
DTO bytes, codec admission bytes and PostgreSQL text bytes separately.

These checks establish storage and recovery behavior. They do not establish
matching accuracy, throughput or peak-memory improvement. The
[main plan](README.md#progress) owns the current executed verification record.
