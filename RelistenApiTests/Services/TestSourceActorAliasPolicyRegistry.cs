using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable]
public sealed class TestSourceActorAliasPolicyRegistry
{
    private const string Operator = "source-alias-fixture-operator";

    [TestCase(false)]
    [TestCase(true)]
    public void SeparateSourceEvidenceQualifiesSourceOnlyAndCombinedArtifacts(bool combined)
    {
        var policy = Policy(combined);

        PeopleResolutionPolicyService.SourceActorQualificationIsValid(policy).Should().BeTrue();
        policy.Evaluation.SourceActorAliasEvaluation!.Matching.Cases.Should().Be(37);
        policy.Evaluation.Matching.Cases.Should().Be(100);
        policy
            .Evaluation.SourceActorAliasEvaluation.ApplicabilityHash.Should()
            .Be(
                PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(
                    policy.Artifact.SourceActorAliasApplicability!
                )
            );
    }

    [TestCase("credit_only")]
    [TestCase("declared_without_tuple")]
    [TestCase("declared_without_source_evaluation")]
    [TestCase("tuple_without_input_form")]
    [TestCase("evaluation_without_tuple")]
    public void CreditAttestationAndInputFormStringsCannotSupplySourceQualification(string missing)
    {
        var policy = Policy();
        policy = missing switch
        {
            "credit_only" => policy with
            {
                Artifact = policy.Artifact with
                {
                    InputForms = ["retained_credit"],
                    SourceActorAliasApplicability = null,
                },
                Evaluation = policy.Evaluation with { SourceActorAliasEvaluation = null },
            },
            "declared_without_tuple" => policy with
            {
                Artifact = policy.Artifact with { SourceActorAliasApplicability = null },
            },
            "evaluation_without_tuple" => policy with
            {
                Artifact = policy.Artifact with
                {
                    InputForms = ["retained_credit"],
                    SourceActorAliasApplicability = null,
                },
            },
            "declared_without_source_evaluation" => policy with
            {
                Evaluation = policy.Evaluation with { SourceActorAliasEvaluation = null },
            },
            _ => policy with
            {
                Artifact = policy.Artifact with { InputForms = ["retained_credit"] },
            },
        };
        policy = Rehash(policy);

        PeopleResolutionPolicyService.SourceActorQualificationIsValid(policy).Should().BeFalse();
    }

    [TestCase("processing")]
    [TestCase("extraction")]
    [TestCase("assertion")]
    [TestCase("projection")]
    [TestCase("facts")]
    [TestCase("retrieval")]
    [TestCase("scorer")]
    public void HistoricalSourceTuplesRemainStructurallyReadableButCannotQualifyCurrentMatching(
        string contract
    )
    {
        var policy = Policy();
        var tuple = policy.Artifact.SourceActorAliasApplicability!;
        tuple = contract switch
        {
            "processing" => tuple with { RecordingProcessingVersion = "historical-processing-v0" },
            "extraction" => tuple with { ExtractionVersion = "historical-extraction-v0" },
            "assertion" => tuple with { AssertionVersion = "historical-assertion-v0" },
            "projection" => tuple with { ProjectionVersion = "historical-projection-v0" },
            "facts" => tuple with { CandidateFactsVersion = "historical-facts-v0" },
            "retrieval" => tuple with { RetrievalVersion = "historical-retrieval-v0" },
            _ => tuple with { ScorerVersion = "historical-scorer-v0" },
        };
        policy = policy with
        {
            Artifact = policy.Artifact with
            {
                SourceActorAliasApplicability = tuple,
                ExtractionVersion = tuple.ExtractionVersion,
                CandidateFactsVersion = tuple.CandidateFactsVersion,
                RetrievalVersion = tuple.RetrievalVersion,
                ScorerVersion = tuple.ScorerVersion,
            },
            Evaluation = policy.Evaluation with
            {
                SourceActorAliasEvaluation = policy.Evaluation.SourceActorAliasEvaluation! with
                {
                    ApplicabilityHash =
                        PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(tuple),
                },
            },
        };
        policy = Rehash(policy);

        PeopleResolutionPolicyService.SourceActorQualificationIsValid(policy).Should().BeFalse();
        PeopleResolutionPolicyService
            .SourceActorQualificationIsValid(policy, requireCurrentVersions: false)
            .Should()
            .BeTrue();
    }

