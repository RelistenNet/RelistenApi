using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowCompositionAdapter
{
    private static readonly JsonSerializerOptions ProviderJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Provider work identities and recording-to-work assertions are already explicit source
    /// facts. They can share work keys across captures without assigning any catalog recording,
    /// performer repertoire, or canonical person to those works.
    /// </summary>
    private ShadowCatalogResult ProcessProvider(ShadowCatalogInput input)
    {
        if (providerIndexFailures.TryGetValue(input.Uuid, out var indexFailure))
        {
            return ShadowCatalogResult.For(
                input,
                "compositions",
                "failed",
                Version,
                null,
                new { inputRevisionUuid = input.Uuid, errorType = indexFailure },
                "invalid_retained_provider_input"
            );
        }
        try
        {
            var revision =
                input
                    .Record.GetProperty("input")
                    .Deserialize<NormalizationInputRevision>(ProviderJson)
                ?? throw new InvalidDataException("Missing retained provider revision.");
            if (
                revision.Uuid != input.Uuid
                || !StringComparer.OrdinalIgnoreCase.Equals(
                    revision.Sha256,
                    Convert.ToHexStringLower(SHA256.HashData(revision.Payload))
                )
            )
            {
                throw new InvalidDataException(
                    "Provider revision identity or retained bytes differ."
                );
            }

            var decoded = new NormalizationProviderDecoder().Decode(revision);
            using var document = NormalizationRetainedDocument.Open(revision);
            var works = decoded.MusicBrainzWorks ?? [];
            var paths = new List<ShadowProviderCompositionRelationship>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var work in works)
            {
                document.ValidateEvidence(work.Work.Identity.Evidence);
                AddWorkKey(keys, work.Work.Identity);
                foreach (var relationship in work.Relationships ?? [])
                {
                    document.ValidateEvidence(relationship.Evidence);
                }
            }

            foreach (var recording in decoded.MusicBrainzRecordings ?? [])
            {
                foreach (var relationship in recording.Relationships ?? [])
                {
                    if (
                        relationship.Type?.Value != "performance"
                        || relationship.Direction?.Value != "forward"
                        || relationship.TargetType?.Value != "work"
                        || relationship.Target is not { } target
                    )
                    {
                        continue;
                    }

                    document.ValidateEvidence(relationship.Evidence);
                    document.ValidateEvidence(recording.Recording.Identity.Evidence);
                    AddWorkKey(keys, target.Identity);
                    paths.Add(new(recording.Recording.Identity, target.Identity, relationship));
                }
            }

            var workPages =
                revision.Resource.ResourceType == "work-search"
                    ? decoded.MusicBrainzSearchPages ?? []
                    : [];
            foreach (var hit in workPages.SelectMany(page => page.Hits ?? []))
            {
                document.ValidateEvidence(hit.Evidence);
                AddWorkKey(keys, hit.Entity.Identity);
                foreach (var artist in hit.WorkArtists ?? [])
                {
                    document.ValidateEvidence(artist.Evidence);
                }
            }

            var titleClaims = decoded
                .Setlists.SelectMany(account => account.Passages)
                .Select(passage => passage.Title)
                .ToArray();
            foreach (var title in titleClaims)
            {
                document.ValidateEvidence(title.Evidence);
            }

            var outcome =
                keys.Count > 0 ? "proposed"
                : titleClaims.Length > 0 ? "unresolved"
                : "not_applicable";
            var proposal = new ShadowProviderCompositionProposal(
                works,
                paths,
                workPages,
                titleClaims,
                false,
                false
            );
            return ShadowCatalogResult.For(
                input,
                "compositions",
                outcome,
                Version,
                proposal,
                new
                {
                    revision.Uuid,
                    revision.Resource,
                    revision.Sha256,
                    revision.CaptureVersion,
                    decoded.DecoderVersion,
                    decoded.Issues,
                    scope = "literal_provider_work_facts_only",
                },
                keys.Count > 0 ? "explicit_provider_work_assertions"
                    : titleClaims.Length > 0 ? "provider_titles_need_work_correspondence"
                    : "provider_input_has_no_work_claims",
                keys.Order(StringComparer.Ordinal).ToArray()
            );
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or InvalidDataException
                        or NotSupportedException
            )
        {
            return ShadowCatalogResult.For(
                input,
                "compositions",
                "failed",
                Version,
                null,
                new { inputRevisionUuid = input.Uuid, errorType = error.GetType().Name },
                error is NotSupportedException
                    ? "unsupported_provider_capture"
                    : "invalid_retained_provider_input"
            );
        }
    }

    private static void AddWorkKey(HashSet<string> keys, ObservedProviderIdentity identity)
    {
        if (
            identity.Identity.Provider == "musicbrainz.org"
            && identity.Identity.ResourceType == "work"
            && Guid.TryParse(identity.Identity.ProviderKey, out var work)
            && work != Guid.Empty
        )
        {
            keys.Add(WorkKey(work));
        }
    }
}

public sealed record ShadowProviderCompositionProposal(
    IReadOnlyList<MusicBrainzWorkObservation> WorkAssertions,
    IReadOnlyList<ShadowProviderCompositionRelationship> RecordingWorkRelationships,
    IReadOnlyList<MusicBrainzSearchPageObservation> WorkSearchPages,
    IReadOnlyList<ObservedValue<string>> UnassignedPassageTitles,
    bool CatalogWorkIdentityAssigned,
    bool PublicationPermitted
);

public sealed record ShadowProviderCompositionRelationship(
    ObservedProviderIdentity Recording,
    ObservedProviderIdentity Work,
    MusicBrainzRelationshipObservation Relationship
);
