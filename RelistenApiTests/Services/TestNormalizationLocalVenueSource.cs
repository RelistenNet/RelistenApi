using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>
/// Local venue claims retain their original root account while the directory's media binding
/// supplies catalog ownership. These disposable-database examples activate no matching policy.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationLocalVenueSource
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationProviderDecoder decoder = null!;
    private NormalizationVenueSourceService sources = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        decoder = new();
        sources = new(fixture.Db, evidence, decoder);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SelectedRootAccountReachesVenueProcessorWithoutChangingRecordingIdentity()
    {
        var context = await PublishDirectory();
        var source = (await sources.PrepareShowClaimAsync(context.Claim))!;
        source.RecordingBinding!.InputRevisionUuid.Should().Be(context.Directory.Uuid);
        source.RecordingBinding.Recording.Value!.SourceUuid.Should().Be(context.Source.SourceUuid);
        source.Claim!.Observation.Names.Single().Value.Should().Be("Selected Hall");
        source.Claim.Evidence.InputRevisionUuid.Should().Be(context.Root.Uuid);
        source.Claim.Evidence.JsonPointer.Should().Be("/data/2026-10-17/0");
        source.Claim.ObservedDate!.Value.Should().Be(new DateOnly(2026, 10, 17));
        source.Claim.ObservedDate.Evidence.InputRevisionUuid.Should().Be(context.Root.Uuid);
        source
            .OriginalTicket!.Inputs.Select(input => input.RevisionUuid)
            .Should()
            .BeEquivalentTo([context.Root.Uuid, context.Directory.Uuid]);

        var processor = new NormalizationVenueResolutionProcessor(
            fixture.Db,
            new(fixture.Db),
            new(fixture.Db, evidence),
            new(fixture.Db),
            evidence,
            decoder,
            new(fixture.Db, fixture.Work, allowFixturePolicies: true),
            new(fixture.Db),
            fixture.Work,
            new(fixture.Db, fixture.Work),
            sources
        );
        var attemptUuid = Guid.NewGuid();
        var attempt = await processor.ResolveShowAsync(attemptUuid, context.Claim);
        attempt.Preparation!.Source.Claim.Should().BeEquivalentTo(source.Claim);
        for (var step = 0; attempt.Result == null && step < 10; step++)
        {
            attempt = await processor.ResolveShowAsync(attemptUuid, context.Claim);
        }
        attempt.Result.Should().NotBeNull();
        attempt.Result!.Disposition.Should().Be(VenueResolutionDisposition.Unresolved);
        (await processor.ResolveShowAsync(attemptUuid, context.Claim))
            .Result!.Replayed.Should()
            .BeTrue();
        var original = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM sources WHERE id=@SourceId", context.Source)
        );
        original.Should().Be(context.Source.SourceUuid);
    }

    [TestCase("adjacent_account")]
    [TestCase("wrong_root")]
    [TestCase("missing_root")]
    [TestCase("directory_revision")]
    public async Task UnsupportedCoordinatesCannotBorrowRootVenueOrDate(string change)
    {
        var context = await PublishDirectory();
        var claim = context.Claim;
        if (change == "wrong_root")
        {
            var foreign = await Capture(
                new("local", "metadata_root", "foreign-" + Guid.NewGuid()),
                new { data = new Dictionary<string, object>() }
            );
            claim = claim with { InputRevisionUuid = foreign.Uuid };
        }
        else
        {
            claim = change switch
            {
                "adjacent_account" => claim with { VenueJsonPointer = "/data/2026-10-17/1" },
                "missing_root" => claim with { InputRevisionUuid = Guid.NewGuid() },
                "directory_revision" => claim with { InputRevisionUuid = context.Directory.Uuid },
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
        }
        var prepare = () => sources.PrepareShowClaimAsync(claim);
        await prepare.Should().ThrowAsync<ArgumentException>();
        var original = (await sources.PrepareShowClaimAsync(context.Claim))!;
        original.Claim!.Observation.Names.Single().Value.Should().Be("Selected Hall");
        original
            .RecordingBinding!.Recording.Value!.SourceUuid.Should()
            .Be(context.Source.SourceUuid);
    }

    private async Task<LocalContext> PublishDirectory()
    {
        var source = await fixture.Source();
        var rootName = "local-venue-" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='local' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'local','https://audio.relisten.net' FROM upstream_sources RETURNING id"
                );
            await c.ExecuteAsync(
                """
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier)
                VALUES(@ArtistId,@provider,@rootName);
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                """,
                new
                {
                    source.ArtistId,
                    source.TrackUuid,
                    provider,
                    rootName,
                    media = $"https://audio.relisten.net/{rootName}/disc/one.mp3",
                }
            );
        });
        var root = await Capture(
            new("local", "metadata_root", rootName),
            new
            {
                data = new Dictionary<string, object>
                {
                    ["2026-10-17"] = new[]
                    {
                        new
                        {
                            dir = "disc",
                            date = "2026-10-17",
                            venue = "Selected Hall",
                            city = "San Francisco",
                            state = "CA",
                        },
                        new
                        {
                            dir = "adjacent",
                            date = "2026-10-17",
                            venue = "Other Hall",
                            city = "Oakland",
                            state = "CA",
                        },
                    },
                },
            }
        );
        var directory = await Capture(
            NormalizationProviderResources.LocalDirectory(rootName, "disc"),
            new
            {
                txts = Array.Empty<string>(),
                tracks = new[]
                {
                    new
                    {
                        mp3 = "one.mp3",
                        tags = new { title = "Local Song", track = new { no = 1 } },
                    },
                },
            }
        );
        var binding = await new NormalizationCatalogBindingService(
            fixture.Db
        ).BindLocalDirectoryAsync(root, directory);
        binding.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        var proposals = new NormalizationRecordingProposalService(fixture.Db);
        var preparation = new NormalizationRecordingPreparation(
            null,
            [],
            binding,
            null,
            0,
            directory.Resource,
            ExpectedContributions: []
        );
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            NormalizationRecordingProcessor.Version
        );
        await proposals.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            new("extractions", "recording", source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            [
                new(
                    new(
                        directory.Resource,
                        0,
                        binding.Inputs.Select(input => input.Resource).ToArray(),
                        []
                    ),
                    binding.Inputs,
                    binding.CatalogInputs,
                    binding.SourceIdentities,
                    binding.MappingRegistry,
                    SourceUuids: binding.SourceUuids
                ),
            ],
            expectedContributionRevisions: []
        );
        var extraction = new NormalizationRecordingExtractor(new(), new()).ExtractLocal(
            directory,
            decoder.Decode(directory),
            binding,
            root,
            NormalizationLocalRecordingSelection.DecodeRoot(root, decoder)
        );
        var batch = new NormalizationRecordingProposalBatch(
            attempt.AttemptUuid,
            source.SourceUuid,
            binding.Recording.Value!.ArtistUuid,
            NormalizationRecordingProcessor.Version,
            preparation,
            ticket,
            extraction
        );
        await proposals.SaveExtractionAsync(batch);
        (
            await fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return new(
            source,
            root,
            directory,
            new(
                VenueShowClaimSourceKind.Recording,
                attempt.AttemptUuid,
                root.Uuid,
                "/data/2026-10-17/0"
            )
        );
    }

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        object value
    ) =>
        (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    JsonSerializer.SerializeToUtf8Bytes(value),
                    "application/json",
                    "local-http-json-v1",
                    0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;

    private sealed record LocalContext(
        CatalogSource Source,
        NormalizationInputRevision Root,
        NormalizationInputRevision Directory,
        VenueShowClaimReference Claim
    );
}
