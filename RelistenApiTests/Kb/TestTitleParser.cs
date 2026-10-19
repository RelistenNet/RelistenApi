using FluentAssertions;
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

    [TestCase("wsp2007-07-13d2t02")]
    [TestCase("kglw2017-04-10t06.flac")]
    [TestCase("gsbg20110324d01t07.mp3")]
    [TestCase("moe2019-07-26.s1t09.suraci.mc950.mp3")]
    [TestCase("NM2008-10-31.flac16/NM2008-10-31S3D4T06.mp3")]
    [TestCase("Abear_2023-09-28_S202.mp3")]
    [TestCase("10. 2026-03-17dw.mp3")]
    [TestCase("tuckab11.mp3")]
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
