using System.Data;
using FluentAssertions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCandidateSnapshots
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
    public async Task CallerSnapshotMatchesPublicFactsWithoutOwningItsTransaction()
    {
        var person = await Person();
        var query = Query(person);
        var publicFacts = await candidates.FindPeopleFactsAsync(query);
        await using var connection = fixture.Db.CreateConnection(false, false);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead
        );

        var facts = await candidates.FindPeopleFactsInSnapshotAsync(connection, transaction, query);
        var repeated = await candidates.FindPeopleFactsInSnapshotAsync(
            connection,
            transaction,
            query
        );

        facts.Should().BeEquivalentTo(publicFacts);
        repeated.Should().BeEquivalentTo(facts);
        await transaction.CommitAsync();
    }

    [Test]
    public async Task SameCallerSnapshotRetainsOriginalFactsAndOwnerClocksAcrossRealPublication()
    {
        var person = await Person();
        var query = Query(person);
        await using var connection = fixture.Db.CreateConnection(false, false);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead
        );
        var before = await candidates.FindPeopleFactsInSnapshotAsync(
            connection,
            transaction,
            query
        );
        var alias = "Newly accepted " + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture(alias, "people");
        (
            await fixture.PublishPeople(
                people,
                input,
                new(
                    [],
                    [new PersonAlias(Guid.NewGuid(), person.Uuid, alias, input.Reference)],
                    [],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        var retained = await candidates.FindPeopleFactsInSnapshotAsync(
            connection,
            transaction,
            query
        );
        var current = await candidates.FindPeopleFactsAsync(query);

        retained.Should().BeEquivalentTo(before);
        retained.Facts.Single().AcceptedAliasMatchingNames.Should().NotContain(alias);
        current.Facts.Single().AcceptedAliasMatchingNames.Should().Contain(alias);
        current
            .Facts.Single()
            .Header.Revision.Should()
            .BeGreaterThan(before.Facts.Single().Header.Revision);
        current
            .ReadSet.CandidateUniverses.Single()
            .Generation.Should()
            .BeGreaterThan(before.ReadSet.CandidateUniverses.Single().Generation);
        var owner = before.Facts.Single().Header.OwnerSubjectUuid;
        current
            .ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner)
            .Should()
            .NotBe(before.ReadSet.OwnerObservations.Single(value => value.SubjectUuid == owner));
        await transaction.CommitAsync();
    }

    [Test]
    public async Task PublicInvalidQueryFailsBeforeOpeningAConnection()
    {
        var previous = DbService.ConnStr;
        try
        {
            DbService.ConnStr = "This cannot be parsed as a database connection";
            var invalid = () =>
                candidates.FindPeopleFactsAsync(
                    new(IdentityMatchingDomain.Venues, ["wrong facts domain"], [])
                );

            await invalid
                .Should()
                .ThrowAsync<ArgumentException>()
                .WithMessage("Use the facts method for the query's identity domain.*");
        }
        finally
        {
            DbService.ConnStr = previous;
        }
    }

    private async Task<CanonicalPerson> Person()
    {
        var person = new CanonicalPerson(
            Guid.NewGuid(),
            "Snapshot candidate " + Guid.NewGuid().ToString("N")
        );
        var input = await fixture.Capture(person.DisplayName, "people");
        (await fixture.PublishPeople(people, input, new([person], [], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return person;
    }

    private static NormalizationCandidateQuery Query(CanonicalPerson person) =>
        new(IdentityMatchingDomain.People, [person.DisplayName], [], MinimumNameSimilarity: .99);
}
