using System.Security.Cryptography;
using System.Text;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Turns cited program references into literal recording searches. The caller
/// authenticates the frozen selection inputs before planning. This class checks
/// target ownership and reproduces both source spans; it performs no transport,
/// identity matching or allocation of catalog identities.
/// </summary>
public static class CompositionProgramQueryPlanner
{
    public const string Version = "composition-program-query-plan-v1";
    private const int MaximumReferences = 1024;

    public static CompositionProgramQueryPlan Plan(
        IReadOnlyList<CompositionProgramTarget> inputs,
        IReadOnlyList<CompositionProgramReference> references,
        MusicBrainzSearchPlanIdentity identity
    )
    {
        if (
            references.Count > MaximumReferences
            || identity.PlanUuid == Guid.Empty
            || identity.RequestSha256 is not { Length: 64 }
            || !identity.RequestSha256.All(char.IsAsciiHexDigitLower)
        )
        {
            throw new ArgumentException("Use a bounded program-reference plan with an identity.");
        }
        var targets = inputs.ToDictionary(input => new CompositionSelectionTargetKey(
            input.Song.Scope,
            input.Song.ArtistUuid,
            input.Song.EntityKey
        ));

        // Validate the entire reference set before constructing commands. Identical
        // prose from another song or recording cannot supply this target's citation.
        foreach (var reference in references)
        {
            if (!targets.TryGetValue(reference.Target, out var input))
            {
                throw new InvalidDataException("A program reference belongs to another target.");
            }
            ValidateReference(input, reference);
        }

        var queries = new Dictionary<NormalizationResourceKey, CompositionProgramQuery>();
        var unresolved = new List<CompositionProgramReferenceIssue>();
        foreach (var reference in references)
        {
            if (reference.Kind == CompositionProgramReferenceKind.Unknown)
            {
                unresolved.Add(new(reference, "program_relationship_unresolved"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(reference.ProgramTitle))
            {
                unresolved.Add(new(reference, "missing_program_title"));
                continue;
            }
            var target = targets[reference.Target];
            var request = new MusicBrainzSearchPageRequest(
                new(
                    MusicBrainzLookupKind.Recording,
                    target.Song.Title,
                    Program: new(ReleaseTitle: reference.ProgramTitle)
                ),
                0,
                MusicBrainzCacheMode.ReuseFresh,
                TimeSpan.FromDays(7),
                identity
            );
            var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
            if (queries.TryGetValue(descriptor.Resource, out var previous))
            {
                queries[descriptor.Resource] = previous with
                {
                    References = previous.References.Append(reference).ToArray(),
                };
                continue;
            }

            var commandUuid = ShadowCompositionAcquisition.CommandUuid(
                descriptor.Resource,
                identity
            );
            queries.Add(
                descriptor.Resource,
                new(
                    new(
                        commandUuid,
                        descriptor.Resource,
                        request,
                        null,
                        [],
                        "cited_program_recording_search"
                    ),
                    [reference]
                )
            );
        }

        return new(queries.Values.ToArray(), unresolved, identity);
    }

    private static void ValidateReference(
        CompositionProgramTarget input,
        CompositionProgramReference reference
    )
    {
        if (input.Context.Key != reference.Target || !Enum.IsDefined(reference.Kind))
        {
            throw new InvalidDataException(
                "A program reference requires its exact target context."
            );
        }
        var source = input
            .Context.Witnesses.SelectMany(witness => witness.Texts)
            .FirstOrDefault(text =>
                text.Origin == reference.Origin
                && text.TextSha256 == reference.TextSha256
                && text.Field == reference.TitleEvidence.Field
            );
        if (
            source is null
            || Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Value)))
                != reference.TextSha256
            || reference.TitleEvidence.Quote != reference.ProgramTitle
        )
        {
            throw new InvalidDataException("A program reference differs from its original source.");
        }
        var runes = source.Value.EnumerateRunes().ToArray();
        ValidateSpan(source, runes, reference.TitleEvidence);
        ValidateSpan(source, runes, reference.ReferenceEvidence);
        if (
            reference.ReferenceEvidence.StartCodePoint > reference.TitleEvidence.StartCodePoint
            || reference.ReferenceEvidence.EndCodePoint < reference.TitleEvidence.EndCodePoint
        )
        {
            throw new InvalidDataException("Program wording must enclose the cited program title.");
        }
    }

    private static void ValidateSpan(
        CompositionSelectionTargetText source,
        Rune[] runes,
        NormalizationTextEvidence evidence
    )
    {
        if (
            evidence.Field != source.Field
            || evidence.JsonPointer != source.Origin.JsonPointer
            || evidence.StartCodePoint < 0
            || evidence.EndCodePoint < evidence.StartCodePoint
            || evidence.EndCodePoint > runes.Length
            || string.Concat(
                runes[evidence.StartCodePoint..evidence.EndCodePoint]
                    .Select(rune => rune.ToString())
            ) != evidence.Quote
        )
        {
            throw new InvalidDataException(
                "Program evidence does not reproduce its original span."
            );
        }
    }
}

/// <summary>One deduplicated external resource with every target's original reference retained.</summary>
public sealed record CompositionProgramQuery(
    ShadowCompositionAcquisitionCommand Command,
    IReadOnlyList<CompositionProgramReference> References
);

public sealed record CompositionProgramReferenceIssue(
    CompositionProgramReference Reference,
    string Reason
);

public sealed record CompositionProgramQueryPlan(
    IReadOnlyList<CompositionProgramQuery> Queries,
    IReadOnlyList<CompositionProgramReferenceIssue> Unresolved,
    MusicBrainzSearchPlanIdentity PlanIdentity
)
{
    public bool PublicationPermitted => false;
    public bool ExternalUniverseComplete => false;
}
