using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [TestCase("accept", "accepted")]
    [TestCase("reject", "rejected")]
    [TestCase("retract", "superseded")]
    public async Task ReviewingOneManagedCreditDoesNotBlockIndependentAcceptedCreditRefresh(
        string action,
        string reviewedState
    )
    {
        var aliceName = UniqueName();
        var bobName = UniqueName();
        var alice = await Person(aliceName);
        var bob = await Person(bobName);
        await ActivatePolicy();
        var source = await Source(aliceName);
        await SetLegacyCredits(source, aliceName, bobName);
        var first = await Resolve(source);
        var accepted = await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);
        accepted
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(accepted)
            );
        var recordingOwner = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
        var proposalOwner = NormalizationPeopleResolutionProcessor.Owner(source.SourceUuid);
        var membership = await fixture.Work.GetInputContributionsAsync(recordingOwner);
        var proposalBefore = await fixture.Work.GetSubjectAsync(proposalOwner);
        var credit = (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Single(
            value => value.Credit.PersonUuid == alice && value.Credit.Role == "taper"
        );

        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                membership.Owner!.SubjectUuid,
                membership.Owner.EditorialRevision,
                credit.Revision,
                action,
                credit.Credit,
                "The operator reviewed this taper credit, independently of transferrer support."
            ),
            Operator
        );

        var reviewed = await fixture.Work.GetInputContributionsAsync(recordingOwner);
        reviewed.Owner!.DesiredGeneration.Should().Be(membership.Owner.DesiredGeneration + 1);
        reviewed.Owner.EditorialRevision.Should().Be(membership.Owner.EditorialRevision + 1);
        reviewed
            .Contributions.Should()
            .BeEquivalentTo(
                membership.Contributions,
                "credit review changes accepted meaning, not the admitted contributor allocation"
            );
        (await fixture.Work.GetSubjectAsync(proposalOwner)).Should().BeEquivalentTo(proposalBefore);
        var protectedCredit = (
            await people.ReadCreditContextAsync(source.SourceUuid)
        ).Credits.Single(value => value.Credit.Uuid == credit.Credit.Uuid);
        protectedCredit.Credit.State.Should().Be(reviewedState);
        protectedCredit.Credit.Basis.Should().Be("reviewed");

        // This second public proposal reaches the current reviewed context. Its recording
        // application must find the prior admitted contributor preparation despite review's
        // generation advance, then independently refresh Bob's still-supported credit.
        var nextProposal = await Resolve(source);
        nextProposal
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var applicationUuid = Guid.NewGuid();
        var refreshed = await applier.ApplyRecordingAsync(
            applicationUuid,
            nextProposal.AttemptUuid
        );

        refreshed
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Review,
                "the reviewed taper remains protected while the transferrer action publishes: {0}",
                ApplicationAudit(refreshed)
            );
        refreshed.Result.Reason.Should().Be("recording_credit_application");
        refreshed.Stages.Should().ContainSingle().Which.Receipt.Should().NotBeNull();
        refreshed.Stages.Single().DecisionOnly.Should().BeFalse();
        var current = await people.ReadCreditContextAsync(source.SourceUuid);
        current
            .Credits.Where(value =>
                value.Credit.State == "accepted" && value.Credit.Role == "transferrer"
            )
            .Should()
            .ContainSingle()
            .Which.Credit.PersonUuid.Should()
            .Be(bob);
        current
            .Credits.Single(value => value.Credit.Uuid == credit.Credit.Uuid)
            .Should()
            .BeEquivalentTo(
                protectedCredit,
                "automatic refresh cannot replace the reviewed taper's UUID, revision, state or evidence"
            );
        (await applier.ApplyRecordingAsync(applicationUuid, nextProposal.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
    }
}
