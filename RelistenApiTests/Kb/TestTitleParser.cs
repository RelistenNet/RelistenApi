using FluentAssertions;
using Relisten.Kb.Catalogue;
using Relisten.Kb.Titles;

namespace RelistenApiTests.Kb;

/// Titles sampled from production source_tracks (2026-10-19).
public class TestTitleParser
{
    private static string Describe(ParsedTitle parsed) =>
        string.Join(
            " | ",
            parsed.Parts.Select(p =>
                p.Key
                + (p.Qualifiers.Count > 0 ? $" [{string.Join(",", p.Qualifiers)}]" : "")
                + (p.Hint == PartHint.Noise ? " (noise)" : "")
                + (p.SegueAfter ? " >" : "")
            )
        );

    [TestCase("Scarlet Begonias >", null, "scarlet begonias >")]
    [TestCase("Stella Blue ->", null, "stella blue >")]
    [TestCase("Stranglehold (Ted Nugent) ->Barfly", null, "stranglehold ted nugent > | barfly")]
    [TestCase(
        "Drive* > I've Seen All Good People > Drive*",
        null,
        "drive > | ive seen all good people > | drive"
    )]
    [TestCase("RV > *", null, "rv >")]
    [TestCase("Magic Carpet Ride*#", null, "magic carpet ride")]
    [TestCase("bearsong **", null, "bearsong")]
    [TestCase("E: Six Of One", null, "six of one")]
    [TestCase("Blue sky blues (12:50)", null, "blue sky blues")]
    [TestCase("Axilla (Part II)", null, "axilla part ii")]
    [TestCase("Paris (Ooh La La)", null, "paris ooh la la")]
    [TestCase("AC/DC Bag", null, "ac dc bag")]
    [TestCase("Drums/Space", null, "drums | space")]
    [TestCase("NO2", null, "no2")]
    [TestCase("(Check) Harpua", null, "harpua [soundcheck]")]
    [TestCase("(Check) Jam > Leaves", null, "jam [soundcheck] > | leaves [soundcheck]")]
    [TestCase("(Check) Banter", null, "banter [soundcheck] (noise)")]
    [TestCase("Introduction", null, "introduction (noise)")]
    [TestCase("Encore", null, "encore (noise)")]
    [TestCase("audience", null, "audience (noise)")]
    [TestCase("Guitar", null, "guitar")]
    [TestCase("Set 2- Crazy Fingers", null, "crazy fingers")]
    [TestCase("Cassidy 080925_201809_TrMic", null, "cassidy")]
    [TestCase("1x1 >", null, "1x1 >")]
    [TestCase("LL3", null, "ll3")]
    [TestCase("crowd/tuning", null, "crowd (noise) | tuning (noise)")]
    [TestCase("./-tuning-", null, "tuning (noise)")]
    [TestCase("21-banter-tuning", 22, "banter tuning (noise)")]
    [TestCase("06. set 2 intro", 6, "set 2 intro")]
    [TestCase("[I didn't fuck up today]", null, "i didnt fuck up today")]
    [TestCase("Mama Tried (cont)", null, "mama tried [continued]")]
    [TestCase("Jam (w/ Branford Marsalis)", null, "jam [with_guest]")]
    [TestCase("Morning Dew (partial)", null, "morning dew [partial]")]
    public void SplitsSeguesQualifiersAndMarkers(string raw, int? position, string expected) =>
        Describe(TitleParser.Parse(raw, position)).Should().Be(expected);

    [TestCase("wsp2007-04-14set2t05-Mercy", 13, "mercy")]
    [TestCase(
        "1979-04-23 - University Of Illinois - Champaign (pre-FM) - 101 Intro.mp3",
        1,
        "intro"
    )]
    [TestCase(
        "1975-06-23 - Soon The Light - Long Beach - 203 Long Distance Runaround.mp3",
        8,
        "long distance runaround"
    )]
    [TestCase("09. Mama Tried.mp3", 9, "mama tried")]
    [TestCase("16 I Need A Miracle.flac", 16, "i need a miracle")]
    [TestCase("02 Fat Man in the Bathtub", 2, "fat man in the bathtub")]
    [TestCase("75 and Sunny", 11, "75 and sunny")]
    [TestCase("08 Sugaree.mp3", 99, "sugaree")]
    [TestCase("gd83-03-27 s2t08 Space", 8, "space")]
    public void StripsFileNamesIdentifiersAndTrackNumbers(
        string raw,
        int position,
        string expected
    ) => Describe(TitleParser.Parse(raw, position)).Should().Be(expected);

