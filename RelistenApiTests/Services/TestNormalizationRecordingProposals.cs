using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Durable extraction attempts keep original facts and publish no accepted domain identities.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingProposals
{
    private const string ProcessingVersion = "recording-proposal-fixture-v1";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationRecordingProposalService proposals = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        proposals = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task AttemptHeaderReplaysExactlyAndCannotChangeSourceOrVersion()
    {
        var uuid = Guid.NewGuid();
        var source = Guid.NewGuid();
        var first = await proposals.GetOrCreateAttemptAsync(uuid, source, ProcessingVersion);
        var replay = await proposals.GetOrCreateAttemptAsync(uuid, source, ProcessingVersion);
        replay.Should().BeEquivalentTo(first);
        var changedSource = () =>
            proposals.GetOrCreateAttemptAsync(uuid, Guid.NewGuid(), ProcessingVersion);
        var changedVersion = () =>
            proposals.GetOrCreateAttemptAsync(uuid, source, "changed-version");
        await changedSource.Should().ThrowAsync<NormalizationConflictException>();
        await changedVersion.Should().ThrowAsync<NormalizationConflictException>();
        (await proposals.GetAttemptAsync(uuid)).Should().BeEquivalentTo(first);
        (await proposals.GetAttemptAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Test]
    public async Task ConcurrentPreparationsRetainTheFirstReceiptAndAdmissionRevisions()
    {
        var source = await fixture.Source();
        var first = await Prepare(source);
        var second = await Prepare(source);
        second = second with
        {
            ExpectedConfigurationGeneration = 41,
            ExpectedContributionRevision = 17,
        };
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            ProcessingVersion
        );

        var saved = await Task.WhenAll(
            proposals.SavePreparationAsync(attempt.AttemptUuid, first),
            proposals.SavePreparationAsync(attempt.AttemptUuid, second)
        );

        saved[0].Should().BeEquivalentTo(saved[1]);
        var winning = saved[0].Preparation!;
        new[] { first.FetchUuid, second.FetchUuid }.Should().Contain(winning.FetchUuid);
        var later = await proposals.SavePreparationAsync(
            attempt.AttemptUuid,
            first with
            {
                ExpectedConfigurationGeneration = 99,
                ExpectedContributionRevision = 99,
            }
        );
        later.Should().BeEquivalentTo(saved[0]);
        var recovered = await new NormalizationEvidenceService(fixture.Db).GetCaptureAsync(
            winning.FetchUuid
        );
        recovered!.Revision.Uuid.Should().Be(winning.Binding.InputRevisionUuid);
        (await proposals.GetAttemptAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(saved[0]);
    }

    [TestCase(NormalizationRecordingProcessingDisposition.Missing)]
    [TestCase(NormalizationRecordingProcessingDisposition.Unresolved)]
    [TestCase(NormalizationRecordingProcessingDisposition.Stale)]
    public async Task FirstTerminalOutcomeIsRetainedWithoutARequiredCatalogSource(
        NormalizationRecordingProcessingDisposition disposition
    )
    {
        var first = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ProcessingVersion
        );
        var terminal = await proposals.CompleteAsync(first, disposition, "original_reason");
        var replay = await proposals.CompleteAsync(
            first,
            NormalizationRecordingProcessingDisposition.Stale,
            "later_reason"
        );
        replay.Should().BeEquivalentTo(terminal);
        terminal.Result!.Disposition.Should().Be(disposition);
        terminal.Result.Reason.Should().Be("original_reason");
        (await proposals.GetAsync(first.SourceUuid)).Should().BeNull();

        var mutate = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.recording_processing_attempts SET reason='replacement' WHERE attempt_uuid=@AttemptUuid",
                    first
                )
            );
        var remove = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "DELETE FROM normalization.recording_processing_attempts WHERE attempt_uuid=@AttemptUuid",
                    first
                )
            );
        await mutate.Should().ThrowAsync<Exception>();
        await remove.Should().ThrowAsync<Exception>();
        (await proposals.GetAttemptAsync(first.AttemptUuid)).Should().BeEquivalentTo(terminal);
    }

    [Test]
    public async Task UnpreparedLoserCannotTerminalizeAConcurrentPreparationWinner()
    {
        var source = await fixture.Source();
        var unprepared = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            ProcessingVersion
        );
        var prepared = await proposals.SavePreparationAsync(
            unprepared.AttemptUuid,
            await Prepare(source)
        );

        var loser = await proposals.CompleteAsync(
            unprepared,
            NormalizationRecordingProcessingDisposition.Stale,
            "discarded_loser_facts"
        );

        loser.Should().BeEquivalentTo(prepared);
        loser.Result.Should().BeNull();
        var terminal = await proposals.CompleteAsync(
            prepared,
            NormalizationRecordingProcessingDisposition.Unresolved,
            "winning_preparation"
        );
        terminal.Result!.Reason.Should().Be("winning_preparation");
    }

    [Test]
    public async Task PreparationMustReferenceItsOwnAdmittedImmutableCapture()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var preparation = await Prepare(source);
        var otherPreparation = await Prepare(other);
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            ProcessingVersion
        );
        var wrongReceipt = () =>
            proposals.SavePreparationAsync(
                attempt.AttemptUuid,
                preparation with
                {
                    FetchUuid = otherPreparation.FetchUuid,
                }
            );

        await wrongReceipt.Should().ThrowAsync<NormalizationConflictException>();
        (await proposals.GetAttemptAsync(attempt.AttemptUuid))!.Preparation.Should().BeNull();
        var saved = await proposals.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var overwrite = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.recording_processing_attempts SET preparation=NULL,preparation_fetch_uuid=NULL WHERE attempt_uuid=@AttemptUuid",
                    attempt
                )
            );
        await overwrite.Should().ThrowAsync<Exception>();
        (await proposals.GetAttemptAsync(attempt.AttemptUuid)).Should().BeEquivalentTo(saved);
    }

    [Test]
    public async Task CallbackRollbackPreservesCheckpointWithoutPublishingTheOutcome()
    {
        var source = await fixture.Source();
        var batch = await Batch(source);
        await proposals.SaveExtractionAsync(batch);
        Func<Task> failed = () =>
            fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                async (c, t, ct) =>
                {
                    await proposals.WritePublishedAsync(c, t, batch, ct);
                    throw new InvalidOperationException("Rollback the complete publication.");
                }
            );

        await failed.Should().ThrowAsync<InvalidOperationException>();
        (await proposals.GetAttemptAsync(batch.AttemptUuid))!.Result.Should().BeNull();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(batch.Ticket.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .BeNull();

        var published = await Publish(batch);
        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await proposals.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(batch);
        (await proposals.GetAttemptAsync(batch.AttemptUuid))!
            .Result!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var replay = await fixture.Work.TryPublishAsync(
            batch.AttemptUuid,
            (_, _, _) =>
                throw new AssertionException(
                    "A completed work replay must not write the batch again."
                )
        );
        replay.Replayed.Should().BeTrue();
    }

    [Test]
    public async Task LastPublishedBatchFollowsItsExtractionOwnerAndSurvivesMissingCatalogRows()
    {
        var source = await fixture.Source();
        var first = await Batch(source);
        await Publish(first);
        var second = await Batch(source);
        (await proposals.GetAsync(source.SourceUuid))!.AttemptUuid.Should().Be(first.AttemptUuid);
        await Publish(second);
        (await proposals.GetAsync(source.SourceUuid))!.AttemptUuid.Should().Be(second.AttemptUuid);

        var third = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            ProcessingVersion
        );
        await proposals.CompleteAsync(
            third,
            NormalizationRecordingProcessingDisposition.Stale,
            "later_stale_attempt"
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@SourceId", source)
        );

        // Last extraction is deliberately available for inspection; catalog deletion is not proof
        // that its original proposals are current or that any identity was accepted.
        (await proposals.GetAsync(source.SourceUuid))
            .Should()
            .BeEquivalentTo(second);
        (await proposals.GetAttemptAsync(first.AttemptUuid))!
            .Result!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
    }

    [Test]
    public async Task CompletedExtractionSurvivesCancellationAndStalePublicationWithoutBecomingCurrent()
    {
        var source = await fixture.Source();
        var batch = await Batch(source);
        var saved = await proposals.SaveExtractionAsync(batch);
        (await proposals.SaveExtractionAsync(batch)).Should().BeEquivalentTo(saved);
        var replacement = batch with { Extraction = batch.Extraction with { Credits = [] } };
        Func<Task> overwrite = () => proposals.SaveExtractionAsync(replacement);
        await overwrite.Should().ThrowAsync<NormalizationConflictException>();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelled = () =>
            fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct),
                cancellation.Token
            );
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE sources SET taper='Later correction' WHERE id=@SourceId", source)
        );
        var stale = await fixture.Work.TryPublishAsync(
            batch.AttemptUuid,
            (_, _, _) =>
                throw new AssertionException(
                    "Stale extraction must not run its publication callback."
                )
        );
        stale.Status.Should().Be(NormalizationPublicationStatus.Stale);
        await proposals.CompleteAsync(
            saved,
            NormalizationRecordingProcessingDisposition.Stale,
            "catalog_changed"
        );

        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptAsync(batch.AttemptUuid))!
            .Result!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
    }

    [Test]
    public async Task PublicationRequiresItsExactPreviouslyCheckpointedExtraction()
    {
        var batch = await Batch(await fixture.Source());
        Func<Task> missingCheckpoint = () =>
            fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
            );
        await missingCheckpoint.Should().ThrowAsync<NormalizationConflictException>();
        await proposals.SaveExtractionAsync(batch);
        var changed = batch with { Extraction = batch.Extraction with { Credits = [] } };
        Func<Task> changedCheckpoint = () =>
            fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, changed, ct)
            );
        await changedCheckpoint.Should().ThrowAsync<NormalizationConflictException>();

        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
        (await proposals.GetAttemptAsync(batch.AttemptUuid))!.Result.Should().BeNull();
        (await Publish(batch)).Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task PublishingCreditAndTitleProposalsLeavesAcceptedPeopleAndMusicUnchanged()
    {
        var source = await fixture.Source();
        var batch = await Batch(source);
        batch.Extraction.Credits.Should().NotBeEmpty();
        batch.Extraction.Titles.Should().NotBeEmpty();
        var before = await AcceptedCounts();

        await Publish(batch);

        (await AcceptedCounts()).Should().Be(before);
        var saved = await proposals.GetAsync(source.SourceUuid);
        saved!.Extraction.CandidateSearchStatus.Should().Be("not_run");
        saved.Extraction.Credits.Should().BeEquivalentTo(batch.Extraction.Credits);
        saved.Extraction.Titles.Should().BeEquivalentTo(batch.Extraction.Titles);
    }

    [Test]
    public async Task VenueClockOutsideCurrentBatchFormatCannotBeSavedOrPublished()
    {
        var source = await fixture.Source();
        var batch = await Batch(source, importedVenues: [new(Guid.NewGuid(), 0)]);
        batch.Ticket.ImportedVenues.Should().ContainSingle();

        Func<Task> save = () => proposals.SaveExtractionAsync(batch);
        await save.Should().ThrowAsync<ArgumentException>().WithMessage("*imported venue inputs*");
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(Owner(source)))!.PublishedWorkUuid.Should().BeNull();

        // A new complete contribution can remove unsupported inputs and proceed.
        var supported = await Batch(source);
        (await Publish(supported)).Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task CandidatePoolOutsideCurrentBatchFormatCannotBeAcknowledged()
    {
        var source = await fixture.Source();
        var batch = await Batch(
            source,
            candidateUniverses:
            [
                new(NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid()), 0),
            ]
        );
        batch.Ticket.CandidateUniverses.Should().ContainSingle();

        Func<Task> save = () => proposals.SaveExtractionAsync(batch);
        await save.Should().ThrowAsync<ArgumentException>().WithMessage("*candidate pool facts*");
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(Owner(source)))!.PublishedWorkUuid.Should().BeNull();

        var supported = await Batch(source);
        (await Publish(supported)).Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    [TestCase("source")]
    [TestCase("version")]
    public async Task TerminalExtractionRejectsChangedHeadersButKeepsItsFirstOutcome(string field)
    {
        var batch = await Batch(await fixture.Source());
        var prepared = (await proposals.GetAttemptAsync(batch.AttemptUuid))!;
        var terminal = await proposals.CompleteAsync(
            prepared,
            NormalizationRecordingProcessingDisposition.Stale,
            "first_outcome"
        );
        var changed =
            field == "source"
                ? batch with
                {
                    SourceUuid = Guid.NewGuid(),
                }
                : batch with
                {
                    ProcessingVersion = "changed-version",
                };
        Func<Task> save = () => proposals.SaveExtractionAsync(changed);

        await save.Should().ThrowAsync<NormalizationConflictException>();

        // Later preparation/body differences cannot replace the already recorded outcome.
        var laterBody = batch with
        {
            Preparation = batch.Preparation with { ExpectedContributionRevision = 99 },
        };
        (await proposals.SaveExtractionAsync(laterBody)).Should().BeEquivalentTo(terminal);
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        (await proposals.GetAttemptAsync(batch.AttemptUuid)).Should().BeEquivalentTo(terminal);
    }

    [TestCase("source")]
    [TestCase("artist")]
    [TestCase("version")]
    [TestCase("ticket")]
    [TestCase("preparation")]
    public async Task ChangedHeaderOrAdmissionCannotReplaceTheSavedAttempt(string field)
    {
        var original = await Batch(await fixture.Source());
        var changed = field switch
        {
            "source" => original with { SourceUuid = Guid.NewGuid() },
            "artist" => original with { ArtistUuid = Guid.NewGuid() },
            "version" => original with { ProcessingVersion = "another-version" },
            "ticket" => original with
            {
                Ticket = original.Ticket with { SubjectUuid = Guid.NewGuid() },
            },
            _ => original with
            {
                Preparation = original.Preparation with { ExpectedContributionRevision = 99 },
            },
        };
        await RejectMalformedExtraction(original, changed);
    }

    [TestCase("wrong_citation")]
    [TestCase("duplicate_file")]
    [TestCase("missing_file")]
    [TestCase("missing_extraction")]
    public async Task TitleProposalsRequireExactFileAssociationAndCompleteCoverage(string defect)
    {
        var original = await Batch(await fixture.Source());
        var title = original.Extraction.Titles.Single();
        var credit = original.Extraction.Credits.First().Mention;
        NormalizationRecordingTitleProposal[] titles = defect switch
        {
            "wrong_citation" =>
            [
                title with
                {
                    Title = new(credit.Name, credit.Evidence),
                    Extraction = new TrackTitleExtractor().Extract(
                        original.ArtistUuid,
                        credit.Name
                    ),
                },
            ],
            "duplicate_file" => [title, title with { ProposalUuid = Guid.NewGuid() }],
            "missing_file" => [],
            _ => [title with { Extraction = null }],
        };
        var changed = original with { Extraction = original.Extraction with { Titles = titles } };
        await RejectMalformedExtraction(original, changed);
    }

    [TestCase("segment_span")]
    [TestCase("segment_quote")]
    [TestCase("qualifier_span")]
    public async Task InnerTitleEvidenceMustReproduceTheOriginalTitle(string defect)
    {
        var original = await Batch(await fixture.Source());
        var title = original.Extraction.Titles.Single();
        var extraction = title.Extraction!;
        var segment = extraction.Segments.Single();
        var changedExtraction = defect switch
        {
            "segment_span" => extraction with
            {
                Segments =
                [
                    segment with
                    {
                        Evidence = segment.Evidence with { EndCodePoint = 1000 },
                    },
                ],
            },
            "segment_quote" => extraction with
            {
                Segments =
                [
                    segment with
                    {
                        Evidence = segment.Evidence with { Quote = "different quote" },
                    },
                ],
            },
            _ => extraction with
            {
                Qualifiers = [new("separator", new("title", "/title", "outside title", 100, 110))],
            },
        };
        var changed = original with
        {
            Extraction = original.Extraction with
            {
                Titles = [title with { Extraction = changedExtraction }],
            },
        };
        await RejectMalformedExtraction(original, changed);
    }

    private async Task RejectMalformedExtraction(
        NormalizationRecordingProposalBatch original,
        NormalizationRecordingProposalBatch changed
    )
    {
        Func<Task> save = () => proposals.SaveExtractionAsync(changed);
        await save.Should().ThrowAsync<Exception>();
        (await proposals.GetAttemptAsync(original.AttemptUuid))!.Result.Should().BeNull();
        (await proposals.GetAttemptBatchAsync(original.AttemptUuid)).Should().BeNull();
        (await proposals.GetAsync(original.SourceUuid)).Should().BeNull();
        (await Publish(original)).Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    [TestCase("source")]
    [TestCase("artist")]
    public async Task FalseBoundFileScopeCannotSupplyPublishedTitleProposals(string field)
    {
        var source = await fixture.Source();
        var batch = await Batch(
            source,
            preparation =>
                preparation with
                {
                    Binding = preparation.Binding with
                    {
                        Files = preparation
                            .Binding.Files.Select(file =>
                                file with
                                {
                                    Binding = file.Binding with
                                    {
                                        Value =
                                            field == "source"
                                                ? file.Binding.Value! with
                                                {
                                                    SourceUuid = Guid.NewGuid(),
                                                }
                                                : file.Binding.Value! with
                                                {
                                                    ArtistUuid = Guid.NewGuid(),
                                                },
                                    },
                                }
                            )
                            .ToArray(),
                    },
                }
        );
        Func<Task> publish = () => Publish(batch);

        await publish.Should().ThrowAsync<ArgumentException>();
        (await proposals.GetAttemptAsync(batch.AttemptUuid))!.Result.Should().BeNull();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
    }

    [Test]
    public async Task UnicodeTitleEvidencePublishesWithItsOriginalText()
    {
        var source = await fixture.Source();
        const string title = "🎤 Song > Tune (partial)";
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET title=@title WHERE uuid=@TrackUuid",
                new { title, source.TrackUuid }
            )
        );
        var batch = await Batch(source);

        (await Publish(batch)).Status.Should().Be(NormalizationPublicationStatus.Published);

        var saved = await proposals.GetAsync(source.SourceUuid);
        saved!.Extraction.Titles.Single().Title!.Value.Should().Be(title);
        saved
            .Extraction.Titles.Single()
            .Extraction.Should()
            .BeEquivalentTo(batch.Extraction.Titles.Single().Extraction);
    }

    private async Task<NormalizationRecordingPreparation> Prepare(CatalogSource source)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var snapshot =
            await new NormalizationLegacyInputService(fixture.Db, evidence).RefreshRecordingAsync(
                source.SourceUuid
            ) ?? throw new AssertionException("The fixture recording must exist.");
        var decoded = new NormalizationProviderDecoder().Decode(snapshot.Capture.Revision);
        var binding = await new NormalizationCatalogBindingService(
            fixture.Db
        ).BindRefreshedRecordingAsync(snapshot, decoded.Recordings.Single());
        var contributor = await fixture.Work.GetInputContributionAsync(
            Owner(source),
            Contributor(source)
        );
        var owner =
            contributor == null
                ? null
                : await fixture.Work.GetSubjectAsync(contributor.SubjectUuid);
        return new(
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            binding,
            owner?.DesiredGeneration,
            contributor?.Revision ?? 0
        );
    }

    private async Task<NormalizationRecordingProposalBatch> Batch(
        CatalogSource source,
        Func<
            NormalizationRecordingPreparation,
            NormalizationRecordingPreparation
        >? changePreparation = null,
        IReadOnlyList<NormalizationImportedVenueDependency>? importedVenues = null,
        IReadOnlyList<NormalizationCandidateUniverseDependency>? candidateUniverses = null
    )
    {
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("UPDATE sources SET taper='Zoë Recorder' WHERE id=@SourceId", source)
        );
        var attempt = await proposals.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            ProcessingVersion
        );
        var proposed = await Prepare(source);
        var saved = await proposals.SavePreparationAsync(
            attempt.AttemptUuid,
            changePreparation?.Invoke(proposed) ?? proposed
        );
        var preparation = saved.Preparation!;
        var ticket = await fixture.Work.BeginContributedWorkAsync(
            attempt.AttemptUuid,
            Owner(source),
            ProcessingVersion,
            new(
                Contributor(source),
                preparation.ExpectedContributionRevision,
                preparation.Binding.Inputs.Select(input => input.Resource).ToArray(),
                []
            ),
            preparation.ExpectedConfigurationGeneration,
            catalogInputs: preparation.Binding.CatalogInputs,
            sourceIdentities: preparation.Binding.SourceIdentities,
            mappingRegistry: preparation.Binding.MappingRegistry,
            expectedInputRevisions: preparation.Binding.Inputs,
            importedVenues: importedVenues,
            candidateUniverses: candidateUniverses
        );
        var input = (
            await new NormalizationEvidenceService(fixture.Db).GetCaptureAsync(
                preparation.FetchUuid
            )
        )!.Revision;
        var decoded = new NormalizationProviderDecoder().Decode(input);
        var recording = decoded.Recordings.Single();
        var credits = new RetainedRecordingCreditExtractor().Extract(input, recording.Text);
        var artist = preparation.Binding.Recording.Value!.ArtistUuid;
        var titles = preparation
            .Binding.Files.Where(file => file.Binding.Value != null)
            .Select(file => new NormalizationRecordingTitleProposal(
                Guid.NewGuid(),
                file.Binding.Value!.TrackUuid,
                file.Observation.Title,
                file.Observation.Title == null
                    ? null
                    : new TrackTitleExtractor().Extract(artist, file.Observation.Title.Value),
                file.Observation.Title == null ? "missing_title" : null
            ))
            .ToArray();
        return new(
            attempt.AttemptUuid,
            source.SourceUuid,
            artist,
            ProcessingVersion,
            preparation,
            ticket,
            new(
                credits
                    .Credits.Select(mention => new NormalizationRecordingCreditProposal(
                        Guid.NewGuid(),
                        mention
                    ))
                    .ToArray(),
                credits.Unresolved,
                titles,
                decoded.Issues
            )
        );
    }

    private async Task<NormalizationPublicationResult> Publish(
        NormalizationRecordingProposalBatch batch
    )
    {
        await proposals.SaveExtractionAsync(batch);
        return await fixture.Work.TryPublishAsync(
            batch.AttemptUuid,
            (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
        );
    }

    private Task<string> AcceptedCounts() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_array((SELECT count(*) FROM normalization.people),
                    (SELECT count(*) FROM normalization.recording_credits WHERE state='accepted'),
                    (SELECT count(*) FROM normalization.repertoire_songs),(SELECT count(*) FROM normalization.track_contents))::text
                """
            )
        );

    private static NormalizationSubjectKey Owner(CatalogSource source) =>
        new("extractions", "recording", source.SourceUuid);

    private static NormalizationResourceKey Contributor(CatalogSource source) =>
        new("relisten", "recording", source.SourceUuid.ToString("D"));
}
