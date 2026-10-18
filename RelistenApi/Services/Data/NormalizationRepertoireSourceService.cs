using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace Relisten.Data;

/// <summary>Authenticates complete source accounts before any repertoire candidate decision.</summary>
public sealed partial class NormalizationRepertoireSourceService(
    DbService db,
    NormalizationEvidenceService evidence,
    NormalizationProviderDecoder decoder,
    NormalizationImportedCatalogService imported,
    NormalizationCatalogBindingService bindings,
    NormalizationRecordingProposalService recordingProposals,
    NormalizationSetlistProposalService setlistProposals
)
{
    public async Task<RepertoireResolutionSourceSnapshot> ReadAsync(
        RepertoireResolutionRequest request,
        CancellationToken ct = default
    )
    {
        if (request.ExpectedArtistUuid == Guid.Empty)
        {
            throw new ArgumentException("An expected artist must be a nonempty constraint.");
        }
        var result = request.Source switch
        {
            ImportedRepertoireResolutionSource source => await ReadImported(source, ct),
            ProviderRepertoireResolutionSource source => await ReadProvider(source, ct),
            RecordingRepertoireResolutionSource source => await ReadRecording(source, ct),
            SetlistRepertoireResolutionSource source => await ReadSetlist(source, ct),
            _ => throw new ArgumentException(
                "A supported typed repertoire source reference is required."
            ),
        };
        if (
            request.ExpectedArtistUuid is { } expected
            && result.ArtistUuid is { } actual
            && expected != actual
        )
        {
            result = result with
            {
                ArtistUuid = null,
                Issues = result.Issues.Append("expected_artist_conflict").ToArray(),
            };
        }
        if (result.Claims.Count > NormalizationImportedCatalogService.MaximumRepertoireSongs)
        {
            throw new InvalidOperationException(
                "The complete repertoire source exceeds its 50,000 claim limit."
            );
        }
        return result;
    }

    public async Task<RepertoireResolutionRequest> RefreshRequestAsync(
        RepertoireResolutionRegistration registration,
        RepertoireResolutionRequest previous,
        CancellationToken ct = default
    )
    {
        RepertoireResolutionSource source;
        switch (previous.Source)
        {
            case ImportedRepertoireResolutionSource original:
                if (
                    registration.Lineage
                        is not { Provider: "legacy_catalog", ResourceType: "artist_repertoire" }
                    || !Guid.TryParse(registration.Lineage.ProviderKey, out var artist)
                )
                {
                    throw new NormalizationConflictException(
                        "The imported registration has no authenticated artist dictionary lineage."
                    );
                }
                var originalCapture =
                    await ReadImportedCapture(original.FetchUuid, ct)
                    ?? throw new NormalizationConflictException(
                        "The original imported dictionary capture is missing."
                    );
                if (originalCapture.Revision.Resource != registration.Lineage)
                {
                    throw new NormalizationConflictException(
                        "The imported retry capture belongs to another lineage."
                    );
                }
                var currentFetch = await ReadCurrentImportedFetch(registration.Lineage, ct);
                if (currentFetch is { } fetch)
                {
                    try
                    {
                        // Reuse the current producer receipt, including its generation. Two
                        // registrations must not invalidate each other with equal-byte refreshes.
                        var reusable = new ImportedRepertoireResolutionSource(fetch);
                        await ReadImported(reusable, ct);
                        source = reusable;
                        break;
                    }
                    catch (NormalizationConflictException)
                    {
                        // The catalog projection changed even though its retained head did not.
                    }
                }
                var refreshed =
                    await imported.RefreshArtistRepertoireAsync(artist, ct)
                    ?? throw new NormalizationConflictException(
                        "The imported dictionary artist is missing."
                    );
                source = new ImportedRepertoireResolutionSource(refreshed.Capture.Fetch.FetchUuid);
                break;
            case ProviderRepertoireResolutionSource provider:
                source = provider with
                {
                    Input = (
                        await evidence.GetCurrentInputReferencesAsync([provider.Input.Resource], ct)
                    ).Single(),
                };
                break;
            case RecordingRepertoireResolutionSource:
                if (!Guid.TryParse(registration.Lineage.ProviderKey, out var recordingUuid))
                {
                    throw new NormalizationConflictException("The recording lineage is invalid.");
                }
                var recording =
                    await recordingProposals.GetAsync(recordingUuid, ct)
                    ?? throw new NormalizationConflictException(
                        "The recording has no current published extraction."
                    );
                source = new RecordingRepertoireResolutionSource(recording.AttemptUuid);
                break;
            case SetlistRepertoireResolutionSource:
                if (!Guid.TryParse(registration.Lineage.ProviderKey, out var setlistUuid))
                {
                    throw new NormalizationConflictException("The setlist lineage is invalid.");
                }
                var setlist =
                    await setlistProposals.GetAsync(setlistUuid, ct)
                    ?? throw new NormalizationConflictException(
                        "The account has no current published extraction."
                    );
                source = new SetlistRepertoireResolutionSource(setlist.AttemptUuid);
                break;
            default:
                throw new ArgumentException("Unknown repertoire source kind.");
        }
        return new(source, registration.ExpectedArtistUuid);
    }

    private async Task<RepertoireResolutionSourceSnapshot> ReadImported(
        ImportedRepertoireResolutionSource source,
        CancellationToken ct
    )
    {
        if (source.FetchUuid == Guid.Empty)
        {
            throw new ArgumentException("An exact imported dictionary fetch is required.");
        }
        var capture =
            await ReadImportedCapture(source.FetchUuid, ct)
            ?? throw new ArgumentException("Unknown imported dictionary fetch.");
        var input = capture.Revision;
        if (
            input.Resource is not { Provider: "legacy_catalog", ResourceType: "artist_repertoire" }
            || input.CaptureVersion != NormalizationImportedCatalogService.RepertoireCaptureVersion
            || !capture.Fetch.AcceptedAsCurrent
        )
        {
            throw new ArgumentException("The fetch is not an admitted imported artist dictionary.");
        }
        using var document = NormalizationRetainedDocument.Open(input, ct);
        var clocks = document
            .Resolve("/catalog_inputs")
            .EnumerateArray()
            .Select(value => new NormalizationCatalogDependency(
                value.GetProperty("artist_uuid").GetGuid(),
                value.GetProperty("generation").GetInt64()
            ))
            .ToArray();
        var bound = await bindings.BindRefreshedRepertoireAsync(new(capture, clocks), ct);
        var metadata = (await ReadInputMetadata([input.Uuid], ct)).Single();
        var claims = bound
            .Songs.Select(song =>
                DictionaryClaim(
                    song.Observation,
                    "imported:" + song.Binding.Value!.ImportedSongUuid.ToString("D"),
                    song.Binding.Value.ImportedSongUuid
                )
            )
            .ToArray();
        return new(
            input.Resource,
            bound.Artist.Value?.ArtistUuid,
            claims,
            new(
                [new(metadata.ResourceUuid, input.Uuid, capture.Fetch.ResourceGeneration)],
                [],
                clocks,
                [],
                null,
                [],
                [],
                [],
                []
            ),
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            []
        );
    }

    private async Task<RepertoireResolutionSourceSnapshot> ReadProvider(
        ProviderRepertoireResolutionSource source,
        CancellationToken ct
    )
    {
        var retained = (
            await evidence.GetInputsAsync(
                [source.Input],
                new(1, 32 * 1024 * 1024, 32 * 1024 * 1024),
                ct
            )
        ).Single();
        await using var c = db.CreateConnection(false, false);
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: ct
            )
        );
        var input = await c.QuerySingleAsync<SourceInputMetadata>(
            new CommandDefinition(
                """
                SELECT r.uuid AS "RevisionUuid",r.resource_uuid AS "ResourceUuid",p.provider AS "Provider",
                    p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey",p.current_revision_uuid AS "CurrentRevisionUuid",
                    p.generation AS "Generation"
                FROM normalization.input_revisions r JOIN normalization.input_resources p ON p.uuid=r.resource_uuid WHERE r.uuid=@uuid
                """,
                new { uuid = retained.Uuid },
                t,
                cancellationToken: ct
            )
        );
        if (input.CurrentRevisionUuid != retained.Uuid)
        {
            throw new NormalizationConflictException(
                "The provider dictionary is not its exact current retained input."
            );
        }
        var dependency = new NormalizationInputDependency(
            input.ResourceUuid,
            retained.Uuid,
            input.Generation
        );
        if (
            retained.Resource != new NormalizationResourceKey("phish.in", "catalog", "songs")
            || retained.CaptureVersion != "raw-http:1"
            || source.AccountIdentity != retained.Resource
        )
        {
            await t.CommitAsync(ct);
            return new(
                retained.Resource,
                null,
                [],
                new([dependency], [], [], [], null, [], [], [], []),
                RepertoireResolutionPolicyEvaluator.SourceVersion,
                ["source_profile_unsupported"]
            );
        }
        var registry = await c.QuerySingleAsync<NormalizationMappingRegistryDependency>(
            new CommandDefinition(
                "SELECT generation AS \"Generation\" FROM normalization.catalog_mapping_registry_revision WHERE singleton",
                transaction: t,
                cancellationToken: ct
            )
        );
        var artists = (
            await c.QueryAsync<ProviderArtist>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT a.uuid AS "ArtistUuid",r.generation AS "Generation"
                    FROM artists a JOIN artists_upstream_sources m ON m.artist_id=a.id
                    JOIN upstream_sources p ON p.id=m.upstream_source_id
                    LEFT JOIN normalization.catalog_artist_revisions r ON r.artist_uuid=a.uuid
                    WHERE p.name='phish.in' ORDER BY a.uuid LIMIT 1001
                    """,
                    transaction: t,
                    cancellationToken: ct
                )
            )
        ).ToArray();
        var issues = new List<string>();
        if (artists.Length > 1000)
        {
            issues.Add("provider_artist_mapping_limit");
        }
        if (artists.Length == 0)
        {
            issues.Add("provider_artist_missing");
        }
        if (artists.Length > 1)
        {
            issues.Add("provider_artist_ambiguous");
        }
        if (artists.Any(artist => artist.Generation == null))
        {
            issues.Add("provider_artist_clock_missing");
        }
        var decoded = decoder.Decode(retained, ct);
        if (decoded.Issues.Count > 0)
        {
            issues.Add("provider_dictionary_decode_issues");
        }
        var completeDictionary = RetainProviderDictionaryMembers(retained, decoded.Repertoire, ct);
        var duplicates = completeDictionary
            .Where(song => song.Identities.Count > 0)
            .GroupBy(song => song.Origin.Item)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        if (duplicates.Count > 0)
        {
            issues.Add("duplicate_provider_identity");
        }
        var malformedMembers = new HashSet<string>(StringComparer.Ordinal);
        var malformedAccount = false;
        foreach (
            var issue in decoded.Issues.Where(issue =>
                issue.Code is not ("missing_title" or "duplicate_provider_identity")
            )
        )
        {
            var pointer = issue.Evidence.JsonPointer;
            if (pointer.StartsWith("/data/", StringComparison.Ordinal))
            {
                var end = pointer.IndexOf('/', "/data/".Length);
                malformedMembers.Add(end < 0 ? pointer : pointer[..end]);
            }
            else
            {
                malformedAccount = true;
            }
        }
        var occurrence = new Dictionary<NormalizationResourceKey, int>();
        var claims = completeDictionary
            .Select(song =>
            {
                var key =
                    song.Identities.Count == 0
                        ? "provider_observation:"
                            + retained.Uuid.ToString("D")
                            + song.Origin.Evidence.JsonPointer
                        : "provider:" + JsonSerializer.Serialize(song.Origin.Item);
                if (duplicates.Contains(song.Origin.Item))
                {
                    occurrence.TryGetValue(song.Origin.Item, out var index);
                    occurrence[song.Origin.Item] = index + 1;
                    key += ":duplicate:" + index;
                }
                var claim = DictionaryClaim(song, key);
                var pointer = song.Origin.Evidence.JsonPointer;
                var malformed =
                    song.Identities.Count == 0
                    || malformedAccount
                    || malformedMembers.Contains(pointer);
                return malformed ? claim with { Kind = "unknown" } : claim;
            })
            .ToArray();
        var clocks = artists
            .Where(artist => artist.Generation.HasValue)
            .Select(artist => new NormalizationCatalogDependency(
                artist.ArtistUuid,
                artist.Generation!.Value
            ))
            .ToArray();
        await t.CommitAsync(ct);
        return new(
            retained.Resource,
            artists.Length == 1 && artists[0].Generation.HasValue ? artists[0].ArtistUuid : null,
            claims,
            new([dependency], [], clocks, [], registry, [], [], [], []),
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            issues
        );
    }

    private Task<Guid?> ReadCurrentImportedFetch(
        NormalizationResourceKey resource,
        CancellationToken ct
    ) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    """
                    SELECT f.fetch_uuid FROM normalization.input_resources p
                    JOIN normalization.input_fetches f ON f.resource_uuid=p.uuid AND f.input_revision_uuid=p.current_revision_uuid
                        AND f.resource_generation=p.generation AND f.accepted_as_current
                    WHERE p.provider=@Provider AND p.resource_type=@ResourceType AND p.provider_key=@ProviderKey
                    ORDER BY f.sequence DESC LIMIT 1
                    """,
                    resource,
                    cancellationToken: ct
                )
            )
        );

    private async Task<NormalizationCaptureResult?> ReadImportedCapture(
        Guid fetchUuid,
        CancellationToken ct
    )
    {
        // Fetch/revision bytes are immutable. Check their size before GetCaptureAsync loads
        // them; large dictionaries are complete source accounts, not working decision pages.
        var bytes = await db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<long?>(
                new CommandDefinition(
                    """
                    SELECT octet_length(r.payload)::bigint FROM normalization.input_fetches f
                    JOIN normalization.input_revisions r ON r.uuid=f.input_revision_uuid WHERE f.fetch_uuid=@fetchUuid
                    """,
                    new { fetchUuid },
                    cancellationToken: ct
                )
            )
        );
        if (bytes > 128 * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "The complete imported dictionary exceeds its retained byte limit."
            );
        }
        return bytes == null ? null : await evidence.GetCaptureAsync(fetchUuid, ct);
    }

    private static ProviderRepertoireObservation[] RetainProviderDictionaryMembers(
        NormalizationInputRevision retained,
        IReadOnlyList<ProviderRepertoireObservation> decoded,
        CancellationToken ct
    )
    {
        using var document = NormalizationRetainedDocument.Open(retained, ct);
        var root = document.Resolve("");
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
        )
        {
            return decoded.ToArray();
        }
        if (data.GetArrayLength() > NormalizationImportedCatalogService.MaximumRepertoireSongs)
        {
            throw new InvalidOperationException(
                "The complete provider dictionary exceeds its 50,000 member limit."
            );
        }
        var byPointer = decoded.ToDictionary(song => song.Origin.Evidence.JsonPointer);
        return data.EnumerateArray()
            .Select(
                (member, index) =>
                {
                    var pointer = "/data/" + index;
                    if (byPointer.TryGetValue(pointer, out var song))
                    {
                        return song;
                    }
                    var citation = new NormalizedEvidenceReference(retained.Uuid, pointer);
                    var identity =
                        member.ValueKind == JsonValueKind.Object
                        && member.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.Number
                        && id.TryGetInt32(out var number)
                        && number > 0
                            ? new ObservedProviderIdentity(
                                new(
                                    "phish.in",
                                    "song",
                                    number.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture
                                    )
                                ),
                                new(retained.Uuid, pointer + "/id")
                            )
                            : null;
                    var title =
                        member.ValueKind == JsonValueKind.Object
                        && member.TryGetProperty("title", out var text)
                        && text.ValueKind == JsonValueKind.String
                            ? new ObservedValue<string>(
                                text.GetString()!,
                                new(retained.Uuid, pointer + "/title")
                            )
                            : null;
                    // Malformed members remain visible with their exact input coordinate. A resource
                    // coordinate identifies their evidence and supplies no qualified song identity.
                    return new ProviderRepertoireObservation
                    {
                        Origin = new(
                            identity?.Identity ?? retained.Resource,
                            citation,
                            ProviderObservationScope.CatalogMember
                        ),
                        Title = title,
                        Identities = identity == null ? [] : [identity],
                    };
                }
            )
            .ToArray();
    }

    private static RepertoireResolutionClaim DictionaryClaim(
        ProviderRepertoireObservation song,
        string correspondence,
        Guid? importedUuid = null
    ) =>
        new(
            ClaimUuid(correspondence, song),
            correspondence,
            song.Title,
            "dictionary",
            song.Origin,
            song.Identities,
            song.Aliases,
            song.AliasOf,
            importedUuid
        );

    private static Guid ClaimUuid(string key, object value) =>
        new(
            SHA256
                .HashData(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            version = RepertoireResolutionPolicyEvaluator.SourceVersion,
                            key,
                            value,
                        }
                    )
                )
                .AsSpan(0, 16)
        );

    private Task<SourceInputMetadata[]> ReadInputMetadata(Guid[] ids, CancellationToken ct) =>
        db.WithWriteConnection(async c =>
            (
                await c.QueryAsync<SourceInputMetadata>(
                    new CommandDefinition(
                        """
                        SELECT r.uuid AS "RevisionUuid",r.resource_uuid AS "ResourceUuid",p.provider AS "Provider",
                            p.resource_type AS "ResourceType",p.provider_key AS "ProviderKey",p.current_revision_uuid AS "CurrentRevisionUuid",p.generation AS "Generation"
                        FROM normalization.input_revisions r JOIN normalization.input_resources p ON p.uuid=r.resource_uuid
                        WHERE r.uuid=ANY(@ids) ORDER BY r.uuid
                        """,
                        new { ids },
                        cancellationToken: ct
                    )
                )
            ).ToArray()
        );

    private sealed record SourceInputMetadata(
        Guid RevisionUuid,
        Guid ResourceUuid,
        string Provider,
        string ResourceType,
        string ProviderKey,
        Guid? CurrentRevisionUuid,
        long Generation
    )
    {
        public NormalizationResourceKey Resource => new(Provider, ResourceType, ProviderKey);
    }

    private sealed record ProviderArtist(Guid ArtistUuid, long? Generation);
}
