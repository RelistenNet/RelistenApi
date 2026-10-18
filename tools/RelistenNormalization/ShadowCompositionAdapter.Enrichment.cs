using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// An evidenced path from a literal catalog artist MBID to an external recording performer
/// and its work. It proposes composition correspondence, never live/studio recording equality.
/// All roles retain provider wording and qualified artist IDs; no canonical people are minted.
/// </summary>
public sealed record ShadowCompositionProviderCorrespondence(
    Guid WorkMbid,
    string WorkTitle,
    JsonElement CatalogArtistAssertion,
    string RecordingInputSha256,
    MusicBrainzRecordingObservation Recording,
    MusicBrainzRelationshipObservation Performance,
    IReadOnlyList<MusicBrainzWorkObservation> HydratedWorks,
    IReadOnlyList<MusicBrainzRelationshipObservation> WriterRoles,
    IReadOnlyList<string> RemainingGaps
);

public sealed partial class ShadowCompositionAdapter
{
    private readonly Dictionary<
        long,
        (Guid LocalUuid, Guid Mbid, JsonElement Evidence)
    > catalogArtistMbids = new();
    private readonly Dictionary<
        (Guid ArtistMbid, string Title),
        List<(
            MusicBrainzRecordingObservation Recording,
            string Hash,
            IReadOnlyList<ProviderDecodeIssue> Issues
        )>
    > providerRecordings = new();
    private readonly Dictionary<Guid, List<MusicBrainzWorkObservation>> providerWorks = new();
    private readonly Dictionary<Guid, string> indexedProviderInputs = new();
    private readonly HashSet<long> conflictingCatalogArtists = [];
    private readonly Dictionary<Guid, string> providerIndexFailures = new();

    private void ObserveCompositionContext(JsonElement record)
    {
        try
        {
            IndexCompositionContext(record);
        }
        catch (Exception error)
            when (record.TryGetProperty("kind", out var kind)
                && kind.GetString() == "provider_input"
                && error
                    is JsonException
                        or ArgumentException
                        or InvalidDataException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or NotSupportedException
            )
        {
            if (
                record.TryGetProperty("row", out var row)
                && row.TryGetProperty("uuid", out var uuid)
                && uuid.TryGetGuid(out var inputUuid)
            )
            {
                providerIndexFailures[inputUuid] = error.GetType().Name;
            }
        }
    }