    private static string DescribeNotes(ParsedTitle parsed) =>
        string.Join(
            " | ",
            parsed.Parts.Select(p =>
                p.Key
                + (p.Qualifiers.Count > 0 ? $" [{string.Join(",", p.Qualifiers)}]" : "")
                + (p.Notes.Count > 0 ? $" {{{string.Join(";", p.Notes)}}}" : "")
                + (p.Bare != null ? $" ~{p.Bare}+{p.BareQualifier}" : "")
                + (p.SegueAfter ? " >" : "")
            )
        );

    /// Titles from production (2026-10-20): only the song name is the key; the rest is
    /// position, file name, marks or notes about the recording.
    [TestCase("05 - Scarlet Begonias **(aud -> sbd)", 5, "scarlet begonias {aud -> sbd}")]
    [TestCase("gd74-09-11d1t05 Scarlet Begonias.flac", 5, "scarlet begonias")]
    [TestCase("Bertha *", 1, "bertha")]
    [TestCase("Playing in the Band >", 4, "playing in the band >")]
    [TestCase(
        "I'm Still Here (John Hartford cover) (>)",
        3,
        "im still here {John Hartford cover} >"
    )]
    [TestCase(
        "16 Randall Collins - (Norman Blake cover) (>) Haul de Woodpile Down - (Sam Patterson Trio cover)",
        16,
        "randall collins {Norman Blake cover} > | haul de woodpile down {Sam Patterson Trio cover}"
    )]
    [TestCase("Time Ed > (Second Cousins? >)", 2, "time ed > | second cousins >")]
    [TestCase(
        "15 Ol’ Rickard (2) > Rueben’s Train (2)",
        15,
        "ol rickard {2} > | ruebens train {2}"
    )]
    [TestCase("Not Fade Away SBB", 9, "not fade away {SBB}")]
    [TestCase(
        "Hard To Handle * SBB -> Hey Bulldog Jam SBB -> Hard To Handle Reprise SBB *",
        7,
        "hard to handle {SBB} > | hey bulldog jam {SBB} ~hey bulldog+jam > | hard to handle reprise {SBB} ~hard to handle+reprise"
    )]
    [TestCase("Plane Crash reprise", 3, "plane crash reprise ~plane crash+reprise")]
    [TestCase(
        "Feel Like A Stranger Reprise +",
        3,
        "feel like a stranger reprise ~feel like a stranger+reprise"
    )]
    [TestCase("Nebraska (reprise)", 3, "nebraska [reprise]")]
    [TestCase("Amazing Grace Jam", 3, "amazing grace jam ~amazing grace+jam")]
    [TestCase("Dark Star (jam)", 3, "dark star [jam]")]
    [TestCase("Jam + @@ ->", 3, "jam >")]
    [TestCase("08 NO2.mp3", 8, "no2")]
    [TestCase("5:15", 3, "5 15")]
    [TestCase("Space > (2)", 3, "space >")]
    [TestCase("Part 1 >", 3, "part 1 >")]
    [TestCase("05:12 - Nebraska", 3, "nebraska")]
    [TestCase("4:43) Nebraska", 3, "nebraska")]
    [TestCase("01:26 ] intro", 3, "intro")]
    [TestCase("Dark Star (w/ St. Stephen tease)", 3, "dark star {w/ St. Stephen tease}")]
    public void CutsATitleDownToSongNames(string raw, int position, string expected) =>
        DescribeNotes(TitleParser.Parse(raw, position)).Should().Be(expected);

