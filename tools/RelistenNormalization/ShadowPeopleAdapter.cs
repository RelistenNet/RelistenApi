using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Projects quoted recording credits and role-free aliases into shadow people. The global
/// index retrieves explicit names, aliases and attributed contact/profile accounts across
/// artists. Pair scores and whole-pool decisions remain proposals. Every credited actor
/// retains its original source ownership; no identity is accepted by this adapter.
/// </summary>
public sealed partial class ShadowPeopleAdapter : IShadowCatalogAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
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
    private readonly ShadowLunaCreditEvidence? luna;
    private readonly ShadowPeopleIdentityModel? identityModel;

    public ShadowPeopleAdapter() { }

    /// <summary>Load optional pinned Luna evidence and an offline trained identity-ranking model.</summary>
    /// <param name="identityModelPath">Optional &lt;models-path&gt;#sha256=&lt;digest&gt; of the trainer's portable model. Scores provide ranking, never acceptance authority.</param>
    public ShadowPeopleAdapter(
        string? lunaOutputDirectory,
        string? expectedExtractionSha256,
        string? identityModelPath = null
    )
    {
        identityModel =
            identityModelPath == null ? null : new ShadowPeopleIdentityModel(identityModelPath);
        if (lunaOutputDirectory is null && expectedExtractionSha256 is null)
        {
            return;
        }
        if (lunaOutputDirectory is null || expectedExtractionSha256 is null)
        {
            throw new ArgumentException(
                "Luna output and its expected extraction SHA-256 are both required."
            );
        }
        luna = new(lunaOutputDirectory, expectedExtractionSha256);
    }

    public string Version =>
        "shadow-people-v3/global-retrieval="
        + PeopleActorProjection.Version
        + "/"
        + IdentityCandidateMatcher.PeopleActorFeaturesVersion
        + (luna is null ? "" : "/luna:" + luna.Identity)
        + "/model:"
        + (identityModel?.Identity ?? "none");

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind == "source_sample" ? Domains : [];

    /// <summary>
    /// The runner supplies extraction records as well as raw rows during its first pass.
    /// Raw rows have no resourceUuid and do not add a second copy of an extraction.
    /// Index entries retain compact semantic facts and two deterministic distinct-source
    /// witnesses per artist. Retrieval chooses one outside the incoming source; repeated
    /// descriptions cannot expand the pool or hide the representative's counterpart.
    /// </summary>
    public void Observe(JsonElement record)
    {
        if (!record.TryGetProperty("kind", out var kind) || kind.GetString() != "source_sample")
        {
            return;
        }
        if (record.TryGetProperty("row", out _))
        {
            luna?.ObserveRaw(record);
            return;
        }
        if (!record.TryGetProperty("resourceUuid", out _))
        {
            return;
        }

        var extraction = ReadExtraction(record);
        if (!observedSources.Add(extraction.ResourceUuid))
        {
            return;
        }

        var model = luna?.Resolve(extraction);
        foreach (var actor in LocalActors(extraction).Concat(ModelActors(extraction, model)))
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
            signature =
                (actor.ModelDerived ? "model:" : "local:")
                + actor.ProjectionComplete
                + ":"
                + signature;
            foreach (var lookup in LookupKeys(actor.Match))
            {
                Remember(accounts, lookup, signature, actor);
            }
            foreach (var name in actor.Match.Names)
            {
                Remember(names, NormalizedPeopleService.NameKey(name.Wording), signature, actor);
            }
        }
    }

    private static void Remember(
        Dictionary<string, Dictionary<string, ActorWitnesses>> index,
        string key,
        string signature,
        ActorAccount actor
    )
    {
        if (!index.TryGetValue(key, out var bucket))
        {
            bucket = new();
            index.Add(key, bucket);
        }
        if (!bucket.TryGetValue(signature, out var witnesses))
        {
            witnesses = new ActorWitnesses(actor);
            bucket.Add(signature, witnesses);
        }
        else
        {
            witnesses.Remember(actor);
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
        var model = luna?.Resolve(extraction, input.Row);
        var modelActors = ModelActors(extraction, model).ToArray();
        var actors = LocalActors(extraction).Concat(modelActors).ToArray();
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
        var modelCredits = ModelCredits(extraction, model).ToArray();
        credits = credits.Concat(modelCredits).ToArray();
        var modelInterpretation = model is null
            ? null
            : new ShadowLunaCreditInterpretation(
                model.State,
                model.Reason,
                model.Basis,
                modelActors.Select(actor => actor.Person.EntityKey).ToArray(),
                modelCredits.Select(credit => credit.EntityKey).ToArray()
            );
        var personKeys = people
            .Select(person => person.EntityKey)
            .Concat(
                comparisons
                    .SelectMany(result => result.Decision.CandidateOutcomes)
                    .Select(candidate => candidate.CandidateEntityKey)
            )
            // Shared name keys are retrieval groups, never canonical identity keys.
            .Concat(
                actors
                    .SelectMany(actor => actor.Match.Names)
                    .Select(name =>
                        ShadowCatalogIdentity.Key(
                            "person_name",
                            NormalizedPeopleService.NameKey(name.Wording)
                        )
                    )
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
                    observedSources.Contains(input.Uuid),
                    modelInterpretation,
                    comparisons.Select(result => result.Decision).ToArray()
                ),
                new
                {
                    OriginalInputs = extraction.Inputs,
                    extraction.SourceActorAliases,
                    CreditActors = extraction.CreditExtraction?.Credits,
                    Model = ModelEvidence(model),
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
                        .ToArray(),
                    modelInterpretation
                ),
                new
                {
                    OriginalInputs = extraction.Inputs,
                    extraction.CreditExtraction,
                    Model = ModelEvidence(model),
                },
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

    private static IEnumerable<ActorAccount> LocalActors(NormalizationBatchExtraction extraction)
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
            yield return new(
                person,
                projected.Record,
                citations,
                ProjectionComplete: projected.Complete
            );
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
            var projected = PeopleActorProjection.Project(assertion, extraction.ArtistUuid);
            yield return new(
                person,
                projected.Record,
                new[] { assertion.NameEvidence, assertion.Context.Statement }
                    .Concat(assertion.Aliases.SelectMany(alias => alias.Evidence))
                    .Distinct()
                    .ToArray(),
                ProjectionComplete: projected.Complete
            );
        }
    }

    private static IEnumerable<ActorAccount> ModelActors(
        NormalizationBatchExtraction extraction,
        ShadowLunaCreditMatch? model
    )
    {
        var seen = new HashSet<string>();
        foreach (var credit in model?.Extraction?.Credits ?? [])
        {
            var key = PersonKey(extraction.ResourceUuid, ModelReference(model!, credit));
            if (!seen.Add(key))
            {
                continue;
            }
            var person = new ShadowPerson(
                key,
                credit.Name,
                null,
                [],
                extraction.ResourceUuid,
                extraction.ArtistUuid,
                null,
                "retained_luna_extraction"
            );
            var projected = PeopleActorProjection.Project(credit, extraction.ArtistUuid);
            yield return new(person, projected.Record, [credit.Evidence], true, projected.Complete);
        }
    }

    private static IEnumerable<ShadowRecordingCredit> ModelCredits(
        NormalizationBatchExtraction extraction,
        ShadowLunaCreditMatch? model
    )
    {
        foreach (var credit in model?.Extraction?.Credits ?? [])
        {
            var reference = ModelReference(model!, credit);
            yield return new(
                ShadowCatalogIdentity.Key(
                    "credit",
                    extraction.ResourceUuid + ":" + reference + ":" + credit.Role
                ),
                PersonKey(extraction.ResourceUuid, reference),
                extraction.ResourceUuid,
                extraction.ArtistUuid,
                credit.Name,
                credit.Role,
                credit.OriginalRole,
                credit.Evidence,
                "retained_luna_extraction"
            );
        }
    }

    private static string ModelReference(ShadowLunaCreditMatch model, CreditMention credit) =>
        "luna:"
        + model.Basis.ExtractorVersion
        + ":"
        + model.Basis.CacheKey
        + ":"
        + CreditReference(credit);

    private static object? ModelEvidence(ShadowLunaCreditMatch? model) =>
        model is null
            ? null
            : new
            {
                model.Basis,
                model.State,
                model.Reason,
                OriginalLunaResult = model.OriginalResult,
                IdentityAndExhaustiveCoverageEstablished = false,
            };

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
        IReadOnlyList<NormalizationTextEvidence> Evidence,
        bool ModelDerived = false,
        bool ProjectionComplete = true
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
    Guid? SourceReferenceUuid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? InterpretationBasis = null
);

