using FluentAssertions;
using Relisten.Normalization;

namespace RelistenApiTests.Normalization;

[TestFixture]
public sealed class TestArtistSongTitleResolver
{
    private static readonly Guid Artist = Guid.Parse("d20ca0c1-7219-41a5-9982-f4323c286de1");

    [Test]
    public void EqualArtistTitlesRetainEveryMemberAndChooseAStableRepresentative()
    {
        var first = Candidate("imported:a", "Scarlet Begonias");
        var second = Candidate("imported:b", " SCARLET--BEGONIAS ");

        var forward = new ArtistSongTitleResolver([first, second]).Resolve(
            Artist,
            "Scarlet Begonias"
        );
        var backward = new ArtistSongTitleResolver([second, first]).Resolve(
            Artist,
            "Scarlet Begonias"
        );

        forward.Kind.Should().Be(ArtistSongTitleResolutionKind.SharedTitle);
        forward.Representative.Should().BeEquivalentTo(first);
        forward
            .Members.Select(member => member.EntityKey)
            .Should()
            .Equal(first.EntityKey, second.EntityKey);
        backward.Should().BeEquivalentTo(forward, options => options.WithStrictOrdering());
    }

    [Test]
    public void TitleMatchCannotCrossArtistOrSelectAnotherSong()
    {
        var target = Candidate("imported:a", "Fire on the Mountain");
        var otherArtist = Candidate("imported:b", "Scarlet Begonias") with
        {
            ArtistUuid = Guid.Parse("a7c68c34-41a8-437e-8529-9dc1ca515bc2"),
        };

        var result = new ArtistSongTitleResolver([target, otherArtist]).Resolve(
            Artist,
            "Scarlet Begonias"
        );

        result.Kind.Should().Be(ArtistSongTitleResolutionKind.NoMatch);
        result.Representative.Should().BeNull();
        result.Members.Should().BeEmpty();
    }

    [Test]
    public void SupportedAliasUsesTheSameTitlePolicy()
    {
        var target = Candidate("imported:a", "Turn on Your Love Light") with
        {
            Titles = ["Turn on Your Love Light", "Lovelight"],
        };

        var result = new ArtistSongTitleResolver([target]).Resolve(Artist, "Lovelight");

        result.Kind.Should().Be(ArtistSongTitleResolutionKind.SharedTitle);
        result.Representative.Should().BeEquivalentTo(target);
    }

    [Test]
    public void MissingWorkEvidenceDoesNotBlockAnOtherwiseSharedTitle()
    {
        var unknown = Candidate("imported:a", "Scarlet Begonias");
        var known = Candidate("imported:b", "Scarlet Begonias", "musicbrainz:work:scarlet");

        var result = new ArtistSongTitleResolver([known, unknown]).Resolve(
            Artist,
            "Scarlet Begonias"
        );

        result.Kind.Should().Be(ArtistSongTitleResolutionKind.SharedTitle);
        result.Representative.Should().BeEquivalentTo(unknown);
        result
            .Members.Select(member => member.EntityKey)
            .Should()
            .Equal(unknown.EntityKey, known.EntityKey);
    }

    [Test]
    public void ConflictingWorksRetainCandidatesWithoutLettingUnknownEvidenceBridgeThem()
    {
        var unknown = Candidate("imported:a", "Home") with { Titles = ["Home", "Home Again"] };
        var first = Candidate("imported:b", "Home", "provider:work:first");
        var second = Candidate("imported:c", "Home Again", "provider:work:second");

        var result = new ArtistSongTitleResolver([second, unknown, first]).Resolve(Artist, "Home");

        result.Kind.Should().Be(ArtistSongTitleResolutionKind.ConflictingWorks);
        result.Representative.Should().BeNull();
        result
            .Members.Select(member => member.EntityKey)
            .Should()
            .Equal(unknown.EntityKey, first.EntityKey, second.EntityKey);
    }

    [Test]
    public void WorkSetsCompareIdentitiesWithoutDependingOnOrderOrRepeatedEvidence()
    {
        var first = Candidate("imported:a", "Medley", "work:first", "work:second");
        var second = Candidate("imported:b", "Medley", "work:second", "work:first", "work:first");

        var result = new ArtistSongTitleResolver([first, second]).Resolve(Artist, "Medley");

        result.Kind.Should().Be(ArtistSongTitleResolutionKind.SharedTitle);
        result.Representative.Should().BeEquivalentTo(first);
    }

