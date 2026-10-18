using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Extracts people and role proposals from native provider recording prose. Original input,
/// recording and file coordinates stay distinct from optional historical catalog links.
/// Actor references and account agreements grant no cross-recording identity authority.
/// </summary>
public sealed class ShadowNativePeopleAdapter(ShadowProviderObservations? observations = null)
    : IShadowCatalogAdapter
{
    private static readonly string[] Domains = ["people", "credits"];
    private readonly ShadowProviderObservations observations = observations ?? new();
    private readonly RetainedRecordingCreditExtractor extractor = new();

    public string Version =>
        "shadow-native-people-v1/"
        + ShadowProviderObservations.Version
        + "/"
        + RetainedRecordingCreditExtractor.Version
        + "/"
        + PeopleActorProjection.Version;

    public IReadOnlyList<string> DomainsFor(string inputKind) =>
        inputKind == "provider_input" ? Domains : [];

    public void Observe(JsonElement record) => observations.Observe(record);

    public IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input)
    {
        if (input.Kind != "provider_input")
        {
            return [];
        }
        try
        {
            return Project(input, observations.Read(input));
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or InvalidDataException
                        or NotSupportedException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            return Domains
                .Select(domain =>
                    ShadowCatalogResult.For(
                        input,
                        domain,
                        "failed",
                        Version,
                        null,
                        new { input_revision_uuid = input.Uuid, error_type = error.GetType().Name },
                        error is NotSupportedException
                            ? "unsupported_provider_capture"
                            : "invalid_retained_provider_input"
                    )
                )
                .ToArray();
        }
    }

    private ShadowCatalogResult[] Project(
        ShadowCatalogInput input,
        ShadowProviderObservationSet decoded
    )
    {
        var people = new Dictionary<string, ShadowNativePerson>(StringComparer.Ordinal);
        var credits = new List<ShadowNativeRecordingCredit>();
        var unresolved = new List<ShadowNativeCreditIssue>();
        foreach (var recording in decoded.Recordings)
        {
            foreach (var scope in CreditScopes(decoded.Input, recording))
            {
                ExtractScope(input, recording, scope, people, credits, unresolved);
            }
        }

        var reason =
            decoded.Recordings.Count == 0
                ? "provider_input_has_no_recording_account"
                : decoded.Issues.FirstOrDefault()?.Code
                    ?? (credits.Count == 0 ? "no_native_credit_mentions" : null);
        var outcome =
            decoded.Recordings.Count == 0 ? "not_applicable"
            : people.Count == 0 ? "unresolved"
            : "proposed";
        var evidence = new
        {
            input_revision_uuid = decoded.Input.Uuid,
            resource = decoded.Input.Resource,
            decoded.Decoded.DecoderVersion,
            decoded.Decoded.Issues,
            input_issues = decoded.Issues,
            interpretation = "native_recording_credit_proposals",
        };
        var keys = people
            .Keys.Concat(credits.Select(credit => credit.EntityKey))
            .Concat(
                people
                    .Values.SelectMany(person => person.Projection.Record.Names)
                    .Select(name =>
                        ShadowCatalogIdentity.Key(
                            "person_name",
                            NormalizedPeopleService.NameKey(name.Wording)
                        )
                    )
            )
            .Concat(
                decoded.Recordings.SelectMany(recording =>
                    new[]
                    {
                        recording.CatalogSourceUuid is { } source
                            ? "source:" + source.ToString("D")
                            : null,
                        recording.CatalogShowUuid is { } show ? "show:" + show.ToString("D") : null,
                    }.OfType<string>()
                )
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return
        [
            ShadowCatalogResult.For(
                input,
                "people",
                outcome,
                Version,
                new ShadowNativePeopleProposal(people.Values.ToArray(), unresolved, decoded.Issues),
                evidence,
                reason,
                keys
            ),
            ShadowCatalogResult.For(
                input,
                "credits",
                outcome,
                Version,
                new ShadowNativeCreditsProposal(credits, unresolved, decoded.Issues),
                evidence,
                reason,
                keys
            ),
        ];
    }

    private void ExtractScope(
        ShadowCatalogInput input,
        ShadowProviderRecordingObservation recording,
        CreditScope scope,
        Dictionary<string, ShadowNativePerson> people,
        List<ShadowNativeRecordingCredit> credits,
        List<ShadowNativeCreditIssue> unresolved
    )
    {
        var extracted = extractor.Extract(scope.Input, scope.Text);
        foreach (var issue in extracted.Unresolved)
        {
            unresolved.Add(
                new(
                    issue.Reason,
                    scope.Input.Uuid,
                    scope.RecordingOrigin,
                    scope.FileOrigin,
                    issue.Evidence,
                    recording.CatalogSourceUuid
                )
            );
        }
        foreach (var mention in extracted.Credits)
        {
            var personKey = ShadowCatalogIdentity.Key(
                "native_actor",
                JsonSerializer.Serialize(
                    new
                    {
                        scope.Input.Uuid,
                        scope.RecordingOrigin,
                        scope.FileOrigin,
                        reference = mention.ActorFacts?.ReferenceUuid,
                        mention.Name,
                        mention.Evidence.JsonPointer,
                        mention.Evidence.StartCodePoint,
                        mention.Evidence.EndCodePoint,
                    }
                )
            );
            if (!people.ContainsKey(personKey))
            {
                var projection = PeopleActorProjection.Project(mention, input.Artist.Uuid);
                if (input.Artist.Uuid == Guid.Empty)
                {
                    projection = projection with
                    {
                        Record = projection.Record with { ArtistUuids = new HashSet<Guid>() },
                    };
                }
                people.Add(
                    personKey,
                    new(
                        personKey,
                        mention.Name,
                        mention.ActorFacts?.Kind?.Kind,
                        mention.ActorFacts?.Aliases.Select(alias => alias.Wording).ToArray() ?? [],
                        scope.Input.Uuid,
                        scope.RecordingOrigin,
                        scope.FileOrigin,
                        recording.CatalogSourceUuid,
                        recording.CatalogShowUuid,
                        recording.CatalogBindingReason,
                        new[] { mention.Evidence }
                            .Concat(mention.ActorFacts?.Citations() ?? [])
                            .Distinct()
                            .ToArray(),
                        mention.ActorFacts,
                        projection
                    )
                );
            }
            var creditKey = ShadowCatalogIdentity.Key(
                "native_credit",
                JsonSerializer.Serialize(
                    new
                    {
                        personKey,
                        mention.Role,
                        mention.OriginalRole,
                        mention.Evidence,
                    }
                )
            );
            credits.Add(
                new(
                    creditKey,
                    personKey,
                    mention.Name,
                    mention.Role,
                    mention.OriginalRole,
                    scope.Input.Uuid,
                    scope.RecordingOrigin,
                    scope.FileOrigin,
                    mention.Evidence,
                    recording.CatalogSourceUuid,
                    recording.CatalogShowUuid,
                    scope.CatalogTrackUuid,
                    recording.CatalogBindingReason
                )
            );
        }
    }

    private static IEnumerable<CreditScope> CreditScopes(
        NormalizationInputRevision input,
        ShadowProviderRecordingObservation context
    )
    {
        yield return new(input, context.Recording.Origin, null, context.Recording.Text, null);
        foreach (var file in context.Recording.Files)
        {
            // A file assertion stays file-scoped even when no current track can be linked.
            var binding = context.CatalogFiles.SingleOrDefault(binding =>
                binding.Origin == file.Origin
            );
            yield return new(
                input,
                context.Recording.Origin,
                file.Origin,
                file.Text,
                binding?.TrackUuid
            );
        }
        if (context.SupportingRootInput is { } root && context.SupportingRootAccount is { } account)
        {
            yield return new(root, account.Origin, null, account.Text, null);
        }
    }

    private sealed record CreditScope(
        NormalizationInputRevision Input,
        ProviderObservationOrigin RecordingOrigin,
        ProviderObservationOrigin? FileOrigin,
        IReadOnlyList<ProviderTextObservation> Text,
        Guid? CatalogTrackUuid
    );
}