    [TestCase("extraction")]
    [TestCase("facts")]
    [TestCase("retrieval")]
    [TestCase("scorer")]
    public void EvenHistoricalReadRequiresTupleAndArtifactVersionAgreement(string contract)
    {
        var policy = Policy();
        var tuple = policy.Artifact.SourceActorAliasApplicability!;
        tuple = contract switch
        {
            "extraction" => tuple with { ExtractionVersion = "different-extraction" },
            "facts" => tuple with { CandidateFactsVersion = "different-facts" },
            "retrieval" => tuple with { RetrievalVersion = "different-retrieval" },
            _ => tuple with { ScorerVersion = "different-scorer" },
        };
        policy = Rehash(
            policy with
            {
                Artifact = policy.Artifact with { SourceActorAliasApplicability = tuple },
                Evaluation = policy.Evaluation with
                {
                    SourceActorAliasEvaluation = policy.Evaluation.SourceActorAliasEvaluation! with
                    {
                        ApplicabilityHash =
                            PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(
                                tuple
                            ),
                    },
                },
            }
        );

        PeopleResolutionPolicyService
            .SourceActorQualificationIsValid(policy, requireCurrentVersions: false)
            .Should()
            .BeFalse();
    }

    [TestCase("source_hash")]
    [TestCase("source_counts")]
    [TestCase("source_evaluator")]
    [TestCase("source_reviewer")]
    [TestCase("source_dataset")]
    [TestCase("source_split")]
    [TestCase("source_adjudication")]
    public void SourceEvidenceRemainsRequiredWhenCreditEvaluationIsValid(string invalid)
    {
        var policy = Policy();
        var source = policy.Evaluation.SourceActorAliasEvaluation!;
        source = invalid switch
        {
            "source_hash" => source with { ApplicabilityHash = new string('f', 64) },
            "source_counts" => source with { Matching = new(0, 0) },
            "source_evaluator" => source with { EvaluatorVersion = " " },
            "source_reviewer" => source with { IndependentEvaluator = " " },
            "source_dataset" => source with { DatasetHash = "" },
            "source_split" => source with { SplitHash = "" },
            _ => source with { AdjudicationHash = "" },
        };
        policy = Rehash(
            policy with
            {
                Evaluation = policy.Evaluation with { SourceActorAliasEvaluation = source },
            }
        );

        PeopleResolutionPolicyService.SourceActorQualificationIsValid(policy).Should().BeFalse();
        PeopleResolutionPolicyService
            .SourceActorQualificationIsValid(policy, requireCurrentVersions: false)
            .Should()
            .BeFalse();
    }

    [TestCase("artifact_payload")]
    [TestCase("artifact_link")]
    [TestCase("artifact_uuid")]
    [TestCase("receipt_payload")]
    public void CallerAssembledQualificationCannotBypassEnclosingHashes(string invalid)
    {
        var policy = Policy();
        policy = invalid switch
        {
            "artifact_payload" => policy with
            {
                Artifact = policy.Artifact with { Version = "tampered" },
            },
            "artifact_link" => policy with
            {
                Evaluation = policy.Evaluation with { ArtifactHash = new string('f', 64) },
            },
            "artifact_uuid" => policy with
            {
                Evaluation = policy.Evaluation with { ArtifactUuid = Guid.NewGuid() },
            },
            _ => policy with
            {
                Evaluation = policy.Evaluation with { ApprovedBy = "tampered-operator" },
            },
        };

        PeopleResolutionPolicyService.SourceActorQualificationIsValid(policy).Should().BeFalse();
    }

