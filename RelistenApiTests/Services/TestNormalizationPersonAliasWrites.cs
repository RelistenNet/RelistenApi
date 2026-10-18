using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Canonical alias outcomes do not grant source authority or erase editorial exclusions.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPersonAliasWrites
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task AppendPublishesActualAliasAndExactWorkReplayPreservesFactsAndAuthority()
    {
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", input.Reference);
        var owned = await fixture.Own(input, "person", person.Uuid);
        var publication = new PeoplePublication([person], [alias], [], []);

        (await people.PublishAsync(owned.Ticket.WorkUuid, publication))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var before = await State(person.Uuid);
        before.PersonRevision.Should().Be(1);
        before.AliasCount.Should().Be(1);
        before.UnmanagedCount.Should().Be(1);
        before.CurrentCount.Should().Be(1);

        (await people.PublishAsync(owned.Ticket.WorkUuid, publication)).Replayed.Should().BeTrue();
        var duplicate = await WriteTyped(input, alias with { Uuid = Guid.NewGuid() });
        duplicate.Disposition.Should().Be(CanonicalPersonAliasWriteDisposition.ExactDuplicate);
        duplicate.AliasUuid.Should().Be(alias.Uuid);
        duplicate.AcceptedFactChanged.Should().BeFalse();
        duplicate.AssertionInserted.Should().BeFalse();
        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );

        (await State(person.Uuid)).Should().Be(before);
    }

    [Test]
    public async Task HiddenRawAcceptedDuplicateAcquiresIndependentAppendAuthorityAndReactivates()
    {
        var name = "Hidden-" + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture("Pat; " + name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, name, input.Reference);

        // The canonical writer alone grants no current support. A future authenticated
        // source publisher must attach its own assignment/support in the same transaction.
        var accepted = await WriteTyped(input, alias);
        accepted.Disposition.Should().Be(CanonicalPersonAliasWriteDisposition.Accepted);
        accepted.AliasUuid.Should().Be(alias.Uuid);
        accepted.AcceptedFactChanged.Should().BeTrue();
        accepted.AssertionInserted.Should().BeTrue();
        (await people.FindCandidatesAsync(name)).Should().BeEmpty();
        var before = await State(person.Uuid);

        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );

        var after = await State(person.Uuid);
        after.AliasCount.Should().Be(before.AliasCount);
        after.UnmanagedCount.Should().Be(before.UnmanagedCount + 1);
        after.CurrentCount.Should().Be(before.CurrentCount + 1);
        after.PersonRevision.Should().Be(before.PersonRevision + 1);
        after.PeopleGeneration.Should().BeGreaterThan(before.PeopleGeneration);
        after.CompositionsGeneration.Should().BeGreaterThan(before.CompositionsGeneration);
        (await people.FindCandidatesAsync(name))
            .Should()
            .ContainSingle()
            .Which.Uuid.Should()
            .Be(person.Uuid);
        (await WriteTyped(input, alias with { Uuid = Guid.NewGuid() }))
            .AliasUuid.Should()
            .Be(alias.Uuid);

        await fixture.PublishPeople(people, input, new([], [alias], [], []));
        (await State(person.Uuid)).Should().Be(after);
    }

    [TestCase("reject")]
    [TestCase("retract")]
    public async Task ReviewedNaturalClaimBlocksNewUuidsAndChangedCitationsRemainPending(
        string action
    )
    {
        var name = "Excluded-" + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture("Pat; " + name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, name, input.Reference);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        var decision = new PersonMetadataDecision(
            Guid.NewGuid(),
            owner,
            0,
            person.Uuid,
            1,
            action,
            "Exclude this natural claim",
            Alias: alias,
            ExpectedAssertionRevision: 1
        );
        await people.ApplyMetadataDecisionAsync(decision, "fixture-operator");
        (await people.ApplyMetadataDecisionAsync(decision, "fixture-operator"))
            .Replayed.Should()
            .BeTrue();
        var reviewed = await State(person.Uuid);

        var protectedClaim = await WriteTyped(input, alias with { Uuid = Guid.NewGuid() });
        protectedClaim
            .Disposition.Should()
            .Be(CanonicalPersonAliasWriteDisposition.ProtectedReviewedNaturalClaim);
        protectedClaim.Accepted.Should().BeFalse();
        protectedClaim.AliasUuid.Should().Be(alias.Uuid);
        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );
        (await State(person.Uuid)).Should().Be(reviewed);

        var changed = await fixture.Capture("Revised statement; " + name, "people");
        var pendingAlias = alias with { Evidence = changed.Reference };
        await fixture.PublishPeople(people, changed, new([], [pendingAlias], [], []));
        var pendingState = await State(person.Uuid);
        pendingState.PersonRevision.Should().Be(reviewed.PersonRevision + 1);

        var pending = await WriteTyped(changed, pendingAlias);
        pending.Disposition.Should().Be(CanonicalPersonAliasWriteDisposition.Pending);
        pending.Accepted.Should().BeFalse();
        pending.AssertionInserted.Should().BeFalse();
        pending.AliasUuid.Should().NotBe(alias.Uuid);
        var replay = await WriteTyped(changed, pendingAlias with { Uuid = Guid.NewGuid() });
        replay.Should().Be(pending);
        await fixture.PublishPeople(people, changed, new([], [pendingAlias], [], []));
        await fixture.PublishPeople(
            people,
            changed,
            new([], [pendingAlias with { Uuid = Guid.NewGuid() }], [], [])
        );

        var after = await State(person.Uuid);
        after.Should().Be(pendingState);
        after.UnmanagedCount.Should().Be(reviewed.UnmanagedCount);
        after.CurrentCount.Should().Be(reviewed.CurrentCount);
        (await people.FindCandidatesAsync(name)).Should().BeEmpty();
        var pendingCount = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@person AND wording=@name AND state='pending'",
                new { person = person.Uuid, name }
            )
        );
        pendingCount.Should().Be(1);
    }

    [Test]
    public async Task ExactFactIncludesRawWordingBasisAndEveryCitationCoordinate()
    {
        var input = await fixture.CaptureJson(
            """{"first":"Pat Pat PAT","second":"Pat Pat PAT"}""",
            "people",
            "/first"
        );
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var first = input.Reference with { StartCodePoint = 0, EndCodePoint = 3, Quote = "Pat" };
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", first);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        PersonAlias[] independent =
        [
            alias with
            {
                Uuid = Guid.NewGuid(),
                Evidence = first with { StartCodePoint = 4, EndCodePoint = 7 },
            },
            alias with
            {
                Uuid = Guid.NewGuid(),
                Evidence = first with { JsonPointer = "/second" },
            },
            alias with
            {
                Uuid = Guid.NewGuid(),
                Basis = "validated_match",
            },
            alias with
            {
                Uuid = Guid.NewGuid(),
                Wording = "PAT",
                Evidence = first with { StartCodePoint = 8, EndCodePoint = 11, Quote = "PAT" },
            },
            alias with
            {
                Uuid = Guid.NewGuid(),
                Evidence = input.Reference,
            },
        ];
        await fixture.PublishPeople(people, input, new([], independent, [], []));
        var before = await State(person.Uuid);
        before.AliasCount.Should().Be(6);
        before.UnmanagedCount.Should().Be(6);
        foreach (var original in independent.Append(alias))
        {
            var duplicate = await WriteTyped(input, original with { Uuid = Guid.NewGuid() });
            duplicate.Disposition.Should().Be(CanonicalPersonAliasWriteDisposition.ExactDuplicate);
            duplicate.AliasUuid.Should().Be(original.Uuid);
        }
        await fixture.PublishPeople(
            people,
            input,
            new([], independent.Select(a => a with { Uuid = Guid.NewGuid() }).ToArray(), [], [])
        );
        (await State(person.Uuid)).Should().Be(before);
    }

    [Test]
    public async Task ConflictingUuidCannotHideBehindDuplicateAndRollsBackEarlierAppendSupport()
    {
        var input = await fixture.Capture("Pat; Wiz; New", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var existing = new PersonAlias(Guid.NewGuid(), person.Uuid, "Wiz", input.Reference);
        var matching = existing with { Uuid = Guid.NewGuid(), Basis = "validated_match" };
        await fixture.PublishPeople(people, input, new([person], [existing, matching], [], []));
        var typedConflict = await WriteTyped(input, existing with { Basis = "validated_match" });
        typedConflict.Disposition.Should().Be(CanonicalPersonAliasWriteDisposition.ConflictingUuid);
        typedConflict.Accepted.Should().BeFalse();
        var before = await State(person.Uuid);
        var owned = await fixture.Own(input, "person", person.Uuid);
        var first = new PersonAlias(Guid.NewGuid(), person.Uuid, "New", input.Reference);
        var publish = () =>
            people.PublishAsync(
                owned.Ticket.WorkUuid,
                new([], [first, existing with { Basis = "validated_match" }], [], [])
            );

        await publish.Should().ThrowAsync<NormalizationConflictException>();
        (await State(person.Uuid)).Should().Be(before);
        var firstExists = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.person_aliases WHERE uuid=@Uuid)",
                first
            )
        );
        firstExists.Should().BeFalse();

        // The failed transaction leaves no publication receipt or partial support. The
        // same admitted Work ticket can retry a coherent publication and then replay it.
        var corrected = new PeoplePublication([], [first, existing], [], []);
        (await people.PublishAsync(owned.Ticket.WorkUuid, corrected))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var after = await State(person.Uuid);
        after.AliasCount.Should().Be(before.AliasCount + 1);
        after.UnmanagedCount.Should().Be(before.UnmanagedCount + 1);
        after.PersonRevision.Should().Be(before.PersonRevision + 1);
        (await people.PublishAsync(owned.Ticket.WorkUuid, corrected)).Replayed.Should().BeTrue();
        (await State(person.Uuid)).Should().Be(after);
    }

    [Test]
    public async Task StaleMetadataReviewRollsBackBeforeRetryingTheSameOperation()
    {
        var input = await fixture.Capture("Pat; Wiz", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Wiz", input.Reference);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        var before = await State(person.Uuid);
        var stale = new PersonMetadataDecision(
            Guid.NewGuid(),
            owner,
            0,
            person.Uuid,
            before.PersonRevision,
            "reject",
            "Reject stale preparation",
            Alias: alias,
            ExpectedAssertionRevision: 0
        );
        var review = () => people.ApplyMetadataDecisionAsync(stale, "fixture-operator");

        await review.Should().ThrowAsync<NormalizationConflictException>();
        (await State(person.Uuid)).Should().Be(before);
        var hasReceipt = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<bool>(
                "SELECT EXISTS(SELECT 1 FROM normalization.review_operations WHERE uuid=@OperationUuid)",
                stale
            )
        );
        hasReceipt.Should().BeFalse();
        var historyCount = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.person_aliases_history WHERE uuid=@Uuid",
                alias
            )
        );
        historyCount.Should().Be(0);

        var corrected = stale with { ExpectedAssertionRevision = 1 };
        (await people.ApplyMetadataDecisionAsync(corrected, "fixture-operator"))
            .Replayed.Should()
            .BeFalse();
        var after = await State(person.Uuid);
        after.CurrentCount.Should().Be(before.CurrentCount - 1);
        after.UnmanagedCount.Should().Be(before.UnmanagedCount);
        after.PersonRevision.Should().Be(before.PersonRevision + 1);
        (await people.ApplyMetadataDecisionAsync(corrected, "fixture-operator"))
            .Replayed.Should()
            .BeTrue();
        (await State(person.Uuid)).Should().Be(after);
    }

    [TestCase("basis")]
    [TestCase("evidence")]
    [TestCase("owner")]
    public async Task PublicAppendRetainsAutomaticAuthorityEvidenceAndOwnerGuards(string invalid)
    {
        var input = await fixture.Capture("Pat; Wiz", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Wiz", input.Reference);
        var other = await fixture.Capture("Wiz", "people");
        var owned = await fixture.Own(
            input,
            "person",
            invalid == "owner" ? Guid.NewGuid() : person.Uuid
        );
        var before = await State(person.Uuid);
        var invalidAlias = invalid switch
        {
            "basis" => alias with { Basis = "reviewed" },
            "evidence" => alias with { Evidence = other.Reference },
            _ => alias,
        };
        var publish = () =>
            people.PublishAsync(owned.Ticket.WorkUuid, new([], [invalidAlias], [], []));

        await publish.Should().ThrowAsync<ArgumentException>();
        (await State(person.Uuid)).Should().Be(before);
    }

    private async Task<CanonicalPersonAliasWriteOutcome> WriteTyped(
        DomainInput input,
        PersonAlias alias
    )
    {
        var owned = await fixture.Own(input, "person", alias.PersonUuid);
        CanonicalPersonAliasWriteOutcome? outcome = null;
        var result = await fixture.Work.TryPublishAsync(
            owned.Ticket.WorkUuid,
            async (c, t, ct) =>
            {
                await NormalizedPeopleService.ValidatePublicationEvidenceAsync(
                    c,
                    t,
                    owned.Ticket.WorkUuid,
                    "people",
                    [alias.Evidence],
                    ct
                );
                outcome = await NormalizedPeopleService.WriteAutomaticAliasAsync(
                    c,
                    t,
                    owned.Ticket.WorkUuid,
                    alias,
                    new HashSet<Guid>(),
                    ct
                );
            },
            candidateWriteScopes:
            [
                NormalizationCandidateUniverseKey.Compositions,
                NormalizationCandidateUniverseKey.People,
            ]
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        return outcome!;
    }

    private Task<AliasState> State(Guid person) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AliasState>(
                """
                SELECT (SELECT revision FROM normalization.people WHERE uuid=@person) AS "PersonRevision",
                    (SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@person) AS "AliasCount",
                    (SELECT count(*) FROM normalization.person_alias_unmanaged_support s JOIN normalization.person_aliases a ON a.uuid=s.alias_uuid WHERE a.person_uuid=@person) AS "UnmanagedCount",
                    (SELECT count(*) FROM normalization.current_person_aliases WHERE person_uuid=@person) AS "CurrentCount",
                    (SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@global) AS "PeopleGeneration",
                    (SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='compositions' AND scope_uuid=@global) AS "CompositionsGeneration"
                """,
                new { person, global = Guid.Empty }
            )
        );

    private sealed record AliasState(
        long PersonRevision,
        long AliasCount,
        long UnmanagedCount,
        long CurrentCount,
        long PeopleGeneration,
        long CompositionsGeneration
    );
}
