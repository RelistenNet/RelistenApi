using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationSourceActorAliasApplications
{
    private const string Operator = "source-alias-application-fixture-operator";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationRecordingProposalService extractions = null!;
    private NormalizationRecordingProcessor extractor = null!;
    private NormalizationCandidateService candidates = null!;
    private PeopleResolutionPolicyService policies = null!;
    private SourceActorResolutionStore resolutions = null!;
    private SourceActorResolutionService resolver = null!;
    private SourceActorReviewService reviews = null!;
    private SourceActorAliasApplicationStore applications = null!;
    private ExpandedPublisher publisher = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        var evidence = new NormalizationEvidenceService(fixture.Db);
        extractions = new(fixture.Db);
        extractor = new(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            extractions,
            new(fixture.Db)
        );
        candidates = new(fixture.Db);
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        resolutions = new(fixture.Db);
        resolver = new(candidates, policies, new(new(), new()), resolutions, fixture.Work);
        reviews = new(fixture.Db, fixture.Work, new(fixture.Db));
        applications = new(fixture.Db);
        publisher = NewPublisher();
    }

    [SetUp, TearDown]
    public async Task LeaveNoActivePolicy()
    {
        if ((await policies.ReadStateAsync()).Active != null)
        {
            await SelectPolicy(null);
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    private SourceActorAliasApplicationService NewService() =>
        new(
            fixture.Db,
            resolutions,
            candidates,
            policies,
            new(new(), new()),
            applications,
            people,
            fixture.Work
        );

    private ExpandedPublisher NewPublisher() => new(NewService(), this);

    // Specifications deliberately fetch individual immutable stage proofs when asserting
    // their contents. Production orchestration and its application API return headers only.
    private async Task<ExpandedApplication?> ReadExpandedApplication(
        Guid uuid,
        CancellationToken ct = default
    )
    {
        var header = await applications.GetApplicationAsync(uuid, ct);
        return header == null ? null : await Expand(header, ct);
    }

    private async Task<ExpandedApplication> Expand(
        Relisten.Api.Models.Normalization.SourceActorAliasApplication header,
        CancellationToken ct
    )
    {
        var stages = new List<SourceActorAliasPersonStage>();
        foreach (var stage in header.Stages)
        {
            stages.Add((await applications.GetStageAsync(stage.StageUuid, ct))!);
        }
        return new(
            header.AttemptUuid,
            header.ResolutionAttemptUuid,
            header.SourceUuid,
            header.Version,
            header.Plan,
            stages,
            header.Result
        );
    }

    private sealed class ExpandedPublisher(
        SourceActorAliasApplicationService service,
        TestNormalizationSourceActorAliasApplications fixture
    )
    {
        public async Task<ExpandedApplication> ApplyAsync(
            Guid applicationUuid,
            Guid resolutionUuid,
            CancellationToken ct = default
        ) =>
            await fixture.Expand(await service.ApplyAsync(applicationUuid, resolutionUuid, ct), ct);
    }

    private sealed record ExpandedApplication(
        Guid AttemptUuid,
        Guid ResolutionAttemptUuid,
        Guid SourceUuid,
        string Version,
        SourceActorAliasApplicationPlan? Plan,
        IReadOnlyList<SourceActorAliasPersonStage> Stages,
        SourceActorAliasApplicationResult? Result
    );

    private async Task<Guid> Person(string name, string kind = "person")
    {
        var input = await fixture.Capture(name, "people");
        var uuid = Guid.NewGuid();
        await fixture.PublishPeople(people, input, new([new(uuid, name, kind)], [], [], []));
        return uuid;
    }

    private async Task<SourceContext> Source(string description)
    {
        var source = await fixture.Source();
        await SetDescription(source, description);
        return await Resolve(source);
    }

    private Task SetDescription(CatalogSource source, string? description) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description=@description,taper=NULL,transferrer=NULL,lineage=NULL,source=NULL,taper_notes=NULL WHERE id=@SourceId",
                new { description, source.SourceId }
            )
        );

    private async Task<SourceContext> Resolve(CatalogSource source)
    {
        var result = await extractor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var extraction = (await extractions.GetAttemptBatchAsync(result.AttemptUuid))!;
        var resolution = await resolver.ResolveAsync(Guid.NewGuid(), extraction.AttemptUuid);
        resolution
            .Result!.Disposition.Should()
            .Be(
                SourceActorResolutionDisposition.Published,
                "the source resolver returned {0}",
                resolution.Result.Reason
            );
        return new(source, extraction, resolution);
    }

    private async Task<SourceContext> Replace(SourceContext source, string? description)
    {
        await SetDescription(source.Source, description);
        return await Resolve(source.Source);
    }

    private Task<ExpandedApplication> Apply(SourceContext source, Guid? uuid = null) =>
        publisher.ApplyAsync(uuid ?? Guid.NewGuid(), source.Resolution.AttemptUuid);

    private static IEnumerable<SourceActorResolutionClaim> Claims(SourceContext source) =>
        source
            .Resolution.Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.Claims);

    private async Task Review(
        SourceContext source,
        SourceActorReviewAction action,
        Guid? target = null
    )
    {
        var claim = Claims(source).Single();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                SourceActorResolutionService.Owner(source.Source.SourceUuid)
            )
        )!;
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT coalesce(max(review_revision),0) FROM normalization.source_actor_alias_review_state WHERE source_uuid=@SourceUuid AND assertion_uuid=@AssertionUuid",
                claim.Coordinates
            )
        );
        var preparation = await reviews.PrepareAsync(
            Guid.NewGuid(),
            new(
                source.Extraction.AttemptUuid,
                claim.Coordinates,
                action,
                owner.EditorialRevision,
                revision,
                target,
                target == null ? null : await PersonRevision(target.Value)
            )
        );
        await reviews.ApplyAsync(
            Guid.NewGuid(),
            preparation.PreparationUuid,
            "Operator checked this whole source field and selected its original identity.",
            Operator
        );
    }

    private async Task<PersonAlias> ReviewCanonicalAlias(
        Guid personUuid,
        string wording,
        string action
    )
    {
        var person = new NormalizationSubjectKey("people", "person", personUuid);
        var membership = await fixture.Work.GetInputContributionsAsync(person, new(100, 100000));
        var baseline =
            membership.UnmanagedBaseline
            ?? membership.Contributions.Single(value =>
                value.Contributor.Provider == "relisten"
                && value.Contributor.ResourceType == "preexisting_owner_bindings"
            );
        var resource = baseline.Resources.Single();
        var evidenceService = new NormalizationEvidenceService(fixture.Db);
        var original = (await evidenceService.GetCurrentInputAsync(resource))!;
        // A curated native input is already bound to this person. Capture its new wording
        // without replacing the person's complete source-contributor configuration.
        var captured = await evidenceService.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { text = wording }),
                "application/json",
                "test",
                original.Generation,
                DateTimeOffset.UtcNow
            )
        );
        var evidence = new NormalizedEvidenceReference(
            captured.Revision.Uuid,
            "/text",
            0,
            wording.EnumerateRunes().Count(),
            wording
        );
        var alias = new PersonAlias(Guid.NewGuid(), personUuid, wording, evidence);
        var owner = (await fixture.Work.GetSubjectAsync(person))!;
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                personUuid,
                await PersonRevision(personUuid),
                "accept",
                "Explicit canonical alias review",
                Alias: alias,
                ExpectedAssertionRevision: 0
            ),
            Operator
        );
        if (action != "accept")
        {
            owner = (await fixture.Work.GetSubjectAsync(person))!;
            var assertionRevision = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<long>(
                    "SELECT revision FROM normalization.person_aliases WHERE uuid=@Uuid",
                    alias
                )
            );
            await people.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    personUuid,
                    await PersonRevision(personUuid),
                    action,
                    "Canonical natural claim must remain protected",
                    Alias: alias,
                    ExpectedAssertionRevision: assertionRevision
                ),
                Operator
            );
        }
        return alias;
    }

    private Task<long> PersonRevision(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<AliasRow[]> CurrentAliases(Guid personUuid) =>
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<AliasRow>(
                    "SELECT uuid AS \"Uuid\",wording AS \"Wording\" FROM normalization.current_person_aliases WHERE person_uuid=@personUuid ORDER BY uuid",
                    new { personUuid }
                )
            )
        ).ToArray();

    private Task<int> ActiveAssignments(Guid sourceUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.source_actor_alias_assignments WHERE source_uuid=@sourceUuid AND retired_work_uuid IS NULL",
                new { sourceUuid }
            )
        );

    private Task<string> AcceptedAuthority() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'aliases',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY uuid),'[]') FROM normalization.person_aliases a),
                    'assignments',(SELECT coalesce(jsonb_agg(to_jsonb(a) ORDER BY uuid),'[]') FROM normalization.source_actor_alias_assignments a),
                    'support',(SELECT coalesce(jsonb_agg(to_jsonb(s) ORDER BY uuid),'[]') FROM normalization.person_alias_contributor_support s),
                    'unmanaged',(SELECT coalesce(jsonb_agg(to_jsonb(u) ORDER BY uuid),'[]') FROM normalization.person_alias_unmanaged_support u))::text
                """
            )
        );

    private Task<string> NonAliasState() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT coalesce(jsonb_agg(jsonb_build_array(uuid,display_name,kind) ORDER BY uuid),'[]') FROM normalization.people),
                    'identifiers',(SELECT coalesce(jsonb_agg(to_jsonb(i) ORDER BY uuid),'[]') FROM normalization.person_external_identities i),
                    'credits',(SELECT coalesce(jsonb_agg(to_jsonb(c) ORDER BY uuid),'[]') FROM normalization.recording_credits c),
                    'playback',(SELECT coalesce(jsonb_agg(jsonb_build_array(tr.uuid,tr.mp3_url) ORDER BY tr.uuid),'[]') FROM source_tracks tr))::text
                """
            )
        );

    private static IEnumerable<SourceActorAliasStageReceipt> Receipts(
        ExpandedApplication application
    ) => application.Stages.Where(stage => stage.Receipt != null).Select(stage => stage.Receipt!);

    private static string Personnel(string name, string alias) =>
        $"Personnel:\n{name} aka {alias} - vocals";

    private static string Unique(string stem) => stem + " " + Guid.NewGuid().ToString("N");

    private async Task SelectPolicy(RegisteredPeopleAcceptancePolicy? policy)
    {
        if (policy != null)
        {
            await policies.RegisterEvaluatedPolicyAsync(
                policy.Artifact,
                policy.Evaluation,
                Operator
            );
        }
        var state = await policies.ReadStateAsync();
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.OwnerObservation.SubjectUuid,
                state.OwnerObservation.EditorialRevision,
                state.Active?.Policy.Artifact.Uuid,
                policy?.Artifact.Uuid,
                "Select separately qualified source alias fixture policy"
            ),
            Operator
        );
    }

    private static RegisteredPeopleAcceptancePolicy Policy()
    {
        var applicability = new SourceActorAliasPolicyApplicability(
            PeopleResolutionPolicyEvaluator.SourceInputForm,
            NormalizationRecordingProcessor.Version,
            NormalizationRecordingExtractor.Version,
            SourceActorAliasExtractor.Version,
            PeopleActorProjection.SourceAliasVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            PeopleResolutionPolicyEvaluator.ScorerVersion
        );
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "source-application-fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            [PeopleResolutionPolicyEvaluator.SourceInputForm],
            new(0, 1, 1, 1, 2, 0.5),
            FixtureOnly: true,
            SourceActorAliasApplicability: applicability
        );
        artifact = artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-credit-evaluator-v1",
            "fixture-credit-reviewer",
            Operator,
            new(100, 0),
            FixtureOnly: true,
            SourceActorAliasEvaluation: new(
                PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(applicability),
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "fixture-source-evaluator-v1",
                "fixture-source-reviewer",
                new(100, 0)
            )
        );
        evaluation = evaluation with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        return new(artifact, evaluation);
    }

    private sealed record SourceContext(
        CatalogSource Source,
        NormalizationRecordingProposalBatch Extraction,
        SourceActorResolutionAttempt Resolution
    );

    private sealed record AliasRow(Guid Uuid, string Wording);
}
