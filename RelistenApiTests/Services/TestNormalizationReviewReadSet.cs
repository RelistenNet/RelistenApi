using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>One-off review proof is locked and retained without changing observed owners or Work bindings.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationReviewReadSet
{
    private DbService db = null!;
    private NormalizationWorkService work = null!;
    private NormalizationEvidenceService evidence = null!;
    private string? previousWrite;
    private string? previousRead;

    [OneTimeSetUp]
    public async Task Start()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        work = new NormalizationWorkService(db);
        evidence = new NormalizationEvidenceService(db);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "CREATE TABLE normalization.review_fence_test_values(uuid uuid PRIMARY KEY,value text NOT NULL)"
            )
        );
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (previousWrite != null)
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead!;
        }
    }

    [Test]
    public async Task SuccessRetainsProofAndChangesOnlyTheActualCommandOwner()
    {
        var source = await Owner("source_actor_resolution");
        var extraction = await Owner("recording");
        var personUuid = Guid.NewGuid();
        var person = await Owner("person", personUuid);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@personUuid,'Review target','review target','person')",
                new { personUuid }
            )
        );
        var sourceBefore = (await work.GetSubjectAsync(source.SubjectUuid))!;
        var personBefore = (await work.GetSubjectAsync(person.SubjectUuid))!;
        var extractionBefore = (await work.GetSubjectAsync(extraction.SubjectUuid))!;
        var revisionBefore = await PersonRevision(personUuid);
        var capture = await Capture();
        var command = Command(
            source,
            new(
                Inputs: [capture.Input],
                Dependencies:
                [
                    new(
                        extraction.SubjectUuid,
                        extraction.DesiredGeneration,
                        extraction.EditorialRevision,
                        extraction.PreviousPublishedWorkUuid
                    ),
                ],
                OwnerObservations: [Observe(person), Observe(source)]
            )
        );

        var result = await work.ApplyReviewAsync(command, Write(command.OperationUuid));

        result.EditorialRevision.Should().Be(1);
        (await work.GetSubjectAsync(person.SubjectUuid)).Should().BeEquivalentTo(personBefore);
        (await work.GetSubjectAsync(extraction.SubjectUuid))
            .Should()
            .BeEquivalentTo(extractionBefore);
        (await PersonRevision(personUuid)).Should().Be(revisionBefore);
        var after = (await work.GetSubjectAsync(source.SubjectUuid))!;
        after.EditorialRevision.Should().Be(sourceBefore.EditorialRevision + 1);
        after.DesiredGeneration.Should().Be(sourceBefore.DesiredGeneration + 1);
        after.PublishedWorkUuid.Should().Be(sourceBefore.PublishedWorkUuid);
        (await ReadValue(command.OperationUuid)).Should().Be("accepted");
        var proof = JsonSerializer.Deserialize<NormalizationReviewReadSet>(
            (await ReadProof(command.OperationUuid))!
        )!;
        proof.Should().BeEquivalentTo(command.ReadSet);
        (
            await db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.work_dependencies WHERE subject_uuid=@SubjectUuid",
                    source
                )
            )
        )
            .Should()
            .Be(0);
    }

    [Test]
    public async Task NullReadSetPreservesLegacySerializationHashAndAdditionalOwnerMutation()
    {
        var source = await Owner("source_actor_resolution");
        var changed = await Owner("person");
        var command = Command(source) with { AdditionalSubjectUuids = [changed.SubjectUuid] };
        var normalized = command with { AdditionalSubjectUuids = new[] { changed.SubjectUuid } };
        var legacy = new
        {
            command.OperationUuid,
            command.SubjectUuid,
            command.ExpectedEditorialRevision,
            command.Action,
            command.Payload,
            command.Reason,
            command.Operator,
            AdditionalSubjectUuids = new[] { changed.SubjectUuid },
        };
        JsonSerializer.Serialize(normalized).Should().Be(JsonSerializer.Serialize(legacy));
        var legacyCanonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["OperationUuid"] = command.OperationUuid,
            ["SubjectUuid"] = command.SubjectUuid,
            ["ExpectedEditorialRevision"] = 0L,
            ["Action"] = "accept",
            ["Payload"] = command.Payload,
            ["Reason"] = command.Reason,
            ["Operator"] = command.Operator,
            ["AdditionalSubjectUuids"] = new[] { changed.SubjectUuid },
        };
        var expectedHash = Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(legacyCanonical))
        );

        await work.ApplyReviewAsync(command, Write(command.OperationUuid));

        (
            await db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT command_hash FROM normalization.review_operations WHERE uuid=@OperationUuid",
                    command
                )
            )
        )
            .Should()
            .Be(expectedHash);
        (await ReadProof(command.OperationUuid)).Should().BeNull();
        (await work.GetSubjectAsync(changed.SubjectUuid))!.EditorialRevision.Should().Be(1);
        (await work.GetSubjectAsync(changed.SubjectUuid))!
            .DesiredGeneration.Should()
            .Be(changed.DesiredGeneration + 1);
    }

    [Test]
    public async Task ChangedInputRejectsWithoutWritesAndTheSameUncommittedOperationCanRecover()
    {
        var source = await Owner("source_actor_resolution");
        var capture = await Capture();
        var command = Command(source, new(Inputs: [capture.Input]));
        var changed = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                capture.Resource,
                Encoding.UTF8.GetBytes("new"),
                "text/plain",
                "test",
                capture.Input.Generation,
                DateTimeOffset.UtcNow
            )
        );
        var stale = () => work.ApplyReviewAsync(command, Write(command.OperationUuid));

        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await ReadValue(command.OperationUuid)).Should().BeNull();
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
        (await work.GetSubjectAsync(source.SubjectUuid))!.EditorialRevision.Should().Be(0);
        var recovered = command with
        {
            ReadSet = new(
                Inputs:
                [
                    capture.Input with
                    {
                        RevisionUuid = changed.Revision.Uuid,
                        Generation = changed.Fetch.ResourceGeneration,
                    },
                ]
            ),
        };
        await work.ApplyReviewAsync(recovered, Write(command.OperationUuid));
        (await ReadValue(command.OperationUuid)).Should().Be("accepted");
    }

    [TestCase("resource")]
    [TestCase("owner")]
    [TestCase("conflicting_owner")]
    [TestCase("zero_revision")]
    public async Task MissingOrMalformedProofNeverInvokesTheWriter(string kind)
    {
        var source = await Owner("source_actor_resolution");
        var observed = await Owner("person");
        var capture = await Capture();
        var proof = kind switch
        {
            "resource" => new NormalizationReviewReadSet(
                Inputs: [capture.Input with { ResourceUuid = Guid.NewGuid() }]
            ),
            "owner" => new(
                OwnerObservations: [Observe(observed) with { SubjectUuid = Guid.NewGuid() }]
            ),
            "conflicting_owner" => new(
                OwnerObservations: [Observe(observed)],
                Dependencies: [new(observed.SubjectUuid, observed.DesiredGeneration + 1, 0, null)]
            ),
            _ => new(Inputs: [capture.Input with { RevisionUuid = Guid.Empty }]),
        };
        var command = Command(source, proof);
        var calls = 0;
        var apply = () =>
            work.ApplyReviewAsync(
                command,
                (_, _, _) =>
                {
                    calls++;
                    return Task.CompletedTask;
                }
            );
        if (kind == "resource")
        {
            await apply.Should().ThrowAsync<NormalizationConflictException>();
        }
        else
        {
            await apply.Should().ThrowAsync<ArgumentException>();
        }
        calls.Should().Be(0);
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
        (await work.GetSubjectAsync(source.SubjectUuid))!.EditorialRevision.Should().Be(0);
    }

    [Test]
    public async Task SelfObservationDoesNotGetThePublicationExemption()
    {
        var source = await Owner("source_actor_resolution");
        var command = Command(source, new(OwnerObservations: [Observe(source)]));
        await work.TryPublishAsync(source.WorkUuid, (_, _, _) => Task.CompletedTask);
        var stale = () => work.ApplyReviewAsync(command, Write(command.OperationUuid));
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
    }

    [Test]
    public async Task DependencyPublicationInvalidatesItsOriginalStampWithoutAnEditorialChange()
    {
        var source = await Owner("source_actor_resolution");
        var dependency = await Owner("person");
        var original = new NormalizationSubjectDependency(
            dependency.SubjectUuid,
            dependency.DesiredGeneration,
            dependency.EditorialRevision,
            null
        );
        var command = Command(source, new(Dependencies: [original]));
        await work.TryPublishAsync(dependency.WorkUuid, (_, _, _) => Task.CompletedTask);
        var stale = () => work.ApplyReviewAsync(command, Write(command.OperationUuid));
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
        var current = (await work.GetSubjectAsync(dependency.SubjectUuid))!;
        current.EditorialRevision.Should().Be(original.EditorialRevision);
        current.DesiredGeneration.Should().Be(original.DesiredGeneration);
        var refreshed = command with
        {
            ReadSet = new(
                Dependencies: [original with { PublishedWorkUuid = current.PublishedWorkUuid }]
            ),
        };
        await work.ApplyReviewAsync(refreshed, Write(command.OperationUuid));
        (await ReadValue(command.OperationUuid)).Should().Be("accepted");
    }

    [Test]
    public async Task CallbackFailureAndCancellationRollBackProofAndCanRetry()
    {
        var source = await Owner("source_actor_resolution");
        var capture = await Capture();
        var command = Command(source, new(Inputs: [capture.Input]));
        var fail = () =>
            work.ApplyReviewAsync(
                command,
                async (c, t, ct) =>
                {
                    await Write(command.OperationUuid)(c, t, ct);
                    throw new InvalidOperationException("synthetic callback failure");
                }
            );
        await fail.Should().ThrowAsync<InvalidOperationException>();
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
        (await ReadValue(command.OperationUuid)).Should().BeNull();

        using var cancellation = new CancellationTokenSource();
        var cancel = () =>
            work.ApplyReviewAsync(
                command,
                async (c, t, ct) =>
                {
                    await Write(command.OperationUuid)(c, t, ct);
                    cancellation.Cancel();
                },
                cancellation.Token
            );
        await cancel.Should().ThrowAsync<OperationCanceledException>();
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
        (await ReadValue(command.OperationUuid)).Should().BeNull();
        await work.ApplyReviewAsync(command, Write(command.OperationUuid));
        (await ReadValue(command.OperationUuid)).Should().Be("accepted");
    }

    [Test]
    public async Task ReplayDoesNotAcquireConsumedLocksOrCallTheWriterAgain()
    {
        var source = await Owner("source_actor_resolution");
        var target = await Owner("person");
        var capture = await Capture();
        var command = Command(
            source,
            new(Inputs: [capture.Input], OwnerObservations: [Observe(target)])
        );
        await work.ApplyReviewAsync(command, Write(command.OperationUuid));
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT 1 FROM normalization.input_resources WHERE uuid=@ResourceUuid FOR UPDATE;SELECT 1 FROM normalization.work_subjects WHERE uuid=@SubjectUuid FOR UPDATE",
            new { capture.Input.ResourceUuid, target.SubjectUuid },
            transaction
        );

        var replay = await work.ApplyReviewAsync(
                command,
                (_, _, _) => throw new AssertionException("A replay must not call the writer.")
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        replay.Replayed.Should().BeTrue();
        replay.EditorialRevision.Should().Be(1);
    }

    [Test]
    public async Task TheRetainedReadSetCannotBeMutatedAfterCommit()
    {
        var source = await Owner("source_actor_resolution");
        var capture = await Capture();
        var command = Command(source, new(Inputs: [capture.Input]));
        await work.ApplyReviewAsync(command, Write(command.OperationUuid));
        var tamper = () =>
            db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.review_operations SET read_set='{}'::jsonb WHERE uuid=@OperationUuid",
                    new { command.OperationUuid }
                )
            );
        var failure = await tamper.Should().ThrowAsync<Exception>();
        failure
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should()
            .Be(PostgresErrorCodes.RaiseException);
        var proof = JsonSerializer.Deserialize<NormalizationReviewReadSet>(
            (await ReadProof(command.OperationUuid))!
        )!;
        proof.Inputs.Should().ContainSingle().Which.Should().Be(capture.Input);
    }

    [Test]
    public async Task ReviewHoldsTheConsumedResourceUntilItsCallbackCommits()
    {
        var source = await Owner("source_actor_resolution");
        var capture = await Capture();
        var command = Command(source, new(Inputs: [capture.Input]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applying = work.ApplyReviewAsync(
            command,
            async (_, _, ct) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refresh = evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                capture.Resource,
                Encoding.UTF8.GetBytes("after review"),
                "text/plain",
                "test",
                capture.Input.Generation,
                DateTimeOffset.UtcNow
            )
        );
        try
        {
            await using var probe = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
            await probe.OpenAsync();
            var locked = () =>
                probe.ExecuteAsync(
                    "SELECT 1 FROM normalization.input_resources WHERE uuid=@ResourceUuid FOR UPDATE NOWAIT",
                    capture.Input
                );
            (await locked.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should()
                .Be("55P03");
        }
        finally
        {
            release.TrySetResult();
        }
        await applying;
        (await refresh).Fetch.AcceptedAsCurrent.Should().BeTrue();
        (await HasReceipt(command.OperationUuid)).Should().BeTrue();
    }

    [Test]
    public async Task CallerListsAreFrozenBeforeWaitingForFactLocks()
    {
        var source = await Owner("source_actor_resolution");
        var target = await Owner("person");
        var capture = await Capture();
        var inputs = new List<NormalizationInputDependency> { capture.Input };
        var observations = new List<NormalizationSubjectObservation> { Observe(target) };
        var command = Command(source, new(Inputs: inputs, OwnerObservations: observations));
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "SELECT 1 FROM normalization.input_resources WHERE uuid=@ResourceUuid FOR UPDATE",
            capture.Input,
            transaction
        );
        var applying = work.ApplyReviewAsync(command, Write(command.OperationUuid));
        inputs.Clear();
        observations[0] = observations[0] with { EditorialRevision = 999 };
        await transaction.CommitAsync();
        await applying;

        var proof = JsonSerializer.Deserialize<NormalizationReviewReadSet>(
            (await ReadProof(command.OperationUuid))!
        )!;
        proof.Inputs.Should().ContainSingle().Which.Should().Be(capture.Input);
        proof.OwnerObservations.Should().ContainSingle().Which.Should().Be(Observe(target));
    }

    [Test]
    public async Task ReviewHoldsObservedOwnersUntilCommitAndLaterStaleReviewConflicts()
    {
        var source = await Owner("source_actor_resolution");
        var target = await Owner("person");
        var command = Command(source, new(OwnerObservations: [Observe(target)]));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = work.ApplyReviewAsync(
            command,
            async (_, _, ct) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(ct);
            }
        );
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var targetEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var targetChange = work.ApplyReviewAsync(
            Command(target),
            (_, _, _) =>
            {
                targetEntered.SetResult();
                return Task.CompletedTask;
            }
        );
        try
        {
            // The first callback has started, so its observed row lock is already held. Probe
            // that actual lock independently rather than treating a slow task as proof of it.
            await using var probe = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
            await probe.OpenAsync();
            var locked = () =>
                probe.ExecuteAsync(
                    "SELECT 1 FROM normalization.work_subjects WHERE uuid=@SubjectUuid FOR UPDATE NOWAIT",
                    target
                );
            (await locked.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should()
                .Be("55P03");
            targetEntered.Task.IsCompleted.Should().BeFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        await first;
        await targetChange;
        var another = await Owner("source_actor_resolution");
        var stale = () =>
            work.ApplyReviewAsync(
                Command(another, new(OwnerObservations: [Observe(target)])),
                (_, _, _) => Task.CompletedTask
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task UnusedPoolAndPolicyOwnerChangesDoNotInvalidateExplicitReview()
    {
        var source = await Owner("source_actor_resolution");
        var target = await Owner("person");
        var unrelatedPolicy = await Owner("acceptance_policy");
        var command = Command(source, new(OwnerObservations: [Observe(target)]));
        await work.ApplyReviewAsync(Command(unrelatedPolicy), (_, _, _) => Task.CompletedTask);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.people(uuid,display_name,name_key,kind) VALUES(@uuid,'Unrelated new person','unrelated new person','person')",
                new { uuid = Guid.NewGuid() }
            )
        );

        (await work.ApplyReviewAsync(command, Write(command.OperationUuid)))
            .Replayed.Should()
            .BeFalse();
        (await work.GetSubjectAsync(unrelatedPolicy.SubjectUuid))!.EditorialRevision.Should().Be(1);
        (await work.GetSubjectAsync(target.SubjectUuid))!.EditorialRevision.Should().Be(0);
    }

    [Test]
    public async Task NormalizedOrderReplaysButChangedProofCannotReuseTheOperation()
    {
        var source = await Owner("source_actor_resolution");
        var a = await Capture();
        var b = await Capture();
        var command = Command(source, new(Inputs: [b.Input, a.Input, b.Input]));
        await work.ApplyReviewAsync(command, Write(command.OperationUuid));
        var reordered = command with { ReadSet = new(Inputs: [a.Input, b.Input]) };
        (
            await work.ApplyReviewAsync(
                reordered,
                (_, _, _) => throw new AssertionException("No replay writer.")
            )
        )
            .Replayed.Should()
            .BeTrue();
        var changed = () =>
            work.ApplyReviewAsync(
                command with
                {
                    ReadSet = new(Inputs: [a.Input]),
                },
                (_, _, _) => Task.CompletedTask
            );
        await changed.Should().ThrowAsync<NormalizationConflictException>();
    }

    [TestCase("artist")]
    [TestCase("identifier")]
    [TestCase("source_uuid")]
    [TestCase("venue")]
    [TestCase("mapping")]
    [TestCase("pool")]
    public async Task EveryCatalogAndCandidateFamilyComparesItsOriginalGeneration(string family)
    {
        var source = await Owner("source_actor_resolution");
        var good = await CatalogProof();
        var bad = family switch
        {
            "artist" => good with
            {
                CatalogInputs =
                [
                    good.CatalogInputs![0] with
                    {
                        Generation = good.CatalogInputs[0].Generation + 1,
                    },
                ],
            },
            "identifier" => good with
            {
                SourceIdentities =
                [
                    good.SourceIdentities![0] with
                    {
                        Generation = good.SourceIdentities[0].Generation + 1,
                    },
                ],
            },
            "source_uuid" => good with
            {
                SourceUuids =
                [
                    good.SourceUuids![0] with
                    {
                        Generation = good.SourceUuids[0].Generation + 1,
                    },
                ],
            },
            "venue" => good with
            {
                ImportedVenues =
                [
                    good.ImportedVenues![0] with
                    {
                        Generation = good.ImportedVenues[0].Generation + 1,
                    },
                ],
            },
            "mapping" => good with { MappingRegistry = new(good.MappingRegistry!.Generation + 1) },
            _ => good with
            {
                CandidateUniverses =
                [
                    good.CandidateUniverses![0] with
                    {
                        Generation = good.CandidateUniverses[0].Generation + 1,
                    },
                ],
            },
        };
        var command = Command(source, bad);
        var stale = () => work.ApplyReviewAsync(command, Write(command.OperationUuid));
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await HasReceipt(command.OperationUuid)).Should().BeFalse();
        await work.ApplyReviewAsync(command with { ReadSet = good }, Write(command.OperationUuid));
        (await ReadValue(command.OperationUuid)).Should().Be("accepted");
    }

    private static NormalizationReviewCommand Command(
        NormalizationWorkTicket owner,
        NormalizationReviewReadSet? proof = null
    ) =>
        new(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            JsonSerializer.SerializeToElement(new { a = 1 }),
            "test review",
            "review-test",
            ReadSet: proof
        );

    private Task<NormalizationWorkTicket> Owner(string kind, Guid? uuid = null) =>
        work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", kind, uuid ?? Guid.NewGuid()),
            "test",
            [],
            []
        );

    private static NormalizationSubjectObservation Observe(NormalizationWorkTicket ticket) =>
        new(
            ticket.SubjectUuid,
            ticket.DesiredGeneration,
            ticket.EditorialRevision,
            ticket.PreviousPublishedWorkUuid
        );

    private async Task<CapturedInput> Capture()
    {
        var key = new NormalizationResourceKey(
            "test",
            "review-read-set",
            Guid.NewGuid().ToString()
        );
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                key,
                Encoding.UTF8.GetBytes("original"),
                "text/plain",
                "test",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var owner = await work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "review_input_fixture", Guid.NewGuid()),
            "test",
            [key],
            []
        );
        return new(key, owner.Inputs.Single());
    }

    private static Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> Write(
        Guid uuid
    ) =>
        (c, t, ct) =>
            c.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO normalization.review_fence_test_values(uuid,value) VALUES(@uuid,'accepted')",
                    new { uuid },
                    t,
                    cancellationToken: ct
                )
            );

    private Task<string?> ReadValue(Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM normalization.review_fence_test_values WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<string?> ReadProof(Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                "SELECT read_set::text FROM normalization.review_operations WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<bool> HasReceipt(Guid uuid) =>
        db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.review_operations WHERE uuid=@uuid)",
                new { uuid }
            )
        );

    private Task<long> PersonRevision(Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<NormalizationReviewReadSet> CatalogProof()
    {
        // Fixture clocks are initialized by the same catalog triggers used in production.
        return await db.WithWriteConnection(async c =>
        {
            var artistUuid = Guid.NewGuid();
            var artist = await c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Review fixture',@slug,@artistUuid,now()) RETURNING id",
                new { slug = artistUuid.ToString(), artistUuid }
            );
            var sourceUuid = Guid.NewGuid();
            var identifier = sourceUuid.ToString();
            await c.ExecuteAsync(
                "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@artist,@identifier,'2026-10-18',@sourceUuid,now(),false,false,false)",
                new
                {
                    artist,
                    identifier,
                    sourceUuid,
                }
            );
            var venueUuid = Guid.NewGuid();
            await c.ExecuteAsync(
                "INSERT INTO venues(artist_id,name,slug,uuid,updated_at) VALUES(@artist,'Review venue',@slug,@venueUuid,now())",
                new
                {
                    artist,
                    slug = venueUuid.ToString(),
                    venueUuid,
                }
            );
            var artistGeneration = await c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@artistUuid",
                new { artistUuid }
            );
            var sourceGeneration = await c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_source_identity_revisions WHERE upstream_identifier=@identifier",
                new { identifier }
            );
            var sourceUuidGeneration = await c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_source_uuid_revisions WHERE source_uuid=@sourceUuid",
                new { sourceUuid }
            );
            var venueGeneration = await c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_venue_revisions WHERE venue_uuid=@venueUuid",
                new { venueUuid }
            );
            var mappingGeneration = await c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_mapping_registry_revision WHERE singleton"
            );
            var pool = await c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@empty",
                new { empty = Guid.Empty }
            );
            return new NormalizationReviewReadSet(
                CatalogInputs: [new(artistUuid, artistGeneration)],
                SourceIdentities: [new(identifier, sourceGeneration)],
                MappingRegistry: new(mappingGeneration),
                ImportedVenues: [new(venueUuid, venueGeneration)],
                CandidateUniverses: [new(NormalizationCandidateUniverseKey.People, pool)],
                SourceUuids: [new(sourceUuid, sourceUuidGeneration)]
            );
        });
    }

    private sealed record CapturedInput(
        NormalizationResourceKey Resource,
        NormalizationInputDependency Input
    );
}
