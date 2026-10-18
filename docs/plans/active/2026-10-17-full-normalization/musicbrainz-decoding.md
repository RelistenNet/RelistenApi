# MusicBrainz lookup observations

`NormalizationProviderDecoder.Decode` can interpret retained MusicBrainz work, recording and artist lookups. It emits cited external assertions. It performs no HTTP requests, catalog binding, canonical identity allocation or acceptance. The [development pilot](../../../research/2026-10-17-full-normalization/composition-enrichment-pilot.md) records where the real fixtures came from and what they establish.

## Supported representations

The resource provider is `musicbrainz.org`. Its key must be a nonempty lowercase MBID in UUID D format. Each profile has one fixed request shape:

| Resource type | Capture version | JSON lookup profile |
| --- | --- | --- |
| `work` | `musicbrainz-work-json-v1` | Work lookup with `inc=artist-rels aliases` |
| `recording` | `musicbrainz-recording-work-json-v1` | Recording lookup with `inc=work-rels artist-credits` |
| `artist` | `musicbrainz-artist-json-v1` | Artist lookup with `inc=aliases` |

These names identify the representation the caller retained. The decoder cannot recover HTTP parameters from JSON. A future adapter must validate and record the actual request profile before using these capture versions. Changing a profile requires an explicit compatibility decision or a new capture version.

The requested resource MBID must agree with the root `id`. An invalid resource key throws. A nonobject root, provider `error`, missing or malformed root ID, or root identity mismatch produces cited issues and no MusicBrainz observations. Unsupported resource/capture combinations, including search, throw `NotSupportedException`. A retained 503 error body cannot become artist absence; the decoder does not itself know HTTP status.

The decoder version is `provider-observations-v4`. Existing processing fingerprints include that version. Historical terminal attempts remain replayable through their existing attempt contracts; new interpretation uses the current fingerprint.

## Typed facts and citations

The optional `ProviderDecodeResult.MusicBrainzWorks`, `.MusicBrainzRecordings` and `.MusicBrainzArtists` members are omitted from JSON when absent. Existing result families remain separate. A decoded MusicBrainz recording does not populate Relisten recording/file observations or repertoire links.

A work observation retains its qualified work identity, title, type, disambiguation, aliases, ISWCs, registry attributes and relationships.

Each ISWC retains its exact `Original` spelling and a separate qualified comparison identity `("iswc", "work", "T<ten digits>")`. The decoder accepts compact `T0709396246`, grouped `T-070.939.624-6`, and ungrouped display `T-070939624-6`; other layouts remain unqualified with a cited issue. It requires a nonzero nine-digit work number and the ISO 15707 Annex B check digit. The [ISWC description](https://www.iswc.org/iswc) distinguishes display separators from identity, and the [CISAC specification](https://www.iswc.org/sites/iswc/files/files/2024-08/Specification%20-%203rd%20Party%20access.pdf) requires the compact form and a valid check digit. Passing those checks does not establish registry issuance or the correct work.

Registry-specific attributes retain their MusicBrainz attribute type and value; they do not become guessed provider IDs. Each work's `CompleteAuthorship` is null: none of these lookup profiles asserts an exhaustive author list.

Relationships preserve the original type wording and type MBID, direction, target type, source/target credit wording, begin/end values, ended flag, attribute strings, attribute IDs and attribute values. Writer, composer, lyricist, arranger, performance and unfamiliar types remain distinct source claims. The decoder does not infer music-versus-lyrics roles from a generic writer relationship. Artist, work and recording targets have typed external references; other target kinds retain their original target-type wording and exact relationship-object citation, with no invented target identity.

External artist references preserve the provider's type, including `Person`, `Group` or other wording. MusicBrainz's `artist` namespace is separate from Relisten's catalog artists and normalized people. Resolving either a person or group to a local identity requires its own supported identity decision. Recording artist credits preserve credited names, join phrases and artist references; they do not assert work authorship.

Each scalar and identity has a `NormalizedEvidenceReference` into the supplied immutable input revision. Alias, registry attribute, artist-credit, entity and relationship objects also retain object citations. These locators preserve access to unrecognized fields without copying arbitrary provider JSON into downstream proposals. Citations use JSON Pointer escaping for attribute keys. Returned lists preserve provider order and duplicate claims.

A null collection means the field was absent or invalid; malformed shapes also produce issues. An empty collection with no relevant issues means the response supplied an array/object with no entries. Malformed members are omitted with exact cited issues; valid neighboring members remain available, so an empty projection with issues can also mean that no members were valid. Neither a missing relationship list nor an explicitly empty one establishes that a work has no authors. Partial valid fields remain available alongside issues. Evidence and freshness remain separate: decoding does not establish that this retained revision is the current provider head.

## Verification and remaining integration

The focused fixture `TestNormalizationProviderDecoderMusicBrainz` uses exact development response bodies with a compact URL/time/status/hash manifest. It checks external recording-to-work relationships, distinct author roles, person/group references, same-title unresolved candidates, unknown relationship details, missing versus empty lists, errors, malformed fields, resource/profile rejection and exact nested citations. The retained failure and same-title examples are source observations, not adjudicated matching labels.

The network adapter, durable HTTP receipts and runtime search decoder remain required work. The adapter must retain exact response bytes through the existing generation CAS outside downstream transactions, coordinate rate limits across the egress IP, and distinguish failed requests from successful entity observations. Search needs its own exact query/page resource contract and must expose incomplete pages and external freshness limits.

A resolver must separately establish any connection between a Relisten claim and an external recording/work, consume the complete original read set, and compare all retrieved candidates. These decoders supply no catalog bridge, complete-author assertion, automatic policy or measured accuracy. Independent development labels and adjudicated evaluation are required before activating automatic acceptance.
