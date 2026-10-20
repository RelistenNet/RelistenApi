using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Current title interpretation must improve file discovery without moving its audio.</summary>
[TestFixture]
public sealed class TestRetainedFileTitleReplay
{
    private static readonly Guid Artist = Guid.NewGuid();
    private static readonly Guid File = Guid.NewGuid();
    private static readonly Guid Source = Guid.NewGuid();
    private static readonly Guid Show = Guid.NewGuid();
    private static readonly MusicTitleWitness Witness = new(
        "/synthetic/retained",
        "file_contents/retained.jsonl.gz",
        1,
        27,
        null,
        new('a', 64)
    );

    [Test]
    public void PreviouslyUnresolvedDuplicateTitleFindsOneSongAndKeepsEveryOriginalIdentifier()
    {
        var first = Guid.Parse("00000001-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000002-0000-0000-0000-000000000002");
        var retained = Retained("Drums");
        var replay = Replay(retained, Name("Drums", first), Name("DRUMS", second));

        replay.FileUuid.Should().Be(File);
        replay.SourceUuid.Should().Be(Source);
        replay.ShowUuid.Should().Be(Show);
        replay.OriginalWitness.Should().Be(Witness);
        replay.Correspondence!.Targets.Should().ContainSingle();
        var target = replay.Correspondence.Targets.Single();
        target.Disposition.Should().Be("proposed");
        target
            .ComparisonDiagnostics.Select(value => value.Song.LegacySongUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { first, second });
        retained.TitleInterpretation!.Segments.Should().BeEmpty();
        retained.TitleInterpretation.IsResolvedByExtractor.Should().BeFalse();
        replay
            .Correspondence.OriginalKey.Should()
            .Be(ShadowRepertoireAdapter.ProvisionalEntityKey(Artist, "Drums"));
    }

    [Test]
    public void RepeatedMusicalMentionsRemainSeparateFromTheirOneFileAndPerformance()
    {
        var replay = Replay(
            Retained("Drums > Space > Drums"),
            Name("Drums", Guid.NewGuid()),
            Name("Space", Guid.NewGuid())
        );

        replay
            .Correspondence!.Targets.Select(value => value.Song.Name)
            .Should()
            .Equal("Drums", "Space", "Drums");
        replay
            .Correspondence.Targets.Select(value => value.Evidence.StartCodePoint)
            .Should()
            .BeInAscendingOrder()
            .And.OnlyHaveUniqueItems();
        replay.FileUuid.Should().Be(File);
        replay.ShowUuid.Should().Be(Show);
    }

    [Test]
    public void ContradictoryQualifiedWorksStayReviewableInsteadOfChoosingTheFirstName()
    {
        var replay = Replay(
            Retained("Home"),
            Name("Home", Guid.NewGuid(), "work:a"),
            Name("Home", Guid.NewGuid(), "work:b")
        );

        replay.Correspondence!.Disposition.Should().Be("review");
        replay
            .Correspondence.Targets.Should()
            .HaveCount(2)
            .And.OnlyContain(value =>
                value.Disposition == "review" && value.Reason == "qualified_work_contradiction"
            );
        replay.SourceUuid.Should().Be(Source);
    }

    [Test]
    public void MissingTitleDoesNotRemoveAPlayableFileOrInventASong()
    {
        var replay = Replay(Retained(null));

        replay.Correspondence.Should().BeNull();
        replay.Reason.Should().Be("missing_original_title");
        replay.FileUuid.Should().Be(File);
        replay.SourceUuid.Should().Be(Source);
        replay.ShowUuid.Should().Be(Show);
    }

    [Test]
    public void ConflictingPlaybackOwnershipCannotBecomeAReplayMembership()
    {
        var retained = Retained("Drums");
        retained = retained with
        {
            Playback = retained.Playback with { ShowUuid = Guid.NewGuid() },
        };

        Action act = () => Replay(retained, Name("Drums", Guid.NewGuid()));

        act.Should().Throw<InvalidDataException>();
    }

    private static ReplayedFileTitle Replay(
        ShadowFileContents retained,
        params MusicDictionaryName[] names
    )
    {
        var resolver = new MusicRepertoireConsolidator();
        foreach (var name in names)
        {
            resolver.ObserveDictionary(name);
        }
        resolver.CompleteObservation();
        return new RetainedFileTitleReplay(resolver).Resolve(
            "catalogue",
            Artist,
            File,
            retained,
            Witness
        );
    }

    private static MusicDictionaryName Name(string title, Guid uuid, params string[] works) =>
        new(
            "catalogue",
            Artist,
            ShadowRepertoireAdapter.KnownEntityKey(uuid),
            uuid,
            title,
            "song_sample:" + uuid,
            works,
            Witness with
            {
                ValuePointer = "/proposal/name",
            }
        );

    private static ShadowFileContents Retained(string? title) =>
        new(
            new(Show, 1, Source, File, null, "1980-01-01", "default", null, []),
            new(File, Source, Show, "https://example.test/original.mp3"),
            title == null ? null : new(File, Source, "track-title-v1", title, false, [], [], null)
        );
}
