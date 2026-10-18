using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task ContributorOwnershipChangesWithinOneUnionStillPermitGenuineArchiveWithdrawal()
    {
        var name = UniqueName();
        var person = await Person(name);
        await ActivatePolicy();
        // An independently published recording supplies a candidate owner outside the probe's
        // own credit context. Archive stops consuming it when its wording becomes unresolved.
        var sentinel = await PublishActorContext("", person, name);
        var localContact = RandomWord() + "@example.test";
        var archiveContact = RandomWord() + "@example.test";
        var source = await Source($"{name} ({localContact})");
        var local = await Resolve(source);
        AssertCapacityApplied(await applier.ApplyRecordingAsync(Guid.NewGuid(), local.AttemptUuid));
        var archive = await ExtractArchive(source, $"{name} ({archiveContact})");
        var combined = await resolver.ResolveRecordingAsync(Guid.NewGuid(), archive.AttemptUuid);
        var accepted = await applier.ApplyRecordingAsync(Guid.NewGuid(), combined.AttemptUuid);
        AssertCapacityApplied(accepted);
        var before = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording", source.SourceUuid)
        );

        // Changing an original stamp without changing any owned key must identify a NEW
        // admission. The older published stage remains an immutable account of what it consumed.
        var sentinelOwner = await fixture.OwnerUuid(
            "people",
            "recording",
            sentinel.Source.SourceUuid
        );
        var original = accepted
            .Stages.Single()
            .Ticket!.OwnerObservations!.Single(value => value.SubjectUuid == sentinelOwner);
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                sentinelOwner,
                original.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(new { fixture = true }),
                "Reconsider the existing sentinel without changing its actor facts",
                Operator
            ),
            (_, _, _) => Task.CompletedTask
        );
        var refreshed = await Resolve(source);
        var freshApplication = await applier.ApplyRecordingAsync(
            Guid.NewGuid(),
            refreshed.AttemptUuid
        );
        AssertCapacityApplied(freshApplication);
        await AssertCurrentCapacityAdmission(source, refreshed, freshApplication);
        var refreshedMembership = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording", source.SourceUuid)
        );
        refreshedMembership.Owner!.DesiredGeneration.Should().Be(before.Owner!.DesiredGeneration);
        refreshedMembership
            .Contributions.Should()
            .OnlyContain(value =>
                value.Revision
                == before
                    .Contributions.Single(prior => prior.Contributor == value.Contributor)
                    .Revision + 1
            );
        freshApplication
            .Stages.Single()
            .Ticket!.OwnerObservations!.Single(value => value.SubjectUuid == sentinelOwner)
            .Should()
            .Be(
                original with
                {
                    DesiredGeneration = original.DesiredGeneration + 1,
                    EditorialRevision = original.EditorialRevision + 1,
                }
            );
        (await applier.GetApplicationAsync(accepted.AttemptUuid))!
            .Stages.Single()
            .Ticket!.OwnerObservations!.Single(value => value.SubjectUuid == sentinelOwner)
            .Should()
            .Be(original);
        before = refreshedMembership;

        var partial = await ExtractArchive(source, "unknown");
        var unresolved = await resolver.ResolveRecordingAsync(Guid.NewGuid(), partial.AttemptUuid);
        unresolved
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var held = await applier.ApplyRecordingAsync(Guid.NewGuid(), unresolved.AttemptUuid);
        held.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        await AssertCapacityContacts(source, localContact, archiveContact);
        var current = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording", source.SourceUuid)
        );
        current
            .Owner!.DesiredGeneration.Should()
            .Be(
                before.Owner!.DesiredGeneration,
                "local still owns the union of candidate keys that Archive stopped consuming"
            );
        current
            .Contributions.Should()
            .OnlyContain(value =>
                value.Revision
                == before
                    .Contributions.Single(prior => prior.Contributor == value.Contributor)
                    .Revision + 1
            );
        current
            .Contributions.Sum(value => value.OwnerObservationSubjectUuids!.Count)
            .Should()
            .BeLessThan(
                before.Contributions.Sum(value => value.OwnerObservationSubjectUuids!.Count)
            );
        await AssertCurrentCapacityAdmission(source, unresolved, held);

        var clearing = await ExtractArchive(source, null);
        var resolution = await resolver.ResolveRecordingAsync(Guid.NewGuid(), clearing.AttemptUuid);
        resolution
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var retired = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        AssertCapacityApplied(retired);
        var retained = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        retained
            .Facts.Contacts.Should()
            .Contain(value => value.LookupKey == localContact)
            .And.NotContain(value => value.LookupKey == archiveContact);
    }

    [Test]
    public async Task ConfiguredOwnershipPreservesUnmentionedEmptyContributorHeaders()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source(name);
        var peer = new NormalizationResourceKey(
            "tests",
            "empty_people_peer",
            Guid.NewGuid().ToString("D")
        );
        foreach (
            var configuration in new[]
            {
                (
                    Kind: "recording_resolution",
                    Version: NormalizationPeopleResolutionProcessor.Version
                ),
                (Kind: "recording", Version: NormalizationPeopleResolutionApplier.Version),
            }
        )
        {
            // Public Work metadata only: register an empty contributor, without actor facts or
            // accepted writes. Real extraction and acceptance below must retain it unchanged.
            var workUuid = Guid.NewGuid();
            await fixture.Work.BeginContributedBatchAsync(
                workUuid,
                new("people", configuration.Kind, source.SourceUuid),
                configuration.Version,
                [new(new(peer, 0, [], []), [])],
                expectedContributionRevisions: []
            );
            (await fixture.Work.TryPublishAsync(workUuid, (_, _, _) => Task.CompletedTask))
                .Status.Should()
                .Be(NormalizationPublicationStatus.Published);
        }
        for (var pass = 0; pass < 2; pass++)
        {
            var resolution = await Resolve(source);
            var application = await applier.ApplyRecordingAsync(
                Guid.NewGuid(),
                resolution.AttemptUuid
            );
            AssertCapacityApplied(application);
            await AssertCurrentCapacityAdmission(source, resolution, application);
            foreach (var kind in new[] { "recording_resolution", "recording" })
            {
                var membership = await fixture.Work.GetInputContributionsAsync(
                    new("people", kind, source.SourceUuid)
                );
                membership.Contributions.Should().HaveCount(2);
                var tombstone = membership.Contributions.Single(value => value.Contributor == peer);
                tombstone.Revision.Should().Be(1);
                CountCapacityStoredBindings(tombstone).Should().Be(0);
                var missingPeer = membership with
                {
                    Contributions = membership
                        .Contributions.Where(value => value.Contributor != peer)
                        .ToArray(),
                };
                var stalePeer = membership with
                {
                    Contributions = membership
                        .Contributions.Select(value =>
                            value.Contributor == peer
                                ? value with
                                {
                                    Revision = value.Revision + 1,
                                }
                                : value
                        )
                        .ToArray(),
                };
                foreach (var unmatched in new[] { missingPeer, stalePeer })
                {
                    if (kind == "recording_resolution")
                    {
                        (
                            await resolutions.GetConfiguredPreparationAsync(
                                source.SourceUuid,
                                unmatched,
                                CancellationToken.None
                            )
                        )
                            .Should()
                            .BeNull();
                    }
                    else
                    {
                        (
                            await applications.GetConfiguredStageAsync(
                                source.SourceUuid,
                                unmatched,
                                CancellationToken.None
                            )
                        )
                            .Should()
                            .BeNull();
                    }
                }
            }
        }
    }

    [Test]
    public async Task ConfiguredProposalUsesAdmissionRevisionsInsteadOfAttemptCreationOrder()
    {
        var name = UniqueName();
        await Person(name);
        var source = await Source(name);
        var extraction = await Extract(source);
        // Creation order is deliberately opposite to admission order, without changing evidence.
        var early = await resolutions.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid,
            NormalizationPeopleResolutionProcessor.Version
        );
        var later = await resolver.ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);
        later.Result!.Disposition.Should().Be(NormalizationPeopleResolutionDisposition.Published);
        var admittedLast = await resolver.ResolveRecordingAsync(
            early.AttemptUuid,
            extraction.AttemptUuid
        );
        admittedLast
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var membership = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording_resolution", source.SourceUuid)
        );
        var configured = await resolutions.GetConfiguredPreparationAsync(
            source.SourceUuid,
            membership,
            CancellationToken.None
        );
        JsonSerializer
            .Serialize(configured)
            .Should()
            .Be(JsonSerializer.Serialize(admittedLast.Preparation));
        configured!
            .Inputs.Single()
            .ExpectedContributionRevision.Should()
            .Be(later.Preparation!.Inputs.Single().ExpectedContributionRevision + 1);
    }

    [Test]
    public async Task LargeContributorMembershipResolvesRetriesRejectsStaleAndRetiresThroughPublicServices()
    {
        await ActivatePolicy();
        // The seed has two real service publications plus 6,786 relational copies. The copies
        // exercise membership and freshness mechanics; their citations assert no source truth.
        var seeded = await SeedDistinctActorOwners();
        var localContact = RandomWord() + "@example.test";
        var archiveContact = RandomWord() + "@example.test";
        var source = await Source($"{seeded.First.Name} ({localContact})");
        var first = await Resolve(source);
        var local = await applier.ApplyRecordingAsync(Guid.NewGuid(), first.AttemptUuid);
        AssertCapacityApplied(local);
        var legacyContributor = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Single()
            .Contributor;
        var archive = await ExtractArchive(source, $"{seeded.First.Name} ({archiveContact})");
        var combined = await resolver.ResolveRecordingAsync(Guid.NewGuid(), archive.AttemptUuid);
        combined
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var established = await applier.ApplyRecordingAsync(Guid.NewGuid(), combined.AttemptUuid);
        AssertCapacityApplied(established);
        await AssertLargePeopleMembership(source, seeded, established);

        // New attempts, not terminal replays: both owners already have two large contributions.
        // A real local input change also makes rollback/recovery observable in accepted facts.
        var replacementContact = RandomWord() + "@example.test";
        await SetTaper(source, $"{seeded.First.Name} ({replacementContact})");
        var second = await Resolve(source);
        AssertCapacityOriginalSnapshots(second.Preparation!, seeded.OriginalObservations);
        var interrupted = await InterruptCapacityPublication(source, second);
        var recovered = await applier.ApplyRecordingAsync(
            interrupted.AttemptUuid,
            second.AttemptUuid
        );
        AssertCapacityApplied(recovered);
        recovered.Stages.Single().WorkUuid.Should().Be(interrupted.Stages.Single().WorkUuid);
        recovered
            .Stages.Single()
            .Ticket.Should()
            .BeEquivalentTo(interrupted.Stages.Single().Ticket);
        AssertCapacityTicketObservations(recovered.Stages.Single());
        await AssertCapacityContacts(source, replacementContact, archiveContact);
        var publishedContexts = JsonSerializer.Serialize(
            await people.GetRecordingActorContextsAsync(source.SourceUuid)
        );
        var replay = await applier.ApplyRecordingAsync(recovered.AttemptUuid, second.AttemptUuid);
        replay.Result!.Replayed.Should().BeTrue();
        JsonSerializer
            .Serialize(await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .Be(publishedContexts);
        await AssertLargePeopleMembership(source, seeded, recovered);
        await WriteCapacityCheckpointSizes(second, recovered, seeded);

        await RejectCapacityStageAfterUnwitnessedOwnerReview(source, seeded);
        var refreshed = await Resolve(source);
        var restored = await applier.ApplyRecordingAsync(Guid.NewGuid(), refreshed.AttemptUuid);
        AssertCapacityApplied(restored);
        await AssertCurrentCapacityAdmission(source, refreshed, restored);
        await AssertCapacityContacts(source, replacementContact, archiveContact);
        await AcknowledgeCapacityDecisionWithoutAcceptedWrites(source, seeded, restored);
        await RetireOnlyCapacityArchiveAccount(
            source,
            seeded.First.Name,
            legacyContributor,
            replacementContact,
            archiveContact
        );
    }

    private async Task AssertLargePeopleMembership(
        CatalogSource source,
        OwnerScaleSeed seeded,
        PeopleAcceptanceApplication application
    )
    {
        foreach (var kind in new[] { "recording_resolution", "recording" })
        {
            var subject = new NormalizationSubjectKey("people", kind, source.SourceUuid);
            Func<Task> defaultRead = async () =>
            {
                await fixture.Work.GetInputContributionsAsync(subject);
            };
            var refused = (
                await defaultRead
                    .Should()
                    .ThrowAsync<NormalizationInputContributionsCapacityException>()
            ).Which;
            refused.Membership.Contributors.Should().Be(2);
            refused
                .Membership.Bindings.Should()
                .BeGreaterThan(10000)
                .And.BeLessThanOrEqualTo(100000);
            var complete = await fixture.Work.GetInputContributionsAsync(
                subject,
                new(MaximumContributors: 100, MaximumBindings: 100000)
            );
            complete.UnmanagedBaseline.Should().BeNull();
            complete.Contributions.Should().HaveCount(2);
            complete
                .Contributions.Select(value => value.Contributor)
                .OrderBy(value => value.ToString())
                .Should()
                .Equal(
                    application
                        .Stages.Single()
                        .Contributions.Select(value => value.Contribution.Contributor)
                        .OrderBy(value => value.ToString())
                );
            var originalKeys = seeded
                .OriginalObservations.Select(value => value.SubjectUuid)
                .ToHashSet();
            foreach (var contribution in complete.Contributions)
            {
                // All original 6,789 keys are retained independently by BOTH contributors.
                contribution
                    .OwnerObservationSubjectUuids!.Where(originalKeys.Contains)
                    .Order()
                    .Should()
                    .Equal(originalKeys.Order());
                if (kind == "recording")
                {
                    var saved = application
                        .Stages.Single()
                        .Contributions.Single(value =>
                            value.Contribution.Contributor == contribution.Contributor
                        );
                    contribution
                        .OwnerObservationSubjectUuids!.Order()
                        .Should()
                        .Equal(saved.Contribution.OwnerObservationSubjectUuids!.Order());
                }
            }
            var stored = complete.Contributions.Sum(CountCapacityStoredBindings);
            stored.Should().Be(refused.Membership.Bindings);
            WriteCapacityMeasurement(
                "complete_membership",
                new
                {
                    kind,
                    contributors = complete.Contributions.Count,
                    storedBindings = stored,
                    originalOwnerBindingsPerContributor = originalKeys.Count,
                    distinctOwnerUnion = complete
                        .Contributions.SelectMany(value => value.OwnerObservationSubjectUuids!)
                        .Distinct()
                        .Count(),
                    defaultWorkLimitStillRefuses = true,
                }
            );
        }
    }

    private static long CountCapacityStoredBindings(NormalizationInputContributionState value) =>
        (long)value.Resources.Count
        + value.DependencySubjectUuids.Count
        + value.CatalogArtistUuids.Count
        + value.SourceIdentifiers.Count
        + (value.UsesMappingRegistry ? 1 : 0)
        + (value.ImportedVenueUuids?.Count ?? 0)
        + (value.CandidateUniverses?.Count ?? 0)
        + (value.OwnerObservationSubjectUuids?.Count ?? 0)
        + (value.SourceUuids?.Count ?? 0);

    private static void AssertCapacityOriginalSnapshots(
        NormalizationPeopleResolutionPreparation preparation,
        IReadOnlyList<NormalizationSubjectObservation> original
    )
    {
        preparation.Inputs.Should().HaveCount(2);
        var keys = original.Select(value => value.SubjectUuid).ToHashSet();
        foreach (var input in preparation.Inputs)
        {
            input.Claims.Should().ContainSingle();
            var snapshot = input.Claims.Single().Candidates!;
            snapshot.Complete.Should().BeTrue();
            snapshot.Facts.Should().ContainSingle();
            snapshot.ReadSet.OwnerObservations.Should().HaveCount(DistinctActorOwnerScale + 2);
            snapshot
                .ReadSet.OwnerObservations.Where(value => keys.Contains(value.SubjectUuid))
                .OrderBy(value => value.SubjectUuid)
                .Should()
                .Equal(original.OrderBy(value => value.SubjectUuid));
        }
    }

    private static void AssertCapacityTicketObservations(PeopleAcceptanceStage stage)
    {
        stage.Contributions.Should().HaveCount(2);
        var expected = stage
            .Contributions.SelectMany(value => value.ExpectedOwnerObservations!)
            .Distinct()
            .OrderBy(value => value.SubjectUuid);
        stage
            .Ticket!.OwnerObservations!.OrderBy(value => value.SubjectUuid)
            .Should()
            .Equal(expected);
    }

    private async Task<PeopleAcceptanceApplication> InterruptCapacityPublication(
        CatalogSource source,
        NormalizationPeopleResolutionAttempt resolution
    )
    {
        var applicationUuid = Guid.NewGuid();
        var before = JsonSerializer.Serialize(
            await people.GetRecordingActorContextsAsync(source.SourceUuid)
        );
        var trigger = "capacity_receipt_failure_" + Guid.NewGuid().ToString("N");
        // Admission is already durable when this trigger aborts the publication transaction.
        // It targets this application only, after domain writes and before their shared receipt.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.application_uuid='{applicationUuid:D}'::uuid THEN
                        RAISE EXCEPTION 'synthetic capacity receipt failure';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER {trigger} BEFORE UPDATE OF receipt ON normalization.people_acceptance_stages
                    FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}();
                """
            )
        );
        try
        {
            Func<Task> publish = async () =>
            {
                await applier.ApplyRecordingAsync(applicationUuid, resolution.AttemptUuid);
            };
            var failure = (await publish.Should().ThrowAsync<Exception>()).Which;
            var databaseFailure = failure
                .InnerException.Should()
                .BeOfType<PostgresException>()
                .Which;
            databaseFailure.SqlState.Should().Be("P0001");
            databaseFailure.MessageText.Should().Be("synthetic capacity receipt failure");
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"""
                    DROP TRIGGER {trigger} ON normalization.people_acceptance_stages;
                    DROP FUNCTION normalization.{trigger}();
                    """
                )
            );
        }
        var interrupted = (await applier.GetApplicationAsync(applicationUuid))!;
        interrupted.Result.Should().BeNull();
        var stage = interrupted.Stages.Should().ContainSingle().Which;
        stage.Ticket.Should().NotBeNull();
        stage.Receipt.Should().BeNull();
        AssertCapacityTicketObservations(stage);
        JsonSerializer
            .Serialize(await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .Be(before, "failed publication must leave both original accounts intact");
        return interrupted;
    }

    private async Task RejectCapacityStageAfterUnwitnessedOwnerReview(
        CatalogSource source,
        OwnerScaleSeed seeded
    )
    {
        var resolution = await Resolve(source);
        var interrupted = await InterruptCapacityPublication(source, resolution);
        var stage = interrupted.Stages.Single();
        var witnessed = stage
            .Facts.Inputs.SelectMany(input => input.Claims)
            .SelectMany(claim => claim.Candidates!.Facts)
            .SelectMany(fact => fact.ActorEvidence ?? [])
            .Select(value => value.SourceUuid)
            .ToHashSet();
        var omitted = seeded.Copies.First(value => !witnessed.Contains(value.SourceUuid));
        var original = stage.Ticket!.OwnerObservations!.Single(value =>
            value.SubjectUuid == omitted.OwnerUuid
        );
        original
            .Should()
            .Be(
                seeded.OriginalObservations.Single(value => value.SubjectUuid == omitted.OwnerUuid)
            );
        var before = JsonSerializer.Serialize(
            await people.GetRecordingActorContextsAsync(source.SourceUuid)
        );
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                original.SubjectUuid,
                original.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(
                    new { note = "Mechanical copied owner review; no actor truth asserted" }
                ),
                "Invalidate an owner outside every retained raw witness",
                Operator
            ),
            (_, _, _) => Task.CompletedTask
        );
        var reviewed = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "recording", omitted.SourceUuid)
            )
        )!;
        reviewed.DesiredGeneration.Should().Be(original.DesiredGeneration + 1);
        reviewed.EditorialRevision.Should().Be(original.EditorialRevision + 1);
        reviewed.PublishedWorkUuid.Should().Be(original.PublishedWorkUuid);
        var stale = await applier.ApplyRecordingAsync(
            interrupted.AttemptUuid,
            resolution.AttemptUuid
        );
        stale.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        stale.Stages.Single().Receipt.Should().BeNull();
        stale.Stages.Single().Ticket.Should().BeEquivalentTo(stage.Ticket);
        JsonSerializer
            .Serialize(await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .Be(before);
        var current = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [seeded.First.Name], [])
        );
        current.Complete.Should().BeTrue();
        var originalKeys = seeded
            .OriginalObservations.Select(value => value.SubjectUuid)
            .ToHashSet();
        current
            .ReadSet.OwnerObservations.Where(value =>
                originalKeys.Contains(value.SubjectUuid) && value.SubjectUuid != omitted.OwnerUuid
            )
            .OrderBy(value => value.SubjectUuid)
            .Should()
            .Equal(
                seeded
                    .OriginalObservations.Where(value => value.SubjectUuid != omitted.OwnerUuid)
                    .OrderBy(value => value.SubjectUuid)
            );
        WriteCapacityMeasurement(
            "stale_original_owner",
            new
            {
                outsideAllWitnesses = true,
                original,
                reviewed.DesiredGeneration,
                reviewed.EditorialRevision,
                stale.Result.Disposition,
                unchangedSavedTicket = true,
            }
        );
    }

    private async Task AcknowledgeCapacityDecisionWithoutAcceptedWrites(
        CatalogSource source,
        OwnerScaleSeed seeded,
        PeopleAcceptanceApplication lastAccepted
    )
    {
        await AssertLargePeopleMembership(source, seeded, lastAccepted);
        var before = JsonSerializer.Serialize(
            await people.GetRecordingActorContextsAsync(source.SourceUuid)
        );
        await SetActivePolicy(null);
        var resolution = await Resolve(source);
        var acknowledged = await applier.ApplyRecordingAsync(
            Guid.NewGuid(),
            resolution.AttemptUuid
        );
        acknowledged.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        acknowledged.Result.Reason.Should().Be("acceptance_policy_inactive");
        AssertDecisionAcknowledged(acknowledged);
        AssertCapacityTicketObservations(acknowledged.Stages.Single());
        JsonSerializer
            .Serialize(await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .Be(before);
        var replay = await applier.ApplyRecordingAsync(
            acknowledged.AttemptUuid,
            resolution.AttemptUuid
        );
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be("acceptance_policy_inactive");
        WriteCapacityMeasurement(
            "large_membership_acknowledged",
            new
            {
                contributors = acknowledged.Stages.Single().Contributions.Count,
                acceptedWrites = acknowledged.Result.Publications.Sum(value => value.Credits.Count),
                acknowledged.Result.Reason,
            }
        );
        await ActivatePolicy();
    }

    private async Task RetireOnlyCapacityArchiveAccount(
        CatalogSource source,
        string name,
        NormalizationResourceKey legacyContributor,
        string localContact,
        string archiveContact
    )
    {
        var restore = await Resolve(source);
        AssertCapacityApplied(
            await applier.ApplyRecordingAsync(Guid.NewGuid(), restore.AttemptUuid)
        );
        var partial = await ExtractArchive(source, "unknown");
        var unresolved = await resolver.ResolveRecordingAsync(Guid.NewGuid(), partial.AttemptUuid);
        unresolved
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var held = await applier.ApplyRecordingAsync(Guid.NewGuid(), unresolved.AttemptUuid);
        held.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        await AssertCapacityContacts(source, localContact, archiveContact);
        await AssertCurrentCapacityAdmission(source, unresolved, held);
        var clearing = await ExtractArchive(source, null);
        var resolution = await resolver.ResolveRecordingAsync(Guid.NewGuid(), clearing.AttemptUuid);
        resolution
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var retired = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);
        AssertCapacityApplied(retired);
        var retained = (await people.GetRecordingActorContextsAsync(source.SourceUuid))
            .Should()
            .ContainSingle()
            .Which;
        retained.Contributor.Should().Be(legacyContributor);
        retained
            .Facts.Contacts.Should()
            .Contain(value => value.LookupKey == localContact)
            .And.NotContain(value => value.LookupKey == archiveContact);
        var aggregate = await candidates.FindPeopleFactsAsync(
            new(IdentityMatchingDomain.People, [name], [])
        );
        aggregate.Complete.Should().BeTrue();
        aggregate.ReadSet.OwnerObservations.Should().HaveCount(DistinctActorOwnerScale + 2);
        aggregate
            .Facts.Single()
            .ActorMatchingFacts!.Contacts.Should()
            .NotContain(value => value.LookupKey == archiveContact);
        WriteCapacityMeasurement(
            "genuine_archive_withdrawal",
            new
            {
                remainingAccounts = 1,
                unresolvedPreservedBoth = true,
                ownContributorRetiredThroughPublicAcceptance = true,
            }
        );
    }

    private async Task AssertCurrentCapacityAdmission(
        CatalogSource source,
        NormalizationPeopleResolutionAttempt proposal,
        PeopleAcceptanceApplication application
    )
    {
        var resolutionMembership = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording_resolution", source.SourceUuid),
            new(MaximumContributors: 100, MaximumBindings: 100000)
        );
        var preparation = await resolutions.GetConfiguredPreparationAsync(
            source.SourceUuid,
            resolutionMembership,
            CancellationToken.None
        );
        JsonSerializer
            .Serialize(preparation)
            .Should()
            .Be(JsonSerializer.Serialize(proposal.Preparation));
        var creditMembership = await fixture.Work.GetInputContributionsAsync(
            new("people", "recording", source.SourceUuid),
            new(MaximumContributors: 100, MaximumBindings: 100000)
        );
        var stage = await applications.GetConfiguredStageAsync(
            source.SourceUuid,
            creditMembership,
            CancellationToken.None
        );
        stage!.WorkUuid.Should().Be(application.Stages.Single().WorkUuid);

        // A plausible union generation alone cannot authenticate a checkpoint. These read-only
        // requests name nonexistent header revisions; they fabricate no accepted or Work state.
        var unadmittedResolution = resolutionMembership with
        {
            Contributions = resolutionMembership
                .Contributions.Select(value => value with { Revision = value.Revision + 1000000 })
                .ToArray(),
        };
        var unadmittedCredits = creditMembership with
        {
            Contributions = creditMembership
                .Contributions.Select(value => value with { Revision = value.Revision + 1000000 })
                .ToArray(),
        };
        (
            await resolutions.GetConfiguredPreparationAsync(
                source.SourceUuid,
                unadmittedResolution,
                CancellationToken.None
            )
        )
            .Should()
            .BeNull();
        (
            await applications.GetConfiguredStageAsync(
                source.SourceUuid,
                unadmittedCredits,
                CancellationToken.None
            )
        )
            .Should()
            .BeNull();
    }

    private async Task AssertCapacityContacts(CatalogSource source, params string[] contacts)
    {
        var contexts = await people.GetRecordingActorContextsAsync(source.SourceUuid);
        contexts.Should().HaveCount(2);
        contexts.Select(value => value.Contributor).Distinct().Should().HaveCount(2);
        contexts
            .SelectMany(value => value.Facts.Contacts)
            .Select(value => value.LookupKey)
            .Order()
            .Should()
            .Equal(contacts.Order());
    }

    private static void AssertCapacityApplied(PeopleAcceptanceApplication application)
    {
        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        application.Stages.Should().ContainSingle();
        application.Stages.Single().Receipt.Should().NotBeNull();
    }

    private async Task WriteCapacityCheckpointSizes(
        NormalizationPeopleResolutionAttempt resolution,
        PeopleAcceptanceApplication application,
        OwnerScaleSeed seeded
    )
    {
        var inventory = await ReadOwnerScaleInventory(
            seeded.First.PersonUuid,
            seeded.First.Source.SourceUuid
        );
        inventory.Rows.Should().Be(DistinctActorOwnerScale + 2);
        inventory.Sources.Should().Be(DistinctActorOwnerScale + 1);
        inventory.Owners.Should().Be(DistinctActorOwnerScale + 1);
        inventory.InvalidJoins.Should().Be(0);
        inventory.InvalidTypedCopies.Should().Be(0);
        var storage = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CapacityCheckpointBytes>(
                """
                SELECT octet_length(r.preparation::text) AS "Preparation",octet_length(r.batch::text) AS "Batch",
                    octet_length(a.plan::text) AS "Plan",octet_length(s.stage::text) AS "Stage",
                    octet_length(s.ticket::text) AS "Ticket"
                FROM normalization.people_resolution_attempts r
                JOIN normalization.people_acceptance_applications a ON a.resolution_attempt_uuid=r.attempt_uuid
                JOIN normalization.people_acceptance_stages s ON s.application_uuid=a.attempt_uuid
                WHERE r.attempt_uuid=@resolution AND a.attempt_uuid=@application
                """,
                new { resolution = resolution.AttemptUuid, application = application.AttemptUuid }
            )
        );
        WriteCapacityMeasurement(
            "checkpoint_bytes",
            new
            {
                activeContextInventory = inventory,
                postgresJsonTextBytes = storage,
                candidateSnapshotUtf8Bytes = resolution
                    .Preparation!.Inputs.SelectMany(input => input.Claims)
                    .Select(claim => JsonSerializer.SerializeToUtf8Bytes(claim.Candidates).Length)
                    .ToArray(),
                rawWitnessUtf8Bytes = resolution
                    .Preparation.Inputs.SelectMany(input => input.Claims)
                    .Select(claim =>
                        JsonSerializer
                            .SerializeToUtf8Bytes(claim.Candidates!.Facts.Single().ActorEvidence)
                            .Length
                    )
                    .ToArray(),
                candidateOwnerStampCounts = resolution
                    .Preparation.Inputs.SelectMany(input => input.Claims)
                    .Select(claim => claim.Candidates!.ReadSet.OwnerObservations.Count)
                    .ToArray(),
                rawWitnessCounts = resolution
                    .Preparation.Inputs.SelectMany(input => input.Claims)
                    .Select(claim => claim.Candidates!.Facts.Single().ActorEvidence!.Count)
                    .ToArray(),
                matchingAggregateUtf8Bytes = resolution
                    .Preparation.Inputs.SelectMany(input => input.Claims)
                    .Select(claim =>
                        JsonSerializer
                            .SerializeToUtf8Bytes(
                                claim.Candidates!.Facts.Single().ActorMatchingFacts
                            )
                            .Length
                    )
                    .ToArray(),
                originalOwnerStampUtf8Bytes = resolution
                    .Preparation.Inputs.SelectMany(input => input.Claims)
                    .Select(claim =>
                        JsonSerializer
                            .SerializeToUtf8Bytes(claim.Candidates!.ReadSet.OwnerObservations)
                            .Length
                    )
                    .ToArray(),
                contributionMembershipLimitIsNotCheckpointByteCapacity = true,
                manyClaimCheckpointCapacityEstablished = false,
            }
        );
    }

    private static void WriteCapacityMeasurement<T>(string phase, T details) =>
        TestContext.Progress.WriteLine(
            "CONTRIBUTION_CAPACITY_MEASUREMENT " + JsonSerializer.Serialize(new { phase, details })
        );

    private sealed record CapacityCheckpointBytes(
        int Preparation,
        int Batch,
        int Plan,
        int Stage,
        int Ticket
    );
}
