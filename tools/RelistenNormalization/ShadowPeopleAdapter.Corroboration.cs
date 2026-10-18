using System.Security.Cryptography;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPeopleAdapter
{
    private const int MaximumHypothesisExamples = 8;
    private const string HypothesisMethod = "direct_name_or_explicit_alias_and_attributed_account";
    private readonly Dictionary<string, ActorWitnesses> matchingFacts = new(StringComparer.Ordinal);

    private ActorWitnesses RememberMatchingFacts(string signature, ActorAccount actor)
    {
        // All lookup routes share membership. A role, alias route or model interpretation
        // must not turn one source into several corroborating sources.
        if (!matchingFacts.TryGetValue(signature, out var witnesses))
        {
            witnesses = new(actor);
            matchingFacts.Add(signature, witnesses);
        }
        else
        {
            witnesses.Remember(actor);
        }

        return witnesses;
    }

    private static string ExtractionIdentity(NormalizationBatchExtraction extraction) =>
        Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(extraction, Json))
        );

    private ShadowPeopleIdentityHypothesis? BuildHypothesis(
        ActorAccount actor,
        IReadOnlyList<ComparedActor> candidates,
        ShadowPeopleRelationshipCoverage coverage,
        string extractionIdentity
    )
    {
        var supported = candidates.Where(pair => pair.Supported).ToArray();
        if (supported.Length == 0)
        {
            return null;
        }

        // Equivalent facts have one comparison, but every observed source remains in the
        // membership ledger. Union before counting: name/account routes, roles and local/model
        // interpretations can overlap. This neither expands all source pairs nor merges actors.
        var buckets = supported.Select(pair => pair.Witnesses).Distinct().ToArray();
        // Repeated identical facts are common. Reuse their existing membership set rather
        // than walking N sources again for every one of those N incoming recordings.
        IReadOnlySet<Guid> sources =
            buckets.Length == 1
                ? buckets[0].SourceUuids
                : buckets.SelectMany(bucket => bucket.SourceUuids).ToHashSet();
        var sourceCount = sources.Count - (sources.Contains(actor.Person.SourceUuid) ? 1 : 0);
        var artists = supported.Select(pair => pair.Candidate.Person.ArtistUuid).Distinct().Count();
        var examples = supported
            .OrderBy(pair => pair.Candidate.Person.EntityKey, StringComparer.Ordinal)
            .Take(MaximumHypothesisExamples)
            .Select(pair => Relationship(actor, pair))
            .ToArray();
        var anchorIndexed =
            observedExtractionIdentities.TryGetValue(actor.Person.SourceUuid, out var observed)
            && observed == extractionIdentity;

        // The anchor and original extraction identify a proposal, not its changing pool.
        // Adding support or changing a representative therefore cannot allocate a new handle.
        // Method/extraction identity is still required when replaying or comparing hypotheses.
        var handle = ShadowCatalogIdentity.Key(
            "person_hypothesis",
            JsonSerializer.Serialize(
                new
                {
                    AnchorEntityKey = actor.Person.EntityKey,
                    Method = Version,
                    ExtractionIdentitySha256 = extractionIdentity,
                },
                Json
            )
        );
        return new(
            handle,
            actor.Person.EntityKey,
            extractionIdentity,
            HypothesisMethod,
            supported.Length,
            sourceCount,
            artists,
            coverage.Complete,
            anchorIndexed && coverage.Complete,
            examples,
            // Every example comes from a supported bucket, so equality of distinct counts
            // establishes complete membership without scanning the large source set again.
            examples.Length == supported.Length
                && sourceCount
                    == examples.Select(example => example.TargetSourceUuid).Distinct().Count()
        );
    }
}

/// <summary>
/// An unaccepted, anchor-directed identity hypothesis from directly supported comparisons.
/// Counts describe actual source membership of the retrieved matching-fact buckets in this
/// observed corpus. They do not count independent recordings, accepted people or identity truth.
/// No transitive links or synthetic union of different actors' matching facts is constructed.
/// The proposal handle is not a canonical UUID and grants no creation/publication authority.
/// </summary>
public sealed record ShadowPeopleIdentityHypothesis(
    string ProposalHandle,
    string AnchorEntityKey,
    string ExtractionIdentitySha256,
    string Method,
    int DirectSupportedMatchingFactCount,
    int DistinctSupportingSourceCount,
    int DistinctSupportingArtistCount,
    bool MatchingFactComparisonsComplete,
    bool SourceMembershipComplete,
    IReadOnlyList<ShadowPersonRelationship> DirectEvidenceExamples,
    bool DirectEvidenceExamplesComplete,
    bool AcceptedIdentity = false,
    bool QualifiedForAutomaticAcceptance = false
);
