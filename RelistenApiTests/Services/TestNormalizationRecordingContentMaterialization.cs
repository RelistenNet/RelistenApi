using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationRecordingContentMaterialization
{
    private const string Operator = "recording-materialization-fixture";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizedMusicService music = null!;
    private RepertoireConsumerPolicyService policies = null!;
    private NormalizationRecordingContentMaterializer materializer = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        music = new(fixture.Db, fixture.Work);
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        materializer = new(
            fixture.Db,
            new(fixture.Db),
            new(fixture.Db),
            policies,
            fixture.Work,
            music
        );
    }

    [TearDown]
    public async Task ResetPolicy()
    {
        var state = await policies.ReadStateAsync();
        if (state.Active != null)
        {
            await policies.ApplyDecisionAsync(
                new(
                    Guid.NewGuid(),
                    state.OwnerObservation.SubjectUuid,
                    state.OwnerObservation.EditorialRevision,
                    state.Active.Policy.Artifact.Uuid,
                    null,
                    "End isolated fixture policy"
                ),
                Operator
            );
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task CompleteMedleyRetainsEverySegmentAndLegacyPlaybackWithoutInventingAlignment()
    {
        var titles = new[] { Title(), Title(), Title() };
        var source = await Source(string.Join(" > ", titles));
        var account = await Resolve(source);
        var before = await Playback(source);
        var uuid = Guid.NewGuid();

        var completed = await Drain(uuid, new(account.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        var contents = await music.GetTrackContentsAsync(source.TrackUuid);
        contents.Should().HaveCount(3);
        contents.Select(row => row.Value.Position).Should().Equal(1, 2, 3);
        contents.Select(row => row.Value.OriginalText).Should().Equal(titles);
        contents
            .Should()
            .OnlyContain(row =>
                row.Value.SourceTrackUuid == source.TrackUuid
                && row.Value.RepertoireSongUuid != null
                && row.Value.OccurrenceUuid == null
                && row.Value.StartMs == null
                && row.Value.EndMs == null
                && row.Value.Basis == "title_derived"
            );
        (await Decisions(uuid))
            .Should()
            .HaveCount(3)
            .And.OnlyContain(decision =>
                decision.Source != null
                && decision.Source.TrackUuid == source.TrackUuid
                && decision.Disposition == MusicMaterializationDecisionDisposition.Accepted
            );
        (await Playback(source)).Should().Be(before);
        await AssertSettled(source);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("!!! ???")]
    public async Task MissingOrUnsupportedTitleAcknowledgesItsRealSourceWithoutCreatingContents(
        string? title
    )
    {
        var source = await Source(title);
        var account = await Resolve(source);
        var uuid = Guid.NewGuid();

        var completed = await Drain(uuid, new(account.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        (await Decisions(uuid))
            .Should()
            .ContainSingle()
            .Which.Disposition.Should()
            .Be(MusicMaterializationDecisionDisposition.Unresolved);
        await AssertSettled(source);
    }

    [Test]
    public async Task MixedKnownAndUnknownRawTitleCannotPublishAPartialFileImage()
    {
        var source = await Source("Jam > unknown");
        var account = await Resolve(source);

        var completed = await Drain(Guid.NewGuid(), new(account.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        completed.Preparation!.Image!.Contents.Should().BeEmpty();
        completed.Preparation.Image.Correspondence.Should().BeEmpty();
        completed.Preparation.Image.Withdrawals.Should().BeEmpty();
        var decisions = await Decisions(completed.AttemptUuid);
        decisions.Should().HaveCount(2);
        decisions
            .Should()
            .ContainSingle(decision =>
                decision.Disposition == MusicMaterializationDecisionDisposition.Unresolved
                && decision.Reason == "repertoire_unresolved"
            );
        decisions
            .Should()
            .ContainSingle(decision =>
                decision.Disposition == MusicMaterializationDecisionDisposition.Review
                && decision.Reason == "complete_file_image_requires_review"
            );
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        await AssertSettled(source);
    }

    [Test]
    public async Task TwoUnplayableFilesRemainExplicitUnresolvedClaimsWithoutInventedTrackBindings()
    {
        var source = await Source(Title());
        var secondTrack = Guid.NewGuid();
        await Execute(
            """
            UPDATE source_tracks SET is_orphaned=true WHERE uuid=@TrackUuid;
            INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at,is_orphaned)
            SELECT source_id,source_set_id,artist_id,2,@title,@slug,'https://example.test/other.mp3',@secondTrack,now(),true
            FROM source_tracks WHERE uuid=@TrackUuid
            """,
            new
            {
                source.TrackUuid,
                secondTrack,
                title = Title(),
                slug = secondTrack.ToString("D"),
            }
        );
        var account = await Resolve(source);
        var uuid = Guid.NewGuid();

        var completed = await Drain(uuid, new(account.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        var decisions = await Decisions(uuid);
        decisions
            .Should()
            .HaveCount(2)
            .And.OnlyContain(decision =>
                decision.Source == null
                && decision.ContentUuid == null
                && decision.Disposition == MusicMaterializationDecisionDisposition.Unresolved
                && decision.Reason == "file_binding_unresolved"
            );
        decisions.Select(decision => decision.Key).Should().OnlyHaveUniqueItems();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        (await music.GetTrackContentsAsync(secondTrack)).Should().BeEmpty();
        await AssertSettled(source);
    }

    [Test]
    public async Task ExactReplayAndUnchangedSubscriberRetainContentUuidsAndRevisions()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var request = new RecordingContentMaterializationRequest(account.AttemptUuid);
        var uuid = Guid.NewGuid();
        var first = await Drain(uuid, request);
        var contents = await music.GetTrackContentsAsync(source.TrackUuid);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;

        var replay = await materializer.ProcessAsync(uuid, request);
        var next = await Drain(Guid.NewGuid(), request);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Should().BeEquivalentTo(first.Result! with { Replayed = true });
        next.Result!.Retained.Should().Be(1);
        next.Preparation!.Image!.Contents.Should().BeEmpty();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(contents);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(owner.EditorialRevision);
        await AssertSettled(source);
    }

    [Test]
    public async Task WrongWorkOrAttemptCannotPublishTheSavedImage()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var uuid = Guid.NewGuid();
        var admitted = await UntilTicket(uuid, new(account.AttemptUuid));

        await FluentActions
            .Invoking(() => music.PublishRecordingContentMaterializationAsync(Guid.NewGuid(), uuid))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        await FluentActions
            .Invoking(() =>
                music.PublishRecordingContentMaterializationAsync(
                    admitted.Ticket!.WorkUuid,
                    Guid.NewGuid()
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        var completed = await Drain(uuid, new(account.AttemptUuid));
        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        (await music.PublishRecordingContentMaterializationAsync(admitted.Ticket!.WorkUuid, uuid))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task ChangedRequestCannotResumeAnExistingPreparation()
    {
        var source = await Source(Title());
        var first = await Resolve(source);
        var other = await Resolve(await Source(Title()));
        var uuid = Guid.NewGuid();
        await materializer.ProcessAsync(uuid, new(first.AttemptUuid));

        await FluentActions
            .Invoking(() => materializer.ProcessAsync(uuid, new(other.AttemptUuid)))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task CancellationLeavesSavedAllocationsResumable()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var request = new RecordingContentMaterializationRequest(account.AttemptUuid);
        var uuid = Guid.NewGuid();
        var prepared = await materializer.ProcessAsync(uuid, request);
        var target = prepared.Preparation!.Image!.Contents.Single().Uuid;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions
            .Invoking(() => materializer.ProcessAsync(uuid, request, cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        (await Drain(uuid, request))
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed);
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.Uuid.Should()
            .Be(target);
    }

    [Test]
    public async Task ReceiptFailureRollsBackAcceptedRowsAndCorrespondenceAndResumeUsesSavedUuids()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var uuid = Guid.NewGuid();
        var request = new RecordingContentMaterializationRequest(account.AttemptUuid);
        var admitted = await UntilTicket(uuid, request);
        var target = admitted.Preparation!.Image!.Contents.Single().Uuid;
        var trigger = "recording_rollback_" + Guid.NewGuid().ToString("N");
        await Execute(
            $"""
            CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.attempt_uuid='{uuid}'::uuid AND NEW.receipt IS NOT NULL THEN RAISE EXCEPTION 'fixture rollback'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE OF receipt ON normalization.recording_content_materialization_attempts
            FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}()
            """
        );
        try
        {
            var failure = await FluentActions
                .Invoking(() => materializer.ProcessAsync(uuid, request))
                .Should()
                .ThrowAsync<Exception>();
            var databaseFailure = failure
                .Which.InnerException.Should()
                .BeOfType<Npgsql.PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be("P0001");
            databaseFailure.MessageText.Should().Be("fixture rollback");
            (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
            var count = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM normalization.recording_content_correspondences WHERE attempt_uuid=@uuid",
                    new { uuid }
                )
            );
            count.Should().Be(0);
            (await materializer.GetAsync(uuid))!.Receipt.Should().BeNull();
        }
        finally
        {
            await Execute(
                $"DROP TRIGGER {trigger} ON normalization.recording_content_materialization_attempts; DROP FUNCTION normalization.{trigger}()"
            );
        }

        (await Drain(uuid, request))
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed);
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.Uuid.Should()
            .Be(target);
    }

    [Test]
    public async Task OriginalHeadChangeMakesSavedImageStaleAndPreservesPlayback()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var uuid = Guid.NewGuid();
        var request = new RecordingContentMaterializationRequest(account.AttemptUuid);
        var admitted = await UntilTicket(uuid, request);
        var input = admitted.Preparation!.Context!.Consumption.InputReferences.First();
        var retained = (await evidence.GetInputAsync(input.InputRevisionUuid))!;
        var original = admitted.Preparation.Context.Consumption.ProducerReadSet.Inputs.Single(
            value => value.RevisionUuid == input.InputRevisionUuid
        );
        var before = await Playback(source);
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                retained.Resource,
                retained.Payload,
                retained.MediaType,
                retained.CaptureVersion,
                original.Generation,
                DateTimeOffset.UtcNow
            )
        );

        var stale = await Drain(uuid, request);

        stale.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Stale);
        stale.Receipt.Should().BeNull();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        (await Playback(source)).Should().Be(before);
    }

    [Test]
    public async Task ReviewedUnmanagedPositionBlocksTheWholeFileImage()
    {
        var source = await Source(Title() + " > " + Title());
        var account = await Resolve(source);
        var retained = await fixture.Capture("Protected file content", "songs");
        var owner = new NormalizationSubjectKey("songs", "recording", source.SourceUuid);
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            owner,
            "recording-fixture-review-v1",
            [],
            []
        );
        var state = (await fixture.Work.GetSubjectAsync(ticket.SubjectUuid))!;
        var uuid = Guid.NewGuid();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            state.SubjectUuid,
            state.EditorialRevision,
            "accept",
            new()
            {
                TrackContents =
                [
                    new()
                    {
                        Uuid = uuid,
                        SourceTrackUuid = source.TrackUuid,
                        Position = 1,
                        Kind = "unknown",
                        OriginalText = "Protected file content",
                        Evidence = [retained.Reference],
                    },
                ],
            },
            "Independent protected baseline content",
            Operator
        );
        var before = await music.GetTrackContentsAsync(source.TrackUuid);
        var attemptUuid = Guid.NewGuid();

        var completed = await Drain(attemptUuid, new(account.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        completed.Result.Review.Should().BeGreaterThan(0);
        completed.Preparation!.Image!.Contents.Should().BeEmpty();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(before);
        (await Decisions(attemptUuid))
            .Should()
            .OnlyContain(decision =>
                decision.Disposition == MusicMaterializationDecisionDisposition.Review
            );
    }

    [Test]
    public async Task RevisedUnkeyedSingleFileNeedsEvaluatedPolicyAndThenReusesTheAcceptedContent()
    {
        var source = await Source("Jam");
        var original = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(original.AttemptUuid));
        var contents = await music.GetTrackContentsAsync(source.TrackUuid);
        await Execute("UPDATE source_tracks SET duration=123 WHERE uuid=@TrackUuid", source);
        var revised = await Resolve(source);
        var negativeUuid = Guid.NewGuid();

        var negative = await Drain(negativeUuid, new(revised.AttemptUuid));

        negative.Result!.Review.Should().BeGreaterThan(0);
        negative.Preparation!.Image!.Contents.Should().BeEmpty();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(contents);
        await ActivatePolicy();
        (await IsPending(source))
            .Should()
            .BeTrue("the negative acknowledgment watches the policy prerequisite");
        var uuid = Guid.NewGuid();
        var completed = await Drain(uuid, new(revised.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        var mapped = (await Decisions(uuid)).Single(decision => decision.Source != null);
        mapped.ContentUuid.Should().Be(contents.Single().Value.Uuid);
        mapped.Evaluation!.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Value.Uuid.Should()
            .Be(contents.Single().Value.Uuid);
        first.Result!.Accepted.Should().Be(1);
    }

    [Test]
    public async Task AReviewedRejectionRemainsAuthorityWhenTheNextImageWouldOtherwiseBeANoOp()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var original = (await music.GetTrackContentsAsync(source.TrackUuid)).Single();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "reject",
            new()
            {
                TrackContents = [original.Value with { ExpectedRevision = original.Revision }],
            },
            "Reject this exact retained assertion without changing historical accepted metadata",
            Operator
        );

        var completed = await Drain(Guid.NewGuid(), new(account.AttemptUuid));

        completed.Result!.Review.Should().BeGreaterThan(0);
        completed.Preparation!.Image!.Contents.Should().BeEmpty();
        (await Decisions(completed.AttemptUuid))
            .Should()
            .Contain(decision => decision.Reason == "reviewed_content_exclusion");
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(original);
    }

    [Test]
    public async Task NarrowCorrespondenceBlockChangesAuthorityWithoutRewritingAcceptedMetadata()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var claim = (await Decisions(first.AttemptUuid)).Single();
        var before = (await music.GetTrackContentsAsync(source.TrackUuid)).Single();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;
        var authorizationUuid = Guid.NewGuid();
        var approval = new RecordingContentCorrespondenceApproval(
            authorizationUuid,
            claim.Source,
            [new(before.Value.Uuid, before.Revision)],
            SetlistAuthorizationAction.Block,
            false,
            null,
            claim.Evidence
        );
        var command = new RecordingContentCorrespondenceReview(
            Guid.NewGuid(),
            owner.SubjectUuid,
            source.SourceUuid,
            owner.EditorialRevision,
            [approval],
            "Block this exact historical correspondence"
        );

        await music.ReviewRecordingContentCorrespondenceAsync(command, Operator);
        var blocked = await Drain(Guid.NewGuid(), new(account.AttemptUuid));

        blocked.Result!.Review.Should().BeGreaterThan(0);
        blocked.Preparation!.Image!.Contents.Should().BeEmpty();
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(before);
        var authority = (
            await music.GetRecordingContentAuthorizationsAsync(source.SourceUuid)
        ).Single();
        authority.AuthorizationUuid.Should().Be(authorizationUuid);
        authority.OperationUuid.Should().Be(command.OperationUuid);
        authority.ProcessorDecisionUuid.Should().BeNull();
        authority.Active.Should().BeTrue();
        (await music.ReviewRecordingContentCorrespondenceAsync(command, Operator))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task InitialRequestWithNewerUnpublishedAcknowledgmentKeepsADurablePendingHeader()
    {
        var source = await Source(Title());
        var account = await Resolve(source);
        var acknowledgment = (
            await new NormalizationRepertoireResolutionStore(fixture.Db).GetStageAsync(
                account.AttemptUuid,
                account
                    .Stages.Single(stage =>
                        stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource
                    )
                    .StageUuid
            )
        )!;
        var ticket = acknowledgment.Ticket!;
        var extra = await fixture.Capture("New acknowledgment configuration", "songs");
        // A separate contributor advances the acknowledgment without replacing the resolver's
        // existing inputs. Preserve its original clocks while admitting this unpublished work.
        var admitted = await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            new("songs", "repertoire_observation", account.Registration.RegistrationUuid),
            ticket.PolicyVersion,
            new(
                extra.Resource,
                0,
                [extra.Resource],
                ticket.Dependencies.Select(dependency => dependency.SubjectUuid).ToArray()
            ),
            ticket.DesiredGeneration,
            catalogInputs: ticket.CatalogInputs,
            sourceIdentities: ticket.SourceIdentities,
            mappingRegistry: ticket.MappingRegistry,
            expectedInputRevisions: [new(extra.Reference.InputRevisionUuid, extra.Resource)],
            importedVenues: ticket.ImportedVenues,
            candidateUniverses: ticket.CandidateUniverses,
            expectedOwnerObservations: ticket.OwnerObservations,
            sourceUuids: ticket.SourceUuids
        );
        admitted.DesiredGeneration.Should().BeGreaterThan(ticket.DesiredGeneration);
        admitted.PolicyVersion.Should().Be(ticket.PolicyVersion);

        var uuid = Guid.NewGuid();

        var pending = await materializer.ProcessAsync(uuid, new(account.AttemptUuid));

        pending.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Pending);
        pending.Result.Reason.Should().Be("awaiting_repertoire_resolution");
        pending.Preparation.Should().BeNull();
        (await materializer.GetAsync(uuid))!
            .Request!.RepertoireResolutionAttemptUuid.Should()
            .Be(account.AttemptUuid);
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task RetryHeaderWaitsForNewCompletedRepertoireThenPinsItAndReplays()
    {
        var source = await Source(Title());
        var original = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(original.AttemptUuid));
        await PublishRecording(source);
        var retryUuid = Guid.NewGuid();

        var waiting = await materializer.RetryAsync(retryUuid, first.Registration.RegistrationUuid);

        waiting.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Pending);
        waiting.Result.Reason.Should().Be("awaiting_repertoire_resolution");
        waiting.Request.Should().BeNull();
        waiting.SelectedRepertoireAttemptUuid.Should().BeNull();
        var currentBatch = (
            await new NormalizationRecordingProposalService(fixture.Db).GetAsync(source.SourceUuid)
        )!;
        var resolved = await ResolveBatch(currentBatch.AttemptUuid);
        var selected = await materializer.RetryAsync(
            retryUuid,
            first.Registration.RegistrationUuid
        );
        selected.SelectedRepertoireAttemptUuid.Should().Be(resolved.AttemptUuid);
        await FluentActions
            .Invoking(() => materializer.ProcessAsync(retryUuid, new(resolved.AttemptUuid)))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        var completed = await DrainRetry(retryUuid, first.Registration.RegistrationUuid);
        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        (await materializer.RetryAsync(retryUuid, first.Registration.RegistrationUuid))
            .Result!.Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task FiftyOneContentsUseCompleteImagesAndExistingPublicCursors()
    {
        var source = await Source(string.Join(" > ", Enumerable.Range(0, 51).Select(_ => Title())));
        var account = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        first.Preparation!.Image!.Contents.Should().HaveCount(51);
        var page = await music.GetTrackContentsAsync(source.TrackUuid);
        page.Should().HaveCount(50);
        var last = page[^1].Value;
        var tail = await music.GetTrackContentsAsync(source.TrackUuid, last.Position, last.Uuid);
        tail.Should().ContainSingle();
        var expected = page.Concat(tail).ToArray();

        var unchanged = await Drain(Guid.NewGuid(), new(account.AttemptUuid));

        unchanged.Result!.Retained.Should().Be(51);
        unchanged.Preparation!.Image!.Contents.Should().BeEmpty();
        (await Decisions(unchanged.AttemptUuid)).Should().HaveCount(51);
        var all = await music.GetTrackContentsAsync(source.TrackUuid, limit: 100);
        all.Should().BeEquivalentTo(expected);
    }

    [Test]
    public async Task NewExtractionOfTheSameImmutableTitleReusesExactContentWithoutAnInferredPolicy()
    {
        var source = await Source(Title());
        var firstAccount = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(firstAccount.AttemptUuid));
        var original = (await music.GetTrackContentsAsync(source.TrackUuid)).Single();
        var secondAccount = await Resolve(source);
        secondAccount.AttemptUuid.Should().NotBe(firstAccount.AttemptUuid);
        secondAccount
            .Preparation!.Source.ReadSet.Inputs.Select(input => input.RevisionUuid)
            .Should()
            .BeEquivalentTo(
                firstAccount.Preparation!.Source.ReadSet.Inputs.Select(input => input.RevisionUuid)
            );

        var current = await Drain(Guid.NewGuid(), new(secondAccount.AttemptUuid));

        current.Result!.Retained.Should().Be(1);
        current.Result.Review.Should().Be(0);
        current.Preparation!.Image!.Contents.Should().BeEmpty();
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Should()
            .BeEquivalentTo(original);
        var decision = (await Decisions(current.AttemptUuid)).Single();
        decision
            .Source!.ExtractionAttemptUuid.Should()
            .NotBe((await Decisions(first.AttemptUuid)).Single().Source!.ExtractionAttemptUuid);
        decision.Evaluation.Should().BeNull();
    }

    [Test]
    public async Task ThreeSourceGenerationsUseTheLatestExactCorrespondenceAfterANoOp()
    {
        var source = await Source("Jam");
        await ActivatePolicy();
        var firstAccount = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(firstAccount.AttemptUuid));
        var original = (await music.GetTrackContentsAsync(source.TrackUuid)).Single();
        var secondAccount = await Resolve(source);
        var second = await Drain(Guid.NewGuid(), new(secondAccount.AttemptUuid));
        second.Result!.Retained.Should().Be(1);
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Revision.Should()
            .Be(original.Revision);
        await Execute(
            "UPDATE source_tracks SET duration=456,title='jam' WHERE uuid=@TrackUuid",
            source
        );
        var thirdAccount = await Resolve(source);

        var third = await Drain(Guid.NewGuid(), new(thirdAccount.AttemptUuid));

        third.Result!.Review.Should().Be(0);
        var decision = (await Decisions(third.AttemptUuid)).Single();
        decision.ContentUuid.Should().Be(original.Value.Uuid);
        decision.Evaluation!.Disposition.Should().Be(MusicUsageDecisionDisposition.Authorized);
        decision
            .Evaluation.PreviousClaim!.ProcessingAttemptUuid.Should()
            .Be((await Decisions(second.AttemptUuid)).Single().Source!.ExtractionAttemptUuid);
        (await music.GetTrackContentsAsync(source.TrackUuid))
            .Single()
            .Revision.Should()
            .Be(original.Revision + 1);
        first.Result!.Accepted.Should().Be(1);
    }

    [Test]
    public async Task ExplicitWithdrawalPreservesHistoricalBindingAndCannotAutomaticallyReactivate()
    {
        var title = Title();
        var source = await Source(title);
        var account = await Resolve(source);
        await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var original = (await music.GetTrackContentsAsync(source.TrackUuid)).Single();
        await Execute("UPDATE source_tracks SET title='' WHERE uuid=@TrackUuid", source);
        var removed = await Resolve(source);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;
        var authorizationUuid = Guid.NewGuid();
        await music.ReviewRecordingContentCorrespondenceAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                source.SourceUuid,
                owner.EditorialRevision,
                [
                    new(
                        authorizationUuid,
                        null,
                        [new(original.Value.Uuid, original.Revision)],
                        SetlistAuthorizationAction.Authorize,
                        true,
                        null,
                        original.Value.Evidence
                    ),
                ],
                "Approve removal against the historical accepted file claim"
            ),
            Operator
        );

        var completed = await Drain(Guid.NewGuid(), new(removed.AttemptUuid));

        (await Decisions(completed.AttemptUuid))
            .Should()
            .Contain(decision =>
                decision.Withdrawal
                && decision.ContentUuid == original.Value.Uuid
                && decision.Disposition == MusicMaterializationDecisionDisposition.Withdrawn
            );
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        var tombstone = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT r.payload::text FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@Uuid",
                original.Value
            )
        );
        var value = JsonSerializer.Deserialize<TrackContentChange>(tombstone)!;
        value.Retracted.Should().BeTrue();
        value.RepertoireSongUuid.Should().Be(original.Value.RepertoireSongUuid);
        var consumed = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.recording_content_removal_authorizations_used WHERE authorization_uuid=@authorizationUuid",
                new { authorizationUuid }
            )
        );
        consumed.Should().Be(1);
        await Execute(
            "UPDATE source_tracks SET title=@title WHERE uuid=@TrackUuid",
            new { title, source.TrackUuid }
        );
        var reappeared = await Resolve(source);
        var blocked = await Drain(Guid.NewGuid(), new(reappeared.AttemptUuid));
        blocked.Result!.Review.Should().BeGreaterThan(0);
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task ChangedMedleyCannotAutomaticallyWithdrawOrRealignEvenWithAnEvaluatedWithdrawalRule()
    {
        var source = await Source("Jam > Banter > Applause");
        await ActivatePolicy(includeWithdrawal: true);
        var account = await Resolve(source);
        await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var original = await music.GetTrackContentsAsync(source.TrackUuid);
        original.Should().HaveCount(3);
        await Execute(
            "UPDATE source_tracks SET title='Jam > Applause' WHERE uuid=@TrackUuid",
            source
        );
        var revised = await Resolve(source);

        var blocked = await Drain(Guid.NewGuid(), new(revised.AttemptUuid));

        blocked.Result!.Review.Should().BeGreaterThan(0);
        blocked.Preparation!.Image!.Contents.Should().BeEmpty();
        blocked.Preparation.Image.Correspondence.Should().BeEmpty();
        blocked.Preparation.Image.Withdrawals.Should().BeEmpty();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(original);
        (await Decisions(blocked.AttemptUuid))
            .Should()
            .Contain(decision =>
                decision.Withdrawal && decision.Reason == "withdrawal_authority_required"
            );
    }

    [Test]
    public async Task ExactReviewedMiddleRemovalAndSurvivorMovePublishOneCompleteFileImage()
    {
        var source = await Source("Jam > Banter > Applause");
        var account = await Resolve(source);
        await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var original = (await music.GetTrackContentsAsync(source.TrackUuid))
            .OrderBy(row => row.Value.Position)
            .ToArray();
        original.Select(row => row.Value.Kind).Should().Equal("jam", "speech", "nonmusic");
        await Execute(
            "UPDATE source_tracks SET title='Jam > Applause' WHERE uuid=@TrackUuid",
            source
        );
        var revised = await Resolve(source);
        var negative = await Drain(Guid.NewGuid(), new(revised.AttemptUuid));
        var claims = (await Decisions(negative.AttemptUuid))
            .Where(decision => decision.Source != null)
            .OrderBy(decision => decision.Source!.SegmentPosition)
            .ToArray();
        claims.Should().HaveCount(2);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;
        var removalUuid = Guid.NewGuid();
        await music.ReviewRecordingContentCorrespondenceAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                source.SourceUuid,
                owner.EditorialRevision,
                [
                    new(
                        Guid.NewGuid(),
                        claims[0].Source,
                        [new(original[0].Value.Uuid, original[0].Revision)],
                        SetlistAuthorizationAction.Authorize,
                        false,
                        null,
                        claims[0].Evidence
                    ),
                    new(
                        Guid.NewGuid(),
                        claims[1].Source,
                        [new(original[2].Value.Uuid, original[2].Revision)],
                        SetlistAuthorizationAction.Authorize,
                        false,
                        null,
                        claims[1].Evidence
                    ),
                    new(
                        removalUuid,
                        null,
                        [new(original[1].Value.Uuid, original[1].Revision)],
                        SetlistAuthorizationAction.Authorize,
                        true,
                        null,
                        original[1].Value.Evidence
                    ),
                ],
                "Approve these exact survivors and removal as one revised complete file"
            ),
            Operator
        );

        var completed = await Drain(Guid.NewGuid(), new(revised.AttemptUuid));

        completed.Result!.Review.Should().Be(0);
        var current = (await music.GetTrackContentsAsync(source.TrackUuid))
            .OrderBy(row => row.Value.Position)
            .ToArray();
        current
            .Select(row => row.Value.Uuid)
            .Should()
            .Equal(original[0].Value.Uuid, original[2].Value.Uuid);
        current.Select(row => row.Value.Position).Should().Equal(1, 2);
        completed
            .Preparation!.Image!.Withdrawals.Should()
            .ContainSingle()
            .Which.ContentUuid.Should()
            .Be(original[1].Value.Uuid);
        var consumed = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM normalization.recording_content_removal_authorizations_used WHERE authorization_uuid=@removalUuid",
                new { removalUuid }
            )
        );
        consumed.Should().Be(1);
        var replay = await materializer.ProcessAsync(
            completed.AttemptUuid,
            new(revised.AttemptUuid)
        );
        replay.Result!.Replayed.Should().BeTrue();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(current);
    }

    [Test]
    public async Task CurrentRecordingMayAdoptTheSameFileContentOnlyThroughExactManualCorrespondence()
    {
        var originalSource = await Source("Jam");
        var originalAccount = await Resolve(originalSource);
        await Drain(Guid.NewGuid(), new(originalAccount.AttemptUuid));
        var original = (await music.GetTrackContentsAsync(originalSource.TrackUuid)).Single();
        var destination = await Source("Speech");
        await Execute(
            """
            UPDATE sources SET artist_id=@ArtistId WHERE id=@destinationId;
            UPDATE source_tracks SET artist_id=@ArtistId WHERE source_id=@destinationId;
            UPDATE source_tracks SET source_id=@destinationId,source_set_id=(SELECT source_set_id FROM source_tracks WHERE uuid=@destinationTrack)
            WHERE uuid=@TrackUuid
            """,
            new
            {
                originalSource.ArtistId,
                destinationId = destination.SourceId,
                destinationTrack = destination.TrackUuid,
                originalSource.TrackUuid,
            }
        );
        var account = await Resolve(destination);
        var negative = await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        negative.Result!.Review.Should().BeGreaterThan(0);
        var sourceClaim = (await Decisions(negative.AttemptUuid))
            .Single(decision => decision.Source?.TrackUuid == originalSource.TrackUuid)
            .Source!;
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", destination.SourceUuid)
            )
        )!;
        await music.ReviewRecordingContentCorrespondenceAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                destination.SourceUuid,
                owner.EditorialRevision,
                [
                    new(
                        Guid.NewGuid(),
                        sourceClaim,
                        [new(original.Value.Uuid, original.Revision)],
                        SetlistAuthorizationAction.Authorize,
                        false,
                        null,
                        [sourceClaim.FileOrigin.Evidence]
                    ),
                ],
                "Adopt this exact unchanged file under its current recording"
            ),
            Operator
        );

        var adopted = await Drain(Guid.NewGuid(), new(account.AttemptUuid));

        adopted.Result!.Review.Should().Be(0);
        (await music.GetTrackContentsAsync(originalSource.TrackUuid))
            .Single()
            .Value.Uuid.Should()
            .Be(original.Value.Uuid);
        var storedSource = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>(
                "SELECT source_uuid FROM normalization.track_contents WHERE uuid=@Uuid",
                original.Value
            )
        );
        storedSource.Should().Be(destination.SourceUuid);
        var stale = await Drain(Guid.NewGuid(), new(originalAccount.AttemptUuid));
        stale.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Stale);
    }

    [Test]
    public async Task NarrowReviewCannotMapAClaimToAnotherFileInTheSameRecording()
    {
        var source = await Source("Jam");
        var otherTrack = Guid.NewGuid();
        await Execute(
            """
            INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
            SELECT source_id,source_set_id,artist_id,2,'Speech',@slug,'https://example.test/second.mp3',@otherTrack,now()
            FROM source_tracks WHERE uuid=@TrackUuid
            """,
            new
            {
                source.TrackUuid,
                otherTrack,
                slug = otherTrack.ToString("D"),
            }
        );
        var account = await Resolve(source);
        var first = await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var claim = (await Decisions(first.AttemptUuid)).Single(decision =>
            decision.Source?.TrackUuid == source.TrackUuid
        );
        var target = (await music.GetTrackContentsAsync(otherTrack)).Single();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;
        var command = new RecordingContentCorrespondenceReview(
            Guid.NewGuid(),
            owner.SubjectUuid,
            source.SourceUuid,
            owner.EditorialRevision,
            [
                new(
                    Guid.NewGuid(),
                    claim.Source,
                    [new(target.Value.Uuid, target.Revision)],
                    SetlistAuthorizationAction.Authorize,
                    false,
                    null,
                    claim.Evidence
                ),
            ],
            "Deliberately mismatched file coordinate"
        );

        await FluentActions
            .Invoking(() => music.ReviewRecordingContentCorrespondenceAsync(command, Operator))
            .Should()
            .ThrowAsync<NormalizationConflictException>();

        (await music.GetRecordingContentAuthorizationsAsync(source.SourceUuid)).Should().BeEmpty();
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(owner.EditorialRevision);
        (await music.GetTrackContentsAsync(otherTrack))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(target);
    }

    [Test]
    public async Task SegmentCoverageAndTransitionRetainAbsoluteQualifierEvidence()
    {
        var title = "🎵" + Title();
        var raw = "…" + title + " (partial) > Jam";
        var source = await Source(raw);
        var account = await Resolve(source);

        var completed = await Drain(Guid.NewGuid(), new(account.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        var first = (await music.GetTrackContentsAsync(source.TrackUuid)).First().Value;
        first.OriginalText.Should().Be(title);
        first.TitleFacts!.Coverage.Should().Be(TitleCoverage.PartialMarker);
        first.TitleFacts.TransitionAfter.Should().Be(TitleTransition.SegueMarker);
        first
            .TitleFacts.Qualifiers.Select(qualifier => qualifier.Kind)
            .Should()
            .BeEquivalentTo("partial_marker", "partial_coverage");
        first
            .TitleFacts.Qualifiers.Should()
            .OnlyContain(qualifier =>
                qualifier.Evidence.Quote != null
                && qualifier.Evidence.InputRevisionUuid == first.Evidence.Single().InputRevisionUuid
                && qualifier.Evidence.JsonPointer == first.Evidence.Single().JsonPointer
            );
        first.TitleFacts.SourceEvidence!.Quote.Should().BeNull();
        var reads = new NormalizationReadService(
            fixture.Db,
            Options.Create(new NormalizationReadOptions { Enabled = true })
        );
        var projected = (await reads.ListTrackContentsAsync(source.TrackUuid))!.items.First();
        projected.title_facts_status.Should().Be("available");
        projected.title_facts!.coverage.Should().Be("partial_marker");
        projected.title_facts.transition_after.Should().Be("segue_marker");
        projected.title_facts.source_evidence!.quote.Should().BeNull();
        foreach (var qualifier in projected.title_facts.qualifiers)
        {
            var citation = qualifier.evidence;
            var quote = string.Concat(
                raw.EnumerateRunes()
                    .Skip(citation.start_code_point!.Value)
                    .Take(citation.end_code_point!.Value - citation.start_code_point.Value)
                    .Select(rune => rune.ToString())
            );
            citation.quote.Should().Be(quote);
            citation.evidence_status.Should().Be("current");
        }
        projected
            .title_facts.qualifiers.Single(qualifier => qualifier.kind == "partial_marker")
            .evidence.start_code_point.Should()
            .Be(0);
    }

    [Test]
    public void LegacyContentAndPublicDtoKeepOptionalTitleFactsOutOfExistingJsonShapes()
    {
        var change = new TrackContentChange
        {
            Uuid = Guid.NewGuid(),
            SourceTrackUuid = Guid.NewGuid(),
            Position = 1,
            Kind = "jam",
            OriginalText = "Jam",
        };
        var serialized = JsonSerializer.Serialize(change);
        using var json = JsonDocument.Parse(serialized);
        json.RootElement.TryGetProperty("TitleFacts", out _).Should().BeFalse();
        var publication = JsonSerializer.Serialize(
            new MusicPublication { TrackContents = [change] }
        );
        publication.Should().NotContain("TitleFacts");
        var dto = new NormalizedTrackContent();
        JsonSerializer.Serialize(dto).Should().NotContain("title_facts");
        Newtonsoft.Json.JsonConvert.SerializeObject(dto).Should().NotContain("title_facts");
    }

    [TestCase("Jam (partial)", "Jam", false)]
    [TestCase("Jam (partial)", "Jam", true)]
    [TestCase("Jam > Banter", "Jam / Banter", false)]
    [TestCase("Jam > Banter", "Jam / Banter", true)]
    public async Task ConflictingTitleFactsFromRealInputGroupsPreserveThePriorImageInEitherOrder(
        string left,
        string right,
        bool reverse
    )
    {
        var source = await Source(reverse ? right : left);
        var account = await Resolve(source);
        await Drain(Guid.NewGuid(), new(account.AttemptUuid));
        var original = await music.GetTrackContentsAsync(source.TrackUuid);
        var batch = await PublishArchiveAndLegacy(source, reverse ? left : right);
        var revised = await ResolveBatch(batch);

        var blocked = await Drain(Guid.NewGuid(), new(revised.AttemptUuid));

        blocked.Result!.Review.Should().BeGreaterThan(0);
        blocked.Preparation!.Image!.Contents.Should().BeEmpty();
        blocked.Preparation.Image.Correspondence.Should().BeEmpty();
        blocked.Preparation.Image.Withdrawals.Should().BeEmpty();
        (await Decisions(blocked.AttemptUuid))
            .Where(decision => decision.Source != null)
            .Should()
            .HaveCount(original.Count * 2)
            .And.OnlyContain(decision =>
                decision.Disposition == MusicMaterializationDecisionDisposition.Review
                && decision.Reason == "conflicting_file_images"
            );
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(original);
    }

    [TestCase(1, true)]
    [TestCase(5001, false)]
    public async Task EveryInputGroupSurvivesConflictOrMoreThanTenThousandAgreeingCorrespondences(
        int fileCount,
        bool conflict
    )
    {
        var source = await Source("Jam");
        var key = "materializer-" + Guid.NewGuid().ToString("N");
        var names = Enumerable
            .Range(0, fileCount)
            .Select(index => "track" + index + ".mp3")
            .ToArray();
        var extraTracks = Enumerable.Range(1, fileCount - 1).Select(_ => Guid.NewGuid()).ToArray();
        var archiveProvider = await EnsureArchiveProvider();
        await Execute(
            """
            UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId;
            UPDATE source_tracks SET mp3_url='https://archive.org/download/'||@key||'/'||@firstName WHERE uuid=@TrackUuid;
            INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
            VALUES(@SourceId,@archiveProvider,true,false,false,'https://archive.org/'||@key,'Provider');
            INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at)
            SELECT original.source_id,original.source_set_id,original.artist_id,extra.ord+1,'Jam',extra.uuid::text,
                'https://archive.org/download/'||@key||'/'||extra.name,extra.uuid,now()
            FROM source_tracks original CROSS JOIN unnest(@extraTracks::uuid[],@extraNames::text[]) WITH ORDINALITY extra(uuid,name,ord)
            WHERE original.uuid=@TrackUuid
            """,
            new
            {
                source.SourceId,
                source.TrackUuid,
                archiveProvider,
                key,
                firstName = names[0],
                extraTracks,
                extraNames = names.Skip(1).ToArray(),
            }
        );
        var legacy = await PublishRecording(source);
        IReadOnlyList<MusicRecord<TrackContentChange>>? before = null;
        if (conflict)
        {
            var account = await ResolveBatch(legacy);
            await Drain(Guid.NewGuid(), new(account.AttemptUuid));
            before = await music.GetTrackContentsAsync(source.TrackUuid);
        }
        var resource = new NormalizationResourceKey("archive.org", "item_metadata", key);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new { identifier = key },
                        files = names
                            .Select(name => new
                            {
                                name,
                                title = conflict ? "Speech" : "Jam",
                                format = "VBR MP3",
                            })
                            .ToArray(),
                    }
                ),
                "application/json",
                "archive-item-response-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var lookupStore = new NormalizationRecordingLookupStore(fixture.Db);
        var processor = new NormalizationRecordingProcessor(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            new(fixture.Db),
            lookupStore
        );
        var lookups = new NormalizationRecordingLookupService(
            lookupStore,
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            fixture.Work
        );
        var lookup = await lookups.LookupRetainedAsync(
            Guid.NewGuid(),
            new(captured.Revision.Uuid, resource)
        );
        lookup.Result!.Disposition.Should().Be(NormalizationRecordingLookupDisposition.Published);
        lookup.Result.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        lookup.Result.HandoffUuid.Should().NotBeNull();
        var delivery = await new NormalizationRecordingLookupDispatcher(
            lookupStore,
            processor
        ).DispatchAsync(lookup.Result!.HandoffUuid!.Value);
        delivery
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (
            await new NormalizationRecordingProposalService(fixture.Db).GetAsync(source.SourceUuid)
        )!;
        batch.Extraction.Titles.Should().HaveCount(fileCount);
        batch
            .AdditionalExtractions.Should()
            .ContainSingle()
            .Which.Extraction.Titles.Should()
            .HaveCount(fileCount);
        var completedAccount = await ResolveBatch(batch.AttemptUuid, maximumPhases: 500);

        var completed = await Drain(Guid.NewGuid(), new(completedAccount.AttemptUuid));

        completed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Completed);
        if (conflict)
        {
            completed.Preparation!.Image!.Contents.Should().BeEmpty();
            (await Decisions(completed.AttemptUuid))
                .Where(decision => decision.Source != null)
                .Should()
                .HaveCount(2)
                .And.OnlyContain(decision =>
                    decision.Disposition == MusicMaterializationDecisionDisposition.Review
                );
            (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEquivalentTo(before!);
        }
        else
        {
            completed.Preparation!.Image!.Contents.Should().HaveCount(fileCount);
            completed.Preparation.Image.Correspondence.Should().HaveCount(10002);
            var unchanged = await Drain(Guid.NewGuid(), new(completedAccount.AttemptUuid));
            unchanged.Result!.Retained.Should().Be(10002);
            unchanged.Preparation!.Image!.Contents.Should().BeEmpty();
            unchanged.Preparation.Image.Correspondence.Should().HaveCount(10002);
            var count = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM normalization.track_contents WHERE source_uuid=@SourceUuid AND NOT retracted",
                    source
                )
            );
            count.Should().Be(fileCount);
            await AssertSettled(source);
        }
    }

    [Test]
    public async Task CompleteContentOverflowIsPermanentAndCannotEnrollAnUnchangedRetry()
    {
        var source = await Source(string.Join(" > ", Enumerable.Repeat("Jam", 10001)));
        var account = await ResolveBatch(await PublishRecording(source), maximumPhases: 500);
        var uuid = Guid.NewGuid();

        var rejected = await materializer.ProcessAsync(uuid, new(account.AttemptUuid));

        rejected.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Stale);
        rejected.Result.Reason.Should().Be("materialization_capacity_exceeded");
        rejected
            .Result.RetryRequirement!.RepertoireResolutionAttemptUuid.Should()
            .Be(account.AttemptUuid);
        rejected.Ticket.Should().BeNull();
        rejected.Preparation.Should().BeNull();
        (await music.GetTrackContentsAsync(source.TrackUuid)).Should().BeEmpty();
        var retryUuid = Guid.NewGuid();
        await FluentActions
            .Invoking(() =>
                materializer.RetryAsync(retryUuid, rejected.Registration.RegistrationUuid)
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        (await materializer.GetAsync(retryUuid)).Should().BeNull();
        Guid? cursor = null;
        do
        {
            var page = await materializer.GetRecoveryAttemptsAsync(200, cursor);
            page.Attempts.Should().NotContain(attempt => attempt.AttemptUuid == uuid);
            cursor = page.NextCursor;
        } while (cursor != null);
    }

    private async Task<CatalogSource> Source(string? title)
    {
        var source = await fixture.Source();
        await Execute(
            "UPDATE source_tracks SET title=@title,mp3_url=@url WHERE uuid=@TrackUuid",
            new
            {
                title = title ?? "",
                url = $"https://example.test/{source.TrackUuid:D}.mp3",
                source.TrackUuid,
            }
        );
        return source;
    }

    private async Task<Guid> PublishArchiveAndLegacy(CatalogSource source, string providerTitle)
    {
        var key = "materializer-facts-" + Guid.NewGuid().ToString("N");
        const string name = "track.mp3";
        var archiveProvider = await EnsureArchiveProvider();
        await Execute(
            """
            UPDATE sources SET upstream_identifier=@key WHERE id=@SourceId;
            UPDATE source_tracks SET mp3_url='https://archive.org/download/'||@key||'/'||@name WHERE uuid=@TrackUuid;
            INSERT INTO links(source_id,upstream_source_id,for_source,for_reviews,for_ratings,url,label)
            VALUES(@SourceId,@archiveProvider,true,false,false,'https://archive.org/'||@key,'Provider')
            """,
            new
            {
                source.SourceId,
                source.TrackUuid,
                archiveProvider,
                key,
                name,
            }
        );
        await PublishRecording(source);
        var resource = new NormalizationResourceKey("archive.org", "item_metadata", key);
        var captured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        metadata = new { identifier = key },
                        files = new[]
                        {
                            new
                            {
                                name,
                                title = providerTitle,
                                format = "VBR MP3",
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
        var store = new NormalizationRecordingLookupStore(fixture.Db);
        var processor = new NormalizationRecordingProcessor(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            new(fixture.Db),
            store
        );
        var lookups = new NormalizationRecordingLookupService(
            store,
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            fixture.Work
        );
        var lookup = await lookups.LookupRetainedAsync(
            Guid.NewGuid(),
            new(captured.Revision.Uuid, resource)
        );
        lookup.Result!.Disposition.Should().Be(NormalizationRecordingLookupDisposition.Published);
        lookup.Result.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        lookup.Result.HandoffUuid.Should().NotBeNull();
        var delivered = await new NormalizationRecordingLookupDispatcher(
            store,
            processor
        ).DispatchAsync(lookup.Result!.HandoffUuid!.Value);
        delivered
            .ProcessingResult.Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (
            await new NormalizationRecordingProposalService(fixture.Db).GetAsync(source.SourceUuid)
        )!;
        batch.AdditionalExtractions.Should().ContainSingle();
        return batch.AttemptUuid;
    }

    private Task<int> EnsureArchiveProvider() =>
        fixture.Db.WithWriteConnection(async c =>
            await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name='archive.org' ORDER BY id LIMIT 1"
            )
            ?? await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) SELECT COALESCE(max(id),0)+1,'archive.org','https://archive.org' FROM upstream_sources RETURNING id"
            )
        );

    private async Task<Guid> PublishRecording(CatalogSource source)
    {
        var processor = new NormalizationRecordingProcessor(
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
        (await processor.ProcessAsync(uuid, source.SourceUuid))
            .Disposition.Should()
            .Be(NormalizationRecordingProcessingDisposition.Published);
        return uuid;
    }

    private async Task<RepertoireResolutionAttempt> Resolve(CatalogSource source) =>
        await ResolveBatch(await PublishRecording(source));

    private async Task<RepertoireResolutionAttempt> ResolveBatch(
        Guid extractionUuid,
        int maximumPhases = 30
    )
    {
        var imported = new NormalizationImportedCatalogService(fixture.Db, evidence);
        var source = new NormalizationRepertoireSourceService(
            fixture.Db,
            evidence,
            new(),
            imported,
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db)
        );
        var resolver = new NormalizationRepertoireResolutionProcessor(
            fixture.Db,
            source,
            new(fixture.Db),
            new(fixture.Db, fixture.Work),
            new(fixture.Db),
            fixture.Work,
            music
        );
        var uuid = Guid.NewGuid();
        var request = new RepertoireResolutionRequest(
            new RecordingRepertoireResolutionSource(extractionUuid)
        );
        for (var phase = 0; phase < maximumPhases; phase++)
        {
            var attempt = await resolver.ResolveAsync(uuid, request);
            if (attempt.Result is { Disposition: not RepertoireResolutionDisposition.Pending })
            {
                attempt.Result.Disposition.Should().Be(RepertoireResolutionDisposition.Completed);
                return attempt;
            }
        }
        throw new AssertionException(
            "The public repertoire resolver did not settle within thirty durable phases."
        );
    }

    private async Task<RecordingContentMaterializationAttempt> Drain(
        Guid uuid,
        RecordingContentMaterializationRequest request
    )
    {
        for (var phase = 0; phase < 12; phase++)
        {
            var attempt = await materializer.ProcessAsync(uuid, request);
            if (attempt.Result is { Disposition: not MusicMaterializationDisposition.Pending })
            {
                return attempt;
            }
        }
        throw new AssertionException(
            "The public recording materializer did not settle within twelve durable phases."
        );
    }

    private async Task<RecordingContentMaterializationAttempt> DrainRetry(
        Guid uuid,
        Guid registrationUuid
    )
    {
        for (var phase = 0; phase < 12; phase++)
        {
            var attempt = await materializer.RetryAsync(uuid, registrationUuid);
            if (attempt.Result is { Disposition: not MusicMaterializationDisposition.Pending })
            {
                return attempt;
            }
        }
        throw new AssertionException(
            "The recording retry did not settle within twelve durable phases."
        );
    }

    private async Task<RecordingContentMaterializationAttempt> UntilTicket(
        Guid uuid,
        RecordingContentMaterializationRequest request
    )
    {
        for (var phase = 0; phase < 8; phase++)
        {
            var attempt = await materializer.ProcessAsync(uuid, request);
            if (attempt.Ticket != null && attempt.Receipt == null)
            {
                return attempt;
            }
        }
        throw new AssertionException("No durable recording content admission was retained.");
    }

    private async Task<IReadOnlyList<RecordingContentMaterializationDecision>> Decisions(
        Guid attemptUuid
    )
    {
        var decisions = new List<RecordingContentMaterializationDecision>();
        Guid? cursor = null;
        do
        {
            var page = await materializer.GetDecisionsAsync(attemptUuid, 100, cursor);
            decisions.AddRange(page.Decisions);
            cursor = page.NextCursor;
        } while (cursor != null);
        return decisions;
    }

    private async Task ActivatePolicy(bool includeWithdrawal = false)
    {
        var artifact = new MusicMaterializationPolicyArtifact(
            Guid.NewGuid(),
            "fixture-recording-v1",
            "",
            NormalizationSetlistMaterializer.Version,
            NormalizationRecordingContentMaterializer.Version,
            RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
            RepertoireConsumerCorrespondenceEvaluator.Version,
            includeWithdrawal
                ?
                [
                    MusicMaterializationRule.CheckedSingleFileClaimContinuity,
                    MusicMaterializationRule.BracketedWithdrawal,
                ]
                : [MusicMaterializationRule.CheckedSingleFileClaimContinuity],
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = RepertoireConsumerPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new MusicMaterializationEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "independent-recording-labels-v1",
            "independent-fixture-evaluator",
            Operator,
            new(20, 0),
            new(20, 0),
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = RepertoireConsumerPolicyService.ComputeEvaluationHash(evaluation),
        };
        await policies.RegisterEvaluatedPolicyAsync(artifact, evaluation, Operator);
        var state = await policies.ReadStateAsync();
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.OwnerObservation.SubjectUuid,
                state.OwnerObservation.EditorialRevision,
                state.Active?.Policy.Artifact.Uuid,
                artifact.Uuid,
                "Evaluated isolated single-file continuity fixture"
            ),
            Operator
        );
    }

    private async Task AssertSettled(CatalogSource source) =>
        (await IsPending(source)).Should().BeFalse();

    private async Task<bool> IsPending(CatalogSource source)
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "recording", source.SourceUuid)
            )
        )!;
        Guid? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var pending = await fixture.Work.GetPendingSubjectsAsync(cursor, 1000);
            if (pending.Any(row => row.SubjectUuid == owner.SubjectUuid))
            {
                return true;
            }
            if (pending.Count < 1000)
            {
                return false;
            }
            cursor = pending[^1].SubjectUuid;
        }
        throw new AssertionException("The pending recording owner scan did not finish.");
    }

    private Task<string> Playback(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT jsonb_build_object('source',to_jsonb(s),'tracks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.uuid) FROM source_tracks t WHERE t.source_id=s.id))::text FROM sources s WHERE s.id=@SourceId",
                source
            )
        );

    private Task Execute(string sql, object? args = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, args));

    private static string Title() => "T" + Guid.NewGuid().ToString("N");
}
