using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>
/// Trusted review starts with an actual published extraction and explicit identity IDs. These
/// tests assert the retained decision, Work replay and read fences; no proposal or policy grants
/// authority, and review alone never publishes canonical aliases or contributor support.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestSourceActorReview
{
    private const string Operator = "source-review-fixture-operator";
    private const string Reason = "Operator checked the complete source field.";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationRecordingProposalService extractions = null!;
    private NormalizationRecordingProcessor extractor = null!;
    private SourceActorReviewStore store = null!;
    private SourceActorReviewService reviews = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        extractions = new(fixture.Db);
        var evidence = new NormalizationEvidenceService(fixture.Db);
        extractor = new(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new(),
            new(new(), new()),
            fixture.Work,
            extractions,
            new(fixture.Db)
        );
        store = new(fixture.Db);
        reviews = new(fixture.Db, fixture.Work, store);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task FirstRejectionUsesCasZeroAndRetainsTheExactOriginalProof()
    {
        var source = await Source();
        var prepared = await Prepare(source, SourceActorReviewAction.Reject);
        prepared.PriorReview.Should().BeNull();
        prepared.Target.Should().BeNull();
        var before = await Owners(prepared);
        var accepted = await AcceptedState();
        var operation = Guid.NewGuid();

        var result = await reviews.ApplyAsync(
            operation,
            prepared.PreparationUuid,
            Reason,
            Operator
        );

        result.Replayed.Should().BeFalse();
        result.EditorialRevision.Should().Be(prepared.SourceOwner.EditorialRevision + 1);
        var decision = await Decision(prepared.ReviewStateUuid);
        decision.State.Should().Be("rejected");
        decision.ReviewRevision.Should().Be(1);
        decision.SelectedPersonUuid.Should().BeNull();
        decision.LastOperationUuid.Should().Be(operation);
        decision
            .InputRevisionUuid.Should()
            .Be(prepared.OriginalField.Coordinates.InputRevisionUuid);
        await AssertOriginalProof(prepared);
        (await History(prepared.ReviewStateUuid))
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(decision);
        await AssertOnlySourceOwnerAdvanced(prepared, before);
        (await AcceptedState()).Should().Be(accepted);
        (await store.GetPreparationAsync(prepared.PreparationUuid))
            .Should()
            .BeEquivalentTo(prepared);
    }

    [Test]
    public async Task ConcurrentFirstDecisionsCommitOneReceiptAndOneHistoryRow()
    {
        var source = await Source();
        var first = await Prepare(source, SourceActorReviewAction.Reject);
        var second = await Prepare(source, SourceActorReviewAction.Reject);
        var operations = new[] { Guid.NewGuid(), Guid.NewGuid() };

        async Task<Exception?> Apply(Guid operation, SourceActorReviewPreparation preparation)
        {
            try
            {
                await reviews.ApplyAsync(operation, preparation.PreparationUuid, Reason, Operator);
                return null;
            }
            catch (Exception exception) when (IsConflict(exception))
            {
                return exception;
            }
        }
        var results = await Task.WhenAll(Apply(operations[0], first), Apply(operations[1], second));

        results.Should().ContainSingle(result => result == null);
        results.Should().ContainSingle(result => result != null && IsConflict(result));
        var history = await HistoryForClaim(first.Request.Claim);
        history.Should().ContainSingle();
        history[0].State.Should().Be("rejected");
        history[0].ReviewRevision.Should().Be(1);
        (await ReceiptCount(operations)).Should().Be(1);
    }

    [Test]
    public async Task ExplicitOutOfPoolAcceptanceIgnoresUnreadCandidateChangesAndOnlyAdvancesSourceOwner()
    {
        var source = await Source();
        // Deliberately unrelated wording: an explicit human selection does not depend on
        // name retrieval, a machine proposal or an active acceptance policy.
        var person = await Person("Operator selected existing identity " + Guid.NewGuid());
        var prepared = await Prepare(source, SourceActorReviewAction.Accept, person);
        prepared.Target!.OriginalPersonUuid.Should().Be(person.Uuid);
        prepared.ReadSet.CandidateUniverses.Should().BeNullOrEmpty();
        var before = await Owners(prepared);
        await Person("Unrelated candidate added after preparation " + Guid.NewGuid());
        var accepted = await AcceptedState();
        var revision = await PersonRevision(person.Uuid);

        await reviews.ApplyAsync(Guid.NewGuid(), prepared.PreparationUuid, Reason, Operator);

        var decision = await Decision(prepared.ReviewStateUuid);
        decision.State.Should().Be("accepted");
        decision.SelectedPersonUuid.Should().Be(person.Uuid);
        (await PersonRevision(person.Uuid)).Should().Be(revision);
        await AssertOnlySourceOwnerAdvanced(prepared, before);
        (await AcceptedState()).Should().Be(accepted);
    }

    [TestCase("input_head")]
    [TestCase("source_binding")]
    [TestCase("target_revision")]
    public async Task ChangedObservedFactsRefuseApplyWithoutAReceiptOrReviewRow(string change)
    {
        var source = await Source();
        var target = await Person("Selected target " + Guid.NewGuid());
        var prepared = await Prepare(source, SourceActorReviewAction.Accept, target);
        switch (change)
        {
            case "input_head":
                await AdvanceInput(prepared);
                break;
            case "source_binding":
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteAsync(
                        "UPDATE sources SET description=@text WHERE id=@SourceId",
                        new { text = "Changed source catalog context", source.Source.SourceId }
                    )
                );
                break;
            case "target_revision":
                await AddAlias(target);
                break;
        }
        var before = await Owners(prepared);
        var operation = Guid.NewGuid();

        var apply = () => reviews.ApplyAsync(operation, prepared.PreparationUuid, Reason, Operator);

        await AssertConflict(apply);
        (await HistoryForClaim(prepared.Request.Claim)).Should().BeEmpty();
        (await ReceiptCount([operation])).Should().Be(0);
        (await Owners(prepared)).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task HistoricalRejectionRecordsCurrentHeadSeparatelyWhilePositiveReviewRequiresCurrentSupport()
    {
        var source = await Source();
        var old = await Prepare(source, SourceActorReviewAction.Reject);
        var current = await AdvanceInput(old);
        var historical = await Prepare(source, SourceActorReviewAction.Reject);
        historical
            .OriginalField.Coordinates.InputRevisionUuid.Should()
            .Be(old.Request.Claim.InputRevisionUuid);
        historical.ReadSet.Inputs.Should().Contain(input => input.RevisionUuid == current);
        historical
            .ReadSet.Inputs.Should()
            .NotContain(input => input.RevisionUuid == old.Request.Claim.InputRevisionUuid);
        var target = await Person("Historical positive target " + Guid.NewGuid());

        var positive = () => Prepare(source, SourceActorReviewAction.Accept, target);
        await AssertConflict(positive);
        await reviews.ApplyAsync(Guid.NewGuid(), historical.PreparationUuid, Reason, Operator);

        (await Decision(historical.ReviewStateUuid))
            .InputRevisionUuid.Should()
            .Be(old.Request.Claim.InputRevisionUuid);
        (await HistoryForClaim(old.Request.Claim)).Should().ContainSingle();
    }

    [Test]
    public async Task RestoreReassignAndRetractRetainEveryStateAndMappedReceipt()
    {
        var source = await Source();
        var first = await Person("First choice " + Guid.NewGuid());
        var second = await Person("Replacement " + Guid.NewGuid());
        var transitions = new[]
        {
            (SourceActorReviewAction.Reject, (PersonFixture?)null, "rejected", "reject"),
            (SourceActorReviewAction.Accept, first, "accepted", "accept"),
            (SourceActorReviewAction.Reassign, second, "accepted", "replace"),
            (SourceActorReviewAction.Retract, (PersonFixture?)null, "retracted", "retract"),
            (SourceActorReviewAction.Accept, first, "accepted", "accept"),
        };
        Guid? stateUuid = null;
        for (var index = 0; index < transitions.Length; index++)
        {
            var (action, target, state, receiptAction) = transitions[index];
            var prepared = await Prepare(source, action, target);
            stateUuid ??= prepared.ReviewStateUuid;
            prepared.ReviewStateUuid.Should().Be(stateUuid.Value);
            prepared.Request.ExpectedSourceReviewRevision.Should().Be(index);
            var operation = Guid.NewGuid();

            await reviews.ApplyAsync(operation, prepared.PreparationUuid, Reason, Operator);

            var saved = await Decision(stateUuid.Value);
            saved.State.Should().Be(state);
            saved.SelectedPersonUuid.Should().Be(target?.Uuid);
            saved.ReviewRevision.Should().Be(index + 1);
            (await ReceiptAction(operation)).Should().Be(receiptAction);
        }
        var history = await History(stateUuid!.Value);
        history
            .Select(row => row.State)
            .Should()
            .Equal(transitions.Select(transition => transition.Item3));
        history.Select(row => row.ReviewRevision).Should().Equal(1L, 2L, 3L, 4L, 5L);
    }

    [TestCase("field_hash")]
    [TestCase("actor_reference")]
    [TestCase("resource")]
    [TestCase("input_revision")]
    [TestCase("producer")]
    public async Task ClaimSelectorsMustMatchTheCompletePublishedOriginal(string change)
    {
        var source = await Source();
        var owner = (await fixture.Work.GetSubjectAsync(SourceOwner(source.Source.SourceUuid)))!;
        var claim = change switch
        {
            "field_hash" => source.Claim with { FieldSha256 = new string('0', 64) },
            "actor_reference" => source.Claim with { ActorReferenceUuid = Guid.NewGuid() },
            "resource" => source.Claim with { ResourceUuid = Guid.NewGuid() },
            "input_revision" => source.Claim with { InputRevisionUuid = Guid.NewGuid() },
            "producer" => source.Claim with { ProducerVersion = "unsupported-producer" },
            _ => throw new AssertionException("Unknown selector case."),
        };
        var preparationUuid = Guid.NewGuid();
        var prepare = () =>
            reviews.PrepareAsync(
                preparationUuid,
                new(
                    source.Extraction.AttemptUuid,
                    claim,
                    SourceActorReviewAction.Reject,
                    owner.EditorialRevision,
                    0
                )
            );

        var exception = await prepare.Should().ThrowAsync<Exception>();

        HasArgumentRejection(exception.Which).Should().BeTrue();
        (await store.GetPreparationAsync(preparationUuid)).Should().BeNull();
        (await HistoryForClaim(source.Claim)).Should().BeEmpty();
    }

    [Test]
    public async Task PreparationRetryKeepsOriginalObservationsAndRejectsChangedRequest()
    {
        var source = await Source();
        var prepared = await Prepare(source, SourceActorReviewAction.Reject);
        await AdvanceInput(prepared);
        var restarted = new SourceActorReviewService(fixture.Db, fixture.Work, new(fixture.Db));

        var retry = await restarted.PrepareAsync(prepared.PreparationUuid, prepared.Request);
        retry.Should().BeEquivalentTo(prepared);
        var changed = () =>
            restarted.PrepareAsync(
                prepared.PreparationUuid,
                prepared.Request with
                {
                    Action = SourceActorReviewAction.Retract,
                }
            );
        await AssertConflict(changed);
        (await store.GetPreparationAsync(prepared.PreparationUuid))
            .Should()
            .BeEquivalentTo(prepared);
    }

    [Test]
    public async Task CommittedReplayPrecedesFreshnessValidationButChangedCommandConflicts()
    {
        var source = await Source();
        var target = await Person("Replay target " + Guid.NewGuid());
        var prepared = await Prepare(source, SourceActorReviewAction.Accept, target);
        var alternative = await reviews.PrepareAsync(Guid.NewGuid(), prepared.Request);
        var operation = Guid.NewGuid();
        var committed = await reviews.ApplyAsync(
            operation,
            prepared.PreparationUuid,
            Reason,
            Operator
        );
        await AdvanceInput(prepared);
        await AddAlias(target);
        var after = await Owners(prepared);
        var restarted = new SourceActorReviewService(fixture.Db, fixture.Work, new(fixture.Db));

        (await restarted.ApplyAsync(operation, prepared.PreparationUuid, Reason, Operator))
            .Should()
            .Be(committed with { Replayed = true });
        var changedReason = () =>
            restarted.ApplyAsync(operation, prepared.PreparationUuid, "Changed reason", Operator);
        await AssertConflict(changedReason);
        var changedOperator = () =>
            restarted.ApplyAsync(operation, prepared.PreparationUuid, Reason, "another operator");
        await AssertConflict(changedOperator);
        var changedPreparation = () =>
            restarted.ApplyAsync(operation, alternative.PreparationUuid, Reason, Operator);
        await AssertConflict(changedPreparation);
        (await History(prepared.ReviewStateUuid)).Should().ContainSingle();
        (await Owners(prepared)).Should().BeEquivalentTo(after);
    }
}
