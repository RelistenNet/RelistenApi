using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Multi-input admission preserves each producer's facts, ownership and retry receipt.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationContributionBatch
{
    private NormalizationDomainFixture fixture = null!;

    [OneTimeSetUp]
    public void Start() => fixture = new();

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SnapshotReturnsAllContributorsAndTombstonesWithTheOwnerState()
    {
        var subject = Subject();
        var first = await fixture.Capture("First original input", "people");
        var second = await fixture.Capture("Second original input", "people");
        var a = Contributor();
        var b = Contributor();
        await Batch(subject, [Prepared(a, 0, first), Prepared(b, 0, second)]);
        await Batch(subject, [new(new(a, 1, [], []), [])]);

        var snapshot = await fixture.Work.GetInputContributionsAsync(subject);

        snapshot.Owner.Should().Be(await fixture.Work.GetSubjectAsync(subject));
        snapshot.UnmanagedBaseline.Should().BeNull();
        snapshot.Contributions.Should().HaveCount(2);
        snapshot.Contributions.Single(state => state.Contributor == a).Revision.Should().Be(2);
        snapshot.Contributions.Single(state => state.Contributor == a).Resources.Should().BeEmpty();
        snapshot
            .Contributions.Single(state => state.Contributor == b)
            .Resources.Should()
            .Equal(second.Resource);
        snapshot
            .Contributions.Should()
            .OnlyContain(state => state.SubjectUuid == snapshot.Owner!.SubjectUuid);
    }

    [Test]
    public async Task MissingOwnerSnapshotDoesNotRegisterWork()
    {
        var subject = Subject();
        var snapshot = await fixture.Work.GetInputContributionsAsync(subject);

        snapshot.Owner.Should().BeNull();
        snapshot.Contributions.Should().BeEmpty();
        snapshot.UnmanagedBaseline.Should().BeNull();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SnapshotBoundsRejectPartialResultsAndAcceptTheExactBoundary(bool contributors)
    {
        var subject = Subject();
        var first = await fixture.Capture("First bound input", "people");
        var second = await fixture.Capture("Second bound input", "people");
        await Batch(
            subject,
            [Prepared(Contributor(), 0, first), Prepared(Contributor(), 0, second)]
        );
        var limit = contributors ? new NormalizationInputContributionsLimits(1, 2) : new(2, 1);
        var read = () => fixture.Work.GetInputContributionsAsync(subject, limit);

        var error = (
            await read.Should()
                .ThrowAsync<NormalizationInputContributionsCapacityException>()
                .WithMessage("*no partial snapshot*")
        ).Which;
        var complete = await fixture.Work.GetInputContributionsAsync(subject, new(2, 2));
        error
            .Owner.Should()
            .Be(
                new NormalizationSubjectObservation(
                    complete.Owner!.SubjectUuid,
                    complete.Owner.DesiredGeneration,
                    complete.Owner.EditorialRevision,
                    complete.Owner.PublishedWorkUuid
                )
            );
        error
            .Membership.Should()
            .Be(
                new NormalizationInputContributionsCapacityStamp(
                    2,
                    2,
                    complete
                        .Contributions.Sum(contribution => contribution.Revision)
                        .ToString(System.Globalization.CultureInfo.InvariantCulture)
                )
            );
        complete.Contributions.Should().HaveCount(2);
        complete.Contributions.Sum(state => state.Resources.Count).Should().Be(2);
    }

    [Test]
    public async Task UnmanagedSnapshotAlsoRejectsOversizedBindingsWithoutPersistingABaseline()
    {
        var subject = Subject();
        var first = await fixture.Capture("Unmanaged bound A", "people");
        var second = await fixture.Capture("Unmanaged bound B", "people");
        await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "batch-v1",
            [first.Resource, second.Resource],
            []
        );
        var read = () => fixture.Work.GetInputContributionsAsync(subject, new(1, 1));

        var error = (
            await read.Should()
                .ThrowAsync<NormalizationInputContributionsCapacityException>()
                .WithMessage("*no partial snapshot*")
        ).Which;
        var complete = await fixture.Work.GetInputContributionsAsync(subject, new(1, 2));
        error
            .Owner.Should()
            .Be(
                new NormalizationSubjectObservation(
                    complete.Owner!.SubjectUuid,
                    complete.Owner.DesiredGeneration,
                    complete.Owner.EditorialRevision,
                    complete.Owner.PublishedWorkUuid
                )
            );
        error
            .Membership.Should()
            .Be(new NormalizationInputContributionsCapacityStamp(0, 0, "0", 2));
        complete.Contributions.Should().BeEmpty();
        complete
            .UnmanagedBaseline!.Resources.Should()
            .BeEquivalentTo(new[] { first.Resource, second.Resource });
        (
            await fixture.Work.GetInputContributionAsync(
                subject,
                complete.UnmanagedBaseline.Contributor
            )
        )
            .Should()
            .BeNull();
    }

    [Test]
    public async Task UnmanagedBindingsAreExplicitAndAdoptionPreservesTheirBaseline()
    {
        var subject = Subject();
        var original = await fixture.Capture("Unmanaged original", "people");
        var additional = await fixture.Capture("Additional producer", "people");
        var unmanaged = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "batch-v1",
            [original.Resource],
            []
        );
        var before = await fixture.Work.GetInputContributionsAsync(subject);

        before.Contributions.Should().BeEmpty();
        before.UnmanagedBaseline!.Revision.Should().Be(0);
        before.UnmanagedBaseline.Resources.Should().Equal(original.Resource);
        before
            .UnmanagedBaseline.Contributor.Should()
            .Be(
                new NormalizationResourceKey(
                    "relisten",
                    "preexisting_owner_bindings",
                    unmanaged.SubjectUuid.ToString("D")
                )
            );
        (
            await fixture.Work.GetInputContributionAsync(
                subject,
                before.UnmanagedBaseline.Contributor
            )
        )
            .Should()
            .BeNull();
        var admitted = await Batch(subject, [Prepared(Contributor(), 0, additional)]);
        admitted
            .Inputs.Select(input => input.RevisionUuid)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    original.Reference.InputRevisionUuid,
                    additional.Reference.InputRevisionUuid,
                }
            );
        var after = await fixture.Work.GetInputContributionsAsync(subject);
        after.UnmanagedBaseline.Should().BeNull();
        after
            .Contributions.Single(state =>
                state.Contributor == before.UnmanagedBaseline.Contributor
            )
            .Should()
            .Match<NormalizationInputContributionState>(state =>
                state.Revision == 1 && state.Resources.Single() == original.Resource
            );
    }

    [Test]
    public async Task BatchReplacesEveryPreparedContributorAndPreservesAnUnknownPeer()
    {
        var subject = Subject();
        var a = Contributor();
        var b = Contributor();
        var peer = Contributor();
        var oldA = await fixture.Capture("Old A", "people");
        var oldB = await fixture.Capture("Old B", "people");
        var retained = await fixture.Capture("Unknown producer remains owned", "people");
        await Batch(
            subject,
            [Prepared(a, 0, oldA), Prepared(b, 0, oldB), Prepared(peer, 0, retained)]
        );
        var newA = await fixture.Capture("New A", "people");
        var newB = await fixture.Capture("New B", "people");

        var ticket = await Batch(subject, [Prepared(a, 1, newA), Prepared(b, 1, newB)]);

        ticket
            .Inputs.Select(input => input.RevisionUuid)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    newA.Reference.InputRevisionUuid,
                    newB.Reference.InputRevisionUuid,
                    retained.Reference.InputRevisionUuid,
                }
            );
        var snapshot = await fixture.Work.GetInputContributionsAsync(subject);
        snapshot
            .Contributions.Single(state => state.Contributor == a)
            .Resources.Should()
            .Equal(newA.Resource);
        snapshot
            .Contributions.Single(state => state.Contributor == b)
            .Resources.Should()
            .Equal(newB.Resource);
        snapshot.Contributions.Single(state => state.Contributor == peer).Revision.Should().Be(1);
        snapshot
            .Contributions.Single(state => state.Contributor == peer)
            .Resources.Should()
            .Equal(retained.Resource);
    }

    [Test]
    public async Task ExactBatchReplayIsOrderIndependentAndDoesNotAdvanceContributorRevisions()
    {
        var subject = Subject();
        var a = Prepared(Contributor(), 0, await fixture.Capture("Replay A", "people"));
        var b = Prepared(Contributor(), 0, await fixture.Capture("Replay B", "people"));
        var workUuid = Guid.NewGuid();
        var first = await Batch(subject, [a, b], workUuid);
        await fixture.Work.TryPublishAsync(first.WorkUuid, (_, _, _) => Task.CompletedTask);

        var replay = await Batch(subject, [b, a], workUuid);

        replay.Should().BeEquivalentTo(first);
        (await fixture.Work.GetInputContributionsAsync(subject))
            .Contributions.Should()
            .OnlyContain(state => state.Revision == 1);
        var changed = () =>
            Batch(
                subject,
                [a with { Contribution = a.Contribution with { ExpectedRevision = 1 } }, b],
                workUuid
            );
        await changed.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task StaleContributorCasCannotCommitAnotherReplacement()
    {
        var subject = Subject();
        var a = Prepared(Contributor(), 0, await fixture.Capture("CAS A", "people"));
        var b = Prepared(Contributor(), 0, await fixture.Capture("CAS B", "people"));
        await Batch(subject, [a, b]);
        var nextA = Prepared(
            a.Contribution.Contributor,
            1,
            await fixture.Capture("Next A", "people")
        );
        var invalid = () => Batch(subject, [nextA, b]);

        await invalid.Should().ThrowAsync<NormalizationConflictException>();
        var snapshot = await fixture.Work.GetInputContributionsAsync(subject);
        snapshot
            .Contributions.Single(state => state.Contributor == a.Contribution.Contributor)
            .Revision.Should()
            .Be(1);
        snapshot
            .Contributions.Single(state => state.Contributor == a.Contribution.Contributor)
            .Resources.Should()
            .Equal(a.Contribution.Resources);
        snapshot
            .Contributions.Single(state => state.Contributor == b.Contribution.Contributor)
            .Revision.Should()
            .Be(1);
    }

    [Test]
    public async Task FailureDuringTheSecondReplacementRollsBackTheWholeBatchAndCanRetry()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = Subject();
        var a = Prepared(
            new("test", "batch", "a-" + suffix),
            0,
            await fixture.Capture("Rollback A", "people")
        );
        var b = Prepared(
            new("test", "batch", "z-" + suffix),
            0,
            await fixture.Capture("Rollback B", "people")
        );
        var function = "fail_contribution_batch_" + suffix;
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.{function}() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'second batch replacement failed' USING ERRCODE='P0001'; END $$;
                CREATE TRIGGER {function} BEFORE INSERT ON normalization.work_input_contributions
                    FOR EACH ROW WHEN(NEW.contributor_key='z-{suffix}') EXECUTE FUNCTION normalization.{function}();
                """
            )
        );
        var workUuid = Guid.NewGuid();
        try
        {
            var failure = () => Batch(subject, [a, b], workUuid);
            var error = (await failure.Should().ThrowAsync<Exception>()).Which;
            var postgres = FindPostgres(error);
            postgres.Should().NotBeNull("the second database replacement must cause the failure");
            postgres!.SqlState.Should().Be("P0001");
            (await fixture.Work.GetInputContributionsAsync(subject)).Owner.Should().BeNull();
        }
        finally
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    $"DROP TRIGGER {function} ON normalization.work_input_contributions; DROP FUNCTION normalization.{function}()"
                )
            );
        }
        var retry = await Batch(subject, [a, b], workUuid);
        (await fixture.Work.GetInputContributionsAsync(subject))
            .Contributions.Should()
            .HaveCount(2);
        (await fixture.Work.TryPublishAsync(retry.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [TestCase("artist")]
    [TestCase("identifier")]
    [TestCase("source_uuid")]
    [TestCase("venue")]
    [TestCase("candidate")]
    [TestCase("registry")]
    [TestCase("owner")]
    public async Task DifferentOriginalVersionsOfASharedClockCannotBeCombined(string clock)
    {
        var subject = Subject();
        var a = Prepared(Contributor(), 0, await fixture.Capture("Clock A", "people"));
        var b = Prepared(Contributor(), 0, await fixture.Capture("Clock B", "people"));
        var key = Guid.NewGuid();
        (a, b) = clock switch
        {
            "artist" => (
                a with
                {
                    CatalogInputs = [new(key, 1)],
                },
                b with
                {
                    CatalogInputs = [new(key, 2)],
                }
            ),
            "identifier" => (
                a with
                {
                    SourceIdentities = [new("shared", 1)],
                },
                b with
                {
                    SourceIdentities = [new("shared", 2)],
                }
            ),
            "source_uuid" => (
                a with
                {
                    SourceUuids = [new(key, 1)],
                },
                b with
                {
                    SourceUuids = [new(key, 2)],
                }
            ),
            "venue" => (
                a with
                {
                    ImportedVenues = [new(key, 1)],
                },
                b with
                {
                    ImportedVenues = [new(key, 2)],
                }
            ),
            "candidate" => (
                a with
                {
                    CandidateUniverses = [new(NormalizationCandidateUniverseKey.People, 1)],
                },
                b with
                {
                    CandidateUniverses = [new(NormalizationCandidateUniverseKey.People, 2)],
                }
            ),
            "registry" => (
                a with
                {
                    MappingRegistry = new(1),
                },
                b with
                {
                    MappingRegistry = new(2),
                }
            ),
            "owner" => (
                a with
                {
                    Contribution = a.Contribution with { OwnerObservationSubjectUuids = [key] },
                    ExpectedOwnerObservations = [new(key, 1, 0, null)],
                },
                b with
                {
                    Contribution = b.Contribution with { OwnerObservationSubjectUuids = [key] },
                    ExpectedOwnerObservations = [new(key, 2, 0, null)],
                }
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(clock)),
        };
        var admit = () => Batch(subject, [a, b]);

        await admit
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*different revisions*");
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    [Test]
    public async Task SharedResourceCannotChooseBetweenDifferentOriginalInputRevisions()
    {
        var subject = Subject();
        var oldInput = await fixture.Capture("Original shared input", "people");
        var current = await fixture.Refresh(oldInput, "Changed shared input");
        var admit = () =>
            Batch(
                subject,
                [Prepared(Contributor(), 0, oldInput), Prepared(Contributor(), 0, current)]
            );

        await admit.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    [Test]
    public async Task EveryPreparedResourceIsPinnedAndChangedHeadsCannotAcquireFreshExpectations()
    {
        var subject = Subject();
        var first = await fixture.Capture("Original pinned A", "people");
        var second = await fixture.Capture("Original pinned B", "people");
        var a = Prepared(Contributor(), 0, first);
        var b = Prepared(Contributor(), 0, second);
        var unpinned = () => Batch(subject, [a, b with { ExpectedInputRevisions = [] }]);
        await unpinned
            .Should()
            .ThrowAsync<ArgumentException>()
            .WithMessage("*Every prepared contribution resource*");
        await fixture.Refresh(first, "Updated after preparation");
        var stale = () => Batch(subject, [a, b]);

        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    [Test]
    public async Task OriginalCatalogClockIsCheckedBeforeCommittingAnyContribution()
    {
        var subject = Subject();
        var source = await fixture.Source();
        var clock = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<NormalizationCatalogDependency>(
                "SELECT r.artist_uuid AS \"ArtistUuid\",r.generation AS \"Generation\" FROM normalization.catalog_artist_revisions r JOIN artists a ON a.uuid=r.artist_uuid WHERE a.id=@ArtistId",
                source
            )
        );
        var a = Prepared(Contributor(), 0, await fixture.Capture("Catalog A", "people")) with
        {
            CatalogInputs = [clock],
        };
        var b = Prepared(Contributor(), 0, await fixture.Capture("Catalog B", "people"));
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET description='Changed original catalog facts' WHERE id=@SourceId",
                source
            )
        );
        var stale = () => Batch(subject, [a, b]);

        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    [Test]
    public async Task ChangedPolicyStillRequiresTheOriginalOwnerConfigurationCas()
    {
        var subject = Subject();
        var a = Prepared(Contributor(), 0, await fixture.Capture("Policy A", "people"));
        var first = await Batch(subject, [a]);
        await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "batch-intermediate",
            a.Contribution.Resources,
            [],
            first.DesiredGeneration
        );
        var next = a with { Contribution = a.Contribution with { ExpectedRevision = 1 } };
        var stale = () =>
            fixture.Work.BeginContributedBatchAsync(
                Guid.NewGuid(),
                subject,
                "batch-v2",
                new[] { next },
                first.DesiredGeneration
            );

        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetInputContributionsAsync(subject))
            .Contributions.Single()
            .Revision.Should()
            .Be(1);
    }

    [Test]
    public async Task CompleteMembershipRejectsANewPeerEvenWhenItsResourceUnionIsIdentical()
    {
        var subject = Subject();
        var input = await fixture.Capture("Shared membership input", "people");
        var a = Contributor();
        var peer = Contributor();
        await Batch(subject, [Prepared(a, 0, input)]);
        var before = await fixture.Work.GetInputContributionsAsync(subject);
        await Batch(subject, [Prepared(peer, 0, input)]);
        var stale = () =>
            fixture.Work.BeginContributedBatchAsync(
                Guid.NewGuid(),
                subject,
                "batch-v1",
                new[] { Prepared(a, 1, input) },
                expectedContributionRevisions: Revisions(before)
            );

        await stale
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*membership changed*");
        var after = await fixture.Work.GetInputContributionsAsync(subject);
        after.Contributions.Should().HaveCount(2).And.OnlyContain(state => state.Revision == 1);
    }

    [Test]
    public async Task CompleteMembershipIncludesEmptyTombstonesAndTheirRevisions()
    {
        var subject = Subject();
        var input = await fixture.Capture("Tombstone membership input", "people");
        var a = Contributor();
        var tombstone = Contributor();
        await Batch(subject, [Prepared(a, 0, input), new(new(tombstone, 0, [], []), [])]);
        var before = await fixture.Work.GetInputContributionsAsync(subject);
        await Batch(subject, [new(new(tombstone, 1, [], []), [])]);
        var stale = () =>
            fixture.Work.BeginContributedBatchAsync(
                Guid.NewGuid(),
                subject,
                "batch-v1",
                new[] { Prepared(a, 1, input) },
                expectedContributionRevisions: Revisions(before)
            );

        await stale
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*membership changed*");
        (await fixture.Work.GetInputContributionAsync(subject, a))!.Revision.Should().Be(1);
        (await fixture.Work.GetInputContributionAsync(subject, tombstone))!.Revision.Should().Be(2);
    }

    [Test]
    public async Task EmptyMembershipCannotHideNewUnmanagedBindingsWithTheSameResources()
    {
        var subject = Subject();
        var input = await fixture.Capture("New unmanaged input", "people");
        var before = await fixture.Work.GetInputContributionsAsync(subject);
        before.Owner.Should().BeNull();
        await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "batch-v1",
            [input.Resource],
            []
        );
        var stale = () =>
            fixture.Work.BeginContributedBatchAsync(
                Guid.NewGuid(),
                subject,
                "batch-v1",
                new[] { Prepared(Contributor(), 0, input) },
                expectedContributionRevisions: Revisions(before)
            );

        await stale
            .Should()
            .ThrowAsync<NormalizationConflictException>()
            .WithMessage("*membership changed*");
        var after = await fixture.Work.GetInputContributionsAsync(subject);
        after.Contributions.Should().BeEmpty();
        after.UnmanagedBaseline!.Resources.Should().Equal(input.Resource);
    }

    [Test]
    public async Task MembershipReplayUsesTheSavedObservationBeforeCheckingCurrentRevisions()
    {
        var subject = Subject();
        var input = await fixture.Capture("Membership replay", "people");
        var prepared = Prepared(Contributor(), 0, input);
        var workUuid = Guid.NewGuid();
        var first = await fixture.Work.BeginContributedBatchAsync(
            workUuid,
            subject,
            "batch-v1",
            new[] { prepared },
            expectedContributionRevisions: []
        );

        var replay = await fixture.Work.BeginContributedBatchAsync(
            workUuid,
            subject,
            "batch-v1",
            new[] { prepared },
            expectedContributionRevisions: []
        );

        replay.Should().BeEquivalentTo(first);
        var current = await fixture.Work.GetInputContributionsAsync(subject);
        current.Contributions.Single().Revision.Should().Be(1);
        var changed = () =>
            fixture.Work.BeginContributedBatchAsync(
                workUuid,
                subject,
                "batch-v1",
                new[] { prepared },
                expectedContributionRevisions: Revisions(current)
            );
        await changed.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task PreCancelledBatchAndSnapshotLeaveTheOwnerUnregistered()
    {
        var subject = Subject();
        var input = await fixture.Capture("Cancelled batch", "people");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var batch = () =>
            fixture.Work.BeginContributedBatchAsync(
                Guid.NewGuid(),
                subject,
                "batch-v1",
                new[] { Prepared(Contributor(), 0, input) },
                cancellationToken: cancellation.Token
            );
        var snapshot = () =>
            fixture.Work.GetInputContributionsAsync(subject, cancellationToken: cancellation.Token);

        await batch.Should().ThrowAsync<OperationCanceledException>();
        await snapshot.Should().ThrowAsync<OperationCanceledException>();
        (await fixture.Work.GetSubjectAsync(subject)).Should().BeNull();
    }

    private Task<NormalizationWorkTicket> Batch(
        NormalizationSubjectKey subject,
        IReadOnlyList<NormalizationPreparedInputContribution> contributions,
        Guid? workUuid = null
    ) =>
        fixture.Work.BeginContributedBatchAsync(
            workUuid ?? Guid.NewGuid(),
            subject,
            "batch-v1",
            contributions
        );

    private static NormalizationInputContributionRevision[] Revisions(
        NormalizationInputContributionsSnapshot snapshot
    ) =>
        snapshot
            .Contributions.Select(state => new NormalizationInputContributionRevision(
                state.Contributor,
                state.Revision
            ))
            .ToArray();

    private static NormalizationPreparedInputContribution Prepared(
        NormalizationResourceKey contributor,
        long revision,
        DomainInput input
    ) =>
        new(
            new(contributor, revision, [input.Resource], []),
            [new(input.Reference.InputRevisionUuid, input.Resource)]
        );

    private static NormalizationResourceKey Contributor() =>
        new("test", "batch", Guid.NewGuid().ToString("N"));

    private static NormalizationSubjectKey Subject() =>
        new("extractions", "recording", Guid.NewGuid());

    private static PostgresException? FindPostgres(Exception error) =>
        error as PostgresException
        ?? (error.InnerException == null ? null : FindPostgres(error.InnerException));
}