    private void IndexCompositionContext(JsonElement record)
    {
        if (!record.TryGetProperty("kind", out var kindValue))
        {
            return;
        }
        var kind = kindValue.GetString();
        if (
            kind == "artist"
            && record.TryGetProperty("row", out var artist)
            && artist.TryGetProperty("musicbrainz_id", out var value)
            && Guid.TryParse(value.GetString(), out var mbid)
            && mbid != Guid.Empty
        )
        {
            var id = artist.GetProperty("id").GetInt64();
            var localUuid = artist.GetProperty("uuid").GetGuid();
            if (
                catalogArtistMbids.TryGetValue(id, out var previous)
                && (previous.LocalUuid != localUuid || previous.Mbid != mbid)
            )
            {
                conflictingCatalogArtists.Add(id);
            }
            catalogArtistMbids[id] = (localUuid, mbid, artist.Clone());
        }
        if (kind != "provider_input" || !record.TryGetProperty("input", out var serialized))
        {
            return;
        }
        var revision =
            serialized.Deserialize<NormalizationInputRevision>(ProviderJson)
            ?? throw new InvalidDataException("Missing retained provider input.");
        ShadowCompositionAcquisition.VerifyInput(revision);
        if (
            record.TryGetProperty("row", out var row)
            && row.GetProperty("uuid").GetGuid() != revision.Uuid
        )
        {
            throw new InvalidDataException("Provider wrapper identifies another retained input.");
        }
        if (indexedProviderInputs.TryGetValue(revision.Uuid, out var previousHash))
        {
            if (previousHash != revision.Sha256)
            {
                throw new InvalidDataException(
                    "Provider input UUID has conflicting retained bytes."
                );
            }
            return;
        }
        indexedProviderInputs.Add(revision.Uuid, revision.Sha256);
        if (revision.Resource.Provider != "musicbrainz.org")
        {
            return;
        }
        var decoded = new NormalizationProviderDecoder().Decode(revision);
        using var document = NormalizationRetainedDocument.Open(revision);
        foreach (var work in decoded.MusicBrainzWorks ?? [])
        {
            document.ValidateEvidence(work.Work.Evidence);
            if (TryWorkMbid(work.Work.Identity, out var workMbid))
            {
                if (!providerWorks.TryGetValue(workMbid, out var existing))
                {
                    existing = [];
                    providerWorks.Add(workMbid, existing);
                }
                existing.Add(work);
            }
        }
        foreach (var recording in decoded.MusicBrainzRecordings ?? [])
        {
            document.ValidateEvidence(recording.Recording.Evidence);
            if (recording.Recording.Name is not { } title)
            {
                continue;
            }
            document.ValidateEvidence(title.Evidence);
            foreach (var credit in recording.ArtistCredits ?? [])
            {
                if (
                    credit.Artist?.Identity.Identity
                        is not { Provider: "musicbrainz.org", ResourceType: "artist" } identity
                    || !Guid.TryParseExact(identity.ProviderKey, "D", out var performer)
                )
                {
                    continue;
                }
                document.ValidateEvidence(credit.Evidence);
                var key = (performer, NormalizeTitle(title.Value));
                if (!providerRecordings.TryGetValue(key, out var existing))
                {
                    existing = [];
                    providerRecordings.Add(key, existing);
                }
                existing.Add((recording, revision.Sha256, decoded.Issues));
            }
        }
    }

    private IReadOnlyList<ShadowCompositionProviderCorrespondence> ProviderCorrespondences(
        ShadowCatalogInput input,
        string title
    )
    {
        if (
            conflictingCatalogArtists.Contains(input.Artist.Id)
            || !catalogArtistMbids.TryGetValue(input.Artist.Id, out var artist)
            || artist.LocalUuid != input.Artist.Uuid
        )
        {
            return [];
        }
        var recordings =
            providerRecordings.GetValueOrDefault((artist.Mbid, NormalizeTitle(title))) ?? [];
        var result = new List<ShadowCompositionProviderCorrespondence>();
        foreach (var source in recordings)
        {
            if (
                providerIndexFailures.ContainsKey(
                    source.Recording.Origin.Evidence.InputRevisionUuid
                )
            )
            {
                continue;
            }
            foreach (var relation in source.Recording.Relationships ?? [])
            {
                if (
                    relation.Type?.Value != "performance"
                    || relation.Direction?.Value != "forward"
                    || relation.TargetType?.Value != "work"
                    || relation.Target is not { } target
                    || !TryWorkMbid(target.Identity, out var workMbid)
                )
                {
                    continue;
                }
                var works = (providerWorks.GetValueOrDefault(workMbid) ?? [])
                    .Where(work =>
                        !providerIndexFailures.ContainsKey(work.Origin.Evidence.InputRevisionUuid)
                    )
                    .ToArray();
                var writers = works
                    .SelectMany(work => work.Relationships ?? [])
                    .Where(value =>
                        value.Type?.Value is "composer" or "lyricist" or "writer"
                        && value.Direction?.Value == "backward"
                        && value.TargetType?.Value == "artist"
                        && value.Target?.Identity.Identity
                            is { Provider: "musicbrainz.org", ResourceType: "artist" }
                    )
                    .ToArray();
                var gaps = new List<string>
                {
                    "catalog_artist_external_id_is_a_retained_assertion",
                    "external_search_universe_incomplete",
                    "canonical_author_people_unassigned",
                };
                if (works.Length == 0)
                {
                    gaps.Add("full_work_lookup_required");
                }
                if (writers.Length == 0)
                {
                    gaps.Add("cited_writer_roles_unavailable");
                }
                gaps.AddRange(
                    source.Issues.Select(issue =>
                        "recording_decode:" + issue.Code + ":" + issue.Evidence.JsonPointer
                    )
                );
                result.Add(
                    new(
                        workMbid,
                        works.FirstOrDefault()?.Work.Name?.Value ?? target.Name?.Value ?? title,
                        artist.Evidence,
                        source.Hash,
                        source.Recording,
                        relation,
                        works,
                        writers,
                        gaps
                    )
                );
            }
        }
        return result;
    }

