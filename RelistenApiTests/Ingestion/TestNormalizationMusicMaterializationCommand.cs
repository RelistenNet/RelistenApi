using System.Diagnostics;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using RelistenApiTests.Services;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Ingestion;

/// <summary>Exercises real worker commands and accepted reads with retained synthetic source accounts.</summary>
[TestFixture, NonParallelizable]
public sealed class TestNormalizationMusicMaterializationCommand
{
    private const string Setlists = "--normalize-setlists";
    private const string RecordingContents = "--normalize-recording-contents";

    [TestCase(Setlists), TestCase(RecordingContents), Category("LocalIngestion")]
    public async Task WorkerPublishesResumesAndRetriesWithoutChangingAcceptedIdentities(
        string command
    )
    {
        using var fixture = new NormalizationDomainFixture();
        var source = await PrepareSource(fixture, command == Setlists);
        var attemptUuid = Guid.NewGuid();
        string[] arguments =
        [
            command,
            "process",
            "--attempt",
            attemptUuid.ToString("D"),
            "--repertoire-attempt",
            source.RepertoireAttemptUuid.ToString("D"),
        ];

        var first = await RunWorker(arguments, LocalTestDatabase.DatabaseUrl);
        first.ExitCode.Should().Be(2, first.Error);
        first.Output.Should().NotContain(source.PrivateTitle);
        var recovering = await FindRecoveryAttempt(command, attemptUuid);
        recovering
            .Should()
            .BeTrue("the first durable phase must be discoverable after the process exits");

        var completed = await RunUntilCompleted(arguments, source.PrivateTitle);
        var registrationUuid = completed.GetProperty("registration_uuid").GetGuid();
        var reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        Guid acceptedUuid;
        long acceptedRevision;
        if (command == Setlists)
        {
            acceptedUuid = completed.GetProperty("edition_uuid").GetGuid();
            var accepted = (await music.GetSetlistStructureAsync(acceptedUuid))!;
            accepted.Value.Occurrences.Should().ContainSingle();
            accepted.Value.Occurrences.Single().OriginalText.Should().Be(source.PrivateTitle);
            acceptedRevision = accepted.Revision;
            (await reads.ListOccurrencesAsync(acceptedUuid))!.items.Should().ContainSingle();
        }
        else
        {
            var contents = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items;
            var accepted = contents.Should().ContainSingle().Which;
            accepted.original_text.Should().Be(source.PrivateTitle);
            accepted.occurrence_uuid.Should().BeNull();
            accepted.start_ms.Should().BeNull();
            accepted.end_ms.Should().BeNull();
            acceptedUuid = accepted.uuid;
            acceptedRevision = (await music.GetTrackContentsAsync(source.TrackUuid))
                .Single()
                .Revision;
        }

        var replay = await RunWorker(arguments, LocalTestDatabase.DatabaseUrl);
        replay.ExitCode.Should().Be(0, replay.Error);
        replay.Output.Should().NotContain(source.PrivateTitle);
        using var replayed = JsonDocument.Parse(replay.Output);
        replayed
            .RootElement.GetProperty("result")
            .GetProperty("replayed")
            .GetBoolean()
            .Should()
            .BeTrue();

        var inspection = await RunWorker(
            [command, "get", "--attempt", attemptUuid.ToString("D")],
            LocalTestDatabase.DatabaseUrl
        );
        inspection.ExitCode.Should().Be(0, inspection.Error);
        inspection.Output.Should().NotContain(source.PrivateTitle);
        using var inspected = JsonDocument.Parse(inspection.Output);
        inspected.RootElement.GetProperty("attempt_uuid").GetGuid().Should().Be(attemptUuid);
        if (command == Setlists)
        {
            inspected.RootElement.GetProperty("edition_uuid").GetGuid().Should().Be(acceptedUuid);
        }

        var decisions = await RunWorker(
            [command, "decisions", "--attempt", attemptUuid.ToString("D"), "--limit", "1"],
            LocalTestDatabase.DatabaseUrl
        );
        decisions.ExitCode.Should().Be(0, decisions.Error);
        decisions.Output.Should().NotContain(source.PrivateTitle);
        using var page = JsonDocument.Parse(decisions.Output);
        page.RootElement.GetProperty("decisions").GetArrayLength().Should().Be(1);
        page.RootElement.GetProperty("decisions")[0]
            .GetProperty("decision_uuid")
            .GetGuid()
            .Should()
            .NotBeEmpty();

        var pending = await RunWorker(
            [command, "pending", "--limit", "1"],
            LocalTestDatabase.DatabaseUrl
        );
        pending.ExitCode.Should().Be(0, pending.Error);
        pending.Output.Should().NotContain(source.PrivateTitle);
        using var pendingPage = JsonDocument.Parse(pending.Output);
        pendingPage
            .RootElement.GetProperty("registrations")
            .GetArrayLength()
            .Should()
            .BeLessThanOrEqualTo(1);

        var retry = await RunUntilCompleted(
            [
                command,
                "retry",
                "--attempt",
                Guid.NewGuid().ToString("D"),
                "--registration",
                registrationUuid.ToString("D"),
            ],
            source.PrivateTitle
        );
        retry.GetProperty("registration_uuid").GetGuid().Should().Be(registrationUuid);
        retry.GetProperty("attempt_uuid").GetGuid().Should().NotBe(attemptUuid);
        if (command == Setlists)
        {
            retry.GetProperty("edition_uuid").GetGuid().Should().Be(acceptedUuid);
            (await music.GetSetlistStructureAsync(acceptedUuid))!
                .Revision.Should()
                .Be(acceptedRevision);
        }
        else
        {
            var accepted = (await music.GetTrackContentsAsync(source.TrackUuid))
                .Should()
                .ContainSingle()
                .Which;
            accepted.Value.Uuid.Should().Be(acceptedUuid);
            accepted.Revision.Should().Be(acceptedRevision);
        }
    }

