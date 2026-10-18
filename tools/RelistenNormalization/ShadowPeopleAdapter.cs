using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects quoted recording credits and role-free aliases into shadow people. The global
/// index retrieves attributed contact/profile accounts; it never turns those nonexclusive
/// accounts into accepted identities. Every person remains scoped to its original source.
/// </summary>
public sealed class ShadowPeopleAdapter : IShadowCatalogAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private const int MaximumRelatedAccounts = 100;
    private static readonly string[] Domains = ["people", "credits"];
    private static readonly string[] Fields =
    [
        "taper",
        "transferrer",
        "description",
        "source",
        "lineage",
        "taper_notes",
    ];
    private readonly HashSet<Guid> observedSources = [];
    private readonly Dictionary<string, Dictionary<string, ActorAccount>> accounts = new();
    private readonly IdentityCandidateMatcher matcher = new();

    public string Version => "shadow-people-v1";

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind == "source_sample" ? Domains : [];

    /// <summary>
    /// The runner supplies extraction records as well as raw rows during its first pass.
    /// Raw rows have no resourceUuid and do not add a second copy of an extraction.
    /// Index entries retain compact semantic facts and one deterministic source witness
    /// per artist, rather than every repeated description in the catalog.
    /// </summary>
    public void Observe(JsonElement record)
    {
        if (
            !record.TryGetProperty("kind", out var kind)
            || kind.GetString() != "source_sample"
            || !record.TryGetProperty("resourceUuid", out _)
        )
        {
            return;
        }

        var extraction = ReadExtraction(record);
        if (!observedSources.Add(extraction.ResourceUuid))
        {
            return;
        }

        foreach (var actor in Actors(extraction))
        {
            var signature =
                JsonSerializer.Serialize(
                    actor.Match with
                    {
                        Uuid = null,
                        ArtistUuids = new HashSet<Guid>(),
                    },
                    Json
                )
                + ":"
                + extraction.ArtistUuid;
            foreach (var lookup in LookupKeys(actor.Match))
            {
                if (!accounts.TryGetValue(lookup, out var bucket))
                {
                    bucket = new();
                    accounts.Add(lookup, bucket);
                }

                if (
                    !bucket.TryGetValue(signature, out var previous)
                    || StringComparer.Ordinal.Compare(
                        actor.Person.EntityKey,
                        previous.Person.EntityKey
                    ) < 0
                )
                {
                    bucket[signature] = actor;
                }
            }
        }
    }

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (input.Kind != "source_sample")
        {
            return [];
        }

        try
        {
            var extraction = input.Extraction is { } saved ? ReadExtraction(saved) : Extract(input);
            if (
                extraction.Kind != input.Kind
                || extraction.ResourceUuid != input.Uuid
                || extraction.ArtistUuid != input.Artist.Uuid
            )
            {
                throw new FormatException("Extraction belongs to a different source or artist.");
            }

            return Project(input, extraction);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return Domains
                .Select(domain =>
                    ShadowCatalogResult.For(
                        input,
                        domain,
                        "failed",
                        Version,
                        null,
                        new { Error = error.Message },
                        "invalid_source_extraction"
                    )
                )
                .ToArray();
        }
    }

    private ShadowCatalogResult[] Project(
        ShadowCatalogInput input,
        NormalizationBatchExtraction extraction
    )
    {
        var actors = Actors(extraction).ToArray();
        var comparisons = actors.Select(Relationships).ToArray();
        var relationships = comparisons.SelectMany(result => result.Links).ToArray();
        var people = actors.Select(actor => actor.Person).ToArray();
        var credits = (extraction.CreditExtraction?.Credits ?? [])
            .Select(credit => new ShadowRecordingCredit(
                ShadowCatalogIdentity.Key(
                    "credit",
                    input.Uuid + ":" + JsonSerializer.Serialize(credit, Json)
                ),
                PersonKey(input.Uuid, CreditReference(credit)),
                input.Uuid,
                input.Artist.Uuid,
                credit.Name,
                credit.Role,
                credit.OriginalRole,
                credit.Evidence
            ))
            .Where(credit => people.Any(person => person.EntityKey == credit.PersonEntityKey))
            .ToArray();
        var personKeys = people
            .Select(person => person.EntityKey)
            .Concat(
                relationships.SelectMany(link => new[] { link.FromEntityKey, link.ToEntityKey })
            )
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        var creditIssues = extraction.CreditExtraction?.Unresolved ?? [];

        return
        [
            ShadowCatalogResult.For(
                input,
                "people",
                people.Length > 0 ? "proposed" : "unresolved",
                Version,
                new ShadowPeopleProposal(
                    people,
                    relationships,
                    comparisons.Select(result => result.Coverage).ToArray(),
                    observedSources.Contains(input.Uuid)
                ),
                new
                {
                    OriginalInputs = extraction.Inputs,
                    extraction.SourceActorAliases,
                    CreditActors = extraction.CreditExtraction?.Credits,
                },
                people.Length == 0 ? "no_named_actor_claims"
                    : !observedSources.Contains(input.Uuid) ? "global_actor_index_unavailable"
                    : null,
                personKeys
            ),
            ShadowCatalogResult.For(
                input,
                "credits",
                credits.Length > 0 ? "proposed" : "unresolved",
                Version,
                new ShadowCreditsProposal(
                    credits,
                    creditIssues,
                    (extraction.CreditExtraction?.Credits ?? [])
                        .Where(credit => !HasDisplayName(credit.Name))
                        .ToArray()
                ),
                new { OriginalInputs = extraction.Inputs, extraction.CreditExtraction },
                credits.Length == 0 ? "no_named_role_claims" : null,
                credits
                    .Select(credit => credit.EntityKey)
                    .Concat(personKeys)
                    .Distinct()
                    .Order(StringComparer.Ordinal)
                    .ToArray()
            ),
        ];
    }

    private (
        IReadOnlyList<ShadowPersonRelationship> Links,
        ShadowPeopleRelationshipCoverage Coverage
    ) Relationships(ActorAccount actor)
    {
        var candidates = LookupKeys(actor.Match)
            .Where(accounts.ContainsKey)
            .SelectMany(key => accounts[key].Values)
            .DistinctBy(candidate => candidate.Person.EntityKey)
            .Where(candidate => candidate.Person.SourceUuid != actor.Person.SourceUuid)
            .OrderBy(candidate => candidate.Person.EntityKey, StringComparer.Ordinal)
            .ToArray();

        // Shared accounts can name hundreds of different actors. Bound comparison output,
        // retain every local claim, and report the exact omitted comparison count.
        var links = new List<ShadowPersonRelationship>();
        foreach (var candidate in candidates.Take(MaximumRelatedAccounts))
        {
            var comparison = matcher.ComparePeople(actor.Match, candidate.Match);
            var sameName = actor.Match.Names.Any(left =>
                candidate.Match.Names.Any(right =>
                    NormalizedPeopleService.NameKey(left.Wording)
                    == NormalizedPeopleService.NameKey(right.Wording)
                )
            );
            var uncertain =
                actor.Match.ActorContext?.HasAttachmentUncertainty == true
                || candidate.Match.ActorContext?.HasAttachmentUncertainty == true;
            var conflict = comparison.Features.Any(feature =>
                feature.Agreement == IdentityFeatureAgreement.Conflicts
            );
            var contactAgrees = comparison.Features.Any(feature =>
                feature.Name == "actor_contact"
                && feature.Agreement == IdentityFeatureAgreement.Agrees
            );
            var attributedProfileAgrees = ProfileKeys(actor.Match)
                .Intersect(ProfileKeys(candidate.Match))
                .Any();
            var supported =
                sameName && !uncertain && !conflict && (contactAgrees || attributedProfileAgrees);

            links.Add(
                new(
                    supported ? "proposed_same_person" : "unresolved_shared_account",
                    actor.Person.EntityKey,
                    candidate.Person.EntityKey,
                    candidate.Person.SourceUuid,
                    candidate.Person.ArtistUuid,
                    supported
                        ? "name_or_explicit_alias_and_attributed_account"
                        : "nonexclusive_account_requires_review",
                    comparison.Features,
                    candidate.Evidence
                )
            );
        }
        return (links, new(actor.Person.EntityKey, candidates.Length, links.Count));
    }

    private static IEnumerable<ActorAccount> Actors(NormalizationBatchExtraction extraction)
    {
        var seen = new HashSet<string>();
        foreach (var credit in extraction.CreditExtraction?.Credits ?? [])
        {
            var key = PersonKey(extraction.ResourceUuid, CreditReference(credit));
            if (!HasDisplayName(credit.Name) || !seen.Add(key))
            {
                continue;
            }

            var projected = PeopleActorProjection.Project(credit, extraction.ArtistUuid);
            var facts = credit.ActorFacts;
            var person = new ShadowPerson(
                key,
                credit.Name,
                facts?.Kind?.Kind,
                facts?.Aliases.Select(alias => alias.Wording).ToArray() ?? [],
                extraction.ResourceUuid,
                extraction.ArtistUuid,
                facts?.ReferenceUuid
            );
            // Coverage describes the parser's reach; it is not account ownership evidence.
            var citations = new[] { credit.Evidence }
                .Concat(facts?.Aliases.SelectMany(alias => alias.Evidence) ?? [])
                .Concat(facts?.Contacts.SelectMany(contact => contact.Evidence) ?? [])
                .Concat(facts?.Profiles.SelectMany(profile => profile.Evidence) ?? [])
                .Distinct()
                .ToArray();
            yield return new(person, projected.Record, citations);
        }

        foreach (var assertion in extraction.SourceActorAliases?.Assertions ?? [])
        {
            var key = PersonKey(extraction.ResourceUuid, "alias:" + assertion.ActorReferenceUuid);
            if (!HasDisplayName(assertion.Name) || !seen.Add(key))
            {
                continue;
            }

            var person = new ShadowPerson(
                key,
                assertion.Name,
                null,
                assertion.Aliases.Select(alias => alias.Wording).ToArray(),
                extraction.ResourceUuid,
                extraction.ArtistUuid,
                assertion.ActorReferenceUuid
            );
            yield return new(
                person,
                PeopleActorProjection.Project(assertion, extraction.ArtistUuid).Record,
                new[] { assertion.NameEvidence, assertion.Context.Statement }
                    .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
                    .Distinct()
                    .ToArray()
            );
        }
    }

    private static IEnumerable<string> LookupKeys(PersonMatchRecord actor)
    {
        foreach (var contact in actor.ActorContext?.Contacts ?? [])
        {
            if (PeopleActorLookupContract.Supports(contact))
            {
                yield return JsonSerializer.Serialize(
                    new[] { "contact", contact.ContactType, contact.LookupKey }
                );
            }
        }
        foreach (var profile in actor.ActorContext?.Profiles ?? [])
        {
            if (PeopleActorLookupContract.Supports(profile))
            {
                yield return JsonSerializer.Serialize(
                    new[]
                    {
                        "profile",
                        profile.Provider.ToLowerInvariant(),
                        profile.Namespace,
                        profile.LookupKey,
                    }
                );
            }
        }
    }

    private static IEnumerable<string> ProfileKeys(PersonMatchRecord actor) =>
        (actor.ActorContext?.Profiles ?? [])
            .Where(profile =>
                profile.Relationship != ActorProfileRelationship.CreditedProjectProfile
            )
            .Select(profile =>
                JsonSerializer.Serialize(
                    new[]
                    {
                        profile.Provider.ToLowerInvariant(),
                        profile.Namespace,
                        profile.LookupKey,
                    }
                )
            );

    private static string PersonKey(Guid sourceUuid, string reference) =>
        ShadowCatalogIdentity.Key("person", sourceUuid + ":" + reference);

    private static string CreditReference(CreditMention credit) =>
        credit.ActorFacts is { } facts
            ? "actor:" + facts.ActorUuid
            : "mention:" + JsonSerializer.Serialize(new { credit.Name, credit.Evidence }, Json);

    // Contacts can retrieve candidates, but are never a person's default public name.
    private static bool HasDisplayName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('@')
        && !name.Contains("://", StringComparison.Ordinal)
        && !name.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    private static NormalizationBatchExtraction ReadExtraction(JsonElement value) =>
        value.Deserialize<NormalizationBatchExtraction>(Json)
        ?? throw new FormatException("Missing source extraction.");

    private static NormalizationBatchExtraction Extract(ShadowCatalogInput input)
    {
        var fields = Fields.ToDictionary(
            field => field,
            field =>
                input.Row.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null
        );
        return new(
            input.Kind,
            input.Row.GetProperty("id").GetInt64(),
            input.Uuid,
            input.Artist.Id,
            input.Artist.Uuid,
            null,
            fields,
            null,
            new RecordingCreditExtractor().Extract(fields),
            null,
            new SourceActorAliasExtractor().Extract(fields)
        );
    }

    private sealed record ActorAccount(
        ShadowPerson Person,
        PersonMatchRecord Match,
        IReadOnlyList<NormalizationTextEvidence> Evidence
    );
}

