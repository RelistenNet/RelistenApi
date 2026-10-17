using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Data;
using RelistenApiTests.Services;

namespace RelistenApiTests.Ingestion;

/// <summary>Exercises the actual worker executable without Redis, Temporal or provider connections.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingCommand
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("Test")]
    [TestCase("Development")]
    public async Task WorkerProcessesAndReplaysASavedPageWithoutResolvingTemporalOrRedis(
        string environment
    )
    {
        var (first, second) = await Cohort();
        var artist = await ArtistUuid(first.ArtistId);
        var pageUuid = Guid.NewGuid();
        var arguments = Arguments(pageUuid, artist, 1);

        var firstRun = await RunWorker(arguments, environment: environment);

        firstRun.ExitCode.Should().Be(0, firstRun.Error);
        using var receipt = JsonDocument.Parse(firstRun.Output.Trim());
        receipt.RootElement.GetProperty("published").GetInt32().Should().Be(1);
        receipt.RootElement.GetProperty("settled").GetBoolean().Should().BeTrue();
        new[] { first.SourceUuid, second.SourceUuid }
            .Should()
            .Contain(receipt.RootElement.GetProperty("continuation_cursor").GetGuid());
        firstRun.Output.Should().NotContain("private credit sentinel");
        var page = (await new NormalizationRecordingPageStore(fixture.Db).GetAsync(pageUuid))!;
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var resource = new Relisten.Api.Models.Normalization.NormalizationResourceKey(
            "legacy_catalog",
            "recording",
            page.Items.Single().SourceUuid.ToString("D")
        );
        var before = await evidence.GetFetchHistoryAsync(resource);

        var replay = await RunWorker(arguments, environment: environment);

        replay.ExitCode.Should().Be(0, replay.Error);
        using var repeated = JsonDocument.Parse(replay.Output.Trim());
        repeated
            .RootElement.GetProperty("items")[0]
            .GetProperty("result")
            .GetProperty("replayed")
            .GetBoolean()
            .Should()
            .BeTrue();
        (await evidence.GetFetchHistoryAsync(resource)).Should().BeEquivalentTo(before);
        var changed = await RunWorker(Arguments(pageUuid, artist, 2), environment: environment);
        changed.ExitCode.Should().Be(1);
        (await new NormalizationRecordingPageStore(fixture.Db).GetAsync(pageUuid))!
            .Items.Should()
            .HaveCount(1);
    }

    [Test]
    public async Task RetryableItemFailureReturnsPartialExitCodeAndResumesTheSameSelection()
    {
        var (first, _) = await Cohort();
        var artist = await ArtistUuid(first.ArtistId);
        var pageUuid = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var function = "normalization.cli_transient_" + suffix;
        var trigger = "cli_transient_" + suffix;
        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                $"""
                CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.source_uuid='{first.SourceUuid}'::uuid THEN
                        RAISE EXCEPTION 'injected transient failure' USING ERRCODE='40001';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER {trigger} BEFORE INSERT ON normalization.recording_processing_attempts
                    FOR EACH ROW EXECUTE FUNCTION {function}();
                """
            )
        );
        try
        {
            var partial = await RunWorker(Arguments(pageUuid, artist, 2));

            partial.ExitCode.Should().Be(2, partial.Error);
            using var receipt = JsonDocument.Parse(partial.Output.Trim());
            receipt.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
            receipt.RootElement.GetProperty("published").GetInt32().Should().Be(1);
            receipt.RootElement.GetProperty("settled").GetBoolean().Should().BeFalse();
            receipt
                .RootElement.GetProperty("continuation_cursor")
                .ValueKind.Should()
                .Be(JsonValueKind.Null);
            var failed = receipt
                .RootElement.GetProperty("items")
                .EnumerateArray()
                .Single(item => item.GetProperty("failure").ValueKind != JsonValueKind.Null);
            failed
                .GetProperty("failure")
                .GetProperty("code")
                .GetString()
                .Should()
                .Be("database_failed");
            partial.Output.Should().NotContain("injected transient failure");
        }
        finally
        {
            await fixture.Db.WithWriteConnection(connection =>
                connection.ExecuteAsync(
                    $"DROP TRIGGER {trigger} ON normalization.recording_processing_attempts; DROP FUNCTION {function}();"
                )
            );
        }

        var resumed = await RunWorker(Arguments(pageUuid, artist, 2));

        resumed.ExitCode.Should().Be(0, resumed.Error);
        using var complete = JsonDocument.Parse(resumed.Output.Trim());
        complete.RootElement.GetProperty("published").GetInt32().Should().Be(2);
        complete.RootElement.GetProperty("failed").GetInt32().Should().Be(0);
        (await new NormalizationRecordingPageStore(fixture.Db).GetFailuresAsync(pageUuid))
            .Should()
            .ContainSingle();
    }

    [TestCase("--normalize-recordings", "invalid-uuid")]
    [TestCase("--normalize-recordings", "00000000-0000-0000-0000-000000000000")]
    [TestCase("--limit", "0")]
    [TestCase("--artist", "invalid-artist")]
    public async Task InvalidArgumentsFailBeforeCatalogOrTemporalServicesAreResolved(
        string option,
        string value
    )
    {
        var arguments = new List<string> { "--normalize-recordings", Guid.NewGuid().ToString("D") };
        if (option == "--normalize-recordings")
        {
            arguments[1] = value;
        }
        else
        {
            arguments.AddRange([option, value]);
        }

        var result = await RunWorker(arguments, configureDatabase: false);

        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result.Error.Should().NotContain("DATABASE_URL").And.NotContain("REDIS_URL");
    }

    [TestCase("--normalize-recordings")]
    [TestCase("--artist")]
    [TestCase("--after")]
    [TestCase("--limit")]
    public async Task MissingOptionValueReturnsAControlledInvalidRequest(string option)
    {
        string[] arguments =
            option == "--normalize-recordings"
                ? [option]
                : ["--normalize-recordings", Guid.NewGuid().ToString("D"), option];

        var result = await RunWorker(arguments, configureDatabase: false);

        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result
            .Error.Should()
            .NotBeEmpty()
            .And.NotContain("DATABASE_URL")
            .And.NotContain("REDIS_URL");
    }

    private async Task<(
        NormalizationDomainFixture.CatalogSource First,
        NormalizationDomainFixture.CatalogSource Second
    )> Cohort()
    {
        var first = await fixture.Source();
        var second = await fixture.Source();
        await fixture.Db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                "UPDATE sources SET artist_id=@ArtistId,taper='private credit sentinel' WHERE id IN (@FirstSourceId,@SecondSourceId)",
                new
                {
                    first.ArtistId,
                    FirstSourceId = first.SourceId,
                    SecondSourceId = second.SourceId,
                }
            );
            await connection.ExecuteAsync(
                "UPDATE source_tracks SET artist_id=@ArtistId,mp3_url=@url WHERE uuid=@TrackUuid",
                new
                {
                    first.ArtistId,
                    second.TrackUuid,
                    url = "https://example.test/" + second.SourceUuid + ".mp3",
                }
            );
        });
        return (first, second);
    }

    private Task<Guid> ArtistUuid(int id) =>
        fixture.Db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@id", new { id })
        );

    private static string[] Arguments(Guid pageUuid, Guid artistUuid, int limit) =>
        [
            "--normalize-recordings",
            pageUuid.ToString("D"),
            "--artist",
            artistUuid.ToString("D"),
            "--limit",
            limit.ToString(),
        ];

    private static async Task<WorkerResult> RunWorker(
        IEnumerable<string> arguments,
        bool configureDatabase = true,
        string environment = "Test"
    )
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = TestContext.CurrentContext.TestDirectory,
        };
        start.ArgumentList.Add(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "RelistenIngestionWorker.dll")
        );
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (
            var name in new[]
            {
                "DATABASE_URL",
                "REDIS_URL",
                "PGBOUNCER_WRITE_HOST",
                "PGBOUNCER_READ_ONLY_HOST",
                "Temporal__PayloadRedisUrl",
            }
        )
        {
            start.Environment.Remove(name);
        }
        if (configureDatabase)
        {
            start.Environment["DATABASE_URL"] = LocalTestDatabase.DatabaseUrl;
        }
        start.Environment["SENTRY_DSN"] = "";
        start.Environment["DOTNET_ENVIRONMENT"] = environment;
        start.Environment["Temporal__Address"] = "127.0.0.1:1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        return new(process.ExitCode, await output, await error);
    }

    private sealed record WorkerResult(int ExitCode, string Output, string Error);
}
