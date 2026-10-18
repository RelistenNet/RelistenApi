using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using RelistenApiTests.Services;

namespace RelistenApiTests.Ingestion;

/// <summary>Tests the real operator executable and accepted read contracts, without provider services.</summary>
[TestFixture, NonParallelizable]
public sealed class TestNormalizationRepertoireCommand
{
    private string directory = null!;

    [SetUp]
    public void Start()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "relisten-repertoire-command-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void Stop() => Directory.Delete(directory, recursive: true);

    [TestCase("Test"), TestCase("Development"), Category("LocalIngestion")]
    public async Task WorkerResolvesAStoredDictionaryAndReplaysWithoutChangingAcceptedIdentities(
        string environment
    )
    {
        using var fixture = new NormalizationDomainFixture();
        var source = await fixture.Source();
        var artistUuid = await fixture.Db.WithConnection(connection =>
            connection.QuerySingleAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );
        var importedSongUuid = Guid.NewGuid();
        var privateTitle = "private title sentinel " + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "INSERT INTO setlist_songs(artist_id,uuid,name,slug,upstream_identifier,updated_at) VALUES(@artist,@uuid,@name,@key,@key,now())",
                new
                {
                    artist = source.ArtistId,
                    uuid = importedSongUuid,
                    name = privateTitle,
                    key = importedSongUuid.ToString("D"),
                }
            )
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var snapshot = (
            await new NormalizationImportedCatalogService(
                fixture.Db,
                evidence
            ).RefreshArtistRepertoireAsync(artistUuid)
        )!;
        var sourceFile = Path.Combine(directory, "request.json");
        await File.WriteAllTextAsync(
            sourceFile,
            JsonSerializer.Serialize(
                new RepertoireResolutionRequest(
                    new ImportedRepertoireResolutionSource(snapshot.Capture.Fetch.FetchUuid),
                    artistUuid
                ),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        var attemptUuid = Guid.NewGuid();
        string[] arguments =
        [
            "--normalize-repertoire",
            "resolve",
            "--attempt",
            attemptUuid.ToString("D"),
            "--request",
            sourceFile,
        ];

        // Each invocation resumes the same durable attempt. Completion must not depend on
        // starting workers or refreshing the immutable dictionary between saved phases.
        var completed = await RunUntilSettled(arguments, environment, privateTitle);
        using var receipt = JsonDocument.Parse(completed.Output);
        receipt.RootElement.GetProperty("attempt_uuid").GetGuid().Should().Be(attemptUuid);
        receipt
            .RootElement.GetProperty("result")
            .GetProperty("disposition")
            .GetString()
            .Should()
            .Be("completed");
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        var accepted = await music.SearchRepertoireAsync(artistUuid);
        accepted.Should().ContainSingle();
        accepted.Single().Value.DisplayTitle.Should().Be(privateTitle);
        var mapping = await music.GetImportedSongsAsync(accepted.Single().Value.Uuid);
        mapping.Should().ContainSingle();
        mapping.Single().Value.ImportedSongUuid.Should().Be(importedSongUuid);

        var replay = await RunWorker(arguments, environment, LocalTestDatabase.DatabaseUrl);
        replay.ExitCode.Should().Be(0, replay.Error);
        replay.Output.Should().NotContain(privateTitle);
        using var replayed = JsonDocument.Parse(replay.Output);
        replayed
            .RootElement.GetProperty("result")
            .GetProperty("replayed")
            .GetBoolean()
            .Should()
            .BeTrue();
        var inspection = await RunWorker(
            ["--normalize-repertoire", "get", "--attempt", attemptUuid.ToString("D")],
            environment,
            LocalTestDatabase.DatabaseUrl
        );
        inspection.ExitCode.Should().Be(0, inspection.Error);
        inspection.Output.Should().NotContain(privateTitle);
        var decisions = await RunWorker(
            [
                "--normalize-repertoire",
                "decisions",
                "--attempt",
                attemptUuid.ToString("D"),
                "--limit",
                "1",
            ],
            environment,
            LocalTestDatabase.DatabaseUrl
        );
        decisions.ExitCode.Should().Be(0, decisions.Error);
        decisions.Output.Should().NotContain(privateTitle);
        using var page = JsonDocument.Parse(decisions.Output);
        page.RootElement.GetProperty("decisions").GetArrayLength().Should().Be(1);
        page.RootElement.GetProperty("decisions")[0]
            .GetProperty("repertoire_song_uuid")
            .GetGuid()
            .Should()
            .Be(accepted.Single().Value.Uuid);

        var registrationUuid = receipt.RootElement.GetProperty("registration_uuid").GetGuid();
        string[] retryArguments =
        [
            "--normalize-repertoire",
            "retry",
            "--attempt",
            Guid.NewGuid().ToString("D"),
            "--registration",
            registrationUuid.ToString("D"),
        ];
        await RunUntilSettled(retryArguments, environment, privateTitle);
        (await music.SearchRepertoireAsync(artistUuid)).Should().BeEquivalentTo(accepted);
        (await music.GetImportedSongsAsync(accepted.Single().Value.Uuid))
            .Should()
            .BeEquivalentTo(mapping);
    }

    [TestCase("unknown")]
    [TestCase("resolve")]
    [TestCase("retry")]
    [TestCase("get")]
    public async Task IncompleteOperationFailsBeforeCatalogServicesAreResolved(string operation)
    {
        var result = await RunWorker(["--normalize-repertoire", operation]);

        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result.Error.Should().NotContain("DATABASE_URL").And.NotContain("REDIS_URL");
    }

    [TestCase("--normalize-repertoire")]
    [TestCase("--attempt")]
    [TestCase("--request")]
    [TestCase("--registration")]
    [TestCase("--after")]
    [TestCase("--limit")]
    public async Task MissingSelectorValueIsAControlledFailure(string option)
    {
        string[] arguments =
            option == "--normalize-repertoire"
                ? [option]
                : ["--normalize-repertoire", "resolve", option];
        var result = await RunWorker(arguments);

        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result
            .Error.Should()
            .NotBeEmpty()
            .And.NotContain("DATABASE_URL")
            .And.NotContain("REDIS_URL");
    }

    [TestCase("--attempt", "not-a-uuid")]
    [TestCase("--attempt", "00000000-0000-0000-0000-000000000000")]
    [TestCase("--limit", "201")]
    [TestCase("--after", "bad-cursor")]
    [TestCase("--registration", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidOrUnrelatedSelectorsFailBeforeCatalogResolution(
        string option,
        string value
    )
    {
        var result = await RunWorker([
            "--normalize-repertoire",
            "decisions",
            "--attempt",
            Guid.NewGuid().ToString("D"),
            option,
            value,
        ]);

        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result.Error.Should().NotContain("DATABASE_URL").And.NotContain("REDIS_URL");
    }

    [TestCase("missing-kind")]
    [TestCase("unknown-member")]
    [TestCase("empty-id")]
    [TestCase("malformed")]
    [TestCase("oversized")]
    public async Task InvalidSourceFileFailsWithoutOpeningTheCatalogOrEchoingItsContents(
        string variant
    )
    {
        var path = Path.Combine(directory, "request.json");
        var sentinel = "do-not-echo-request-payload";
        var payload = variant switch
        {
            "missing-kind" => "{\"source\":{\"fetchUuid\":\"" + Guid.NewGuid() + "\"}}",
            "unknown-member" => "{\"source\":{\"kind\":\"imported_dictionary\",\"fetchUuid\":\""
                + Guid.NewGuid()
                + "\"},\"winner\":\""
                + sentinel
                + "\"}",
            "empty-id" =>
                "{\"source\":{\"kind\":\"imported_dictionary\",\"fetchUuid\":\"00000000-0000-0000-0000-000000000000\"}}",
            "malformed" => "{\"source\":\"" + sentinel,
            _ => new string('x', 16 * 1024 + 1) + sentinel,
        };
        await File.WriteAllTextAsync(path, payload);

        var result = await RunWorker([
            "--normalize-repertoire",
            "resolve",
            "--attempt",
            Guid.NewGuid().ToString("D"),
            "--request",
            path,
        ]);

        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result
            .Error.Should()
            .NotContain(sentinel)
            .And.NotContain("DATABASE_URL")
            .And.NotContain("REDIS_URL");
    }

    private static async Task<WorkerResult> RunUntilSettled(
        string[] arguments,
        string environment,
        string privateTitle
    )
    {
        for (var invocation = 0; invocation < 20; invocation++)
        {
            var result = await RunWorker(arguments, environment, LocalTestDatabase.DatabaseUrl);
            result.Output.Should().NotContain(privateTitle);
            result.ExitCode.Should().BeOneOf([0, 2], result.Error);
            if (result.ExitCode == 0)
            {
                return result;
            }
        }
        throw new AssertionException(
            "The worker did not settle within the bounded resume attempts."
        );
    }

    private static async Task<WorkerResult> RunWorker(
        IEnumerable<string> arguments,
        string environment = "Test",
        string? database = null
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
            var key in new[]
            {
                "DATABASE_URL",
                "REDIS_URL",
                "PGBOUNCER_WRITE_HOST",
                "PGBOUNCER_READ_ONLY_HOST",
                "Temporal__PayloadRedisUrl",
                "OPENAI_API_KEY",
            }
        )
        {
            start.Environment.Remove(key);
        }
        if (database != null)
        {
            start.Environment["DATABASE_URL"] = database;
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
