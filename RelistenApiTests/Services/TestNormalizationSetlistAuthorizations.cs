using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

/// <summary>Exact correspondence and removal authority do not replace accepted metadata or refresh historical evidence.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistAuthorizations
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationSetlistProcessor processor = null!;
    private NormalizedMusicService music = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        processor = new(new(fixture.Db), evidence, new(fixture.Db), new(), new(), fixture.Work);
        music = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ExactApprovalEnablesAutomaticRefreshAndLaterMetadataChangesWithoutReviewingMetadata()
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var image = Image(revised, scenario.Image);
        Func<Task> unsupported = () => Publish(revised, image);
        await unsupported.Should().ThrowAsync<ArgumentException>();
        var before = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!;
        var grants = Approvals(image, 1);
        var command = Decision(scenario, grants);

        var approved = await music.ReviewSetlistCorrespondenceAsync(command, "test-operator");

        approved.Replayed.Should().BeFalse();
        (await music.GetSetlistStructureAsync(image.Edition.Uuid)).Should().BeEquivalentTo(before);
        var history = await music.GetSetlistAuthorizationsAsync(image.Edition.Uuid);
        history
            .Should()
            .HaveCount(4)
            .And.OnlyContain(record =>
                record.Active && record.Action == SetlistAuthorizationAction.Authorize
            );
        history
            .Should()
            .OnlyContain(record =>
                record.OriginalSourceTickets.Count == 1
                && record.OriginalSourceTickets[0].WorkUuid == revised.Batch.AttemptUuid
            );
        var authorized = Attach(image, grants);
        (await Publish(revised, authorized))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var current = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!;
        current.Value.Basis.Should().Be("provider_assertion");
        current.Value.Edition.Basis.Should().Be("provider_assertion");
        current
            .Value.Occurrences.Should()
            .OnlyContain(occurrence => occurrence.Basis == "provider_assertion");

        var next = Image(revised, current.Value, current.Revision);
        next = Attach(next, grants) with
        {
            Occurrences = [next.Occurrences[0] with { Kind = "jam" }, .. next.Occurrences.Skip(1)],
        };
        (await Publish(revised, next)).Status.Should().Be(NormalizationPublicationStatus.Published);
        var later = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!.Value;
        later.Occurrences[0].Kind.Should().Be("jam");
        later
            .PassageCorrespondence.Select(map => map.AuthorizationUuid)
            .Should()
            .Equal(authorized.PassageCorrespondence.Select(map => map.AuthorizationUuid));
        later.Basis.Should().Be("provider_assertion");
    }

    [TestCase(SetlistAuthorizationAction.Block)]
    [TestCase(SetlistAuthorizationAction.Revoke)]
    public async Task ActiveNegativeHeadsPreventAnOlderAcceptedCorrespondenceFromRestoringAuthority(
        SetlistAuthorizationAction action
    )
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var image = Image(revised, scenario.Image);
        var grants = Approvals(image, 1);
        await music.ReviewSetlistCorrespondenceAsync(Decision(scenario, grants), "test-operator");
        image = Attach(image, grants);
        await Publish(revised, image);
        var current = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!;
        var originalGrant = grants.Single(grant =>
            grant.Claim.ProposalUuid == image.PassageCorrespondence[0].ProposalUuid
        );
        var negative = originalGrant with
        {
            AuthorizationUuid = Guid.NewGuid(),
            Action = action,
            ExpectedPriorAuthorizationUuid = originalGrant.AuthorizationUuid,
            Targets = [new(image.Occurrences[0].Uuid, 2)],
        };
        await music.ReviewSetlistCorrespondenceAsync(
            Decision(scenario, [negative], editorial: 1, revision: current.Revision),
            "test-operator"
        );
        var replayedSource = await Revise(revised);
        replayedSource.Input.Uuid.Should().Be(revised.Input.Uuid);
        replayedSource.Batch.AttemptUuid.Should().NotBe(revised.Batch.AttemptUuid);
        var next = Image(replayedSource, current.Value, current.Revision);
        // A fresh receipt for the same immutable input uses accepted history; the originating
        // authorization head must still be active before that history can justify reuse.
        Func<Task> inherited = () => Publish(replayedSource, next);

        await inherited
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*historical correspondence*");

        (await music.GetSetlistStructureAsync(image.Edition.Uuid))!
            .Revision.Should()
            .Be(current.Revision);
        var history = await music.GetSetlistAuthorizationsAsync(image.Edition.Uuid);
        history
            .Single(record => record.AuthorizationUuid == negative.AuthorizationUuid)
            .Active.Should()
            .BeTrue();
        history
            .Single(record => record.AuthorizationUuid == originalGrant.AuthorizationUuid)
            .Active.Should()
            .BeFalse();
    }

    [Test]
    public async Task ReplacingAnAuthorizationRequiresItsExactCurrentHead()
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var grants = Approvals(Image(revised, scenario.Image), 1);
        await music.ReviewSetlistCorrespondenceAsync(Decision(scenario, grants), "test-operator");
        var stale = grants[0] with
        {
            AuthorizationUuid = Guid.NewGuid(),
            Action = SetlistAuthorizationAction.Block,
        };
        Func<Task> replace = () =>
            music.ReviewSetlistCorrespondenceAsync(
                Decision(scenario, [stale], editorial: 1),
                "test-operator"
            );

        await replace.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetSetlistAuthorizationsAsync(scenario.Image.Edition.Uuid))
            .Should()
            .HaveCount(4)
            .And.OnlyContain(record => record.Active);
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(1);
    }

    [TestCase("target")]
    [TestCase("structure")]
    [TestCase("edition")]
    public async Task AuthorizationRequiresTheExactTargetAndAcceptedMetadataRevisions(
        string changed
    )
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var grants = Approvals(Image(revised, scenario.Image), 1);
        var command = Decision(scenario, [grants[0]]);
        command = changed switch
        {
            "target" => command with
            {
                Correspondence =
                [
                    grants[0] with
                    {
                        Targets = [grants[0].Targets[0] with { ExpectedRevision = 99 }],
                    },
                ],
            },
            "structure" => command with { ExpectedStructureRevision = 99 },
            "edition" => command with { ExpectedEditionRevision = 99 },
            _ => throw new ArgumentOutOfRangeException(nameof(changed)),
        };
        Func<Task> review = () => music.ReviewSetlistCorrespondenceAsync(command, "test-operator");

        if (changed == "target")
        {
            await review.Should().ThrowAsync<NormalizationConflictException>();
        }
        else
        {
            await review.Should().ThrowAsync<ArgumentException>();
        }
        (await music.GetSetlistAuthorizationsAsync(scenario.Image.Edition.Uuid)).Should().BeEmpty();
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(0);
    }

    [TestCase("forged_account")]
    [TestCase("unrelated_citation")]
    public async Task AuthorizationRequiresTheExactPublishedAccountAndItsClaimCitation(string fault)
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var grant = Approvals(Image(revised, scenario.Image), 1)[0];
        grant =
            fault == "forged_account"
                ? grant with
                {
                    Source = grant.Source with
                    {
                        Account = grant.Source.Account with
                        {
                            Account = grant.Source.Account.Account with
                            {
                                LiteralDate = grant.Source.Account.Account.LiteralDate! with
                                {
                                    Value = "Invented date",
                                },
                            },
                        },
                    },
                }
                : grant with
                {
                    Evidence = [grant.Source.Account.Account.Origin.Evidence],
                };
        Func<Task> review = () =>
            music.ReviewSetlistCorrespondenceAsync(Decision(scenario, [grant]), "test-operator");

        await review.Should().ThrowAsync<ArgumentException>();

        (await music.GetSetlistAuthorizationsAsync(scenario.Image.Edition.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task ExactDecisionReplayPreservesOneReceiptAndChangedCommandsCannotReuseItsOperationUuid()
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var command = Decision(scenario, Approvals(Image(revised, scenario.Image), 1));
        await music.ReviewSetlistCorrespondenceAsync(command, "test-operator");

        (await music.ReviewSetlistCorrespondenceAsync(command, "test-operator"))
            .Replayed.Should()
            .BeTrue();
        Func<Task> changed = () =>
            music.ReviewSetlistCorrespondenceAsync(
                command with
                {
                    Reason = "Different decision payload",
                },
                "test-operator"
            );
        await changed.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetSetlistAuthorizationsAsync(scenario.Image.Edition.Uuid))
            .Should()
            .HaveCount(4);
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(1);
        (await music.GetSetlistStructureAsync(scenario.Image.Edition.Uuid))!
            .Revision.Should()
            .Be(1);
    }

    [Test]
    public async Task LaterInvalidTargetRollsBackEarlierAuthorizationAndTheEditorialReceipt()
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var grants = Approvals(Image(revised, scenario.Image), 1);
        var bad = grants[1] with
        {
            Targets = [grants[1].Targets[0] with { ExpectedRevision = 99 }],
        };
        var command = Decision(scenario, [grants[0], bad]);
        Func<Task> partial = () => music.ReviewSetlistCorrespondenceAsync(command, "test-operator");

        await partial.Should().ThrowAsync<NormalizationConflictException>();
        (await music.GetSetlistAuthorizationsAsync(scenario.Image.Edition.Uuid)).Should().BeEmpty();
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(0);

        var repaired = command with { Correspondence = [grants[0], grants[1]] };
        (await music.ReviewSetlistCorrespondenceAsync(repaired, "test-operator"))
            .Replayed.Should()
            .BeFalse();
        (await music.GetSetlistAuthorizationsAsync(scenario.Image.Edition.Uuid))
            .Should()
            .HaveCount(2);
    }

    [Test]
    public async Task HistoricalInterpretationCanBeApprovedButCannotPublishWithAChangedRawHead()
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var image = Image(revised, scenario.Image);
        var grants = Approvals(image, 1);
        await Page(
            revised.Resource,
            revised.ArtistKey,
            revised.AccountKey,
            "A newer unprocessed source response"
        );

        await music.ReviewSetlistCorrespondenceAsync(Decision(scenario, grants), "test-operator");
        Func<Task> stale = () => Publish(revised, Attach(image, grants));
        await stale.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetSetlistAuthorizationsAsync(image.Edition.Uuid)).Should().HaveCount(4);
        (await music.GetSetlistStructureAsync(image.Edition.Uuid))!.Revision.Should().Be(1);
    }

    [Test]
    public async Task ExactRemovalApprovalAllowsAutomaticTombstonesWithoutChangingMetadataAuthority()
    {
        var scenario = await Accepted();
        var removal = await PrepareRemoval(scenario);
        var before = (await music.GetSetlistStructureAsync(scenario.Image.Edition.Uuid))!;

        await music.ReviewSetlistCorrespondenceAsync(removal.Command, "test-operator");

        (await music.GetSetlistStructureAsync(scenario.Image.Edition.Uuid))
            .Should()
            .BeEquivalentTo(before);
        (await Publish(removal.Source, removal.Image))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var accepted = (await music.GetSetlistStructureAsync(scenario.Image.Edition.Uuid))!.Value;
        accepted.Basis.Should().Be("provider_assertion");
        accepted.Edition.Basis.Should().Be("provider_assertion");
        accepted
            .Occurrences.Should()
            .OnlyContain(occurrence => occurrence.Basis == "provider_assertion");
        accepted
            .Sets.Single(set => set.Uuid == scenario.Image.Sets[1].Uuid)
            .Retracted.Should()
            .BeTrue();
        accepted
            .Occurrences.Single(occurrence => occurrence.Uuid == scenario.Image.Occurrences[1].Uuid)
            .Retracted.Should()
            .BeTrue();
        (await music.GetOccurrencesAsync(accepted.Edition.Uuid)).Should().ContainSingle();
        accepted.RemovalAuthorizations.Should().HaveCount(2);
    }

    [Test]
    public async Task AConsumedRemovalCannotRetireALaterReactivationEvenThroughTheReviewedRoute()
    {
        var scenario = await Accepted();
        var removal = await PrepareRemoval(scenario);
        await music.ReviewSetlistCorrespondenceAsync(removal.Command, "test-operator");
        await Publish(removal.Source, removal.Image);
        var reactivated = Image(scenario.Source, scenario.Image, 2);
        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            scenario.Owner.SubjectUuid,
            1,
            reactivated,
            "Reviewed reactivation of the retained second passage and set",
            "test-operator"
        );
        var repeatedRemoval = removal.Image with
        {
            ExpectedRevision = 3,
            Edition = removal.Image.Edition with { ExpectedRevision = 3 },
            // Full-image review supplies correspondence authority here. Keep only the old
            // removal grants so this rejection cannot be caused by a stale mapping grant.
            SetCorrespondence = removal
                .Image.SetCorrespondence.Select(map => map with { AuthorizationUuid = null })
                .ToArray(),
            PassageCorrespondence = removal
                .Image.PassageCorrespondence.Select(map => map with { AuthorizationUuid = null })
                .ToArray(),
            Occurrences = removal
                .Image.Occurrences.Select(occurrence => occurrence with { ExpectedRevision = 3 })
                .ToArray(),
        };
        Func<Task> oldGrant = () =>
            music.ApplySetlistStructureDecisionAsync(
                Guid.NewGuid(),
                scenario.Owner.SubjectUuid,
                2,
                repeatedRemoval,
                "Attempt to reuse an earlier removal after reactivation",
                "test-operator"
            );

        await oldGrant.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetSetlistStructureAsync(scenario.Image.Edition.Uuid))!
            .Revision.Should()
            .Be(3);
        (await music.GetOccurrencesAsync(scenario.Image.Edition.Uuid)).Should().HaveCount(2);
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(2);
    }

    [Test]
    public async Task FirstStructureApprovalCanReuseALegacyOccurrenceWithoutReviewingEditionMetadata()
    {
        var source = await Source();
        var image = Image(source);
        var legacyOccurrence = image.Occurrences[0] with { SetUuid = null };
        var owner = await Admit(source, image);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication { Editions = [image.Edition], Occurrences = [legacyOccurrence] }
        );
        image = image with
        {
            Edition = image.Edition with { ExpectedRevision = 1 },
            Occurrences =
            [
                image.Occurrences[0] with
                {
                    ExpectedRevision = 1,
                },
                image.Occurrences[1],
            ],
        };
        var scenario = new Scenario(source, image, owner);
        var sourceAccount = image.Sources.Single();
        var passage = sourceAccount.Account.Passages[0];
        var grant = new SetlistCorrespondenceApproval(
            Guid.NewGuid(),
            sourceAccount,
            new(ProposalUuid: passage.ProposalUuid),
            SetlistAuthorizationAction.Authorize,
            [new(legacyOccurrence.Uuid, 1)],
            null,
            [passage.Passage.Title.Evidence]
        );
        var command = Decision(scenario, [grant]) with { ExpectedStructureRevision = 0 };

        await music.ReviewSetlistCorrespondenceAsync(command, "test-operator");

        (await music.GetSetlistStructureAsync(image.Edition.Uuid)).Should().BeNull();
        (await music.GetEditionAsync(image.Edition.Uuid))!
            .Value.Basis.Should()
            .Be("provider_assertion");
        image = image with
        {
            PassageCorrespondence =
            [
                image.PassageCorrespondence[0] with
                {
                    AuthorizationUuid = grant.AuthorizationUuid,
                },
                image.PassageCorrespondence[1],
            ],
        };
        (await Publish(source, image)).Status.Should().Be(NormalizationPublicationStatus.Published);
        var accepted = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!;
        accepted.Revision.Should().Be(1);
        accepted.Value.Basis.Should().Be("provider_assertion");
        accepted.Value.Occurrences[0].Uuid.Should().Be(legacyOccurrence.Uuid);
        accepted
            .Value.Occurrences.Should()
            .OnlyContain(occurrence => occurrence.Basis == "provider_assertion");
    }

    [Test]
    public async Task AnUnusedGrantCannotFirstBindATargetWhoseRevisionChangedAfterApproval()
    {
        var scenario = await Accepted();
        var revised = await Revise(scenario.Source);
        var image = Image(revised, scenario.Image);
        var grant = Approvals(image, 1)
            .Single(item => item.Claim.ProposalUuid == image.PassageCorrespondence[0].ProposalUuid);
        await music.ReviewSetlistCorrespondenceAsync(Decision(scenario, [grant]), "test-operator");
        var changed = Image(scenario.Source, scenario.Image) with
        {
            Occurrences =
            [
                scenario.Image.Occurrences[0] with
                {
                    ExpectedRevision = 1,
                    Kind = "jam",
                },
                scenario.Image.Occurrences[1] with
                {
                    ExpectedRevision = 1,
                },
            ],
        };
        await music.ApplySetlistStructureDecisionAsync(
            Guid.NewGuid(),
            scenario.Owner.SubjectUuid,
            1,
            changed,
            "Review a metadata change before the unused grant is consumed",
            "test-operator"
        );
        var stale = Image(revised, scenario.Image, 2);
        stale = stale with
        {
            PassageCorrespondence =
            [
                stale.PassageCorrespondence[0] with
                {
                    AuthorizationUuid = grant.AuthorizationUuid,
                },
                stale.PassageCorrespondence[1],
            ],
        };
        Func<Task> firstUse = () =>
            music.ApplySetlistStructureDecisionAsync(
                Guid.NewGuid(),
                scenario.Owner.SubjectUuid,
                2,
                stale,
                "Attempt to first consume the earlier target revision",
                "test-operator"
            );

        await firstUse
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*target changed*");

        var retained = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!;
        retained.Revision.Should().Be(2);
        retained.Value.Occurrences[0].Kind.Should().Be("jam");
        retained
            .Value.PassageCorrespondence.Should()
            .OnlyContain(map => map.AuthorizationUuid == null);
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(2);
    }

    [TestCase(SetlistAuthorizationAction.Block)]
    [TestCase(SetlistAuthorizationAction.Revoke)]
    public async Task HistoricalGrantsCanBeDisabledWithoutReinterpretingAnUnsupportedProcessingVersion(
        SetlistAuthorizationAction action
    )
    {
        var scenario = await Accepted();
        var historical = await HistoricalSource(scenario.Source);
        var image = Image(historical, scenario.Image);
        var grant = Approvals(image, 1)
            .Single(item => item.Claim.ProposalUuid == image.PassageCorrespondence[0].ProposalUuid);
        // This is immutable state from a prior executable version. Seed its event and receipt
        // through shared work rather than changing the published proposal or an existing event.
        await SeedHistoricalGrant(scenario, historical, grant);
        var negative = grant with
        {
            AuthorizationUuid = Guid.NewGuid(),
            Action = action,
            ExpectedPriorAuthorizationUuid = grant.AuthorizationUuid,
        };

        await music.ReviewSetlistCorrespondenceAsync(
            Decision(scenario, [negative], editorial: 1),
            "test-operator"
        );

        var history = await music.GetSetlistAuthorizationsAsync(image.Edition.Uuid);
        history.Should().HaveCount(2);
        history
            .Single(item => item.AuthorizationUuid == grant.AuthorizationUuid)
            .Active.Should()
            .BeFalse();
        var active = history.Single(item => item.AuthorizationUuid == negative.AuthorizationUuid);
        active.Active.Should().BeTrue();
        active.Action.Should().Be(action);
        active.Correspondence!.Source.Should().BeEquivalentTo(grant.Source);
        active
            .OriginalSourceTickets.Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(historical.Batch.Ticket);
        active
            .OriginalSourceTickets[0]
            .PolicyVersion.Should()
            .Be(historical.Batch.ProcessingVersion);
        var reinterpretation = negative with
        {
            AuthorizationUuid = Guid.NewGuid(),
            Action = SetlistAuthorizationAction.Authorize,
            ExpectedPriorAuthorizationUuid = negative.AuthorizationUuid,
        };
        Func<Task> authorize = () =>
            music.ReviewSetlistCorrespondenceAsync(
                Decision(scenario, [reinterpretation], editorial: 2),
                "test-operator"
            );
        await authorize.Should().ThrowAsync<NotSupportedException>();
        (await music.GetSetlistAuthorizationsAsync(image.Edition.Uuid)).Should().HaveCount(2);
        (await music.GetSetlistStructureAsync(image.Edition.Uuid))!.Revision.Should().Be(1);
        (await fixture.Work.GetSubjectAsync(scenario.Owner.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(2);
    }

    [Test]
    public async Task AnAcceptedSplitRetainsItsCompleteAuthorizedTargetListThroughHistoricalReuse()
    {
        var scenario = await Accepted(unordered: true);
        var revised = await Revise(scenario.Source);
        var image = Unordered(Image(revised, scenario.Image));
        var additional = image.Occurrences[0] with { Uuid = Guid.NewGuid(), ExpectedRevision = 0 };
        image = image with
        {
            Occurrences = [.. image.Occurrences, additional],
            PassageCorrespondence =
            [
                image.PassageCorrespondence[0] with
                {
                    OccurrenceUuids = [image.Occurrences[0].Uuid, additional.Uuid],
                },
                image.PassageCorrespondence[1],
            ],
        };
        var grants = Approvals(image, 1);
        var splitIndex = Array.FindIndex(
            grants,
            grant => grant.Claim.ProposalUuid == image.PassageCorrespondence[0].ProposalUuid
        );
        grants[splitIndex] = grants[splitIndex] with
        {
            Targets = [new(image.Occurrences[0].Uuid, 1), new(additional.Uuid, 0)],
        };
        await music.ReviewSetlistCorrespondenceAsync(Decision(scenario, grants), "test-operator");
        await Publish(revised, Attach(image, grants));
        var current = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!;
        var occurrences = (await music.GetOccurrencesAsync(image.Edition.Uuid)).ToDictionary(
            record => record.Value.Uuid,
            record => record.Revision
        );
        occurrences[additional.Uuid].Should().Be(1);
        var replayedSource = await Revise(revised);
        replayedSource.Input.Uuid.Should().Be(revised.Input.Uuid);
        var next = current.Value with
        {
            ExpectedRevision = current.Revision,
            Edition = current.Value.Edition with { ExpectedRevision = 2 },
            Occurrences = current
                .Value.Occurrences.Select(occurrence =>
                    occurrence with
                    {
                        ExpectedRevision = occurrences[occurrence.Uuid],
                    }
                )
                .ToArray(),
            Sources = Image(replayedSource).Sources,
            SetCorrespondence = current
                .Value.SetCorrespondence.Select(map => map with { AuthorizationUuid = null })
                .ToArray(),
            PassageCorrespondence = current
                .Value.PassageCorrespondence.Select(map => map with { AuthorizationUuid = null })
                .ToArray(),
        };

        (await Publish(replayedSource, next))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var retained = (await music.GetSetlistStructureAsync(image.Edition.Uuid))!.Value;
        retained
            .PassageCorrespondence[0]
            .OccurrenceUuids.Should()
            .Equal(image.Occurrences[0].Uuid, additional.Uuid);
        retained
            .PassageCorrespondence[0]
            .AuthorizationUuid.Should()
            .Be(grants[splitIndex].AuthorizationUuid);
        retained.Occurrences.Should().HaveCount(3);
        retained.Basis.Should().Be("provider_assertion");
    }

    [Test]
    public async Task AnActualCitationFromAnotherAccountOnTheSamePageCannotAuthorizeThisClaim()
    {
        var source = await Source(includeOtherAccount: true);
        var image = Image(source);
        var owner = await Admit(source, image);
        await music.PublishSetlistStructureAsync(owner.WorkUuid, image);
        var scenario = new Scenario(source, image, owner);
        var grant = Approvals(image, 1)[0];
        grant = grant with
        {
            Evidence = [.. grant.Evidence, new(source.Input.Uuid, "/setlist/1/sets/set/0")],
        };
        Func<Task> review = () =>
            music.ReviewSetlistCorrespondenceAsync(Decision(scenario, [grant]), "test-operator");

        await review.Should().ThrowAsync<ArgumentException>();

        (await music.GetSetlistAuthorizationsAsync(image.Edition.Uuid)).Should().BeEmpty();
        (await music.GetSetlistStructureAsync(image.Edition.Uuid))!.Revision.Should().Be(1);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!.EditorialRevision.Should().Be(0);
    }

    private async Task<Scenario> Accepted(bool unordered = false)
    {
        var source = await Source();
        var image = Image(source);
        if (unordered)
        {
            image = Unordered(image);
        }

        var ticket = await Admit(source, image);
        await music.PublishSetlistStructureAsync(ticket.WorkUuid, image);
        return new(source, image, ticket);
    }

    private async Task<RetainedSource> HistoricalSource(RetainedSource source)
    {
        const string version = "setlist-processing-historical-v0";
        var store = new NormalizationSetlistProposalService(fixture.Db);
        var attempt = await store.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            new(
                new(source.Input.Uuid, source.Input.Resource),
                new("setlist.fm", "setlist", source.AccountKey)
            ),
            version
        );
        var owner = new NormalizationSubjectKey(
            "extractions",
            "setlist",
            attempt.Registration.RegistrationUuid
        );
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var inputs = source
            .Batch.Preparation.Inputs.Select(input =>
                input with
                {
                    ExpectedContributionRevision = membership
                        .Contributions.Single(state => state.Contributor == input.Contributor)
                        .Revision,
                }
            )
            .ToArray();
        var preparation = new NormalizationSetlistPreparation(
            membership.Owner!.DesiredGeneration,
            membership
                .Contributions.Select(state => new NormalizationInputContributionRevision(
                    state.Contributor,
                    state.Revision
                ))
                .ToArray(),
            inputs
        );
        await store.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            owner,
            version,
            NormalizationSetlistReadSet.Contributions(preparation),
            preparation.ExpectedConfigurationGeneration,
            expectedContributionRevisions: preparation.ExpectedContributions
        );
        var historical = new NormalizationSetlistProposalBatch(
            attempt.AttemptUuid,
            attempt.Registration.RegistrationUuid,
            version,
            preparation,
            ticket,
            source.Batch.Extractions
        );
        await store.SaveExtractionAsync(historical);
        (
            await fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                (c, t, ct) => store.WritePublishedAsync(c, t, historical, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return source with { Batch = historical };
    }

    private async Task SeedHistoricalGrant(
        Scenario scenario,
        RetainedSource historical,
        SetlistCorrespondenceApproval grant
    )
    {
        var operationUuid = Guid.NewGuid();
        var key =
            $"claim/{grant.Source.ProposalAttemptUuid:D}/{grant.Source.InputRevisionUuid:D}/{grant.Source.AccountIndex}/passage/{grant.Claim.ProposalUuid!.Value:D}";
        await fixture.Work.ApplyReviewAsync(
            new(
                operationUuid,
                scenario.Owner.SubjectUuid,
                0,
                "replace",
                JsonSerializer.SerializeToElement(grant),
                "Retain an authorization issued by a historical executable",
                "test-operator"
            ),
            async (c, t, ct) =>
            {
                await c.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO normalization.setlist_authorization_events
                            (uuid,edition_uuid,operation_uuid,authorization_key,kind,action,
                             expected_structure_revision,expected_edition_revision,prior_uuid,payload,source_tickets)
                        VALUES(@AuthorizationUuid,@editionUuid,@operationUuid,@key,'correspondence','authorize',
                            1,1,NULL,CAST(@payload AS jsonb),CAST(@tickets AS jsonb));
                        INSERT INTO normalization.setlist_authorization_heads
                            (edition_uuid,authorization_key,authorization_uuid)
                        VALUES(@editionUuid,@key,@AuthorizationUuid)
                        """,
                        new
                        {
                            grant.AuthorizationUuid,
                            editionUuid = scenario.Image.Edition.Uuid,
                            operationUuid,
                            key,
                            payload = JsonSerializer.Serialize(grant),
                            tickets = JsonSerializer.Serialize(new[] { historical.Batch.Ticket }),
                        },
                        t,
                        cancellationToken: ct
                    )
                );
            }
        );
    }

    private static SetlistStructureChange Unordered(SetlistStructureChange image) =>
        image with
        {
            Edition = image.Edition with { Ordered = false },
            Sets = image.Sets.Select(set => set with { Position = null }).ToArray(),
            Occurrences = image
                .Occurrences.Select(occurrence => occurrence with { Position = null })
                .ToArray(),
        };

    private async Task<RemovalScenario> PrepareRemoval(Scenario scenario)
    {
        var source = await Revise(scenario.Source, removeSecond: true);
        var image = Image(source, scenario.Image);
        var sourceAccount = image.Sources.Single();
        var sourceEvidence = sourceAccount.Account.Account.Origin.Evidence;
        image = image with
        {
            Sets =
            [
                .. image.Sets,
                scenario.Image.Sets[1] with
                {
                    Retracted = true,
                    Evidence = [sourceEvidence],
                },
            ],
            Occurrences =
            [
                .. image.Occurrences,
                scenario.Image.Occurrences[1] with
                {
                    ExpectedRevision = 1,
                    Retracted = true,
                    Evidence = [sourceEvidence],
                },
            ],
        };
        var grants = Approvals(image, 1);
        SetlistRemovalApproval[] removals =
        [
            new(
                Guid.NewGuid(),
                SetlistRemovalKind.Set,
                new(scenario.Image.Sets[1].Uuid, 1),
                SetlistAuthorizationAction.Authorize,
                [sourceAccount],
                null,
                [sourceEvidence]
            ),
            new(
                Guid.NewGuid(),
                SetlistRemovalKind.Occurrence,
                new(scenario.Image.Occurrences[1].Uuid, 1),
                SetlistAuthorizationAction.Authorize,
                [sourceAccount],
                null,
                [sourceEvidence]
            ),
        ];
        image = Attach(image, grants) with
        {
            RemovalAuthorizations = removals
                .Select(item => new SetlistRemovalAuthorization(
                    item.Kind,
                    item.Target.Uuid,
                    item.AuthorizationUuid
                ))
                .ToArray(),
        };
        return new(source, image, Decision(scenario, grants) with { Removals = removals });
    }

    private static SetlistCorrespondenceApproval[] Approvals(
        SetlistStructureChange image,
        long revision
    )
    {
        var source = image.Sources.Single();
        return image
            .SetCorrespondence.Select(map => new SetlistCorrespondenceApproval(
                Guid.NewGuid(),
                source,
                new(SetIndex: map.SetIndex),
                SetlistAuthorizationAction.Authorize,
                [new(map.SetUuid, revision)],
                null,
                [source.Account.Account.Sets[map.SetIndex].Evidence]
            ))
            .Concat(
                image.PassageCorrespondence.Select(map => new SetlistCorrespondenceApproval(
                    Guid.NewGuid(),
                    source,
                    new(ProposalUuid: map.ProposalUuid),
                    SetlistAuthorizationAction.Authorize,
                    map.OccurrenceUuids.Select(uuid => new SetlistTargetRevision(uuid, revision))
                        .ToArray(),
                    null,
                    [
                        source
                            .Account.Passages.Single(passage =>
                                passage.ProposalUuid == map.ProposalUuid
                            )
                            .Passage.Title.Evidence,
                    ]
                ))
            )
            .ToArray();
    }

    private static SetlistStructureChange Attach(
        SetlistStructureChange image,
        IReadOnlyList<SetlistCorrespondenceApproval> grants
    ) =>
        image with
        {
            SetCorrespondence = image
                .SetCorrespondence.Select(map =>
                    map with
                    {
                        AuthorizationUuid = grants
                            .Single(grant =>
                                grant.Claim.SetIndex == map.SetIndex
                                && grant.Claim.ProposalUuid == null
                            )
                            .AuthorizationUuid,
                    }
                )
                .ToArray(),
            PassageCorrespondence = image
                .PassageCorrespondence.Select(map =>
                    map with
                    {
                        AuthorizationUuid = grants
                            .Single(grant => grant.Claim.ProposalUuid == map.ProposalUuid)
                            .AuthorizationUuid,
                    }
                )
                .ToArray(),
        };

    private static SetlistCorrespondenceDecision Decision(
        Scenario scenario,
        IReadOnlyList<SetlistCorrespondenceApproval> grants,
        long editorial = 0,
        long revision = 1
    ) =>
        new(
            Guid.NewGuid(),
            scenario.Owner.SubjectUuid,
            scenario.Image.Edition.Uuid,
            editorial,
            revision,
            revision,
            grants,
            [],
            "Review exact source correspondence without changing accepted metadata"
        );

    private async Task<NormalizationPublicationResult> Publish(
        RetainedSource source,
        SetlistStructureChange image
    ) => await music.PublishSetlistStructureAsync((await Admit(source, image)).WorkUuid, image);

    private async Task<NormalizationWorkTicket> Admit(
        RetainedSource source,
        SetlistStructureChange image
    )
    {
        var subject = new NormalizationSubjectKey("songs", "setlist", image.Edition.Uuid);
        var state = await fixture.Work.GetSubjectAsync(subject);
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "setlist-authorization-test-v1",
            source.Batch.Preparation.Inputs.Select(input => input.Input.Resource).ToArray(),
            [source.Batch.Ticket.SubjectUuid],
            state?.DesiredGeneration,
            catalogInputs: source.Batch.Ticket.CatalogInputs,
            mappingRegistry: source.Batch.Ticket.MappingRegistry,
            expectedInputRevisions: source
                .Batch.Preparation.Inputs.Select(input => input.Input)
                .ToArray()
        );
    }

    private static SetlistStructureChange Image(
        RetainedSource source,
        SetlistStructureChange? previous = null,
        long revision = 1
    )
    {
        var proposal = source.Batch.Extractions.Single().Accounts.Single();
        var account = proposal.Account;
        var edition = previous?.Edition.Uuid ?? Guid.NewGuid();
        var sets = account
            .Sets.Select(
                (set, index) =>
                    new SetlistStructureSet
                    {
                        Uuid = previous?.Sets[index].Uuid ?? Guid.NewGuid(),
                        Position = set.Ordinal,
                        Label = set.Label?.Value,
                        Evidence = [set.Evidence],
                    }
            )
            .ToArray();
        var occurrences = proposal
            .Passages.Select(
                (passage, index) =>
                    new SongOccurrenceChange
                    {
                        Uuid = previous?.Occurrences[index].Uuid ?? Guid.NewGuid(),
                        ExpectedRevision = previous == null ? 0 : revision,
                        EditionUuid = edition,
                        Position = passage.Passage.Ordinal,
                        SetUuid = sets.Single(set =>
                            set.Position == passage.Passage.SetOrdinal
                        ).Uuid,
                        SetLabel = passage.Passage.SetLabel?.Value,
                        Kind = "unknown",
                        OriginalText = passage.Passage.Title.Value,
                        Evidence = [passage.Passage.Title.Evidence],
                    }
            )
            .ToArray();
        return new()
        {
            Uuid = previous?.Uuid ?? Guid.NewGuid(),
            ExpectedRevision = previous == null ? 0 : revision,
            Edition = new()
            {
                Uuid = edition,
                ExpectedRevision = previous == null ? 0 : revision,
                ArtistUuid = source.ArtistUuid,
                LiteralDate = account.LiteralDate!.Value,
                Ordered = account.Ordered,
                Provider = account.Origin.Item.Provider,
                ResourceType = account.Origin.Item.ResourceType,
                ProviderKey = account.Origin.Item.ProviderKey,
                Evidence = [account.Origin.Evidence],
            },
            Sets = sets,
            Occurrences = occurrences,
            Sources = [new(source.Batch.AttemptUuid, source.Input.Uuid, 0, proposal)],
            SetCorrespondence = sets.Select(
                    (set, index) => new SetlistSetCorrespondence(0, index, set.Uuid)
                )
                .ToArray(),
            PassageCorrespondence = proposal
                .Passages.Select(
                    (passage, index) =>
                        new SetlistPassageCorrespondence(
                            0,
                            passage.ProposalUuid,
                            [occurrences[index].Uuid]
                        )
                )
                .ToArray(),
            Evidence = [account.Origin.Evidence],
        };
    }

    private async Task<RetainedSource> Source(bool includeOtherAccount = false)
    {
        var catalog = await fixture.Source();
        var artistUuid = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", catalog)
        );
        var artistKey = Guid.NewGuid().ToString("D");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='setlist.fm' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'setlist.fm','https://provider.invalid') RETURNING id"
                );
            await c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId; INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@artistKey)",
                new
                {
                    catalog.ArtistId,
                    provider,
                    artistKey,
                }
            );
        });
        var accountKey = "authorization-" + Guid.NewGuid().ToString("N");
        var resource = new NormalizationResourceKey(
            "setlist.fm",
            "setlist-page",
            artistKey + ":page=1"
        );
        var input = await Page(
            resource,
            artistKey,
            accountKey,
            "Original source notes",
            includeOtherAccount: includeOtherAccount
        );
        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            new("setlist.fm", "setlist", accountKey)
        );
        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return new(
            artistUuid,
            artistKey,
            accountKey,
            resource,
            input,
            (await processor.GetAsync(attempt.Registration.RegistrationUuid))!
        );
    }

    private async Task<RetainedSource> Revise(
        RetainedSource source,
        bool removeSecond = false,
        string notes = "Corrected source notes"
    )
    {
        var input = await Page(
            source.Resource,
            source.ArtistKey,
            source.AccountKey,
            notes,
            removeSecond
        );
        var attempt = await processor.ProcessAsync(
            Guid.NewGuid(),
            new(input.Uuid, input.Resource),
            new("setlist.fm", "setlist", source.AccountKey)
        );
        attempt
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        return source with
        {
            Input = input,
            Batch = (await processor.GetAsync(attempt.Registration.RegistrationUuid))!,
        };
    }

    private async Task<NormalizationInputRevision> Page(
        NormalizationResourceKey resource,
        string artistKey,
        string accountKey,
        string notes,
        bool removeSecond = false,
        bool includeOtherAccount = false
    )
    {
        var sets = new List<object>
        {
            new { name = "First", song = new[] { new { name = "First Song" } } },
        };
        if (!removeSecond)
        {
            sets.Add(new { name = "Second", song = new[] { new { name = "Second Song" } } });
        }
        object Account(string id) =>
            new
            {
                id,
                eventDate = "01-01-2000",
                artist = new { mbid = artistKey, name = "Fixture artist" },
                sets = new { set = sets },
                info = notes,
            };
        var accounts = new List<object> { Account(accountKey) };
        if (includeOtherAccount)
        {
            accounts.Add(Account("other-" + accountKey));
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                page = 1,
                itemsPerPage = 20,
                total = accounts.Count,
                setlist = accounts,
            }
        );
        var current = await evidence.GetCurrentInputAsync(resource);
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    payload,
                    "application/json",
                    "setlist-fm-http-v1",
                    current?.Generation ?? 0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private sealed record RetainedSource(
        Guid ArtistUuid,
        string ArtistKey,
        string AccountKey,
        NormalizationResourceKey Resource,
        NormalizationInputRevision Input,
        NormalizationSetlistProposalBatch Batch
    );

    private sealed record Scenario(
        RetainedSource Source,
        SetlistStructureChange Image,
        NormalizationWorkTicket Owner
    );

    private sealed record RemovalScenario(
        RetainedSource Source,
        SetlistStructureChange Image,
        SetlistCorrespondenceDecision Command
    );
}