    [TestCase("Café Night", "Cafe\u0301 Night", true)]
    [TestCase("Café Night", "Cafe Night", false)]
    [TestCase("Love Light", "Lovelight", false)]
    [TestCase("Tweezer Reprise", "Tweezer", false)]
    public void ExactTitlePolicyPreservesMusicalWordingAndUnicode(
        string storedTitle,
        string query,
        bool matches
    )
    {
        var candidate = Candidate("imported:a", storedTitle);

        var result = new ArtistSongTitleResolver([candidate]).Resolve(Artist, query);

        result
            .Kind.Should()
            .Be(
                matches
                    ? ArtistSongTitleResolutionKind.SharedTitle
                    : ArtistSongTitleResolutionKind.NoMatch
            );
    }

    [TestCase("")]
    [TestCase(" \t ")]
    [TestCase("---")]
    public void EmptyTitleCannotJoinUnrelatedUnknownSongs(string title)
    {
        var result = new ArtistSongTitleResolver([Candidate("imported:a", title)]).Resolve(
            Artist,
            title
        );

        result.Kind.Should().Be(ArtistSongTitleResolutionKind.NoMatch);
        result.Representative.Should().BeNull();
        result.Members.Should().BeEmpty();
    }

    [Test]
    public void OverlappingAliasesHaveOneDirectRepresentativeAcrossTheWholeComponent()
    {
        var shared = Candidate("imported:b", "Foo") with { Titles = ["Foo", "Bar"] };
        var foo = Candidate("imported:a", "Foo");
        var bar = Candidate("imported:c", "Bar");
        var resolver = new ArtistSongTitleResolver([bar, shared, foo]);

        var fooResult = resolver.Resolve(Artist, "Foo");
        var barResult = resolver.Resolve(Artist, "Bar");

        fooResult.Kind.Should().Be(ArtistSongTitleResolutionKind.SharedTitle);
        fooResult.Representative!.EntityKey.Should().Be(foo.EntityKey);
        fooResult
            .Members.Select(member => member.EntityKey)
            .Should()
            .Equal(foo.EntityKey, shared.EntityKey, bar.EntityKey);
        barResult.Should().BeSameAs(fooResult);
    }

    [Test]
    public void AliasCycleWithConflictingWorksBlocksEveryEntryPoint()
    {
        var first = Candidate("imported:a", "Foo", "work:first") with { Titles = ["Foo", "Bar"] };
        var unknown = Candidate("imported:b", "Bar") with { Titles = ["Bar", "Baz"] };
        var last = Candidate("imported:c", "Baz", "work:second") with { Titles = ["Baz", "Foo"] };
        var resolver = new ArtistSongTitleResolver([last, unknown, first]);

        foreach (var title in new[] { "Foo", "Bar", "Baz" })
        {
            var result = resolver.Resolve(Artist, title);
            result.Kind.Should().Be(ArtistSongTitleResolutionKind.ConflictingWorks);
            result.Representative.Should().BeNull();
            result
                .Members.Select(member => member.EntityKey)
                .Should()
                .Equal(first.EntityKey, unknown.EntityKey, last.EntityKey);
        }
    }

    [Test]
    public void CapturedIndexDoesNotChangeWhenCallerMutatesItsInputCollections()
    {
        var titles = new List<string> { "Foo" };
        var works = new List<string> { "work:first" };
        var candidates = new List<ArtistSongTitleCandidate>
        {
            Candidate("imported:a", "Foo") with
            {
                Titles = titles,
                WorkKeys = works,
            },
        };
        var resolver = new ArtistSongTitleResolver(candidates);

        titles[0] = "Bar";
        works.Add("work:second");
        candidates.Clear();

        var result = resolver.Resolve(Artist, "Foo");
        result.Kind.Should().Be(ArtistSongTitleResolutionKind.SharedTitle);
        result.Representative!.Titles.Should().Equal("Foo");
        result.Representative.WorkKeys.Should().Equal("work:first");
        resolver.Resolve(Artist, "Bar").Kind.Should().Be(ArtistSongTitleResolutionKind.NoMatch);
    }

    private static ArtistSongTitleCandidate Candidate(
        string key,
        string title,
        params string[] works
    ) => new(key, Artist, [title], works);
}
