using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RelistenIngestionWorker;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol.Envelopes;

namespace RelistenApiTests.Ingestion;

/// <summary>Exercises worker reporting through the real SDK without sending events to Sentry.</summary>
[TestFixture, NonParallelizable]
public class TestWorkerLogging
{
    [Test]
    public void HostReportsErrorsAndExistingCapturesThenFlushesOnDisposal()
    {
        var transport = new RecordingTransport();
        var builder = CreateBuilder("https://public@example.invalid/1");
        var sentryOptions = WorkerLogging.Configure(builder)!;
        sentryOptions.Transport = transport;

        using (SentrySdk.Init(sentryOptions))
        using (var host = builder.Build())
        {
            var logger = host.Services.GetRequiredService<ILogger<TestWorkerLogging>>();
            using var scope = logger.BeginScope(
                new Dictionary<string, object>
                {
                    ["WorkflowId"] = "ingest/artist/test",
                    ["Artist"] = "test-artist",
                    ["Provider"] = "archive.org",
                }
            );

            logger.LogWarning("Retrying an upstream request");
            logger.LogError(new InvalidOperationException("Catalog write failed"), "Import failed");
            SentrySdk.CaptureException(new FormatException("Invalid upstream metadata"));
        }

        // Do not explicitly flush: worker shutdown must drain the SDK's queue.
        var events = transport.Events.ToArray();
        events.Should().HaveCount(2);
        foreach (var reported in events)
        {
            reported.GetProperty("environment").GetString().Should().Be("WorkerTest");

            var tags = reported.GetProperty("tags");
            tags.GetProperty("WorkflowId").GetString().Should().Be("ingest/artist/test");
            tags.GetProperty("Artist").GetString().Should().Be("test-artist");
            tags.GetProperty("Provider").GetString().Should().Be("archive.org");
        }

        events
            .Select(value =>
                value
                    .GetProperty("exception")
                    .GetProperty("values")[0]
                    .GetProperty("value")
                    .GetString()
            )
            .Should()
            .BeEquivalentTo("Catalog write failed", "Invalid upstream metadata");
    }

    [Test]
    public void HostRunsWithoutSentryWhenDsnIsMissing()
    {
        var builder = CreateBuilder(null);
        WorkerLogging.Configure(builder);
        using var host = builder.Build();

        host.Services.GetRequiredService<ILogger<TestWorkerLogging>>()
            .LogError("Local development error");

        SentrySdk.IsEnabled.Should().BeFalse();
        SentrySdk
            .CaptureException(new InvalidOperationException("Local error"))
            .Should()
            .Be(SentryId.Empty);
    }

    private static HostApplicationBuilder CreateBuilder(string? dsn)
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                DisableDefaults = true,
                EnvironmentName = "WorkerTest",
            }
        );
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["SENTRY_DSN"] = dsn }
        );

        return builder;
    }

    private sealed class RecordingTransport : ITransport
    {
        public ConcurrentQueue<JsonElement> Events { get; } = new();

        public async Task SendEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            foreach (var item in envelope.Items.Where(item => item.TryGetType() == "event"))
            {
                using var stream = new MemoryStream();
                await item.Payload.SerializeAsync(stream, null, cancellationToken);
                stream.Position = 0;
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken
                );
                Events.Enqueue(document.RootElement.Clone());
            }
        }
    }
}
