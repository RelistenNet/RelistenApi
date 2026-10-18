using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPersonAliasWrites
{
    [TestCase(true, "reject")]
    [TestCase(false, "reject")]
    [TestCase(true, "retract")]
    [TestCase(false, "retract")]
    public async Task ReviewedClaimProtectsBothEndsOfRedirect(bool reviewOriginal, string action)
    {
        var name = "Component-handle-" + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture("Original Pat; Survivor Pat; " + name, "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Original Pat");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Survivor Pat");
        var reviewedPerson = reviewOriginal ? original : survivor;
        var writingPerson = reviewOriginal ? survivor : original;
        var reviewedAlias = new PersonAlias(
            Guid.NewGuid(),
            reviewedPerson.Uuid,
            name,
            input.Reference
        );
        await fixture.PublishPeople(
            people,
            input,
            new([original, survivor], [reviewedAlias], [], [])
        );
        await MergeAliasIdentities(original, survivor);
        await ReviewComponentAlias(reviewedAlias, action);
        var before = await State(writingPerson.Uuid);

        var alias = reviewedAlias with { Uuid = Guid.NewGuid(), PersonUuid = writingPerson.Uuid };
        await fixture.PublishPeople(people, input, new([], [alias], [], []));

        (await State(writingPerson.Uuid)).Should().Be(before);
        (await people.FindCandidatesAsync(name)).Should().BeEmpty();

        // A different source remains a retained pending assertion. It cannot acquire
        // independent support simply by choosing another original UUID in this component.
        var changed = await fixture.Capture("Another source names " + name, "people");
        await fixture.PublishPeople(
            people,
            changed,
            new([], [alias with { Evidence = changed.Reference }], [], [])
        );
        var after = await State(writingPerson.Uuid);
        after.AliasCount.Should().Be(before.AliasCount + 1);
        after.UnmanagedCount.Should().Be(before.UnmanagedCount);
        after.CurrentCount.Should().Be(before.CurrentCount);
        (await people.FindCandidatesAsync(name)).Should().BeEmpty();
        var pendingOwner = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT person_uuid FROM normalization.person_aliases WHERE person_uuid=@person AND wording=@name AND state='pending'",
                new { person = writingPerson.Uuid, name }
            )
        );
        pendingOwner.Should().Be(writingPerson.Uuid);
    }

    [Test]
    public async Task ReviewOnOneRetiredOriginalProtectsAnotherRetiredOriginal()
    {
        var name = "Shared-component-" + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture("Pat One; Pat Two; Pat Survivor; " + name, "people");
        var first = new CanonicalPerson(Guid.NewGuid(), "Pat One");
        var second = new CanonicalPerson(Guid.NewGuid(), "Pat Two");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Pat Survivor");
        var alias = new PersonAlias(Guid.NewGuid(), first.Uuid, name, input.Reference);
        await fixture.PublishPeople(people, input, new([first, second, survivor], [alias], [], []));
        await MergeAliasIdentities(first, survivor);
        await MergeAliasIdentities(second, survivor);
        await ReviewComponentAlias(alias, "reject");
        var before = await State(second.Uuid);

        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid(), PersonUuid = second.Uuid }], [], [])
        );

        (await State(second.Uuid)).Should().Be(before);
        (await people.FindCandidatesAsync(name)).Should().BeEmpty();
    }

    [Test]
    public async Task ReversingMergeRestoresIndependentAliasClaimsWithoutMovingOwnership()
    {
        var name = "Separate-again-" + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture("Original Pat; Survivor Pat; " + name, "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Original Pat");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Survivor Pat");
        var rejected = new PersonAlias(Guid.NewGuid(), survivor.Uuid, name, input.Reference);
        await fixture.PublishPeople(people, input, new([original, survivor], [rejected], [], []));
        var merge = await MergeAliasIdentities(original, survivor);
        await ReviewComponentAlias(rejected, "reject");
        var proposed = rejected with { Uuid = Guid.NewGuid(), PersonUuid = original.Uuid };
        await fixture.PublishPeople(people, input, new([], [proposed], [], []));
        (await people.FindCandidatesAsync(name)).Should().BeEmpty();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Uuid)
            )
        )!;

        await people.ReverseMergeAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                merge,
                original.Uuid,
                survivor.Uuid,
                (await State(original.Uuid)).PersonRevision,
                (await State(survivor.Uuid)).PersonRevision,
                "These fixture identities were merged incorrectly"
            ),
            "fixture-operator"
        );
        await fixture.PublishPeople(people, input, new([], [proposed], [], []));

        (await people.FindCandidatesAsync(name))
            .Should()
            .ContainSingle()
            .Which.Uuid.Should()
            .Be(original.Uuid);
        var storedOwner = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT person_uuid FROM normalization.person_aliases WHERE uuid=@Uuid",
                proposed
            )
        );
        storedOwner.Should().Be(original.Uuid);
    }

    private async Task<Guid> MergeAliasIdentities(
        CanonicalPerson original,
        CanonicalPerson survivor
    )
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Uuid)
            )
        )!;
        var operation = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                operation,
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original.Uuid,
                (await State(original.Uuid)).PersonRevision,
                survivor,
                (await State(survivor.Uuid)).PersonRevision,
                [],
                "Fixture identities describe the same person"
            ),
            "fixture-operator"
        );
        return operation;
    }

    private async Task ReviewComponentAlias(PersonAlias alias, string action)
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", alias.PersonUuid)
            )
        )!;
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                alias.PersonUuid,
                (await State(alias.PersonUuid)).PersonRevision,
                action,
                "Exclude this alias on the documented identity",
                Alias: alias,
                ExpectedAssertionRevision: 1
            ),
            "fixture-operator"
        );
    }
}
