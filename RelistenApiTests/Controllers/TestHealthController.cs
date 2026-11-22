using System.Collections.Frozen;
using System.Threading;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten.Controllers;
using Relisten.Search;
using Relisten.Services.Health;

namespace RelistenApiTests.Controllers;

[TestFixture]
public class TestHealthController
{
    [Test]
    public void Live_ReturnsOkWithoutCheckingDependencies()
    {
        var readinessCheck = new StubReadinessCheck();
        var controller = new HealthController(
            readinessCheck,
            NullLogger<HealthController>.Instance
        );

        var result = controller.Live();

        result.Should().BeOfType<OkObjectResult>();
        readinessCheck.Calls.Should().Be(0);
    }

    [Test]
    public void SearchStartupPassesAsSoonAsEvidenceIsPublished()
    {
        var clock = new StartupClock();
        var evidence = new CatalogQueryEvidenceStore(clock);
        var readinessCheck = new StubReadinessCheck();
        var controller = new HealthController(
            readinessCheck,
            NullLogger<HealthController>.Instance
        );

        controller
            .SearchStartup(evidence)
            .Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(503);

        clock.Elapsed = TimeSpan.FromSeconds(12);
        evidence.Publish(
            new CatalogQueryEvidence(
                "entities_test",
                SearchVocabulary.Empty,
                SearchPassages.Empty,
                FrozenDictionary<string, long>.Empty,
                FrozenSet<string>.Empty
            )
        );
        controller.SearchStartup(evidence).Should().BeOfType<OkObjectResult>();
        readinessCheck.Calls.Should().Be(0);
    }

    [Test]
    public void SearchStartupPassesAfterSixtySecondsWithoutEvidence()
    {
        var clock = new StartupClock();
        var evidence = new CatalogQueryEvidenceStore(clock);
        var readinessCheck = new StubReadinessCheck();
        var controller = new HealthController(
            readinessCheck,
            NullLogger<HealthController>.Instance
        );

        clock.Elapsed = TimeSpan.FromSeconds(59.999);
        controller
            .SearchStartup(evidence)
            .Should()
            .BeOfType<ObjectResult>()
            .Which.StatusCode.Should()
            .Be(503);

        clock.Elapsed = TimeSpan.FromSeconds(60);
        controller.SearchStartup(evidence).Should().BeOfType<OkObjectResult>();
        readinessCheck.Calls.Should().Be(0);
    }

    [Test]
    public async Task Ready_WhenDatabasePathsAreHealthy_ReturnsOk()
    {
        var readinessCheck = new StubReadinessCheck();
        var controller = new HealthController(
            readinessCheck,
            NullLogger<HealthController>.Instance
        );

        var result = await controller.Ready(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        readinessCheck.Calls.Should().Be(1);
    }

    [Test]
    public async Task Ready_WhenADatabasePathFails_ReturnsServiceUnavailable()
    {
        var readinessCheck = new StubReadinessCheck(
            new InvalidOperationException("database unavailable")
        );
        var controller = new HealthController(
            readinessCheck,
            NullLogger<HealthController>.Instance
        );

        var result = await controller.Ready(CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(503);
    }

    private sealed class StartupClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;
    }

    private sealed class StubReadinessCheck : IReadinessCheck
    {
        private readonly Exception? _exception;

        public StubReadinessCheck(Exception? exception = null)
        {
            _exception = exception;
        }

        public int Calls { get; private set; }

        public Task CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return _exception == null ? Task.CompletedTask : Task.FromException(_exception);
        }
    }
}
