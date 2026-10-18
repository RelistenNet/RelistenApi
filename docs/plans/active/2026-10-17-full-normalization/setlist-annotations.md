# Read provider annotations within accepted setlists

`GET /api/v3/normalization/setlists/{editionUuid}/annotations` exposes the provider
annotations retained by an accepted structure decision. Clients can show footnotes,
prerecorded-playback flags, transition markers and cover or guest performer claims
without fetching an internal source account. These remain provider assertions;
accepting their correspondence to a set or occurrence does not establish authorship
or a canonical person identity.

Use this route with [accepted sets](setlist-public-reads.md) and the existing
`setlists/{editionUuid}/occurrences` route. It uses the normalization read flag,
`Cache-Control: no-store`, and the existing `items` / `next_cursor` envelope.
`limit` is 1–200, default 50. An unknown or retracted edition returns 404. A known
active edition without a structure returns an empty page. Invalid UUIDs, limits
or cursors return `400 invalid_request`; disabled reads return
`503 normalization_unavailable`.

## Read one source assertion in its own context

Each item groups the annotations for one provider account, one source set or one
source passage. Its `scope` is `account`, `set` or `passage`. Each item identifies
its exact source through `source_index`, `proposal_attempt_uuid`,
`input_revision_uuid` and `account_index`. The source's qualified provider identity,
observation scope and original evidence appear in `origin` when detail is available.
A page member records where an assertion was observed; it does not prove that the
provider has no newer version of that item elsewhere.

`source_set_index` and `source_passage_uuid` locate the original set or passage.
`set_uuids` and `occurrence_uuids` contain only explicit accepted correspondence.
The `correspondence` field distinguishes `mapped`, `unresolved` and `unmapped`;
`account` identifies the source account itself. An unresolved passage retains its
observed title and annotations. The endpoint does not guess a correspondence from
a title, date or array position, and does not publish an operator's free-form
unresolved reason.

`structure_revision_uuid`, `revision`, `basis` and `decision_uuid` describe the
accepted structure image that retained these assertions. They are not separate
reviews of every footnote or performer claim. An item's `index` is a traversal key
within that image, not a musical position or a permanent annotation identity.

Pages visit each source account, its sets and its passages in that order. They then
visit the next source account. Cursors pin the edition, route and exact structure
revision. If a correction replaces that revision, the next request returns 400;
restart pagination. Provider input updates alone do not change this traversal.

## Typed details and exact support

`notes` contains finite note kinds: `info`, `source_credit_note`, `pre_show`,
`post_show`, `footnote`, `setlist_notes`, `tour_note`, `marker` and `notes`.
Unsupported future kinds are identified as `unsupported` with
`detail_status: unsupported_kind`; their raw field names are not an alternate
unstructured API.

Each note has `text`, `detail_status` and original `evidence`. `text` is display
prose only when its status is `available`. Plain JSON strings remain plain strings.
For source markup, the decoder records a separate plain-text derivation; the
original exact quote remains in evidence. That derived text does not claim to be
an exact quote. Older notes without an explicit text format, unknown formats and
markup without a derivation return `not_decoded` and null display text. Clients
must render evidence quotes as text, including quotes that contain markup.

`footnotes` preserves every definition, including duplicate numbers, unnumbered
notes and definitions that no passage references. Each definition has an index,
nullable number, exact number evidence and a typed text note. A passage's
`footnote_references` retains each explicit number and its own evidence. Its
`definition_indexes` refer only to that same source account. `resolution` is
`matched`, `ambiguous` or `missing`; duplicate definitions remain ambiguous even
when their text agrees. If the older proposal did not capture definitions, the
resolution is `not_captured`. Unnumbered HTML notes do not acquire invented numeric
links to passages.

`prerecorded_playback` and `segue` preserve observed booleans, including false.
They are nullable: absence does not assert false. Prerecorded playback is a
passage assertion; there is no inferred account-wide tape flag.
`transition_marker` retains the provider's marker without converting it to an
accepted transition classification.

`performers` distinguishes `reported_performer`, `cover_performer` and
`guest_performer`. Each observed name and provider-qualified identity has its own
exact evidence. These claims do not identify an author, composer or accepted
person. Unsupported relationships remain explicit. Set-level notes are exposed
only when the provider supplied a supported field. A null set `notes` value means
absent or uncaptured; an explicit empty list means a supported field was empty.
Set-level performer claims are not inferred from account or passage claims.

Evidence preserves the input UUID, JSON pointer, Unicode span and original quote.
Its `evidence_status` is `current`, `changed` or `missing`, based on that input
resource's retained head in the same database snapshot. A changed resource changes
this status without replacing accepted annotations or their citations.

## Bounds and compatibility

The read uses one primary repeatable-read transaction. SQL selects only the typed
annotation fields and the requested correspondence keys. It never returns a full
`Sources.Account`, raw retained document, unrelated venue metadata or the complete
structure evidence list.

One public group is limited to 16 KiB of UTF-8 JSON, including source metadata,
origin, citations and correspondence targets. SQL first caps retained detail before
transferring it. The reader then bounds repeated footnote-reference expansion and
measures the final typed group with the HTTP serializer, including JSON escaping.
One group can expose at most 128 accepted correspondence targets.
If any bound is exceeded, the group remains in the page with
`detail_status: size_limit` and its `target_count`. Detail and target arrays are
null, rather than truncated. The source tuple still identifies the omitted group;
operators can inspect its retained source through the existing internal evidence
contracts. Other groups remain readable. These per-group bounds and the page limit
bound public response work without silently losing a passage or note.

The decoder now preserves explicit footnote definitions and supported set notes.
It marks source markup separately and derives display text from the original HTML
nodes or attributes. Decoder version `provider-observations-v3` and setlist
extractor version `setlist-extraction-v2` change the processing fingerprint.
Existing immutable proposal batches and terminal replays retain their old values.
Create new compatible proposals before accepting a new structure from them; do not
reinterpret a historical proposal as if it captured these additional fields.
Existing legacy playback routes and accepted set/occurrence UUIDs do not change.

Verification covers provider decoding and evidence validation separately from the
public read. The public service and HTTP cases exercise duplicate definitions,
missing references, unresolved correspondence, explicit false, original citations
after provider changes, legacy and unknown text formats, repeated footnote-reference
expansion, Unicode escaping, revision-pinned cursors, known-empty editions,
retraction and disabled reads.
