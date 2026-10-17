using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Fresh work and assertion UUIDs cannot turn the same supported fact into a new identity revision.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMetadataIdempotence
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizedVenueService venues = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        venues = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RepeatedAliasWithFreshUuidsDoesNotChangeAcceptedFacts()
    {
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", input.Reference);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        var before = await State("people", "person_aliases", "person_uuid", person.Uuid);

        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );

        (await State("people", "person_aliases", "person_uuid", person.Uuid)).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedVenueNameOrLocationWithFreshUuidsDoesNotChangeAcceptedFacts(
        bool location
    )
    {
        var input = await fixture.Capture("Hall; City", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var name = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Hall",
            null,
            null,
            null,
            input.Reference
        );
        var locality = Location(venue.Uuid, input.Reference);
        await fixture.PublishVenues(
            venues,
            input,
            new([venue], [name], location ? [locality] : [], [], [], [])
        );
        var table = location ? "venue_locations" : "venue_names";
        var before = await State("venues", table, "venue_uuid", venue.Uuid);

        await fixture.PublishVenues(
            venues,
            input,
            new(
                [],
                location ? [] : [name with { Uuid = Guid.NewGuid() }],
                location ? [locality with { Uuid = Guid.NewGuid() }] : [],
                [],
                [],
                []
            )
        );

        (await State("venues", table, "venue_uuid", venue.Uuid)).Should().Be(before);
    }

    [TestCase("people")]
    [TestCase("venues")]
    public async Task RepeatedQualifiedIdentityWithFreshUuidsDoesNotChangeAcceptedFacts(
        string domain
    )
    {
        var input = await fixture.Capture("Owner; provider identity", domain);
        var uuid = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        if (domain == "people")
        {
            var identity = new PersonExternalIdentity(
                uuid,
                "fixture",
                "person",
                key,
                input.Reference,
                Guid.NewGuid()
            );
            await fixture.PublishPeople(
                people,
                input,
                new([new(uuid, "Owner")], [], [identity], [])
            );
            var before = await State(domain, "person_external_identities", "person_uuid", uuid);
            await fixture.PublishPeople(
                people,
                input,
                new([], [], [identity with { Uuid = Guid.NewGuid() }], [])
            );
            (await State(domain, "person_external_identities", "person_uuid", uuid))
                .Should()
                .Be(before);
        }
        else
        {
            var identity = new VenueExternalIdentity(
                uuid,
                "fixture",
                "venue",
                key,
                input.Reference,
                Guid.NewGuid()
            );
            await fixture.PublishVenues(
                venues,
                input,
                new([new(uuid, "Owner")], [], [], [identity], [], [])
            );
            var before = await State(domain, "venue_external_identities", "venue_uuid", uuid);
            await fixture.PublishVenues(
                venues,
                input,
                new([], [], [], [identity with { Uuid = Guid.NewGuid() }], [], [])
            );
            (await State(domain, "venue_external_identities", "venue_uuid", uuid))
                .Should()
                .Be(before);
        }
    }

    [Test]
    public async Task RawCaseAndAuthorityBasisRemainDistinctAliasFacts()
    {
        var input = await fixture.Capture("Pat; PAT", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", input.Reference);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));

        await fixture.PublishPeople(
            people,
            input,
            new(
                [],
                [
                    alias with
                    {
                        Uuid = Guid.NewGuid(),
                        Wording = "PAT",
                    },
                    alias with
                    {
                        Uuid = Guid.NewGuid(),
                        Basis = "validated_match",
                    },
                ],
                [],
                []
            )
        );

        (await State("people", "person_aliases", "person_uuid", person.Uuid)).Count.Should().Be(3);
    }

    [Test]
    public async Task NameLanguageAndHistoricalDatesRemainDistinctFacts()
    {
        var input = await fixture.Capture("Hall; HALL", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var name = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Hall",
            null,
            null,
            null,
            input.Reference
        );
        await fixture.PublishVenues(venues, input, new([venue], [name], [], [], [], []));

        await fixture.PublishVenues(
            venues,
            input,
            new(
                [],
                [
                    name with
                    {
                        Uuid = Guid.NewGuid(),
                        Wording = "HALL",
                    },
                    name with
                    {
                        Uuid = Guid.NewGuid(),
                        Language = "en",
                    },
                    name with
                    {
                        Uuid = Guid.NewGuid(),
                        ValidFrom = new(1990, 1, 1),
                        ValidTo = new(2000, 12, 31),
                    },
                ],
                [],
                [],
                [],
                []
            )
        );

        (await State("venues", "venue_names", "venue_uuid", venue.Uuid)).Count.Should().Be(4);
    }

    [Test]
    public async Task GeographyAndCoordinatePrecisionRemainDistinctLocationFacts()
    {
        var input = await fixture.CaptureJson(
            """{"name":"Hall","location":{"original_locality":"City","country":"US","latitude":40.0,"longitude":-74.0,"coordinate_precision":"city"}}""",
            "venues",
            "/name"
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var name = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Hall",
            null,
            null,
            null,
            input.Reference
        );
        var locality = Location(venue.Uuid, input.Reference with { JsonPointer = "/location" });
        await fixture.PublishVenues(venues, input, new([venue], [name], [locality], [], [], []));

        await fixture.PublishVenues(
            venues,
            input,
            new(
                [],
                [],
                [
                    locality with
                    {
                        Uuid = Guid.NewGuid(),
                        Country = "US",
                    },
                    locality with
                    {
                        Uuid = Guid.NewGuid(),
                        Country = "US",
                        Latitude = 40,
                        Longitude = -74,
                        CoordinatePrecision = "city",
                    },
                ],
                [],
                [],
                []
            )
        );

        (await State("venues", "venue_locations", "venue_uuid", venue.Uuid)).Count.Should().Be(3);
    }

    [Test]
    public async Task DifferentPointersWithinOneInputRemainIndependentAcceptedSupport()
    {
        var input = await fixture.CaptureJson(
            """{"first":"Pat","second":"Pat"}""",
            "people",
            "/first"
        );
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", input.Reference);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));

        await fixture.PublishPeople(
            people,
            input,
            new(
                [],
                [
                    alias with
                    {
                        Uuid = Guid.NewGuid(),
                        Evidence = input.Reference with { JsonPointer = "/second" },
                    },
                ],
                [],
                []
            )
        );

        (await State("people", "person_aliases", "person_uuid", person.Uuid)).Count.Should().Be(2);
    }

    [Test]
    public async Task DifferentSpansWithinOneStringRemainIndependentAcceptedSupport()
    {
        var input = await fixture.Capture("Pat; Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var first = input.Reference with { StartCodePoint = 0, EndCodePoint = 3, Quote = "Pat" };
        var second = first with { StartCodePoint = 5, EndCodePoint = 8 };
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", first);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));

        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid(), Evidence = second }], [], [])
        );

        (await State("people", "person_aliases", "person_uuid", person.Uuid)).Count.Should().Be(2);
    }

    [Test]
    public async Task ReviewedUuidRetriesKeepSeparatePendingCitationsAndDeduplicateExactRepeats()
    {
        var original = await fixture.Capture("Pat; Wiz", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Wiz", original.Reference);
        await fixture.PublishPeople(people, original, new([person], [alias], [], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "retract",
                "Incorrect alias",
                Alias: alias,
                ExpectedAssertionRevision: 1
            ),
            "fixture-operator"
        );
        var changed = await fixture.CaptureJson(
            """{"first":"Wiz","second":"Wiz"}""",
            "people",
            "/first"
        );
        await fixture.PublishPeople(
            people,
            changed,
            new([], [alias with { Evidence = changed.Reference }], [], [])
        );
        await fixture.PublishPeople(
            people,
            changed,
            new(
                [],
                [alias with { Evidence = changed.Reference with { JsonPointer = "/second" } }],
                [],
                []
            )
        );
        var before = await State("people", "person_aliases", "person_uuid", person.Uuid);

        await fixture.PublishPeople(
            people,
            changed,
            new([], [alias with { Uuid = Guid.NewGuid(), Evidence = changed.Reference }], [], [])
        );

        (await State("people", "person_aliases", "person_uuid", person.Uuid)).Should().Be(before);
        (await CountAliases(person.Uuid, "pending")).Should().Be(2);
        (await CountAliases(person.Uuid, "rejected")).Should().Be(0);
        (await CountAliases(person.Uuid, "superseded")).Should().Be(1);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConflictingUuidCannotHideBehindAnotherMatchingAssertion(bool differentOwner)
    {
        var input = await fixture.Capture("Pat; Wrong; Tape", "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var wrong = new PersonAlias(Guid.NewGuid(), original.Uuid, "Wrong", input.Reference);
        await fixture.PublishPeople(people, input, new([original], [wrong], [], []));
        var target = differentOwner ? new CanonicalPerson(Guid.NewGuid(), "Pat") : original;
        var matching = new PersonAlias(Guid.NewGuid(), target.Uuid, "Tape", input.Reference);
        await fixture.PublishPeople(
            people,
            input,
            new(differentOwner ? [target] : [], [matching], [], [])
        );
        var before = await State("people", "person_aliases", "person_uuid", target.Uuid);

        var publish = () =>
            fixture.PublishPeople(
                people,
                input,
                new([], [matching with { Uuid = wrong.Uuid }], [], [])
            );

        await publish.Should().ThrowAsync<NormalizationConflictException>();
        (await State("people", "person_aliases", "person_uuid", target.Uuid)).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task VenueAssertionUuidCannotBeReusedForAnotherExistingFact(bool location)
    {
        var input = await fixture.Capture("Hall; Wrong; City", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var wrongName = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Wrong",
            null,
            null,
            null,
            input.Reference
        );
        var matchingName = wrongName with { Uuid = Guid.NewGuid(), Wording = "Hall" };
        var matchingLocation = Location(venue.Uuid, input.Reference);
        var wrongLocation = matchingLocation with
        {
            Uuid = Guid.NewGuid(),
            OriginalLocality = "Wrong",
        };
        await fixture.PublishVenues(
            venues,
            input,
            new(
                [venue],
                [wrongName, matchingName],
                location ? [wrongLocation, matchingLocation] : [],
                [],
                [],
                []
            )
        );
        var table = location ? "venue_locations" : "venue_names";
        var before = await State("venues", table, "venue_uuid", venue.Uuid);

        var publish = () =>
            fixture.PublishVenues(
                venues,
                input,
                new(
                    [],
                    location ? [] : [matchingName with { Uuid = wrongName.Uuid }],
                    location ? [matchingLocation with { Uuid = wrongLocation.Uuid }] : [],
                    [],
                    [],
                    []
                )
            );

        await publish.Should().ThrowAsync<NormalizationConflictException>();
        (await State("venues", table, "venue_uuid", venue.Uuid)).Should().Be(before);
    }

    [TestCase("people")]
    [TestCase("venues")]
    public async Task QualifiedIdentityUuidCannotBeReusedForAnotherExistingKey(string domain)
    {
        var wrongKey = Guid.NewGuid().ToString("D");
        var matchingKey = Guid.NewGuid().ToString("D");
        var input = await fixture.Capture($"Owner; {wrongKey}; {matchingKey}", domain);
        var owner = Guid.NewGuid();
        var wrongUuid = Guid.NewGuid();
        if (domain == "people")
        {
            var wrong = new PersonExternalIdentity(
                owner,
                "fixture",
                "person",
                wrongKey,
                input.Reference,
                wrongUuid
            );
            var matching = wrong with { Uuid = Guid.NewGuid(), ProviderKey = matchingKey };
            await fixture.PublishPeople(
                people,
                input,
                new([new(owner, "Owner")], [], [wrong, matching], [])
            );
            var before = await State(domain, "person_external_identities", "person_uuid", owner);

            var publish = () =>
                fixture.PublishPeople(
                    people,
                    input,
                    new([], [], [matching with { Uuid = wrongUuid }], [])
                );

            await publish.Should().ThrowAsync<NormalizationConflictException>();
            (await State(domain, "person_external_identities", "person_uuid", owner))
                .Should()
                .Be(before);
        }
        else
        {
            var wrong = new VenueExternalIdentity(
                owner,
                "fixture",
                "venue",
                wrongKey,
                input.Reference,
                wrongUuid
            );
            var matching = wrong with { Uuid = Guid.NewGuid(), ProviderKey = matchingKey };
            await fixture.PublishVenues(
                venues,
                input,
                new([new(owner, "Owner")], [], [], [wrong, matching], [], [])
            );
            var before = await State(domain, "venue_external_identities", "venue_uuid", owner);

            var publish = () =>
                fixture.PublishVenues(
                    venues,
                    input,
                    new([], [], [], [matching with { Uuid = wrongUuid }], [], [])
                );

            await publish.Should().ThrowAsync<NormalizationConflictException>();
            (await State(domain, "venue_external_identities", "venue_uuid", owner))
                .Should()
                .Be(before);
        }
    }

    [Test]
    public async Task KnownAliasUuidCannotChangeItsAuthorityBasis()
    {
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", input.Reference);
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        var before = await State("people", "person_aliases", "person_uuid", person.Uuid);

        var publish = () =>
            fixture.PublishPeople(
                people,
                input,
                new([], [alias with { Basis = "validated_match" }], [], [])
            );

        await publish.Should().ThrowAsync<NormalizationConflictException>();
        (await State("people", "person_aliases", "person_uuid", person.Uuid)).Should().Be(before);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task KnownVenueAssertionUuidCannotChangeItsAuthorityBasis(bool location)
    {
        var input = await fixture.Capture("Hall; City", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var name = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "Hall",
            null,
            null,
            null,
            input.Reference
        );
        var locality = Location(venue.Uuid, input.Reference);
        await fixture.PublishVenues(
            venues,
            input,
            new([venue], [name], location ? [locality] : [], [], [], [])
        );
        var table = location ? "venue_locations" : "venue_names";
        var before = await State("venues", table, "venue_uuid", venue.Uuid);

        var publish = () =>
            fixture.PublishVenues(
                venues,
                input,
                new(
                    [],
                    location ? [] : [name with { Basis = "validated_match" }],
                    location ? [locality with { Basis = "validated_match" }] : [],
                    [],
                    [],
                    []
                )
            );

        await publish.Should().ThrowAsync<NormalizationConflictException>();
        (await State("venues", table, "venue_uuid", venue.Uuid)).Should().Be(before);
    }

    [TestCase("people", "known-basis")]
    [TestCase("people", "fresh-basis")]
    [TestCase("people", "fresh-citation")]
    [TestCase("venues", "known-basis")]
    [TestCase("venues", "fresh-basis")]
    [TestCase("venues", "fresh-citation")]
    public async Task QualifiedIdentityCannotSilentlyDiscardDifferentAuthorityOrSupport(
        string domain,
        string change
    )
    {
        var input = await fixture.CaptureJson(
            """{"first":"Owner; provider identity","second":"Owner; provider identity"}""",
            domain,
            "/first"
        );
        var owner = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var evidence =
            change == "fresh-citation"
                ? input.Reference with
                {
                    JsonPointer = "/second",
                }
                : input.Reference;
        var basis = change == "fresh-citation" ? "provider_assertion" : "validated_match";
        if (domain == "people")
        {
            var identity = new PersonExternalIdentity(
                owner,
                "fixture",
                "person",
                key,
                input.Reference,
                Guid.NewGuid()
            );
            await fixture.PublishPeople(
                people,
                input,
                new([new(owner, "Owner")], [], [identity], [])
            );
            var before = await State(domain, "person_external_identities", "person_uuid", owner);
            var changed = identity with
            {
                Uuid = change == "known-basis" ? identity.Uuid : Guid.NewGuid(),
                Basis = basis,
                Evidence = evidence,
            };

            var publish = () => fixture.PublishPeople(people, input, new([], [], [changed], []));

            await publish.Should().ThrowAsync<NormalizationConflictException>();
            (await State(domain, "person_external_identities", "person_uuid", owner))
                .Should()
                .Be(before);
        }
        else
        {
            var identity = new VenueExternalIdentity(
                owner,
                "fixture",
                "venue",
                key,
                input.Reference,
                Guid.NewGuid()
            );
            await fixture.PublishVenues(
                venues,
                input,
                new([new(owner, "Owner")], [], [], [identity], [], [])
            );
            var before = await State(domain, "venue_external_identities", "venue_uuid", owner);
            var changed = identity with
            {
                Uuid = change == "known-basis" ? identity.Uuid : Guid.NewGuid(),
                Basis = basis,
                Evidence = evidence,
            };

            var publish = () =>
                fixture.PublishVenues(venues, input, new([], [], [], [changed], [], []));

            await publish.Should().ThrowAsync<NormalizationConflictException>();
            (await State(domain, "venue_external_identities", "venue_uuid", owner))
                .Should()
                .Be(before);
        }
    }

    private static VenueLocationAssertion Location(
        Guid venue,
        NormalizedEvidenceReference evidence
    ) =>
        new(
            Guid.NewGuid(),
            venue,
            "City",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            evidence
        );

    private Task<FactState> State(string domain, string table, string ownerColumn, Guid owner) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<FactState>(
                $"""
                SELECT (SELECT count(*) FROM normalization.{table} WHERE {ownerColumn}=@owner) AS "Count",
                    (SELECT revision FROM normalization.{(
                    domain == "people" ? "people" : "canonical_venues"
                )} WHERE uuid=@owner) AS "Revision",
                    (SELECT generation FROM normalization.candidate_universe_revisions WHERE domain=@domain AND scope_uuid=@global) AS "Generation"
                """,
                new
                {
                    owner,
                    domain,
                    global = Guid.Empty,
                }
            )
        );

    private Task<long> CountAliases(Guid owner, string state) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@owner AND state=@state",
                new { owner, state }
            )
        );

    private sealed record FactState(long Count, long Revision, long Generation);
}
