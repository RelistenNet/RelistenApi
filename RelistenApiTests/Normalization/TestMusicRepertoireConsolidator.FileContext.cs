using FluentAssertions;
using Relisten.Normalization.Batch;

namespace RelistenApiTests.Normalization;

public sealed partial class TestMusicRepertoireConsolidator
{
    [Test]
    public void ReviewedFileOnlyTargetIsAvailableAfterTheCompleteTitlePopulationIsObserved()
    {
        var source = Descriptor("Playin");
        var target = Descriptor("Playing in the Band");
        var snapshot = ReviewedSnapshot(source.OriginalKey, target.OriginalKey, "accepted");
        var resolver = new MusicRepertoireConsolidator(snapshot);
        resolver.ObserveRetainedTitle(Scope, Artist, source.Title!);
        resolver.ObserveRetainedTitle(Scope, Artist, target.Title!);
        resolver.CompleteObservation();

        var result = resolver.Resolve(source);

        result.Disposition.Should().Be("proposed");
        result
            .Targets.Should()
            .ContainSingle()
            .Which.Song.EntityKey.Should()
            .Be(target.OriginalKey);
        result
            .Targets.Single()
            .Knowledge.Should()
            .ContainSingle()
            .Which.SnapshotSha256.Should()
            .Be(snapshot.Sha256);
        resolver.DescriptorCount.Should().Be(0);
        result.SourceWitness.Should().Be(source.Witness);
    }

    [Test]
    public void FileContextDisplayAndReviewTargetsDoNotDependOnObservationOrder()
    {
        var source = Descriptor("Playin");
        var target = Descriptor("Playing in the Band");
        var snapshot = ReviewedSnapshot(source.OriginalKey, target.OriginalKey, "accepted");
        var values = new[] { source.Title!, "PLAYING IN THE BAND", target.Title! };
        var results = new[] { values, values.Reverse().ToArray() }
            .Select(titles =>
            {
                var resolver = new MusicRepertoireConsolidator(snapshot);
                foreach (var title in titles)
                {
                    resolver.ObserveRetainedTitle(Scope, Artist, title);
                }
                resolver.CompleteObservation();
                return resolver.Resolve(source);
            })
            .ToArray();

        results[0].Should().BeEquivalentTo(results[1]);
        results[0].Targets.Single().Song.EntityKey.Should().Be(target.OriginalKey);
    }
}
