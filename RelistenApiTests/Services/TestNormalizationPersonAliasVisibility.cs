using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationPersonAliasVisibility
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("reject", "rejected")]
    [TestCase("retract", "superseded")]
    public async Task CanonicalExclusionHidesBothPreexistingAliasesInMergedComponent(
        string action,
        string state
    )
    {
        var name = AliasName();
        var original = await Person(name);
        var survivor = await Person(name);
        var unrelated = await Person(name);
        var all = new[] { original.Person.Uuid, survivor.Person.Uuid, unrelated.Person.Uuid };
        await AssertVisibility(name, all, all);
        var beforeMerge = await StoredClaims(all);

        await Merge(original, survivor);

        (await StoredClaims(all)).Should().Be(beforeMerge);
        await AssertVisibility(name, all, [survivor.Person.Uuid, unrelated.Person.Uuid]);
        await AssertFacts([original, survivor], survivor, [name]);
        var otherOriginals = await StoredClaims(survivor.Person.Uuid, unrelated.Person.Uuid);

        await Review(original, action);

        (await StoredClaims(survivor.Person.Uuid, unrelated.Person.Uuid))
            .Should()
            .Be(otherOriginals, "review changes only the reviewed original's raw claims");
        (await AliasState(original.Alias.Uuid))
            .Should()
            .Be(new RawAliasState(original.Person.Uuid, state, "reviewed"));
        (await AliasState(survivor.Alias.Uuid))
            .Should()
            .Be(new RawAliasState(survivor.Person.Uuid, "accepted", "provider_assertion"));
        await AssertVisibility(name, [unrelated.Person.Uuid], [unrelated.Person.Uuid]);
        await AssertFacts([original, survivor], survivor, []);
        await AssertFacts([unrelated], unrelated, [name]);
    }

    [Test]
    public async Task ReviewedAcceptanceOfExistingDuplicateOverridesItsSupersededSiblingHistory()
    {
        var name = AliasName();
        var first = await Person(name);
        var duplicate = await AppendAlias(first, name);
        var before = await StoredClaims(first.Person.Uuid);
        (await AliasState(first.Alias.Uuid)).State.Should().Be("accepted");
        (await AliasState(duplicate.Alias.Uuid)).State.Should().Be("accepted");

        await Review(duplicate, "accept");

        (await AliasState(first.Alias.Uuid))
            .Should()
            .Be(new RawAliasState(first.Person.Uuid, "superseded", "reviewed"));
        (await AliasState(duplicate.Alias.Uuid))
            .Should()
            .Be(new RawAliasState(first.Person.Uuid, "accepted", "reviewed"));
        var after = await StoredClaims(first.Person.Uuid);
        after.UnmanagedSupport.Should().Be(before.UnmanagedSupport);
        after.History.Should().NotBe(before.History);
        var visibleUuids = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT uuid FROM normalization.current_person_aliases WHERE person_uuid=@person AND name_key=@key",
                new { person = first.Person.Uuid, key = NormalizedPeopleService.NameKey(name) }
            )
        );
        visibleUuids.Should().Equal(duplicate.Alias.Uuid);
        await AssertVisibility(name, [first.Person.Uuid], [first.Person.Uuid]);
        await AssertFacts([first], first, [name]);
    }

    [TestCase("reject")]
    [TestCase("retract")]
    public async Task NewReviewedUuidRestoresSameOriginalWithoutRewritingItsNegative(string action)
    {
        var name = AliasName();
        var original = await Person(name);
        await Review(original, action);
        await AssertVisibility(name, [], []);
        var negative = await RawAlias(original.Alias.Uuid);
        var before = await StoredClaims(original.Person.Uuid);
        var restored = original.Alias with { Uuid = Guid.NewGuid() };

        await Review(original, "accept", restored);

        (await RawAlias(original.Alias.Uuid)).Should().Be(negative);
        (await AliasState(restored.Uuid))
            .Should()
            .Be(new RawAliasState(original.Person.Uuid, "accepted", "reviewed"));
        var after = await StoredClaims(original.Person.Uuid);
        after.History.Should().Be(before.History);
        after.UnmanagedSupport.Should().Be(before.UnmanagedSupport);
        await AssertVisibility(name, [original.Person.Uuid], [original.Person.Uuid]);
        await AssertFacts([original], original, [name]);
    }

    [TestCase("provider_assertion")]
    [TestCase("validated_match")]
    public async Task ReplacementHistoryWithAutomaticBasisExcludesOldNameUntilExplicitRestoration(
        string basis
    )
    {
        var oldName = AliasName();
        var newName = AliasName();
        var original = await Person(oldName, basis, newName);
        var survivor = await Person(oldName);
        await Merge(original, survivor);
        var survivorClaims = await StoredClaims(survivor.Person.Uuid);

        await Review(original, "replace", original.Alias with { Wording = newName });

        (await StoredClaims(survivor.Person.Uuid)).Should().Be(survivorClaims);
        var retainedOldBasis = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT basis FROM normalization.person_aliases_history WHERE uuid=@Uuid AND name_key=@key",
                new { original.Alias.Uuid, key = NormalizedPeopleService.NameKey(oldName) }
            )
        );
        retainedOldBasis.Should().Be(basis);
        await AssertVisibility(oldName, [], []);
        await AssertVisibility(newName, [original.Person.Uuid], [survivor.Person.Uuid]);
        await AssertFacts([original, survivor], survivor, [newName]);
        var beforeRestore = await StoredClaims(original.Person.Uuid);
        var restored = original.Alias with { Uuid = Guid.NewGuid() };

        await Review(original, "accept", restored);

        (await StoredClaims(original.Person.Uuid)).History.Should().Be(beforeRestore.History);
        (await StoredClaims(survivor.Person.Uuid)).Should().Be(survivorClaims);
        await AssertVisibility(
            oldName,
            [original.Person.Uuid, survivor.Person.Uuid],
            [survivor.Person.Uuid]
        );
        await AssertVisibility(newName, [original.Person.Uuid], [survivor.Person.Uuid]);
        await AssertFacts([original, survivor], survivor, [oldName, newName]);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DifferentOriginalReviewedAcceptanceCannotOverrideExclusionUntilRestoreOrReversal(
        bool restoreExcludedOriginal
    )
    {
        var name = AliasName();
        var accepted = await Person(name);
        var excluded = await Person(name);
        await Review(accepted, "accept");
        await Review(excluded, "reject");
        await AssertVisibility(name, [accepted.Person.Uuid], [accepted.Person.Uuid]);
        var beforeMerge = await StoredClaims(accepted.Person.Uuid, excluded.Person.Uuid);

        var operation = await Merge(excluded, accepted);

        (await StoredClaims(accepted.Person.Uuid, excluded.Person.Uuid)).Should().Be(beforeMerge);
        await AssertVisibility(name, [], []);
        await AssertFacts([accepted, excluded], accepted, []);
        var excludedRaw = await RawAlias(excluded.Alias.Uuid);
        if (restoreExcludedOriginal)
        {
            var acceptedClaims = await StoredClaims(accepted.Person.Uuid);
            await Review(excluded, "accept", excluded.Alias with { Uuid = Guid.NewGuid() });

            (await StoredClaims(accepted.Person.Uuid)).Should().Be(acceptedClaims);
            await AssertVisibility(
                name,
                [accepted.Person.Uuid, excluded.Person.Uuid],
                [accepted.Person.Uuid]
            );
            await AssertFacts([accepted, excluded], accepted, [name]);
        }
        else
        {
            await Reverse(excluded, accepted, operation);

            (await StoredClaims(accepted.Person.Uuid, excluded.Person.Uuid))
                .Should()
                .Be(beforeMerge);
            await AssertVisibility(name, [accepted.Person.Uuid], [accepted.Person.Uuid]);
            await AssertFacts([accepted], accepted, [name]);
            await AssertFacts([excluded], excluded, []);
        }
        (await RawAlias(excluded.Alias.Uuid)).Should().Be(excludedRaw);
    }

    [Test]
    public async Task MultihopReversalRecomputesVisibilityWithoutMovingOriginalClaimsOrSupports()
    {
        var name = AliasName();
        var first = await Person(name);
        var middle = await Person(name);
        var last = await Person(name);
        var all = new[] { first.Person.Uuid, middle.Person.Uuid, last.Person.Uuid };
        var originalClaims = await StoredClaims(all);

        var firstMerge = await Merge(first, middle);
        var secondMerge = await Merge(middle, last);

        (await StoredClaims(all)).Should().Be(originalClaims);
        await AssertVisibility(name, all, [last.Person.Uuid]);
        var otherOriginals = await StoredClaims(first.Person.Uuid, middle.Person.Uuid);
        await Review(last, "reject");
        (await StoredClaims(first.Person.Uuid, middle.Person.Uuid)).Should().Be(otherOriginals);
        var reviewedClaims = await StoredClaims(all);
        await AssertVisibility(name, [], []);
        var hidden = await AssertFacts([first, middle, last], last, []);
        hidden
            .Redirects.Select(redirect => (redirect.OriginalUuid, redirect.SurvivorUuid))
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    (first.Person.Uuid, middle.Person.Uuid),
                    (middle.Person.Uuid, last.Person.Uuid),
                }
            );

        await Reverse(middle, last, secondMerge);

        (await StoredClaims(all)).Should().Be(reviewedClaims);
        await AssertVisibility(name, [first.Person.Uuid, middle.Person.Uuid], [middle.Person.Uuid]);
        var separated = await AssertFacts([first, middle], middle, [name]);
        separated
            .Redirects.Should()
            .ContainSingle()
            .Which.OriginalUuid.Should()
            .Be(first.Person.Uuid);
        await AssertFacts([last], last, []);

        await Reverse(first, middle, firstMerge);

        (await StoredClaims(all)).Should().Be(reviewedClaims);
        await AssertVisibility(
            name,
            [first.Person.Uuid, middle.Person.Uuid],
            [first.Person.Uuid, middle.Person.Uuid]
        );
        (await AssertFacts([first], first, [name])).Redirects.Should().BeEmpty();
        (await AssertFacts([middle], middle, [name])).Redirects.Should().BeEmpty();
        await AssertFacts([last], last, []);
    }

    [Test]
    public async Task PendingClaimAloneDoesNotExcludeAnIndependentAcceptedAliasAfterComponentSplit()
    {
        var name = AliasName();
        var otherName = AliasName();
        var excluded = await Person(name);
        var pendingOriginal = await Person(otherName);
        var accepted = await Person(name);
        await Review(excluded, "reject");
        var operation = await Merge(pendingOriginal, excluded);

        // The automatic writer retains a changed citation as pending while this original
        // belongs to the rejected component. It has no canonical review of this alias itself.
        var changed = await fixture.Capture(
            $"{pendingOriginal.Person.DisplayName}; {name}; changed citation",
            "people"
        );
        (
            await fixture.PublishPeople(
                people,
                changed,
                new(
                    [],
                    [new(Guid.NewGuid(), pendingOriginal.Person.Uuid, name, changed.Reference)],
                    [],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var pendingStates = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                "SELECT state FROM normalization.person_aliases WHERE person_uuid=@person AND name_key=@key",
                new
                {
                    person = pendingOriginal.Person.Uuid,
                    key = NormalizedPeopleService.NameKey(name),
                }
            )
        );
        pendingStates.Should().Equal("pending");
        var ownHistory = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "SELECT count(*) FROM normalization.person_aliases_history WHERE person_uuid=@person AND name_key=@key",
                new
                {
                    person = pendingOriginal.Person.Uuid,
                    key = NormalizedPeopleService.NameKey(name),
                }
            )
        );
        ownHistory.Should().Be(0);
        var all = new[] { pendingOriginal.Person.Uuid, excluded.Person.Uuid, accepted.Person.Uuid };
        var retained = await StoredClaims(all);

        await Reverse(pendingOriginal, excluded, operation);
        await Merge(pendingOriginal, accepted);

        (await StoredClaims(all)).Should().Be(retained);
        await AssertVisibility(name, [accepted.Person.Uuid], [accepted.Person.Uuid]);
        await AssertFacts([pendingOriginal, accepted], accepted, [otherName, name]);
        await AssertFacts([excluded], excluded, []);
    }

    private static string AliasName() => "Alias " + Guid.NewGuid().ToString("N");

    private async Task<AliasPerson> Person(
        string alias,
        string basis = "provider_assertion",
        string additionalText = ""
    )
    {
        var person = new CanonicalPerson(
            Guid.NewGuid(),
            "Identity " + Guid.NewGuid().ToString("N")
        );
        var input = await fixture.Capture(
            $"{person.DisplayName}; {alias}; {additionalText}",
            "people"
        );
        var assertion = new PersonAlias(Guid.NewGuid(), person.Uuid, alias, input.Reference, basis);
        (await fixture.PublishPeople(people, input, new([person], [assertion], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return new(person, input, assertion);
    }

    private async Task<AliasPerson> AppendAlias(AliasPerson original, string wording)
    {
        var input = await fixture.Capture(
            $"{original.Person.DisplayName}; {wording}; evidence {Guid.NewGuid():N}",
            "people"
        );
        var alias = new PersonAlias(Guid.NewGuid(), original.Person.Uuid, wording, input.Reference);
        (await fixture.PublishPeople(people, input, new([], [alias], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return original with { Input = input, Alias = alias };
    }

    private async Task Review(AliasPerson original, string action, PersonAlias? assertion = null)
    {
        var alias = assertion ?? original.Alias;
        // New reviewed assertions require a current original-owner binding to their genuine
        // retained evidence. Configuration and review are both exercised through Work APIs.
        await fixture.Own(original.Input, "person", original.Person.Uuid);
        var owner = (await fixture.Work.GetSubjectAsync(Owner(original)))!;
        var assertionRevision = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<long?>(
                "SELECT revision FROM normalization.person_aliases WHERE uuid=@Uuid",
                alias
            )
        );
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                original.Person.Uuid,
                await PersonRevision(original),
                action,
                "Review this alias on its original identity",
                Alias: alias,
                ExpectedAssertionRevision: assertionRevision ?? 0
            ),
            "fixture-operator"
        );
    }

    private async Task<Guid> Merge(AliasPerson original, AliasPerson survivor)
    {
        var owner = (await fixture.Work.GetSubjectAsync(Owner(original)))!;
        var operation = Guid.NewGuid();
        await people.ApplyIdentityDecisionAsync(
            new(
                operation,
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original.Person.Uuid,
                await PersonRevision(original),
                survivor.Person,
                await PersonRevision(survivor),
                [],
                "These fixture identities describe the same person"
            ),
            "fixture-operator"
        );
        return operation;
    }

    private async Task Reverse(AliasPerson original, AliasPerson survivor, Guid merge)
    {
        var owner = (await fixture.Work.GetSubjectAsync(Owner(original)))!;
        await people.ReverseMergeAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                merge,
                original.Person.Uuid,
                survivor.Person.Uuid,
                await PersonRevision(original),
                await PersonRevision(survivor),
                "Restore these separate fixture identities"
            ),
            "fixture-operator"
        );
    }

    private async Task AssertVisibility(
        string wording,
        Guid[] visibleOriginals,
        Guid[] resolvedPeople
    )
    {
        (await people.FindCandidatesAsync(wording))
            .Select(person => person.Uuid)
            .Should()
            .BeEquivalentTo(resolvedPeople);
        var selection = await candidates.FindAsync(
            new(IdentityMatchingDomain.People, [wording], [], MinimumNameSimilarity: 0.99)
        );
        selection.NameCandidatesTruncated.Should().BeFalse();
        selection
            .Items.Where(hit => hit.Reasons.Contains("alias"))
            .Select(hit => hit.Uuid)
            .Should()
            .BeEquivalentTo(resolvedPeople);
        var current = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT DISTINCT person_uuid FROM normalization.current_person_aliases WHERE name_key=@key",
                new { key = NormalizedPeopleService.NameKey(wording) }
            )
        );
        current.Should().BeEquivalentTo(visibleOriginals);
        var resolved = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT DISTINCT resolved_person_uuid FROM normalization.resolved_person_aliases WHERE name_key=@key",
                new { key = NormalizedPeopleService.NameKey(wording) }
            )
        );
        resolved.Should().BeEquivalentTo(resolvedPeople);
    }

    private async Task<PersonCandidateFacts> AssertFacts(
        AliasPerson[] component,
        AliasPerson survivor,
        string[] aliases
    )
    {
        // Display-name selection keeps the person observable when every tested alias is
        // hidden, so an empty candidate pool cannot masquerade as a correct alias projection.
        var snapshot = await candidates.FindPeopleFactsAsync(
            new(
                IdentityMatchingDomain.People,
                [component[0].Person.DisplayName],
                [],
                MinimumNameSimilarity: 0.99
            )
        );
        snapshot.Version.Should().Be(NormalizationCandidateService.PeopleFactsVersion);
        snapshot.Complete.Should().BeTrue();
        var facts = snapshot.Facts.Should().ContainSingle().Which;
        facts.Hit.Uuid.Should().Be(survivor.Person.Uuid);
        facts
            .IdentityClosure.Select(identity => identity.Uuid)
            .Should()
            .BeEquivalentTo(component.Select(person => person.Person.Uuid));
        facts.AliasMatchingNamesComplete.Should().BeTrue();
        var expectedNames = component
            .Select(person => person.Person.DisplayName)
            .Concat(aliases)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        facts.AcceptedAliasMatchingNames.Should().Equal(expectedNames);
        facts
            .Aliases.Select(alias => alias.Wording)
            .Distinct(StringComparer.Ordinal)
            .Should()
            .BeEquivalentTo(expectedNames);
        foreach (var original in component)
        {
            var owner = (await fixture.Work.GetSubjectAsync(Owner(original)))!;
            snapshot
                .ReadSet.OwnerObservations.Should()
                .Contain(value =>
                    value.SubjectUuid == owner.SubjectUuid
                    && value.EditorialRevision == owner.EditorialRevision
                    && value.DesiredGeneration == owner.DesiredGeneration
                    && value.PublishedWorkUuid == owner.PublishedWorkUuid
                );
        }
        return facts;
    }

    private static NormalizationSubjectKey Owner(AliasPerson person) =>
        new("people", "person", person.Person.Uuid);

    private Task<long> PersonRevision(AliasPerson person) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@Uuid",
                person.Person
            )
        );

    private Task<RawAliasState> AliasState(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<RawAliasState>(
                "SELECT person_uuid AS \"PersonUuid\",state AS \"State\",basis AS \"Basis\" FROM normalization.person_aliases WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<string> RawAlias(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT to_jsonb(alias)::text FROM normalization.person_aliases alias WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private Task<ClaimSnapshot> StoredClaims(params Guid[] personUuids) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<ClaimSnapshot>(
                """
                SELECT
                    COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.uuid)
                        FROM normalization.person_aliases a WHERE a.person_uuid=ANY(@personUuids)), '[]'::jsonb)::text AS "Aliases",
                    COALESCE((SELECT jsonb_agg(to_jsonb(a) ORDER BY a.uuid,a.revision,a.superseding_decision_uuid)
                        FROM normalization.person_aliases_history a WHERE a.person_uuid=ANY(@personUuids)), '[]'::jsonb)::text AS "History",
                    COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.uuid)
                        FROM normalization.person_alias_unmanaged_support s
                        JOIN normalization.person_aliases a ON a.uuid=s.alias_uuid
                        WHERE a.person_uuid=ANY(@personUuids)), '[]'::jsonb)::text AS "UnmanagedSupport"
                """,
                new { personUuids }
            )
        );

    private sealed record AliasPerson(CanonicalPerson Person, DomainInput Input, PersonAlias Alias);

    private sealed record RawAliasState(Guid PersonUuid, string State, string Basis);

    private sealed record ClaimSnapshot(string Aliases, string History, string UnmanagedSupport);
}
