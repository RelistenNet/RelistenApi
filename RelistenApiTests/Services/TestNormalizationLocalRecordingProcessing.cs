using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Local directory extraction retains its joint root evidence and complete contributor ownership.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationLocalRecordingProcessing
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRecordingLookupStore store = null!;
    private NormalizationRecordingLookupService lookups = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingProcessor processor = null!;
    private NormalizationRecordingLookupDispatcher dispatcher = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        store = new(fixture.Db);
        proposals = new(fixture.Db);
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        var binder = new NormalizationCatalogBindingService(fixture.Db);
        lookups = new(store, legacy, evidence, binder, new(), fixture.Work);
        processor = new(
            legacy,
            evidence,
            binder,
            new(),
            new(new(), new()),
            fixture.Work,
            proposals,
            store
        );
        dispatcher = new(store, processor);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task UnknownDatesAndRootOnlyNotesRetainTheirActualEvidenceAndDirectoryScope()
    {
        var context = await Source();
        var root = await Root(
            context,
            new Account(context.Directory, "Root Recorder"),
            new Account("other", "Other Recorder")
        );
        var directory = await Directory(context, "Tagged 🎤 Song");

        var lookup = await lookups.LookupLocalAsync(Guid.NewGuid(), root, directory);
        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await proposals.GetAsync(context.Source.SourceUuid))!;
        batch.Preparation.Binding.Inputs.Should().BeEquivalentTo(new[] { root, directory });
        batch.Preparation.Binding.MappingRegistry.Should().NotBeNull();
        batch.Preparation.Contributor.Should().Be(directory.Resource);
        batch.Ticket.Inputs.Should().HaveCount(2);
        batch
            .AdditionalExtractions.Should()
            .BeNullOrEmpty("the root is supporting context, not another recording");
        batch
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.Mention.Name == "Root Recorder"
                && credit.Mention.Evidence.InputRevisionUuid == root.InputRevisionUuid
                && credit.Mention.Evidence.JsonPointer == "/data/1970-xx-xx/0/taper_notes"
            );
        batch
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.Mention.Name == "Directory Transfer"
                && credit.Mention.Evidence.InputRevisionUuid == directory.InputRevisionUuid
            );
        batch
            .Extraction.Credits.Should()
            .NotContain(credit => credit.Mention.Name == "Other Recorder");
        batch
            .Extraction.Titles.Should()
            .ContainSingle()
            .Which.Title!.Value.Should()
            .Be("Tagged 🎤 Song");
        batch.Extraction.Titles[0].Title!.Evidence.JsonPointer.Should().Be("/tracks/0/tags/title");
        batch.Preparation.Binding.Files.Should().HaveCount(2);
        batch.Preparation.Binding.Files[0].Observation.Origin.Item.Should().Be(directory.Resource);
        batch
            .Preparation.Binding.Files[0]
            .Observation.Origin.Evidence.JsonPointer.Should()
            .Be("/tracks/0");
        batch
            .Preparation.Binding.Files[1]
            .Binding.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Missing);
        (await Media(context.Source.TrackUuid)).Should().Be(Url(context.Root, context.Directory));
        (
            await evidence.GetCurrentInputAsync(
                new("legacy_catalog", "recording", context.Source.SourceUuid.ToString("D"))
            )
        )
            .Should()
            .BeNull();
        (await dispatcher.DispatchAsync(lookup.Result.HandoffUuid.Value))
            .ProcessingResult.Replayed.Should()
            .BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ChangedRootOrDirectoryCannotReplaceTheOriginalDeliveryPair(bool changeRoot)
    {
        var context = await Source();
        var root = await Root(context, new Account(context.Directory, "Root Recorder"));
        var directory = await Directory(context, "Original Song");
        var lookup = await lookups.LookupLocalAsync(Guid.NewGuid(), root, directory);
        var latestRoot = changeRoot
            ? await Root(context, new Account(context.Directory, "Corrected Recorder"))
            : root;
        var latestDirectory = changeRoot ? directory : await Directory(context, "Corrected Song");

        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
        var stale = (await proposals.GetAttemptAsync(delivery.ProcessingResult.AttemptUuid))!;
        stale.Preparation!.Binding.Inputs.Should().BeEquivalentTo(new[] { root, directory });
        (await proposals.GetAsync(context.Source.SourceUuid)).Should().BeNull();
        var next = await lookups.RetryAsync(Guid.NewGuid(), lookup.Registration.LookupUuid);
        next.Request.Input.Should().Be(latestDirectory);
        next.Request.SupportingInputs.Should().Equal(latestRoot);
        (await dispatcher.DispatchAsync(next.Result!.HandoffUuid!.Value))
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var replay = await lookups.LookupLocalAsync(lookup.AttemptUuid, root, directory);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Request.Input.Should().Be(directory);
        replay.Request.SupportingInputs.Should().Equal(root);
    }

    [Test]
    public async Task MappingRegistryChangeMakesAnOriginalJointLookupStale()
    {
        var context = await Source();
        var root = await Root(context, new Account(context.Directory, "Root Recorder"));
        var directory = await Directory(context, "Local Song");
        var lookup = await lookups.LookupLocalAsync(Guid.NewGuid(), root, directory);
        var rival = await fixture.Source();
        await Map(rival.ArtistId, context.Root);

        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
        (await proposals.GetAsync(context.Source.SourceUuid)).Should().BeNull();
        var next = await lookups.RetryAsync(Guid.NewGuid(), lookup.Registration.LookupUuid);
        next.Result!.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        next.Result.Reason.Should().Be("multiple_root_artists");
        next.Preparation!.Binding!.MappingRegistry.Should()
            .NotBe(lookup.Preparation!.Binding!.MappingRegistry);
        next.Result.HandoffUuid.Should().BeNull();
    }

    [Test]
    public async Task LegacyAndTwoLocalDirectoriesConsumeOneSharedRootWithoutCreatingARootRecording()
    {
        var context = await Source();
        const string secondDirectory = "second set";
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at) SELECT source_id,source_set_id,artist_id,2,'Second Song',@slug,@url,@uuid,now() FROM source_tracks WHERE uuid=@TrackUuid",
                new
                {
                    context.Source.TrackUuid,
                    slug = Guid.NewGuid().ToString("D"),
                    uuid = Guid.NewGuid(),
                    url = Url(context.Root, secondDirectory),
                }
            )
        );
        await processor.ProcessAsync(Guid.NewGuid(), context.Source.SourceUuid);
        var root = await Root(
            context,
            new Account(context.Directory, "First Recorder"),
            new Account(secondDirectory, "Second Recorder")
        );
        var first = await Directory(context, "First Local Song");
        var second = await Directory(
            context with
            {
                Directory = secondDirectory,
            },
            "Second Local Song"
        );
        var firstLookup = await lookups.LookupLocalAsync(Guid.NewGuid(), root, first);
        (await dispatcher.DispatchAsync(firstLookup.Result!.HandoffUuid!.Value))
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var secondLookup = await lookups.LookupLocalAsync(Guid.NewGuid(), root, second);

        (await dispatcher.DispatchAsync(secondLookup.Result!.HandoffUuid!.Value))
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);

        var batch = (await proposals.GetAsync(context.Source.SourceUuid))!;
        batch.Ticket.Inputs.Should().HaveCount(4);
        batch.AdditionalExtractions.Should().HaveCount(2);
        batch
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == "Second Recorder");
        batch
            .Extraction.Credits.Should()
            .NotContain(credit => credit.Mention.Name == "First Recorder");
        var firstGroup = batch.AdditionalExtractions!.Single(group =>
            group.InputRevisionUuid == first.InputRevisionUuid
        );
        firstGroup
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == "First Recorder");
        firstGroup
            .Extraction.Credits.Should()
            .NotContain(credit => credit.Mention.Name == "Second Recorder");
        batch
            .AdditionalExtractions.Should()
            .NotContain(group => group.InputRevisionUuid == root.InputRevisionUuid);
        var rootHead = (await evidence.GetCurrentInputAsync(root.Resource))!;

        (await processor.ProcessAsync(Guid.NewGuid(), context.Source.SourceUuid))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var current = (await proposals.GetAsync(context.Source.SourceUuid))!;
        current.Ticket.Inputs.Should().HaveCount(4);
        current.AdditionalExtractions.Should().HaveCount(2);
        (
            await fixture.Work.GetInputContributionsAsync(
                NormalizationRecordingProcessor.Owner(context.Source.SourceUuid)
            )
        )
            .Contributions.Should()
            .HaveCount(3);
        (await evidence.GetCurrentInputAsync(root.Resource))!
            .Generation.Should()
            .Be(rootHead.Generation);
    }

    [Test]
    public async Task ADeclaredRootFromAnotherDirectoryIdentityCannotRegisterALookup()
    {
        var context = await Source();
        var root = await Root(context, new Account(context.Directory, "Root Recorder"));
        var directory = await Directory(context, "Local Song");
        var wrong = root with { Resource = new("local", "metadata_root", "other-root") };
        var attemptUuid = Guid.NewGuid();

        var lookup = () => lookups.LookupLocalAsync(attemptUuid, wrong, directory);

        await lookup.Should().ThrowAsync<ArgumentException>();
        (await store.GetAttemptAsync(attemptUuid)).Should().BeNull();
    }

    private async Task<Context> Source()
    {
        var source = await fixture.Source();
        var root = "café & \"root\"/" + Guid.NewGuid().ToString("N");
        const string directory = "disc/Zoë & \"Pat\"";
        await Map(source.ArtistId, root);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET taper='Legacy Recorder' WHERE id=@SourceId; UPDATE source_tracks SET mp3_url=@url WHERE uuid=@TrackUuid",
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    url = Url(root, directory),
                }
            )
        );
        return new(source, root, directory);
    }

    private async Task Map(int artist, string root)
    {
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='local' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) SELECT GREATEST(1000,COALESCE(max(id),0)+1),'local','https://audio.relisten.net' FROM upstream_sources RETURNING id"
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

    private Task<NormalizationBindingInputReference> Root(
        Context context,
        params Account[] accounts
    ) =>
        Capture(
            new("local", "metadata_root", context.Root),
            new
            {
                data = new Dictionary<string, object>
                {
                    ["1970-xx-xx"] = accounts
                        .Select(account => new
                        {
                            dir = account.Directory,
                            date = "1970-xx-xx",
                            taper_notes = "Recorded by " + account.Recorder,
                        })
                        .ToArray(),
                },
            }
        );

    private Task<NormalizationBindingInputReference> Directory(Context context, string title) =>
        Capture(
            NormalizationProviderResources.LocalDirectory(context.Root, context.Directory),
            new
            {
                txts = new[] { "Transferred by Directory Transfer" },
                tracks = new[]
                {
                    new
                    {
                        mp3 = "one file.mp3",
                        title = "Outer title",
                        tags = new { title, track = new { no = 1 } },
                    },
                    new
                    {
                        mp3 = "unavailable.mp3",
                        title = "Unplayable",
                        tags = new { title = "Unplayable", track = new { no = 2 } },
                    },
                },
            }
        );

    private async Task<NormalizationBindingInputReference> Capture(
        NormalizationResourceKey resource,
        object payload
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(payload),
                "application/json",
                "local-http-json-v1",
                current?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        return new(capture.Revision.Uuid, resource);
    }

    private Task<string> Media(Guid trackUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT mp3_url FROM source_tracks WHERE uuid=@trackUuid",
                new { trackUuid }
            )
        );

    private static string Url(string root, string directory) =>
        $"https://audio.relisten.net/{root}/{directory}/one file.mp3";

    private sealed record Context(CatalogSource Source, string Root, string Directory);

    private sealed record Account(string Directory, string Recorder);
}