    [TestCase("wsp2007-07-13d2t02")]
    [TestCase("kglw2017-04-10t06.flac")]
    [TestCase("gsbg20110324d01t07.mp3")]
    [TestCase("moe2019-07-26.s1t09.suraci.mc950.mp3")]
    [TestCase("NM2008-10-31.flac16/NM2008-10-31S3D4T06.mp3")]
    [TestCase("Abear_2023-09-28_S202.mp3")]
    [TestCase("10. 2026-03-17dw.mp3")]
    [TestCase("tuckab11.mp3")]
    [TestCase("Track 3")]
    [TestCase("Marker 8")]
    [TestCase("(Fade In)")]
    public void BareFileNamesCarryNoSongText(string raw) =>
        TitleParser.Parse(raw, 1).HasText.Should().BeFalse(raw);
}

public class TestTitleGrouper
{
    private static KeyUsage Key(string key, int shows) => new(key, shows, shows, []);

    [Test]
    public void SpellingsOfOneSongGroupTogether()
    {
        var groups = TitleGrouper.Group([
            Key("porch song", 800),
            Key("prch song", 2),
            Key("porchsong", 3),
            Key("chilly water", 790),
            Key("chily water", 1),
            Key("axilla", 40),
            Key("axilla part ii", 30),
        ]);

        groups
            .Select(g => string.Join(",", g.Keys.Select(k => k.Key)))
            .Should()
            .Equal(
                "porch song,porchsong,prch song",
                "chilly water,chily water",
                "axilla",
                "axilla part ii"
            );
    }

    [Test]
    public void SpellingsDoNotChainIntoOneGroup()
    {
        // "rebula" is one edit from "rebubla" but two from the main spelling "rebubula".
        TitleGrouper
            .Group([Key("rebubula", 900), Key("rebubla", 5), Key("rebula", 2)])
            .Select(g => string.Join(",", g.Keys.Select(k => k.Key)))
            .Should()
            .Equal("rebubula,rebubla", "rebula");
    }

    [Test]
    public void AChunkStopsAtItsKeyBudget()
    {
        var groups = TitleGrouper.Group(
            Enumerable
                .Range(0, 10)
                .Select(i => Key($"song number {i:D3}x{new string((char)('a' + i), 6)}", 10 - i))
        );
        TitleGrouper.NextChunk(groups, 50, 3).Should().HaveCount(3);
        TitleGrouper.NextChunk(groups, 2, 150).Should().HaveCount(2);
    }

    [Test]
    public void ShortKeysNeedAnExactMatch()
    {
        TitleGrouper.Group([Key("fire", 10), Key("dire", 3)]).Should().HaveCount(2);
    }

    [TestCase("kitten", "sitting", 3, 3)]
    [TestCase("kitten", "sitting", 1, 2)]
    [TestCase("abc", "abc", 0, 0)]
    public void BoundedLevenshteinStopsPastTheLimit(string a, string b, int limit, int expected) =>
        TitleGrouper.BoundedLevenshtein(a, b, limit).Should().Be(expected);
}

public class TestTrackNumberFold
{
    private static readonly HashSet<string> Known = ["waiting for the punchline", "rebubula"];

    [TestCase("10 waiting for the punchline", "waiting for the punchline")]
    [TestCase("56 rebubula", "rebubula")]
    [TestCase("46 days", "46 days")]
    [TestCase("18 wheels of love", "18 wheels of love")]
    [TestCase("1999", "1999")]
    public void ANumberFoldsOnlyIntoAKnownSpelling(string key, string expected) =>
        TrackNumberFold.Fold(key, Known.Contains).Should().Be(expected);
}

public class TestTrackMatcher
{
    [Test]
    public void ARulingWrittenUnderAnEarlierParseStillMatches()
    {
        var (jam, track) = (Guid.NewGuid(), Guid.NewGuid());
        var state = new Relisten.Kb.Catalogue.CatalogueState
        {
            ArtistUuid = Guid.NewGuid(),
            ArtistName = "Phish",
            Songs = [],
            Aliases = new Dictionary<string, Guid>(),
            UnmappedSetlistSongs = [],
            Tracks =
            [
                new Relisten.Kb.Catalogue.CatalogueTrack(
                    track,
                    1,
                    TitleParser.Parse("(Check) Meat Jam", 1)
                )
                {
                    Existing = [new("(Check) Meat Jam", jam, true, 0.95f)],
                },
            ],
        };

        Relisten
            .Kb.Catalogue.TrackMatcher.Match(state)
            .Records.Select(r => $"{r.SongUuid == jam}:{r.Basis}:{string.Join(",", r.Qualifiers!)}")
            .Should()
            .Equal("True:ruling:soundcheck");
    }

