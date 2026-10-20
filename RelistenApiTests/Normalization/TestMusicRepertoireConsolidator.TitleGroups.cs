using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

public sealed partial class TestMusicRepertoireConsolidator
{
    [Test]
    public void ImportedDuplicatesAndFileTitlesResolveTogetherWithoutRewritingOriginalAccounts()
    {
        var first = DictionaryName("Scarlet Begonias", Scarlet);
        var second = DictionaryName("SCARLET BEGONIAS!", Fire);
        var originals = new[]
        {
            Descriptor(first.Title) with
            {
                OriginalKey = first.SongKey!,
                LegacySongUuid = Scarlet,
            },
            Descriptor(second.Title) with
            {
                OriginalKey = second.SongKey!,
                LegacySongUuid = Fire,
            },
            Descriptor("09.Scarlet Begonias"),
        };
        var resolver = ObservePopulation(originals, first, second);
        var reverse = ObservePopulation(originals.Reverse().ToArray(), second, first);
        var results = originals.Select(resolver.Resolve).ToArray();

        results
            .Select(result => result.Targets.Single().Song.EntityKey)
            .Distinct()
            .Should()
            .ContainSingle();
        results
            .Select(result => result.OriginalKey)
            .Should()
            .Equal(originals.Select(source => source.OriginalKey));
        results
            .Select(result => result.SourceWitness)
            .Should()
            .Equal(originals.Select(source => source.Witness));
        results.Should().OnlyContain(result => result.Disposition == "proposed");
        results.Should().BeEquivalentTo(originals.Select(reverse.Resolve));
        foreach (var result in results)
        {
            result
                .Targets.Single()
                .ComparisonDiagnostics.Select(candidate => candidate.Song.LegacySongUuid)
                .Should()
                .BeEquivalentTo(new Guid?[] { Scarlet, Fire });
            AssertCitations(result);
        }
    }

    [Test]
    public void ImportedIdentityWithoutADictionaryRowDoesNotBecomeAProvisionalTitleIdentity()
    {
        var source = Descriptor("Scarlet Begonias") with
        {
            OriginalKey = ShadowRepertoireAdapter.KnownEntityKey(Scarlet),
            LegacySongUuid = Scarlet,
        };
        var result = Resolve(source);

        result.Targets.Single().Song.LegacySongUuid.Should().Be(Scarlet);
        result.Targets.Single().Song.EntityKey.Should().Be(source.OriginalKey);
        result.OriginalKey.Should().Be(source.OriginalKey);
    }

    [Test]
    public void DistinctQualifiedWorksKeepKnownAccountsSeparateAndAnUnboundTitleReviewable()
    {
        var first = DictionaryName("Home", Scarlet);
        var second = DictionaryName("Home", Fire);
        var originals = new[]
        {
            Descriptor("Home") with
            {
                OriginalKey = first.SongKey!,
                LegacySongUuid = Scarlet,
                WorkKeys = ["work:first"],
            },
            Descriptor("Home") with
            {
                OriginalKey = second.SongKey!,
                LegacySongUuid = Fire,
                WorkKeys = ["work:second"],
            },
            Descriptor("09.Home"),
        };
        var resolver = ObservePopulation(originals, first, second);

        resolver.Resolve(originals[0]).Targets.Single().Song.LegacySongUuid.Should().Be(Scarlet);
        resolver.Resolve(originals[1]).Targets.Single().Song.LegacySongUuid.Should().Be(Fire);
        var title = resolver.Resolve(originals[2]);
        title.Disposition.Should().Be("review");
        title
            .Targets.Should()
            .HaveCount(2)
            .And.OnlyContain(target =>
                target.Disposition == "review" && target.Reason == "qualified_work_contradiction"
            );
    }

    [Test]
    public void KnownDescriptorWithoutADictionaryRowCannotAliasAContradictoryWork()
    {
        var source = Descriptor("Home") with
        {
            OriginalKey = ShadowRepertoireAdapter.KnownEntityKey(Scarlet),
            LegacySongUuid = Scarlet,
            WorkKeys = ["work:first"],
        };
        var result = Resolve(
            source,
            DictionaryName("Home", Fire) with
            {
                WorkKeys = ["work:second"],
            }
        );

        result.Disposition.Should().Be("proposed");
        result.Targets.Single().Song.LegacySongUuid.Should().Be(Scarlet);
        result.OriginalKey.Should().Be(source.OriginalKey);
    }

