using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed partial class ShadowPeopleAdapter
{
    // Reuse the actual typed actor projection, including literal aliases and
    // cached model interpretation. A mismatched reconstruction cannot be exported.
    internal static SharedPeopleActor[] ReadSharedSource(JsonElement outcome)
    {
        var source = outcome.GetProperty("inputUuid").GetGuid();
        var artist = outcome.GetProperty("artistUuid").GetGuid();
        var evidence = outcome.GetProperty("evidence");
        var fields = evidence
            .GetProperty("originalInputs")
            .Deserialize<Dictionary<string, string?>>(Json)!;
        if (!fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Fields))
        {
            throw new InvalidDataException("Shared people require all six original source fields.");
        }
        var creditActors =
            evidence.GetProperty("creditActors").ValueKind == JsonValueKind.Null
                ? []
                : evidence.GetProperty("creditActors").Deserialize<CreditMention[]>(Json)!;
        var aliases =
            evidence.GetProperty("sourceActorAliases").ValueKind == JsonValueKind.Null
                ? null
                : evidence
                    .GetProperty("sourceActorAliases")
                    .Deserialize<SourceActorAliasExtraction<NormalizationTextEvidence>>(Json);
        var reproducedCredits = new RecordingCreditExtractor().Extract(fields);
        var reproducedAliases = new SourceActorAliasExtractor().Extract(fields);
        if (
            !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(reproducedCredits.Credits, Json),
                JsonSerializer.SerializeToElement(creditActors, Json)
            )
            || !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(reproducedAliases, Json),
                JsonSerializer.SerializeToElement(aliases, Json)
            )
        )
        {
            throw new InvalidDataException(
                "Original actor facts do not reproduce their exact retained source fields."
            );
        }
        var extraction = new NormalizationBatchExtraction(
            "source_sample",
            0,
            source,
            0,
            artist,
            null,
            fields,
            null,
            new(creditActors, []),
            null,
            aliases
        );
        ShadowLunaCreditMatch? model = null;
        if (
            evidence.TryGetProperty("model", out var retained)
            && retained.ValueKind != JsonValueKind.Null
        )
        {
            var state = retained.GetProperty("state").GetString()!;
            var original = retained.GetProperty("originalLunaResult").Clone();
            model = new(
                state,
                retained.GetProperty("reason").GetString(),
                retained.GetProperty("basis").Deserialize<ShadowLunaCreditBasis>(Json)!,
                state == "extracted"
                    ? original
                        .GetProperty("extraction")
                        .Deserialize<RecordingCreditExtraction>(Json)
                    : null,
                original
            );
            if (model.Extraction is not null)
            {
                var retainedResult = original.Deserialize<LunaCreditExtractionResult>(Json)!;
                if (
                    retainedResult.State != "extracted"
                    || retainedResult.CacheKey != model.Basis.CacheKey
                    || retainedResult.Extraction.Credits.Any(credit =>
                        credit.ActorFacts is not null
                    )
                )
                {
                    throw new InvalidDataException(
                        "Retained model credits cannot manufacture actor attachments or identity facts."
                    );
                }
                ShadowLunaCreditEvidence.ValidateCredits(fields, retainedResult);
            }
        }
        var actors = LocalActors(extraction).Concat(ModelActors(extraction, model)).ToArray();
        var actualPeople = JsonSerializer.SerializeToElement(
            actors.Select(actor => actor.Person).ToArray(),
            Json
        );
        if (
            !JsonElement.DeepEquals(
                actualPeople,
                outcome.GetProperty("proposal").GetProperty("people")
            )
        )
        {
            throw new InvalidDataException(
                "Original people do not reproduce their shared typed actor facts."
            );
        }
        var credits = LocalCredits(extraction, actors.Select(actor => actor.Person).ToArray())
            .Concat(ModelCredits(extraction, model))
            .ToArray();
        return actors
            .Select(actor => new SharedPeopleActor(
                actor.Person,
                actor.Match,
                actor.ProjectionComplete,
                actor.Evidence,
                credits
                    .Where(credit => credit.PersonEntityKey == actor.Person.EntityKey)
                    .Select(credit => credit.EntityKey)
                    .ToArray()
            ))
            .ToArray();
    }

    internal static string[] SharedAccountKeys(PersonMatchRecord facts) =>
        LookupKeys(facts).ToArray();

    internal static SharedPeopleSupport CompareSharedBuckets(
        string anchor,
        SharedPeopleActor left,
        string candidate,
        SharedPeopleActor right
    )
    {
        // Artist context belongs to occurrences. It ranks candidates in full
        // matching but never establishes or contradicts global person support.
        var comparison = new IdentityCandidateMatcher().ComparePeople(
            left.Facts with
            {
                ArtistUuids = new HashSet<Guid>(),
            },
            right.Facts with
            {
                ArtistUuids = new HashSet<Guid>(),
            }
        );
        var support = IdentitySupport(
            left.Facts,
            right.Facts,
            comparison,
            left.Complete && right.Complete
        );
        return new(
            anchor,
            candidate,
            support.Supported,
            support.HasConflict,
            support.Uncertain,
            support.AccountAgrees,
            comparison.Features
        );
    }
}
