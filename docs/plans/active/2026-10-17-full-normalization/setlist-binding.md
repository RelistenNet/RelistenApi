# Associate retained setlist accounts with the catalog

A provider's setlist is an account of a performance. Its ordered passages, set
boundaries and notes remain evidence even when Relisten has no matching imported
setlist or playable show. Binding associates that account with current catalog
rows; it does not accept song identities, composition links or a preferred sequence.

`NormalizationCatalogBindingService.BindSetlistAsync(input, account,
expectedArtistUuid?, cancellationToken)` returns three independent results:

- `Artist`: the existing catalog artist supported by this lookup.
- `ImportedSetlist`: an existing legacy `setlist_shows` row and its current venue
  reference. This does not equate the provider's venue wording with a physical venue.
- `Performance`: an existing show association, with its basis and any supporting
  source UUIDs. It creates no performance and moves no recording.

Each result is `Bound`, `Missing`, `Ambiguous` or `ProviderConflict`, with a reason.
Only `Bound` supplies a reference. Missing imported metadata or playback context
does not discard the original account or its passages.

## Read and publication contract

The binder reloads immutable retained input and requires the supplied bytes,
representation and account to reproduce exactly. It reads catalog identities and
their revisions on the primary in one read-only repeatable-read transaction,
outside any ambient catalog transaction. Historical retained input may be inspected;
the binder does not claim that its revision is still current.

The snapshot returns its actual input resource, account origin, catalog artist
revisions and mapping-registry revision. Page members keep the page's input key;
the nested account identity is not a substitute for that input. Supply every
returned `Inputs` entry as an exact input-revision expectation at work admission,
and include `CatalogInputs` and `MappingRegistry` in the publication read set.
Admission rejects replaced current input or changed catalog revisions. Publication
rechecks them before a domain callback can write accepted data.

Binding a historical page member does not establish the latest edition of an
account that can appear on other pages. A future provider coordinator must retain
that distinction when selecting or comparing editions. A dependency on the
binding owner's published head alone cannot replace the original read set.

## Artist and performance qualification

JerryGarcia.com, Setlist.fm and Phantasy Tour resolve provider-qualified artist
mappings before applying an expected artist. A supplied artist must not hide a
second matching mapping elsewhere. All inspected artist owners and the registry
join the read set, including unresolved lookups.

Jerry's retained mirror key contains a configured prefix and a show filename
identity. The importer stores the filename identity as the imported setlist key.
The binder reads that row's actual UUID. It reads a performance only through the
existing `setlist_show_assignments` row. Unknown dates and separate early/late
performances do not change this rule. Jerry's importer supplies setlists, venues
and tours, while its recordings come through other providers; no recording/file
association is invented from Jerry's page.

Setlist.fm qualifies an artist by MusicBrainz ID and its configured provider mapping.
Migration 28 extends mapping-registry invalidation to artist insertion, deletion,
UUID replacement and MusicBrainz-ID changes. Names and timestamp-only updates do
not alter registry membership. This catches a previously missing or unique
MusicBrainz lookup changing under another artist.

Phish.net requires an explicit expected artist. Its lookup uses recorded provider
show-ID or permalink evidence within that artist's source/performance assignments.
`ArtistScopeUuid` records this restriction: the result makes no claim of global
account uniqueness across artists. Existing Phish.net mappings can have no
provider artist ID; a null mapping identifier cannot be converted into one.

No provider uses title, slug, date or passage position to invent a performance or
legacy-song association. Jerry's writer drops song-link IDs, and legacy song/show
memberships cannot express repeats or order. Matching and occurrence publication
must use retained passage evidence rather than reconstructing an ordered setlist
from those memberships. Tour facts also remain outside this binder because its
catalog revisions do not cover tour metadata.

## Verification and operations

Build the solution, then run the setlist, Jerry and mapping-registry contract tests
through `./test-local.sh`. Verify global duplicate mappings before artist filtering,
missing catalog rows, exact early/late assignments, foreign ownership, input
replacement, mapping corrections, stale publication and repeated passages. Include
the importer-to-retention-to-decoder-to-binder boundary, while keeping synthetic
fixtures distinct from independently labeled real evidence.

Install migration 28 before publishing from these mappings. Stop catalog writers
and normalization publishers while installing its trigger changes. Application
rollback retains migration 28, mapping generations, inputs and accepted data;
its reverse migration is intentionally unsupported. Installation advances the
registry once so tickets captured before the expanded trigger coverage cannot
publish as current.

Decoder version `provider-observations-v2` accepts the writer's numeric Phantasy
Tour identity forms without changing their retained wording. This changes the
recording processor's version as well. Finish unfinished old-version pages using
the earlier code, or inspect their retained progress and select new page/attempt
UUIDs with the new version. Do not reuse an old UUID to silently reinterpret a
saved preparation. Published batches remain available for audit.

Registration starts no
processing and enables no normalized reads. Durable unresolved-lookup enrollment,
complete-input provider coordinators and evaluated automatic matching remain
requirements of the [main plan](README.md).
