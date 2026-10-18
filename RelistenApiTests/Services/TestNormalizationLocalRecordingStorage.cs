using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Synthetic Local accounts exercise exact retained root membership at durable storage boundaries.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationLocalRecordingStorage
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingLookupStore lookups = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        proposals = new(fixture.Db);
        lookups = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task DirectoryCheckpointRetainsOnlyItsSelectedRootCreditsAndSurvivesPublicationRollback()
    {
        var context = await Context();
        var batch = await Batch(context);
        await proposals.SaveExtractionAsync(batch);
        var saved = (await proposals.GetAttemptBatchAsync(batch.AttemptUuid))!;
        saved.Ticket.Inputs.Should().HaveCount(2);
        saved.AdditionalExtractions.Should().BeNull();
        saved
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.Mention.Name == "Selected Root Taper"
                && credit.Mention.Evidence.InputRevisionUuid == context.Root.Uuid
                && credit.FileOrigin == null
            );
        saved
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.Mention.Name == "Directory Transfer"
                && credit.Mention.Evidence.InputRevisionUuid == context.Directory.Uuid
            );
        saved
            .Extraction.Credits.Should()
            .NotContain(credit => credit.Mention.Name == "Other Root Taper");
        saved
            .Extraction.DecodeIssues.Should()
            .NotContain(issue =>
                issue.Evidence.JsonPointer.StartsWith(
                    "/data/1970-xx-xx/1",
                    StringComparison.Ordinal
                )
            );

        var rollback = () =>
            fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                async (c, t, ct) =>
                {
                    await proposals.WritePublishedAsync(c, t, batch, ct);
                    throw new InvalidOperationException("synthetic Local publication rollback");
                }
            );
        await rollback.Should().ThrowAsync<InvalidOperationException>();
        (await proposals.GetAsync(batch.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(saved);
        (
            await fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await proposals.GetAsync(batch.SourceUuid)).Should().BeEquivalentTo(saved);
    }

    [TestCase("foreign_credit")]
    [TestCase("foreign_unresolved")]
    [TestCase("foreign_issue")]
    [TestCase("root_as_file")]
    public async Task RootEvidenceCannotBeBorrowedFromAnotherDirectoryAccount(string change)
    {
        var context = await Context();
        var batch = await Batch(context);
        var decodedRoot = NormalizationLocalRecordingSelection.DecodeRoot(context.Root, new());
        var other = decodedRoot.Recordings.Single(recording =>
            recording.Origin.Item != context.Directory.Resource
        );
        var otherMention = new RetainedRecordingCreditExtractor()
            .Extract(context.Root, other.Text)
            .Credits.Single();
        var extraction = batch.Extraction;
        var invalid = change switch
        {
            "foreign_credit" => extraction with
            {
                Credits = [.. extraction.Credits, new(Guid.NewGuid(), otherMention)],
            },
            "foreign_unresolved" => extraction with
            {
                UnresolvedCredits =
                [
                    .. extraction.UnresolvedCredits,
                    new("unknown_credit", otherMention.Evidence),
                ],
            },
            "foreign_issue" => extraction with
            {
                DecodeIssues = [.. extraction.DecodeIssues, decodedRoot.Issues.Single()],
            },
            "root_as_file" => extraction with
            {
                Credits = extraction
                    .Credits.Select(credit =>
                        credit.Mention.Evidence.InputRevisionUuid == context.Root.Uuid
                            ? credit with
                            {
                                FileOrigin = decodedRoot.Recordings[0].Origin,
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

    [TestCase("missing_root")]
    [TestCase("extra_root")]
    [TestCase("another_root")]
    [TestCase("noncanonical_directory")]
    public async Task LookupRequestRequiresOneCanonicalDirectoryAndItsExactRoot(string change)
    {
        var context = await Context();
        var request = Request(context);
        request = change switch
        {
            "missing_root" => request with { SupportingInputs = null },
            "extra_root" => request with
            {
                SupportingInputs = [request.SupportingInputs![0], request.SupportingInputs[0]],
            },
            "another_root" => request with
            {
                SupportingInputs =
                [
                    new(context.Root.Uuid, new("local", "metadata_root", "another-root")),
                ],
            },
            "noncanonical_directory" => request with
            {
                Resource = context.Directory.Resource with
                {
                    ProviderKey = "[ \"" + context.Root.Resource.ProviderKey + "\", \"disc\" ]",
                },
                Input = new(
                    context.Directory.Uuid,
                    context.Directory.Resource with
                    {
                        ProviderKey = "[ \"" + context.Root.Resource.ProviderKey + "\", \"disc\" ]",
                    }
                ),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var create = () =>
            lookups.GetOrCreateAttemptAsync(Guid.NewGuid(), request, "local-store-v1");
        await create.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task RetainedSupportMustExistAndAttemptReplayKeepsItsOriginalRootRevision()
    {
        var context = await Context();
        var request = Request(context);
        var missing = () =>
            lookups.GetOrCreateAttemptAsync(
                Guid.NewGuid(),
                request with
                {
                    SupportingInputs = [new(context.Directory.Uuid, context.Root.Resource)],
                },
                "local-store-v1"
            );
        await missing.Should().ThrowAsync<NormalizationConflictException>();
        var attempt = await lookups.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            request,
            "local-store-v1"
        );
        var changedRoot = await Capture(
            context.Root.Resource,
            new
            {
                data = new Dictionary<string, object>
                {
                    ["1970-xx-xx"] = new[]
                    {
                        new { dir = "disc", taper_notes = "Taped by Changed Root Taper" },
                    },
                },
            }
        );
        var changedRequest = request with
        {
            SupportingInputs = [new(changedRoot.Uuid, changedRoot.Resource)],
        };
        var replay = () =>
            lookups.GetOrCreateAttemptAsync(attempt.AttemptUuid, changedRequest, "local-store-v1");
        await replay.Should().ThrowAsync<NormalizationConflictException>();
        (await lookups.GetAttemptAsync(attempt.AttemptUuid))!
            .Request.Should()
            .BeEquivalentTo(request);
        var later = await lookups.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            changedRequest,
            "local-store-v1"
        );
        later.Registration.Should().Be(attempt.Registration);
    }

    [TestCase("missing_root")]
    [TestCase("different_root_revision")]
    public async Task LookupPreparationCannotReplaceItsRequestedSupportingInput(string change)
    {
        var context = await Context();
        var attempt = await lookups.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            Request(context),
            "local-store-v1"
        );
        var alternate = await Capture(
            context.Root.Resource,
            new
            {
                data = new Dictionary<string, object>
                {
                    ["1970-xx-xx"] = new[] { new { dir = "disc" } },
                },
            }
        );
        var badBinding = context.Binding with
        {
            SupportingInputs =
                change == "missing_root" ? [] : [new(alternate.Uuid, alternate.Resource)],
        };
        var preparation = new NormalizationRecordingLookupPreparation(
            context.Binding,
            null,
            null,
            ProcessingAttemptUuid: Guid.NewGuid()
        );
        var save = () =>
            lookups.SavePreparationAsync(
                attempt.AttemptUuid,
                preparation with
                {
                    Binding = badBinding,
                }
            );
        await save.Should().ThrowAsync<ArgumentException>();
        (await lookups.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        (await lookups.SavePreparationAsync(attempt.AttemptUuid, preparation))
            .Preparation.Should()
            .BeEquivalentTo(preparation);
    }

    private async Task<NormalizationRecordingProposalBatch> Batch(LocalContext context)
    {
        var preparation = new NormalizationRecordingPreparation(
            null,
            [],
            context.Binding,
            null,
            0,
            context.Directory.Resource,
            ExpectedContributions: []
        );
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            context.Source.SourceUuid,
            NormalizationRecordingProcessor.Version
        );
        await proposals.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            new("extractions", "recording", context.Source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            [
                new(
                    new(
                        context.Directory.Resource,
                        0,
                        context.Binding.Inputs.Select(input => input.Resource).ToArray(),
                        []
                    ),
                    context.Binding.Inputs,
                    context.Binding.CatalogInputs,
                    context.Binding.SourceIdentities,
                    context.Binding.MappingRegistry,
                    SourceUuids: context.Binding.SourceUuids
                ),
            ],
            expectedContributionRevisions: []
        );
        var decoder = new NormalizationProviderDecoder();
        var extraction = new NormalizationRecordingExtractor(new(), new()).ExtractLocal(
            context.Directory,
            decoder.Decode(context.Directory),
            context.Binding,
            context.Root,
            NormalizationLocalRecordingSelection.DecodeRoot(context.Root, decoder)
        );
        return new(
            attempt.AttemptUuid,
            context.Source.SourceUuid,
            context.Binding.Recording.Value!.ArtistUuid,
            NormalizationRecordingProcessor.Version,
            preparation,
            ticket,
            extraction
        );
    }

    private async Task<LocalContext> Context(bool sourceActorText = false)
    {
        var source = await fixture.Source();
        var rootName = "local-storage-" + Guid.NewGuid().ToString("N");
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
                INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@rootName);
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
                    ["1970-xx-xx"] = new[]
                    {
                        new
                        {
                            dir = "disc",
                            date = "1970-xx-xx",
                            taper_notes = sourceActorText
                                ? "Personnel:\nSelected Robin aka Finch - vocals"
                                : "Taped by Selected Root Taper",
                        },
                        new
                        {
                            dir = "other",
                            date = "1991-01-01",
                            taper_notes = sourceActorText
                                ? "Personnel:\nOther Morgan aka Jay - bass"
                                : "Taped by Other Root Taper",
                        },
                    },
                },
            }
        );
        var directory = await Capture(
            NormalizationProviderResources.LocalDirectory(rootName, "disc"),
            new
            {
                txts = new[]
                {
                    sourceActorText
                        ? "Personnel:\nDirectory Avery aka Lark - guitar"
                        : "Transferred by Directory Transfer",
                },
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
        return new(source, root, directory, binding);
    }

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        object value
    )
    {
        var generation = (await evidence.GetCurrentInputAsync(resource))?.Generation ?? 0;
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    JsonSerializer.SerializeToUtf8Bytes(value),
                    "application/json",
                    "local-http-json-v1",
                    generation,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private static NormalizationRecordingLookupRequest Request(LocalContext context) =>
        new(
            NormalizationRecordingLookupMode.RetainedProvider,
            context.Directory.Resource,
            Input: new(context.Directory.Uuid, context.Directory.Resource),
            SupportingInputs: [new(context.Root.Uuid, context.Root.Resource)]
        );

    private sealed record LocalContext(
        CatalogSource Source,
        NormalizationInputRevision Root,
        NormalizationInputRevision Directory,
        NormalizationRecordingBindingSnapshot Binding
    );
}
