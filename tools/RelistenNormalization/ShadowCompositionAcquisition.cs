using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed record ShadowCompositionSeed(
    Guid ArtistUuid,
    string ArtistName,
    Guid? ArtistMusicBrainzId,
    Guid CatalogUuid,
    string Title,
    string SourcePointer
);

/// <summary>Both requests name external assertions; neither assigns a catalog composition.</summary>
public sealed record ShadowCompositionAcquisitionCommand(
    Guid CommandUuid,
    NormalizationResourceKey Resource,
    MusicBrainzSearchPageRequest? Search,
    MusicBrainzLookupRequest? Lookup,
    IReadOnlyList<Guid> CatalogUuids,
    string Reason
);

public sealed record ShadowCompositionAcquisitionPlan(
    IReadOnlyList<ShadowCompositionAcquisitionCommand> Commands,
    IReadOnlyList<string> Issues,
    bool ExternalUniverseComplete = false
);

/// <summary>
/// Deduplicates exact external resources across all repertoire claims. Search rank affects
/// transport order only. Every decoded alternative is hydrated, subject to the caller's run
/// bounds; a missing page, malformed member or exhausted bound never establishes absence.
/// </summary>
public static class ShadowCompositionAcquisition
{
    public const string Version = "shadow-composition-acquisition-v1";
    private static readonly TimeSpan CacheAge = TimeSpan.FromDays(7);

    public static IReadOnlyList<ShadowCompositionSeed> ReadCatalogSeeds(
        IEnumerable<JsonElement> records
    )
    {
        var artists = new Dictionary<long, JsonElement>();
        var songs = new Dictionary<Guid, JsonElement>();
        foreach (var record in records)
        {
            if (
                !record.TryGetProperty("kind", out var kind)
                || !record.TryGetProperty("row", out var row)
            )
            {
                continue;
            }
            if (kind.GetString() == "artist")
            {
                artists[row.GetProperty("id").GetInt64()] = row.Clone();
            }
            else if (kind.GetString() == "song_sample")
            {
                songs[row.GetProperty("uuid").GetGuid()] = row.Clone();
            }
            else if (kind.GetString() == "setlist_sample")
            {
                foreach (var member in record.GetProperty("unordered_memberships").EnumerateArray())
                {
                    if (
                        member.TryGetProperty("song", out var song)
                        && song.ValueKind == JsonValueKind.Object
                    )
                    {
                        songs[song.GetProperty("uuid").GetGuid()] = song.Clone();
                    }
                }
            }
        }

        return songs
            .Values.Where(song => artists.ContainsKey(song.GetProperty("artist_id").GetInt64()))
            .Select(song =>
            {
                var artist = artists[song.GetProperty("artist_id").GetInt64()];
                var mbid =
                    artist.TryGetProperty("musicbrainz_id", out var value)
                    && Guid.TryParse(value.GetString(), out var parsed)
                    && parsed != Guid.Empty
                        ? parsed
                        : (Guid?)null;
                return new ShadowCompositionSeed(
                    artist.GetProperty("uuid").GetGuid(),
                    artist.GetProperty("name").GetString()!,
                    mbid,
                    song.GetProperty("uuid").GetGuid(),
                    song.GetProperty("name").GetString() ?? "",
                    "/row/name"
                );
            })
            .Where(seed => !string.IsNullOrWhiteSpace(seed.Title))
            .OrderBy(seed => seed.ArtistUuid)
            .ThenBy(seed => seed.Title, StringComparer.Ordinal)
            .ThenBy(seed => seed.CatalogUuid)
            .ToArray();
    }

