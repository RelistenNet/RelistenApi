using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleResolution
{
    [Test]
    public async Task OrdinaryWorkReusingSavedResolverUuidDoesNotAuthenticateConfigurationLineage()
    {
        var source = await Source("Unrelated credit admission " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var saved = await Stage(extraction);
        var preparation = saved.Preparation!;
        preparation.ExpectedContributions.Should().BeEmpty();
        var contribution = NormalizationPeopleResolutionReadSet.Contributions(preparation).Single();
        var owner = NormalizationPeopleResolutionProcessor.Owner(source.SourceUuid);

        // This public admission installs genuine observed bindings, but it is unrelated to
        // the saved resolver attempt and has no people-resolution proposal checkpoint.
        var unrelated = await fixture.Work.BeginContributedBatchAsync(
            Guid.NewGuid(),
            owner,
            saved.ProcessingVersion,
            [contribution],
            preparation.ExpectedConfigurationGeneration,
            expectedContributionRevisions: preparation.ExpectedContributions
        );
        var installed = await fixture.Work.GetInputContributionsAsync(owner);
        installed.UnmanagedBaseline.Should().BeNull();
        installed
            .Contributions.Should()
            .ContainSingle()
            .Which.Revision.Should()
            .Be(contribution.Contribution.ExpectedRevision + 1);
        var ordinary = await fixture.Work.BeginWorkAsync(
            saved.AttemptUuid,
            owner,
            saved.ProcessingVersion,
            contribution.Contribution.Resources,
            contribution.Contribution.DependencySubjectUuids,
            installed.Owner!.DesiredGeneration,
            catalogInputs: contribution.CatalogInputs,
            sourceIdentities: contribution.SourceIdentities,
            mappingRegistry: contribution.MappingRegistry,
            expectedInputRevisions: contribution.ExpectedInputRevisions,
            importedVenues: contribution.ImportedVenues,
            candidateUniverses: contribution.CandidateUniverses,
            expectedOwnerObservations: contribution.ExpectedOwnerObservations,
            sourceUuids: contribution.SourceUuids
        );

        // Even the entire returned ticket can coincide. Ordinary admission leaves the
        // contributor revision vector untouched, so those values cannot prove its mode.
        ordinary.Should().BeEquivalentTo(unrelated with { WorkUuid = saved.AttemptUuid });
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
        (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        var before = await AcceptedState();

        var refused = await NewConfigurationProcessor()
            .ResolveRecordingAsync(Guid.NewGuid(), extraction.AttemptUuid);

        refused
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Unresolved);
        refused.Result.Reason.Should().Be("unsupported_owner_inputs");
        refused.Preparation.Should().BeNull();
        (await store.GetAttemptBatchAsync(refused.AttemptUuid)).Should().BeNull();
        (await store.GetAsync(source.SourceUuid)).Should().BeNull();
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
        (await store.GetAttemptAsync(saved.AttemptUuid)).Should().BeEquivalentTo(saved);
        (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        (await AcceptedState()).Should().Be(before);
        var replay = await NewConfigurationProcessor()
            .ResolveRecordingAsync(refused.AttemptUuid, extraction.AttemptUuid);
        replay.Result!.Replayed.Should().BeTrue();
        replay.Result.Reason.Should().Be("unsupported_owner_inputs");
        (await fixture.Work.GetInputContributionsAsync(owner)).Should().BeEquivalentTo(installed);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GenuineAdmissionBeforeCheckpointSupportsExactResumeAndNewExtraction(
        bool replaceWithNewExtraction
    )
    {
        var source = await Source("Admitted credit configuration " + Guid.NewGuid().ToString("N"));
        var extraction = await Extract(source);
        var saved = await Stage(extraction);
        var ticket = await Admit(saved);
        var owner = NormalizationPeopleResolutionProcessor.Owner(source.SourceUuid);
        var admitted = await fixture.Work.GetInputContributionsAsync(owner);
        var installed = admitted.Contributions.Should().ContainSingle().Which;
        (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        (await store.GetAttemptAsync(saved.AttemptUuid)).Should().BeEquivalentTo(saved);
        var before = await AcceptedState();
        var currentExtraction = replaceWithNewExtraction ? await Extract(source) : extraction;
        var attemptUuid = replaceWithNewExtraction ? Guid.NewGuid() : saved.AttemptUuid;

        var recovered = await NewConfigurationProcessor()
            .ResolveRecordingAsync(attemptUuid, currentExtraction.AttemptUuid);

        recovered
            .Result!.Disposition.Should()
            .Be(NormalizationPeopleResolutionDisposition.Published);
        var membership = await fixture.Work.GetInputContributionsAsync(owner);
        var current = membership.Contributions.Should().ContainSingle().Which;
        current.Contributor.Should().Be(installed.Contributor);
        current.Revision.Should().Be(installed.Revision + (replaceWithNewExtraction ? 1 : 0));
        if (replaceWithNewExtraction)
        {
            recovered
                .Preparation!.ExpectedContributions.Should()
                .Equal(
                    new NormalizationInputContributionRevision(
                        installed.Contributor,
                        installed.Revision
                    )
                );
            recovered
                .Preparation.ExtractionObservation.PublishedWorkUuid.Should()
                .Be(currentExtraction.AttemptUuid);
            (await store.GetAttemptAsync(saved.AttemptUuid)).Should().BeEquivalentTo(saved);
            (await store.GetAttemptBatchAsync(saved.AttemptUuid)).Should().BeNull();
        }
        else
        {
            recovered.Preparation.Should().BeEquivalentTo(saved.Preparation);
            var batch = (await store.GetAttemptBatchAsync(saved.AttemptUuid))!;
            batch.Ticket.Should().BeEquivalentTo(ticket);
            membership.Contributions.Should().BeEquivalentTo(admitted.Contributions);
        }
        (await store.GetAsync(source.SourceUuid))!.AttemptUuid.Should().Be(attemptUuid);
        (await AcceptedState()).Should().Be(before);
    }

    private NormalizationPeopleResolutionProcessor NewConfigurationProcessor() =>
        new(people, extractions, candidates, decisions, store, fixture.Work);
}