    [Test, Category("LocalIngestion")]
    public async Task PublicRegistryReadsExactReplayWithoutActivatingOrReplacingThePolicy()
    {
        using var fixture = new NormalizationDomainFixture();
        var registry = new PeopleResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        var before = await registry.ReadActiveAsync();
        var policy = Policy();
        var registered = await registry.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );
        var replay = await registry.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );

        JsonSerializer.Serialize(registered).Should().Be(JsonSerializer.Serialize(policy));
        JsonSerializer.Serialize(replay).Should().Be(JsonSerializer.Serialize(policy));
        JsonSerializer
            .Serialize(await registry.ReadActiveAsync())
            .Should()
            .Be(JsonSerializer.Serialize(before));
        var changed = Rehash(
            policy with
            {
                Evaluation = policy.Evaluation with
                {
                    SourceActorAliasEvaluation = policy.Evaluation.SourceActorAliasEvaluation! with
                    {
                        Matching = new(38, 0),
                    },
                },
            }
        );
        var register = () =>
            registry.RegisterEvaluatedPolicyAsync(changed.Artifact, changed.Evaluation, Operator);
        await register.Should().ThrowAsync<NormalizationConflictException>();
        var unchanged = await registry.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );
        JsonSerializer.Serialize(unchanged).Should().Be(JsonSerializer.Serialize(policy));
    }

    [Test, Category("LocalIngestion")]
    public async Task PublicRegistryRefusesDeclaredSourceWithoutSeparateQualification()
    {
        using var fixture = new NormalizationDomainFixture();
        var registry = new PeopleResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        var policy = Policy();
        policy = Rehash(
            policy with
            {
                Evaluation = policy.Evaluation with { SourceActorAliasEvaluation = null },
            }
        );
        var register = () =>
            registry.RegisterEvaluatedPolicyAsync(policy.Artifact, policy.Evaluation, Operator);

        await register.Should().ThrowAsync<ArgumentException>();
        var stored = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.people_acceptance_policies WHERE uuid=@Uuid)",
                policy.Artifact
            )
        );
        stored.Should().BeFalse();
    }

    [Test, Category("LocalIngestion")]
    public async Task CreditOnlyCreationRegistrationKeepsItsExistingShapeAndCapability()
    {
        using var fixture = new NormalizationDomainFixture();
        var registry = new PeopleResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        var policy = Policy();
        policy = Rehash(
            policy with
            {
                Artifact = policy.Artifact with
                {
                    InputForms = ["retained_credit"],
                    SourceActorAliasApplicability = null,
                    Creation = new(PeopleResolutionPolicyEvaluator.CreationClassifierVersion, 0.25),
                },
                Evaluation = policy.Evaluation with
                {
                    SourceActorAliasEvaluation = null,
                    Creation = new(100, 0),
                    ActorKind = new(100, 0),
                },
            }
        );
        var before = await registry.ReadActiveAsync();

        var registered = await registry.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );
        var replay = await registry.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );

        JsonSerializer.Serialize(registered).Should().Be(JsonSerializer.Serialize(policy));
        JsonSerializer.Serialize(replay).Should().Be(JsonSerializer.Serialize(policy));
        JsonSerializer.Serialize(registered).Should().NotContain("SourceActorAlias");
        registered.Artifact.Creation.Should().Be(policy.Artifact.Creation);
        registered.Evaluation.ActorKind.Should().Be(policy.Evaluation.ActorKind);
        PeopleResolutionPolicyService
            .SourceActorQualificationIsValid(registered)
            .Should()
            .BeFalse();
        JsonSerializer
            .Serialize(await registry.ReadActiveAsync())
            .Should()
            .Be(JsonSerializer.Serialize(before));
    }

    [Test, Category("LocalIngestion")]
    public async Task CallerSnapshotRetainsOriginalPolicyWhileFreshReadsSeeReplacement()
    {
        using var fixture = new NormalizationDomainFixture();
        var registry = new PeopleResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        var original = await registry.ReadStateAsync();
        var first = Policy();
        var replacement = Policy();
        await registry.RegisterEvaluatedPolicyAsync(first.Artifact, first.Evaluation, Operator);
        await registry.RegisterEvaluatedPolicyAsync(
            replacement.Artifact,
            replacement.Evaluation,
            Operator
        );
        try
        {
            await SelectFixturePolicy(registry, first.Artifact.Uuid);
            await using var connection = fixture.Db.CreateConnection(false, false);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead
            );
            await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: transaction);
            var observed = await registry.ReadStateInSnapshotAsync(
                connection,
                transaction,
                CancellationToken.None
            );

            await SelectFixturePolicy(registry, replacement.Artifact.Uuid);
            var repeated = await registry.ReadStateInSnapshotAsync(
                connection,
                transaction,
                CancellationToken.None
            );
            var current = await registry.ReadStateAsync();

            repeated.Should().BeEquivalentTo(observed);
            repeated.Active!.Policy.Artifact.Uuid.Should().Be(first.Artifact.Uuid);
            current.Active!.Policy.Artifact.Uuid.Should().Be(replacement.Artifact.Uuid);
            current
                .OwnerObservation.EditorialRevision.Should()
                .BeGreaterThan(observed.OwnerObservation.EditorialRevision);
            await transaction.CommitAsync();
        }
        finally
        {
            await SelectFixturePolicy(registry, original.Active?.Policy.Artifact.Uuid);
        }
    }

    [Test, Category("LocalIngestion")]
    public async Task CallerSnapshotCannotBypassFixturePolicyRestrictions()
    {
        using var fixture = new NormalizationDomainFixture();
        var enabled = new PeopleResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        var original = await enabled.ReadStateAsync();
        var policy = Policy();
        await enabled.RegisterEvaluatedPolicyAsync(policy.Artifact, policy.Evaluation, Operator);
        try
        {
            await SelectFixturePolicy(enabled, policy.Artifact.Uuid);
            var restricted = new PeopleResolutionPolicyService(fixture.Db, fixture.Work);
            var before = await enabled.ReadStateAsync();
            await using var connection = fixture.Db.CreateConnection(false, false);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead
            );
            await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: transaction);

            var read = () =>
                restricted.ReadStateInSnapshotAsync(
                    connection,
                    transaction,
                    CancellationToken.None
                );
            await read.Should().ThrowAsync<ArgumentException>();

            (await enabled.ReadStateAsync()).Should().BeEquivalentTo(before);
            await transaction.CommitAsync();
        }
        finally
        {
            await SelectFixturePolicy(enabled, original.Active?.Policy.Artifact.Uuid);
        }
    }

    private static async Task SelectFixturePolicy(
        PeopleResolutionPolicyService registry,
        Guid? artifact
    )
    {
        var current = await registry.ReadStateAsync();
        await registry.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                current.OwnerObservation.SubjectUuid,
                current.OwnerObservation.EditorialRevision,
                current.Active?.Policy.Artifact.Uuid,
                artifact,
                "Select the policy for this isolated fixture"
            ),
            Operator
        );
    }

    private static RegisteredPeopleAcceptancePolicy Policy(bool combined = true)
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
            "source-alias-fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            combined
                ? ["retained_credit", PeopleResolutionPolicyEvaluator.SourceInputForm]
                : [PeopleResolutionPolicyEvaluator.SourceInputForm],
            new(0, 1, 1, 1, 2.5, 0.5),
            FixtureOnly: true,
            SourceActorAliasApplicability: applicability
        );
        var evaluation = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            "",
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
                "fixture-role-free-source-evaluator-v1",
                "fixture-source-reviewer",
                new(37, 0)
            )
        );
        return Rehash(new(artifact, evaluation));
    }

    private static RegisteredPeopleAcceptancePolicy Rehash(RegisteredPeopleAcceptancePolicy policy)
    {
        var artifact = policy.Artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(policy.Artifact),
        };
        var evaluation = policy.Evaluation with
        {
            ArtifactHash = artifact.ContentHash,
            ReceiptHash = "",
        };
        evaluation = evaluation with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        return new(artifact, evaluation);
    }
}
