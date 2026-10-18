using System.Text.Json;
using FluentAssertions;
using Relisten.Api.Models.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [TestCase(8)]
    [TestCase(1000)]
    [Category("SourceAliasScale"), CancelAfter(3_600_000)]
    public async Task ManyPersonOrchestrationReturnsBoundedHeadersAndSeparateOriginalStageProofs(
        int count
    )
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        await SelectPolicy(Policy());
        var actors = new List<(Guid Person, string Name, string Alias)>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Unrelated letter names keep this capacity case independent of broad-name
            // retrieval ties. Every existing identity still uses the real accepted service.
            var name = IndependentActorName();
            actors.Add((await Person(name), name, IndependentActorName()));
        }
        var source = await Source(
            "Personnel:\n"
                + string.Join(
                    "\n",
                    actors.Select(actor => $"{actor.Name} aka {actor.Alias} - vocals")
                )
        );
        Claims(source).Should().HaveCount(count);
        Claims(source)
            .Should()
            .OnlyContain(claim =>
                claim.Decision.Action == SourceActorResolutionAction.MatchExisting
            );

        // This intentionally bypasses the specifications' ExpandedPublisher audit helper.
        var result = await NewService()
            .ApplyAsync(Guid.NewGuid(), source.Resolution.AttemptUuid, cancellationToken);

        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        result
            .Stages.Select(stage => stage.OriginalPersonUuid)
            .Distinct()
            .Should()
            .HaveCount(count);
        // A spelling can have several cited spans, such as a name alone and its aka phrase.
        // Publication must retain every original citation rather than count distinct labels.
        var assertions = source
            .Resolution.Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.OriginalScope.Extraction.Assertions)
            .ToArray();
        var expectedNames = assertions.Sum(assertion =>
            1 + assertion.Aliases.Sum(alias => alias.Evidence.Count)
        );
        var maximumNamesPerClaim = assertions.Max(assertion =>
            1 + assertion.Aliases.Sum(alias => alias.Evidence.Count)
        );
        result
            .Stages.Where(stage => stage.Receipt != null)
            .Sum(stage => stage.Receipt!.Names.Count)
            .Should()
            .Be(expectedNames);
        result
            .Stages.Should()
            .OnlyContain(stage =>
                stage.PreparedNames.Count <= maximumNamesPerClaim && stage.Receipt != null
            );
        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThan(32 * 1024 * 1024);
        var headerReplay = (await applications.GetApplicationAsync(result.AttemptUuid))!;
        headerReplay
            .Should()
            .BeEquivalentTo(
                result,
                options => options.WithStrictOrdering().AllowingInfiniteRecursion()
            );
        var first = (await applications.GetStageAsync(result.Stages.First().StageUuid))!;
        var last = (await applications.GetStageAsync(result.Stages.Last().StageUuid))!;
        first
            .Facts.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.Claims)
            .Should()
            .HaveCount(count);
        last.Receipt!.ResultingMap.Should()
            .Contain(entry => entry.Scope.SourceUuid == source.Source.SourceUuid);
        first.StageUuid.Should().Be(result.Stages.First().StageUuid);
        last.StageUuid.Should().Be(result.Stages.Last().StageUuid);
    }

    private static string IndependentActorName() =>
        "Z"
        + new string(
            Guid.NewGuid()
                .ToString("N")
                .Select(character =>
                    char.IsDigit(character) ? (char)('g' + character - '0') : character
                )
                .ToArray()
        );
}