    public static ShadowCompositionAcquisitionPlan Plan(
        IReadOnlyList<ShadowCompositionSeed> seeds,
        IReadOnlyList<NormalizationInputRevision> retainedInputs,
        IReadOnlyList<ShadowCompositionAcquisitionCommand>? originalCommands = null
    )
    {
        var commands =
            new Dictionary<NormalizationResourceKey, ShadowCompositionAcquisitionCommand>();
        var originals = (originalCommands ?? []).ToDictionary(command => command.Resource);
        var issues = new List<string>();
        var retained = retainedInputs
            .GroupBy(input => input.Resource)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var decoded = new Dictionary<Guid, ProviderDecodeResult>();
        foreach (
            var input in retainedInputs.Where(input => input.Resource.Provider == "musicbrainz.org")
        )
        {
            VerifyInput(input);
            var facts = new NormalizationProviderDecoder().Decode(input);
            decoded[input.Uuid] = facts;
            issues.AddRange(
                facts.Issues.Select(issue =>
                    $"Decode:{input.Uuid:D}:{issue.Code}:{issue.Evidence.JsonPointer}"
                )
            );
        }

        // Finish already retained recording paths first. Writer targets remain qualified MB
        // artists, without allocating or requiring canonical Relisten people.
        foreach (var facts in decoded.Values)
        {
            foreach (var recording in facts.MusicBrainzRecordings ?? [])
            {
                foreach (var relation in recording.Relationships ?? [])
                {
                    if (
                        relation.Type?.Value == "performance"
                        && relation.Direction?.Value == "forward"
                        && relation.TargetType?.Value == "work"
                        && relation.Target is { } target
                    )
                    {
                        AddLookup(target.Identity.Identity, [], "recording_performance_work");
                    }
                }
            }
        }

        var performerContexts = seeds
            .Where(seed => seed.ArtistMusicBrainzId.HasValue)
            .GroupBy(seed =>
                (
                    seed.ArtistMusicBrainzId!.Value,
                    ShadowCompositionAdapter.NormalizeTitle(seed.Title)
                )
            )
            .ToDictionary(
                group => group.Key,
                group => group.Select(seed => seed.CatalogUuid).Distinct().ToArray()
            );
        foreach (var page in decoded.Values.SelectMany(facts => facts.MusicBrainzSearchPages ?? []))
        {
            foreach (var hit in page.Hits ?? [])
            {
                var owners = (hit.ArtistCredits ?? [])
                    .SelectMany(credit =>
                    {
                        var identity = credit.Artist?.Identity.Identity;
                        return
                            identity is { Provider: "musicbrainz.org", ResourceType: "artist" }
                            && Guid.TryParseExact(identity.ProviderKey, "D", out var artist)
                            && hit.Entity.Name is { } title
                            ? performerContexts.GetValueOrDefault(
                                (artist, ShadowCompositionAdapter.NormalizeTitle(title.Value))
                            ) ?? []
                            : Array.Empty<Guid>();
                    })
                    .Distinct()
                    .ToArray();
                AddLookup(
                    hit.Entity.Identity.Identity,
                    owners,
                    owners.Length > 0
                        ? "matching_recording_performer_alternative"
                        : "retained_search_alternative"
                );
            }
        }

        foreach (var seed in seeds)
        {
            var performer = seed.ArtistMusicBrainzId is { } mbid
                ? new MusicBrainzSearchArtistConstraint(
                    MusicBrainzSearchArtistUse.RecordingPerformer,
                    ExternalArtistMbid: mbid
                )
                : new MusicBrainzSearchArtistConstraint(
                    MusicBrainzSearchArtistUse.RecordingPerformer,
                    Name: seed.ArtistName
                );
            AddSearch(
                new(MusicBrainzLookupKind.Recording, seed.Title, performer),
                0,
                [seed.CatalogUuid]
            );
            // A repertoire performer is never supplied as a work author.
            AddSearch(new(MusicBrainzLookupKind.Work, seed.Title), 0, [seed.CatalogUuid]);
        }
        return new(commands.Values.ToArray(), issues.Distinct(StringComparer.Ordinal).ToArray());

        void AddSearch(MusicBrainzSearchQuery query, int offset, IReadOnlyList<Guid> catalogUuids)
        {
            var request = new MusicBrainzSearchPageRequest(
                query,
                offset,
                MusicBrainzCacheMode.ReuseFresh,
                CacheAge
            );
            var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
            Add(
                new(
                    CommandUuid(descriptor.Resource),
                    descriptor.Resource,
                    request,
                    null,
                    catalogUuids,
                    "literal_repertoire_search"
                )
            );
            if (!retained.TryGetValue(descriptor.Resource, out var pages))
            {
                return;
            }
            foreach (var input in pages)
            {
                var facts = decoded[input.Uuid];
                var page = facts.MusicBrainzSearchPages?.SingleOrDefault();
                if (
                    page == null
                    || page.Offset?.Value != offset
                    || page.Count == null
                    || page.Hits == null
                )
                {
                    issues.Add($"UnreadableSearchPage:{input.Uuid:D}");
                    continue;
                }
                foreach (var hit in page.Hits)
                {
                    AddLookup(hit.Entity.Identity.Identity, catalogUuids, "search_alternative");
                }
                using var document = NormalizationRetainedDocument.Open(input);
                var members = document
                    .Resolve("")
                    .GetProperty(query.Kind == MusicBrainzLookupKind.Work ? "works" : "recordings")
                    .GetArrayLength();
                if (offset + members < page.Count.Value)
                {
                    if (members != MusicBrainzCandidateQueryBuilder.PageSize)
                    {
                        issues.Add($"ShortSearchPageBeforeReportedEnd:{input.Uuid:D}");
                    }
                    else if (
                        offset + members
                        >= MusicBrainzCandidateQueryBuilder.PageSize
                            * MusicBrainzCandidateQueryBuilder.MaximumPages
                    )
                    {
                        issues.Add($"SearchPageLimit:{descriptor.Resource.ProviderKey}");
                    }
                    else
                    {
                        AddSearch(query, offset + members, catalogUuids);
                    }
                }
            }
            if (
                pages
                    .Select(input =>
                        decoded[input.Uuid].MusicBrainzSearchPages?.SingleOrDefault()?.Count?.Value
                    )
                    .Distinct()
                    .Count() > 1
            )
            {
                issues.Add($"SearchCountDrift:{descriptor.Resource.ProviderKey}");
            }
        }

        void AddLookup(
            NormalizationResourceKey resource,
            IReadOnlyList<Guid> catalogUuids,
            string reason
        )
        {
            if (
                resource.Provider != "musicbrainz.org"
                || !Guid.TryParseExact(resource.ProviderKey, "D", out var mbid)
                || resource.ResourceType is not ("work" or "recording")
            )
            {
                issues.Add($"UnsupportedHydrationTarget:{resource}");
                return;
            }
            var kind =
                resource.ResourceType == "work"
                    ? MusicBrainzLookupKind.Work
                    : MusicBrainzLookupKind.Recording;
            Add(
                new(
                    CommandUuid(resource),
                    resource,
                    null,
                    new(kind, mbid, MusicBrainzCacheMode.ReuseFresh, CacheAge),
                    catalogUuids,
                    reason
                )
            );
        }

        void Add(ShadowCompositionAcquisitionCommand command)
        {
            if (commands.TryGetValue(command.Resource, out var existing))
            {
                commands[command.Resource] = existing with
                {
                    CatalogUuids = existing
                        .CatalogUuids.Concat(command.CatalogUuids)
                        .Distinct()
                        .Order()
                        .ToArray(),
                };
                return;
            }
            if (originals.TryGetValue(command.Resource, out var original))
            {
                // Preserve exact request/cache policy and UUID, including terminal failures.
                command = original with
                {
                    CatalogUuids = original
                        .CatalogUuids.Concat(command.CatalogUuids)
                        .Distinct()
                        .Order()
                        .ToArray(),
                    Reason = command.Reason,
                };
            }
            commands.Add(command.Resource, command);
        }
    }

    public static Guid CommandUuid(NormalizationResourceKey resource)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(Version + "\n" + JsonSerializer.Serialize(resource))
        );
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static void VerifyInput(NormalizationInputRevision input)
    {
        if (
            input.Uuid == Guid.Empty
            || !string.Equals(
                input.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(input.Payload)),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidDataException(
                "Retained MusicBrainz input bytes differ from their revision digest."
            );
        }
    }
}