    private ShadowCompositionMention ResolveProviderCorrespondence(
        ShadowCatalogInput input,
        string title,
        string kind,
        string pointer,
        NormalizationTextEvidence? localEvidence,
        IReadOnlyList<string> repertoireKeys,
        IReadOnlyList<ShadowCompositionProviderCorrespondence> correspondences,
        IReadOnlyList<ShadowCompositionCandidate> retainedCandidates
    )
    {
        var works = correspondences.Select(value => value.WorkMbid).Distinct().ToArray();
        // A malformed member in the same recording may conceal a competing relationship.
        // Preserve the observed path for review without granting the partial decode a link.
        if (
            works.Length != 1
            || correspondences.Any(value =>
                value.RemainingGaps.Any(gap =>
                    gap.StartsWith("recording_decode:", StringComparison.Ordinal)
                )
            )
        )
        {
            return Unresolved(
                title,
                kind,
                pointer,
                localEvidence,
                repertoireKeys,
                works.Length > 1
                    ? "competing_provider_work_correspondences"
                    : "provider_recording_decode_requires_review",
                works.Select(WorkKey).ToArray()
            ) with
            {
                ProviderCorrespondences = correspondences,
                RetainedCandidates = retainedCandidates,
            };
        }
        var links = correspondences
            .Select(value =>
            {
                var support = JsonSerializer.SerializeToElement(
                    new
                    {
                        value.CatalogArtistAssertion,
                        value.Recording,
                        value.Performance,
                        value.HydratedWorks,
                    },
                    Json
                );
                var proof = new ShadowCompositionProof(
                    input.Uuid,
                    input.Artist.Uuid,
                    title,
                    value.WorkMbid,
                    value.WorkTitle,
                    "provider-recording:"
                        + value.Recording.Origin.Evidence.InputRevisionUuid.ToString("D"),
                    value.RecordingInputSha256,
                    support,
                    JsonSerializer.SerializeToElement(Array.Empty<object>()),
                    JsonSerializer.SerializeToElement(value.WriterRoles, Json),
                    "The catalog's literal artist MBID matches an external recording performer; the recording explicitly performs this work. This is a composition proposal with no assertion of live/studio recording equality.",
                    "All retained recording paths in this performer/title context resolve to one work; unobserved alternatives remain possible.",
                    value.RemainingGaps
                );
                return new ShadowCompositionWorkLink(
                    WorkKey(value.WorkMbid),
                    value.WorkMbid,
                    value.WorkTitle,
                    "catalog_artist_mbid_recording_performance",
                    proof.ClaimId,
                    null,
                    support,
                    proof.Writers,
                    proof.ProviderWriterRoles,
                    Compare(title, proof),
                    proof.Rationale,
                    proof.CompetingIdentityAssessment,
                    proof.RemainingGaps,
                    value
                );
            })
            .ToArray();
        return new(
            title,
            kind,
            pointer,
            localEvidence,
            repertoireKeys,
            "proposed",
            "provider_supported_composition_draft",
            links,
            [],
            "A retained performer identity and explicit recording-to-work relationship support this composition draft.",
            retainedCandidates,
            correspondences
        );
    }

    private static bool TryWorkMbid(ObservedProviderIdentity identity, out Guid mbid)
    {
        mbid = Guid.Empty;
        return identity.Identity is { Provider: "musicbrainz.org", ResourceType: "work" } resource
            && Guid.TryParseExact(resource.ProviderKey, "D", out mbid)
            && mbid != Guid.Empty;
    }
}
