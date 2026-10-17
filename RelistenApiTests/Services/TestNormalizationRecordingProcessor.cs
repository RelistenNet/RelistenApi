using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>
/// Exercises durable recording extraction through real retention, binding, work admission and proposal storage.
/// Published proposals are not accepted person or song assignments.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingProcessor
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationLegacyInputService legacy = null!;
    private NormalizationCatalogBindingService bindings = null!;
    private NormalizationRecordingProposalService proposals = null!;
    private NormalizationRecordingProcessor processor = null!;
    private readonly NormalizationProviderDecoder decoder = new();
    private readonly NormalizationRecordingExtractor extractor = new(new(), new());

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        legacy = new(fixture.Db, evidence);
        bindings = new(fixture.Db);
        proposals = new(fixture.Db);
        processor = new(legacy, evidence, bindings, decoder, extractor, fixture.Work, proposals);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("source:2169954", "Dave Sorochty", RecordingCreditRole.Transferrer)]
    [TestCase("source:1329586", "Keith Litzenberger", RecordingCreditRole.Taper)]
    [TestCase("local:source:4283434", "Scott Bernstein", RecordingCreditRole.Taper)]
    public async Task RetainedCrossArtistCreditsSurviveCatalogRefreshAndDurablePublication(
        string evidenceId,
        string name,
        RecordingCreditRole role
    )
    {
        var path = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "normalization",
            "evidence.jsonl"
        );
        using var corpus = JsonDocument.Parse("[" + string.Join(",", File.ReadLines(path)) + "]");
        var row = corpus
            .RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == evidenceId);
        var fields = row.GetProperty("fields");
        string? Text(string field) =>
            fields.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        var source = await fixture.Source();
        // These catalog artists and identities are synthetic. Credit wording is replayed
        // exactly; this test does not reconstruct an original provider response.
        await fixture.Db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                "UPDATE sources SET taper=@taper,transferrer=@transferrer,lineage=@lineage,source=@source,description=@description WHERE id=@id",
                new
                {
                    taper = Text("taper"),
                    transferrer = Text("transferrer"),
                    lineage = Text("lineage"),
                    source = Text("source"),
                    description = Text("description"),
                    id = source.SourceId,
                }
            );
        });

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await proposals.GetAsync(source.SourceUuid))!;
        batch
            .Extraction.Credits.Should()
            .Contain(credit => credit.Mention.Name == name && credit.Mention.Role == role);
        var capture = (await evidence.GetCaptureAsync(batch.Preparation.FetchUuid))!;
        using var retained = NormalizationRetainedDocument.Open(capture.Revision);
        foreach (var credit in batch.Extraction.Credits)
        {
            retained.ValidateEvidence(credit.Mention.Evidence);
        }
        (await proposals.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeEquivalentTo(batch);
    }

    [Test]
    public async Task LocalExtractionPublishesCreditsAndCompoundTitlesWithoutChangingAcceptedDataOrPlayback()
    {
        var source = await Source();
        var acceptedBefore = await AcceptedRowCount();
        var playbackBefore = await Playback(source.TrackUuid);

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        result.Replayed.Should().BeFalse();
        var batch = (await proposals.GetAsync(source.SourceUuid))!;
        batch.SourceUuid.Should().Be(source.SourceUuid);
        batch.Extraction.CandidateSearchStatus.Should().Be("not_run");
        batch
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.Mention.Name == "Pat Recorder"
                && credit.Mention.Role == RecordingCreditRole.Taper
            );
        batch
            .Extraction.Credits.Should()
            .Contain(credit =>
                credit.Mention.Name == "Robin Transfer"
                && credit.Mention.Role == RecordingCreditRole.Transferrer
            );
        var title = batch.Extraction.Titles.Should().ContainSingle().Which;
        title.TrackUuid.Should().Be(source.TrackUuid);
        title
            .Extraction!.Segments.Select(segment => segment.Title)
            .Should()
            .Equal("Dark Star", "Wharf Rat");
        title
            .Extraction.Segments.Should()
            .OnlyContain(segment =>
                segment.OccurrenceUuid == null && segment.StartMs == null && segment.EndMs == null
            );
        batch.Preparation.Binding.Recording.Value!.ContextIssues.Should().Contain("missing_show");
        batch
            .Extraction.DecodeIssues.Should()
            .Contain(issue => issue.Code == "missing_recording_show_context");
        (await AcceptedRowCount()).Should().Be(acceptedBefore);
        (await Playback(source.TrackUuid)).Should().Be(playbackBefore);
        (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!
            .PublishedWorkUuid.Should()
            .Be(result.AttemptUuid);
    }

    [Test]
    public async Task ExactRetryReturnsTheSameBatchWithoutAnotherFetchAndRejectsChangedRequestHeaders()
    {
        var source = await Source();
        var attemptUuid = Guid.NewGuid();
        var first = await processor.ProcessAsync(attemptUuid, source.SourceUuid);
        var batch = await proposals.GetAsync(source.SourceUuid);
        var fetches = await Fetches(source.SourceUuid);

        var repeated = await processor.ProcessAsync(attemptUuid, source.SourceUuid);

        repeated.Should().Be(first with { Replayed = true });
        (await proposals.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(batch);
        (await Fetches(source.SourceUuid)).Should().Equal(fetches);
        var other = await fixture.Source();
        Func<Task> changedSource = () => processor.ProcessAsync(attemptUuid, other.SourceUuid);
        await changedSource.Should().ThrowAsync<NormalizationConflictException>();
        Func<Task> changedVersion = () =>
            proposals.GetOrCreateAttemptAsync(
                attemptUuid,
                source.SourceUuid,
                "another-processing-version"
            );
        await changedVersion.Should().ThrowAsync<NormalizationConflictException>();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(batch);
    }

    [Test]
    public async Task SavedPreparationResumesAfterRestartWithoutRefreshingItsOriginalReceipt()
    {
        var source = await Source();
        var prepared = await Prepare(Guid.NewGuid(), source.SourceUuid);
        var restarted = new NormalizationRecordingProcessor(
            legacy,
            evidence,
            bindings,
            decoder,
            extractor,
            fixture.Work,
            new(fixture.Db)
        );

        var result = await restarted.ProcessAsync(prepared.Attempt.AttemptUuid, source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await proposals.GetAsync(source.SourceUuid))!;
        batch.Preparation.Should().BeEquivalentTo(prepared.Attempt.Preparation);
        (await Fetches(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(prepared.Snapshot.Capture.Fetch.FetchUuid);
    }

    [Test]
    public async Task RecoveryAfterAdmissionReplaysTheSavedRequestDespiteTheAdvancedContributorRevision()
    {
        var source = await Source();
        var prepared = await Prepare(Guid.NewGuid(), source.SourceUuid);
        var admitted = await Admit(prepared.Attempt);
        (
            await fixture.Work.GetInputContributionAsync(
                Owner(source.SourceUuid),
                Contributor(source.SourceUuid)
            )
        )!
            .Revision.Should()
            .Be(1);

        var result = await processor.ProcessAsync(prepared.Attempt.AttemptUuid, source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        (await proposals.GetAsync(source.SourceUuid))!.Ticket.Should().BeEquivalentTo(admitted);
        (
            await fixture.Work.GetInputContributionAsync(
                Owner(source.SourceUuid),
                Contributor(source.SourceUuid)
            )
        )!
            .Revision.Should()
            .Be(1);
        (await Fetches(source.SourceUuid)).Should().ContainSingle();
    }

    [TestCase("catalog")]
    [TestCase("input")]
    public async Task ChangedPreparedReadSetBecomesStaleWithoutPublishingABatch(string changed)
    {
        var source = await Source();
        var prepared = await Prepare(Guid.NewGuid(), source.SourceUuid);
        if (changed == "catalog")
        {
            await Execute(
                "UPDATE sources SET taper='Changed Recorder' WHERE id=@id",
                new { id = source.SourceId }
            );
        }
        else
        {
            var current = (await evidence.GetCurrentInputAsync(Resource(source.SourceUuid)))!;
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    current.Resource,
                    Encoding.UTF8.GetBytes(
                        "{\"representation\":\"legacy_catalog\",\"changed\":true}"
                    ),
                    current.Revision.MediaType,
                    current.Revision.CaptureVersion,
                    current.Generation,
                    DateTimeOffset.UtcNow
                )
            );
        }

        var result = await processor.ProcessAsync(prepared.Attempt.AttemptUuid, source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Stale);
        result.Reason.Should().Be("admission_changed");
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptBatchAsync(prepared.Attempt.AttemptUuid)).Should().BeNull();
        (await proposals.GetAttemptAsync(prepared.Attempt.AttemptUuid))!.Result.Should().Be(result);
    }

    [Test]
    public async Task StaleLaterAttemptKeepsThePreviouslyPublishedBatch()
    {
        var source = await Source();
        await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        var previous = await proposals.GetAsync(source.SourceUuid);
        var prepared = await Prepare(Guid.NewGuid(), source.SourceUuid);
        await Execute(
            "UPDATE sources SET taper='New metadata after preparation' WHERE id=@id",
            new { id = source.SourceId }
        );

        var result = await processor.ProcessAsync(prepared.Attempt.AttemptUuid, source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Stale);
        (await proposals.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(previous);
        (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!
            .PublishedWorkUuid.Should()
            .Be(previous!.AttemptUuid);
    }

    [Test]
    public async Task StaleComputedExtractionRemainsAuditableWithoutReplacingThePublishedBatch()
    {
        var source = await Source();
        await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        var published = await proposals.GetAsync(source.SourceUuid);
        var prepared = await Prepare(Guid.NewGuid(), source.SourceUuid);
        var ticket = await Admit(prepared.Attempt);
        var computed = Batch(prepared, ticket);
        await proposals.SaveExtractionAsync(computed);
        await Execute(
            "UPDATE sources SET taper='Correction after extraction checkpoint' WHERE id=@id",
            new { id = source.SourceId }
        );

        var result = await processor.ProcessAsync(prepared.Attempt.AttemptUuid, source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Stale);
        (await proposals.GetAttemptBatchAsync(prepared.Attempt.AttemptUuid))
            .Should()
            .BeEquivalentTo(computed);
        (await proposals.GetAsync(source.SourceUuid)).Should().BeEquivalentTo(published);
        (await proposals.GetAttemptAsync(prepared.Attempt.AttemptUuid))!
            .Result!.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Stale);
    }

    [Test]
    public async Task MissingAttemptReplaysItsTerminalResultEvenAfterTheRecordingArrives()
    {
        var sourceUuid = Guid.NewGuid();
        var attemptUuid = Guid.NewGuid();
        var missing = await processor.ProcessAsync(attemptUuid, sourceUuid);
        missing.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Missing);
        var artist = await fixture.Source();
        await Execute(
            "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@artist,@key,'2000-01-01',@uuid,now(),false,false,false)",
            new
            {
                artist = artist.ArtistId,
                key = sourceUuid.ToString(),
                uuid = sourceUuid,
            }
        );

        var repeated = await processor.ProcessAsync(attemptUuid, sourceUuid);

        repeated.Should().Be(missing with { Replayed = true });
        (await Fetches(sourceUuid)).Should().BeEmpty();
        (await proposals.GetAsync(sourceUuid)).Should().BeNull();
    }

    [Test]
    public async Task PreCancelledAttemptCanRetryWithoutLosingItsRecording()
    {
        var source = await Source();
        var attemptUuid = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> cancelled = () =>
            processor.ProcessAsync(attemptUuid, source.SourceUuid, cancellation.Token);
        await cancelled.Should().ThrowAsync<OperationCanceledException>();
        ((await proposals.GetAttemptAsync(attemptUuid))?.Result).Should().BeNull();

        var recovered = await processor.ProcessAsync(attemptUuid, source.SourceUuid);

        recovered.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        (await Fetches(source.SourceUuid)).Should().ContainSingle();
    }

    [Test]
    public async Task AdditionalContributorRemainsOwnedWithoutPublishingAPartialReadSet()
    {
        var source = await Source();
        var extra = await fixture.Capture("Other contributor input", "people");
        var contributor = new NormalizationResourceKey(
            "test",
            "other-recording-input",
            Guid.NewGuid().ToString()
        );
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            Owner(source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            new(contributor, 0, [extra.Resource], [])
        );

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Unresolved);
        result.Reason.Should().Be("unsupported_owner_inputs");
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
        var contribution = (
            await fixture.Work.GetInputContributionAsync(Owner(source.SourceUuid), contributor)
        )!;
        contribution.Resources.Should().Equal(extra.Resource);
        contribution.Revision.Should().Be(1);
        var current = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        current.PublishedWorkUuid.Should().BeNull();
        (await IsPending(current.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task VenueOnlyContributorCannotBeAcknowledgedWithoutReadingItsFacts()
    {
        var source = await Source();
        var contributor = new NormalizationResourceKey(
            "test",
            "other-venue-input",
            Guid.NewGuid().ToString("D")
        );
        var venue = new NormalizationImportedVenueDependency(Guid.NewGuid(), 0);
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            Owner(source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            new(contributor, 0, [], []),
            importedVenues: [venue]
        );

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Unresolved);
        result.Reason.Should().Be("unsupported_owner_inputs");
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
        var retained = (
            await fixture.Work.GetInputContributionAsync(Owner(source.SourceUuid), contributor)
        )!;
        retained.ImportedVenueUuids.Should().Equal(venue.VenueUuid);
        retained.Revision.Should().Be(1);
        var owner = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        owner.PublishedWorkUuid.Should().BeNull();
        (await IsPending(owner.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task CandidatePoolOnlyContributorCannotBeAcknowledgedWithoutReadingFacts()
    {
        var source = await Source();
        var contributor = new NormalizationResourceKey(
            "test",
            "other-candidate-pool",
            Guid.NewGuid().ToString("D")
        );
        var universe = NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid());
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            Owner(source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            new(contributor, 0, [], []),
            candidateUniverses: [new(universe, 0)]
        );

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Unresolved);
        result.Reason.Should().Be("unsupported_owner_inputs");
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
        var retained = (
            await fixture.Work.GetInputContributionAsync(Owner(source.SourceUuid), contributor)
        )!;
        retained.CandidateUniverses.Should().Equal(universe);
        retained.Revision.Should().Be(1);
        var owner = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        owner.PublishedWorkUuid.Should().BeNull();
        (await IsPending(owner.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task ObservedOwnerContributorCannotBeFreshenedByRecordingExtraction()
    {
        var source = await Source();
        var observed = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", Guid.NewGuid()),
            "observed-owner-fixture-v1",
            [],
            []
        );
        var contributor = new NormalizationResourceKey(
            "test",
            "other-observed-owner",
            Guid.NewGuid().ToString("D")
        );
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            Owner(source.SourceUuid),
            NormalizationRecordingProcessor.Version,
            new(contributor, 0, [], [], OwnerObservationSubjectUuids: [observed.SubjectUuid]),
            candidateUniverses:
            [
                new(NormalizationCandidateUniverseKey.ForRepertoire(Guid.NewGuid()), 0),
            ],
            expectedOwnerObservations:
            [
                new(
                    observed.SubjectUuid,
                    observed.DesiredGeneration,
                    observed.EditorialRevision,
                    observed.PreviousPublishedWorkUuid
                ),
            ]
        );

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        // The recording-only producer has no original candidate snapshot to submit.
        // Admission must reject the incomplete union instead of capturing fresh owner clocks.
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Stale);
        result.Reason.Should().Be("admission_changed");
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptBatchAsync(result.AttemptUuid)).Should().BeNull();
        var retained = (
            await fixture.Work.GetInputContributionAsync(Owner(source.SourceUuid), contributor)
        )!;
        retained.OwnerObservationSubjectUuids.Should().Equal(observed.SubjectUuid);
        retained.Revision.Should().Be(1);
        var owner = (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!;
        owner.PublishedWorkUuid.Should().BeNull();
        (await IsPending(owner.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task UnknownCreditsAndEmptyTitlesDoNotWithdrawExistingAcceptedAssignments()
    {
        var source = await Source();
        var creditInput = await fixture.Capture("Pat Recorder", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat Recorder");
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            "Pat Recorder",
            person.Uuid,
            null,
            [creditInput.Reference]
        );
        await fixture.PublishPeople(
            new(fixture.Db, fixture.Work),
            creditInput,
            new([person], [], [], [credit])
        );
        var artistUuid = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=@id",
                new { id = source.ArtistId }
            )
        );
        var songInput = await fixture.Capture("Known Song", "songs");
        songInput = await fixture.Own(songInput, "artist_repertoire", artistUuid);
        var songUuid = Guid.NewGuid();
        await new NormalizedMusicService(fixture.Db, fixture.Work).PublishAsync(
            songInput.Ticket.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs =
                [
                    new()
                    {
                        Uuid = songUuid,
                        ArtistUuid = artistUuid,
                        DisplayTitle = "Known Song",
                        Evidence = [songInput.Reference],
                    },
                ],
            }
        );
        await Execute(
            "UPDATE sources SET taper='unknown',transferrer=NULL WHERE id=@id; UPDATE source_tracks SET title='' WHERE uuid=@track",
            new { id = source.SourceId, track = source.TrackUuid }
        );
        var acceptedBefore = await AcceptedRowCount();

        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);

        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await proposals.GetAsync(source.SourceUuid))!;
        batch.Extraction.Credits.Should().BeEmpty();
        batch
            .Extraction.UnresolvedCredits.Should()
            .Contain(issue => issue.Reason == "unknown_credit");
        batch.Extraction.Titles.Single().Extraction!.IsResolved.Should().BeFalse();
        (await AcceptedRowCount()).Should().Be(acceptedBefore);
        var creditState = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<string>(
                "SELECT state FROM normalization.recording_credits WHERE uuid=@uuid",
                new { uuid = credit.Uuid }
            )
        );
        creditState.Should().Be("accepted");
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT retracted FROM normalization.repertoire_songs WHERE uuid=@uuid",
                    new { uuid = songUuid }
                )
            )
        )
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task SavedBindingWithAForgedArtistCannotPublishUsingAValidReceiptAndProducerClocks()
    {
        var source = await Source();
        var fakeArtistUuid = Guid.NewGuid();
        var prepared = await Prepare(
            Guid.NewGuid(),
            source.SourceUuid,
            binding =>
                binding with
                {
                    Recording = binding.Recording with
                    {
                        Value = binding.Recording.Value! with { ArtistUuid = fakeArtistUuid },
                    },
                    Files = binding
                        .Files.Select(file =>
                            file with
                            {
                                Binding = file.Binding with
                                {
                                    Value = file.Binding.Value! with
                                    {
                                        ArtistUuid = fakeArtistUuid,
                                    },
                                },
                            }
                        )
                        .ToArray(),
                }
        );

        Func<Task> process = () =>
            processor.ProcessAsync(prepared.Attempt.AttemptUuid, source.SourceUuid);

        await process.Should().ThrowAsync<InvalidOperationException>();
        (await proposals.GetAsync(source.SourceUuid)).Should().BeNull();
        (await proposals.GetAttemptAsync(prepared.Attempt.AttemptUuid))!.Result.Should().BeNull();
        (await fixture.Work.GetSubjectAsync(Owner(source.SourceUuid)))!
            .PublishedWorkUuid.Should()
            .BeNull();
    }

    [Test]
    public async Task PublishedConcurrentWinnerSurvivesALaterBindConflictInAnAlreadyRunningCaller()
    {
        var source = await Source();
        var prepared = await Prepare(Guid.NewGuid(), source.SourceUuid);
        var ticket = await Admit(prepared.Attempt);
        var batch = Batch(prepared, ticket);
        await proposals.SaveExtractionAsync(batch);
        await using var gate = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await gate.OpenAsync();
        await using var transaction = await gate.BeginTransactionAsync();
        await gate.ExecuteAsync(
            "LOCK TABLE normalization.input_fetches IN ACCESS EXCLUSIVE MODE",
            transaction: transaction
        );
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<NormalizationRecordingProcessingResult>? concurrent = null;
        NormalizationRecordingProcessingResult? resumed = null;
        try
        {
            // This caller has read the unfinished attempt and replayed admission before its
            // receipt read blocks. Publish the winner without rereading that receipt table.
            concurrent = processor.ProcessAsync(
                prepared.Attempt.AttemptUuid,
                source.SourceUuid,
                deadline.Token
            );
            await WaitForReceiptRead(gate.ProcessID);
            var published = await fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                (connection, workTransaction, ct) =>
                    proposals.WritePublishedAsync(connection, workTransaction, batch, ct),
                deadline.Token
            );
            published.Status.Should().Be(NormalizationPublicationStatus.Published);
            await Execute(
                "UPDATE sources SET taper='Changed after concurrent publication' WHERE id=@id",
                new { id = source.SourceId }
            );
        }
        finally
        {
            await transaction.RollbackAsync();
            if (concurrent != null)
            {
                resumed = await concurrent;
            }
        }

        resumed!.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        resumed.Replayed.Should().BeTrue();
        (await proposals.GetAsync(source.SourceUuid))!
            .AttemptUuid.Should()
            .Be(prepared.Attempt.AttemptUuid);
        (await Fetches(source.SourceUuid)).Should().ContainSingle();
    }

    private async Task<NormalizationDomainFixture.CatalogSource> Source()
    {
        var source = await fixture.Source();
        await Execute(
            "UPDATE sources SET taper='Pat Recorder',transferrer='Robin Transfer' WHERE id=@id; UPDATE source_tracks SET title='Dark Star > Wharf Rat' WHERE uuid=@track",
            new { id = source.SourceId, track = source.TrackUuid }
        );
        return source;
    }

    private async Task<PreparedContext> Prepare(
        Guid attemptUuid,
        Guid sourceUuid,
        Func<
            NormalizationRecordingBindingSnapshot,
            NormalizationRecordingBindingSnapshot
        >? transform = null
    )
    {
        await proposals.GetOrCreateAttemptAsync(
            attemptUuid,
            sourceUuid,
            NormalizationRecordingProcessor.Version
        );
        var snapshot = (await legacy.RefreshRecordingAsync(sourceUuid))!;
        var decoded = decoder.Decode(snapshot.Capture.Revision);
        var bound = await bindings.BindRefreshedRecordingAsync(
            snapshot,
            decoded.Recordings.Single()
        );
        bound = transform?.Invoke(bound) ?? bound;
        var owner = await fixture.Work.GetSubjectAsync(Owner(sourceUuid));
        var contribution = await fixture.Work.GetInputContributionAsync(
            Owner(sourceUuid),
            Contributor(sourceUuid)
        );
        var preparation = new NormalizationRecordingPreparation(
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            bound,
            owner?.DesiredGeneration,
            contribution?.Revision ?? 0
        );
        var attempt = await proposals.SavePreparationAsync(attemptUuid, preparation);
        return new(attempt, snapshot, decoded);
    }

    private Task<NormalizationWorkTicket> Admit(NormalizationRecordingAttempt attempt)
    {
        var saved = attempt.Preparation!;
        return fixture.Work.BeginContributedWorkAsync(
            attempt.AttemptUuid,
            Owner(attempt.SourceUuid),
            attempt.ProcessingVersion,
            new(
                Contributor(attempt.SourceUuid),
                saved.ExpectedContributionRevision,
                saved.Binding.Inputs.Select(input => input.Resource).ToArray(),
                []
            ),
            saved.ExpectedConfigurationGeneration,
            catalogInputs: saved.Binding.CatalogInputs,
            sourceIdentities: saved.Binding.SourceIdentities,
            mappingRegistry: saved.Binding.MappingRegistry,
            expectedInputRevisions: saved.Binding.Inputs
        );
    }

    private NormalizationRecordingProposalBatch Batch(
        PreparedContext prepared,
        NormalizationWorkTicket ticket
    ) =>
        new(
            prepared.Attempt.AttemptUuid,
            prepared.Attempt.SourceUuid,
            prepared.Attempt.Preparation!.Binding.Recording.Value!.ArtistUuid,
            prepared.Attempt.ProcessingVersion,
            prepared.Attempt.Preparation,
            ticket,
            extractor.Extract(
                prepared.Snapshot.Capture.Revision,
                prepared.Decoded,
                prepared.Attempt.Preparation.Binding
            )
        );

    private async Task<bool> IsPending(Guid subjectUuid)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (page.Any(owner => owner.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            after = page[^1].SubjectUuid;
        }
    }

    private Task<long> AcceptedRowCount() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                """
                SELECT (SELECT count(*) FROM normalization.people)+(SELECT count(*) FROM normalization.recording_credits)
                    +(SELECT count(*) FROM normalization.canonical_venues)+(SELECT count(*) FROM normalization.repertoire_songs)
                    +(SELECT count(*) FROM normalization.compositions)+(SELECT count(*) FROM normalization.song_occurrences)
                    +(SELECT count(*) FROM normalization.track_contents)
                """
            )
        );

    private Task<PlaybackIdentity> Playback(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<PlaybackIdentity>(
                "SELECT s.uuid AS SourceUuid,tr.uuid AS TrackUuid,ss.uuid AS SetUuid,tr.mp3_url AS MediaUrl FROM source_tracks tr JOIN sources s ON s.id=tr.source_id JOIN source_sets ss ON ss.id=tr.source_set_id WHERE tr.uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<Guid[]> Fetches(Guid sourceUuid) =>
        (await evidence.GetFetchHistoryAsync(Resource(sourceUuid)))
            .Select(fetch => fetch.FetchUuid)
            .ToArray();

    private Task Execute(string sql, object? values = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, values));

    private static async Task WaitForReceiptRead(int blockerPid)
    {
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        var waiting = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!waiting && DateTime.UtcNow < deadline)
        {
            waiting = await observer.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND @blockerPid=ANY(pg_blocking_pids(pid)) AND query LIKE '%input_fetches%')",
                new { blockerPid }
            );
            if (!waiting)
            {
                await Task.Delay(10);
            }
        }
        waiting
            .Should()
            .BeTrue(
                "the concurrent caller must read the unfinished attempt before the winner publishes"
            );
    }

    private static NormalizationSubjectKey Owner(Guid uuid) =>
        new("extractions", "recording", uuid);

    private static NormalizationResourceKey Contributor(Guid uuid) =>
        new("relisten", "recording", uuid.ToString("D"));

    private static NormalizationResourceKey Resource(Guid uuid) =>
        new("legacy_catalog", "recording", uuid.ToString("D"));

    private sealed record PreparedContext(
        NormalizationRecordingAttempt Attempt,
        NormalizationLegacyRecordingSnapshot Snapshot,
        ProviderDecodeResult Decoded
    );

    private sealed record PlaybackIdentity(
        Guid SourceUuid,
        Guid TrackUuid,
        Guid SetUuid,
        string? MediaUrl
    );
}
