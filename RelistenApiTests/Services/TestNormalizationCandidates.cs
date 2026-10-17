using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Exercises candidate recall and ambiguity through typed writers and indexed PostgreSQL reads.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCandidates
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationCandidateService candidates = null!;
    private NormalizedPeopleService people = null!;
    private NormalizedMusicService music = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        candidates = new(fixture.Db);
        people = new(fixture.Db, fixture.Work);
        music = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task SimilarNamesReturnSeparatePeopleWithoutAcceptingIdentity()
    {
        var name = "Charlie Miller " + Guid.NewGuid().ToString("N");
        var first = await Person(name);
        var second = await Person(name);
        var before = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>("SELECT count(*) FROM normalization.people")
        );

        var result = await candidates.FindAsync(
            new(
                IdentityMatchingDomain.People,
                [name.Replace("Miller", "Miler")],
                [],
                MinimumNameSimilarity: .5
            )
        );

        result
            .Items.Select(hit => hit.Uuid)
            .Should()
            .Contain([first.Person.Uuid, second.Person.Uuid]);
        result
            .Items.Where(hit => hit.Uuid == first.Person.Uuid || hit.Uuid == second.Person.Uuid)
            .Should()
            .OnlyContain(hit =>
                hit.OwnerSubjectUuid != null && hit.Revision == 1 && hit.NameSimilarity > .5
            );
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<int>("SELECT count(*) FROM normalization.people")
            )
        )
            .Should()
            .Be(before);
    }

    [Test]
    public async Task IdentifierHitsRemainCompleteWhenNameCandidatesAreCapped()
    {
        var common = "Shared Candidate " + Guid.NewGuid().ToString("N");
        var a = await Person(common, identifier: "one-" + Guid.NewGuid());
        var b = await Person(common, identifier: "two-" + Guid.NewGuid());
        await Person(common);

        var result = await candidates.FindAsync(
            new(
                IdentityMatchingDomain.People,
                [common],
                [
                    new("TEST-PROVIDER", "person", a.Identifier!),
                    new("test-provider", "person", b.Identifier!),
                ],
                NameLimit: 1,
                MinimumNameSimilarity: .95
            )
        );

        result.NameCandidatesTruncated.Should().BeTrue();
        result.Items.Select(hit => hit.Uuid).Should().Contain([a.Person.Uuid, b.Person.Uuid]);
        result
            .Items.Single(hit => hit.Uuid == a.Person.Uuid)
            .Reasons.Should()
            .Contain("provider_identity");
        result
            .Items.Single(hit => hit.Uuid == b.Person.Uuid)
            .Reasons.Should()
            .Contain("provider_identity");

        var otherResource = await candidates.FindAsync(
            new(IdentityMatchingDomain.People, [], [new("test-provider", "upload", a.Identifier!)])
        );
        otherResource.Items.Should().BeEmpty();
    }

    [Test]
    public async Task RetiredAliasFindsSurvivorUntilItsReviewedRetraction()
    {
        var alias = "TapeHandle" + Guid.NewGuid().ToString("N");
        var old = await Person("Original " + Guid.NewGuid().ToString("N"), alias);
        var survivor = await Person("Survivor " + Guid.NewGuid().ToString("N"));
        var owner = await fixture.OwnerUuid("people", "person", old.Person.Uuid);
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                "merge",
                old.Person.Uuid,
                1,
                survivor.Person,
                1,
                [],
                "Reviewed same person"
            ),
            "operator"
        );

        var merged = await candidates.FindAsync(
            new(IdentityMatchingDomain.People, [alias], [], MinimumNameSimilarity: .95)
        );
        merged.Items.Should().ContainSingle().Which.Uuid.Should().Be(survivor.Person.Uuid);

        var revision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@uuid",
                new { uuid = old.Person.Uuid }
            )
        );
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                1,
                old.Person.Uuid,
                revision,
                "retract",
                "Wrong old handle",
                Alias: old.Alias,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );

        var retracted = await candidates.FindAsync(
            new(IdentityMatchingDomain.People, [alias], [], MinimumNameSimilarity: .95)
        );
        retracted.Items.Should().BeEmpty();
    }

    [Test]
    public async Task VenueAliasesRetrievePhysicalIdentityWithCurrentOwner()
    {
        var name = "Royal Albert Hall " + Guid.NewGuid().ToString("N");
        var alias = "RAH " + Guid.NewGuid().ToString("N");
        var input = await fixture.Capture(name + "; " + alias, "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), name);
        var service = new NormalizedVenueService(fixture.Db, fixture.Work);
        await fixture.PublishVenues(
            service,
            input,
            new(
                [venue],
                [new(Guid.NewGuid(), venue.Uuid, alias, null, null, null, input.Reference)],
                [],
                [],
                [],
                []
            )
        );

        var result = await candidates.FindAsync(
            new(IdentityMatchingDomain.Venues, [alias], [], MinimumNameSimilarity: .95)
        );
        var hit = result.Items.Should().ContainSingle().Subject;
        hit.Uuid.Should().Be(venue.Uuid);
        hit.OwnerSubjectUuid.Should().Be(await fixture.OwnerUuid("venues", "venue", venue.Uuid));
        hit.Reasons.Should().Contain("alias");
    }

    [Test]
    public async Task RepertoireAliasesAndProviderKeysStayInsideArtistScope()
    {
        var artistA = await Artist();
        var artistB = await Artist();
        var key = "provider-song-" + Guid.NewGuid();
        var songA = await Song(artistA, "Heroes", ["Helden"], key);
        var songB = await Song(artistB, "Heroes", ["Helden"], key);

        var result = await candidates.FindAsync(
            new(
                IdentityMatchingDomain.Repertoire,
                ["Helden"],
                [new("music-provider", "song", key)],
                artistA,
                MinimumNameSimilarity: .95
            )
        );

        result.Items.Should().ContainSingle().Which.Uuid.Should().Be(songA);
        result.Items.Single().Reasons.Should().Contain("provider_identity");
        result.Items.Select(hit => hit.Uuid).Should().NotContain(songB);
    }

    [Test]
    public async Task GlobalWorkNamesRemainAmbiguousAndSupportAliasDiscovery()
    {
        var title = "Global Work " + Guid.NewGuid().ToString("N");
        var alias = "AlternativeTitle" + Guid.NewGuid().ToString("N");
        var a = await Composition(title, [alias]);
        var b = await Composition(title, [alias]);

        var result = await candidates.FindAsync(
            new(IdentityMatchingDomain.Compositions, [alias], [], MinimumNameSimilarity: .95)
        );

        result.Items.Select(hit => hit.Uuid).Should().BeEquivalentTo([a, b]);
        result.Items.Should().OnlyContain(hit => hit.OwnerSubjectUuid.HasValue);
    }

    [Test]
    public async Task MusicNamesUseTheSameUnicodeKeyForCandidatesAndPublicSearch()
    {
        var unique = Guid.NewGuid().ToString("N");
        var title = "Ｆｉｒｅ   Cafe\u0301 " + unique;
        var work = await Composition(title, []);

        var result = await candidates.FindAsync(
            new(
                IdentityMatchingDomain.Compositions,
                ["Fire Café " + unique],
                [],
                MinimumNameSimilarity: .95
            )
        );

        result.Items.Should().ContainSingle().Which.Uuid.Should().Be(work);
    }

    [Test]
    public async Task MusicMergeKeepsRetiredTitleAndAliasCandidates()
    {
        var artist = await Artist();
        var oldAlias = "OldPassage" + Guid.NewGuid().ToString("N");
        var a = await Song(artist, "Old Song", [oldAlias]);
        var b = await Song(artist, "New Song", []);
        var owner = await fixture.OwnerUuid("songs", "artist_repertoire", artist);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner,
            0,
            "merge",
            new MusicPublication { Merges = [new("songs", a, b, 1, 1)] },
            "Two interpretations of one song",
            "operator"
        );

        var result = await candidates.FindAsync(
            new(
                IdentityMatchingDomain.Repertoire,
                [oldAlias],
                [],
                artist,
                MinimumNameSimilarity: .95
            )
        );
        result.Items.Should().ContainSingle().Which.Uuid.Should().Be(b);
    }

    private async Task<(CanonicalPerson Person, PersonAlias? Alias, string? Identifier)> Person(
        string name,
        string? alias = null,
        string? identifier = null
    )
    {
        var input = await fixture.Capture(name + "; " + alias + "; " + identifier, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        var assertion =
            alias == null
                ? null
                : new PersonAlias(Guid.NewGuid(), person.Uuid, alias, input.Reference);
        await fixture.PublishPeople(
            people,
            input,
            new(
                [person],
                assertion == null ? [] : [assertion],
                identifier == null
                    ? []
                    : [new(person.Uuid, "test-provider", "person", identifier, input.Reference)],
                []
            )
        );
        return (person, assertion, identifier);
    }

    private async Task<Guid> Artist() =>
        await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Candidate artist',@slug,@uuid,now()) RETURNING uuid",
                new { slug = Guid.NewGuid().ToString("N"), uuid = Guid.NewGuid() }
            )
        );

    private async Task<Guid> Song(Guid artist, string title, string[] aliases, string? key = null)
    {
        var input = await fixture.Capture(title + "; " + string.Join(';', aliases), "songs");
        var owned = await fixture.Own(input, "artist_repertoire", artist);
        var song = Guid.NewGuid();
        await music.PublishAsync(
            owned.Ticket.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs =
                [
                    new()
                    {
                        Uuid = song,
                        ArtistUuid = artist,
                        DisplayTitle = title,
                        Aliases = aliases,
                        Evidence = [input.Reference],
                    },
                ],
                ExternalIdentities =
                    key == null
                        ? []
                        :
                        [
                            new()
                            {
                                Uuid = Guid.NewGuid(),
                                RepertoireSongUuid = song,
                                Provider = "music-provider",
                                ResourceType = "song",
                                ProviderKey = key,
                                Evidence = [input.Reference],
                            },
                        ],
            }
        );
        return song;
    }

    private async Task<Guid> Composition(string title, string[] aliases)
    {
        var input = await fixture.Capture(title, "compositions");
        var uuid = Guid.NewGuid();
        var owned = await fixture.Own(input, "composition", uuid);
        await music.PublishAsync(
            owned.Ticket.WorkUuid,
            new MusicPublication
            {
                Compositions =
                [
                    new()
                    {
                        Uuid = uuid,
                        DisplayTitle = title,
                        Aliases = aliases,
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
        return uuid;
    }
}
