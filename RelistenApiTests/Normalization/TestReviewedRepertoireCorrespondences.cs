using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

/// <summary>Synthetic reviewed accounts preserve original candidates and matcher evidence.</summary>
[TestFixture]
public sealed class TestReviewedRepertoireCorrespondences
{
    private const string Scope = "captured:fixture";
    private static readonly Guid Artist = Guid.Parse("dc265a12-e8ea-4f82-9656-66ea00000001");
    private static readonly ShadowRepertoireSong First = Song(
        "00000000-0000-4000-8000-000000000011"
    );
    private static readonly ShadowRepertoireSong Second = Song(
        "00000000-0000-4000-8000-000000000012"
    );
    private static readonly ShadowRepertoireSong Rival = Song(
        "00000000-0000-4000-8000-000000000013"
    );

    [Test]
    public void AcceptedDuplicateCorrespondenceCollapsesTargetsWithoutRewritingRawCandidateDiagnostics()
    {
        var rule = Rule(First, Second);
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot([rule]);
        var reviewed = Observed(snapshot, First, Second);
        var original = Segment(First, Second);
        var rawDiagnostics = JsonSerializer.Serialize(original.ComparisonDiagnostics);

        var result = reviewed.Apply(Scope, Artist, original);

        result.Outcome.Should().Be("proposed");
        result.Song.Should().Be(Second);
        result.CandidateSongs.Should().Equal(Second);
        result.ComparisonDiagnostics.Should().BeSameAs(original.ComparisonDiagnostics);
        JsonSerializer.Serialize(result.ComparisonDiagnostics).Should().Be(rawDiagnostics);
        result
            .ComparisonDiagnostics.Select(candidate => candidate.Comparison.CandidateUuid)
            .Should()
            .Equal(First.LegacySongUuid, Second.LegacySongUuid);
        original.Song.Should().BeNull();
        original.CandidateSongs.Should().Equal(First, Second);
        result.Evidence.Should().Be(original.Evidence);
        var knowledge = result.Knowledge.Should().ContainSingle().Subject;
        knowledge.SnapshotSha256.Should().Be(snapshot.Sha256);
        knowledge.AppliedRules.Single().RuleUuid.Should().Be(rule.RuleUuid);
        knowledge.Target!.CanonicalUuid.Should().BeNull();
    }

    [Test]
    public void ArtistLiteralAliasUpdatesAnIndependentAccountAndWithdrawalRestoresItsInterpretation()
    {
        var source = First with { Name = "Playin' in the Band" };
        var target = Second with { Name = "Playing in the Band" };
        var rule = Rule(source, target);
        rule = rule with
        {
            Definition = rule.Definition with
            {
                Selector = new("repertoire", Literal: source.Name, ArtistUuid: Artist),
            },
        };
        var original = Segment(source);

        var result = Observed(ExpertKnowledgeResolver.CreateSnapshot([rule]), source, target)
            .Apply(Scope, Artist, original);
        var withdrawn = Observed(
                ExpertKnowledgeResolver.CreateSnapshot([
                    rule with
                    {
                        Revision = 2,
                        State = "retracted",
                    },
                ]),
                source,
                target
            )
            .Apply(Scope, Artist, original);

        result.Song.Should().Be(target);
        result.Knowledge.Should().ContainSingle();
        withdrawn.Song.Should().Be(source);
        original.Song.Should().Be(source);
        result.Evidence.Should().Be(original.Evidence);
    }

    [Test]
    public void AnUnreviewedRivalRemainsUnresolvedAfterTheReviewedDuplicateIsCollapsed()
    {
        var original = Segment(First, Second, Rival);
        var reviewed = Observed(
            ExpertKnowledgeResolver.CreateSnapshot([Rule(First, Second)]),
            First,
            Second,
            Rival
        );

        var result = reviewed.Apply(Scope, Artist, original);

        result.Outcome.Should().Be("unresolved");
        result.Song.Should().BeNull();
        result.CandidateSongs.Should().Equal(Second, Rival);
        result.ComparisonDiagnostics.Should().BeSameAs(original.ComparisonDiagnostics);
        result
            .ComparisonDiagnostics.Select(candidate => candidate.Song.EntityKey)
            .Should()
            .Equal(First.EntityKey, Second.EntityKey, Rival.EntityKey);
    }

    [TestCase("rejected")]
    [TestCase("retracted")]
    public void RejectedOrRetractedReviewRestoresTheOriginalAmbiguity(string state)
    {
        var accepted = Rule(First, Second);
        var original = Segment(First, Second);
        var before = Observed(ExpertKnowledgeResolver.CreateSnapshot([accepted]), First, Second)
            .Apply(Scope, Artist, original);
        var changed = accepted with
        {
            Revision = 2,
            State = state,
            LastOperationUuid = Guid.NewGuid(),
        };

        var result = Observed(ExpertKnowledgeResolver.CreateSnapshot([changed]), First, Second)
            .Apply(Scope, Artist, original);

        before.Song.Should().Be(Second);
        result.Outcome.Should().Be("unresolved");
        result.Song.Should().BeNull();
        result.CandidateSongs.Should().Equal(First, Second);
        result.ComparisonDiagnostics.Should().BeSameAs(original.ComparisonDiagnostics);
    }