    [TestCase(Setlists), TestCase(RecordingContents), Category("LocalIngestion")]
    public async Task UnknownAttemptReturnsNotFoundWithoutAdmittingWork(string command)
    {
        using var fixture = new NormalizationDomainFixture();
        var attemptUuid = Guid.NewGuid();

        var result = await RunWorker(
            [command, "get", "--attempt", attemptUuid.ToString("D")],
            LocalTestDatabase.DatabaseUrl
        );

        result.ExitCode.Should().Be(1, result.Error);
        using var receipt = JsonDocument.Parse(result.Output);
        receipt.RootElement.GetProperty("attempt_uuid").GetGuid().Should().Be(attemptUuid);
        receipt.RootElement.GetProperty("state").GetString().Should().Be("not_found");
    }

    [TestCase(Setlists, "unknown")]
    [TestCase(Setlists, "process")]
    [TestCase(RecordingContents, "retry")]
    [TestCase(RecordingContents, "get")]
    public async Task InvalidOrIncompleteOperationFailsBeforeCatalogResolution(
        string command,
        string operation
    )
    {
        await AssertInvalid([command, operation]);
    }

    [TestCase(Setlists, "--attempt", "not-a-uuid")]
    [TestCase(Setlists, "--repertoire-attempt", "00000000-0000-0000-0000-000000000000")]
    [TestCase(RecordingContents, "--attempt", "00000000-0000-0000-0000-000000000000")]
    [TestCase(RecordingContents, "--repertoire-attempt", "not-a-uuid")]
    public async Task InvalidSourceCoordinatesFailBeforeCatalogResolution(
        string command,
        string selector,
        string value
    )
    {
        var attempt = selector == "--attempt" ? value : Guid.NewGuid().ToString("D");
        var repertoire = selector == "--repertoire-attempt" ? value : Guid.NewGuid().ToString("D");
        await AssertInvalid([
            command,
            "process",
            "--attempt",
            attempt,
            "--repertoire-attempt",
            repertoire,
        ]);
    }

    [TestCase(Setlists, "--limit", "0")]
    [TestCase(Setlists, "--limit", "201")]
    [TestCase(RecordingContents, "--after", "invalid-cursor")]
    [TestCase(RecordingContents, "--after", "00000000-0000-0000-0000-000000000000")]
    public async Task InvalidDecisionPageFailsBeforeCatalogResolution(
        string command,
        string selector,
        string value
    )
    {
        await AssertInvalid([
            command,
            "decisions",
            "--attempt",
            Guid.NewGuid().ToString("D"),
            selector,
            value,
        ]);
    }