/// <summary>Source-actor comparison through the public matcher; name and account links remain unaccepted.</summary>
public sealed record ShadowPersonRelationship(
    string Kind,
    string FromEntityKey,
    string ToEntityKey,
    Guid TargetSourceUuid,
    Guid TargetArtistUuid,
    string Method,
    IReadOnlyList<IdentityMatchingFeature> Features,
    IReadOnlyList<NormalizationTextEvidence> TargetEvidence,
    IdentityCandidateComparison? Comparison = null,
    bool ModelDerivedEvidence = false
);

public sealed record ShadowPeopleProposal(
    IReadOnlyList<ShadowPerson> People,
    IReadOnlyList<ShadowPersonRelationship> Relationships,
    IReadOnlyList<ShadowPeopleRelationshipCoverage> RelationshipCoverage,
    bool GlobalIndexIncludesSource,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ShadowLunaCreditInterpretation? ModelInterpretation = null,
    IReadOnlyList<ShadowPeopleIdentityDecision>? IdentityDecisions = null
);

/// <summary>Counts compact matching-fact witnesses, not accepted identities or all catalog people.</summary>
public sealed record ShadowPeopleRelationshipCoverage(
    string PersonEntityKey,
    int AvailableAccounts,
    int ComparedAccounts,
    int NameCandidates = 0,
    int AccountCandidates = 0,
    int CrossArtistCandidates = 0,
    int DetailedRelationships = 0
)
{
    public bool Complete => AvailableAccounts == ComparedAccounts;
    public bool DetailedEvidenceComplete => DetailedRelationships == AvailableAccounts;
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
    NormalizationTextEvidence Evidence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? InterpretationBasis = null
);

public sealed record ShadowCreditsProposal(
    IReadOnlyList<ShadowRecordingCredit> Credits,
    IReadOnlyList<RecordingExtractionUnresolved> Unresolved,
    IReadOnlyList<CreditMention> UnassignedCredits,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ShadowLunaCreditInterpretation? ModelInterpretation = null
);
