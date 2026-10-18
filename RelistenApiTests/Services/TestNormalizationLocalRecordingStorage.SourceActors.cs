using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationLocalRecordingStorage
{
    [Test]
    public async Task LocalAliasCheckpointRetainsItsExactSelectedRootAndDirectory()
    {
        var context = await Context(sourceActorText: true);
        var batch = await Batch(context);
        batch
            .Extraction.SourceActorAliases!.SelectMany(scope => scope.Extraction.Assertions)
            .Select(assertion => assertion.Name)
            .Should()
            .Equal("Directory Avery", "Selected Robin");

        await proposals.SaveExtractionAsync(batch);
        (
            await fixture.Work.TryPublishAsync(
                batch.AttemptUuid,
                (c, t, ct) => proposals.WritePublishedAsync(c, t, batch, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        (await proposals.GetAsync(batch.SourceUuid)).Should().BeEquivalentTo(batch);
    }

    [TestCase("sibling_account")]
    [TestCase("sibling_citation")]
    [TestCase("sibling_context")]
    [TestCase("root_as_file")]
    [TestCase("missing_root_scope")]
    public async Task LocalAliasCheckpointCannotBorrowAnotherRootAccount(string change)
    {
        var context = await Context(sourceActorText: true);
        var batch = await Batch(context);
        var scopes = batch.Extraction.SourceActorAliases!;
        var rootScope = scopes.Single(scope => scope.InputRevisionUuid == context.Root.Uuid);
        var root = NormalizationLocalRecordingSelection.DecodeRoot(context.Root, new());
        var sibling = root.Recordings.Single(recording =>
            recording.Origin.Item != context.Directory.Resource
        );
        var other = new NormalizationRecordingSourceActorAliasScope(
            RetainedSourceActorAliasExtractor.Version,
            context.Root.Uuid,
            context.Root.Resource,
            sibling.Origin,
            new RetainedSourceActorAliasExtractor().Extract(context.Root, sibling.Text)
        );
        other.Extraction.Assertions.Should().ContainSingle();
        var assertion = rootScope.Extraction.Assertions.Single();
        var otherAssertion = other.Extraction.Assertions.Single();
        var changedRoot = change switch
        {
            "sibling_account" => other,
            "sibling_citation" => rootScope with
            {
                Extraction = rootScope.Extraction with
                {
                    Assertions = [assertion with { NameEvidence = otherAssertion.NameEvidence }],
                },
            },
            "sibling_context" => rootScope with
            {
                Extraction = rootScope.Extraction with
                {
                    Assertions = [assertion with { Context = otherAssertion.Context }],
                },
            },
            "root_as_file" => rootScope with { FileOrigin = rootScope.RecordingOrigin },
            "missing_root_scope" => rootScope,
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var invalid = batch with
        {
            Extraction = batch.Extraction with
            {
                SourceActorAliases =
                    change == "missing_root_scope"
                        ? scopes.Where(scope => scope != rootScope).ToArray()
                        : scopes
                            .Select(scope => scope == rootScope ? changedRoot : scope)
                            .ToArray(),
            },
        };

        Func<Task> save = () => proposals.SaveExtractionAsync(invalid);

        await save.Should().ThrowAsync<ArgumentException>();
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeNull();
        await proposals.SaveExtractionAsync(batch);
        (await proposals.GetAttemptBatchAsync(batch.AttemptUuid)).Should().BeEquivalentTo(batch);
    }
}