/// <summary>Source-scoped proposed actor; EntityKey is never a canonical person UUID.</summary>
public sealed record ShadowPerson(
    string EntityKey,
    string Name,
    string? Kind,
    IReadOnlyList<string> Aliases,
    Guid SourceUuid,
    Guid ArtistUuid,
    Guid? SourceReferenceUuid
);

/// <summary>Account-supported comparison. Proposed links still require evaluated acceptance policy.</summary>
public sealed record ShadowPersonRelationship(
    string Kind,
    string FromEntityKey,
    string ToEntityKey,
    Guid TargetSourceUuid,
    Guid TargetArtistUuid,
    string Method,
    IReadOnlyList<IdentityMatchingFeature> Features,
    IReadOnlyList<NormalizationTextEvidence> TargetEvidence
);

public sealed record ShadowPeopleProposal(
    IReadOnlyList<ShadowPerson> People,
    IReadOnlyList<ShadowPersonRelationship> Relationships,
    IReadOnlyList<ShadowPeopleRelationshipCoverage> RelationshipCoverage,
    bool GlobalIndexIncludesSource
);

/// <summary>Counts semantic account candidates, not accepted identities or all catalog people.</summary>
public sealed record ShadowPeopleRelationshipCoverage(
    string PersonEntityKey,
    int AvailableAccounts,
    int ComparedAccounts
)
{
    public bool Complete => AvailableAccounts == ComparedAccounts;
}

/// <summary>Quoted role on the native source; no artist attendance or lineage relation is inferred.</summary>
public sealed record ShadowRecordingCredit(
    string EntityKey,
    string PersonEntityKey,
    Guid SourceUuid,
    Guid ArtistUuid,
    string Name,
    RecordingCreditRole Role,
    string? OriginalRole,
    NormalizationTextEvidence Evidence
);

public sealed record ShadowCreditsProposal(
    IReadOnlyList<ShadowRecordingCredit> Credits,
    IReadOnlyList<RecordingExtractionUnresolved> Unresolved,
    IReadOnlyList<CreditMention> UnassignedCredits
);
