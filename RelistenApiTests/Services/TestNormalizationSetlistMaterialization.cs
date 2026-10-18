using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Real retained accounts pass through extraction, repertoire resolution and the accepted setlist writer.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistMaterialization
{
    private const string Operator = "setlist-materialization-fixture";
    private RepertoireResolutionPolicyService repertoirePolicies = null!;
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationSetlistProcessor extraction = null!;
    private NormalizationRepertoireResolutionProcessor repertoire = null!;
    private NormalizationSetlistMaterializer materializer = null!;
    private NormalizedMusicService music = null!;
    private NormalizationReadService reads = null!;
    private RepertoireConsumerPolicyService policies = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        music = new(fixture.Db, fixture.Work);
        reads = new(fixture.Db, Options.Create(new NormalizationReadOptions { Enabled = true }));
        extraction = new(new(fixture.Db), evidence, new(fixture.Db), new(), new(), fixture.Work);
        var sources = new NormalizationRepertoireSourceService(
            fixture.Db,
            evidence,
            new(),
            new(fixture.Db, evidence),
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db)
        );
        repertoirePolicies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        repertoire = new(
            fixture.Db,
            sources,
            new(fixture.Db),
            repertoirePolicies,
            new(fixture.Db),
            fixture.Work,
            music
        );
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
        materializer = new(
            fixture.Db,
            new(fixture.Db),
            policies,
            new(fixture.Db),
            fixture.Work,
            music
        );
    }

    [TearDown]
    public async Task RevokeFixturePolicies()
    {
        var consumer = await policies.ReadStateAsync();
        if (consumer.Active != null)
        {
            await policies.ApplyDecisionAsync(
                new(
                    Guid.NewGuid(),
                    consumer.OwnerObservation.SubjectUuid,
                    consumer.OwnerObservation.EditorialRevision,
                    consumer.Active.Policy.Artifact.Uuid,
                    null,
                    "Restore inactive consumer policy after disposable fixture"
                ),
                Operator
            );
        }
        var resolver = await repertoirePolicies.ReadStateAsync();
        if (resolver.Active != null)
        {
            await repertoirePolicies.ApplyDecisionAsync(
                new(
                    Guid.NewGuid(),
                    resolver.OwnerObservation.SubjectUuid,
                    resolver.OwnerObservation.EditorialRevision,
                    resolver.Active.Policy.Artifact.Uuid,
                    null,
                    "Restore inactive repertoire policy after disposable fixture"
                ),
                Operator
            );
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task CompleteAccountPreservesEmptyRepeatedSetsOccurrencesAndSourceAnnotations()
    {
        var source = await Account();
        var resolved = await Resolve(source);
        var attempt = await Materialize(resolved);
        var accepted = await Accepted(attempt);

        accepted.Value.Edition.ArtistUuid.Should().Be(source.ArtistUuid);
        accepted.Value.Edition.ShowUuid.Should().BeNull();
        accepted.Value.Edition.LiteralDate.Should().Be("17-10-2026");
        accepted.Value.Sets.Select(set => set.Label).Should().Equal("First", null, "First");
        accepted.Value.Sets.Select(set => set.Position).Should().Equal(1, 2, 3);
        accepted.Value.Sets[1].EncoreNumber.Should().Be(1);
        accepted
            .Value.Occurrences.Select(value => value.OriginalText)
            .Should()
            .Equal("Song", "Song", "Reprise");
        accepted.Value.Occurrences.Select(value => value.Uuid).Should().OnlyHaveUniqueItems();
        accepted.Value.Occurrences.Should().OnlyContain(value => value.RepertoireSongUuid != null);
        accepted
            .Value.Sources.Single()
            .Account.Should()
            .BeEquivalentTo(source.Batch.Extractions.Single().Accounts.Single());
        accepted.Value.Sources.Single().Account.Passages[0].Passage.Tape!.Value.Should().BeTrue();
        (await reads.ListSetlistSetsAsync(accepted.Value.Edition.Uuid))!
            .items.Should()
            .HaveCount(3);
        (await reads.ListOccurrencesAsync(accepted.Value.Edition.Uuid))!
            .items.Should()
            .HaveCount(3);
        var annotations = (
            await reads.ListSetlistAnnotationsAsync(accepted.Value.Edition.Uuid)
        )!.items;
        annotations
            .Should()
            .Contain(group =>
                group.scope == "account"
                && group.notes != null
                && group.notes.Any(note => note.text == "Whole account note")
            );
        annotations
            .Should()
            .Contain(group =>
                group.scope == "passage"
                && group.notes != null
                && group.notes.Any(note => note.text == "Original tease wording")
            );
        (await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString()))
            .items.Should()
            .Contain(value => value.uuid == accepted.Value.Edition.Uuid && value.show_uuid == null);
        await AssertSettled(attempt);
    }

    [Test]
    public async Task EmptyCompleteAccountPublishesAnObservableEmptyEdition()
    {
        var source = await Account(empty: true);
        var attempt = await Materialize(await Resolve(source));
        var accepted = await Accepted(attempt);

        accepted.Value.Sets.Should().BeEmpty();
        accepted.Value.Occurrences.Should().BeEmpty();
        accepted.Value.Sources.Should().ContainSingle();
        (await reads.ListOccurrencesAsync(accepted.Value.Edition.Uuid))!.items.Should().BeEmpty();
        (await reads.ListSetlistSetsAsync(accepted.Value.Edition.Uuid))!.items.Should().BeEmpty();
        await AssertSettled(attempt);
    }

    [TestCase("unknown_set")]
    [TestCase("unordered")]
    public async Task UnknownOrderAndMembershipAreNotReplacedWithSourceArrayPositions(string shape)
    {
        var source = await Account(shape: shape);
        var attempt = await Materialize(await Resolve(source));
        var accepted = await Accepted(attempt);

        accepted.Value.Edition.Ordered.Should().Be(shape != "unordered");
        accepted.Value.Sets.Should().BeEmpty();
        if (shape == "unknown_set")
        {
            var occurrence = accepted.Value.Occurrences.Should().ContainSingle().Which;
            occurrence.SetUuid.Should().BeNull();
            occurrence.Position.Should().Be(1);
        }
        else
        {
            accepted.Value.Occurrences.Should().BeEmpty();
        }
    }

    [Test]
    public async Task ExactTerminalReplayKeepsTheFirstImageAndRejectsAnotherRequest()
    {
        var source = await Account();
        var resolved = await Resolve(source);
        var attempt = await Materialize(resolved);
        var accepted = await Accepted(attempt);

        var replay = await materializer.ProcessAsync(
            attempt.AttemptUuid,
            new(resolved.AttemptUuid)
        );
        replay.Result.Should().BeEquivalentTo(attempt.Result! with { Replayed = true });
        (await Accepted(replay)).Should().BeEquivalentTo(accepted);
        Func<Task> different = () =>
            materializer.ProcessAsync(attempt.AttemptUuid, new(Guid.NewGuid()));
        await different.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task UnchangedRetrySettlesWithoutReplacingAcceptedIdentitiesOrRevisions()
    {
        var source = await Account();
        var attempt = await Materialize(await Resolve(source));
        var accepted = await Accepted(attempt);
        var retryUuid = Guid.NewGuid();

        var retry = await Drain(() =>
            materializer.RetryAsync(retryUuid, attempt.Registration.RegistrationUuid)
        );

        retry
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed, Audit(retry));
        (await materializer.GetRegistrationAsync(retry.Registration.RegistrationUuid))!
            .EditionUuid.Should()
            .Be(accepted.Value.Edition.Uuid);
        (await Accepted(retry)).Should().BeEquivalentTo(accepted);
        await AssertSettled(retry);
    }

    [Test]
    public async Task EveryClaimBeyondDefaultResolverAndPublicReadPagesGetsOneOccurrence()
    {
        var source = await Account(passageCount: 101);
        var attempt = await Materialize(await Resolve(source));
        var accepted = await Accepted(attempt);
        accepted.Value.Occurrences.Should().HaveCount(101);
        accepted.Value.PassageCorrespondence.Should().HaveCount(101);
        var publicIds = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = (await reads.ListOccurrencesAsync(accepted.Value.Edition.Uuid, 50, cursor))!;
            publicIds.AddRange(page.items.Select(value => value.uuid));
            cursor = page.next_cursor;
        } while (cursor != null);
        publicIds.Should().HaveCount(101).And.OnlyHaveUniqueItems();
        publicIds.Should().BeEquivalentTo(accepted.Value.Occurrences.Select(value => value.Uuid));
        var decisions = new List<SetlistMaterializationDecision>();
        Guid? after = null;
        do
        {
            var page = await materializer.GetDecisionsAsync(attempt.AttemptUuid, 50, after);
            decisions.AddRange(page.Decisions);
            after = page.NextCursor;
        } while (after != null);
        decisions
            .OfType<SetlistClaimMaterializationDecision>()
            .Count(value => value.Claim.ProposalUuid != null)
            .Should()
            .Be(101);
    }

    [Test]
    public async Task ChangedOriginalHeadAfterPreparationCannotPublishAnAcceptedImage()
    {
        var source = await Account();
        var resolved = await Resolve(source);
        var attemptUuid = Guid.NewGuid();
        var prepared = await Until(
            attemptUuid,
            resolved.AttemptUuid,
            value => value.Preparation != null
        );
        await Recapture(source, "Changed after consumer preparation");

        var stale = await Drain(() =>
            materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid))
        );

        stale.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Stale, Audit(stale));
        (await music.GetSetlistStructureAsync(prepared.Preparation!.Context!.EditionUuid))
            .Should()
            .BeNull();
    }

    [Test]
    public async Task ChangedOriginalHeadAfterAdmissionLeavesTheLastAcceptedImageIntact()
    {
        var source = await Account();
        var resolved = await Resolve(source);
        var initial = await Materialize(resolved);
        var accepted = await Accepted(initial);
        var nextUuid = Guid.NewGuid();
        await Until(nextUuid, resolved.AttemptUuid, value => value.Ticket != null);
        await Recapture(source, "Changed after admission");

        var stale = await Drain(() =>
            materializer.ProcessAsync(nextUuid, new(resolved.AttemptUuid))
        );

        stale.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Stale, Audit(stale));
        (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
    }

    [Test]
    public async Task PartialRepertoireReceiptCannotBeConsumedAsACompleteAccount()
    {
        var source = await Account();
        var repertoireUuid = Guid.NewGuid();
        var partial = await repertoire.ResolveAsync(
            repertoireUuid,
            new(new SetlistRepertoireResolutionSource(source.Batch.AttemptUuid))
        );
        partial.Result?.Disposition.Should().NotBe(RepertoireResolutionDisposition.Completed);
        var attemptUuid = Guid.NewGuid();

        Func<Task> consume = () => materializer.ProcessAsync(attemptUuid, new(repertoireUuid));
        await consume.Should().ThrowAsync<ArgumentException>();
        (await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString()))
            .items.Should()
            .BeEmpty();
    }

    [Test]
    public async Task CancellationAfterPreparationRetainsAllocatedUuidsForRecovery()
    {
        var source = await Account();
        var resolved = await Resolve(source);
        var attemptUuid = Guid.NewGuid();
        var prepared = await Until(
            attemptUuid,
            resolved.AttemptUuid,
            value => value.Preparation?.Image != null
        );
        var image = prepared.Preparation!.Image!;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> canceled = () =>
            materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid), cancellation.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        var recovery = await materializer.GetRecoveryAttemptsAsync(200);
        recovery.Attempts.Should().Contain(value => value.AttemptUuid == attemptUuid);

        var resumed = await Drain(() =>
            materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid))
        );

        (await Accepted(resumed))
            .Value.Occurrences.Select(value => value.Uuid)
            .Should()
            .Equal(image.Occurrences.Select(value => value.Uuid));
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .NotContain(value => value.AttemptUuid == attemptUuid);
    }

    [Test]
    public async Task MissingArtistAcknowledgesOnlyASourceWatcherAndLaterBindingRetiresIt()
    {
        var source = await Account(bindArtist: false);
        var unresolved = await Materialize(await Resolve(source));
        unresolved
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed, Audit(unresolved));
        unresolved.Registration.EditionUuid.Should().BeNull();
        (await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString()))
            .items.Should()
            .BeEmpty();
        var observer = new NormalizationSubjectKey(
            "songs",
            "setlist_materialization",
            source.Batch.RegistrationUuid
        );
        (await fixture.Work.GetInputContributionsAsync(observer))
            .Contributions.Should()
            .Contain(value => value.Resources.Count > 0);
        await Map(source);
        var extractionRetry = await extraction.RetryAsync(
            Guid.NewGuid(),
            source.Batch.RegistrationUuid
        );
        extractionRetry
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var bound = source with
        {
            Batch = (await extraction.GetAsync(source.Batch.RegistrationUuid))!,
        };
        await Resolve(bound);
        var retryUuid = Guid.NewGuid();
        SetlistMaterializationAttempt? published = null;
        for (var phase = 0; phase < 20; phase++)
        {
            var advancing = await materializer.RetryAsync(
                retryUuid,
                unresolved.Registration.RegistrationUuid
            );
            if (advancing.Receipt != null)
            {
                published = advancing;
                break;
            }
        }
        published
            .Should()
            .NotBeNull("canonical adoption must publish before the obsolete watcher retires");
        published!.Result.Should().BeNull();
        published.Preparation!.RequiresObservationRetirement.Should().BeTrue();
        (await fixture.Work.GetInputContributionsAsync(observer))
            .Contributions.Should()
            .Contain(value => value.Resources.Count > 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> interrupted = () =>
            materializer.RetryAsync(
                retryUuid,
                unresolved.Registration.RegistrationUuid,
                cancellation.Token
            );
        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .Contain(value => value.AttemptUuid == retryUuid);

        var adopted = await Drain(() =>
            materializer.RetryAsync(retryUuid, unresolved.Registration.RegistrationUuid)
        );

        adopted
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed, Audit(adopted));
        (await Accepted(adopted)).Value.Edition.ArtistUuid.Should().Be(source.ArtistUuid);
        (await materializer.GetRegistrationAsync(adopted.Registration.RegistrationUuid))!
            .ObservationRetired.Should()
            .BeTrue();
        (await fixture.Work.GetInputContributionsAsync(observer))
            .Contributions.Should()
            .OnlyContain(value => value.Resources.Count == 0);
        await AssertSettled(adopted);
    }

    [Test]
    public async Task RevisedUnkeyedAccountWithoutApplicableAuthorityRetainsTheLastCompleteImage()
    {
        var source = await Account();
        var first = await Materialize(await Resolve(source));
        var accepted = await Accepted(first);
        var changed = await Revise(
            source,
            source.Payload.Replace("Reprise", "Changed reprise", StringComparison.Ordinal)
        );
        var retry = await Materialize(await Resolve(changed));

        retry.Result!.Review.Should().BeGreaterThan(0);
        (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
        (await materializer.GetDecisionsAsync(retry.AttemptUuid, 200))
            .Decisions.Should()
            .Contain(value => value.Reason == "unkeyed_revised_claim_requires_authority");
    }

    [Test]
    public async Task ReviewedMetadataCannotBeReplacedByAnAutomaticMaterialization()
    {
        var source = await Account();
        var first = await Materialize(await Resolve(source));
        var accepted = await Accepted(first);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "setlist", accepted.Value.Edition.Uuid)
            )
        )!;
        var reviewed = accepted.Value with
        {
            ExpectedRevision = accepted.Revision,
            Edition = accepted.Value.Edition with
            {
                ExpectedRevision = accepted.Value.Edition.ExpectedRevision + 1,
            },
            Occurrences = accepted
                .Value.Occurrences.Select(value => value with { ExpectedRevision = 1 })
                .ToArray(),
        };
        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            reviewed,
            "Fixture operator checks the complete source account",
            "setlist-materialization-fixture"
        );
        var reviewSnapshot = (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))!;
        var changed = await Revise(
            source,
            source.Payload.Replace("Reprise", "Reviewed source changed", StringComparison.Ordinal)
        );

        var next = await Materialize(await Resolve(changed));

        next.Result!.Review.Should().BeGreaterThan(0);
        (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(reviewSnapshot);
    }

    [Test]
    public async Task PublicationFailureRollsBackRowsAndReceiptAndRecoversWithSavedUuids()
    {
        var source = await Account();
        var resolved = await Resolve(source);
        var attemptUuid = Guid.NewGuid();
        var admitted = await Until(
            attemptUuid,
            resolved.AttemptUuid,
            value => value.Ticket != null
        );
        var image = admitted.Preparation!.Image!;
        var trigger = "setlist_materialization_" + Guid.NewGuid().ToString("N");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.edition_uuid='{image.Edition.Uuid:D}'::uuid THEN
                        RAISE EXCEPTION 'Injected setlist materialization fixture failure';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE ON normalization.song_occurrences
                FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}();
                """
            )
        );
        try
        {
            Func<Task> publish = () =>
                materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid));
            var failure = (await publish.Should().ThrowAsync<Exception>()).Which;
            failure.Should().BeOfType<Exception>();
            var databaseFailure = failure
                .InnerException.Should()
                .BeOfType<PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be("P0001");
            databaseFailure
                .MessageText.Should()
                .Be("Injected setlist materialization fixture failure");
            (await music.GetSetlistStructureAsync(image.Edition.Uuid)).Should().BeNull();
            var retained = (await materializer.GetAsync(attemptUuid))!;
            retained.Receipt.Should().BeNull();
            retained.Result.Should().BeNull();
            var owner = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("songs", "setlist", image.Edition.Uuid)
                )
            )!;
            owner.PublishedGeneration.Should().Be(0);
            (await materializer.GetRecoveryAttemptsAsync(200))
                .Attempts.Should()
                .Contain(value => value.AttemptUuid == attemptUuid);
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"DROP TRIGGER {trigger} ON normalization.song_occurrences; DROP FUNCTION normalization.{trigger}();"
                )
            );
        }

        var recovered = await Drain(() =>
            materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid))
        );
        (await Accepted(recovered))
            .Value.Occurrences.Select(value => value.Uuid)
            .Should()
            .Equal(image.Occurrences.Select(value => value.Uuid));
        await AssertSettled(recovered);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EmptyRequestIdentifiersFailBeforeRegistration(bool emptyAttempt)
    {
        var attempt = emptyAttempt ? Guid.Empty : Guid.NewGuid();
        var repertoireAttempt = emptyAttempt ? Guid.NewGuid() : Guid.Empty;
        Func<Task> process = () => materializer.ProcessAsync(attempt, new(repertoireAttempt));
        await process.Should().ThrowAsync<ArgumentException>();
        (await materializer.GetAsync(attempt)).Should().BeNull();
    }

    [Test]
    public async Task EvaluatedNativeSetAnchorsReuseTheUnkeyedSetAndAuthorizeItsWithdrawal()
    {
        await ActivateUsagePolicy(
            MusicMaterializationRule.QualifiedUsageAnchors,
            MusicMaterializationRule.BracketedWithdrawal
        );
        var source = await PhantasyAccount();
        var first = await Materialize(await Resolve(source));
        var initial = await Accepted(first);
        var center = initial.Value.Sets.Single(value => value.ProviderIdentity == null);
        var changed = await Revise(
            source,
            PhantasyPage(source, centerLabel: "Source clarifies the center")
        );

        var revised = await Materialize(await Resolve(changed));

        revised.Result!.Review.Should().Be(0, Audit(revised));
        var reused = await Accepted(revised);
        reused
            .Value.Sets.Single(value => value.ProviderIdentity == null)
            .Uuid.Should()
            .Be(center.Uuid);
        reused
            .Value.Sets.Single(value => value.ProviderIdentity == null)
            .Label.Should()
            .Be("Source clarifies the center");
        (await materializer.GetDecisionsAsync(revised.AttemptUuid, 200))
            .Decisions.Should()
            .Contain(value =>
                value.Evaluation != null
                && value.Evaluation.Rule == MusicMaterializationRule.QualifiedUsageAnchors
            );
        var removedSource = await Revise(changed, PhantasyPage(changed, omitCenter: true));
        var removed = await Materialize(await Resolve(removedSource));
        removed.Result!.Review.Should().Be(0, Audit(removed));
        var withdrawn = await Accepted(removed);
        withdrawn.Value.Sets.Single(value => value.Uuid == center.Uuid).Retracted.Should().BeTrue();
        (await reads.ListSetlistSetsAsync(withdrawn.Value.Edition.Uuid))!
            .items.Should()
            .HaveCount(2);
        (await materializer.GetDecisionsAsync(removed.AttemptUuid, 200))
            .Decisions.OfType<SetlistRemovalMaterializationDecision>()
            .Should()
            .Contain(value =>
                value.Target.Uuid == center.Uuid
                && value.Evaluation!.Rule == MusicMaterializationRule.BracketedWithdrawal
            );

        var historicalEvidence = withdrawn
            .Value.Sets.Single(value => value.Uuid == center.Uuid)
            .Evidence;
        var unchangedUuid = Guid.NewGuid();
        var unchanged = await Drain(() =>
            materializer.RetryAsync(unchangedUuid, removed.Registration.RegistrationUuid)
        );
        unchanged.Preparation!.Image.Should().BeNull();
        (await Accepted(unchanged)).Should().BeEquivalentTo(withdrawn);
        var refreshedSource = await Revise(
            removedSource,
            PhantasyPage(removedSource, omitCenter: true, note: "Later source annotation")
        );
        var refreshed = await Materialize(await Resolve(refreshedSource));
        withdrawn = await Accepted(refreshed);
        withdrawn
            .Value.Sets.Single(value => value.Uuid == center.Uuid)
            .Evidence.Should()
            .BeEquivalentTo(historicalEvidence);
        (await reads.ListSetlistSetsAsync(withdrawn.Value.Edition.Uuid))!
            .items.Should()
            .HaveCount(2);

        // A consumed removal is historical evidence, not authority to recreate or remove a later claim.
        var reappearedSource = await Revise(
            refreshedSource,
            PhantasyPage(refreshedSource, centerLabel: "Reappeared center")
        );
        var reappeared = await Materialize(await Resolve(reappearedSource));
        reappeared.Result!.Review.Should().BeGreaterThan(0);
        (await music.GetSetlistStructureAsync(withdrawn.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(withdrawn);
        var currentSource = reappeared.Preparation!.Context!.Sources.Single();
        var currentEdition = (
            await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString())
        ).items.Single(value => value.uuid == withdrawn.Value.Edition.Uuid);
        var currentOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("songs", "setlist", currentEdition.uuid)
            )
        )!;
        var authorizationUuid = Guid.NewGuid();
        await music.ReviewSetlistCorrespondenceAsync(
            new(
                Guid.NewGuid(),
                currentOwner.SubjectUuid,
                currentEdition.uuid,
                currentOwner.EditorialRevision,
                withdrawn.Revision,
                currentEdition.revision,
                [
                    new(
                        authorizationUuid,
                        currentSource,
                        new(SetIndex: 1),
                        SetlistAuthorizationAction.Authorize,
                        [new(center.Uuid, withdrawn.Revision)],
                        null,
                        [currentSource.Account.Account.Sets[1].Evidence]
                    ),
                ],
                [],
                "Explicitly reactivate this exact current tombstone for its reappeared source claim"
            ),
            Operator
        );
        var retryUuid = Guid.NewGuid();

        var reactivated = await Drain(() =>
            materializer.RetryAsync(retryUuid, reappeared.Registration.RegistrationUuid)
        );

        reactivated.Result!.Review.Should().Be(0, Audit(reactivated));
        var restored = await Accepted(reactivated);
        restored.Value.Sets.Single(value => value.Uuid == center.Uuid).Retracted.Should().BeFalse();
        restored
            .Value.SetCorrespondence.Single(value => value.SetIndex == 1)
            .SetUuid.Should()
            .Be(center.Uuid);
        (await materializer.GetDecisionsAsync(reactivated.AttemptUuid, 200))
            .Decisions.OfType<SetlistClaimMaterializationDecision>()
            .Should()
            .Contain(value =>
                value.Claim.SetIndex == 1 && value.ExistingAuthorizationUuid == authorizationUuid
            );
    }

    [Test]
    public async Task HistoricalSetEvidenceCannotBeReplacedOrUsedThroughTheGenericWriter()
    {
        await ActivateUsagePolicy(MusicMaterializationRule.BracketedWithdrawal);
        var source = await PhantasyAccount();
        var first = await Materialize(await Resolve(source));
        var accepted = await Accepted(first);
        var changed = await Revise(source, PhantasyPage(source, omitCenter: true));
        var resolved = await Resolve(changed);
        var attemptUuid = Guid.NewGuid();
        var admitted = await Until(
            attemptUuid,
            resolved.AttemptUuid,
            value => value.Ticket != null
        );
        var preparation = admitted.Preparation!;
        var image = preparation.Image!;
        var tombstone = image.Sets.Single(value => value.Retracted);
        var generic = new MusicPublication
        {
            Editions = [image.Edition],
            Occurrences = image.Occurrences,
            SetlistStructure = image,
        };
        Func<Task> genericPublication = () =>
            music.PublishAsync(admitted.Ticket!.WorkUuid, generic);
        await genericPublication
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("Set evidence is outside the consumed original input read set.");

        // Even a correctly checkpointed write cannot replace the durable preparation. Valid
        // retained evidence from another set also cannot supply this target's provenance.
        var altered = preparation with
        {
            Image = image with
            {
                Sets = image
                    .Sets.Select(value =>
                        value.Uuid == tombstone.Uuid
                            ? value with
                            {
                                Evidence = accepted
                                    .Value.Sets.First(set => set.Uuid != tombstone.Uuid)
                                    .Evidence,
                            }
                            : value
                    )
                    .ToArray(),
            },
        };
        var replacementFailure = await FluentActions
            .Invoking(() => SavePreparation(altered))
            .Should()
            .ThrowAsync<Exception>();
        var databaseFailure = replacementFailure
            .Which.InnerException.Should()
            .BeOfType<Npgsql.PostgresException>()
            .Which;
        databaseFailure.SqlState.Should().Be("P0001");
        databaseFailure
            .MessageText.Should()
            .Be(
                "Materialization original requests, selections, preparations and receipts are retained"
            );
        Func<Task> alteredPublication = () =>
            music.PublishAsync(
                admitted.Ticket!.WorkUuid,
                generic with
                {
                    SetlistStructure = altered.Image,
                }
            );
        await alteredPublication
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("Set evidence is outside the consumed original input read set.");
        (await music.GetSetlistStructureAsync(image.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
        (await materializer.GetAsync(attemptUuid))!.Receipt.Should().BeNull();
        var recovered = await Drain(() =>
            materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid))
        );
        (await Accepted(recovered))
            .Value.Sets.Single(value => value.Uuid == tombstone.Uuid)
            .Evidence.Should()
            .BeEquivalentTo(tombstone.Evidence);

        Task SavePreparation(SetlistMaterializationPreparation value) =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.setlist_materialization_attempts SET preparation=@json::jsonb,checkpoint_revision=checkpoint_revision+1 WHERE attempt_uuid=@attemptUuid",
                    new { attemptUuid, json = JsonSerializer.Serialize(value) }
                )
            );
    }

    [Test]
    public async Task EvaluatedUniqueSongWithinANativeSetRetainsAnUnkeyedOccurrence()
    {
        await ActivateRepertoirePolicy();
        await ActivateUsagePolicy(MusicMaterializationRule.UniqueBoundSongWithinQualifiedSet);
        var source = await PhantasyAccount(withSong: true);
        var first = await Materialize(await Resolve(source));
        var accepted = await Accepted(first);
        var original = accepted.Value.Occurrences.Should().ContainSingle().Which;
        var changed = await Revise(
            source,
            PhantasyPage(source, withSong: true, note: "New exact source annotation")
        );

        var revised = await Materialize(await Resolve(changed));

        revised.Result!.Review.Should().Be(0, Audit(revised));
        var retained = (await Accepted(revised)).Value.Occurrences.Should().ContainSingle().Which;
        retained.Uuid.Should().Be(original.Uuid);
        retained.RepertoireSongUuid.Should().Be(original.RepertoireSongUuid);
        (await materializer.GetDecisionsAsync(revised.AttemptUuid, 200))
            .Decisions.Should()
            .Contain(value =>
                value.Evaluation != null
                && value.Evaluation.Rule
                    == MusicMaterializationRule.UniqueBoundSongWithinQualifiedSet
            );
        var annotations = (
            await reads.ListSetlistAnnotationsAsync(accepted.Value.Edition.Uuid)
        )!.items;
        annotations
            .Should()
            .Contain(value =>
                value.scope == "passage"
                && value.notes != null
                && value.notes.Any(note => note.text == "New exact source annotation")
            );
    }

    [Test]
    public async Task RepeatedSongAcrossTheWholeNativeAccountRefusesUniqueSongCorrespondence()
    {
        await ActivateRepertoirePolicy();
        await ActivateUsagePolicy(MusicMaterializationRule.UniqueBoundSongWithinQualifiedSet);
        var source = await PhantasyAccount(withSong: true, repeatSong: true);
        var first = await Materialize(await Resolve(source));
        var accepted = await Accepted(first);
        var changed = await Revise(
            source,
            PhantasyPage(source, withSong: true, repeatSong: true, note: "Competing repeated claim")
        );

        var reviewed = await Materialize(await Resolve(changed));

        reviewed.Result!.Review.Should().BeGreaterThan(0);
        (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
    }

    [Test]
    public async Task RetryWaitsForTheExactCurrentExtractionToHaveACompletedRepertoireReceipt()
    {
        var source = await Account();
        var first = await Materialize(await Resolve(source));
        var changed = await Revise(
            source,
            source.Payload.Replace(
                "Reprise",
                "Changed current extraction",
                StringComparison.Ordinal
            )
        );
        var retryUuid = Guid.NewGuid();

        var waiting = await materializer.RetryAsync(retryUuid, first.Registration.RegistrationUuid);

        waiting.Result.Should().BeNull();
        waiting.SelectedRepertoireAttemptUuid.Should().BeNull();
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .Contain(value => value.AttemptUuid == retryUuid);
        var resolved = await Resolve(changed);
        var resumed = await Drain(() =>
            materializer.RetryAsync(retryUuid, first.Registration.RegistrationUuid)
        );
        resumed.SelectedRepertoireAttemptUuid.Should().Be(resolved.AttemptUuid);
        resumed
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed, Audit(resumed));
        (await materializer.RetryAsync(retryUuid, first.Registration.RegistrationUuid))
            .SelectedRepertoireAttemptUuid.Should()
            .Be(resolved.AttemptUuid);
    }

    [Test]
    public async Task ExistingUnmanagedEditionRemainsBaselineInsteadOfBeingAdoptedByMatchingItsKey()
    {
        var source = await Account();
        var input = await fixture.Capture("Existing trusted edition", "songs");
        var editionUuid = Guid.NewGuid();
        var owned = await fixture.Own(input, "setlist", editionUuid);
        var account = source.Batch.Extractions.Single().Accounts.Single().Account;
        await music.PublishAsync(
            owned.Ticket.WorkUuid,
            new MusicPublication
            {
                Editions =
                [
                    new()
                    {
                        Uuid = editionUuid,
                        ArtistUuid = source.ArtistUuid,
                        LiteralDate = account.LiteralDate!.Value,
                        Provider = account.Origin.Item.Provider,
                        ResourceType = account.Origin.Item.ResourceType,
                        ProviderKey = account.Origin.Item.ProviderKey,
                        Ordered = true,
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
        var before = (
            await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString())
        ).items.Single();

        var attempt = await Materialize(await Resolve(source));

        attempt.Result!.Review.Should().BeGreaterThan(0);
        (await materializer.GetRegistrationAsync(attempt.Registration.RegistrationUuid))!
            .EditionUuid.Should()
            .Be(editionUuid);
        (await music.GetSetlistStructureAsync(editionUuid)).Should().BeNull();
        (await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString()))
            .items.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(before);
    }

    [Test]
    public async Task CompleteImageCapacityFailureIsPermanentUntilItsSourceOrLimitContractChanges()
    {
        var source = await Account(empty: true);
        var account = source
            .Batch.Extractions.Single()
            .Accounts.Single()
            .Account.Origin.Item.ProviderKey;
        var payload = JsonSerializer.Serialize(
            new
            {
                page = 1,
                itemsPerPage = 20,
                total = 1,
                setlist = new[]
                {
                    new
                    {
                        id = account,
                        eventDate = "17-10-2026",
                        artist = new { mbid = source.ArtistKey, name = "Fixture performer" },
                        sets = new
                        {
                            set = Enumerable
                                .Range(1, 10001)
                                .Select(index => new
                                {
                                    name = "Empty " + index,
                                    song = Array.Empty<object>(),
                                })
                                .ToArray(),
                        },
                    },
                },
            }
        );
        var overflow = await Revise(source, payload);
        var resolved = await Resolve(overflow);
        var attemptUuid = Guid.NewGuid();

        var failed = await Drain(() =>
            materializer.ProcessAsync(attemptUuid, new(resolved.AttemptUuid))
        );

        failed.Result!.Disposition.Should().Be(MusicMaterializationDisposition.Stale);
        failed.Result.Reason.Should().Be("materialization_capacity_exceeded");
        failed.Result.RetryRequirement.Should().NotBeNull();
        failed.Ticket.Should().BeNull();
        (await reads.ListArtistSetlistsAsync(source.ArtistUuid.ToString()))
            .items.Should()
            .BeEmpty();
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .NotContain(value => value.AttemptUuid == attemptUuid);
        (await materializer.GetPendingRegistrationsAsync(200))
            .Registrations.Should()
            .NotContain(value => value.RegistrationUuid == failed.Registration.RegistrationUuid);
        var retryUuid = Guid.NewGuid();
        Func<Task> unchanged = () =>
            materializer.RetryAsync(retryUuid, failed.Registration.RegistrationUuid);
        await unchanged.Should().ThrowAsync<NormalizationConflictException>();
        (await materializer.GetAsync(retryUuid)).Should().BeNull();
    }

    [Test]
    public async Task EquivalentScopedSubscribersSettleWithoutAlternatingTheAcceptedSourceReceipt()
    {
        var source = await PhantasyAccount();
        var first = await Materialize(await Resolve(source));
        var accepted = await Accepted(first);
        var account = source.Batch.Extractions.Single().Accounts.Single().Account.Origin.Item;
        var secondExtraction = await extraction.ProcessAsync(
            Guid.NewGuid(),
            new(source.Input.Uuid, source.Resource),
            account,
            source.ArtistUuid
        );
        secondExtraction
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        secondExtraction
            .Registration.RegistrationUuid.Should()
            .NotBe(source.Batch.RegistrationUuid);
        var scoped = source with
        {
            Batch = (await extraction.GetAsync(secondExtraction.Registration.RegistrationUuid))!,
        };

        var second = await Materialize(await Resolve(scoped));
        var retryUuid = Guid.NewGuid();
        var retried = await Drain(() =>
            materializer.RetryAsync(retryUuid, first.Registration.RegistrationUuid)
        );

        second.Registration.RegistrationUuid.Should().NotBe(first.Registration.RegistrationUuid);
        (await materializer.GetRegistrationAsync(second.Registration.RegistrationUuid))!
            .EditionUuid.Should()
            .Be(accepted.Value.Edition.Uuid);
        (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
        var membership = await fixture.Work.GetInputContributionsAsync(
            new("songs", "setlist", accepted.Value.Edition.Uuid)
        );
        var keys = membership.Contributions.Select(value => value.Contributor).ToArray();
        keys.Should()
            .Contain(
                new NormalizationResourceKey(
                    "relisten",
                    "setlist_materializer",
                    first.Registration.RegistrationUuid.ToString("D")
                )
            );
        keys.Should()
            .Contain(
                new NormalizationResourceKey(
                    "relisten",
                    "setlist_materializer",
                    second.Registration.RegistrationUuid.ToString("D")
                )
            );
        await AssertSettled(retried);
        await AssertSettled(second);
    }

    [Test]
    public async Task CleanupCapacityWaitsForMembershipChangeThenResumesTheSameAcceptedAttempt()
    {
        var source = await Account(empty: true, bindArtist: false);
        var negative = await Materialize(await Resolve(source));
        var observer = new NormalizationSubjectKey(
            "songs",
            "setlist_materialization",
            source.Batch.RegistrationUuid
        );
        await Map(source);
        var extractionRetry = await extraction.RetryAsync(
            Guid.NewGuid(),
            source.Batch.RegistrationUuid
        );
        extractionRetry
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var bound = source with
        {
            Batch = (await extraction.GetAsync(source.Batch.RegistrationUuid))!,
        };
        await Resolve(bound);
        var retryUuid = Guid.NewGuid();
        SetlistMaterializationAttempt? adopted = null;
        for (var phase = 0; phase < 20; phase++)
        {
            var value = await materializer.RetryAsync(
                retryUuid,
                negative.Registration.RegistrationUuid
            );
            if (value.Receipt != null)
            {
                adopted = value;
                break;
            }
        }
        adopted.Should().NotBeNull();
        adopted!.Result.Should().BeNull();
        adopted
            .Preparation!.ContributionsToRetire.Should()
            .Contain(value => value.Owner == observer);
        var accepted = (
            await music.GetSetlistStructureAsync(adopted.Preparation.Context!.EditionUuid)
        )!;
        var peers = await AddCapacityPeers(observer, adopted.Preparation.Context);

        var blocked = await materializer.RetryAsync(
            retryUuid,
            negative.Registration.RegistrationUuid
        );

        blocked.Result.Should().BeNull();
        blocked.Receipt.Should().NotBeNull();
        blocked.CleanupRetryRequirement.Should().NotBeNull();
        blocked.CleanupRetryRequirement!.Reason.Should().Be("materialization_capacity_exceeded");
        blocked.CleanupRetryRequirement.CapacityOwner.Should().NotBeNull();
        blocked.CleanupRetryRequirement.CapacityMembership.Should().NotBeNull();
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .NotContain(value => value.AttemptUuid == retryUuid);
        var unchanged = await materializer.RetryAsync(
            retryUuid,
            negative.Registration.RegistrationUuid
        );
        unchanged.CleanupRetryRequirement.Should().BeEquivalentTo(blocked.CleanupRetryRequirement);
        var beforeAdmission = (await fixture.Work.GetSubjectAsync(observer))!;

        await RetireCapacityPeers(peers);

        // Admission changes the membership footprint while the old publication receipt stays
        // untouched. No extraction, resolver or raw source capture occurs during recovery.
        var afterAdmission = (await fixture.Work.GetSubjectAsync(observer))!;
        afterAdmission.PublishedWorkUuid.Should().Be(beforeAdmission.PublishedWorkUuid);
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .Contain(value => value.AttemptUuid == retryUuid);
        var completed = await Drain(() =>
            materializer.RetryAsync(retryUuid, negative.Registration.RegistrationUuid)
        );
        completed
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed, Audit(completed));
        completed.CleanupRetryRequirement.Should().BeNull();
        completed.AttemptUuid.Should().Be(retryUuid);
        (await music.GetSetlistStructureAsync(accepted.Value.Edition.Uuid))
            .Should()
            .BeEquivalentTo(accepted);
        var remaining = await fixture.Work.GetInputContributionsAsync(observer, new(1000, 100000));
        remaining
            .Contributions.Single(value =>
                value.Contributor
                == new NormalizationResourceKey(
                    "relisten",
                    "setlist_materializer",
                    negative.Registration.RegistrationUuid.ToString("D")
                )
            )
            .Resources.Should()
            .BeEmpty();
        var remainingPeer = peers.Keys[^1];
        remaining
            .Contributions.Should()
            .Contain(value => value.Contributor == remainingPeer && value.Resources.Count > 0);
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .NotContain(value => value.AttemptUuid == retryUuid);
    }

    private async Task<CapacityPeers> AddCapacityPeers(
        NormalizationSubjectKey owner,
        SetlistMaterializationContext accepted
    )
    {
        var membership = await fixture.Work.GetInputContributionsAsync(owner, new(1000, 100000));
        var read = accepted.OwnReadSet;
        var wanted = read.Inputs.Select(value => value.RevisionUuid).ToHashSet();
        var references = accepted
            .InputReferences.Where(value => wanted.Contains(value.InputRevisionUuid))
            .ToList();
        for (var index = 0; index < 100; index++)
        {
            var resource = new NormalizationResourceKey(
                "test",
                "setlist_cleanup_capacity",
                Guid.NewGuid().ToString("N")
            );
            var input = await Capture(
                resource,
                "{\"fixture\":\"retained peer resource\"}",
                "application/json",
                "fixture-capacity-v1"
            );
            references.Add(new(input.Uuid, resource));
        }
        var perPeerBindings =
            references.Count
            + read.Dependencies.Count
            + read.CatalogInputs.Count
            + read.SourceIdentities.Count
            + (read.MappingRegistry == null ? 0 : 1)
            + read.ImportedVenues.Count
            + read.CandidateUniverses.Count
            + read.OwnerObservations.Count
            + read.SourceUuids.Count;
        var count = 100000 / perPeerBindings + 1;
        (count + membership.Contributions.Count).Should().BeLessThanOrEqualTo(1000);
        var keys = Enumerable
            .Range(0, count)
            .Select(index => new NormalizationResourceKey(
                "test",
                "setlist_cleanup_peer",
                Guid.NewGuid().ToString("N")
            ))
            .ToArray();
        var originals = membership
            .Contributions.Select(value => new NormalizationInputContributionRevision(
                value.Contributor,
                value.Revision
            ))
            .ToArray();
        var prepared = keys.Select(key => CapacityPeer(key, 0, references, read)).ToArray();
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            owner,
            NormalizationSetlistMaterializer.Version,
            prepared,
            membership.Owner!.DesiredGeneration,
            expectedContributionRevisions: originals
        );
        return new(owner, keys, references.ToArray(), read, originals, ticket);
    }

    private async Task RetireCapacityPeers(CapacityPeers peers)
    {
        var retiring = peers
            .Keys.Take(peers.Keys.Count / 2)
            .Select(key => new NormalizationPreparedInputContribution(new(key, 1, [], []), []))
            .ToList();
        // One still-live peer supplies the original current union clocks. Its complete binding
        // is unchanged; the empty replacements remove only the selected peers' owned keys.
        retiring.Add(CapacityPeer(peers.Keys[^1], 1, peers.Inputs, peers.ReadSet));
        var membership = peers
            .OriginalMembership.Concat(
                peers.Keys.Select(key => new NormalizationInputContributionRevision(key, 1))
            )
            .ToArray();
        var current = (await fixture.Work.GetSubjectAsync(peers.Owner))!;
        await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            peers.Owner,
            NormalizationSetlistMaterializer.Version,
            retiring,
            current.DesiredGeneration,
            expectedContributionRevisions: membership
        );
    }

    private static NormalizationPreparedInputContribution CapacityPeer(
        NormalizationResourceKey key,
        long revision,
        IReadOnlyList<NormalizationBindingInputReference> inputs,
        RepertoireResolutionReadSet read
    ) =>
        new(
            new(
                key,
                revision,
                inputs.Select(value => value.Resource).ToArray(),
                read.Dependencies.Select(value => value.SubjectUuid).ToArray(),
                read.OwnerObservations.Select(value => value.SubjectUuid).ToArray()
            ),
            inputs,
            read.CatalogInputs,
            read.SourceIdentities,
            read.MappingRegistry,
            read.ImportedVenues,
            read.CandidateUniverses,
            read.OwnerObservations,
            read.SourceUuids
        );

    private sealed record CapacityPeers(
        NormalizationSubjectKey Owner,
        IReadOnlyList<NormalizationResourceKey> Keys,
        IReadOnlyList<NormalizationBindingInputReference> Inputs,
        RepertoireResolutionReadSet ReadSet,
        IReadOnlyList<NormalizationInputContributionRevision> OriginalMembership,
        NormalizationWorkTicket Ticket
    );

    private async Task<AccountState> PhantasyAccount(bool withSong = false, bool repeatSong = false)
    {
        var catalog = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", catalog)
        );
        var band = Random.Shared.Next(10000, int.MaxValue).ToString();
        var show = Random.Shared.Next(10000, int.MaxValue).ToString();
        var provider = await fixture.Db.WithWriteConnection(async c =>
            await c.QuerySingleOrDefaultAsync<int?>(
                "SELECT id FROM upstream_sources WHERE name='phantasytour.com' ORDER BY id LIMIT 1"
            )
            ?? await c.QuerySingleAsync<int>(
                "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'phantasytour.com','https://provider.invalid') RETURNING id"
            )
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@band)",
                new
                {
                    catalog.ArtistId,
                    provider,
                    band,
                }
            )
        );
        var resource = new NormalizationResourceKey("phantasytour.com", "show", show);
        var state = new AccountState(
            catalog,
            artist,
            band,
            resource,
            null!,
            null!,
            "",
            "application/json",
            "phantasy-tour-http-v1"
        );
        var payload = PhantasyPage(state, withSong: withSong);
        var input = await Capture(resource, payload, state.MediaType, state.Version);
        var account = new NormalizationProviderDecoder().Decode(input).Setlists.Single();
        var processed = await extraction.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, resource),
            account.Origin.Item
        );
        processed
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        state = state with
        {
            Input = input,
            Payload = payload,
            Batch = (await extraction.GetAsync(processed.Registration.RegistrationUuid))!,
        };
        if (repeatSong)
        {
            // Establish the qualified song through one real account before the repeated account
            // tests usage ambiguity. A provider song identifier remains unrelated to occurrence identity.
            await Resolve(state);
            return await Revise(state, PhantasyPage(state, withSong: true, repeatSong: true));
        }
        return state;
    }

    private static string PhantasyPage(
        AccountState source,
        bool withSong = false,
        bool repeatSong = false,
        string centerLabel = "Unkeyed center",
        bool omitCenter = false,
        string note = "Initial exact source annotation"
    )
    {
        var song = new
        {
            id = 29,
            name = "Native song",
            footnotes = new[] { 2 },
        };
        var sets = new List<object>();
        if (withSong)
        {
            sets.Add(
                new
                {
                    id = 11,
                    name = "First",
                    songs = new[] { song },
                }
            );
            if (repeatSong)
            {
                sets.Add(
                    new
                    {
                        id = 13,
                        name = "Second",
                        songs = new[] { song },
                    }
                );
            }
        }
        else
        {
            sets.Add(
                new
                {
                    id = 11,
                    name = "Left anchor",
                    songs = Array.Empty<object>(),
                }
            );
            if (!omitCenter)
            {
                sets.Add(new { name = centerLabel, songs = Array.Empty<object>() });
            }
            sets.Add(
                new
                {
                    id = 13,
                    name = "Right anchor",
                    songs = Array.Empty<object>(),
                }
            );
        }
        return JsonSerializer.Serialize(
            new
            {
                data = new
                {
                    id = int.Parse(source.Resource.ProviderKey),
                    dateTime = "2026-10-17T20:00:00-05:00",
                    band = new { id = int.Parse(source.ArtistKey), name = "Fixture band" },
                    footnotes = new[] { new { id = 2, note } },
                    sets,
                },
            }
        );
    }

    private async Task ActivateUsagePolicy(params MusicMaterializationRule[] rules)
    {
        var artifact = new MusicMaterializationPolicyArtifact(
            Guid.NewGuid(),
            "fixture-usage-v1",
            "",
            NormalizationSetlistMaterializer.Version,
            NormalizationRecordingContentMaterializer.Version,
            RepertoireConsumerCorrespondenceEvaluator.SourceFactsVersion,
            RepertoireConsumerCorrespondenceEvaluator.Version,
            rules,
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
            "independent-fixture-labels-v1",
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
                "Isolated evaluated source correspondence fixture"
            ),
            Operator
        );
    }

    private async Task ActivateRepertoirePolicy()
    {
        var artifact = new RepertoireAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "fixture-repertoire-v1",
            "",
            NormalizationRepertoireResolutionProcessor.Version,
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            NormalizationCandidateService.FactsVersion,
            RepertoireResolutionPolicyEvaluator.MatcherVersion,
            [IdentityMatchRule.QualifiedEntityIdentifier],
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = RepertoireResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new RepertoireAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('d', 64),
            new string('e', 64),
            new string('f', 64),
            "independent-fixture-labels-v1",
            "independent-fixture-evaluator",
            Operator,
            new(20, 0),
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = RepertoireResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        await repertoirePolicies.RegisterEvaluatedPolicyAsync(artifact, evaluation, Operator);
        var state = await repertoirePolicies.ReadStateAsync();
        await repertoirePolicies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.OwnerObservation.SubjectUuid,
                state.OwnerObservation.EditorialRevision,
                state.Active?.Policy.Artifact.Uuid,
                artifact.Uuid,
                "Isolated evaluated qualified song identity fixture"
            ),
            Operator
        );
    }

    private async Task<AccountState> Revise(AccountState previous, string payload)
    {
        var input = await Capture(previous.Resource, payload, previous.MediaType, previous.Version);
        var processed = await extraction.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, previous.Resource),
            previous.Batch.Extractions.Single().Accounts.Single().Account.Origin.Item
        );
        processed
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return previous with
        {
            Input = input,
            Payload = payload,
            Batch = (await extraction.GetAsync(processed.Registration.RegistrationUuid))!,
        };
    }

    private async Task<AccountState> Account(
        bool empty = false,
        string shape = "setlist_fm",
        int? passageCount = null,
        bool bindArtist = true
    )
    {
        var catalog = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", catalog)
        );
        var key = Guid.NewGuid().ToString("D");
        var account = "materialization-" + Guid.NewGuid().ToString("N");
        NormalizationResourceKey resource;
        string payload;
        string mediaType;
        string version;
        if (shape == "unknown_set")
        {
            resource = new("phish.net", "ratings", account);
            payload =
                $$"""<div class="setlist-container"><div class="setlist-date"><a href="/setlists/{{account}}.html">Phish</a></div><div class="setlist-body"><a class="setlist-song" href="/song/song">Song</a></div></div>""";
            mediaType = "text/html";
            version = "phish-net-http-v1";
        }
        else if (shape == "unordered")
        {
            resource = new("phish.net", "shows", account);
            payload = JsonSerializer.Serialize(
                new
                {
                    data = new[]
                    {
                        new
                        {
                            showid = Random.Shared.Next(1, int.MaxValue),
                            artistid = 1,
                            artist_name = "Phish",
                            showdate = "2026-10-17",
                        },
                    },
                }
            );
            mediaType = "application/json";
            version = "phish-net-http-v1";
        }
        else
        {
            resource = new("setlist.fm", "setlist-page", key + ":page=1");
            payload = Page(key, account, empty, passageCount);
            mediaType = "application/json";
            version = "setlist-fm-http-v1";
        }
        var input = await Capture(resource, payload, mediaType, version);
        var decoded = new NormalizationProviderDecoder().Decode(input).Setlists.Single();
        var state = new AccountState(
            catalog,
            artist,
            key,
            resource,
            input,
            null!,
            payload,
            mediaType,
            version
        );
        if (bindArtist && shape == "setlist_fm")
        {
            await Map(state);
        }
        var processed = await extraction.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, resource),
            decoded.Origin.Item,
            bindArtist && shape != "setlist_fm" ? artist : null
        );
        processed
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return state with
        {
            Batch = (await extraction.GetAsync(processed.Registration.RegistrationUuid))!,
        };
    }

    private static string Page(string artist, string account, bool empty, int? passageCount)
    {
        object[] songs = passageCount is { } count
            ? Enumerable
                .Range(1, count)
                .Select(index => (object)new { name = "Song " + index })
                .ToArray()
            :
            [
                new
                {
                    name = "Song",
                    tape = true,
                    info = "Original tease wording",
                },
                new { name = "Song" },
            ];
        object[] sets =
            empty ? []
            : passageCount != null ? [new { name = "First", song = songs }]
            :
            [
                new { name = "First", song = songs },
                new { encore = 1, song = Array.Empty<object>() },
                new { name = "First", song = new[] { new { name = "Reprise" } } },
            ];
        return JsonSerializer.Serialize(
            new
            {
                page = 1,
                itemsPerPage = 20,
                total = 1,
                setlist = new[]
                {
                    new
                    {
                        id = account,
                        eventDate = "17-10-2026",
                        artist = new { mbid = artist, name = "Fixture performer" },
                        sets = new { set = sets },
                        info = "Whole account note",
                    },
                },
            }
        );
    }

    private Task Map(AccountState source) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@ArtistKey WHERE id=@ArtistId",
                new { source.ArtistKey, source.Catalog.ArtistId }
            );
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='setlist.fm' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'setlist.fm','https://provider.invalid') RETURNING id"
                );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@ArtistKey)",
                new
                {
                    source.Catalog.ArtistId,
                    provider,
                    source.ArtistKey,
                }
            );
        });

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        string payload,
        string mediaType,
        string version
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    Encoding.UTF8.GetBytes(payload),
                    mediaType,
                    version,
                    current?.Generation ?? 0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private Task<NormalizationInputRevision> Recapture(AccountState source, string changed) =>
        Capture(
            source.Resource,
            source.Payload.Replace("Whole account note", changed, StringComparison.Ordinal),
            source.MediaType,
            source.Version
        );

    private async Task<RepertoireResolutionAttempt> Resolve(AccountState source)
    {
        var uuid = Guid.NewGuid();
        for (var phase = 0; phase < 100; phase++)
        {
            var value = await repertoire.ResolveAsync(
                uuid,
                new(new SetlistRepertoireResolutionSource(source.Batch.AttemptUuid))
            );
            if (value.Result is { Disposition: not RepertoireResolutionDisposition.Pending })
            {
                value
                    .Result.Disposition.Should()
                    .Be(
                        RepertoireResolutionDisposition.Completed,
                        JsonSerializer.Serialize(value.Result)
                    );
                return value;
            }
        }
        throw new AssertionException("Repertoire source did not settle.");
    }

    private async Task<SetlistMaterializationAttempt> Materialize(
        RepertoireResolutionAttempt source
    )
    {
        var uuid = Guid.NewGuid();
        var result = await Drain(() => materializer.ProcessAsync(uuid, new(source.AttemptUuid)));
        result
            .Result!.Disposition.Should()
            .Be(MusicMaterializationDisposition.Completed, Audit(result));
        return result;
    }

    private async Task<SetlistMaterializationAttempt> Until(
        Guid attempt,
        Guid source,
        Func<SetlistMaterializationAttempt, bool> condition
    )
    {
        for (var phase = 0; phase < 20; phase++)
        {
            var value = await materializer.ProcessAsync(attempt, new(source));
            if (condition(value))
            {
                return value;
            }
            value
                .Result?.Disposition.Should()
                .NotBe(MusicMaterializationDisposition.Stale, Audit(value));
        }
        throw new AssertionException("Materialization did not reach the expected durable phase.");
    }

    private static async Task<SetlistMaterializationAttempt> Drain(
        Func<Task<SetlistMaterializationAttempt>> advance
    )
    {
        for (var phase = 0; phase < 30; phase++)
        {
            var value = await advance();
            if (value.Result is { Disposition: not MusicMaterializationDisposition.Pending })
            {
                return value;
            }
        }
        throw new AssertionException("Setlist materialization did not settle.");
    }

    private async Task<MusicRecord<SetlistStructureChange>> Accepted(
        SetlistMaterializationAttempt attempt
    )
    {
        var registration = (
            await materializer.GetRegistrationAsync(attempt.Registration.RegistrationUuid)
        )!;
        registration.EditionUuid.Should().NotBeNull(Audit(attempt));
        return (await music.GetSetlistStructureAsync(registration.EditionUuid!.Value))!;
    }

    private async Task AssertSettled(SetlistMaterializationAttempt attempt)
    {
        (await materializer.GetPendingRegistrationsAsync(200))
            .Registrations.Should()
            .NotContain(value => value.RegistrationUuid == attempt.Registration.RegistrationUuid);
        (await materializer.GetRecoveryAttemptsAsync(200))
            .Attempts.Should()
            .NotContain(value => value.AttemptUuid == attempt.AttemptUuid);
    }

    private static string Audit(SetlistMaterializationAttempt attempt) =>
        JsonSerializer.Serialize(attempt.Result);

    private sealed record AccountState(
        CatalogSource Catalog,
        Guid ArtistUuid,
        string ArtistKey,
        NormalizationResourceKey Resource,
        NormalizationInputRevision Input,
        NormalizationSetlistProposalBatch Batch,
        string Payload,
        string MediaType,
        string Version
    );
}
