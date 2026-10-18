using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationSourceActorAliasApplications
{
    [Test]
    public async Task MissingShowContextDoesNotBlockCompleteSourceActorAliasEvidence()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, IndependentActorName()));
        source
            .Resolution.Preparation!.Inputs.SelectMany(input => input.DecodeIssues)
            .Should()
            .Contain(issue => issue.Code == "missing_recording_show_context");

        var result = await Apply(source);

        result.Result!.Disposition.Should().Be(SourceActorAliasApplicationDisposition.Complete);
        result
            .Stages.Where(stage => stage.Receipt != null)
            .Should()
            .OnlyContain(stage => stage.OriginalPersonUuid == person);
        result
            .Stages.SelectMany(stage => stage.Facts.Inputs)
            .SelectMany(input => input.DecodeIssues)
            .Should()
            .Contain(
                issue => issue.Code == "missing_recording_show_context",
                "unrelated diagnostics remain retained even when actor evidence is complete"
            );
    }

    [Test]
    public async Task AnIncompleteActorFieldKeepsOldSupportDespiteIndividuallyRecognizedNames()
    {
        await SelectPolicy(Policy());
        var name = IndependentActorName();
        var oldAlias = IndependentActorName();
        var person = await Person(name);
        var source = await Source(Personnel(name, oldAlias));
        var previous = await Apply(source);
        var oldSupport = Receipts(previous)
            .Last()
            .ResultingMap.SelectMany(entry => entry.Bindings)
            .Select(binding => binding.SupportUuid)
            .ToArray();
        source = await Replace(
            source,
            Personnel(name, IndependentActorName())
                + "\nThis machine is also known as something else."
        );
        source
            .Resolution.Preparation!.Inputs.SelectMany(input => input.Scopes)
            .SelectMany(scope => scope.OriginalScope.Extraction.Scopes)
            .Should()
            .Contain(coverage => !coverage.Complete);

        var held = await Apply(source);

        held.Result!.Disposition.Should().NotBe(SourceActorAliasApplicationDisposition.Complete);
        (await CurrentAliases(person)).Should().Contain(value => value.Wording == oldAlias);
        Receipts(held)
            .SelectMany(receipt => receipt.RetiredSupportUuids)
            .Should()
            .NotIntersectWith(oldSupport);
    }

    [TestCase("future_actor_decode_failure")]
    [TestCase("expected_file_array")]
    public void UnknownAndActorRelevantDecodeIssuesRemainBlocking(string code)
    {
        // Unknown diagnostics cannot be manufactured by today's public decoder. Its shared
        // actor policy must still fail closed when a future decoder introduces one.
        var issue = new ProviderDecodeIssue(code, new(Guid.NewGuid(), "/source/description"));

        RecordingActorDecodeIssues.AffectsActorEvidence(issue).Should().BeTrue();
    }
}