    [TestCase(Setlists)]
    [TestCase(RecordingContents)]
    [TestCase("--attempt")]
    [TestCase("--repertoire-attempt")]
    [TestCase("--registration")]
    [TestCase("--after")]
    [TestCase("--limit")]
    public async Task MissingSelectorValueReturnsAControlledFailure(string option)
    {
        await AssertInvalid(
            option.StartsWith("--normalize-", StringComparison.Ordinal)
                ? [option]
                : [Setlists, "process", option]
        );
    }

    [Test]
    public async Task MixedOperationsAndUnrelatedSelectorsCannotAdmitWork()
    {
        var uuid = Guid.NewGuid().ToString("D");
        await AssertInvalid([Setlists, "pending", RecordingContents, "pending"]);
        await AssertInvalid([Setlists, "pending", "--normalize-recordings", uuid]);
        await AssertInvalid([RecordingContents, "pending", "--normalize-repertoire", "pending"]);
        await AssertInvalid([Setlists, "pending", "--attempt", uuid]);
        await AssertInvalid([
            RecordingContents,
            "get",
            "--attempt",
            uuid,
            "--repertoire-attempt",
            uuid,
        ]);
        await AssertInvalid([
            Setlists,
            "process",
            "--attempt",
            uuid,
            "--repertoire-attempt",
            uuid,
            "--registration",
            uuid,
        ]);
        await AssertInvalid([RecordingContents, "pending", "--request", "must-not-open-this-file"]);
        await AssertInvalid([Setlists, "pending", "--artist", uuid]);
    }

    private static async Task<SourceState> PrepareSource(
        NormalizationDomainFixture fixture,
        bool setlist
    )
    {
        var catalog = await fixture.Source();
        var privateTitle = "PrivateMaterialization" + Guid.NewGuid().ToString("N");
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var source = setlist
            ? await PrepareSetlistSource(fixture, catalog, evidence, privateTitle)
            : await PrepareRecordingSource(fixture, catalog, evidence, privateTitle);
        var repertoireUuid = await ResolveCompletedRepertoire(fixture, evidence, source);
        return new(repertoireUuid, catalog.TrackUuid, privateTitle);
    }

