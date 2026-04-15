using FluentAssertions;

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
        script.Should().Contain("/api/v3/collections/aadam-jacobs/shows/on-this-day?month=1&day=1");
        script.Should().Contain("/api/v3/artists/delta");
        script.Should().Contain("active_distinct_items");
        script.Should().Contain("broken_links");
        script.Should().Contain("EXPLAIN (ANALYZE, BUFFERS)");
    }
}