    [TestCase("05:12 - Nebraska", "12 - Nebraska")]
    [TestCase(
        "07 BillS2018-09-06 07 Pike Co brkdwn_Little Maggie set1",
        "BillS2018-09-06 07 Pike Co brkdwn_Little Maggie set1"
    )]
    public void ARulingWhoseTextMovedIsKeptAtItsPosition(string title, string written)
    {
        var (song, track) = (Guid.NewGuid(), Guid.NewGuid());
        var state = new Relisten.Kb.Catalogue.CatalogueState
        {
            ArtistUuid = Guid.NewGuid(),
            ArtistName = "moe.",
            Songs = [],
            Aliases = new Dictionary<string, Guid>(),
            UnmappedSetlistSongs = [],
            Tracks =
            [
                new Relisten.Kb.Catalogue.CatalogueTrack(track, 1, TitleParser.Parse(title, 7))
                {
                    Existing = [new(written, song, true, 0.95f)],
                },
            ],
        };

        Relisten
            .Kb.Catalogue.TrackMatcher.Match(state)
            .Records.Select(r => r.SongUuid == song)
            .Should()
            .Equal(true);
    }

    private static Relisten.Kb.Catalogue.CatalogueState OneTrack(
        string title,
        params Relisten.Kb.Catalogue.ExistingPart[] existing
    ) =>
        new()
        {
            ArtistUuid = Guid.NewGuid(),
            ArtistName = "moe.",
            Songs = [],
            Aliases = new Dictionary<string, Guid>(),
            UnmappedSetlistSongs = [],
            Tracks =
            [
                new Relisten.Kb.Catalogue.CatalogueTrack(
                    Guid.NewGuid(),
                    1,
                    TitleParser.Parse(title, 1)
                )
                {
                    Existing = existing,
                },
            ],
        };

    [TestCase("Amazing Grace Jam", "Amazing Grace [jam]")]
    [TestCase("Chalk Dust Torture Reprise >", "Chalk Dust Torture Reprise")]
    [TestCase("Chalk Dust Torture Reprise", "Chalk Dust Torture Reprise")]
    [TestCase("Chalkdust Torture reprise", "Chalk Dust Torture [reprise]")]
    public void ATrailingQualifierIsLiftedUnlessTheWholeNameIsASong(string title, string expected)
    {
        var songs = new[] { "Amazing Grace", "Chalk Dust Torture", "Chalk Dust Torture Reprise" }
            .Select(n => new CatalogueSong(Guid.NewGuid(), n, "song"))
            .ToList();
        var state = new CatalogueState
        {
            ArtistUuid = Guid.NewGuid(),
            ArtistName = "Phish",
            Songs = songs,
            Aliases = new Dictionary<string, Guid> { ["chalkdust torture"] = songs[1].Uuid },
            UnmappedSetlistSongs = [],
            Tracks = [new CatalogueTrack(Guid.NewGuid(), 1, TitleParser.Parse(title, 1))],
        };

        TrackMatcher
            .Match(state)
            .Records.Select(r =>
                songs.Single(s => s.Uuid == r.SongUuid).Name
                + (r.Qualifiers is { Count: > 0 } q ? $" [{string.Join(",", q)}]" : "")
            )
            .Should()
            .Equal(expected);
    }

    [Test]
    public void ARetitledTrackDropsTheRulingItsOldTitleHad()
    {
        var state = OneTrack("Bar", new ExistingPart("Foo", Guid.NewGuid(), true, 0.95f));

        var match = Relisten.Kb.Catalogue.TrackMatcher.Match(state);

        match.Records.Should().BeEmpty();
        match.Cleared.Should().Equal(state.Tracks[0].Uuid);
    }

