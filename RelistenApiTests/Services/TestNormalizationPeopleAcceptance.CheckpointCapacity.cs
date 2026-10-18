using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

// Exercises checkpoint capacity through genuine extraction, resolution and acceptance. The
// shared owner-scale seed includes two service publications and explicitly mechanical copies;
// those copies establish reader/storage load, not additional independently verified recordings.
public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task LegacyPreparationWinsAndPublishesCompactBatchesWithoutRewritingItsEvidence()
    {
        var name = UniqueName();
        await Person(name);
        var source = await Source(name);
        var extraction = await Extract(source);
        var original = await Prepare(extraction);
        var attempt = await resolutions.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );

        // Install the old representation once, before this fresh attempt has a preparation.
        // Facts come from the genuine extraction helper. Work admission and its publication
        // ticket are still produced by the public resolver, never manufactured by this fixture.
        var written = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE normalization.people_resolution_attempts SET preparation=@json::jsonb
                WHERE attempt_uuid=@AttemptUuid AND preparation IS NULL AND disposition IS NULL
                """,
                new { json = JsonSerializer.Serialize(original), attempt.AttemptUuid }
            )
        );
        written.Should().Be(1);
        var legacyJson = await ReadCheckpointCapacityPreparationJson(attempt.AttemptUuid);
        var originalHash = CheckpointCapacityHash(original);

        var winner = await resolutions.SavePreparationAsync(
            attempt.AttemptUuid,
            original with
            {
                ExpectedConfigurationGeneration = 37,
            }
        );
        CheckpointCapacityHash(winner.Preparation).Should().Be(originalHash);
        (await ReadCheckpointCapacityPreparationJson(attempt.AttemptUuid)).Should().Be(legacyJson);

        // This competing request is deliberately invalid and too large for a new write.
        // An existing winner must return before encoding an ignored request. The fixture does
        // not install this payload or claim that a provider produced its decode issue.
        var originalInput = original.Inputs.Single();
        var ignored = original with
        {
            Inputs =
            [
                originalInput with
                {
                    DecodeIssues =
                    [
                        new(
                            new string(
                                'x',
                                NormalizationPeopleResolutionStore.MaximumCheckpointBytes
                            ),
                            originalInput.Claims.Single().Proposal.Mention.Evidence
                        ),
                    ],
                },
            ],
        };
        var retained = await resolutions.SavePreparationAsync(attempt.AttemptUuid, ignored);
        CheckpointCapacityHash(retained.Preparation).Should().Be(originalHash);
        (await ReadCheckpointCapacityPreparationJson(attempt.AttemptUuid)).Should().Be(legacyJson);

        var published = await resolver.ResolveRecordingAsync(
            attempt.AttemptUuid,
            extraction.AttemptUuid
        );
        published
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var batch = (await resolutions.GetAttemptBatchAsync(attempt.AttemptUuid))!;
        CheckpointCapacityHash(batch.Preparation).Should().Be(originalHash);
        var compactBatch = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<bool>(
                """
                SELECT batch->>'Format'=@format FROM normalization.people_resolution_attempts
                WHERE attempt_uuid=@AttemptUuid
                """,
                new { format = PeopleCheckpointCodec.FormatVersion, attempt.AttemptUuid }
            )
        );
        compactBatch.Should().BeTrue();

        // The first new attempt authenticates configured headers from the legacy preparation.
        // Its compact preparation then supplies the configured headers for the following one.
        for (var index = 0; index < 2; index++)
        {
            var next = await resolver.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
            next.Result!.Disposition.Should()
                .Be(NormalizationPeopleResolutionDisposition.Published);
        }
        (await ReadCheckpointCapacityPreparationJson(attempt.AttemptUuid)).Should().Be(legacyJson);
        CheckpointCapacityHash(
                (await resolutions.GetAttemptAsync(attempt.AttemptUuid))!.Preparation
            )
            .Should()
            .Be(originalHash);
    }

    [Test]
    public async Task LargeMultiRoleCheckpointsPreserveFactsAndRecoverAcrossPublicationBoundaries()
    {
        await ActivatePolicy();
        var seeded = await SeedDistinctActorOwners();
        var contacts = Enumerable.Range(0, 7).Select(_ => RandomWord() + "@example.test").ToArray();
        var name = seeded.First.Name;
        var source = await Source($"{name} ({contacts[0]})");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET transferrer=@transferrer,description=@description WHERE id=@SourceId",
                new
                {
                    source.SourceId,
                    transferrer = $"{name} ({contacts[1]})",
                    description = string.Join(
                        "; ",
                        $"Mastered by {name} ({contacts[2]})",
                        $"Matrixed by {name} ({contacts[3]})",
                        $"Uploaded by {name} ({contacts[4]})",
                        $"Collected by {name} ({contacts[5]})"
                    ),
                }
            )
        );

        // Publishing the local extraction first makes it a real peer of the retained Archive
        // input. The Archive field has two explicit roles; no proposal is manufactured here.
        var local = await Extract(source);
        local.Extraction.Credits.Should().HaveCount(6);
        var extraction = await ExtractArchive(
            source,
            $"Recorded and transferred by {name} ({contacts[6]})"
        );
        var resolutionUuid = Guid.NewGuid();
        await FailCheckpointCapacityWrite(
            "people_resolution_attempts",
            "UPDATE OF batch",
            "attempt_uuid",
            resolutionUuid,
            async () =>
            {
                await resolver.ResolveRecordingAsync(resolutionUuid, extraction.AttemptUuid);
            }
        );

        var prepared = (await resolutions.GetAttemptAsync(resolutionUuid))!;
        prepared.Result.Should().BeNull();
        prepared.Preparation.Should().NotBeNull();
        (await resolutions.GetAttemptBatchAsync(resolutionUuid)).Should().BeNull();
        var preparation = prepared.Preparation!;
        var claims = preparation.Inputs.SelectMany(input => input.Claims).ToArray();
        preparation.Inputs.Should().HaveCount(2);
        claims.Should().HaveCount(8);
        claims.Select(claim => claim.Proposal.ProposalUuid).Should().OnlyHaveUniqueItems();
        claims.Select(claim => claim.Proposal.Mention.Role).Distinct().Should().HaveCount(6);
        claims.Select(claim => claim.Candidates!.QueryHash).Distinct().Should().HaveCount(7);
        claims
            .Should()
            .OnlyContain(claim => claim.ScopeIssue == null && claim.Candidates!.Complete);

        // Compare the saved checkpoint with an independent read of the same original query.
        // The raw examples and every owner stamp must survive, including owners outside those
        // bounded examples. Query-specific hints remain distinct in the expanded public DTOs.
        var expected = await candidates.FindPeopleFactsAsync(
            claims[0].Candidates!.Query,
            claims[0].Candidates!.Limits
        );
        expected.Complete.Should().BeTrue();
        var expectedFact = expected.Facts.Should().ContainSingle().Which;
        expectedFact.Hit.Uuid.Should().Be(seeded.First.PersonUuid);
        expectedFact.ActorContextCount.Should().Be(DistinctActorOwnerScale);
        expectedFact.ActorEvidence.Should().NotBeEmpty();
        var witnessHash = CheckpointCapacityHash(expectedFact.ActorEvidence);
        foreach (var claim in claims)
        {
            var snapshot = claim.Candidates!;
            snapshot
                .Facts.Should()
                .ContainSingle()
                .Which.Hit.Uuid.Should()
                .Be(seeded.First.PersonUuid);
            snapshot.ReadSet.OwnerObservations.Should().Equal(seeded.OriginalObservations);
            CheckpointCapacityHash(snapshot.Facts.Single().ActorEvidence).Should().Be(witnessHash);
        }
        claims
            .SelectMany(claim => claim.Candidates!.Query.PeopleHints!.Contacts)
            .Select(contact => contact.LookupKey)
            .Distinct()
            .Should()
            .BeEquivalentTo(contacts);
        var preparationHash = CheckpointCapacityHash(preparation);
        var expandedPreparationBytes = ExpandedCheckpointCapacityBytes(preparation);
        expandedPreparationBytes
            .Should()
            .BeGreaterThan(NormalizationPeopleResolutionStore.MaximumCheckpointBytes);

        var resolution = await resolver.ResolveRecordingAsync(
            resolutionUuid,
            extraction.AttemptUuid
        );
        resolution
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        CheckpointCapacityHash(resolution.Preparation).Should().Be(preparationHash);
        var batch = (await resolutions.GetAttemptBatchAsync(resolutionUuid))!;
        CheckpointCapacityHash(batch.Preparation).Should().Be(preparationHash);
        CheckpointCapacityHash(await resolutions.GetAsync(source.SourceUuid))
            .Should()
            .Be(CheckpointCapacityHash(batch));

        // The first interruption leaves a saved plan but no stage or admitted credit work.
        // The next interruption leaves that same plan and a durably admitted stage, while the
        // publication transaction rolls back both the accepted writes and their receipt.
        var applicationUuid = Guid.NewGuid();
        await FailCheckpointCapacityWrite(
            "people_acceptance_stages",
            "INSERT",
            "application_uuid",
            applicationUuid,
            async () =>
            {
                await applier.ApplyRecordingAsync(applicationUuid, resolutionUuid);
            }
        );
        var planned = (await applier.GetApplicationAsync(applicationUuid))!;
        planned.Result.Should().BeNull();
        planned.Stages.Should().BeEmpty();
        planned.Plan.Should().NotBeNull();
        planned
            .Plan!.Decisions.Should()
            .HaveCount(8)
            .And.OnlyContain(decision =>
                decision.Action == PeopleResolutionAction.MatchExisting
                && decision.PersonUuid == seeded.First.PersonUuid
            );
        var planHash = CheckpointCapacityHash(planned.Plan);
        CheckpointCapacityHash(planned.Plan.Proposal).Should().Be(CheckpointCapacityHash(batch));
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        await FailCheckpointCapacityWrite(
            "people_acceptance_stages",
            "UPDATE OF receipt",
            "application_uuid",
            applicationUuid,
            async () =>
            {
                await applier.ApplyRecordingAsync(applicationUuid, resolutionUuid);
            }
        );
        var admitted = (await applier.GetApplicationAsync(applicationUuid))!;
        admitted.Result.Should().BeNull();
        CheckpointCapacityHash(admitted.Plan).Should().Be(planHash);
        var stage = admitted.Stages.Should().ContainSingle().Which;
        stage.Ticket.Should().NotBeNull();
        stage.Receipt.Should().BeNull();
        CheckpointCapacityHash(stage.Facts).Should().Be(preparationHash);
        AssertCapacityTicketObservations(stage);
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        var stageHash = CheckpointCapacityHash(stage);

        var applied = await applier.ApplyRecordingAsync(applicationUuid, resolutionUuid);
        AssertCapacityApplied(applied);
        CheckpointCapacityHash(applied.Plan).Should().Be(planHash);
        var publishedStage = applied.Stages.Single();
        CheckpointCapacityHash(publishedStage with { Receipt = null }).Should().Be(stageHash);
        var contexts = await people.GetRecordingActorContextsAsync(source.SourceUuid);
        contexts.Should().HaveCount(8);
        contexts
            .SelectMany(context => context.Facts.Contacts)
            .Select(contact => contact.LookupKey)
            .Distinct()
            .Should()
            .BeEquivalentTo(contacts);
        var contextsHash = CheckpointCapacityHash(contexts);
        await AssertCheckpointCapacityStorage(batch, applied, expandedPreparationBytes);
        await AssertLargePeopleMembership(source, seeded, applied);

        var replay = await applier.ApplyRecordingAsync(applicationUuid, resolutionUuid);
        replay.Result!.Replayed.Should().BeTrue();
        CheckpointCapacityHash(replay.Plan).Should().Be(planHash);
        CheckpointCapacityHash(replay.Stages).Should().Be(CheckpointCapacityHash(applied.Stages));
        CheckpointCapacityHash(await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .Be(contextsHash);

        // New attempts must read the compact published preparation and stage to authenticate
        // existing contributor ownership. A terminal UUID replay alone bypasses that path.
        // All credits can be accepted while untyped description prose still lacks authority
        // to retire prior support. That incomplete coverage remains an unresolved outcome.
        var next = await resolver.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        next.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        var configured = await applier.ApplyRecordingAsync(Guid.NewGuid(), next.AttemptUuid);
        configured.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        configured.Result.Reason.Should().Be("recording_credit_application");
        var configuredStage = configured.Stages.Should().ContainSingle().Which;
        configuredStage.Receipt.Should().NotBeNull();
        configuredStage
            .Receipt!.Credits.Should()
            .HaveCount(8)
            .And.OnlyContain(outcome =>
                outcome.State == "accepted" && outcome.PersonUuid == seeded.First.PersonUuid
            );

        var localContributor = preparation
            .Inputs.Single(input => input.OriginalInput.Contributor.Provider == "relisten")
            .OriginalInput.Contributor;
        configuredStage.ScopeCoverage.Should().NotBeNull();
        var incompleteLocalScopes = configuredStage
            .ScopeCoverage!.Where(scope => scope.Contributor == localContributor && !scope.Complete)
            .ToArray();
        incompleteLocalScopes
            .Select(scope => scope.Scope.Role)
            .Should()
            .BeEquivalentTo(
                new[] { "mastering_engineer", "matrix_builder", "uploader", "collector" }
            );
        incompleteLocalScopes
            .Should()
            .OnlyContain(scope => scope.Reason == "missing_or_unproven_current_field");
        CheckpointCapacityHash(await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .Be(contextsHash);
        CheckpointCapacityHash((await resolutions.GetAttemptAsync(resolutionUuid))!.Preparation)
            .Should()
            .Be(preparationHash);
        var historical = (await applier.GetApplicationAsync(applicationUuid))!;
        CheckpointCapacityHash(historical.Plan).Should().Be(planHash);
        CheckpointCapacityHash(historical.Stages)
            .Should()
            .Be(CheckpointCapacityHash(applied.Stages));
    }

    private async Task FailCheckpointCapacityWrite(
        string table,
        string operation,
        string attemptColumn,
        Guid attemptUuid,
        Func<Task> action
    )
    {
        var trigger = "checkpoint_capacity_failure_" + Guid.NewGuid().ToString("N");
        // These private call sites supply fixed table/column names. The trigger changes no
        // checkpoint bytes: it aborts one precise persistence boundary for this UUID only.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.{attemptColumn}='{attemptUuid:D}'::uuid THEN
                        RAISE EXCEPTION 'synthetic checkpoint capacity failure';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER {trigger} BEFORE {operation} ON normalization.{table}
                    FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}();
                """
            )
        );
        try
        {
            var failure = (await action.Should().ThrowAsync<Exception>()).Which;
            var databaseFailure = failure
                .GetBaseException()
                .Should()
                .BeOfType<PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be("P0001");
            databaseFailure.MessageText.Should().Be("synthetic checkpoint capacity failure");
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"""
                    DROP TRIGGER {trigger} ON normalization.{table};
                    DROP FUNCTION normalization.{trigger}();
                    """
                )
            );
        }
    }

    private Task<string> ReadCheckpointCapacityPreparationJson(Guid attemptUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT preparation::text FROM normalization.people_resolution_attempts WHERE attempt_uuid=@attemptUuid",
                new { attemptUuid }
            )
        );

    private async Task AssertCheckpointCapacityStorage(
        NormalizationPeopleResolutionBatch batch,
        PeopleAcceptanceApplication application,
        long expandedPreparationBytes
    )
    {
        var expanded = new
        {
            Preparation = expandedPreparationBytes,
            Batch = ExpandedCheckpointCapacityBytes(batch),
            Plan = ExpandedCheckpointCapacityBytes(application.Plan),
            Stage = ExpandedCheckpointCapacityBytes(
                application.Stages.Single() with
                {
                    Ticket = null,
                    Receipt = null,
                }
            ),
        };
        new[] { expanded.Preparation, expanded.Batch, expanded.Plan, expanded.Stage }
            .Should()
            .OnlyContain(bytes =>
                bytes > NormalizationPeopleResolutionStore.MaximumCheckpointBytes
            );
        var storage = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CapacityCheckpointBytes>(
                """
                SELECT octet_length(r.preparation::text) AS "Preparation",octet_length(r.batch::text) AS "Batch",
                    octet_length(a.plan::text) AS "Plan",octet_length(s.stage::text) AS "Stage",
                    octet_length(s.ticket::text) AS "Ticket"
                FROM normalization.people_resolution_attempts r
                JOIN normalization.people_acceptance_applications a ON a.resolution_attempt_uuid=r.attempt_uuid
                JOIN normalization.people_acceptance_stages s ON s.application_uuid=a.attempt_uuid
                WHERE r.attempt_uuid=@resolution AND a.attempt_uuid=@application
                """,
                new { resolution = batch.AttemptUuid, application = application.AttemptUuid }
            )
        );
        // PostgreSQL's JSON text formatting differs from the codec's canonical UTF-8 write
        // budget. These are actual stored-size observations, not a replacement admission rule.
        new[] { storage.Preparation, storage.Batch, storage.Plan, storage.Stage }
            .Should()
            .OnlyContain(bytes =>
                bytes < NormalizationPeopleResolutionStore.MaximumCheckpointBytes
            );
        WriteCapacityMeasurement(
            "multi_role_checkpoint_roundtrip",
            new
            {
                claims = 8,
                contributors = 2,
                expandedDtoUtf8Bytes = expanded,
                postgresJsonTextBytes = storage,
                servicePublishedSeedSentinels = 2,
                mechanicallyCopiedSeedAccounts = DistinctActorOwnerScale - 2,
                resolutionRecovery = true,
                recoveryBeforeStageAdmission = true,
                recoveryAfterStageAdmission = true,
            }
        );
    }

    private static long ExpandedCheckpointCapacityBytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value).LongLength;

    private static string CheckpointCapacityHash<T>(T value) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
