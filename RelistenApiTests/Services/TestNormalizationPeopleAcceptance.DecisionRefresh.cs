using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task InactiveRegistryHasAStableObservableOwnerWithoutActivatingAPolicy()
    {
        await SetActivePolicy(null);
        var first = await policies.ReadStateAsync();
        var second = await policies.ReadStateAsync();

        first.Active.Should().BeNull();
        second.Should().BeEquivalentTo(first);
        (await policies.ReadActiveAsync()).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(PeopleResolutionPolicyService.Owner))!
            .SubjectUuid.Should()
            .Be(first.OwnerObservation.SubjectUuid);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NegativePolicyDecisionBecomesPendingAfterActivationAndCanBeReprocessed(
        bool unsupportedPolicy
    )
    {
        var name = UniqueName();
        var person = await Person(name);
        if (unsupportedPolicy)
        {
            await ActivatePolicy(inputForm: "unsupported-credit-format");
        }
        else
        {
            await SetActivePolicy(null);
        }
        var source = await Source(name);
        var resolution = await Resolve(source);

        var original = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        original
            .Result!.Reason.Should()
            .Be(
                unsupportedPolicy
                    ? "unsupported_policy_applicability"
                    : "acceptance_policy_inactive"
            );
        AssertDecisionAcknowledged(original);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await IsAcceptancePending(source.SourceUuid)).Should().BeFalse();

        await ActivatePolicy();

        (await IsAcceptancePending(source.SourceUuid)).Should().BeTrue();
        var replay = await applier.ApplyRecordingAsync(
            original.AttemptUuid,
            resolution.AttemptUuid
        );
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be(original.Result.Reason);
        replay.Stages.Should().BeEquivalentTo(original.Stages);
        var current = await Resolve(source);
        var accepted = await applier.ApplyRecordingAsync(Guid.NewGuid(), current.AttemptUuid);
        accepted.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Applied);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(person);
    }

    [TestCase("candidate")]
    [TestCase("source")]
    [TestCase("editorial")]
    public async Task NoCreditActionsRetainDependenciesForLaterChanges(string change)
    {
        await ActivatePolicy();
        var name = UniqueName();
        var source = await Source(name);
        var resolution = await Resolve(source);
        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        AssertDecisionAcknowledged(application);
        application.Result!.Reason.Should().Be("no_accepted_credit_actions");
        (await IsAcceptancePending(source.SourceUuid)).Should().BeFalse();

        if (change == "candidate")
        {
            await Person(name);
        }
        else if (change == "source")
        {
            await SetTaper(source, UniqueName());
        }
        else
        {
            var owner = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("people", "recording", source.SourceUuid)
                )
            )!;
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(
                        new { note = "Fixture editorial correction" }
                    ),
                    "Reconsider unresolved credits",
                    Operator
                ),
                (_, _, _) => Task.CompletedTask
            );
        }

        (await IsAcceptancePending(source.SourceUuid)).Should().BeTrue();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await applier.ApplyRecordingAsync(application.AttemptUuid, resolution.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
    }

    [TestCase("prepared")]
    [TestCase("admitted")]
    [TestCase("published")]
    public async Task DecisionAcknowledgmentResumesItsSavedStageWithoutAcceptedWrites(string phase)
    {
        await SetActivePolicy(null);
        var source = await Source(UniqueName());
        var resolution = await Resolve(source);
        var saved = await PrepareNegativeAcknowledgment(resolution.AttemptUuid);
        var beforeUniverse = (
            await people.ReadCreditContextAsync(source.SourceUuid)
        ).PeopleUniverse;
        var stage = saved.Stages.Single();
        if (phase != "prepared")
        {
            var ticket = await fixture.Work.BeginContributedBatchAsync(
                stage.WorkUuid,
                new("people", "recording", source.SourceUuid),
                saved.Version,
                stage.Contributions,
                stage.ExpectedConfigurationGeneration,
                expectedContributionRevisions: stage.ExpectedContributions
            );
            saved = await applications.AttachTicketAsync(
                saved.AttemptUuid,
                stage.WorkUuid,
                ticket,
                CancellationToken.None
            );
        }
        if (phase == "published")
        {
            (await people.PublishResolutionDecisionAsync(stage.WorkUuid, saved.AttemptUuid))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
        }

        var resumed = await applier.ApplyRecordingAsync(saved.AttemptUuid, resolution.AttemptUuid);

        resumed.Result!.Reason.Should().Be("acceptance_policy_inactive");
        AssertDecisionAcknowledged(resumed);
        resumed.Stages.Single().WorkUuid.Should().Be(stage.WorkUuid);
        resumed.Stages.Single().Contributions.Should().BeEquivalentTo(stage.Contributions);
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        (await IsAcceptancePending(source.SourceUuid)).Should().BeFalse();
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .PeopleUniverse.Should()
            .Be(beforeUniverse);
    }

    [TestCase("candidate")]
    [TestCase("source")]
    [TestCase("policy")]
    public async Task ChangedNegativePreparationIsStaleAndCannotAcknowledgeNewClocks(string change)
    {
        await SetActivePolicy(null);
        var name = UniqueName();
        var source = await Source(name);
        var resolution = await Resolve(source);
        var saved = await PrepareNegativeAcknowledgment(resolution.AttemptUuid);
        var stage = saved.Stages.Single();
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            stage.WorkUuid,
            new("people", "recording", source.SourceUuid),
            saved.Version,
            stage.Contributions,
            stage.ExpectedConfigurationGeneration,
            expectedContributionRevisions: stage.ExpectedContributions
        );
        await applications.AttachTicketAsync(
            saved.AttemptUuid,
            stage.WorkUuid,
            ticket,
            CancellationToken.None
        );
        if (change == "candidate")
        {
            await Person(name);
        }
        else if (change == "source")
        {
            await SetTaper(source, UniqueName());
        }
        else
        {
            await ActivatePolicy();
        }

        var result = await applier.ApplyRecordingAsync(saved.AttemptUuid, resolution.AttemptUuid);

        result.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        result.Result.Publications.Should().BeEmpty();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await IsAcceptancePending(source.SourceUuid)).Should().BeTrue();
    }

    [Test]
    public async Task DecisionPreservesDistinctPublishedPeerInputsAndDoesNotInheritRemovedKeys()
    {
        await SetActivePolicy(null);
        var source = await Source(UniqueName());
        var input = await fixture.Capture("Independent peer evidence", "people");
        var policy = await policies.ReadStateAsync();
        var universe = (await people.ReadCreditContextAsync(source.SourceUuid)).PeopleUniverse;
        var owner = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
        var peerKey = new NormalizationResourceKey("test", "peer", Guid.NewGuid().ToString("D"));
        var peer = new NormalizationPreparedInputContribution(
            new(
                peerKey,
                0,
                [input.Resource],
                [policy.OwnerObservation.SubjectUuid],
                [policy.OwnerObservation.SubjectUuid]
            ),
            [new(input.Reference.InputRevisionUuid, input.Resource)],
            CandidateUniverses: [universe],
            ExpectedOwnerObservations: [policy.OwnerObservation]
        );
        var peerWork = Guid.NewGuid();
        await fixture.Work.BeginContributedBatchAsync(
            peerWork,
            owner,
            NormalizationPeopleResolutionApplier.Version,
            [peer]
        );
        await fixture.Work.TryPublishAsync(peerWork, (_, _, _) => Task.CompletedTask);
        var before = await fixture.Work.GetInputContributionsAsync(owner);
        var resolution = await Resolve(source);

        var acknowledged = await applier.ApplyRecordingAsync(
            Guid.NewGuid(),
            resolution.AttemptUuid
        );

        AssertDecisionAcknowledged(acknowledged);
        var after = await fixture.Work.GetInputContributionsAsync(owner);
        var preservedPeer = after.Contributions.Single(value => value.Contributor == peerKey);
        var originalPeer = before.Contributions.Single();
        preservedPeer
            .Should()
            .BeEquivalentTo(originalPeer, options => options.Excluding(value => value.Revision));
        preservedPeer.Revision.Should().Be(originalPeer.Revision + 1);
        acknowledged.Stages.Single().PeerTicket!.WorkUuid.Should().Be(peerWork);
        (await IsAcceptancePending(source.SourceUuid)).Should().BeFalse();
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .PeopleUniverse.Should()
            .Be(universe);

        // The peer producer clears its own membership. The next acceptance preparation must
        // consume that published configuration, rather than inherit the older resource union.
        var current = after.Owner!;
        var observation = new NormalizationSubjectObservation(
            current.SubjectUuid,
            current.DesiredGeneration,
            current.EditorialRevision,
            current.PublishedWorkUuid
        );
        var replacements = acknowledged
            .Stages.Single()
            .Contributions.Select(value =>
                value.Contribution.Contributor == peerKey
                    ? new NormalizationPreparedInputContribution(
                        new(
                            peerKey,
                            after
                                .Contributions.Single(item => item.Contributor == peerKey)
                                .Revision,
                            [],
                            []
                        ),
                        []
                    )
                    : value with
                    {
                        Contribution = value.Contribution with
                        {
                            ExpectedRevision = after
                                .Contributions.Single(item =>
                                    item.Contributor == value.Contribution.Contributor
                                )
                                .Revision,
                        },
                        ExpectedOwnerObservations = (value.ExpectedOwnerObservations ?? [])
                            .Select(stamp =>
                                stamp.SubjectUuid == current.SubjectUuid ? observation : stamp
                            )
                            .ToArray(),
                    }
            )
            .ToArray();
        var removal = Guid.NewGuid();
        await fixture.Work.BeginContributedBatchAsync(
            removal,
            owner,
            NormalizationPeopleResolutionApplier.Version,
            replacements,
            current.DesiredGeneration,
            expectedContributionRevisions: after
                .Contributions.Select(value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                ))
                .ToArray()
        );
        await fixture.Work.TryPublishAsync(removal, (_, _, _) => Task.CompletedTask);
        var laterResolution = await Resolve(source);
        var later = await applier.ApplyRecordingAsync(Guid.NewGuid(), laterResolution.AttemptUuid);

        AssertDecisionAcknowledged(later);
        later
            .Stages.Single()
            .Ticket!.Inputs.Should()
            .NotContain(value => value.RevisionUuid == input.Reference.InputRevisionUuid);
        (await fixture.Work.GetInputContributionsAsync(owner))
            .Contributions.Single(value => value.Contributor == peerKey)
            .Resources.Should()
            .BeEmpty();
    }

    [Test]
    public async Task PeerInputReturningToTheSameRevisionCannotAcknowledgeAnOlderGeneration()
    {
        await SetActivePolicy(null);
        var source = await Source(UniqueName());
        var input = await fixture.Capture("Original peer observation", "people");
        var evidenceStore = new NormalizationEvidenceService(fixture.Db);
        var original = (await evidenceStore.GetCurrentInputAsync(input.Resource))!;
        var owner = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
        var peerKey = new NormalizationResourceKey("test", "peer", Guid.NewGuid().ToString("D"));
        var peerWork = Guid.NewGuid();
        var originalTicket = await fixture.Work.BeginContributedBatchAsync(
            peerWork,
            owner,
            NormalizationPeopleResolutionApplier.Version,
            [
                new(
                    new(peerKey, 0, [input.Resource], []),
                    [new(original.Revision.Uuid, input.Resource)]
                ),
            ]
        );
        await fixture.Work.TryPublishAsync(peerWork, (_, _, _) => Task.CompletedTask);
        var resolution = await Resolve(source);
        var changed = await evidenceStore.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                input.Resource,
                JsonSerializer.SerializeToUtf8Bytes(new { text = "Different peer observation" }),
                original.Revision.MediaType,
                original.Revision.CaptureVersion,
                original.Generation,
                DateTimeOffset.UtcNow
            )
        );
        var restored = await evidenceStore.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                input.Resource,
                original.Revision.Payload,
                original.Revision.MediaType,
                original.Revision.CaptureVersion,
                changed.Fetch.ResourceGeneration,
                DateTimeOffset.UtcNow
            )
        );
        restored.Revision.Uuid.Should().Be(original.Revision.Uuid);
        restored
            .Fetch.ResourceGeneration.Should()
            .BeGreaterThan(originalTicket.Inputs.Single().Generation);

        var stale = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        stale.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        stale.Result.Publications.Should().BeEmpty();
        (await fixture.Work.GetSubjectAsync(owner))!.PublishedWorkUuid.Should().Be(peerWork);
        (await IsAcceptancePending(source.SourceUuid)).Should().BeTrue();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task OlderUnfinishedApplicationStopsExplicitlyButTerminalHistoryStillReplays()
    {
        await SetActivePolicy(null);
        var source = await Source(UniqueName());
        var resolution = await Resolve(source);
        var oldVersion = NormalizationPeopleResolutionApplier.Version + "/older-fixture";
        var unfinished = await applications.GetOrCreateAsync(
            Guid.NewGuid(),
            resolution.AttemptUuid,
            oldVersion,
            CancellationToken.None
        );

        var stopped = await applier.ApplyRecordingAsync(
            unfinished.AttemptUuid,
            resolution.AttemptUuid
        );

        stopped.Result!.Reason.Should().Be("processing_version_changed");
        stopped.Result.Publications.Should().BeEmpty();
        stopped.Version.Should().Be(oldVersion);
        var terminal = await applications.GetOrCreateAsync(
            Guid.NewGuid(),
            resolution.AttemptUuid,
            oldVersion,
            CancellationToken.None
        );
        terminal = await applications.CompleteAsync(
            terminal,
            PeopleAcceptanceDisposition.Unresolved,
            "legacy_terminal_decision",
            CancellationToken.None
        );

        var replay = await applier.ApplyRecordingAsync(
            terminal.AttemptUuid,
            resolution.AttemptUuid
        );

        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be(terminal.Result!.Reason);
        replay.Version.Should().Be(oldVersion);
        replay.Plan.Should().BeEquivalentTo(terminal.Plan);
        replay.Stages.Should().BeEquivalentTo(terminal.Stages);
    }

    private static void AssertDecisionAcknowledged(PeopleAcceptanceApplication application)
    {
        application.Result!.Publications.Should().ContainSingle().Which.Credits.Should().BeEmpty();
        var stage = application.Stages.Single();
        stage.DecisionOnly.Should().BeTrue();
        stage.Identity.Should().BeNull();
        stage.Reconciliation.Should().BeNull();
        stage.Receipt.Should().NotBeNull();
        stage.PolicyState.Should().NotBeNull();
    }

    private async Task<bool> IsAcceptancePending(Guid sourceUuid)
    {
        Guid? after = null;
        while (true)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(after, 1000);
            if (
                page.Any(value =>
                    value.Subject == new NormalizationSubjectKey("people", "recording", sourceUuid)
                )
            )
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

    private async Task<PeopleAcceptanceApplication> PrepareNegativeAcknowledgment(
        Guid resolutionUuid
    )
    {
        var proposal = (await resolutions.GetAttemptBatchAsync(resolutionUuid))!;
        var policyState = await policies.ReadStateAsync();
        policyState.Active.Should().BeNull();
        var application = await applications.GetOrCreateAsync(
            Guid.NewGuid(),
            resolutionUuid,
            NormalizationPeopleResolutionApplier.Version,
            CancellationToken.None
        );
        application = await applications.SavePlanAsync(
            application.AttemptUuid,
            new(proposal, null, [], policyState, "acceptance_policy_inactive"),
            CancellationToken.None
        );
        var membership = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording", application.SourceUuid)
        );
        var proposalStamp = new NormalizationSubjectObservation(
            proposal.Ticket.SubjectUuid,
            proposal.Ticket.DesiredGeneration,
            proposal.Ticket.EditorialRevision,
            proposal.AttemptUuid
        );
        var stage = new PeopleAcceptanceStage(
            Guid.NewGuid(),
            null,
            null,
            proposal.Preparation,
            [],
            PeopleAcceptanceReadSet.Contributions(
                proposal.Preparation,
                proposalStamp,
                policyState.OwnerObservation,
                membership,
                []
            ),
            membership.Owner?.DesiredGeneration,
            membership
                .Contributions.Select(value => new NormalizationInputContributionRevision(
                    value.Contributor,
                    value.Revision
                ))
                .ToArray(),
            null,
            null,
            PolicyState: policyState,
            DecisionOnly: true
        );
        return await applications.SaveStageAsync(
            application.AttemptUuid,
            stage,
            CancellationToken.None
        );
    }
}