    [Test]
    public void ATrackRetitledToAFileNameIsCleared()
    {
        var state = OneTrack(
            "wsp2007-07-13d2t02",
            new ExistingPart("Foo", Guid.NewGuid(), true, 0.95f)
        );

        TrackMatcher.Match(state).Cleared.Should().Equal(state.Tracks[0].Uuid);
    }

    [Test]
    public void AnOpenMedleysOtherOccurrenceIsStillAsked()
    {
        var state = OneTrack(
            "Foo > Unknown Thing > Foo",
            new ExistingPart("Foo", Guid.NewGuid(), true, 0.95f, SegueAfter: true),
            new ExistingPart("Unknown Thing", null, false, 0.9f, "open")
        );

        CatalogueLlm.UnresolvedKeys(state).Keys.Should().BeEquivalentTo("foo", "unknown thing");
    }

    [Test]
    public void AReaskOfANumberedSpellingAsksTheKeyItFoldsInto()
    {
        var keys = CatalogueLlm.UnresolvedKeys(OneTrack("Let Me Lie"));

        CatalogueLlm.AskedKeys(["03 Let Me Lie"], keys).Should().Contain("let me lie");
    }

    [Test]
    public void ATitleAPersonIsHoldingIsNotAsked()
    {
        var one = OneTrack("Midnight Madness");
        var state = new CatalogueState
        {
            ArtistUuid = one.ArtistUuid,
            ArtistName = one.ArtistName,
            Songs = one.Songs,
            Aliases = one.Aliases,
            UnmappedSetlistSongs = one.UnmappedSetlistSongs,
            Tracks = one.Tracks,
            HeldKeys = new HashSet<string> { "midnight madness" },
        };

        CatalogueLlm.UnresolvedKeys(state).Should().BeEmpty();
    }

    [Test]
    public void AnOpenMedleysPlacedPartIsNotAskedAgain()
    {
        var state = OneTrack(
            "Uncle Song > Unknown Thing",
            new ExistingPart("Uncle Song", Guid.NewGuid(), true, 0.95f, SegueAfter: true),
            new ExistingPart("Unknown Thing", null, false, 0.9f, "open")
        );

        Relisten
            .Kb.Catalogue.CatalogueLlm.UnresolvedKeys(state)
            .Keys.Should()
            .Equal("unknown thing");
    }

    [Test]
    public void ASandwichKeepsOneRulingPerOccurrence()
    {
        var (rebubula, dragon, track) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var state = new Relisten.Kb.Catalogue.CatalogueState
        {
            ArtistUuid = Guid.NewGuid(),
            ArtistName = "moe.",
            Songs =
            [
                new Relisten.Kb.Catalogue.CatalogueSong(rebubula, "Rebubula", "song"),
                new Relisten.Kb.Catalogue.CatalogueSong(dragon, "Paper Dragon", "song"),
            ],
            Aliases = new Dictionary<string, Guid>(),
            UnmappedSetlistSongs = [],
            Tracks =
            [
                new Relisten.Kb.Catalogue.CatalogueTrack(
                    track,
                    1,
                    TitleParser.Parse("Rebula > Paper Dragon > Rebula", 1)
                )
                {
                    // "Rebula" is a one-off spelling the LLM ruled on, at both ends.
                    Existing =
                    [
                        // A duplicate left by title-matcher@2.0.0 before this fix.
                        new("Rebula", rebubula, true, 0.95f, SegueAfter: true),
                        new("Rebula", rebubula, true, 0.95f, SegueAfter: true),
                        new("Paper Dragon", dragon, true, 0.95f, "song_name", SegueAfter: true),
                        new("Rebula", rebubula, true, 0.95f),
                    ],
                },
            ],
        };

        var records = Relisten.Kb.Catalogue.TrackMatcher.Match(state).Records;

        records
            .Select(r =>
                $"{r.Part}:{(r.SongUuid == rebubula ? "Rebubula" : "Paper Dragon")}:{r.Basis}"
            )
            .Should()
            .Equal("1:Rebubula:ruling", "2:Paper Dragon:song_name", "3:Rebubula:ruling");
    }
}
