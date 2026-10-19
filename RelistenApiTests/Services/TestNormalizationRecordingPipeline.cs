using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises retained evidence, delivery acknowledgement, guarded domain writers and recovery
/// together. An acknowledged extraction must not hide unfinished normalization from operators.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationRecordingPipeline
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingLookupStore lookups = null!;
    private NormalizationRecordingLookupService lookupService = null!;
    private NormalizationRecordingPipeline pipeline = null!;
    private NormalizedPeopleService people = null!;
    private NormalizedVenueService venueService = null!;
    private PeopleResolutionPolicyService peoplePolicies = null!;
    private VenueResolutionPolicyService venuePolicies = null!;
    private NormalizationReadService reads = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        proposals = new(fixture.Db);
        lookups = new(fixture.Db);
        var decoder = new NormalizationProviderDecoder();
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        var legacy = new NormalizationLegacyInputService(fixture.Db, evidence);
        lookupService = new(lookups, legacy, evidence, bindings, decoder, fixture.Work);
        var extractor = new NormalizationRecordingProcessor(
            legacy,
            evidence,
            bindings,
            decoder,
            new(new(), new()),
            fixture.Work,
            proposals,
            lookups
        );
        var dispatcher = new NormalizationRecordingLookupDispatcher(lookups, extractor);
        people = new(fixture.Db, fixture.Work);
        venueService = new(fixture.Db, fixture.Work);
        reads = new(fixture.Db, Options.Create(new NormalizationReadOptions { Enabled = true }));
        var candidates = new NormalizationCandidateService(fixture.Db);
        peoplePolicies = new PeopleResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        venuePolicies = new VenueResolutionPolicyService(
            fixture.Db,
            fixture.Work,
            allowFixturePolicies: true
        );
        var decisions = new NormalizationPeopleResolutionDecisionService(new());
        var resolutions = new NormalizationPeopleResolutionStore(fixture.Db);
        var peopleResolver = new NormalizationPeopleResolutionProcessor(
            people,
            proposals,
            candidates,
            decisions,
            resolutions,
            fixture.Work
        );
        var peopleApplier = new NormalizationPeopleResolutionApplier(
            fixture.Db,
            resolutions,
            proposals,
            candidates,
            bindings,
            people,
            decisions,
            new(),
            peoplePolicies,
            new(fixture.Db),
            fixture.Work
        );
        var aliasStore = new SourceActorResolutionStore(fixture.Db);
        var aliasDecisions = new SourceActorResolutionDecisionService(new(), new());
        var aliasResolver = new SourceActorResolutionService(
            candidates,
            peoplePolicies,
            aliasDecisions,
            aliasStore,
            fixture.Work
        );
        var aliasApplier = new SourceActorAliasApplicationService(
            fixture.Db,
            aliasStore,
            candidates,
            peoplePolicies,
            aliasDecisions,
            new(fixture.Db),
            people,
            fixture.Work
        );
        var venueResolver = new NormalizationVenueResolutionProcessor(
            fixture.Db,
            candidates,
            new(fixture.Db, evidence),
            bindings,
            evidence,
            decoder,
            venuePolicies,
            new(fixture.Db),
            fixture.Work,
            venueService,
            new(fixture.Db, evidence, decoder)
        );
        pipeline = new(
            lookups,
            dispatcher,
            proposals,
            peopleResolver,
            peopleApplier,
            aliasResolver,
            aliasApplier,
            venueResolver,
            evidence,
            decoder,
            fixture.Work,
            peoplePolicies,
            venuePolicies
        );
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task InactivePolicyLeavesAcknowledgedHandoffRecoverableAndReplaysOriginalChildren()
    {
        var context = await Recording();
        var input = await Capture(context);
        var request = await Request(input);

        var result = await pipeline.ProcessAsync(request);

        result.Request.Should().Be(request);
        result.SourceUuid.Should().Be(context.Source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingPipelineDisposition.AwaitingPolicy);
        result.Completion.Should().BeNull();
        result
            .Extraction.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        result.PeopleResolution.Should().NotBeNull();
        result.People.Should().NotBeNull();
        result.AliasResolution.Should().NotBeNull();
        result.Aliases.Should().NotBeNull();
        result.Venues.Should().ContainSingle();
        (await lookups.GetHandoffAsync(request.HandoffUuid))!.CompletedResult.Should().NotBeNull();
        (await lookups.GetPendingHandoffsAsync(1000))
            .Should()
            .NotContain(value => value.HandoffUuid == request.HandoffUuid);

        var batch = (await proposals.GetAsync(context.Source.SourceUuid))!;
        batch.Preparation.Binding.InputRevisionUuid.Should().Be(input.InputRevisionUuid);
        batch
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == "Pipeline Taper");
        batch
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Should()
            .ContainSingle(assertion => assertion.Name == "Pipeline Robin");
        batch
            .Extraction.Titles.Should()
            .ContainSingle()
            .Which.TrackUuid.Should()
            .Be(context.Source.TrackUuid);
        (await Credits(context.Source.SourceUuid)).items.Should().BeEmpty();

        var pending = await Pending(request.HandoffUuid);
        pending.State.Should().Be(NormalizationWorkRunState.Running);
        var recovered = new NormalizationRecordingPipelineRequest(
            pending.Ticket.WorkUuid,
            pending.Subject.Subject.Uuid
        );
        recovered.Should().Be(request);
        var replay = await pipeline.ProcessAsync(recovered);
        replay.Disposition.Should().Be(NormalizationRecordingPipelineDisposition.AwaitingPolicy);
        replay.Extraction.AttemptUuid.Should().Be(result.Extraction.AttemptUuid);
        replay.Extraction.Replayed.Should().BeTrue();
        replay.PeopleResolution!.AttemptUuid.Should().Be(result.PeopleResolution!.AttemptUuid);
        replay.PeopleResolution.Replayed.Should().BeTrue();
        replay.People!.AttemptUuid.Should().Be(result.People!.AttemptUuid);
        replay.People.Replayed.Should().BeTrue();
        replay.AliasResolution!.AttemptUuid.Should().Be(result.AliasResolution!.AttemptUuid);
        replay.AliasResolution.Replayed.Should().BeTrue();
        replay.Aliases!.AttemptUuid.Should().Be(result.Aliases!.AttemptUuid);
        replay.Aliases.Replayed.Should().BeTrue();
        replay.Venues.Single().AttemptUuid.Should().Be(result.Venues.Single().AttemptUuid);
        replay.Venues.Single().Replayed.Should().BeTrue();
        (await proposals.GetAsync(context.Source.SourceUuid)).Should().BeEquivalentTo(batch);
        (await Credits(context.Source.SourceUuid)).items.Should().BeEmpty();
        (await Pending(request.HandoffUuid)).Ticket.WorkUuid.Should().Be(request.AttemptUuid);
    }

    [Test]
    public async Task ChangedEvidenceCannotReplaceAcceptedCreditsOrOriginalPlayback()
    {
        var context = await Recording();
        var creditUuid = await AcceptedCredit(context.Source.SourceUuid);
        var accepted = (await Credits(context.Source.SourceUuid)).items.Single();
        accepted.uuid.Should().Be(creditUuid);
        var original = await Capture(context);
        var request = await Request(original);
        await Capture(context, "Changed provider title");

        var result = await pipeline.ProcessAsync(request);

        result.Disposition.Should().Be(NormalizationRecordingPipelineDisposition.Stale);
        result.Completion.Should().BeNull();
        result
            .Extraction.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
        (await Credits(context.Source.SourceUuid))
            .items.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(accepted);
        (await proposals.GetAsync(context.Source.SourceUuid)).Should().BeNull();
        (await Pending(request.HandoffUuid)).Ticket.WorkUuid.Should().Be(request.AttemptUuid);
        var playback = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<PlaybackRow>(
                "SELECT uuid AS Uuid,mp3_url AS Url FROM source_tracks WHERE uuid=@TrackUuid",
                context.Source
            )
        );
        playback.Uuid.Should().Be(context.Source.TrackUuid);
        playback.Url.Should().Be(context.Media);
    }

    private async Task<NormalizationPendingWorkRun> Pending(Guid handoffUuid)
    {
        Guid? after = null;
        do
        {
            var page = await fixture.Work.GetPendingRunsAsync(
                "extractions",
                "recording_pipeline",
                after
            );
            var found = page.Items.SingleOrDefault(value =>
                value.Subject.Subject == NormalizationRecordingPipeline.Owner(handoffUuid)
            );
            if (found != null)
            {
                return found;
            }
            after = page.Next;
        } while (after != null);
        throw new AssertionException(
            "Unfinished recording pipeline disappeared from pending work."
        );
    }

    private async Task<(
        RecordingContext Context,
        NormalizationRecordingPipelineRequest Request
    )> RetainedArchive()
    {
        var context = await Recording();
        return (context, await Request(await Capture(context)));
    }

    private async Task<NormalizationRecordingPipelineRequest> Request(
        NormalizationBindingInputReference input
    )
    {
        var lookup = await lookupService.LookupRetainedAsync(Guid.NewGuid(), input);
        lookup.Result!.HandoffUuid.Should().NotBeNull();
        return new(Guid.NewGuid(), lookup.Result.HandoffUuid!.Value);
    }

    private async Task<NormalizationPage<NormalizedRecordingCredit>> Credits(Guid sourceUuid)
    {
        var controller = new NormalizationPeopleVenuesController(reads)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };
        var result = (await controller.Credits(sourceUuid.ToString("D")))
            .Should()
            .BeOfType<JsonResult>()
            .Subject;
        return result
            .Value.Should()
            .BeOfType<NormalizationPage<NormalizedRecordingCredit>>()
            .Subject;
    }

    private async Task<Guid> AcceptedCredit(Guid sourceUuid)
    {
        var retained = await fixture.Capture("Reviewed Taper", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Reviewed Taper");
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            sourceUuid,
            "taper",
            person.DisplayName,
            person.Uuid,
            null,
            [retained.Reference]
        );
        (await fixture.PublishPeople(people, retained, new([person], [], [], [credit])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return credit.Uuid;
    }

    private async Task<RecordingContext> Recording()
    {
        var source = await fixture.Source();
        var key = "pipeline-" + Guid.NewGuid().ToString("N");
        var media = $"https://archive.org/download/{key}/file.mp3";
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'archive.org','https://archive.org' FROM upstream_sources RETURNING id"
                );
            await c.ExecuteAsync(
                """
                UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId;
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
                VALUES(@SourceId,@provider,true,false,false,@url,'Provider');
                """,
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    key,
                    media,
                    provider,
                    url = "https://archive.org/details/" + key,
                }
            );
        });
        return new(source, new("archive.org", "item_metadata", key), media);
    }

    private async Task<NormalizationBindingInputReference> Capture(
        RecordingContext context,
        string title = "Original provider title",
        string taper = "Pipeline Taper",
        string description = "Personnel:\nPipeline Robin aka Finch - vocals",
        string venue = "Pipeline Hall"
    )
    {
        var current = await evidence.GetCurrentInputAsync(context.Resource);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                context.Resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new
                        {
                            identifier = context.Resource.ProviderKey,
                            date = "2026-10-17",
                            venue,
                            coverage = "San Francisco, CA",
                            taper,
                            description,
                        },
                        files = new[]
                        {
                            new
                            {
                                name = "file.mp3",
                                title,
                                format = "VBR MP3",
                            },
                        },
                    }
                ),
                "application/json",
                "archive-item-response-v1",
                current?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        return new(captured.Revision.Uuid, context.Resource);
    }

    private sealed record RecordingContext(
        CatalogSource Source,
        NormalizationResourceKey Resource,
        string Media
    );

    private sealed record PlaybackRow(Guid Uuid, string Url);
}