    [Test]
    public void OverlappingSupportedNamesUseOneDirectTargetForAllOriginalIdentities()
    {
        var third = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var originals = new[]
        {
            Descriptor("Foo") with
            {
                OriginalKey = ShadowRepertoireAdapter.KnownEntityKey(Scarlet),
                LegacySongUuid = Scarlet,
            },
            Descriptor("Foo") with
            {
                OriginalKey = ShadowRepertoireAdapter.KnownEntityKey(Fire),
                LegacySongUuid = Fire,
            },
            Descriptor("Bar") with
            {
                OriginalKey = ShadowRepertoireAdapter.KnownEntityKey(third),
                LegacySongUuid = third,
            },
            Descriptor("09.Foo"),
            Descriptor("09.Bar"),
        };
        var resolver = ObservePopulation(
            originals,
            DictionaryName("Foo", Scarlet),
            DictionaryName("Bar", Scarlet),
            DictionaryName("Foo", Fire),
            DictionaryName("Bar", third)
        );
        var results = originals.Select(resolver.Resolve).ToArray();

        results
            .Select(result => result.Targets.Single().Song.EntityKey)
            .Distinct()
            .Should()
            .ContainSingle();
        results.Should().OnlyContain(result => result.Disposition == "proposed");
        results
            .Select(result => result.OriginalKey)
            .Should()
            .Equal(originals.Select(source => source.OriginalKey));
        results
            .Should()
            .OnlyContain(result => result.Targets.Single().ComparisonDiagnostics.Count == 3);
    }

    [Test]
    public void CompoundAndRepeatedMentionsRetainTheirOwnPassagesAfterDuplicateIdentityResolution()
    {
        var source = Descriptor("Scarlet Begonias > Fire On The Mountain > Scarlet Begonias");
        var result = Resolve(
            source,
            DictionaryName("Scarlet Begonias", Scarlet),
            DictionaryName("SCARLET BEGONIAS", Guid.Parse("00000000-0000-4000-8000-000000000001")),
            DictionaryName("Fire On The Mountain", Fire)
        );

        result.Disposition.Should().Be("proposed");
        result.Targets.Should().HaveCount(3);
        result.Targets[0].Song.EntityKey.Should().Be(result.Targets[2].Song.EntityKey);
        result.Targets[1].Song.LegacySongUuid.Should().Be(Fire);
        result
            .Targets.Select(target => target.Evidence.StartCodePoint)
            .Distinct()
            .Should()
            .HaveCount(3);
        result
            .Targets.Should()
            .OnlyContain(target => target.Relation == MusicRepertoireRelation.CompoundMember);
        AssertCitations(result);
    }

    [Test]
    public void WithdrawingAReviewedChoiceRestoresTheAutomaticTitleDefault()
    {
        var first = DictionaryName("Shared Song", Scarlet);
        var second = DictionaryName("Shared Song", Fire);
        var source = Descriptor("09.Shared Song");
        var baseline = Resolve(source, first, second);
        var representative = baseline.Targets.Single().Song.EntityKey;
        var exception = representative == first.SongKey ? second.SongKey! : first.SongKey!;
        var reviewed = ReviewedSnapshot(source.OriginalKey, exception, "accepted");
        var accepted = ObservePopulation([source], reviewed, first, second).Resolve(source);
        var withdrawn = ExpertKnowledgeResolver.CreateSnapshot(
            reviewed
                .Rules.Select(rule =>
                    rule with
                    {
                        State = "retracted",
                        Revision = rule.Revision + 1,
                        LastOperationUuid = Guid.NewGuid(),
                    }
                )
                .ToArray()
        );
        var restored = ObservePopulation([source], withdrawn, first, second).Resolve(source);

        accepted.Targets.Single().Song.EntityKey.Should().Be(exception);
        restored.Targets.Single().Song.EntityKey.Should().Be(representative);
        restored.Targets.Single().Knowledge.Should().BeNull();
        restored.OriginalKey.Should().Be(source.OriginalKey);
    }

    [Test]
    public void ReviewedDeduplicationGuidesLaterOrdinaryDuplicatesWithoutAnotherReview()
    {
        var third = Guid.Parse("00000000-0000-4000-8000-000000000001");
        var names = new[]
        {
            DictionaryName("Shared Song", Scarlet),
            DictionaryName("Shared Song", Fire),
            DictionaryName("Shared Song", third),
        };
        var originals = names
            .Select(name =>
                Descriptor(name.Title) with
                {
                    OriginalKey = name.SongKey!,
                    LegacySongUuid = name.LegacySongUuid,
                }
            )
            .Append(Descriptor("09.Shared Song"))
            .ToArray();
        var snapshot = ReviewedSnapshot(names[0].SongKey!, names[1].SongKey!, "accepted");
        var resolver = ObservePopulation(originals, snapshot, names);

        foreach (var source in originals)
        {
            var result = resolver.Resolve(source);
            result.Disposition.Should().Be("proposed");
            result.Targets.Single().Song.LegacySongUuid.Should().Be(Fire);
            result.Targets.Single().ComparisonDiagnostics.Should().HaveCount(3);
            result.OriginalKey.Should().Be(source.OriginalKey);
        }
    }

    private static MusicRepertoireConsolidator ObservePopulation(
        MusicRepertoireDescriptor[] sources,
        params MusicDictionaryName[] names
    ) => ObservePopulation(sources, null, names);

    private static MusicRepertoireConsolidator ObservePopulation(
        MusicRepertoireDescriptor[] sources,
        ExpertKnowledgeSnapshot? knowledge,
        params MusicDictionaryName[] names
    )
    {
        var resolver = new MusicRepertoireConsolidator(knowledge);
        foreach (var name in names)
        {
            resolver.ObserveDictionary(name);
        }
        foreach (var source in sources)
        {
            resolver.ObserveDescriptor(source);
        }
        resolver.CompleteObservation();
        return resolver;
    }
}