/// <summary>A source-account actor proposal with citations in original retained input coordinates.</summary>
public sealed record ShadowNativePerson(
    string EntityKey,
    string Name,
    string? Kind,
    IReadOnlyList<string> Aliases,
    Guid InputRevisionUuid,
    ProviderObservationOrigin RecordingOrigin,
    ProviderObservationOrigin? FileOrigin,
    Guid? CatalogSourceUuid,
    Guid? CatalogShowUuid,
    string? CatalogBindingReason,
    IReadOnlyList<NormalizedEvidenceReference> Evidence,
    CreditedActorFacts<NormalizedEvidenceReference>? ActorFacts,
    PeopleActorProjectionResult Projection,
    bool AutomaticAcceptanceAuthorized = false
);

/// <summary>Observed role wording; nullable catalog UUIDs never change the original recording/file scope.</summary>
public sealed record ShadowNativeRecordingCredit(
    string EntityKey,
    string PersonEntityKey,
    string Name,
    RecordingCreditRole Role,
    string? OriginalRole,
    Guid InputRevisionUuid,
    ProviderObservationOrigin RecordingOrigin,
    ProviderObservationOrigin? FileOrigin,
    NormalizedEvidenceReference Evidence,
    Guid? CatalogSourceUuid,
    Guid? CatalogShowUuid,
    Guid? CatalogTrackUuid,
    string? CatalogBindingReason,
    bool AutomaticAcceptanceAuthorized = false
);

public sealed record ShadowNativeCreditIssue(
    string Reason,
    Guid InputRevisionUuid,
    ProviderObservationOrigin RecordingOrigin,
    ProviderObservationOrigin? FileOrigin,
    NormalizedEvidenceReference Evidence,
    Guid? CatalogSourceUuid
);

public sealed record ShadowNativePeopleProposal(
    IReadOnlyList<ShadowNativePerson> People,
    IReadOnlyList<ShadowNativeCreditIssue> Unresolved,
    IReadOnlyList<ShadowProviderObservationIssue> InputIssues
);

public sealed record ShadowNativeCreditsProposal(
    IReadOnlyList<ShadowNativeRecordingCredit> Credits,
    IReadOnlyList<ShadowNativeCreditIssue> Unresolved,
    IReadOnlyList<ShadowProviderObservationIssue> InputIssues
);
