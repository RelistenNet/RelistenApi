using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Venues;

namespace Relisten.NormalizationTests;

public class TestVenueAnswers
{
    private static LegacyVenue Row(string name, string location, int uses = 10) =>
        new(Guid.NewGuid(), Guid.NewGuid(), name, location, uses, 1990, 2000);

    // l1 New York with Madison Square Garden known (v1); l2 Philadelphia.
    private static (VenueRequest Request, Guid Garden) Request()
    {
        var garden = new CatalogueVenue(
            Guid.NewGuid(),
            "Madison Square Garden",
            "site",
            null,
            "New York",
            "NY",
            "US"
        );
        var state = new VenueState
        {
            Unmapped = [],
            Venues = new Dictionary<Guid, CatalogueVenue> { [garden.Uuid] = garden },
            Names = new[] { ("madison square garden", garden.Uuid) }.ToLookup(
                n => n.Item1,
                n => n.Item2
            ),
            ExternalIds = new Dictionary<(string, string), Guid>(),
        };
        var rows = new[]
        {
            Row("MSG", "NY, NY", 50),
            Row("The Theater at Madison Square Garden", "New York, NY", 5),
            Row("The Spectrum", "Philadelphia, PA", 40),
        };
        var spellings = rows.GroupBy(VenueKeys.Of)
            .Select(g => new Spelling(g.Key!, g.ToList()))
            .ToList();
        return (VenueRequest.Build(state, spellings), garden.Uuid);
    }

    private static string Answer(string spellings, string newVenues = "[]", string known = "[]") =>
        $$"""{"new_venues": {{newVenues}}, "spellings": {{spellings}}, "known_names": {{known}}}""";

