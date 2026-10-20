using System.Net;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationRepertoireResolution
{
    [TestCase("retract")]
    [TestCase("reject")]
    public async Task WithdrawingPublishedExpertCorrespondenceRestoresIndependentProviderIdentity(
        string action
    )
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var alias = UniqueTitle();
        var request = await ProviderDictionary(artist, alias);
        var processor = Processor();
        var original = await ResolveNew(processor, request);
        var baseline = (await Decisions(original)).Single().RepertoireSongUuid!.Value;
        var reviewed = await AcceptedSong(artist, UniqueTitle(), ["Independent supported alias"]);
        var reviewedBefore = await music.GetRepertoireAsync(reviewed);
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "reviewed", reviewed)
        );
        var corrected = await RetryNew(processor, original.Registration.RegistrationUuid);
        (await Decisions(corrected)).Single().RepertoireSongUuid.Should().Be(reviewed);

        await knowledge.Service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 1,
                Action = action,
                Definition = action == "retract" ? null : command.Definition,
                Reason =
                    "Remove this expert target while preserving independently supported correspondences",
            },
            Operator
        );
        var restored = await RetryNew(processor, corrected.Registration.RegistrationUuid);

        (await Decisions(restored)).Single().RepertoireSongUuid.Should().Be(baseline);
        (await music.GetRepertoireAsync(reviewed)).Should().BeEquivalentTo(reviewedBefore);
        var replay = await RetryNew(processor, restored.Registration.RegistrationUuid);
        (await Decisions(replay)).Single().RepertoireSongUuid.Should().Be(baseline);
        var later = await ResolveNew(processor, request);
        (await Decisions(later)).Single().RepertoireSongUuid.Should().Be(baseline);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WithdrawnAliasRefreshesHttpSongShowsAndFileContentsWithoutChangingPlayback(
        bool reviewedMetadata
    )
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var alias = UniqueTitle();
        var canonicalTitle = UniqueTitle();
        var source = await PublishedTitle(alias, setlist: false);
        var artist = new CatalogArtist(source.Catalog.ArtistId, source.ArtistUuid);
        var processor = Processor();
        Guid target;
        if (reviewedMetadata)
        {
            target = await AcceptedSong(artist, canonicalTitle, ["Independent accepted alias"]);
        }
        else
        {
            await ImportedSong(artist, canonicalTitle);
            var dictionary = await ResolveNew(processor, await Dictionary(artist));
            target = (await Decisions(dictionary)).Single().RepertoireSongUuid!.Value;
        }
        await Execute(
            """
            INSERT INTO shows(uuid,artist_id,date,display_date,updated_at)
                VALUES(@show,@ArtistId,'2026-10-17','2026-10-17',now());
            UPDATE sources SET show_id=(SELECT id FROM shows WHERE uuid=@show) WHERE id=@SourceId;
            """,
            new
            {
                source.Catalog.ArtistId,
                source.Catalog.SourceId,
                show = Guid.NewGuid(),
            }
        );
        var request = await PublishKnowledgeRecording(source.Catalog.SourceUuid);
        var originalTarget = await music.GetRepertoireAsync(target);
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "target", target)
        );
        var first = await ResolveNew(processor, request);
        (await Decisions(first)).Single().RepertoireSongUuid.Should().Be(target);
        await MaterializeKnowledgeRecording(first.AttemptUuid);
        var before = await Playback(source.Catalog);
        await using var app = KnowledgeHttpApp();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        (await KnowledgeGet(client, $"songs/{target}/shows"))["items"]!.Should().ContainSingle();
        await AssertKnowledgeTrack(client, source.Catalog.TrackUuid, target);

        await Withdraw(knowledge, command);
        (await processor.GetPendingRegistrationsAsync())
            .Registrations.Should()
            .Contain(value => value.RegistrationUuid == first.Registration.RegistrationUuid);
        var refreshed = await RetryNew(processor, first.Registration.RegistrationUuid);
        var baseline = (await Decisions(refreshed)).Single().RepertoireSongUuid!.Value;
        baseline.Should().NotBe(target);
        await MaterializeKnowledgeRecording(refreshed.AttemptUuid);

        (await KnowledgeGet(client, $"songs/{target}/shows"))["items"]!.Should().BeEmpty();
        (await KnowledgeGet(client, $"songs/{baseline}/shows"))["items"]!.Should().ContainSingle();
        await AssertKnowledgeTrack(client, source.Catalog.TrackUuid, baseline);
        (await Playback(source.Catalog)).Should().Be(before);
        (await music.GetRepertoireAsync(target)).Should().BeEquivalentTo(originalTarget);
        var found = await KnowledgeGet(
            client,
            $"artists/{artist.Uuid}/songs?q={Uri.EscapeDataString(alias)}"
        );
        found["items"]!.Should().ContainSingle().Which["uuid"]!
            .Value<string>()
            .Should()
            .Be(baseline.ToString());

        // A separate dictionary member must follow the independent title identity. It must not
        // inherit the withdrawn expert target through stale accepted names or correspondences.
        var imported = await ImportedSong(artist, alias);
        await ActivatePolicy(titleReuse: true);
        var later = await ResolveNew(processor, await Dictionary(artist));
        (await Decisions(later))
            .Single(decision => Claim(later, decision).ImportedSongUuid == imported)
            .RepertoireSongUuid.Should()
            .Be(baseline);
        (await reads.GetSongAsync(target))!.imported_song_uuids.Should().NotContain(imported);
        (await reads.GetSongAsync(baseline))!.imported_song_uuids.Should().Contain(imported);
        await app.StopAsync();
    }

    [Test]
    public async Task WithdrawalPreservesAnIndependentlyReviewedImportedAssignment()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var target = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        var imported = await ImportedSong(artist, alias);
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "reviewed", target)
        );
        var processor = Processor();
        var first = await ResolveNew(processor, await Dictionary(artist));
        var assignment = (await music.GetImportedSongsAsync(target)).Single(value =>
            value.Value.ImportedSongUuid == imported
        );
        var owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            new()
            {
                ImportedSongs = [assignment.Value with { ExpectedRevision = assignment.Revision }],
            },
            "Independently verify this imported song assignment",
            Operator
        );
        var reviewed = (await music.GetImportedSongsAsync(target)).Single();

        await Withdraw(knowledge, command);
        await RetryNew(processor, first.Registration.RegistrationUuid);

        (await music.GetImportedSongsAsync(target))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(reviewed);
        (await reads.GetSongAsync(target))!.imported_song_uuids.Should().Contain(imported);
    }

    [Test]
    public async Task ConflictingKnowledgeRetiresOnlyItsEarlierAutomaticCorrespondences()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var firstTarget = await AcceptedSong(artist, UniqueTitle());
        var secondTarget = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        var imported = await ImportedSong(artist, alias);
        await knowledge.Save(artist.Uuid, alias, new("songs", "canonical", "first", firstTarget));
        var processor = Processor();
        var first = await ResolveNew(processor, await Dictionary(artist));
        await knowledge.Save(artist.Uuid, alias, new("songs", "canonical", "second", secondTarget));

        var conflicted = await RetryNew(processor, first.Registration.RegistrationUuid);

        (await Decisions(conflicted))
            .Should()
            .ContainSingle()
            .Which.Reason.Should()
            .Be("expert_knowledge_conflict");
        (await reads.GetSongAsync(firstTarget))!.imported_song_uuids.Should().NotContain(imported);
        (await reads.GetSongAsync(secondTarget))!.imported_song_uuids.Should().NotContain(imported);
        (await reads.ListRepertoireAsync(artist.Uuid.ToString())).items.Should().HaveCount(2);
    }

    [Test]
    public async Task ReacceptingKnowledgeInvalidatesASavedWithdrawalBeforeItCanRetireCurrentAssignments()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var target = await AcceptedSong(artist, UniqueTitle());
        var alias = UniqueTitle();
        var imported = await ImportedSong(artist, alias);
        var command = await knowledge.Save(
            artist.Uuid,
            alias,
            new("songs", "canonical", "target", target)
        );
        var processor = Processor();
        var first = await ResolveNew(processor, await Dictionary(artist));
        await Withdraw(knowledge, command);
        var retryUuid = Guid.NewGuid();
        var retry = await processor.RetryAsync(retryUuid, first.Registration.RegistrationUuid);
        await UntilStage(
            processor,
            retryUuid,
            retry.Request,
            RepertoireResolutionStageKind.AcceptedWrite,
            admitted: false
        );
        await knowledge.Service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 2,
                Reason = "Reinstate the verified correspondence before withdrawal publication",
            },
            Operator
        );

        var stale = await Drain(() => processor.ResolveAsync(retryUuid, retry.Request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        (await reads.GetSongAsync(target))!.imported_song_uuids.Should().Contain(imported);
        var current = await RetryNew(processor, first.Registration.RegistrationUuid);
        (await Decisions(current)).Single().RepertoireSongUuid.Should().Be(target);
    }

    [Test]
    public async Task ProviderAliasCannotTurnExpertCorrespondenceIntoPermanentMappingsOrAcceptedNames()
    {
        using var knowledge = new SongKnowledgeFixture(fixture);
        var artist = await Artist();
        var canonicalTitle = UniqueTitle();
        await ImportedSong(artist, canonicalTitle);
        var processor = Processor();
        var canonical = await ResolveNew(processor, await Dictionary(artist));
        var target = (await Decisions(canonical)).Single().RepertoireSongUuid!.Value;
        var original = await music.GetRepertoireAsync(target);
        var firstTitle = UniqueTitle();
        var baseRequest = await ProviderDictionary(artist, firstTitle);
        var command = await knowledge.Save(
            artist.Uuid,
            firstTitle,
            new("songs", "canonical", "expert-target", target)
        );
        var first = await ResolveNew(processor, baseRequest);
        var firstKey = int.Parse(
            first
                .Preparation!.Source.Claims.Single()
                .Identities.Single(value => value.Identity.Provider == "phish.in")
                .Identity.ProviderKey
        );
        var secondKey = Random.Shared.Next(1_000_000, int.MaxValue);
        var secondTitle = UniqueTitle();
        var aliasRequest = await CaptureProviderDictionary(
            new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = firstKey,
                        title = firstTitle,
                        alias_for = (int?)null,
                    },
                    new
                    {
                        id = secondKey,
                        title = secondTitle,
                        alias_for = (int?)firstKey,
                    },
                },
            }
        );
        await ActivatePolicy(titleReuse: true);
        var indirect = await ResolveNew(processor, aliasRequest);
        (await Decisions(indirect))
            .Should()
            .OnlyContain(value => value.RepertoireSongUuid == target);
        (await music.GetRepertoireAsync(target)).Should().BeEquivalentTo(original);

        await Withdraw(knowledge, command);
        RepertoireResolutionAttempt? refreshed = null;
        for (var step = 0; step < 3; step++)
        {
            var uuid = Guid.NewGuid();
            refreshed = await Drain(() =>
                processor.RetryAsync(uuid, indirect.Registration.RegistrationUuid)
            );
            if (refreshed.Result!.Disposition == RepertoireResolutionDisposition.Completed)
            {
                break;
            }
            refreshed
                .Result.Disposition.Should()
                .Be(RepertoireResolutionDisposition.Stale, Audit(refreshed));
        }
        refreshed!
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(refreshed));
        var decisions = await Decisions(refreshed);
        var independent = decisions
            .Single(value => Claim(refreshed, value).AliasOf == null)
            .RepertoireSongUuid!.Value;
        independent.Should().NotBe(target);
        decisions.Should().OnlyContain(value => value.RepertoireSongUuid == independent);
        (await music.GetRepertoireAsync(target)).Should().BeEquivalentTo(original);

        var imported = await ImportedSong(artist, secondTitle);
        var later = await ResolveNew(processor, await Dictionary(artist));
        (await Decisions(later))
            .Single(value => Claim(later, value).ImportedSongUuid == imported)
            .RepertoireSongUuid.Should()
            .Be(independent);
        (await reads.GetSongAsync(target))!.imported_song_uuids.Should().NotContain(imported);
    }

    private async Task<RepertoireResolutionAttempt> ResolveNew(
        NormalizationRepertoireResolutionProcessor processor,
        RepertoireResolutionRequest request
    )
    {
        var uuid = Guid.NewGuid();
        var result = await Drain(() => processor.ResolveAsync(uuid, request));
        result
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(result));
        return result;
    }

    private async Task<RepertoireResolutionAttempt> RetryNew(
        NormalizationRepertoireResolutionProcessor processor,
        Guid registration
    )
    {
        var uuid = Guid.NewGuid();
        var result = await Drain(() => processor.RetryAsync(uuid, registration));
        result
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(result));
        return result;
    }

    private static Task Withdraw(
        SongKnowledgeFixture fixture,
        ExpertKnowledgeSaveCommand command
    ) =>
        fixture.Service.SaveAsync(
            command with
            {
                OperationUuid = Guid.NewGuid(),
                ExpectedRevision = 1,
                Action = "retract",
                Definition = null,
                Reason = "Withdraw the reviewed correspondence and recompute its automatic effects",
            },
            Operator
        );

    private async Task<RepertoireResolutionRequest> PublishKnowledgeRecording(Guid source)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var recording = new NormalizationRecordingProcessor(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            new(fixture.Db),
            new(fixture.Db)
        );
        var uuid = Guid.NewGuid();
        (await recording.ProcessAsync(uuid, source))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return new(new RecordingRepertoireResolutionSource(uuid));
    }

    private async Task MaterializeKnowledgeRecording(Guid repertoireAttempt)
    {
        var materializer = new NormalizationRecordingContentMaterializer(
            fixture.Db,
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db, fixture.Work),
            fixture.Work,
            music
        );
        var uuid = Guid.NewGuid();
        for (var step = 0; step < 12; step++)
        {
            var result = await materializer.ProcessAsync(uuid, new(repertoireAttempt));
            if (result.Result != null)
            {
                result
                    .Result.Disposition.Should()
                    .Be(MusicMaterializationDisposition.Completed, result.Result.Reason);
                return;
            }
        }
        Assert.Fail("Recording contents did not complete within their bounded phase count.");
    }

    private WebApplication KnowledgeHttpApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        var app = builder.Build();
        app.MapControllers();
        return app;
    }

    private static async Task<JObject> KnowledgeGet(HttpClient client, string route)
    {
        using var response = await client.GetAsync("/api/v3/normalization/" + route);
        response.StatusCode.Should().Be(HttpStatusCode.OK, route);
        return JObject.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task AssertKnowledgeTrack(HttpClient client, Guid track, Guid song)
    {
        var contents = await KnowledgeGet(client, $"tracks/{track}/contents");
        var item = contents["items"]!.Should().ContainSingle().Subject;
        item["repertoire_song_uuid"]!.Value<string>().Should().Be(song.ToString());
        item["source_track_uuid"]!.Value<string>().Should().Be(track.ToString());
    }
}
