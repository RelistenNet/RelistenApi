using System.ComponentModel;
using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.RecurringJobExtensions;
using Relisten;

namespace RelistenApiTests.Collections;

[TestFixture]
public class TestAadamJacobsVerificationArtifacts
{
    [Test]
    public void RolloutSmokeCheckScriptContainsRequiredSqlAndApiChecks()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../"));
        var scriptPath = Path.Combine(repoRoot, "tools", "verify-aadam-jacobs-collection.sh");

        File.Exists(scriptPath).Should().BeTrue();

        var script = File.ReadAllText(scriptPath);
        script.Should().Contain("Run the AJC import job twice");
        script.Should().Contain("/api/v3/collections/aadam-jacobs");
        script.Should().Contain("/api/v3/collections/aadam-jacobs/artists");
        script.Should().Contain("/api/v3/collections/aadam-jacobs/years");
        script.Should().Contain("/api/v3/collections/aadam-jacobs/shows/popular-trending");
        script.Should().Contain("/api/v3/collections/aadam-jacobs/shows/recently-added");
        script.Should().Contain("/api/v3/collections/aadam-jacobs/shows/on-this-day?month=7&day=20");
        script.Should().Contain("/api/v3/artists/delta");
        script.Should().Contain("active_distinct_items");
        script.Should().Contain("broken_links");
        script.Should().Contain("EXPLAIN (ANALYZE, BUFFERS)");
    }

    [Test]
    public void AadamJacobsImportIsScheduledAwayFromOtherArtistImportJobs()
    {
        var method = typeof(ScheduledService).GetMethod(nameof(ScheduledService.ImportAadamJacobsCollection))!;
        var recurring = method.GetCustomAttribute<RecurringJobAttribute>();
        var queue = method.GetCustomAttribute<QueueAttribute>();
        var displayName = method.GetCustomAttribute<DisplayNameAttribute>();

        recurring.Should().NotBeNull();
        recurring!.Cron.Should().Be("0 9 * * *");
        recurring.Enabled.Should().BeTrue();
        queue.Should().NotBeNull();
        queue!.Queue.Should().Be("artist_import");
        method.GetCustomAttribute<DisableConcurrentExecutionAttribute>().Should().NotBeNull();
        displayName.Should().NotBeNull();
        displayName!.DisplayName.Should().Be("Import Aadam Jacobs Collection");
    }
}
