using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Retained accounts and catalog snapshots contribute separate claims to one complete extraction.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingRetainedProcessing
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingLookupStore store = null!;
    private NormalizationRecordingLookupService lookups = null!;
    private NormalizationRecordingProcessor processor = null!;
    private NormalizationRecordingLookupDispatcher dispatcher = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        proposals = new(fixture.Db);
        store = new(fixture.Db);
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        lookups = new(store, legacy, evidence, bindings, new(), fixture.Work);
        processor = new(
            legacy,
            evidence,
            bindings,
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

    [TestCase("archive.org")]
    [TestCase("phish.in")]
    public async Task ProviderDeliveryUsesItsExactRetainedEvidenceWithoutLegacySubstitution(
        string provider
    )
    {
        var context = await Source(provider);
        var input = await Capture(context);
        var lookup = await lookups.LookupRetainedAsync(Guid.NewGuid(), input);
        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await proposals.GetAsync(context.Source.SourceUuid))!;
        batch.Preparation.FetchUuid.Should().BeNull();
        batch.Preparation.ProducerCatalogInputs.Should().BeEmpty();
        batch.Preparation.Binding.Should().BeEquivalentTo(lookup.Preparation!.Binding);
        batch
            .Extraction.Titles.Should()
            .ContainSingle()
            .Which.Title!.Value.Should()
            .Be("Provider Song");
        batch
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == "Provider Taper");
        if (provider == "archive.org")
        {
            batch
                .Extraction.Credits.Should()
                .Contain(credit =>
                    credit.FileOrigin != null && credit.Mention.Name == "Unplayable Recorder"
                );
            batch
                .Extraction.Credits.Where(credit => credit.FileOrigin != null)
                .Should()
                .OnlyContain(credit =>
                    credit.FileOrigin!.Evidence.InputRevisionUuid == input.InputRevisionUuid
                );
        }
        (await evidence.GetCurrentInputAsync(Legacy(context.Source))).Should().BeNull();
        (await Media(context.Source)).Should().Be(context.Media);
        (await store.GetHandoffAsync(lookup.Result.HandoffUuid.Value))!
            .CompletedResult.Should()
            .NotBeNull();
        var replay = await dispatcher.DispatchAsync(lookup.Result.HandoffUuid.Value);
        replay.ProcessingResult.Replayed.Should().BeTrue();
        (await proposals.GetAsync(context.Source.SourceUuid))!.Should().BeEquivalentTo(batch);
    }

    [TestCase("archive.org")]
    [TestCase("phish.in")]
    public async Task ProviderAndLegacyRunsPreserveAllContributorsAndConflictingTrackTitles(
        string provider
    )
    {
        var context = await Source(provider);
        (await processor.ProcessAsync(Guid.NewGuid(), context.Source.SourceUuid))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var input = await Capture(context);
        var lookup = await lookups.LookupRetainedAsync(Guid.NewGuid(), input);

        (await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value))
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);

        var providerBatch = (await proposals.GetAsync(context.Source.SourceUuid))!;
        providerBatch.Ticket.Inputs.Should().HaveCount(2);
        providerBatch.Preparation.Binding.InputRevisionUuid.Should().Be(input.InputRevisionUuid);
        providerBatch.AdditionalExtractions.Should().ContainSingle();
        providerBatch.Extraction.Titles.Single().TrackUuid.Should().Be(context.Source.TrackUuid);
        providerBatch
            .AdditionalExtractions![0]
            .Extraction.Titles.Single()
            .TrackUuid.Should()
            .Be(context.Source.TrackUuid);
        providerBatch.Extraction.Titles.Single().Title!.Value.Should().Be("Provider Song");
        providerBatch
            .AdditionalExtractions[0]
            .Extraction.Titles.Single()
            .Title!.Value.Should()
            .Be("Song");
        providerBatch
            .AdditionalExtractions[0]
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == "Legacy Taper");
        var legacyHead = (await evidence.GetCurrentInputAsync(Legacy(context.Source)))!;

        var later = await processor.ProcessAsync(Guid.NewGuid(), context.Source.SourceUuid);

        later.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var legacyBatch = (await proposals.GetAsync(context.Source.SourceUuid))!;
        legacyBatch.Ticket.Inputs.Should().HaveCount(2);
        legacyBatch.Extraction.Titles.Single().Title!.Value.Should().Be("Song");
        legacyBatch
            .AdditionalExtractions.Should()
            .ContainSingle()
            .Which.InputRevisionUuid.Should()
            .Be(input.InputRevisionUuid);
        legacyBatch
            .AdditionalExtractions![0]
            .Extraction.Titles.Single()
            .Title!.Value.Should()
            .Be("Provider Song");
        (await evidence.GetCurrentInputAsync(Legacy(context.Source)))!
            .Generation.Should()
            .Be(legacyHead.Generation);
        (
            await fixture.Work.GetInputContributionsAsync(
                NormalizationRecordingProcessor.Owner(context.Source.SourceUuid)
            )
        )
            .Contributions.Should()
            .HaveCount(2);
    }

    [TestCase("archive.org")]
    [TestCase("phish.in")]
    public async Task StaleIncomingAccountCannotPublishAlongsideNewerCatalogPeerFacts(
        string provider
    )
    {
        var context = await Source(provider);
        await processor.ProcessAsync(Guid.NewGuid(), context.Source.SourceUuid);
        var previous = (await proposals.GetAsync(context.Source.SourceUuid))!;
        var lookup = await lookups.LookupRetainedAsync(Guid.NewGuid(), await Capture(context));
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description='Changed after the retained lookup' WHERE id=@SourceId",
                context.Source
            )
        );

        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
        (await proposals.GetAsync(context.Source.SourceUuid))!
            .AttemptUuid.Should()
            .Be(previous.AttemptUuid);
        var attempt = (await proposals.GetAttemptAsync(delivery.ProcessingResult.AttemptUuid))!;
        attempt.Preparation!.Binding.Should().BeEquivalentTo(lookup.Preparation!.Binding);
        attempt.Preparation.AdditionalInputs.Should().ContainSingle();
        attempt
            .Preparation.AdditionalInputs![0]
            .Binding.CatalogInputs.Should()
            .NotBeEquivalentTo(attempt.Preparation.Binding.CatalogInputs);
        (await store.GetHandoffAsync(lookup.Result.HandoffUuid.Value))!
            .CompletedResult!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
    }

    [Test]
    public async Task UnsupportedPeerProducesAnAcknowledgedAttentionReceiptWithoutConsumingItsInputs()
    {
        var context = await Source("archive.org");
        var contributor = new NormalizationResourceKey(
            "fixture",
            "unread",
            Guid.NewGuid().ToString("D")
        );
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            NormalizationRecordingProcessor.Owner(context.Source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            new(contributor, 0, [], []),
            sourceUuids: [new(Guid.NewGuid(), 0)]
        );
        var lookup = await lookups.LookupRetainedAsync(Guid.NewGuid(), await Capture(context));

        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Unresolved);
        delivery.ProcessingResult.Reason.Should().Be("unsupported_owner_inputs");
        var attempt = (await proposals.GetAttemptAsync(delivery.ProcessingResult.AttemptUuid))!;
        attempt.Preparation.Should().BeNull();
        attempt.InitiatingHandoffUuid.Should().Be(lookup.Result.HandoffUuid);
        (await proposals.GetAsync(context.Source.SourceUuid)).Should().BeNull();
        (
            await fixture.Work.GetInputContributionAsync(
                NormalizationRecordingProcessor.Owner(context.Source.SourceUuid),
                contributor
            )
        )
            .Should()
            .NotBeNull();
        (await store.GetHandoffAsync(lookup.Result.HandoffUuid.Value))!
            .CompletedResult.Should()
            .NotBeNull();
    }

    [Test]
    public async Task ChangedRetainedHeadDoesNotReplaceTheHandoffsOriginalRevision()
    {
        var context = await Source("archive.org");
        var first = await Capture(context);
        var lookup = await lookups.LookupRetainedAsync(Guid.NewGuid(), first);
        var latest = await Capture(context, "Corrected Provider Song");

        var delivery = await dispatcher.DispatchAsync(lookup.Result!.HandoffUuid!.Value);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
        (await proposals.GetAttemptAsync(delivery.ProcessingResult.AttemptUuid))!
            .Preparation!.Binding.InputRevisionUuid.Should()
            .Be(first.InputRevisionUuid);
        (await proposals.GetAsync(context.Source.SourceUuid)).Should().BeNull();
        var next = await lookups.RetryAsync(Guid.NewGuid(), lookup.Registration.LookupUuid);
        (await dispatcher.DispatchAsync(next.Result!.HandoffUuid!.Value))
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        (await proposals.GetAsync(context.Source.SourceUuid))!
            .Preparation.Binding.InputRevisionUuid.Should()
            .Be(latest.InputRevisionUuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingLegacyHandoffFromAnOlderVersionGetsAnExplicitTerminalReceipt(
        bool oldHeaderExists
    )
    {
        var source = await fixture.Source();
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        var snapshot = (await legacy.RefreshRecordingAsync(source.SourceUuid))!;
        var decoded = new Relisten.Normalization.NormalizationProviderDecoder().Decode(
            snapshot.Capture.Revision
        );
        var bound = await new NormalizationCatalogBindingService(
            fixture.Db
        ).BindRefreshedRecordingAsync(snapshot, decoded.Recordings.Single());
        var lookup = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(NormalizationRecordingLookupMode.LegacyCatalog, Legacy(source)),
            "historical-lookup-version"
        );
        lookup = await store.SavePreparationAsync(
            lookup.AttemptUuid,
            new(
                bound,
                null,
                null,
                snapshot.Capture.Fetch.FetchUuid,
                snapshot.CatalogInputs,
                new(snapshot.Capture.Fetch.FetchUuid, snapshot.CatalogInputs, bound, null, 0),
                Guid.NewGuid(),
                "historical-processing-version"
            )
        );
        var ticket = await fixture.Work.BeginWorkAsync(
            lookup.AttemptUuid,
            new("lookups", "recording", lookup.Registration.LookupUuid),
            lookup.ProcessingVersion,
            bound.Inputs.Select(input => input.Resource).ToArray(),
            [],
            catalogInputs: bound.CatalogInputs,
            sourceIdentities: bound.SourceIdentities,
            expectedInputRevisions: bound.Inputs,
            sourceUuids: bound.SourceUuids
        );
        await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            async (c, t, ct) =>
            {
                await store.WritePublishedAsync(c, t, lookup, ticket, ct);
            }
        );

        if (oldHeaderExists)
        {
            var handoff = (await store.GetHandoffAsync(lookup.AttemptUuid))!;
            // The previous dispatcher could stop after registering the target and before
            // installing its preparation. That old request had no initiating-handoff field.
            await proposals.GetOrCreateAttemptAsync(
                handoff.ProcessingAttemptUuid,
                source.SourceUuid,
                handoff.ProcessingVersion!
            );
        }

        var delivery = await dispatcher.DispatchAsync(lookup.AttemptUuid);

        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Unresolved);
        delivery.ProcessingResult.Reason.Should().Be("processing_version_changed");
        (await store.GetHandoffAsync(lookup.AttemptUuid))!.CompletedResult.Should().NotBeNull();
        (await proposals.GetAttemptAsync(delivery.ProcessingResult.AttemptUuid))!
            .Preparation.Should()
            .BeNull();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await dispatcher.DispatchAsync(lookup.AttemptUuid))
            .ProcessingResult.Replayed.Should()
            .BeTrue();
    }

    private async Task<Context> Source(string provider)
    {
        var source = await fixture.Source();
        var key =
            provider == "phish.in"
                ? Random.Shared.Next(10000000, int.MaxValue).ToString()
                : "retained-" + Guid.NewGuid().ToString("N");
        var media =
            provider == "archive.org"
                ? $"https://archive.org/download/{key}/file.mp3"
                : "https://example.test/file.mp3";
        await fixture.Db.WithWriteConnection(async c =>
        {
            var upstream = await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name=@provider ORDER BY id LIMIT 1",
                new { provider }
            );
            upstream ??= await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,@provider,@url FROM upstream_sources RETURNING id",
                new { provider, url = "https://" + provider }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET upstream_identifier=@key,taper='Legacy Taper' WHERE id=@SourceId; UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid; INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label) VALUES(@SourceId,@upstream,true,false,false,@url,'Provider')",
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    key,
                    media,
                    upstream,
                    url = "https://" + provider + "/" + key,
                }
            );
        });
        return new(
            source,
            new(provider, provider == "archive.org" ? "item_metadata" : "show", key),
            media
        );
    }

    private async Task<NormalizationBindingInputReference> Capture(
        Context context,
        string title = "Provider Song"
    )
    {
        var payload =
            context.Resource.Provider == "archive.org"
                ? JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new
                        {
                            identifier = context.Resource.ProviderKey,
                            taper = "Provider Taper",
                            transferer = "Provider Transfer",
                        },
                        files = new object[]
                        {
                            new
                            {
                                name = "file.mp3",
                                title,
                                format = "VBR MP3",
                                taper = "File Recorder",
                            },
                            new
                            {
                                name = "unplayable.flac",
                                title = "Unplayable",
                                format = "Flac",
                                taper = "Unplayable Recorder",
                            },
                        },
                    }
                )
                : JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        id = int.Parse(context.Resource.ProviderKey),
                        date = "1990-01-01",
                        taper_notes = "Recorded by Provider Taper",
                        tracks = new[]
                        {
                            new
                            {
                                id = 1,
                                show_id = int.Parse(context.Resource.ProviderKey),
                                title,
                                mp3 = context.Media,
                            },
                        },
                    }
                );
        var current = await evidence.GetCurrentInputAsync(context.Resource);
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                context.Resource,
                payload,
                "application/json",
                context.Resource.Provider == "archive.org"
                    ? "archive-item-response-v1"
                    : "phishin-show-object-v1",
                current?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        return new(capture.Revision.Uuid, context.Resource);
    }

    private Task<string> Media(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT mp3_url FROM source_tracks WHERE uuid=@TrackUuid",
                source
            )
        );

    private static NormalizationResourceKey Legacy(CatalogSource source) =>
        new("legacy_catalog", "recording", source.SourceUuid.ToString("D"));

    private sealed record Context(
        CatalogSource Source,
        NormalizationResourceKey Resource,
        string Media
    );
}