    private static async Task<RepertoireResolutionSource> PrepareSetlistSource(
        NormalizationDomainFixture fixture,
        CatalogSource catalog,
        NormalizationEvidenceService evidence,
        string privateTitle
    )
    {
        var artistKey = Guid.NewGuid().ToString("D");
        var accountKey = Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId",
                new { artistKey, catalog.ArtistId }
            );
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='setlist.fm' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'setlist.fm','https://provider.invalid') RETURNING id"
                );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@artistKey)",
                new
                {
                    catalog.ArtistId,
                    provider,
                    artistKey,
                }
            );
        });
        var resource = new NormalizationResourceKey(
            "setlist.fm",
            "setlist-page",
            artistKey + ":page=1"
        );
        var retained = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        page = 1,
                        itemsPerPage = 20,
                        total = 1,
                        setlist = new[]
                        {
                            new
                            {
                                id = accountKey,
                                eventDate = "17-10-2026",
                                artist = new { mbid = artistKey, name = "Fixture performer" },
                                sets = new
                                {
                                    set = new[]
                                    {
                                        new
                                        {
                                            name = "First",
                                            song = new[] { new { name = privateTitle } },
                                        },
                                    },
                                },
                            },
                        },
                    }
                ),
                "application/json",
                "setlist-fm-http-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var processor = new NormalizationSetlistProcessor(
            new(fixture.Db),
            evidence,
            new(fixture.Db),
            new(),
            new(),
            fixture.Work
        );
        var processed = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(retained.Revision.Uuid, resource),
            new("setlist.fm", "setlist", accountKey)
        );
        processed
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return new SetlistRepertoireResolutionSource(processed.AttemptUuid);
    }

    private static async Task<RepertoireResolutionSource> PrepareRecordingSource(
        NormalizationDomainFixture fixture,
        CatalogSource catalog,
        NormalizationEvidenceService evidence,
        string privateTitle
    )
    {
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET title=@privateTitle WHERE uuid=@TrackUuid",
                new { privateTitle, catalog.TrackUuid }
            )
        );
        var processor = new NormalizationRecordingProcessor(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            new(fixture.Db),
            new(fixture.Db)
        );
        var attemptUuid = Guid.NewGuid();
        (await processor.ProcessAsync(attemptUuid, catalog.SourceUuid))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return new RecordingRepertoireResolutionSource(attemptUuid);
    }

    private static async Task<Guid> ResolveCompletedRepertoire(
        NormalizationDomainFixture fixture,
        NormalizationEvidenceService evidence,
        RepertoireResolutionSource source
    )
    {
        var music = new NormalizedMusicService(fixture.Db, fixture.Work);
        var sources = new NormalizationRepertoireSourceService(
            fixture.Db,
            evidence,
            new(),
            new(fixture.Db, evidence),
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db)
        );
        var repertoire = new NormalizationRepertoireResolutionProcessor(
            fixture.Db,
            sources,
            new(fixture.Db),
            new(fixture.Db, fixture.Work),
            new(fixture.Db),
            fixture.Work,
            music
        );
        var repertoireUuid = Guid.NewGuid();
        for (var phase = 0; phase < 100; phase++)
        {
            var attempt = await repertoire.ResolveAsync(repertoireUuid, new(source));
            if (attempt.Result is { Disposition: not RepertoireResolutionDisposition.Pending })
            {
                attempt.Result.Disposition.Should().Be(RepertoireResolutionDisposition.Completed);
                return repertoireUuid;
            }
        }
        throw new AssertionException("The fixture repertoire account did not settle.");
    }

    private static async Task<bool> FindRecoveryAttempt(string command, Guid attemptUuid)
    {
        Guid? after = null;
        for (var page = 0; page < 10; page++)
        {
            var args = new List<string> { command, "recovery", "--limit", "200" };
            if (after != null)
            {
                args.AddRange(["--after", after.Value.ToString("D")]);
            }
            var result = await RunWorker(args, LocalTestDatabase.DatabaseUrl);
            result.ExitCode.Should().Be(0, result.Error);
            using var receipt = JsonDocument.Parse(result.Output);
            if (
                receipt
                    .RootElement.GetProperty("attempts")
                    .EnumerateArray()
                    .Any(value => value.GetProperty("attempt_uuid").GetGuid() == attemptUuid)
            )
            {
                return true;
            }
            var cursor = receipt.RootElement.GetProperty("next_cursor");
            if (cursor.ValueKind == JsonValueKind.Null)
            {
                return false;
            }
            after = cursor.GetGuid();
        }
        throw new AssertionException(
            "Recovery pagination did not finish within the fixture bound."
        );
    }

    private static async Task<JsonElement> RunUntilCompleted(
        string[] arguments,
        string privateTitle
    )
    {
        for (var phase = 0; phase < 30; phase++)
        {
            var result = await RunWorker(arguments, LocalTestDatabase.DatabaseUrl);
            result.ExitCode.Should().BeOneOf([0, 2], result.Error);
            result.Output.Should().NotContain(privateTitle);
            using var receipt = JsonDocument.Parse(result.Output);
            if (result.ExitCode == 0)
            {
                receipt
                    .RootElement.GetProperty("result")
                    .GetProperty("disposition")
                    .GetString()
                    .Should()
                    .Be("completed");
                return receipt.RootElement.Clone();
            }
            if (receipt.RootElement.GetProperty("result").ValueKind != JsonValueKind.Null)
            {
                receipt
                    .RootElement.GetProperty("result")
                    .GetProperty("disposition")
                    .GetString()
                    .Should()
                    .Be("pending", result.Output);
            }
        }
        throw new AssertionException(
            "The materialization command did not settle within its durable phases."
        );
    }

    private static async Task AssertInvalid(string[] arguments)
    {
        var result = await RunWorker(arguments);
        result.ExitCode.Should().Be(1);
        result.Output.Should().BeEmpty();
        result
            .Error.Should()
            .NotBeEmpty()
            .And.NotContain("DATABASE_URL")
            .And.NotContain("REDIS_URL");
    }

    private static async Task<WorkerResult> RunWorker(
        IEnumerable<string> arguments,
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
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Temporal__Address"] = "127.0.0.1:1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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

    private sealed record SourceState(
        Guid RepertoireAttemptUuid,
        Guid TrackUuid,
        string PrivateTitle
    );

    private sealed record WorkerResult(int ExitCode, string Output, string Error);
}
