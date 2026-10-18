using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPeopleNameSelection
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

    [Test]
    public async Task TenThousandDuplicateAssertionsCannotHideAnotherPerson()
    {
        var query = UniqueName();
        var crowded = await Person([query]);
        var rival = await Person([query + "x"]);
        await DuplicateAlias(crowded.Uuid, query, 10000);

        var snapshot = await candidates.FindPeopleFactsAsync(
            Query(query),
            new() { MaximumAssertionsPerKind = 2 }
        );

        snapshot.Complete.Should().BeTrue();
        snapshot.Selection.NameCandidatesTruncated.Should().BeFalse();
        snapshot
            .Facts.Select(fact => fact.Hit.Uuid)
            .Should()
            .BeEquivalentTo([crowded.Uuid, rival.Uuid]);
        var crowdedFacts = snapshot.Facts.Single(fact => fact.Hit.Uuid == crowded.Uuid);
        crowdedFacts.Hit.NameSimilarity.Should().Be(1);
        crowdedFacts.Hit.Reasons.Should().Equal("alias");
        crowdedFacts.AliasAssertionCount.Should().Be(10002);
        crowdedFacts.AcceptedAliasMatchingNames.Should().Contain(query);
        crowdedFacts.AliasMatchingNamesComplete.Should().BeTrue();
        crowdedFacts.Aliases.Should().HaveCount(2);
        crowdedFacts.AliasEvidenceTruncated.Should().BeTrue();
        snapshot
            .Facts.Single(fact => fact.Hit.Uuid == rival.Uuid)
            .Hit.NameSimilarity.Should()
            .BeLessThan(1);
    }

    [Test]
    public async Task DifferentNamesOnOnePersonCannotHideAnotherPerson()
    {
        var query = UniqueName();
        var variants = Enumerable
            .Range(0, 24)
            .Select(index => query + " variant " + index)
            .ToArray();
        var crowded = await Person(variants);
        var rival = await Person([query + "x"]);

        var result = await candidates.FindAsync(Query(query));

        result.NameCandidatesTruncated.Should().BeFalse();
        result.Items.Select(hit => hit.Uuid).Should().BeEquivalentTo([crowded.Uuid, rival.Uuid]);
        result.Items.Single(hit => hit.Uuid == crowded.Uuid).NameSimilarity.Should().Be(1);
    }

    [Test]
    public async Task RetiredOriginalsCountAsOneSurvivorAndRetainTheirProofOwners()
    {
        var query = UniqueName();
        var survivor = await Person([]);
        var originals = new List<CanonicalPerson>();
        for (var index = 0; index < 21; index++)
        {
            var original = await Person([], query);
            originals.Add(original);
            await Merge(original, survivor);
        }
        var rival = await Person([query + "x"]);

        var snapshot = await candidates.FindPeopleFactsAsync(Query(query));

        snapshot.Complete.Should().BeTrue();
        snapshot.Selection.NameCandidatesTruncated.Should().BeFalse();
        snapshot
            .Facts.Select(fact => fact.Hit.Uuid)
            .Should()
            .BeEquivalentTo([survivor.Uuid, rival.Uuid]);
        var resolved = snapshot.Facts.Single(fact => fact.Hit.Uuid == survivor.Uuid);
        resolved.Hit.NameSimilarity.Should().Be(1);
        resolved.Hit.Reasons.Should().Equal("alias", "name");
        resolved
            .IdentityClosure.Select(identity => identity.Uuid)
            .Should()
            .BeEquivalentTo(originals.Select(person => person.Uuid).Append(survivor.Uuid));
        var owners = new List<Guid>();
        foreach (var original in originals.Append(survivor))
        {
            owners.Add(await fixture.OwnerUuid("people", "person", original.Uuid));
        }
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .Contain(owners);
    }

    [Test]
    public async Task MultiHopRedirectUsesBestScoreAndKeepsEveryMatchingReason()
    {
        var query = UniqueName();
        var original = await Person([], query + "x");
        var bridge = await Person([query]);
        var survivor = await Person([]);
        var rival = await Person([query + "y"]);
        await Merge(original, bridge);
        await Merge(bridge, survivor);

        var result = await candidates.FindAsync(Query(query));

        result.NameCandidatesTruncated.Should().BeFalse();
        result.Items.Select(hit => hit.Uuid).Should().Equal(survivor.Uuid, rival.Uuid);
        result.Items[0].NameSimilarity.Should().Be(1);
        result.Items[0].Reasons.Should().Equal("alias", "name");
        result.Items[1].NameSimilarity.Should().BeLessThan(1);
    }

    [TestCase(20, false)]
    [TestCase(21, true)]
    public async Task LimitCountsDistinctSurvivorsAndStillReportsRealOverflow(
        int count,
        bool truncated
    )
    {
        var query = UniqueName();
        var expected = new List<Guid>();
        for (var index = 0; index < count; index++)
        {
            // Both reasons match every person. Expanding them must not double the lookahead.
            expected.Add((await Person([], query)).Uuid);
        }

        var result = await candidates.FindAsync(Query(query));

        result.NameCandidatesTruncated.Should().Be(truncated);
        result.Items.Should().HaveCount(Math.Min(count, 20));
        result.Items.Select(hit => hit.Uuid).Should().OnlyContain(uuid => expected.Contains(uuid));
        result.Items.Should().OnlyContain(hit => hit.NameSimilarity == 1);
        foreach (var hit in result.Items)
        {
            hit.Reasons.Should().Equal("alias", "name");
        }
        if (!truncated)
        {
            result.Items.Select(hit => hit.Uuid).Should().BeEquivalentTo(expected);
        }
    }

    private static string UniqueName() => "Name selection " + Guid.NewGuid().ToString("N");

    private static NormalizationCandidateQuery Query(string name) =>
        new(IdentityMatchingDomain.People, [name], [], NameLimit: 20, MinimumNameSimilarity: .7);

    private async Task<CanonicalPerson> Person(string[] aliases, string? displayName = null)
    {
        var person = new CanonicalPerson(
            Guid.NewGuid(),
            displayName ?? "Canonical identity " + Guid.NewGuid().ToString("N")
        );
        var input = await fixture.Capture(
            string.Join("; ", aliases.Prepend(person.DisplayName)),
            "people"
        );
        var assertions = aliases
            .Select(alias => new PersonAlias(Guid.NewGuid(), person.Uuid, alias, input.Reference))
            .ToArray();
        var result = await fixture.PublishPeople(people, input, new([person], assertions, [], []));
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        return person;
    }

    // Fixture-only copies preserve the original input coordinates and unmanaged authority.
    // Reader scale is the subject of this test; producer idempotency is tested elsewhere.
    private Task DuplicateAlias(Guid person, string wording, int copies) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                WITH original AS (
                    SELECT a.*,owner.published_work_uuid FROM normalization.person_aliases a
                    JOIN normalization.work_subjects owner ON owner.domain='people' AND owner.subject_kind='person'
                        AND owner.subject_uuid=a.person_uuid
                    WHERE a.person_uuid=@person AND a.wording=@wording ORDER BY a.uuid LIMIT 1
                ), inserted AS (
                    INSERT INTO normalization.person_aliases(uuid,person_uuid,wording,name_key,input_revision_uuid,
                        json_pointer,start_code_point,end_code_point,quote,state,basis,revision)
                    SELECT gen_random_uuid(),o.person_uuid,o.wording,o.name_key,o.input_revision_uuid,
                        o.json_pointer,o.start_code_point,o.end_code_point,o.quote,o.state,o.basis,o.revision
                    FROM original o CROSS JOIN generate_series(1,@copies) RETURNING uuid
                )
                INSERT INTO normalization.person_alias_unmanaged_support(alias_uuid,introduced_work_uuid)
                SELECT inserted.uuid,o.published_work_uuid FROM inserted CROSS JOIN original o
                """,
                new
                {
                    person,
                    wording,
                    copies,
                }
            )
        );

    private async Task Merge(CanonicalPerson original, CanonicalPerson survivor)
    {
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Uuid)
            )
        )!;
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original.Uuid,
                await Revision(original.Uuid),
                survivor,
                await Revision(survivor.Uuid),
                [],
                "Fixture originals describe the same person"
            ),
            "fixture-operator"
        );
    }

    private Task<long> Revision(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid }
            )
        );
}
