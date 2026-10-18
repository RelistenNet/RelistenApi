using System.Data;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPersonAliasVisibility
{
    [Test]
    public async Task LiveAliasSnapshotRetainsMergedFactsAcrossCompletedOriginalReview()
    {
        var name = AliasName();
        var original = await Person(name);
        var survivor = await Person(name);
        await Merge(original, survivor);
        var owners = await VisibilityOwnerObservations(original, survivor);
        var displayQuery = VisibilityQuery(original.Person.DisplayName);
        var aliasQuery = VisibilityQuery(name);

        await using var connection = fixture.Db.CreateConnection(false, false);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead
        );
        await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: transaction);
        using var reader = await candidates.CreatePeopleFactsSnapshotReaderAsync(
            connection,
            transaction
        );
        var before = await reader.FindAsync(displayQuery);
        AssertVisibilitySnapshot(before, [original, survivor], survivor, [name], owners);
        var beforeAlias = await reader.FindAsync(aliasQuery);
        beforeAlias.Facts.Should().ContainSingle().Which.Hit.Reasons.Should().Contain("alias");
        beforeAlias
            .Facts.Single()
            .Hit.Should()
            .BeEquivalentTo(beforeAlias.Selection.Items.Single());
        reader
            .BodyAcquisitionAttempts.Should()
            .Be(1, "both queries must exercise the retained merged body");

        // The completed facts read establishes the MVCC snapshot. Completion of the public
        // correction, while that transaction remains live, is the ordering proof; no timer
        // or blocked test callback stands in for a committed concurrent change.
        await Review(original, "reject");

        var retained = await reader.FindAsync(displayQuery);
        var retainedAlias = await reader.FindAsync(aliasQuery);
        AssertSameVisibilitySnapshot(before, retained);
        AssertSameVisibilitySnapshot(beforeAlias, retainedAlias);
        AssertVisibilitySnapshot(retained, [original, survivor], survivor, [name], owners);
        var uncached = await candidates.FindPeopleFactsInSnapshotAsync(
            connection,
            transaction,
            aliasQuery
        );
        AssertSameVisibilitySnapshot(uncached, retainedAlias);
        retainedAlias
            .Facts.Single()
            .Hit.Should()
            .BeEquivalentTo(retainedAlias.Selection.Items.Single());
        reader
            .BodyAcquisitionAttempts.Should()
            .Be(1, "the live snapshot can reuse the body after the outside review");

        await AssertVisibility(name, [], []);
        var current = await candidates.FindPeopleFactsAsync(displayQuery);
        AssertVisibilitySnapshot(
            current,
            [original, survivor],
            survivor,
            [],
            await VisibilityOwnerObservations(original, survivor)
        );
        current.ReadSet.CandidateUniverses.Should().NotEqual(before.ReadSet.CandidateUniverses);
        current
            .ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owners[0].SubjectUuid)
            .EditorialRevision.Should()
            .BeGreaterThan(owners[0].EditorialRevision);
        await AssertOldVisibilityAdmissionRefusedAndFreshAdmissionPublishes(before, current);
        await transaction.CommitAsync();
    }

    [Test]
    public async Task LiveAliasSnapshotRetainsMergedExclusionAcrossCompletedMergeReversal()
    {
        var name = AliasName();
        var excluded = await Person(name);
        var accepted = await Person(name);
        await Review(excluded, "reject");
        var merge = await Merge(excluded, accepted);
        await AssertVisibility(name, [], []);
        var owners = await VisibilityOwnerObservations(excluded, accepted);
        var originalQuery = VisibilityQuery(excluded.Person.DisplayName);
        var survivorQuery = VisibilityQuery(accepted.Person.DisplayName);

        await using var connection = fixture.Db.CreateConnection(false, false);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead
        );
        await connection.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: transaction);
        using var reader = await candidates.CreatePeopleFactsSnapshotReaderAsync(
            connection,
            transaction
        );
        var before = await reader.FindAsync(originalQuery);
        AssertVisibilitySnapshot(before, [excluded, accepted], accepted, [], owners);
        var beforeSurvivor = await reader.FindAsync(survivorQuery);
        beforeSurvivor
            .Facts.Single()
            .Hit.Should()
            .BeEquivalentTo(beforeSurvivor.Selection.Items.Single());
        reader
            .BodyAcquisitionAttempts.Should()
            .Be(1, "both queries must exercise the retained merged body");

        await Reverse(excluded, accepted, merge);

        var retained = await reader.FindAsync(originalQuery);
        var retainedSurvivor = await reader.FindAsync(survivorQuery);
        AssertSameVisibilitySnapshot(before, retained);
        AssertSameVisibilitySnapshot(beforeSurvivor, retainedSurvivor);
        AssertVisibilitySnapshot(retained, [excluded, accepted], accepted, [], owners);
        var uncached = await candidates.FindPeopleFactsInSnapshotAsync(
            connection,
            transaction,
            originalQuery
        );
        AssertSameVisibilitySnapshot(uncached, retained);
        reader
            .BodyAcquisitionAttempts.Should()
            .Be(1, "the live snapshot can reuse the body after the outside reversal");
        retained.Facts.Single().Hit.Uuid.Should().Be(accepted.Person.Uuid);
        retained
            .Facts.Single()
            .Redirects.Should()
            .ContainSingle()
            .Which.OriginalUuid.Should()
            .Be(excluded.Person.Uuid);

        await AssertVisibility(name, [accepted.Person.Uuid], [accepted.Person.Uuid]);
        var current = await candidates.FindPeopleFactsAsync(originalQuery);
        AssertVisibilitySnapshot(
            current,
            [excluded],
            excluded,
            [],
            await VisibilityOwnerObservations(excluded)
        );
        current.Facts.Single().Redirects.Should().BeEmpty();
        var currentSurvivor = await candidates.FindPeopleFactsAsync(survivorQuery);
        AssertVisibilitySnapshot(
            currentSurvivor,
            [accepted],
            accepted,
            [name],
            await VisibilityOwnerObservations(accepted)
        );
        currentSurvivor.Facts.Single().Redirects.Should().BeEmpty();
        current.ReadSet.CandidateUniverses.Should().NotEqual(before.ReadSet.CandidateUniverses);
        await AssertOldVisibilityAdmissionRefusedAndFreshAdmissionPublishes(before, current);
        await transaction.CommitAsync();
    }

    private static NormalizationCandidateQuery VisibilityQuery(string name) =>
        new(IdentityMatchingDomain.People, [name], [], MinimumNameSimilarity: 0.99);

    private async Task<NormalizationSubjectObservation[]> VisibilityOwnerObservations(
        params AliasPerson[] component
    )
    {
        var observations = new List<NormalizationSubjectObservation>();
        foreach (var person in component)
        {
            var owner = (await fixture.Work.GetSubjectAsync(Owner(person)))!;
            observations.Add(
                new(
                    owner.SubjectUuid,
                    owner.DesiredGeneration,
                    owner.EditorialRevision,
                    owner.PublishedWorkUuid
                )
            );
        }
        return observations.ToArray();
    }

    private static void AssertVisibilitySnapshot(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot,
        AliasPerson[] component,
        AliasPerson survivor,
        string[] aliases,
        NormalizationSubjectObservation[] owners
    )
    {
        snapshot.Complete.Should().BeTrue();
        var facts = snapshot.Facts.Should().ContainSingle().Which;
        facts.Hit.Uuid.Should().Be(survivor.Person.Uuid);
        facts.Hit.Should().BeEquivalentTo(snapshot.Selection.Items.Single());
        facts
            .IdentityClosure.Select(identity => identity.Uuid)
            .Should()
            .BeEquivalentTo(component.Select(person => person.Person.Uuid));
        facts.AliasMatchingNamesComplete.Should().BeTrue();
        var names = component
            .Select(person => person.Person.DisplayName)
            .Concat(aliases)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        facts.AcceptedAliasMatchingNames.Should().Equal(names);
        facts
            .Aliases.Select(alias => alias.Wording)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeEquivalentTo(names);
        snapshot.ReadSet.OwnerObservations.Should().BeEquivalentTo(owners);
    }

    private static void AssertSameVisibilitySnapshot(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> expected,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> actual
    ) =>
        actual
            .Should()
            .BeEquivalentTo(
                expected,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );

    private async Task AssertOldVisibilityAdmissionRefusedAndFreshAdmissionPublishes(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> old,
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> current
    )
    {
        var workUuid = Guid.NewGuid();
        var consumer = new NormalizationSubjectKey("people", "recording", Guid.NewGuid());
        Func<Task> stale = () =>
            fixture.Work.BeginWorkAsync(
                workUuid,
                consumer,
                "alias-visibility-snapshot-v1",
                [],
                [],
                catalogInputs: old.ReadSet.CatalogInputs,
                candidateUniverses: old.ReadSet.CandidateUniverses,
                expectedOwnerObservations: old.ReadSet.OwnerObservations
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await fixture.Work.GetSubjectAsync(consumer)).Should().BeNull();

        // Reusing the refused ID with the fresh public read proves that stale admission
        // left neither a configured consumer nor a durable request under that Work UUID.
        var ticket = await fixture.Work.BeginWorkAsync(
            workUuid,
            consumer,
            "alias-visibility-snapshot-v1",
            [],
            [],
            catalogInputs: current.ReadSet.CatalogInputs,
            candidateUniverses: current.ReadSet.CandidateUniverses,
            expectedOwnerObservations: current.ReadSet.OwnerObservations
        );
        var callbackRan = false;
        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
            {
                callbackRan = true;
                return Task.CompletedTask;
            }
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        callbackRan.Should().BeTrue();
    }
}
