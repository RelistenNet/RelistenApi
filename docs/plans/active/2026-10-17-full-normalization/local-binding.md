# Bind Local root and directory evidence together

A Local root map says which directories belong to its catalog. A directory
response contains files and notes. Bind both retained inputs together before
using them for normalization; the directory alone cannot establish its artist
or its place in that root.

`NormalizationCatalogBindingService.BindLocalDirectoryAsync(rootInput,
directoryInput, expectedArtistUuid, cancellationToken)` returns the existing
recording binding snapshot. The directory remains its primary resource. The root
appears in `SupportingInputs`, and `Inputs` supplies the complete revision list.
The service reads current associations and catalog clocks in one read-only
repeatable-read transaction on the primary database.

## Establish ownership without guessing a show

The root and directory must have matching tuple identities and exactly one root
account for that directory. Their keys use the original Local capture encoding,
including Unicode and punctuation. Missing or duplicate accounts produce explicit
unresolved outcomes.

Artist ownership comes from the configured Local root mapping. The lookup checks
all distinct mapped artists before applying an expected artist. Two artists
sharing the root are ambiguous even when the caller expected one of them.

Recording ownership comes from exact writer URLs:
`https://audio.relisten.net/{root}/{directory}/{mp3}`. A usable file must have
matching current recording, artist and set ownership and must not be orphaned.
Valid anchors must agree on one recording. No usable anchor remains unresolved;
anchors in several recordings are ambiguous. Titles, dates, UUID formulas and
Local's review links cannot substitute for missing proof. The importer marks
those links `for_source=false`.

Individual invalid files stay unresolved alongside valid siblings. A positive
recording association still permits its source-level credits. Multiple directories
can bind to one recording; contributor-owned inputs keep their evidence together.
Each recording belongs to one performance. Its files can contain several songs
or sets within that performance.

## Preserve both kinds of lookup dependency

The result includes clocks for every known root artist, including unresolved
results. It also includes a `MappingRegistry` dependency. That singleton clock
detects a new artist acquiring the root even when that artist has no recordings
and the original artist's clock did not change. Generation zero is valid. Missing
mapping results can carry the registry dependency before any artist is known.

Migration 25 updates the registry transactionally for meaningful provider and
mapping changes. It uses one global counter because mappings change infrequently;
an unrelated mapping edit can conservatively refresh Local work. This avoids a
separate absence protocol for each root and provider rename.

Pass `Inputs` as `expectedInputRevisions` during work admission, together with
`CatalogInputs`, `SourceIdentities` and `MappingRegistry`. A root revision change
must reject admission even when the directory bytes are unchanged and a credit
cites only the directory. Publication then checks changes after admission.

Contributed work owns the registry dependency just as it owns its resource keys.
Removing one contributor keeps that dependency while another still uses it.
The returned ticket covers the complete owner union and must be consumed in full.

The lock order is artist clocks, source-identifier clocks, mapping registry,
retained resources, then work owners. Publication callbacks make normalization
writes only. Bulk provider or mapping maintenance must prelock affected clocks
consistently or retry its whole transaction after a PostgreSQL deadlock.
Truncation remains offline maintenance. Retain clocks and contribution history
when rolling back reader or worker enablement.

## What this enables

The service can supply positive associations and retry dependencies to a producer.
It does not register or schedule that producer, allocate identities, accept a
match or withdraw playback. The [Local coordinator](local-processing.md) retains lookup outcomes and consumes
the complete extraction ticket. Automatic matching must still apply an evaluated
policy and preserve reviewed decisions. See [catalog refresh](catalog-refresh.md) for current legacy wording
and [catalog binding](catalog-binding.md) for shared publication contracts.
