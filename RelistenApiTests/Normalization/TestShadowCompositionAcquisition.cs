using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestShadowCompositionAcquisition
{
    private string directory = null!;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [SetUp]
    public void Start() =>
        directory = Path.Combine(Path.GetTempPath(), "composition-acquire-" + Guid.NewGuid());

    [TearDown]
    public void Finish()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    public void SharedTitlesShareWorkSearchButKeepPerformerQueriesAndOriginalFailedCommand()
    {
        var a = Seed("Shared Song");
        var b = Seed("Shared Song");
        var initial = ShadowCompositionAcquisition.Plan([a, b], []);
        initial.Commands.Should().HaveCount(3);
        var work = initial.Commands.Single(command =>
            command.Search?.Query.Kind == MusicBrainzLookupKind.Work
        );
        work.Search!.Query.RelatedArtist.Should().BeNull();
        work.CatalogUuids.Should().BeEquivalentTo([a.CatalogUuid, b.CatalogUuid]);
        var original = work with
        {
            CommandUuid = Guid.NewGuid(),
            Search = work.Search with { CacheMode = MusicBrainzCacheMode.Refresh },
        };
        var resumed = ShadowCompositionAcquisition.Plan([a, b], [], [original]);
        resumed
            .Commands.Single(command => command.Resource == work.Resource)
            .CommandUuid.Should()
            .Be(original.CommandUuid);
        resumed
            .Commands.Single(command => command.Resource == work.Resource)
            .Search.Should()
            .Be(original.Search);
        resumed.ExternalUniverseComplete.Should().BeFalse();
    }

    [Test]
    public void FullPageSchedulesNextPageAndEveryAlternativeThenHydratesRecordingWork()
    {
        var seed = Seed("Shared Song");
        var query = ShadowCompositionAcquisition
            .Plan([seed], [])
            .Commands.Single(command =>
                command.Search?.Query.Kind == MusicBrainzLookupKind.Recording
            );
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToArray();
        var page = Input(
            query.Resource,
            "musicbrainz-recording-search-json-v1",
            new
            {
                count = 51,
                offset = 0,
                recordings = ids.Select(id => new { id, title = "Shared Song" }).ToArray(),
            }
        );
        var work = Guid.NewGuid();
        var recording = Input(
            new("musicbrainz.org", "recording", ids[0].ToString("D")),
            "musicbrainz-recording-work-json-v1",
            new
            {
                id = ids[0],
                title = "Shared Song",
                relations = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["type"] = "performance",
                        ["direction"] = "forward",
                        ["work"] = new { id = work, title = "Authored Work" },
                        ["target-type"] = "work",
                    },
                },
            }
        );
        var plan = ShadowCompositionAcquisition.Plan([seed], [page, recording]);
        plan.Commands.Count(command => command.Lookup?.Kind == MusicBrainzLookupKind.Recording)
            .Should()
            .Be(50);
        plan.Commands.Should()
            .Contain(command => command.Lookup != null && command.Lookup.Mbid == work);
        plan.Commands.Should()
            .Contain(command =>
                command.Search != null
                && command.Search.Query == query.Search!.Query
                && command.Search.Offset == 50
            );
    }

    [Test]
    public async Task ReservationsSurviveInterruptedCallsAndOriginalFailedCommandsAreNotRetried()
    {
        var seed = Seed("Shared Song");
        var client = new FakeClient { FailAdvance = true };
        var runner = new ShadowCompositionAcquisitionRunner(client);
        var limits = new ShadowCompositionAcquisitionLimits(
            1,
            8 * 1024 * 1024,
            TimeSpan.FromMinutes(1)
        );
        Func<Task> interrupted = async () => await runner.RunAsync([seed], [], directory, limits);
        await interrupted.Should().ThrowAsync<IOException>();
        var partial = JsonSerializer.Deserialize<ShadowCompositionAcquisitionCheckpoint>(
            File.ReadAllText(Path.Combine(directory, "checkpoint.json")),
            Json
        )!;
        partial.ReservedServiceCalls.Should().Be(1);
        client.FailAdvance = false;
        var resumed = await runner.RunAsync([seed], [], directory, limits);
        client.Advances.Should().Be(1);
        resumed.State.Should().Be("run_bound_reached");
        resumed.ReservedServiceCalls.Should().Be(1);
    }

    [Test]
    public async Task CompletedCacheReceiptsExportExactBytesWithNoServiceReservation()
    {
        var seed = Seed("Shared Song");
        var client = new FakeClient();
        foreach (var command in ShadowCompositionAcquisition.Plan([seed], []).Commands)
        {
            var input = Input(
                command.Resource,
                command.Search!.Query.Kind == MusicBrainzLookupKind.Work
                    ? "musicbrainz-work-search-json-v1"
                    : "musicbrainz-recording-search-json-v1",
                command.Search.Query.Kind == MusicBrainzLookupKind.Work
                    ? JsonSerializer.SerializeToElement(
                        new
                        {
                            count = 0,
                            offset = 0,
                            works = Array.Empty<object>(),
                        }
                    )
                    : JsonSerializer.SerializeToElement(
                        new
                        {
                            count = 0,
                            offset = 0,
                            recordings = Array.Empty<object>(),
                        }
                    )
            );
            client.Inputs[input.Uuid] = input;
            client.Receipts[command.CommandUuid] = Receipt(command, input);
        }
        var result = await new ShadowCompositionAcquisitionRunner(client).RunAsync(
            [seed],
            [],
            directory,
            new(0, 0, TimeSpan.FromMinutes(1))
        );
        result.State.Should().Be("plan_exhausted");
        result.Inputs.Should().HaveCount(2);
        result.ReservedServiceCalls.Should().Be(0);
        client.Advances.Should().Be(0);
        foreach (var saved in result.Inputs)
        {
            var exact = JsonSerializer.Deserialize<NormalizationInputRevision>(
                File.ReadAllBytes(Path.Combine(directory, saved.File)),
                Json
            )!;
            exact.Payload.Should().Equal(client.Inputs[saved.Uuid].Payload);
        }
    }

    [Test]
    public async Task WrongReturnedRevisionUuidCannotEnterRetainedEvidence()
    {
        var seed = Seed("Shared Song");
        var client = new FakeClient();
        var command = ShadowCompositionAcquisition.Plan([seed], []).Commands.First();
        var input = Input(
            command.Resource,
            "musicbrainz-recording-search-json-v1",
            new
            {
                count = 0,
                offset = 0,
                recordings = Array.Empty<object>(),
            }
        );
        var expected = Guid.NewGuid();
        client.Receipts[command.CommandUuid] = Receipt(command, input) with
        {
            Result = new(MusicBrainzAcquisitionDisposition.Cached, new(expected, command.Resource)),
        };
        client.Inputs[expected] = input;
        Func<Task> act = async () =>
            await new ShadowCompositionAcquisitionRunner(client).RunAsync(
                [seed],
                [],
                directory,
                new(0, 0, TimeSpan.FromMinutes(1))
            );
        await act.Should().ThrowAsync<InvalidDataException>();
        Directory.GetFiles(directory, "input-*.json").Should().BeEmpty();
    }

    [Test]
    public async Task PendingAdmissionDefersOtherCommandsUntilObservedNotBefore()
    {
        var seed = Seed("Shared Song");
        var client = new FakeClient { ReturnGateOnce = true };
        var result = await new ShadowCompositionAcquisitionRunner(client).RunAsync(
            [seed],
            [],
            directory,
            new(2, 16 * 1024 * 1024, TimeSpan.FromSeconds(10))
        );
        client.EarlyGateCalls.Should().Be(0);
        client.Advances.Should().Be(2);
        result.ReservedServiceCalls.Should().Be(2);
        result.State.Should().Be("run_bound_reached");
    }

    [Test]
    public async Task ExpiredRestartReconcilesThePersistedReservationWithoutSendingAgain()
    {
        var seed = Seed("Shared Song");
        var client = new FakeClient { FailAdvance = true };
        var runner = new ShadowCompositionAcquisitionRunner(client);
        var limits = new ShadowCompositionAcquisitionLimits(
            1,
            8 * 1024 * 1024,
            TimeSpan.FromMinutes(1)
        );
        Func<Task> interrupted = async () => await runner.RunAsync([seed], [], directory, limits);
        await interrupted.Should().ThrowAsync<IOException>();
        var path = Path.Combine(directory, "checkpoint.json");
        var partial = JsonSerializer.Deserialize<ShadowCompositionAcquisitionCheckpoint>(
            File.ReadAllText(path),
            Json
        )!;
        var command = partial.Commands.Single(value =>
            partial.ReservedCommandUuids!.Contains(value.CommandUuid)
        );
        var input = Input(
            command.Resource,
            "musicbrainz-recording-search-json-v1",
            new
            {
                count = 0,
                offset = 0,
                recordings = Array.Empty<object>(),
            }
        );
        client.Inputs[input.Uuid] = input;
        client.Receipts[command.CommandUuid] = Receipt(command, input);
        // Simulate process loss before any returned receipt was checkpointed, followed by
        // a restart after the original immutable deadline and a durable DB completion.
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                partial with
                {
                    Deadline = DateTimeOffset.UtcNow.AddSeconds(-1),
                    Receipts = [],
                },
                Json
            )
        );
        var recovered = await runner.RunAsync([seed], [], directory, limits);
        recovered.State.Should().Be("deadline_reached");
        recovered.Inputs.Should().ContainSingle(value => value.Uuid == input.Uuid);
        recovered
            .Receipts.Should()
            .ContainSingle(value => value.CommandUuid == command.CommandUuid);
        client.Advances.Should().Be(1);
    }

    private static ShadowCompositionSeed Seed(string title) =>
        new(Guid.NewGuid(), "Performer", Guid.NewGuid(), Guid.NewGuid(), title, "/row/name");

    private static NormalizationInputRevision Input(
        NormalizationResourceKey resource,
        string version,
        object body
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        return new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            CaptureVersion = version,
            MediaType = "application/json",
            Payload = bytes,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
    }

    private static ShadowCompositionAcquisitionReceipt Receipt(
        ShadowCompositionAcquisitionCommand command,
        NormalizationInputRevision input
    ) =>
        new(
            command.CommandUuid,
            MusicBrainzAcquisitionState.Completed,
            null,
            new(MusicBrainzAcquisitionDisposition.Cached, new(input.Uuid, input.Resource)),
            [],
            JsonSerializer.SerializeToElement(new { fixture = true })
        );

    private sealed class FakeClient : IShadowCompositionAcquisitionClient
    {
        internal bool FailAdvance;
        internal int Advances;
        internal bool ReturnGateOnce;
        internal int EarlyGateCalls;
        private DateTimeOffset gateUntil;
        internal Dictionary<Guid, NormalizationInputRevision> Inputs { get; } = new();
        internal Dictionary<Guid, ShadowCompositionAcquisitionReceipt> Receipts { get; } = new();

        public Task<ShadowCompositionAcquisitionReceipt?> GetAsync(
            ShadowCompositionAcquisitionCommand command,
            CancellationToken ct
        ) => Task.FromResult(Receipts.GetValueOrDefault(command.CommandUuid));

        public Task<ShadowCompositionAcquisitionReceipt> AdvanceAsync(
            ShadowCompositionAcquisitionCommand command,
            CancellationToken ct
        )
        {
            Advances++;
            if (ReturnGateOnce && Advances == 1)
            {
                gateUntil = DateTimeOffset.UtcNow.AddMilliseconds(1600);
                var pending = new ShadowCompositionAcquisitionReceipt(
                    command.CommandUuid,
                    MusicBrainzAcquisitionState.Pending,
                    gateUntil,
                    null,
                    [],
                    JsonSerializer.SerializeToElement(new { fixture = true })
                );
                Receipts[command.CommandUuid] = pending;
                return Task.FromResult(pending);
            }
            if (DateTimeOffset.UtcNow < gateUntil)
            {
                EarlyGateCalls++;
            }
            if (FailAdvance)
            {
                throw new IOException("Synthetic interrupted service call.");
            }
            var failed = new ShadowCompositionAcquisitionReceipt(
                command.CommandUuid,
                MusicBrainzAcquisitionState.Failed,
                null,
                new(MusicBrainzAcquisitionDisposition.Failed, Reason: "synthetic_terminal"),
                [],
                JsonSerializer.SerializeToElement(new { fixture = true })
            );
            Receipts[command.CommandUuid] = failed;
            return Task.FromResult(failed);
        }

        public Task<NormalizationInputRevision?> ReadInputAsync(
            Guid inputUuid,
            CancellationToken ct
        ) => Task.FromResult(Inputs.GetValueOrDefault(inputUuid));
    }
}
