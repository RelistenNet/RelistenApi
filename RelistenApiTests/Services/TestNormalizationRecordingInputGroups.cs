using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Checkpoint storage preserves each retained account's facts and exact credit scope.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingInputGroups
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private int archiveUpstreamId;

    [OneTimeSetUp]
    public async Task Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        proposals = new(fixture.Db);
        archiveUpstreamId = await fixture.Db.WithWriteConnection(async c =>
        {
            var existing = await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
            );
            return existing
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'archive.org','https://archive.org' FROM upstream_sources RETURNING id"
                );
        });
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task DistinctAccountsPreserveConflictingTrackTitlesAndScopedUnplayableCredits()
    {
        var batch = await Batch(includeLegacy: true);
        // This Archive account binds by provider-qualified identifier, without a retained
        // catalog UUID assertion. Its existing identifier and artist fences remain sufficient.
        (batch.Preparation.Binding.SourceUuids ?? [])
            .Should()
            .BeEmpty();
        batch.Preparation.Binding.SourceIdentities.Should().NotBeEmpty();
        batch.Preparation.Binding.CatalogInputs.Should().NotBeEmpty();
        await proposals.SaveExtractionAsync(batch);
        var saved = (await proposals.GetAttemptBatchAsync(batch.AttemptUuid))!;

        saved.Preparation.FetchUuid.Should().BeNull();
        saved.Preparation.AdditionalInputs!.Single().FetchUuid.Should().NotBeNull();
        saved.Extraction.Titles.Single().Title!.Value.Should().Be("Provider Song");
        saved
            .AdditionalExtractions!.Single()
            .Extraction.Titles.Single()
            .Title!.Value.Should()
            .Be("Song");
        saved
            .Extraction.Titles.Single()
            .TrackUuid.Should()
            .Be(saved.AdditionalExtractions!.Single().Extraction.Titles.Single().TrackUuid);
        saved
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.FileOrigin != null && credit.Mention.Name == "Unplayable Recorder"
            );
        saved.Extraction.UnresolvedCredits.Should().Contain(issue => issue.FileOrigin != null);

        var failed = () =>
            fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                async (c, t, ct) =>
                {
                    await proposals.WritePublishedAsync(c, t, batch, ct);
                    throw new InvalidOperationException("synthetic publication rollback");
                }
            );
        await failed.Should().ThrowAsync<InvalidOperationException>();
        (await proposals.GetAsync(batch.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptAsync(batch.AttemptUuid))!.Result.Should().BeNull();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(saved);

        var published = await fixture.Work.TryPublishAsync(
            batch.AttemptUuid,
            (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
        );
        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await proposals.GetAsync(batch.SourceUuid)).Should().BeEquivalentTo(saved);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task RetainedArchiveUuidLookupMustRemainInTheCheckpointReadSet(
        bool consumeUuidFence
    )
    {
        var batch = await Batch(retainedAssignment: true, consumeUuidFence: consumeUuidFence);
        batch
            .Preparation.Binding.SourceUuids.Should()
            .ContainSingle()
            .Which.SourceUuid.Should()
            .Be(batch.SourceUuid);
        if (consumeUuidFence)
        {
            await proposals.SaveExtractionAsync(batch);
            var published = await fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
            );
            published.Status.Should().Be(NormalizationPublicationStatus.Published);
        }
        else
        {
            var save = () => proposals.SaveExtractionAsync(batch);
            await save.Should().ThrowAsync<ArgumentException>();
            (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
            (await proposals.GetAsync(batch.SourceUuid)).Should().BeNull();
        }
    }

    [TestCase("missing_group")]
    [TestCase("duplicate_group")]
    [TestCase("foreign_group_evidence")]
    public async Task InputGroupsMustBeCompleteDistinctAndCiteTheirOwnRetainedBytes(string change)
    {
        var batch = await Batch(includeLegacy: true);
        var peer = batch.AdditionalExtractions!.Single();
        var invalid = change switch
        {
            "missing_group" => batch with { AdditionalExtractions = null },
            "duplicate_group" => batch with { AdditionalExtractions = [peer, peer] },
            "foreign_group_evidence" => batch with
            {
                Extraction = batch.Extraction with
                {
                    Credits =
                    [
                        .. batch.Extraction.Credits,
                        peer.Extraction.Credits.First() with
                        {
                            ProposalUuid = Guid.NewGuid(),
                        },
                    ],
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var save = () => proposals.SaveExtractionAsync(invalid);
        await save.Should().ThrowAsync<ArgumentException>();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        await proposals.SaveExtractionAsync(batch);
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
    }

    [TestCase("mention_scope_removed")]
    [TestCase("unresolved_scope_removed")]
    [TestCase("invented_file")]
    [TestCase("other_file")]
    [TestCase("outside_credit_field")]
    public async Task CreditScopeCannotBeDroppedInventedOrMovedToAnotherOriginalFile(string change)
    {
        var batch = await Batch();
        var extraction = batch.Extraction;
        var mention = extraction.Credits.First(credit => credit.FileOrigin != null);
        var unresolved = extraction.UnresolvedCredits.First(issue => issue.FileOrigin != null);
        var invalid = change switch
        {
            "mention_scope_removed" => extraction with
            {
                Credits = extraction
                    .Credits.Select(credit =>
                        credit == mention ? credit with { FileOrigin = null } : credit
                    )
                    .ToArray(),
            },
            "unresolved_scope_removed" => extraction with
            {
                UnresolvedCredits = extraction
                    .UnresolvedCredits.Select(issue =>
                        issue == unresolved ? issue with { FileOrigin = null } : issue
                    )
                    .ToArray(),
            },
            "invented_file" => extraction with
            {
                Credits = extraction
                    .Credits.Select(credit =>
                        credit == mention
                            ? credit with
                            {
                                FileOrigin = mention.FileOrigin! with
                                {
                                    Item = new("archive.org", "file", "invented"),
                                },
                            }
                            : credit
                    )
                    .ToArray(),
            },
            "other_file" => extraction with
            {
                Credits = extraction
                    .Credits.Select(credit =>
                        credit == mention
                            ? credit with
                            {
                                FileOrigin = extraction
                                    .Credits.First(other =>
                                        other.FileOrigin != null
                                        && other.FileOrigin != mention.FileOrigin
                                    )
                                    .FileOrigin,
                            }
                            : credit
                    )
                    .ToArray(),
            },
            "outside_credit_field" => extraction with
            {
                Credits = extraction
                    .Credits.Select(credit =>
                        credit == mention
                            ? credit with
                            {
                                Mention = mention.Mention with
                                {
                                    Evidence = extraction.Titles.Single().Title!.Evidence,
                                },
                            }
                            : credit
                    )
                    .ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var save = () => proposals.SaveExtractionAsync(batch with { Extraction = invalid });
        await save.Should().ThrowAsync<ArgumentException>();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        await proposals.SaveExtractionAsync(batch);
    }

    [Test]
    public async Task CurrentProcessingCannotSaveAnIncompleteMembershipReceiptOrInventLegacyFetch()
    {
        var context = await CaptureSource();
        var preparation = new NormalizationRecordingPreparation(
            null,
            [],
            context.Binding,
            null,
            0,
            context.Binding.Resource
        );
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            context.Source.SourceUuid,
            NormalizationRecordingProcessor.Version
        );
        var missingMembership = () =>
            proposals.SavePreparationAsync(attempt.AttemptUuid, preparation);
        await missingMembership.Should().ThrowAsync<ArgumentException>();
        var fakeFetch = () =>
            proposals.SavePreparationAsync(
                attempt.AttemptUuid,
                preparation with
                {
                    FetchUuid = Guid.NewGuid(),
                    ExpectedContributions = [],
                }
            );
        await fakeFetch.Should().ThrowAsync<ArgumentException>();
        var wrongContributor = () =>
            proposals.SavePreparationAsync(
                attempt.AttemptUuid,
                preparation with
                {
                    Contributor = new("archive.org", "item_metadata", "another-account"),
                    ExpectedContributions = [],
                }
            );
        await wrongContributor.Should().ThrowAsync<ArgumentException>();
        (await proposals.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        await proposals.SavePreparationAsync(
            attempt.AttemptUuid,
            preparation with
            {
                ExpectedContributions = [],
            }
        );
    }

    private async Task<NormalizationRecordingProposalBatch> Batch(
        bool includeLegacy = false,
        bool retainedAssignment = false,
        bool consumeUuidFence = true
    )
    {
        var context = await CaptureSource(retainedAssignment);
        var bindings = new NormalizationCatalogBindingService(fixture.Db);
        var peerInputs = new List<NormalizationRecordingPreparedInput>();
        var peerExtractions = new List<NormalizationRecordingInputExtraction>();
        if (includeLegacy)
        {
            var snapshot = (
                await new NormalizationLegacyInputService(
                    fixture.Db,
                    evidence
                ).RefreshRecordingAsync(context.Source.SourceUuid)
            )!;
            var decoded = new NormalizationProviderDecoder().Decode(snapshot.Capture.Revision);
            var binding = await bindings.BindRefreshedRecordingAsync(
                snapshot,
                decoded.Recordings.Single()
            );
            peerInputs.Add(
                new(
                    new("relisten", "recording", context.Source.SourceUuid.ToString("D")),
                    0,
                    binding,
                    snapshot.Capture.Fetch.FetchUuid,
                    snapshot.CatalogInputs
                )
            );
            peerExtractions.Add(
                new(
                    binding.InputRevisionUuid,
                    new NormalizationRecordingExtractor(new(), new()).Extract(
                        snapshot.Capture.Revision,
                        decoded,
                        binding
                    )
                )
            );
        }
        var preparation = new NormalizationRecordingPreparation(
            null,
            [],
            context.Binding,
            null,
            0,
            context.Binding.Resource,
            peerInputs.Count == 0 ? null : peerInputs,
            []
        );
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            context.Source.SourceUuid,
            NormalizationRecordingProcessor.Version
        );
        await proposals.SavePreparationAsync(attempt.AttemptUuid, preparation);
        NormalizationRecordingPreparedInput[] inputs =
        [
            new(context.Binding.Resource, 0, context.Binding),
            .. peerInputs,
        ];
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            new("extractions", "recording", context.Source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            inputs
                .Select(input => new NormalizationPreparedInputContribution(
                    new(
                        input.Contributor,
                        0,
                        input.Binding.Inputs.Select(reference => reference.Resource).ToArray(),
                        []
                    ),
                    input.Binding.Inputs,
                    input.Binding.CatalogInputs,
                    input.Binding.SourceIdentities,
                    input.Binding.MappingRegistry,
                    SourceUuids: consumeUuidFence ? input.Binding.SourceUuids : []
                ))
                .ToArray(),
            expectedContributionRevisions: []
        );
        var extraction = new NormalizationRecordingExtractor(new(), new()).Extract(
            context.Input,
            new NormalizationProviderDecoder().Decode(context.Input),
            context.Binding
        );
        return new(
            attempt.AttemptUuid,
            context.Source.SourceUuid,
            context.Binding.Recording.Value!.ArtistUuid,
            NormalizationRecordingProcessor.Version,
            preparation,
            ticket,
            extraction,
            peerExtractions.Count == 0 ? null : peerExtractions
        );
    }

    private async Task<CapturedSource> CaptureSource(bool retainedAssignment = false)
    {
        var source = await fixture.Source();
        var key = "input-groups-" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE sources SET upstream_identifier=@key,taper='Legacy Taper' WHERE id=@SourceId;
                UPDATE source_tracks SET mp3_url=@media WHERE uuid=@TrackUuid;
                INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
                    VALUES(@SourceId,@archiveUpstreamId,true,false,false,@url,'Archive');
                """,
                new
                {
                    source.SourceId,
                    source.TrackUuid,
                    archiveUpstreamId,
                    key,
                    media = $"https://archive.org/download/{key}/file.mp3",
                    url = $"https://archive.org/details/{key}",
                }
            )
        );
        if (retainedAssignment)
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    """
                    INSERT INTO normalization.archive_item_resolutions(item_identifier,selected_artist_uuid,canonical_source_uuid,status)
                        SELECT @key,a.uuid,@SourceUuid,'resolved' FROM artists a WHERE a.id=@ArtistId
                    """,
                    new
                    {
                        key,
                        source.SourceUuid,
                        source.ArtistId,
                    }
                )
            );
        }
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                new("archive.org", "item_metadata", key),
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new { identifier = key, taper = "Provider Taper" },
                        files = new object[]
                        {
                            new
                            {
                                name = "file.mp3",
                                title = "Provider Song",
                                format = "VBR MP3",
                                taper = "File Recorder",
                                transferer = "unknown",
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
                ),
                "application/json",
                "archive-item-response-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var decoded = new NormalizationProviderDecoder().Decode(captured.Revision);
        var binding = await new NormalizationCatalogBindingService(fixture.Db).BindRecordingAsync(
            decoded.Recordings.Single()
        );
        binding.Recording.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        binding
            .Files.Count(file => file.Binding.Status == NormalizationCatalogBindingStatus.Bound)
            .Should()
            .Be(1);
        return new(source, captured.Revision, binding);
    }

    private sealed record CapturedSource(
        CatalogSource Source,
        NormalizationInputRevision Input,
        NormalizationRecordingBindingSnapshot Binding
    );
}
