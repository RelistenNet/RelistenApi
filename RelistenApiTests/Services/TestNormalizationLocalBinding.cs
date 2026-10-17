using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Joint Local provenance selects current media owners and fences missing or changing root mappings.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationLocalBinding
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationCatalogBindingService binder = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        binder = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task UnknownDateUsesExactUnicodeDirectoryMediaAndRetainsBothRequiredInputs()
    {
        var source = await fixture.Source();
        var root = "café & \"root\"/" + Guid.NewGuid();
        const string directory = "disc/Zoë & \"Pat\"";
        var inputs = Inputs(root, directory, "one file.mp3");
        await Configure(source, root, directory, "one file.mp3");

        var result = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        result.Recording.Value.ContextIssues.Should().Contain("missing_show");
        result.Files.Single().Binding.Value!.TrackUuid.Should().Be(source.TrackUuid);
        result
            .Files.Single()
            .Binding.Value!.MediaUrl.Should()
            .Be(Url(root, directory, "one file.mp3"));
        result
            .Inputs.Should()
            .BeEquivalentTo(
                new[]
                {
                    new NormalizationBindingInputReference(inputs.Root.Uuid, inputs.Root.Resource),
                    new NormalizationBindingInputReference(
                        inputs.Directory.Uuid,
                        inputs.Directory.Resource
                    ),
                }
            );
        result
            .SupportingInputs.Should()
            .ContainSingle(input => input.InputRevisionUuid == inputs.Root.Uuid);
        result.MappingRegistry.Should().NotBeNull();
        result
            .CatalogInputs.Should()
            .ContainSingle(clock => clock.ArtistUuid == result.Recording.Value.ArtistUuid);
    }

    [Test]
    public async Task AnotherRootsAccountCannotEstablishDirectoryOwnership()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "one.mp3");
        await Configure(source, root, "disc", "one.mp3");

        var result = await binder.BindLocalDirectoryAsync(
            RootInput(RootName(), "disc"),
            inputs.Directory
        );

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Recording.Reason.Should().Be("directory_root_identity_mismatch");
        result.Files.Single().Binding.Value.Should().BeNull();
        result.MappingRegistry.Should().NotBeNull();
    }

    [Test]
    public async Task ExpectedArtistCannotOverrideTheConfiguredRootOwner()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "one.mp3");
        await Configure(source, root, "disc", "one.mp3");

        var result = await binder.BindLocalDirectoryAsync(
            inputs.Root,
            inputs.Directory,
            Guid.NewGuid()
        );

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Recording.Reason.Should().Be("artist_mismatch");
        result.CatalogInputs.Should().HaveCount(1);
    }

    [Test]
    public async Task DuplicateFileAccountsRemainUnresolvedWithoutWithholdingRecordingCredits()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "one.mp3", "one.mp3");
        await Configure(source, root, "disc", "one.mp3");

        var result = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result
            .Files.Should()
            .HaveCount(2)
            .And.OnlyContain(file =>
                file.Binding.Status == NormalizationCatalogBindingStatus.Ambiguous
                && file.Binding.Value == null
            );
    }

    [Test]
    public async Task SeparateDirectoriesCanBindTheSameCatalogRecording()
    {
        var source = await fixture.Source();
        var root = RootName();
        var first = Inputs(root, "first", "one.mp3");
        var second = Inputs(root, "second", "two.mp3");
        var rootInput = RootInput(root, "first", "second");
        await Configure(source, root, "first", "one.mp3");
        var secondTrack = await AddTrack(source, Url(root, "second", "two.mp3"));

        var a = await binder.BindLocalDirectoryAsync(rootInput, first.Directory);
        var b = await binder.BindLocalDirectoryAsync(rootInput, second.Directory);

        a.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        b.Recording.Value!.SourceUuid.Should().Be(source.SourceUuid);
        a.Files.Single().Binding.Value!.TrackUuid.Should().Be(source.TrackUuid);
        b.Files.Single().Binding.Value!.TrackUuid.Should().Be(secondTrack);
    }

    [TestCase(false, NormalizationCatalogBindingStatus.Missing, "directory_not_in_root")]
    [TestCase(true, NormalizationCatalogBindingStatus.Ambiguous, "multiple_directory_accounts")]
    public async Task RootDirectoryMembershipMustBeUnambiguous(
        bool duplicate,
        NormalizationCatalogBindingStatus status,
        string reason
    )
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "one.mp3");
        var rootInput = duplicate ? RootInput(root, "disc", "disc") : RootInput(root, "different");
        await Configure(source, root, "disc", "one.mp3");

        var result = await binder.BindLocalDirectoryAsync(rootInput, inputs.Directory);

        result.Recording.Status.Should().Be(status);
        result.Recording.Reason.Should().Be(reason);
        result
            .Files.Should()
            .ContainSingle(file =>
                file.Observation.PlaybackLocator!.Value == "one.mp3" && file.Binding.Value == null
            );
        result.CatalogInputs.Should().HaveCount(1);
        result.MappingRegistry.Should().NotBeNull();
        result.Inputs.Should().HaveCount(2);
    }

    [Test]
    public async Task ADateAndMatchingTitleCannotReplaceMissingDirectoryMedia()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "unavailable.mp3");
        await Map(source.ArtistId, root);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier='1970-xx-xx',display_date='1970-xx-xx' WHERE id=@id",
                new { id = source.SourceId }
            )
        );

        var result = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Recording.Reason.Should().Be("missing_directory_media");
        result.CatalogInputs.Should().HaveCount(1);
        result.MappingRegistry.Should().NotBeNull();
    }

    [Test]
    public async Task FilesSplitAcrossRecordingsCannotChooseAnOwner()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "one.mp3", "two.mp3");
        await Configure(source, root, "disc", "one.mp3");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET artist_id=@artist WHERE id=@id; UPDATE source_tracks SET artist_id=@artist,mp3_url=@url WHERE uuid=@track",
                new
                {
                    artist = source.ArtistId,
                    id = other.SourceId,
                    track = other.TrackUuid,
                    url = Url(root, "disc", "two.mp3"),
                }
            )
        );

        var result = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        result.Recording.Reason.Should().Be("multiple_directory_recordings");
        result.Files.Should().HaveCount(2).And.OnlyContain(file => file.Binding.Value == null);
    }

    [TestCase("orphaned", NormalizationCatalogBindingStatus.Missing)]
    [TestCase("artist", NormalizationCatalogBindingStatus.ProviderConflict)]
    [TestCase("set", NormalizationCatalogBindingStatus.ProviderConflict)]
    [TestCase("source", NormalizationCatalogBindingStatus.ProviderConflict)]
    public async Task BadFileOwnershipDoesNotWithholdCreditsForTheProvenRecording(
        string change,
        NormalizationCatalogBindingStatus badStatus
    )
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var root = RootName();
        var inputs = Inputs(root, "disc", "good.mp3", "bad.mp3");
        await Configure(source, root, "disc", "good.mp3");
        var bad = await AddTrack(source, Url(root, "disc", "bad.mp3"));
        var sql = change switch
        {
            "orphaned" => "UPDATE source_tracks SET is_orphaned=true WHERE uuid=@bad",
            "artist" => "UPDATE source_tracks SET artist_id=@artist WHERE uuid=@bad",
            "set" =>
                "UPDATE source_tracks SET source_set_id=(SELECT source_set_id FROM source_tracks WHERE uuid=@other) WHERE uuid=@bad",
            _ => "UPDATE source_tracks SET source_id=NULL WHERE uuid=@bad",
        };
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                sql,
                new
                {
                    bad,
                    artist = other.ArtistId,
                    other = other.TrackUuid,
                }
            )
        );

        var result = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);

        result.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.Files[0].Binding.Value!.TrackUuid.Should().Be(source.TrackUuid);
        result.Files[1].Binding.Status.Should().Be(badStatus);
        result.Files[1].Observation.PlaybackLocator!.Value.Should().Be("bad.mp3");
    }

    [Test]
    public async Task ANewRootOwnerWithoutSourcesInvalidatesTheOldTicketAndAmbiguityCanRecover()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = await Retain(Inputs(root, "disc", "one.mp3"));
        await Configure(source, root, "disc", "one.mp3");
        var snapshot = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);
        var ticket = await Begin(snapshot, new("people", "recording", source.SourceUuid));
        var newcomer = await NewArtist();
        await Map(newcomer.Id, root);
        var original = snapshot.CatalogInputs.Single();
        (await ArtistGeneration(original.ArtistUuid)).Should().Be(original.Generation);
        var called = false;

        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
        var ambiguous = await binder.BindLocalDirectoryAsync(
            inputs.Root,
            inputs.Directory,
            snapshot.Recording.Value!.ArtistUuid
        );
        ambiguous.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        ambiguous.CatalogInputs.Should().HaveCount(2);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM artists_upstream_sources WHERE artist_id=@Id", newcomer)
        );
        var recovered = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);
        var retry = await Begin(recovered, new("people", "recording", source.SourceUuid));
        (await fixture.Work.TryPublishAsync(retry.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task MissingMappingIsFencedBeforeAnyArtistCanBeKnown()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = await Retain(Inputs(root, "disc", "one.mp3"));
        await SetMedia(source, Url(root, "disc", "one.mp3"));
        var missing = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);
        missing.Recording.Reason.Should().Be("missing_root_artist");
        missing.CatalogInputs.Should().BeEmpty();
        missing.MappingRegistry.Should().NotBeNull();
        // This known recording supplies a legitimate work owner even though the missing
        // root mapping leaves the binder unable to associate the directory with any artist.
        var ticket = await Begin(missing, new("people", "recording", source.SourceUuid));
        await Map(source.ArtistId, root);
        var called = false;

        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );

        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
        (await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory))
            .Recording.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Bound);
    }

    [Test]
    public async Task ChangingOnlyTheRootMembershipInvalidatesDirectoryCreditWork()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = await Retain(Inputs(root, "disc", "one.mp3"));
        await Configure(source, root, "disc", "one.mp3");
        var snapshot = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);
        var ticket = await Begin(snapshot, new("people", "recording", source.SourceUuid));
        var refreshed = await RetainOne(RootInput(root, "another-directory"));
        var called = false;

        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }
        );

        publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
        called.Should().BeFalse();
        (await binder.BindLocalDirectoryAsync(refreshed, inputs.Directory))
            .Recording.Reason.Should()
            .Be("directory_not_in_root");
    }

    [Test]
    public async Task RootChangesBetweenBindingAndAdmissionRejectTheOldSnapshotAndCanRecover()
    {
        var source = await fixture.Source();
        var root = RootName();
        var inputs = await Retain(Inputs(root, "disc", "one.mp3"));
        await Configure(source, root, "disc", "one.mp3");
        var snapshot = await binder.BindLocalDirectoryAsync(inputs.Root, inputs.Directory);
        await RetainOne(RootInput(root, "another-directory"));

        var admitOldSnapshot = () => Begin(snapshot, new("people", "recording", source.SourceUuid));

        await admitOldSnapshot.Should().ThrowAsync<NormalizationConflictException>();

        var refreshed = await RetainOne(RootInput(root, "disc", "another-directory"));
        var recovered = await binder.BindLocalDirectoryAsync(refreshed, inputs.Directory);
        var ticket = await Begin(recovered, new("people", "recording", source.SourceUuid));
        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) => Task.CompletedTask
        );

        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    private Task<NormalizationWorkTicket> Begin(
        NormalizationRecordingBindingSnapshot snapshot,
        NormalizationSubjectKey subject
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "local-binding-test",
            snapshot.Inputs.Select(input => input.Resource).ToArray(),
            [],
            catalogInputs: snapshot.CatalogInputs,
            mappingRegistry: snapshot.MappingRegistry,
            expectedInputRevisions: snapshot.Inputs
        );

    private async Task Configure(
        NormalizationDomainFixture.CatalogSource source,
        string root,
        string directory,
        string filename
    )
    {
        await Map(source.ArtistId, root);
        await SetMedia(source, Url(root, directory, filename));
    }

    private Task SetMedia(NormalizationDomainFixture.CatalogSource source, string url) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET mp3_url=@url WHERE uuid=@TrackUuid",
                new { url, source.TrackUuid }
            )
        );

    private async Task Map(int artist, string root)
    {
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='local' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'local','https://audio.relisten.net') RETURNING id"
                );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@artist,@provider,@root)",
                new
                {
                    artist,
                    provider,
                    root,
                }
            );
        });
    }

    private Task<long> ArtistGeneration(Guid artistUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT generation FROM normalization.catalog_artist_revisions WHERE artist_uuid=@artistUuid",
                new { artistUuid }
            )
        );

    private Task<ArtistReference> NewArtist() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ArtistReference>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Local newcomer',@slug,@uuid,now()) RETURNING id AS Id,uuid AS Uuid",
                new { slug = Guid.NewGuid().ToString(), uuid = Guid.NewGuid() }
            )
        );

    private Task<Guid> AddTrack(NormalizationDomainFixture.CatalogSource source, string url)
    {
        var uuid = Guid.NewGuid();
        return fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at) SELECT @SourceId,source_set_id,@ArtistId,2,'Another song',@slug,@url,@uuid,now() FROM source_tracks WHERE uuid=@TrackUuid RETURNING uuid",
                new
                {
                    source.SourceId,
                    source.ArtistId,
                    source.TrackUuid,
                    slug = uuid.ToString(),
                    url,
                    uuid,
                }
            )
        );
    }

    private async Task<LocalInputs> Retain(LocalInputs inputs) =>
        new(await RetainOne(inputs.Root), await RetainOne(inputs.Directory));

    private async Task<NormalizationInputRevision> RetainOne(NormalizationInputRevision input)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var generation = (await evidence.GetCurrentInputAsync(input.Resource))?.Generation ?? 0;
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    input.Resource,
                    input.Payload,
                    input.MediaType,
                    input.CaptureVersion,
                    generation,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private static string RootName() => "local-root-" + Guid.NewGuid();

    private static string Url(string root, string directory, string filename) =>
        $"https://audio.relisten.net/{root}/{directory}/{filename}";

    private static LocalInputs Inputs(string root, string directory, params string[] filenames) =>
        new(
            RootInput(root, directory),
            Input(
                NormalizationProviderResources.LocalDirectory(root, directory),
                new
                {
                    txts = new[] { "Taped by Zoë" },
                    tracks = filenames.Select(
                        (filename, index) =>
                            new
                            {
                                mp3 = filename,
                                tags = new { title = "A song", track = new { no = index + 1 } },
                            }
                    ),
                }
            )
        );

    private static NormalizationInputRevision RootInput(string root, params string[] directories) =>
        Input(
            new("local", "metadata_root", root),
            new
            {
                data = new Dictionary<string, object>
                {
                    ["1970-xx-xx"] = directories
                        .Select(directory => new
                        {
                            dir = directory,
                            date = "1970-xx-xx",
                            venue = "A hall",
                        })
                        .ToArray(),
                },
            }
        );

    private static NormalizationInputRevision Input(
        NormalizationResourceKey resource,
        object payload
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            Resource = resource,
            Payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
            CaptureVersion = "local-http-json-v1",
            MediaType = "application/json",
        };

    private sealed record LocalInputs(
        NormalizationInputRevision Root,
        NormalizationInputRevision Directory
    );

    private sealed record ArtistReference(int Id, Guid Uuid);
}