    [Test]
    public void DirectCorrespondenceChainsDoNotSupplyTransitiveAuthority()
    {
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot([
            Rule(First, Second),
            Rule(Second, Rival),
        ]);
        var reviewed = Observed(snapshot, First, Second, Rival);

        var single = reviewed.Apply(Scope, Artist, Segment(First));
        var ambiguous = reviewed.Apply(Scope, Artist, Segment(First, Second));

        single.Song.Should().Be(Second);
        single.Song.Should().NotBe(Rival);
        ambiguous.Outcome.Should().Be("unresolved");
        ambiguous.Song.Should().BeNull();
        ambiguous.CandidateSongs.Should().Equal(Second, Rival);
    }

    [TestCase("missing")]
    [TestCase("artist")]
    [TestCase("scope")]
    public void ReviewedTargetsMustAlreadyExistInTheSameScopeAndArtist(string unavailable)
    {
        const string otherScope = "captured:other";
        var rule = Rule(First, Second, targetScope: unavailable == "scope" ? otherScope : Scope);
        var reviewed = Observed(ExpertKnowledgeResolver.CreateSnapshot([rule]), First);
        if (unavailable == "artist")
        {
            reviewed.Observe(
                Scope,
                Second with
                {
                    ArtistUuid = Guid.Parse("dc265a12-e8ea-4f82-9656-66ea00000002"),
                }
            );
        }
        else if (unavailable == "scope")
        {
            reviewed.Observe(otherScope, Second);
        }

        var result = reviewed.Apply(Scope, Artist, Segment(First));

        result.Outcome.Should().Be("unresolved");
        result.Song.Should().BeNull();
        result.CandidateSongs.Should().Equal(First);
        result.Knowledge.Should().ContainSingle();
    }

    [TestCase("hash")]
    [TestCase("rule_content")]
    public void AReviewedSnapshotCannotBeRelabeledWithoutItsContentHash(string mutation)
    {
        var rule = Rule(First, Second);
        var snapshot = ExpertKnowledgeResolver.CreateSnapshot([rule]);
        var changed =
            mutation == "hash"
                ? snapshot with
                {
                    Sha256 = new string('0', 64),
                }
                : snapshot with
                {
                    Rules =
                    [
                        rule with
                        {
                            Definition = rule.Definition with
                            {
                                Target = new(
                                    "songs",
                                    "repertoire",
                                    ReviewedRepertoireCorrespondences.StableKey(
                                        Scope,
                                        Rival.EntityKey
                                    )
                                ),
                            },
                        },
                    ],
                };
        var load = () => new ReviewedRepertoireCorrespondences(changed);

        load.Should().Throw<ArgumentException>();
    }

    private static ReviewedRepertoireCorrespondences Observed(
        ExpertKnowledgeSnapshot snapshot,
        params ShadowRepertoireSong[] songs
    )
    {
        var reviewed = new ReviewedRepertoireCorrespondences(snapshot);
        foreach (var song in songs)
        {
            reviewed.Observe(Scope, song);
        }
        return reviewed;
    }

    private static ShadowRepertoireSong Song(string uuid)
    {
        var id = Guid.Parse(uuid);
        return new(
            ShadowRepertoireAdapter.KnownEntityKey(id),
            Artist,
            id,
            "Shared Song",
            "captured_artist_dictionary"
        );
    }

    private static ShadowRepertoireSegment Segment(params ShadowRepertoireSong[] songs)
    {
        var extracted = new TrackTitleExtractor().Extract(Artist, songs[0].Name).Segments.Single();
        var matcher = new IdentityCandidateMatcher();
        var diagnostics = songs
            .Select(song => new ShadowRepertoireCandidate(
                song,
                [new("song_sample:" + song.LegacySongUuid, "/row/name", song.Name)],
                matcher.CompareRepertoire(
                    new(null, Artist, [new(extracted.Title!)], []),
                    new(song.LegacySongUuid, Artist, [new(song.Name)], [])
                )
            ))
            .ToArray();
        return new(
            extracted.Position,
            extracted.Kind,
            extracted.Title,
            extracted.Evidence,
            songs.Length == 1 ? "proposed" : "unresolved",
            null,
            songs.Length == 1 ? songs[0] : null,
            songs.Length == 1 ? [] : songs,
            diagnostics
        );
    }

    private static ExpertKnowledgeRule Rule(
        ShadowRepertoireSong source,
        ShadowRepertoireSong target,
        string targetScope = Scope
    )
    {
        var account = ReviewedRepertoireCorrespondences.StableKey(Scope, source.EntityKey);
        var destination = ReviewedRepertoireCorrespondences.StableKey(
            targetScope,
            target.EntityKey
        );
        var evidence = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                account,
                destination,
                artistUuid = Artist,
            }
        );
        return new(
            Guid.NewGuid(),
            1,
            "accepted",
            new(
                "songs",
                new("repertoire", AccountStableKey: account, ArtistUuid: Artist),
                new("songs", "repertoire", destination),
                [
                    new(
                        "/synthetic/repertoire-review.json",
                        evidence.Length,
                        Convert.ToHexStringLower(SHA256.HashData(evidence))
                    ),
                ],
                "Synthetic retained duplicate-song account correspondence for a pure fixture."
            ),
            Guid.NewGuid(),
            new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc)
        );
    }
}
