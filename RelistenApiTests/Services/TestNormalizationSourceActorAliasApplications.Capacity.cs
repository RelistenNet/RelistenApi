using System.Text;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test, Category("SourceAliasScale"), CancelAfter(3_600_000)]
    public async Task OneThousandAndOnePublicSourceApplicationsRetainEveryScopeAndOwnerWhenOneSourceBecomesIncomplete()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await SelectPolicy(Policy());
        var name = Unique("Scale Robin");
        var alias = Unique("Scale Finch");
        var person = await Person(name);
        var sources = new List<SourceContext>();
        ExpandedApplication? latest = null;
        for (var index = 0; index < 1001; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Every authority row comes from real extraction, Resolve and Apply. No direct
            // accepted-row SQL seeding substitutes for producer authentication or publication.
            var source = await Source(Personnel(name, alias));
            sources.Add(source);
            latest = await publisher.ApplyAsync(
                Guid.NewGuid(),
                source.Resolution.AttemptUuid,
                cancellationToken
            );
            latest
                .Result!.Disposition.Should()
                .Be(
                    SourceActorAliasApplicationDisposition.Complete,
                    "source {0} must commit through the public lifecycle",
                    index
                );
        }
        var map = Receipts(latest!).Last().ResultingMap;
        map.Select(entry => entry.Scope.SourceUuid).Distinct().Should().HaveCount(1001);
        map.Should().OnlyContain(entry => entry.Bindings.Count > 0);
        var sourceUuids = sources.Select(source => source.Source.SourceUuid).ToArray();
        var observations = latest!
            .Stages.Last(stage => stage.Receipt != null)
            .Facts.CurrentReadSet.OwnerObservations!.Concat(
                latest.Stages.Last(stage => stage.Receipt != null).Facts.CarryObservations
            )
            .Select(owner => owner.SubjectUuid)
            .ToHashSet();
        var requiredOwners = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                """
                SELECT DISTINCT source_resolution_owner_uuid FROM normalization.source_actor_alias_assignments
                    WHERE source_uuid=ANY(@sourceUuids) AND retired_work_uuid IS NULL
                UNION SELECT DISTINCT extraction_owner_uuid FROM normalization.source_actor_alias_assignments
                    WHERE source_uuid=ANY(@sourceUuids) AND retired_work_uuid IS NULL
                """,
                new { sourceUuids }
            )
        );
        observations.Should().Contain(requiredOwners);
        var membership = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", person),
            new(100, 100000)
        );
        membership
            .Contributions.Count(contribution =>
                contribution.Contributor == SourceActorAliasApplicationStore.StableContributor
            )
            .Should()
            .Be(1);
        var retained = map.Where(entry => entry.Scope.SourceUuid != sources[0].Source.SourceUuid)
            .ToArray();
        var changed = await Replace(sources[0], null);

        var partial = await publisher.ApplyAsync(
            Guid.NewGuid(),
            changed.Resolution.AttemptUuid,
            cancellationToken
        );

        partial.Result!.Disposition.Should().NotBe(SourceActorAliasApplicationDisposition.Complete);
        Receipts(partial)
            .Should()
            .NotBeEmpty(
                "the incomplete source observation must be retained without losing prior authority"
            );
        var current = Receipts(partial).Last().ResultingMap;
        current
            .Where(entry => entry.Scope.SourceUuid != changed.Source.SourceUuid)
            .Should()
            .BeEquivalentTo(
                retained,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == alias);
        var encodedSizes = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<int>(
                """
                SELECT octet_length(stage::text) FROM normalization.source_actor_alias_person_stages
                WHERE application_uuid=ANY(@applications)
                """,
                new { applications = new[] { latest.AttemptUuid, partial.AttemptUuid } }
            )
        );
        // JSONB transport adds whitespace; canonical codec acceptance is checked separately.
        encodedSizes.Should().NotBeEmpty();
        foreach (var stage in latest.Stages.Concat(partial.Stages))
        {
            var body = stage with { Ticket = null, Receipt = null, TerminalReason = null };
            Encoding
                .UTF8.GetByteCount(SourceActorAliasApplicationCheckpointCodec.WriteStage(body))
                .Should()
                .BeLessThanOrEqualTo(
                    SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes
                );
        }
    }

    [TestCase(50)]
    [TestCase(1000)]
    [Category("SourceAliasScale"), CancelAfter(3_600_000)]
    public async Task RepeatedManyClaimApplicationsKeepCompleteProofInIndependentlyBoundedStages(
        int count
    )
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await SelectPolicy(Policy());
        var name = Unique("Manyclaim Robin");
        var person = await Person(name);
        var aliases = Enumerable.Range(0, count).Select(_ => Unique("Manyclaim Finch")).ToArray();
        var text =
            "Personnel:\n"
            + string.Join("\n", aliases.Select(alias => $"{name} aka {alias} - vocals"));
        var source = await Source(text);
        Claims(source).Should().HaveCount(count);
        var first = await publisher.ApplyAsync(
            Guid.NewGuid(),
            source.Resolution.AttemptUuid,
            cancellationToken
        );
        first.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        var current = await Resolve(source.Source);

        var repeated = await publisher.ApplyAsync(
            Guid.NewGuid(),
            current.Resolution.AttemptUuid,
            cancellationToken
        );

        repeated.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        (await CurrentAliases(person)).Select(value => value.Wording).Should().Contain(aliases);
        foreach (var stage in first.Stages.Concat(repeated.Stages))
        {
            var body = stage with { Ticket = null, Receipt = null, TerminalReason = null };
            var encoded = SourceActorAliasApplicationCheckpointCodec.WriteStage(body);
            Encoding
                .UTF8.GetByteCount(encoded)
                .Should()
                .BeLessThanOrEqualTo(
                    SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes
                );
            SourceActorAliasApplicationCheckpointCodec
                .ReadStage(encoded)
                .Should()
                .BeEquivalentTo(
                    body,
                    options => options.WithStrictOrdering().AllowingInfiniteRecursion()
                );
        }
    }

    [Test]
    public async Task UnknownPeerTombstoneIsPreservedAndRefusesSourceAdmission()
    {
        await SelectPolicy(Policy());
        var name = Unique("Unsupported peer Robin");
        var person = await Person(name);
        await AddOpaquePeers(person, 1);
        var source = await Source(Personnel(name, Unique("Unsupported peer Finch")));
        var before = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", person),
            new(100, 100000)
        );
        var accepted = await AcceptedAuthority();

        var result = await Apply(source);

        result.Result!.Disposition.Should().NotBe(SourceActorAliasApplicationDisposition.Complete);
        result
            .Stages.Should()
            .BeEmpty("a generic Work receipt cannot authenticate this domain's opaque peer");
        (
            await fixture.Work.GetInputContributionsAsync(
                new("people", "person", person),
                new(100, 100000)
            )
        )
            .Should()
            .BeEquivalentTo(before);
        (await AcceptedAuthority()).Should().Be(accepted);
    }

    [Test]
    public async Task MoreThanOneHundredCompleteContributorHeadersRefuseBeforeAnySourceAdmission()
    {
        await SelectPolicy(Policy());
        var name = Unique("Membership overflow Robin");
        var person = await Person(name);
        await AddOpaquePeers(person, 100);
        var source = await Source(Personnel(name, Unique("Membership overflow Finch")));
        var before = await fixture.Work.GetInputContributionsAsync(
            new("people", "person", person),
            new(101, 100000)
        );
        before
            .Contributions.Should()
            .HaveCount(101, "the original baseline and 100 new empty tombstones all count");
        var uuid = Guid.NewGuid();
        var accepted = await AcceptedAuthority();

        var apply = () => publisher.ApplyAsync(uuid, source.Resolution.AttemptUuid);
        var failure = await apply.Should().ThrowAsync<Exception>();

        HasMembershipCapacity(failure.Which).Should().BeTrue();
        var retained = await ReadExpandedApplication(uuid);
        retained!.Stages.Should().BeEmpty("capacity refusal must precede partial stage admission");
        (
            await fixture.Work.GetInputContributionsAsync(
                new("people", "person", person),
                new(101, 100000)
            )
        )
            .Should()
            .BeEquivalentTo(before);
        (await AcceptedAuthority()).Should().Be(accepted);
    }

    private async Task AddOpaquePeers(Guid person, int count)
    {
        var owner = new NormalizationSubjectKey("people", "person", person);
        var prior = await fixture.Work.GetInputContributionsAsync(owner);
        var expected = prior
            .Contributions.Select(value => new NormalizationInputContributionRevision(
                value.Contributor,
                value.Revision
            ))
            .Concat(prior.UnmanagedBaseline is { } baseline ? [new(baseline.Contributor, 0)] : [])
            .ToArray();
        var replacements = Enumerable
            .Range(0, count)
            .Select(_ => new NormalizationPreparedInputContribution(
                new(
                    new("test", "opaque_source_alias_peer", Guid.NewGuid().ToString("N")),
                    0,
                    [],
                    []
                ),
                []
            ))
            .ToArray();
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            owner,
            "opaque-peer-fixture-v1",
            replacements,
            prior.Owner!.DesiredGeneration,
            expectedContributionRevisions: expected
        );
        var result = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) => Task.CompletedTask
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    private static bool HasMembershipCapacity(Exception exception) =>
        exception is NormalizationInputContributionsCapacityException
        || exception.InnerException != null && HasMembershipCapacity(exception.InnerException);

    [Test]
    public void SourceCheckpointByteCapAcceptsExactlyThirtyTwoMiBAndRefusesTheNextByteWithoutTruncation()
    {
        var result = new SourceActorAliasApplicationResult(
            Guid.NewGuid(),
            Guid.NewGuid(),
            SourceActorAliasApplicationDisposition.Review,
            "",
            [],
            []
        );
        var overhead = Encoding.UTF8.GetByteCount(
            SourceActorAliasApplicationCheckpointCodec.WriteResult(result)
        );
        var exact = result with
        {
            Reason = new string(
                'a',
                SourceActorAliasApplicationCheckpointCodec.MaximumCheckpointBytes - overhead
            ),
        };

        var encoded = SourceActorAliasApplicationCheckpointCodec.WriteResult(exact);

        Encoding.UTF8.GetByteCount(encoded).Should().Be(32 * 1024 * 1024);
        SourceActorAliasApplicationCheckpointCodec
            .ReadResult(encoded)
            .Reason.Should()
            .Be(exact.Reason);
        var overflow = () =>
            SourceActorAliasApplicationCheckpointCodec.WriteResult(
                exact with
                {
                    Reason = exact.Reason + "a",
                }
            );
        overflow.Should().Throw<ArgumentException>().WithMessage("*no proof*truncated*");
    }
}
