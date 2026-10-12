using System.Text.Json;
using FluentAssertions;
using Relisten.Ingestion.Transport;
using StackExchange.Redis;
using Temporalio.Activities;
using Temporalio.Api.Common.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Converters;
using Temporalio.Testing;
using Temporalio.Worker;
using Temporalio.Workflows;

namespace RelistenApiTests.Ingestion;

/// <summary>Checks large values at real Temporal command/result boundaries, including replay after restart.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public class TestRedisPayloadWorkflowTransport
{
    [Test]
    public async Task OversizedResultSurvivesWorkerRestartActivityHandoffContinuationAndReplay()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
            Assert.Ignore("Start local-dev/temporal and set RELISTEN_LOCAL_INGESTION_TESTS=1.");
        using var redis = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:16380");
        var database = redis.GetDatabase();
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var converter = DataConverter.Default with
        {
            PayloadCodec = new RedisPayloadCodec(database, environment.Client.Options.Namespace),
        };
        var client = new TemporalClient(
            environment.Client.Connection,
            new TemporalClientOptions
            {
                Namespace = environment.Client.Options.Namespace,
                DataConverter = converter,
            }
        );
        var workflowQueue = "codec-workflow-" + Guid.NewGuid();
        var consumeQueue = "codec-consumer-" + Guid.NewGuid();
        var activities = new TransportActivities();
        var handle = await client.StartWorkflowAsync(
            (TransportWorkflow workflow) => workflow.RunAsync(consumeQueue, null, 0),
            new WorkflowOptions("codec-transport-" + Guid.NewGuid(), workflowQueue)
        );
        var keys = new HashSet<string>();
        try
        {
            // The first worker commits the large result and next command to history.
            // No consumer is running yet, so stopping the worker cannot lose that handoff.
            using (var firstWorker = Worker(client, workflowQueue, activities))
            {
                await firstWorker.ExecuteAsync(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    while (
                        !(await handle.FetchHistoryAsync()).Events.Any(e =>
                            e.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                            == "CodecConsume"
                        )
                    )
                        await Task.Delay(25, timeout.Token);
                });
            }
            var firstHistory = await handle.FetchHistoryAsync();
            var resultPayload = firstHistory
                .Events.Single(e => e.ActivityTaskCompletedEventAttributes != null)
                .ActivityTaskCompletedEventAttributes.Result.Payloads_.Single();
            var key = ReferenceKey(resultPayload);
            keys.Add(key);
            await database.KeyExpireAsync(key, TimeSpan.FromHours(1));

            using (var restartedWorker = Worker(client, workflowQueue, activities))
            using (
                var consumer = new TemporalWorker(
                    client,
                    new TemporalWorkerOptions(consumeQueue).AddAllActivities(activities)
                )
            )
            {
                await restartedWorker.ExecuteAsync(() =>
                    consumer.ExecuteAsync(async () =>
                    {
                        (await handle.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(30)))
                            .Should()
                            .Be(new TransportResult(3, activities.Value.Text.Length));
                    })
                );
            }
            activities.Produced.Should().Be(1);
            activities.Consumed.Should().Be(3);
            (await database.KeyTimeToLiveAsync(key))
                .Should()
                .BeCloseTo(TimeSpan.FromDays(15), TimeSpan.FromSeconds(30));

            var replayer = new WorkflowReplayer(
                new WorkflowReplayerOptions
                {
                    DataConverter = converter,
                }.AddWorkflow<TransportWorkflow>()
            );
            var runId = handle.FirstExecutionRunId;
            var histories = new List<WorkflowHistory>();
            while (runId != null)
            {
                var history = await client.GetWorkflowHandle(handle.Id, runId).FetchHistoryAsync();
                histories.Add(history);
                var references = history.Events.SelectMany(e =>
                    e.ActivityTaskCompletedEventAttributes?.Result?.Payloads_
                    ?? e.ActivityTaskScheduledEventAttributes?.Input?.Payloads_
                    ?? e.WorkflowExecutionContinuedAsNewEventAttributes?.Input?.Payloads_
                    ?? e.WorkflowExecutionStartedEventAttributes?.Input?.Payloads_
                    ?? Enumerable.Empty<Payload>()
                );
                foreach (
                    var payload in references.Where(p =>
                        p.Metadata.TryGetValue("encoding", out var encoding)
                        && encoding.ToStringUtf8() == RedisPayloadCodec.Encoding
                    )
                )
                    keys.Add(ReferenceKey(payload));
                await replayer.ReplayWorkflowAsync(history);
                runId = history
                    .Events.LastOrDefault()
                    ?.WorkflowExecutionContinuedAsNewEventAttributes?.NewExecutionRunId;
            }
            histories.Should().HaveCount(3);
            keys.Should()
                .ContainSingle(
                    "the retained selection is identical across all activity and continuation payloads"
                );
        }
        finally
        {
            foreach (var key in keys)
                await database.KeyDeleteAsync(key);
        }
    }

    private static TemporalWorker Worker(
        ITemporalClient client,
        string queue,
        TransportActivities activities
    ) =>
        new(
            client,
            new TemporalWorkerOptions(queue)
                .AddWorkflow<TransportWorkflow>()
                .AddAllActivities(activities)
        );

    private static string ReferenceKey(Payload payload)
    {
        using var reference = JsonDocument.Parse(payload.Data.Memory);
        return reference.RootElement.GetProperty("key").GetString()!;
    }

    public record Selection(string Identity, string Text);

    public record TransportResult(int Consumed, int Length);

    public class TransportActivities
    {
        public Selection Value { get; } =
            new(Guid.NewGuid().ToString(), new string('x', 3 * 1024 * 1024));
        public int Produced { get; private set; }
        public int Consumed { get; private set; }

        [Activity("CodecProduce")]
        public Selection Produce()
        {
            Produced++;
            return Value;
        }

        [Activity("CodecConsume")]
        public int Consume(Selection selection)
        {
            selection.Should().Be(Value);
            Consumed++;
            return selection.Text.Length;
        }
    }

    [Workflow("CodecTransportTest")]
    public class TransportWorkflow
    {
        [WorkflowRun]
        public async Task<TransportResult> RunAsync(
            string consumerQueue,
            Selection? selection,
            int progress
        )
        {
            selection ??= await Workflow.ExecuteActivityAsync(
                (TransportActivities activity) => activity.Produce(),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) }
            );
            var length = await Workflow.ExecuteActivityAsync(
                (TransportActivities activity) => activity.Consume(selection),
                new ActivityOptions
                {
                    TaskQueue = consumerQueue,
                    StartToCloseTimeout = TimeSpan.FromSeconds(30),
                }
            );
            if (progress < 2)
                throw Workflow.CreateContinueAsNewException(
                    (TransportWorkflow workflow) =>
                        workflow.RunAsync(consumerQueue, selection, progress + 1)
                );
            return new TransportResult(progress + 1, length);
        }
    }
}