    [Test]
    public void AValidAnswerPlacesSpellingsOnKnownAndNewVenues()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """
                [{"id": "s1", "spelling_text": "MSG", "venue": "v1", "not_a_venue": false, "confidence": 0.97, "rationale": ""},
                {"id": "s2", "spelling_text": "The Theater at Madison Square Garden", "venue": "n1", "not_a_venue": false, "confidence": 0.95, "rationale": ""},
                {"id": "s3", "spelling_text": "The Spectrum", "venue": "n2", "not_a_venue": false, "confidence": 0.95, "rationale": ""}]
                """,
                """
                [{"id": "n1", "locality": "l1", "name": "Hulu Theater", "kind": "room", "parent": "v1", "names": [{"name": "The Theater at Madison Square Garden", "kind": "official", "from": "1991", "to": "2018-09"}]},
                {"id": "n2", "locality": "l2", "name": "The Spectrum", "kind": "site", "parent": null, "names": [{"name": "CoreStates Spectrum", "kind": "official", "from": "1994", "to": "1998"}, {"name": "Spectrum", "kind": "short", "from": "1967", "to": null}]}]
                """
            ),
            request
        );

        answer.Spellings.Values.Select(s => s.Venue).Should().Equal("v1", "n1", "n2");
        var theater = answer.NewVenues.Single(n => n.Ref == "n1");
        theater.Kind.Should().Be("room");
        theater.Names.Single().ValidTo.Should().Be(new DateOnly(2018, 9, 1));
        // A short name has no dates.
        answer
            .NewVenues.Single(n => n.Ref == "n2")
            .Names.Single(n => n.Kind == "short")
            .ValidFrom.Should()
            .BeNull();
    }

    [Test]
    public void ASpellingPlacedInAnotherLocalityOrMisstatedStaysOpen()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """
                [{"id": "s3", "spelling_text": "The Spectrum", "venue": "v1", "not_a_venue": false, "confidence": 0.99, "rationale": ""},
                {"id": "s1", "spelling_text": "Madison Square Garden", "venue": "v1", "not_a_venue": false, "confidence": 0.99, "rationale": ""}]
                """
            ),
            request
        );

        answer.Spellings.Should().BeEmpty();
        // Each set-aside answer says why, for the review item.
        answer
            .Dropped.Values.Should()
            .BeEquivalentTo(
                "venue v1 is in another state",
                "restated as \"Madison Square Garden\"",
                "no answer"
            );
    }

    [Test]
    public void ARoomWhoseSiteIsElsewhereIsDropped()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """[]""",
                """[{"id": "n1", "locality": "l2", "name": "Theater", "kind": "room", "parent": "v1", "names": []}]"""
            ),
            request
        );

        answer.NewVenues.Should().BeEmpty();
    }

    [Test]
    public void ARoomOfADroppedRoomIsDroppedWithIt()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """[{"id": "s2", "spelling_text": "The Theater at Madison Square Garden", "venue": "n1", "not_a_venue": false, "confidence": 0.95, "rationale": ""}]""",
                """
                [{"id": "n1", "locality": "l1", "name": "Balcony", "kind": "room", "parent": "n2", "names": []},
                 {"id": "n2", "locality": "l1", "name": "Theater", "kind": "room", "parent": "n9", "names": []}]
                """
            ),
            request
        );

        answer.NewVenues.Should().BeEmpty();
        answer.Spellings.Should().BeEmpty();
    }

    [Test]
    public void AVenueListedFromAnotherTownSaysWhereItIs()
    {
        var (request, _) = Request();
        // The Garden is listed for New York's spellings (its own town) only.
        request.Data.Should().NotContain("\"town\":\"New York");
        var oakland = new CatalogueVenue(
            Guid.NewGuid(),
            "Fox Theatre",
            "site",
            null,
            "Oakland",
            "CA",
            "US"
        );
        var state = new VenueState
        {
            Unmapped = [],
            Venues = new Dictionary<Guid, CatalogueVenue> { [oakland.Uuid] = oakland },
            Names = new[] { ("fox theater", oakland.Uuid) }.ToLookup(n => n.Item1, n => n.Item2),
            ExternalIds = new Dictionary<(string, string), Guid>(),
        };
        var row = Row("Fox Theatre", "Redwood City, CA");
        var redwood = VenueRequest.Build(state, [new Spelling(VenueKeys.Of(row)!, [row])]);

        redwood.Data.Should().Contain("\"town\":\"Oakland, CA\"");
    }

    [Test]
    public void AVenueInsideAnotherIsARoom()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                "[]",
                """
                [{"id": "n1", "locality": "l1", "name": "Theater", "kind": "site", "parent": "v1", "names": []}]
                """
            ),
            request
        );

        answer.NewVenues.Single().Kind.Should().Be("room");
    }

    [TestCase("not json")]
    [TestCase("""{"spellings": []}""")]
    public void AnAnswerOfTheWrongShapeThrows(string json) =>
        FluentActions
            .Invoking(() => VenueRequest.Parse(json, Request().Request))
            .Should()
            .Throw<VenueAnswerException>();

    [TestCase("2010", 2010, 1, 1)]
    [TestCase("2010-08", 2010, 8, 1)]
    [TestCase("2010-08-01", 2010, 8, 1)]
    public void DatesNameTheirFirstDay(string text, int y, int m, int d)
    {
        VenueRequest.TryDate(text, out var date).Should().BeTrue();
        date.Should().Be(new DateOnly(y, m, d));
    }

    [Test]
    public void AChunkKeepsAFamilyWhole()
    {
        var busy = Enumerable
            .Range(0, 100)
            .Select(i => Row($"Hall {i}", "Chicago, IL", 5))
            .ToList();
        var quiet = Enumerable.Range(0, 40).Select(i => Row($"Club {i}", "Peoria, IL", 1)).ToList();
        // Each town's spellings are one family here.
        var spellings = busy.Concat(quiet)
            .GroupBy(VenueKeys.Of)
            .Select(g => new Spelling(
                g.Key!,
                g.ToList(),
                g.First().Location!.StartsWith("Chicago") ? 0 : 1
            ))
            .ToList();

        var chunk = VenueLlm.NextChunk(spellings);

        chunk.Select(s => s.Locality).Distinct().Should().Equal("chicago|il|US");
    }

    [Test]
    public void RequestsAskedAtOnceNeverShareAState()
    {
        Spelling One(string name, string location, int family, int uses = 5)
        {
            var row = Row(name, location, uses);
            return new Spelling(VenueKeys.Of(row)!, [row], family);
        }

        var spellings = new[]
        {
            One("Red Rocks", "Morrison, CO", 0, 50),
            One("Fox Theatre", "Boulder, CO", 1, 40),
            One("Fillmore", "San Francisco, CA", 2, 30),
            One("Paradiso", "Amsterdam, Netherlands", 3, 20),
            One("Gothic Theatre", "Englewood, CO", 4, 10),
        };

        var chunks = VenueLlm.NextChunks(spellings, 3);

        chunks.Should().HaveCount(3);
        chunks
            .Select(c => string.Join(",", c.Select(s => s.Region).Distinct()))
            .Should()
            .Equal("co|US", "ca|US", "NL");
        // One request at a time is the busiest families together, whatever their states.
        VenueLlm.NextChunk(spellings).Should().HaveCount(5);
    }

    [Test]
    public void ARoomIsInsideASiteOfItsOwnTown()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                "[]",
                """
                [{"id": "n3", "locality": "l1", "name": "Arena Complex", "kind": "site", "parent": null, "names": []},
                 {"id": "n2", "locality": "l1", "name": "Theater", "kind": "room", "parent": "n3", "names": []},
                 {"id": "n1", "locality": "l1", "name": "Balcony", "kind": "room", "parent": "n2", "names": []}]
                """
            ),
            request
        );

        // A room inside a room is no room of ours.
        answer.NewVenues.Select(n => n.Ref).Should().BeEquivalentTo("n3", "n2");
    }

    [Test]
    public void ARoomOfAKnownSiteElsewhereIsDropped()
    {
        var oakland = new CatalogueVenue(
            Guid.NewGuid(),
            "Fox Theatre",
            "site",
            null,
            "Oakland",
            "CA",
            "US"
        );
        var state = new VenueState
        {
            Unmapped = [],
            Venues = new Dictionary<Guid, CatalogueVenue> { [oakland.Uuid] = oakland },
            Names = new[] { ("fox theater", oakland.Uuid) }.ToLookup(n => n.Item1, n => n.Item2),
            ExternalIds = new Dictionary<(string, string), Guid>(),
        };
        var row = Row("Fox Theatre", "Redwood City, CA");
        var request = VenueRequest.Build(state, [new Spelling(VenueKeys.Of(row)!, [row])]);
        var fox = request.VenueIds.Single(v => v.Value == oakland.Uuid).Key;

        var answer = VenueRequest.Parse(
            Answer(
                "[]",
                $$"""[{"id": "n1", "locality": "l1", "name": "Lounge", "kind": "room", "parent": "{{fox}}", "names": []}]"""
            ),
            request
        );

        answer.NewVenues.Should().BeEmpty();
    }

    [Test]
    public void AnOfficialPeriodThatEndsBeforeItStartsIsNoName()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            """{"name": "Spectrum", "kind": "official", "from": "2010", "to": "2010"}"""
        )!;
        VenueRequest.Name(Guid.NewGuid(), node).Should().BeNull();
        var shortName = System.Text.Json.Nodes.JsonNode.Parse(
            """{"name": "MSG", "kind": "short", "from": "1968", "to": null}"""
        )!;
        VenueRequest.Name(Guid.NewGuid(), shortName)!.ValidFrom.Should().BeNull();
    }

    [Test]
    public void AKnownRoomSaysWhichSiteItIsIn()
    {
        var site = new CatalogueVenue(
            Guid.NewGuid(),
            "Three Sisters Park",
            "site",
            null,
            "Chillicothe",
            "IL",
            "US"
        );
        var room = new CatalogueVenue(
            Guid.NewGuid(),
            "Main Stage",
            "room",
            site.Uuid,
            "Chillicothe",
            "IL",
            "US"
        );
        var state = new VenueState
        {
            Unmapped = [],
            Venues = new Dictionary<Guid, CatalogueVenue>
            {
                [site.Uuid] = site,
                [room.Uuid] = room,
            },
            Names = new[] { ("main stage", room.Uuid) }.ToLookup(n => n.Item1, n => n.Item2),
            ExternalIds = new Dictionary<(string, string), Guid>(),
        };
        var row = Row("Main Stage", "Chillicothe, IL");

        VenueRequest
            .Build(state, [new Spelling(VenueKeys.Of(row)!, [row])])
            .Data.Should()
            .Contain("\"inside\":\"Three Sisters Park\"");
    }

    [Test]
    public void AnUnsureSpellingIsNeverLearnedAsAName()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """
                [{"id": "s1", "spelling_text": "MSG", "venue": "v1", "not_a_venue": false, "confidence": 0.6, "rationale": "unsure"}]
                """,
                "[]",
                """
                [{"venue": "v1", "name": "MSG", "kind": "short", "from": null, "to": null},
                 {"venue": "v1", "name": "The Garden", "kind": "short", "from": null, "to": null}]
                """
            ),
            request
        );

        VenueLlm
            .KnownNamesToLearn(request, answer)
            .Select(k => k.Name.Name)
            .Should()
            .Equal("The Garden");
    }

    [Test]
    public void AParentlessRoomIsNoParent()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """[{"id": "s2", "spelling_text": "The Theater at Madison Square Garden", "venue": "n2", "not_a_venue": false, "confidence": 0.95, "rationale": ""}]""",
                """
                [{"id": "n1", "locality": "l1", "name": "Hall", "kind": "room", "parent": null, "names": []},
                 {"id": "n2", "locality": "l1", "name": "Balcony", "kind": "room", "parent": "n1", "names": []}]
                """
            ),
            request
        );

        // A room with no site is no room, and nothing can be inside it.
        answer.NewVenues.Should().BeEmpty();
        answer.Spellings.Should().BeEmpty();
    }

    [Test]
    public void AnUnsureSpellingIsNoNameOfAProposedVenueEither()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """
                [{"id": "s2", "spelling_text": "The Theater at Madison Square Garden", "venue": "n1", "not_a_venue": false, "confidence": 0.95, "rationale": ""},
                 {"id": "s1", "spelling_text": "MSG", "venue": "n1", "not_a_venue": false, "confidence": 0.6, "rationale": "unsure"}]
                """,
                """
                [{"id": "n1", "locality": "l1", "name": "Hulu Theater", "kind": "room", "parent": "v1",
                  "names": [{"name": "MSG", "kind": "short", "from": null, "to": null},
                            {"name": "The Theater at Madison Square Garden", "kind": "official", "from": "1991", "to": "2018"}]}]
                """
            ),
            request
        );

        var learnable = VenueLlm.Learnable(request, answer);
        answer
            .NewVenues.Single()
            .Names.Where(n => learnable(n.Name))
            .Select(n => n.Name)
            .Should()
            .Equal("The Theater at Madison Square Garden");
    }

    [Test]
    public void AnUnsureSpellingNeverMatchesAProposedVenueToATwin()
    {
        var (request, _) = Request();
        var answer = VenueRequest.Parse(
            Answer(
                """
                [{"id": "s2", "spelling_text": "The Theater at Madison Square Garden", "venue": "n1", "not_a_venue": false, "confidence": 0.6, "rationale": "unsure"}]
                """,
                """
                [{"id": "n1", "locality": "l1", "name": "Hulu Theater", "kind": "site", "parent": null,
                  "names": [{"name": "The Theater at Madison Square Garden", "kind": "official", "from": "1991", "to": "2018"}]}]
                """
            ),
            request
        );

        VenueLlm
            .TwinNames(answer.NewVenues.Single(), VenueLlm.Learnable(request, answer))
            .Should()
            .Equal("hulu theater");
    }
}
