using FluentAssertions;
using Relisten.Normalization;
using Relisten.Normalization.Songs;

namespace Relisten.NormalizationTests;

public class TestSongGate
{
    private static readonly SongId Scarlet = SongId.New();
    private static readonly SongId Fire = SongId.New();
    private static readonly SongId Playing = SongId.New();

    private static readonly Dictionary<SongId, IReadOnlySet<string>> Keys = new()
    {
        [Scarlet] = Spellings("Scarlet Begonias"),
        [Fire] = Spellings("Fire on the Mountain", "Fire"),
        [Playing] = Spellings("Playing in the Band", "Playin' in the Band"),
    };

    private static HashSet<string> Spellings(params string[] names) =>
        names.Select(SongGate.Key).ToHashSet();

    private static readonly Dictionary<SongId, string> Names = new()
    {
        [Scarlet] = "Scarlet Begonias",
        [Fire] = "Fire on the Mountain",
        [Playing] = "Playing in the Band",
    };

    private static GateResult Score(string songs, params GatePart[] written)
    {
        var gold = SongGate.Parse([
            "artist\ttrack_uuid\ttitle\tsongs",
            $"grateful-dead\t{Guid.Empty}\tsome title\t{songs}",
        ]);
        gold.Should().HaveCount(1);
        return SongGate.Score(
            "grateful-dead",
            gold,
            new Dictionary<Guid, IReadOnlyList<GatePart>> { [Guid.Empty] = written },
            Keys,
            Names
        );
    }

    private static GatePart Link(SongId song) => new(song, true);

    private static readonly GatePart None = new(null, true);
    private static readonly GatePart Open = new(null, false);

    [Test]
    public void ALinkToTheLabelledSongIsRight()
    {
        var result = Score("Scarlet Begonias > Fire on the Mountain", Link(Scarlet), Link(Fire));

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Right);
        (result.Links, result.LinksRight, result.SongsCovered, result.LabelledSongs)
            .Should()
            .Be((2, 2, 2, 2));
    }

    [Test]
    public void ALinkToAnotherSongIsWrongEvenWhenTheShowPlayedIt()
    {
        // Setlist agreement would count this: both songs were played that night.
        var result = Score("Scarlet Begonias", Link(Fire));

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Wrong);
        result.Precision.Should().Be(0);
    }

    [Test]
    public void ALinkedPlaceholderIsWrong()
    {
        var result = Score("-", Link(Playing));

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Wrong);
        result.Links.Should().Be(1);
        result.LabelledSongs.Should().Be(0);
    }

    [Test]
    public void ASongWrittenAsStageTalkIsHidden()
    {
        Score("Playing in the Band", None).Tracks.Single().Verdict.Should().Be(GateVerdict.Hidden);
    }

    [Test]
    public void AnOpenPartLeavesTheTrackPartial()
    {
        var result = Score("Scarlet Begonias > Fire on the Mountain", Link(Scarlet), Open);

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Partial);
        result.Coverage.Should().Be(0.5);
        result.Precision.Should().Be(1);
    }

    [Test]
    public void NothingWrittenIsOpen()
    {
        Score("Scarlet Begonias").Tracks.Single().Verdict.Should().Be(GateVerdict.Open);
    }

    [Test]
    public void AnyLabelledAlternativeCounts()
    {
        // Spelling is a convention: "Playin'" reaches the song through its alias, and a part
        // labelled song-or-not may be placed either way.
        Score("Playin' In The Band", Link(Playing))
            .Tracks.Single()
            .Verdict.Should()
            .Be(GateVerdict.Right);
        Score("Audience Chess Move | -", None)
            .Tracks.Single()
            .Verdict.Should()
            .Be(GateVerdict.Right);
    }

    [Test]
    public void ALinkTheLabelAcceptsCountsTowardsPrecision()
    {
        var result = Score("Fire on the Mountain | -", Link(Fire));

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Right);
        result.Precision.Should().Be(1);
        // A part that may be stage talk isn't a song the catalogue had to find.
        result.LabelledSongs.Should().Be(0);
    }

    [Test]
    public void ALinkFillsAPartThatMustBeASongFirst()
    {
        // The link could take either part; on the optional one, the not-a-song row would leave
        // the second Fire unplaced and the track hidden.
        var result = Score("Fire on the Mountain | - > Fire on the Mountain", None, Link(Fire));

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Right);
        (result.SongsCovered, result.LabelledSongs).Should().Be((1, 1));
    }

    [Test]
    public void ARepeatedSongNeedsALinkPerOccurrence()
    {
        var result = Score(
            "Playing in the Band > - > Playing in the Band",
            Link(Playing),
            None,
            Link(Playing)
        );

        result.Tracks.Single().Verdict.Should().Be(GateVerdict.Right);
        Score("Playing in the Band > - > Playing in the Band", Link(Playing), None, Open)
            .Tracks.Single()
            .Verdict.Should()
            .Be(GateVerdict.Partial);
    }

    [Test]
    public void EachTrackCarriesItsShareAndTheArtistIsTheirSum()
    {
        var (one, two) = (Guid.NewGuid(), Guid.NewGuid());
        var gold = SongGate.Parse([
            "artist\ttrack_uuid\ttitle\tsongs",
            $"grateful-dead\t{one}\tScarlet > Fire\tScarlet Begonias > Fire on the Mountain",
            $"grateful-dead\t{two}\tPITB\tPlaying in the Band",
        ]);

        var result = SongGate.Score(
            "grateful-dead",
            gold,
            new Dictionary<Guid, IReadOnlyList<GatePart>>
            {
                [one] = [Link(Scarlet), Open],
                [two] = [Link(Fire)],
            },
            Keys,
            Names
        );

        result
            .Tracks.Select(t => (t.Verdict, t.Links, t.LinksRight, t.SongsCovered, t.LabelledSongs))
            .Should()
            .Equal((GateVerdict.Partial, 1, 1, 1, 2), (GateVerdict.Wrong, 1, 0, 0, 1));
        (result.Links, result.LinksRight, result.SongsCovered, result.LabelledSongs)
            .Should()
            .Be((2, 1, 1, 3));
        result.Tracks[0].Got.Should().Be("Scarlet Begonias > open");
    }

    [Test]
    public void AnUnreadableLabelIsLeftOutAndAnUnknownNameIsListed()
    {
        SongGate
            .Parse(["artist\ttrack_uuid\ttitle\tsongs", $"moe\t{Guid.Empty}\td1t03\t?"])
            .Should()
            .BeEmpty();
        Score("Sugaree", Open).NotInCatalogue.Should().Equal("Sugaree");
    }
}
