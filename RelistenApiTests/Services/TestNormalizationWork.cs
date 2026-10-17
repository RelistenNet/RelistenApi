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

/// <summary>Exercises publication and correction transactions against actual PostgreSQL locks and retained inputs.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationWork
{
    private DbService db = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationWorkService work = null!;
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
        evidence = new NormalizationEvidenceService(db);
        work = new NormalizationWorkService(db);
        await db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                CREATE TABLE normalization.work_test_values(uuid uuid PRIMARY KEY,value text NOT NULL)
                """
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
    public async Task PublishesTypedWritesAtomicallyAndReplayDoesNotCallWriter()
    {
        var capture = await Capture();
        var ticket = await Begin(capture.Resource);
        (await work.GetPendingSubjectsAsync())
            .Should()
            .Contain(s => s.SubjectUuid == ticket.SubjectUuid);
        var calls = 0;
        async Task Write(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await WriteValue(c, t, ticket.WorkUuid, "accepted", ct);
        }
        var results = await Task.WhenAll(
            work.TryPublishAsync(ticket.WorkUuid, Write),
            work.TryPublishAsync(ticket.WorkUuid, Write)
        );
        results.Should().OnlyContain(r => r.Status == NormalizationPublicationStatus.Published);
        results.Count(r => r.Replayed).Should().Be(1);
        calls.Should().Be(1);
        (await ReadValue(ticket.WorkUuid)).Should().Be("accepted");
        (await work.GetPendingSubjectsAsync())
            .Should()
            .NotContain(s => s.SubjectUuid == ticket.SubjectUuid);
    }

    [Test]
    public async Task ExactWorkReplayReturnsOriginalReadSetAfterInputsChange()
    {
        var capture = await Capture();
        var subject = Subject();
        var first = await Begin(capture.Resource, subject);
        await Change(capture, "newer", 1);
        var replay = await work.BeginWorkAsync(
            first.WorkUuid,
            subject,
            "policy-1",
            [capture.Resource],
            []
        );
        replay.Should().BeEquivalentTo(first);
        var conflict = () =>
            work.BeginWorkAsync(first.WorkUuid, subject, "policy-2", [capture.Resource], []);
        await conflict.Should().ThrowAsync<NormalizationConflictException>();
        var result = await work.TryPublishAsync(first.WorkUuid, NeverWrite);
        result.Status.Should().Be(NormalizationPublicationStatus.Stale);
        (await work.TryPublishAsync(first.WorkUuid, NeverWrite)).Replayed.Should().BeTrue();
    }

    [Test]
    public async Task ResourceOrderingDoesNotChangeRequestIdentity()
    {
        var a = await Capture();
        var b = await Capture();
        var subject = Subject();
        var uuid = Guid.NewGuid();
        var ticket = await work.BeginWorkAsync(
            uuid,
            subject,
            "policy-1",
            [a.Resource, b.Resource],
            []
        );
        var replay = await work.BeginWorkAsync(
            uuid,
            subject,
            "policy-1",
            [b.Resource, a.Resource],
            []
        );
        replay.Should().BeEquivalentTo(ticket);
    }

    [Test]
    public async Task InputCaptureAndPublicationSerializeWithoutLosingRefreshWork()
    {
        var capture = await Capture();
        var ticket = await Begin(capture.Resource);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Write(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            await WriteValue(c, t, ticket.WorkUuid, "published before input change", ct);
        }
        var publication = work.TryPublishAsync(ticket.WorkUuid, Write);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Publication holds the resource row until commit. A concurrent fetch must commit either
        // before publication's freshness check or after publication, leaving the subject pending.
        var change = Change(capture, "changed concurrently", 1);
        release.SetResult();
        var result = await publication;
        await change;
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await ReadValue(ticket.WorkUuid)).Should().Be("published before input change");
        (await work.GetPendingSubjectsAsync())
            .Should()
            .Contain(s => s.SubjectUuid == ticket.SubjectUuid);
    }

    [Test]
    public async Task InputChangesAreDurablyPendingAndRevertedBytesCannotPublishOldWork()
    {
        var capture = await Capture();
        var subject = Subject();
        var first = await Begin(capture.Resource, subject);
        await work.TryPublishAsync(first.WorkUuid, NoWrite);
        var old = await Begin(capture.Resource, subject);
        await Change(capture, "B", 1);
        await Change(capture, "A", 2);
        (await work.GetPendingSubjectsAsync())
            .Should()
            .Contain(s => s.SubjectUuid == first.SubjectUuid);
        (await work.TryPublishAsync(old.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var fresh = await Begin(capture.Resource, subject);
        fresh.Inputs.Single().RevisionUuid.Should().Be(first.Inputs.Single().RevisionUuid);
        fresh.Inputs.Single().Generation.Should().Be(3);
        (await work.TryPublishAsync(fresh.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await work.GetPendingSubjectsAsync())
            .Should()
            .NotContain(s => s.SubjectUuid == first.SubjectUuid);
    }

    [Test]
    public async Task PolicyAndBindingChangesFenceOlderRuns()
    {
        var firstInput = await Capture();
        var secondInput = await Capture();
        var subject = Subject();
        var first = await Begin(firstInput.Resource, subject);
        var second = await work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-2",
            [secondInput.Resource],
            [],
            expectedConfigurationGeneration: first.DesiredGeneration
        );
        second.DesiredGeneration.Should().Be(first.DesiredGeneration + 1);
        (await work.TryPublishAsync(first.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await work.TryPublishAsync(second.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        await Change(firstInput, "old binding changed", 1);
        (await work.GetPendingSubjectsAsync())
            .Should()
            .NotContain(s => s.SubjectUuid == first.SubjectUuid);
    }

    [Test]
    public async Task ObsoleteWorkerCannotRestoreEarlierConfiguration()
    {
        var input = await Capture();
        var subject = Subject();
        var original = await Begin(input.Resource, subject);
        var changed = await work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-2",
            [input.Resource],
            [],
            expectedConfigurationGeneration: original.DesiredGeneration
        );
        var stale = () =>
            work.BeginWorkAsync(
                Guid.NewGuid(),
                subject,
                "policy-1",
                [input.Resource],
                [],
                expectedConfigurationGeneration: original.DesiredGeneration
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        var missingCas = () =>
            work.BeginWorkAsync(Guid.NewGuid(), subject, "policy-1", [input.Resource], []);
        await missingCas.Should().ThrowAsync<NormalizationConflictException>();
        var current = await work.GetSubjectAsync(original.SubjectUuid);
        current!.PolicyVersion.Should().Be("policy-2");
        current.DesiredGeneration.Should().Be(changed.DesiredGeneration);
        (await work.TryPublishAsync(changed.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [TestCase(2)]
    [TestCase(3)]
    public async Task RejectsIndirectDependencyCyclesWithoutChangingOwner(int count)
    {
        var subjects = Enumerable.Range(0, count).Select(_ => Subject()).ToArray();
        var initial = new List<NormalizationWorkTicket>();
        foreach (var subject in subjects)
        {
            initial.Add(await work.BeginWorkAsync(Guid.NewGuid(), subject, "policy-1", [], []));
        }
        for (var i = 0; i < count - 1; i++)
        {
            await work.BeginWorkAsync(
                Guid.NewGuid(),
                subjects[i],
                "policy-1",
                [],
                [initial[i + 1].SubjectUuid],
                expectedConfigurationGeneration: initial[i].DesiredGeneration
            );
        }
        var last = initial[^1];
        var cycle = () =>
            work.BeginWorkAsync(
                Guid.NewGuid(),
                subjects[^1],
                "policy-1",
                [],
                [initial[0].SubjectUuid],
                expectedConfigurationGeneration: last.DesiredGeneration
            );
        await cycle.Should().ThrowAsync<ArgumentException>().WithMessage("*acyclic*");
        (await work.GetSubjectAsync(last.SubjectUuid))!
            .DesiredGeneration.Should()
            .Be(last.DesiredGeneration);
        (await work.TryPublishAsync(last.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task ConcurrentDependencyEditsCannotCreateACycle()
    {
        var a = Subject();
        var b = Subject();
        var initialA = await work.BeginWorkAsync(Guid.NewGuid(), a, "policy-1", [], []);
        var initialB = await work.BeginWorkAsync(Guid.NewGuid(), b, "policy-1", [], []);
        async Task<bool> Bind(NormalizationSubjectKey subject, Guid dependency)
        {
            try
            {
                await work.BeginWorkAsync(
                    Guid.NewGuid(),
                    subject,
                    "policy-1",
                    [],
                    [dependency],
                    expectedConfigurationGeneration: 1
                );
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        var results = await Task.WhenAll(
            Bind(a, initialB.SubjectUuid),
            Bind(b, initialA.SubjectUuid)
        );
        results.Count(success => success).Should().Be(1);
    }

    [Test]
    public async Task ConcurrentDistinctRunsWithSameGenerationHaveOnePublicationWinner()
    {
        var input = await Capture();
        var subject = Subject();
        var a = await Begin(input.Resource, subject);
        var b = await Begin(input.Resource, subject);
        var calls = 0;
        async Task Write(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await WriteValue(c, t, a.SubjectUuid, "winner", ct);
        }
        var results = await Task.WhenAll(
            work.TryPublishAsync(a.WorkUuid, Write),
            work.TryPublishAsync(b.WorkUuid, Write)
        );
        results.Count(r => r.Status == NormalizationPublicationStatus.Published).Should().Be(1);
        results.Count(r => r.Status == NormalizationPublicationStatus.Stale).Should().Be(1);
        calls.Should().Be(1);
        (await ReadValue(a.SubjectUuid)).Should().Be("winner");
    }

    [Test]
    public async Task NewerPublicationFencesAnotherRunWithIdenticalGeneration()
    {
        var capture = await Capture();
        var subject = Subject();
        var first = await Begin(capture.Resource, subject);
        var second = await Begin(capture.Resource, subject);
        (await work.TryPublishAsync(second.WorkUuid, NoWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await work.TryPublishAsync(first.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task DependencyPublicationAndReviewInvalidateConsumersWithoutFanoutDelivery()
    {
        var input = await Capture();
        var dependency = await Begin(input.Resource);
        await work.TryPublishAsync(dependency.WorkUuid, NoWrite);
        var subject = Subject();
        var consumer = await work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-1",
            [],
            [dependency.SubjectUuid]
        );
        await work.TryPublishAsync(consumer.WorkUuid, NoWrite);
        var stale = await work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-1",
            [],
            [dependency.SubjectUuid]
        );
        var nextDependency = await Begin(
            input.Resource,
            (await work.GetSubjectAsync(dependency.SubjectUuid))!.Subject
        );
        await work.TryPublishAsync(nextDependency.WorkUuid, NoWrite);
        (await work.GetPendingSubjectsAsync())
            .Should()
            .Contain(s => s.SubjectUuid == consumer.SubjectUuid);
        (await work.TryPublishAsync(stale.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var afterPublication = await work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-1",
            [],
            [dependency.SubjectUuid]
        );
        await work.TryPublishAsync(afterPublication.WorkUuid, NoWrite);
        await work.ApplyReviewAsync(Review(dependency.SubjectUuid), NoWrite);
        (await work.GetPendingSubjectsAsync())
            .Should()
            .Contain(s => s.SubjectUuid == consumer.SubjectUuid);
    }

    [Test]
    public async Task ReviewCanonicalReplayAndEditorialCasPreserveOperatorCorrection()
    {
        var capture = await Capture();
        var subject = Subject();
        var ticket = await Begin(capture.Resource, subject);
        var command = Review(ticket.SubjectUuid);
        var review = await work.ApplyReviewAsync(
            command,
            (c, t, ct) => WriteValue(c, t, command.OperationUuid, "reviewed", ct)
        );
        review.EditorialRevision.Should().Be(1);
        var replay = await work.ApplyReviewAsync(
            command with
            {
                Payload = JsonSerializer.Deserialize<JsonElement>("{\"b\":2,\"a\":1}"),
            },
            NeverWrite
        );
        replay.Replayed.Should().BeTrue();
        var conflict = () =>
            work.ApplyReviewAsync(command with { Reason = "different" }, NeverWrite);
        await conflict.Should().ThrowAsync<NormalizationConflictException>();
        var stale = () =>
            work.ApplyReviewAsync(command with { OperationUuid = Guid.NewGuid() }, NeverWrite);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await ReadValue(command.OperationUuid)).Should().Be("reviewed");
        (await work.TryPublishAsync(ticket.WorkUuid, NeverWrite))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        var current = await work.GetSubjectAsync(ticket.SubjectUuid);
        current!.EditorialRevision.Should().Be(1);
        current.DesiredGeneration.Should().Be(2);
    }

    [Test]
    public async Task PublicationFailureAndCancellationRollBackTypedWritesAndRemainRetryable()
    {
        var capture = await Capture();
        var ticket = await Begin(capture.Resource);
        async Task Fail(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
        {
            await WriteValue(c, t, ticket.WorkUuid, "failed", ct);
            throw new InvalidOperationException("domain validation failed");
        }
        var failure = () => work.TryPublishAsync(ticket.WorkUuid, Fail);
        await failure.Should().ThrowAsync<InvalidOperationException>();
        (await ReadValue(ticket.WorkUuid)).Should().BeNull();
        using var cancellation = new CancellationTokenSource();
        async Task Cancel(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
        {
            await WriteValue(c, t, ticket.WorkUuid, "cancelled", ct);
            cancellation.Cancel();
        }
        var cancelled = () => work.TryPublishAsync(ticket.WorkUuid, Cancel, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await ReadValue(ticket.WorkUuid)).Should().BeNull();
        (
            await work.TryPublishAsync(
                ticket.WorkUuid,
                (c, t, ct) => WriteValue(c, t, ticket.WorkUuid, "retry", ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await ReadValue(ticket.WorkUuid)).Should().Be("retry");
    }

    [Test]
    public async Task ReviewCancellationRollsBackAuditAndAllowsExactRetry()
    {
        var capture = await Capture();
        var ticket = await Begin(capture.Resource);
        var command = Review(ticket.SubjectUuid);
        using var cancellation = new CancellationTokenSource();
        async Task Cancel(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct)
        {
            await WriteValue(c, t, command.OperationUuid, "cancelled", ct);
            cancellation.Cancel();
        }
        var cancelled = () => work.ApplyReviewAsync(command, Cancel, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await ReadValue(command.OperationUuid)).Should().BeNull();
        (await work.GetSubjectAsync(ticket.SubjectUuid))!.EditorialRevision.Should().Be(0);
        (
            await work.ApplyReviewAsync(
                command,
                (c, t, ct) => WriteValue(c, t, command.OperationUuid, "reviewed", ct)
            )
        )
            .Replayed.Should()
            .BeFalse();
    }

    private Task<NormalizationWorkTicket> Begin(
        NormalizationResourceKey input,
        NormalizationSubjectKey? subject = null
    ) => work.BeginWorkAsync(Guid.NewGuid(), subject ?? Subject(), "policy-1", [input], []);

    private static NormalizationSubjectKey Subject() =>
        new("people", "recording_credits", Guid.NewGuid());

    private static Task NoWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        Task.CompletedTask;

    private static Task NeverWrite(NpgsqlConnection c, NpgsqlTransaction t, CancellationToken ct) =>
        throw new AssertionException("A stale or replayed operation invoked the domain writer.");

    private static NormalizationReviewCommand Review(Guid subjectUuid) =>
        new(
            Guid.NewGuid(),
            subjectUuid,
            0,
            "replace",
            JsonSerializer.Deserialize<JsonElement>("{\"a\":1,\"b\":2}"),
            "Correct person identity",
            "trusted-test-operator"
        );

    private async Task<NormalizationInputCapture> Capture()
    {
        var capture = new NormalizationInputCapture(
            Guid.NewGuid(),
            new("archive.org", "item", Guid.NewGuid().ToString()),
            Encoding.UTF8.GetBytes("A"),
            "application/json",
            "capture-1",
            0,
            DateTimeOffset.UtcNow
        );
        await evidence.CaptureInputAsync(capture);
        return capture;
    }

    private Task<NormalizationCaptureResult> Change(
        NormalizationInputCapture capture,
        string value,
        long expectedGeneration
    ) =>
        evidence.CaptureInputAsync(
            capture with
            {
                FetchUuid = Guid.NewGuid(),
                Payload = Encoding.UTF8.GetBytes(value),
                ExpectedGeneration = expectedGeneration,
            }
        );

    private static Task WriteValue(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid uuid,
        string value,
        CancellationToken ct
    ) =>
        c.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO normalization.work_test_values(uuid,value) VALUES(@uuid,@value)",
                new { uuid, value },
                t,
                cancellationToken: ct
            )
        );

    private Task<string?> ReadValue(Guid uuid) =>
        db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM normalization.work_test_values WHERE uuid=@uuid",
                new { uuid }
            )
        );
}
