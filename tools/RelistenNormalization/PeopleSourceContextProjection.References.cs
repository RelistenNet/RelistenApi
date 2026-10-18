using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class PeopleSourceContextProjection
{
    public const string ReferenceVersion = "people-source-reference-v1";

    /// <summary>
    /// Selects expressed actors from an exact contact/profile source reference. Unowned hint
    /// hypotheses remain distinct from confirmed attachment, even when only one actor is nearby.
    /// Candidate UUIDs are field-local references, never canonical person identities.
    /// </summary>
    public PeopleSourceReferenceProjectionResult ProjectReference(
        string evidenceId,
        Guid artistUuid,
        PeopleSourceReferenceSelector selector
    )
    {
        if (!fields.TryGetValue((evidenceId, selector.Evidence.Field), out var field))
        {
            return new(PeopleSourceReferenceStatus.NotFound, [], ["source_field_not_found"]);
        }
        if (!IsOriginalAnchor(field.Raw, selector.Evidence))
        {
            return new(PeopleSourceReferenceStatus.NotFound, [], ["source_anchor_mismatch"]);
        }

        var candidates = field
            .Credits.Where(credit => credit.ActorFacts != null)
            // One actor can occupy several recording roles. Preserve source-reference identity
            // while retaining separate actors and all uncertainty in their original facts.
            .GroupBy(credit => credit.ActorFacts!.ReferenceUuid)
            .Select(group => group.First())
            .Select(credit =>
                (Credit: credit, Attachment: ReferenceAttachment(credit.ActorFacts!, selector))
            )
            .Where(value => value.Attachment.HasValue)
            .Select(value => new PeopleSourceReferenceCandidate(
                value.Credit.ActorFacts!.ReferenceUuid,
                value.Credit.Name,
                value.Attachment!.Value,
                PeopleActorProjection.Project(value.Credit, artistUuid),
                value.Credit.ActorFacts!
            ))
            .ToArray();
        if (candidates.Length == 0)
        {
            return new(PeopleSourceReferenceStatus.NotFound, [], ["source_reference_not_attached"]);
        }

        var status =
            candidates.Length > 1 ? PeopleSourceReferenceStatus.Ambiguous
            : candidates[0].Attachment == PeopleSourceReferenceAttachment.Unattributed
                ? PeopleSourceReferenceStatus.Unattributed
            : PeopleSourceReferenceStatus.Matched;
        var issues = candidates
            .SelectMany(candidate => candidate.Facts.Unresolved)
            .Select(issue => issue.Reason)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (status == PeopleSourceReferenceStatus.Ambiguous)
        {
            issues.Add("ambiguous_source_reference");
        }
        if (candidates.Any(candidate => !candidate.Projection.Complete))
        {
            issues.Add("context_query_incomplete");
        }
        return new(status, candidates, issues);
    }

    private static PeopleSourceReferenceAttachment? ReferenceAttachment(
        CreditedActorFacts<NormalizationTextEvidence> facts,
        PeopleSourceReferenceSelector selector
    )
    {
        var evidence = selector.Evidence;
        if (
            selector.Kind == PeopleSourceReferenceKind.Contact
            && facts.Contacts.Any(contact => contact.Evidence[0] == evidence)
        )
        {
            return PeopleSourceReferenceAttachment.Contact;
        }
        if (selector.Kind == PeopleSourceReferenceKind.Profile)
        {
            if (facts.Profiles.Any(profile => profile.Evidence[0] == evidence))
            {
                return PeopleSourceReferenceAttachment.Profile;
            }
            // The supported extractor emits this only after proving attachment to the one
            // named actor. Its failure to classify the URL must not invent a profile key.
            if (
                facts.Unresolved.Any(issue =>
                    issue.Reason == "unknown_profile_namespace"
                    && issue.Evidence[0] == evidence
                    && issue.CandidateReferenceUuids.Count == 1
                    && issue.CandidateReferenceUuids[0] == facts.ReferenceUuid
                )
            )
            {
                return PeopleSourceReferenceAttachment.UnknownProfileNamespace;
            }
        }
        var unresolvedReason = selector.Kind switch
        {
            PeopleSourceReferenceKind.Contact => "unattributed_contact",
            PeopleSourceReferenceKind.Profile => "unattributed_profile",
            _ => null,
        };
        return
            unresolvedReason != null
            && facts.Unresolved.Any(issue =>
                issue.Reason == unresolvedReason
                && issue.Evidence[0] == evidence
                && issue.CandidateReferenceUuids.Contains(facts.ReferenceUuid)
            )
            ? PeopleSourceReferenceAttachment.Unattributed
            : null;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<PeopleSourceReferenceKind>))]
public enum PeopleSourceReferenceKind
{
    [JsonStringEnumMemberName("contact")]
    Contact,

    [JsonStringEnumMemberName("profile")]
    Profile,
}

[JsonConverter(typeof(JsonStringEnumConverter<PeopleSourceReferenceStatus>))]
public enum PeopleSourceReferenceStatus
{
    [JsonStringEnumMemberName("matched")]
    Matched,

    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,

    [JsonStringEnumMemberName("unattributed")]
    Unattributed,

    [JsonStringEnumMemberName("not_found")]
    NotFound,
}

[JsonConverter(typeof(JsonStringEnumConverter<PeopleSourceReferenceAttachment>))]
public enum PeopleSourceReferenceAttachment
{
    [JsonStringEnumMemberName("contact")]
    Contact,

    [JsonStringEnumMemberName("profile")]
    Profile,

    [JsonStringEnumMemberName("unknown_profile_namespace")]
    UnknownProfileNamespace,

    [JsonStringEnumMemberName("unattributed")]
    Unattributed,
}

public sealed record PeopleSourceReferenceSelector(
    PeopleSourceReferenceKind Kind,
    NormalizationTextEvidence Evidence
);

public sealed record PeopleSourceReferenceCandidate(
    Guid ReferenceUuid,
    string Name,
    PeopleSourceReferenceAttachment Attachment,
    PeopleActorProjectionResult Projection,
    CreditedActorFacts<NormalizationTextEvidence> Facts
);

public sealed record PeopleSourceReferenceProjectionResult(
    PeopleSourceReferenceStatus Status,
    IReadOnlyList<PeopleSourceReferenceCandidate> Candidates,
    IReadOnlyList<string> Issues
);
