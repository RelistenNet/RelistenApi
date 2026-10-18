using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [TestCase("lookup_key")]
    [TestCase("alias_relation")]
    [TestCase("stripped_facts")]
    [TestCase("other_actor")]
    public async Task ValidQuotesDoNotAuthorizeForgedSemanticActorFacts(string change)
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source(
            change == "other_actor"
                ? name + " and Another Recorder"
                : name + " aka Night Owl (actor@example.test)"
        );
        var extraction = await PublishForgedActorExtraction(
            source,
            credits =>
            {
                var credit = credits[0];
                var facts = credit.Mention.ActorFacts!;
                var altered = change switch
                {
                    "lookup_key" => facts with
                    {
                        Contacts =
                        [
                            facts.Contacts.Single() with
                            {
                                LookupKey = "invented@example.test",
                            },
                        ],
                    },
                    "alias_relation" => facts with
                    {
                        Aliases =
                        [
                            facts.Aliases.Single() with
                            {
                                Wording = "Invented Unrelated Alias",
                            },
                        ],
                    },
                    "stripped_facts" => null,
                    "other_actor" => credits[1].Mention.ActorFacts,
                    _ => throw new ArgumentOutOfRangeException(nameof(change)),
                };
                return credits
                    .Select(item =>
                        item == credit
                            ? item with
                            {
                                Mention = item.Mention with { ActorFacts = altered },
                            }
                            : item
                    )
                    .ToArray();
            }
        );
        var resolution = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid
        );

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        application.Result.Publications.Should().BeEmpty();
        (await people.ReadCreditContextAsync(source.SourceUuid)).Credits.Should().BeEmpty();
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        (await applier.ApplyRecordingAsync(application.AttemptUuid, resolution.AttemptUuid))
            .Result!.Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task RemovingOriginalUnresolvedCreditsCannotClaimCompleteActorCoverage()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source(name + " and unknown");
        var extraction = await PublishForgedActorExtraction(
            source,
            credits => credits,
            alterExtraction: value => value with { UnresolvedCredits = [] }
        );
        extraction.Extraction.UnresolvedCredits.Should().BeEmpty();
        var resolution = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid
        );

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Stale);
        application.Result.Publications.Should().BeEmpty();
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task UnsupportedRecordedProducerCannotPublishNewIndexedActorContext()
    {
        var name = UniqueName();
        await Person(name);
        await ActivatePolicy();
        var source = await Source(name + " (actor@example.test)");
        var extraction = await PublishForgedActorExtraction(
            source,
            credits => credits,
            NormalizationRecordingProcessor.Version + "/unsupported-fixture"
        );
        var resolution = await resolver.ResolveRecordingAsync(
            Guid.NewGuid(),
            extraction.AttemptUuid
        );

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Unresolved);
        application.Result.Reason.Should().Be("unsupported_extraction_version");
        (await people.GetRecordingActorContextsAsync(source.SourceUuid)).Should().BeEmpty();
        (await extractions.GetAttemptBatchAsync(extraction.AttemptUuid)).Should().NotBeNull();
    }

    [Test]
    public async Task EqualNamesWithDifferentActorHintsRetainDifferentCompleteCandidateQueries()
    {
        var name = UniqueName();
        var source = await Source(name + " (one@example.test) and " + name + " (two@example.test)");

        var resolution = await Resolve(source);

        var claims = resolution.Preparation!.Inputs.Single().Claims;
        claims.Should().HaveCount(2);
        claims.Select(claim => claim.Candidates!.QueryHash).Distinct().Should().HaveCount(2);
        claims
            .Select(claim => claim.Candidates!.Query.PeopleHints!.Contacts.Single().LookupKey)
            .Should()
            .BeEquivalentTo("one@example.test", "two@example.test");
        claims.Should().OnlyContain(claim => claim.Candidates!.Complete);
    }

    [Test]
    public async Task ExplicitGroupEvidenceCannotUseTheExistingPersonOnlyCreationCapability()
    {
        await ActivatePolicy(allowCreation: true);
        var source = await Source(UniqueName() + " (a recording group)");
        var resolution = await Resolve(source);

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application
            .Plan!.Decisions.Single()
            .Reason.Should()
            .Be("unsupported_creation_actor_evidence");
        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Review);
        AssertDecisionAcknowledged(application);
    }

    [Test]
    public async Task CreditedProjectProfileDoesNotSupplyThePersonOnlyCreationClassifierWithKind()
    {
        await ActivatePolicy(allowCreation: true);
        var name = Capitalize(RandomWord()) + " Crew";
        var source = await Source(name + " (https://project.example.test)");
        var resolution = await Resolve(source);
        var actor = resolution
            .Preparation!.Inputs.Single()
            .Claims.Single()
            .Proposal.Mention.ActorFacts!;
        actor.Kind.Should().BeNull();

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application
            .Plan!.Decisions.Single()
            .Reason.Should()
            .Be("unsupported_creation_actor_evidence");
        AssertDecisionAcknowledged(application);
        application.Result!.Disposition.Should().Be(PeopleAcceptanceDisposition.Review);
    }

    [Test]
    public async Task EvaluatedFixtureContextWeightRanksTheCompletePoolWithoutIdentifierAuthority()
    {
        var winnerName = UniqueName();
        var winner = await Person(winnerName);
        await ActivatePolicy();
        var seed = await Source(winnerName + " (weighted_fixture@example.test)");
        var seedResolution = await Resolve(seed);
        (await applier.ApplyRecordingAsync(Guid.NewGuid(), seedResolution.AttemptUuid))
            .Result!.Disposition.Should()
            .Be(PeopleAcceptanceDisposition.Applied);
        var rivalName = UniqueName();
        var rival = await Person(rivalName);
        await ActivatePolicy(matchThreshold: 3.5, contactWeight: 4);
        var source = await Source(rivalName + " (weighted_fixture@example.test)");
        var resolution = await Resolve(source);
        var claim = resolution.Preparation!.Inputs.Single().Claims.Single();
        claim
            .Candidates!.Selection.Items.Select(value => value.Uuid)
            .Should()
            .Contain([winner, rival]);
        claim.Candidates.Query.ExternalIdentities.Should().BeEmpty();

        var application = await applier.ApplyRecordingAsync(Guid.NewGuid(), resolution.AttemptUuid);

        application
            .Result!.Disposition.Should()
            .Be(
                PeopleAcceptanceDisposition.Applied,
                "application audit: {0}",
                ApplicationAudit(application)
            );
        application.Plan!.Decisions.Single().PersonUuid.Should().Be(winner);
        application
            .Plan.Decisions.Single()
            .Candidates.Select(value => value.PersonUuid)
            .Should()
            .Contain([winner, rival]);
        (await people.ReadCreditContextAsync(source.SourceUuid))
            .Credits.Single()
            .Credit.PersonUuid.Should()
            .Be(winner);
    }

    // Use the real immutable input, checked binder, public proposal checkpoint and Work
    // publication. Only semantic actor payloads are forged, so raw citation checks still pass.
    private async Task<NormalizationRecordingProposalBatch> PublishForgedActorExtraction(
        CatalogSource source,
        Func<
            IReadOnlyList<NormalizationRecordingCreditProposal>,
            IReadOnlyList<NormalizationRecordingCreditProposal>
        > alter,
        string? processingVersion = null,
        Func<NormalizationRecordingExtraction, NormalizationRecordingExtraction>? alterExtraction =
            null
    )
    {
        var version = processingVersion ?? NormalizationRecordingProcessor.Version;
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var snapshot = (
            await new NormalizationLegacyInputService(fixture.Db, evidence).RefreshRecordingAsync(
                source.SourceUuid
            )
        )!;
        var decoded = new NormalizationProviderDecoder().Decode(snapshot.Capture.Revision);
        var binding = await new NormalizationCatalogBindingService(
            fixture.Db
        ).BindRefreshedRecordingAsync(snapshot, decoded.Recordings.Single());
        var contributor = NormalizationRecordingProcessor.Contributor(source.SourceUuid);
        var preparation = new NormalizationRecordingPreparation(
            snapshot.Capture.Fetch.FetchUuid,
            snapshot.CatalogInputs,
            binding,
            null,
            0,
            contributor,
            ExpectedContributions: []
        );
        var attempt = await extractions.GetOrCreateAttemptAsync(
            Guid.NewGuid(),
            source.SourceUuid,
            version
        );
        await extractions.SavePreparationAsync(attempt.AttemptUuid, preparation);
        var ticket = await fixture.Work.BeginContributedBatchAsync(
            attempt.AttemptUuid,
            NormalizationRecordingProcessor.Owner(source.SourceUuid),
            version,
            [
                new(
                    new(
                        contributor,
                        0,
                        binding.Inputs.Select(value => value.Resource).ToArray(),
                        []
                    ),
                    binding.Inputs,
                    binding.CatalogInputs,
                    binding.SourceIdentities,
                    binding.MappingRegistry,
                    SourceUuids: binding.SourceUuids
                ),
            ],
            expectedContributionRevisions: []
        );
        var output = new NormalizationRecordingExtractor(new(), new()).Extract(
            snapshot.Capture.Revision,
            decoded,
            binding
        );
        output = output with { Credits = alter(output.Credits) };
        output = alterExtraction?.Invoke(output) ?? output;
        var batch = new NormalizationRecordingProposalBatch(
            attempt.AttemptUuid,
            source.SourceUuid,
            binding.Recording.Value!.ArtistUuid,
            attempt.ProcessingVersion,
            preparation,
            ticket,
            output
        );
        await extractions.SaveExtractionAsync(batch);
        (
            await fixture.Work.TryPublishAsync(
                attempt.AttemptUuid,
                (c, t, ct) => extractions.WritePublishedAsync(c, t, batch, ct)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return batch;
    }
}
