using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationRecordingPipeline
{
    private const string PolicyOperator = "recording-pipeline-fixture-operator";

    [SetUp, TearDown]
    public async Task LeaveFixturePoliciesInactive()
    {
        var peopleState = await peoplePolicies.ReadStateAsync();
        if (peopleState.Active != null)
        {
            await peoplePolicies.ApplyDecisionAsync(
                new(
                    Guid.NewGuid(),
                    peopleState.OwnerObservation.SubjectUuid,
                    peopleState.OwnerObservation.EditorialRevision,
                    peopleState.Active.Policy.Artifact.Uuid,
                    null,
                    "End disposable recording pipeline fixture"
                ),
                PolicyOperator
            );
        }
        var venueState = await venuePolicies.ReadStateAsync();
        if (venueState.Active != null)
        {
            await venuePolicies.ApplyDecisionAsync(
                new(
                    Guid.NewGuid(),
                    venueState.OwnerObservation.SubjectUuid,
                    venueState.OwnerObservation.EditorialRevision,
                    venueState.Active.Policy.Artifact.Uuid,
                    null,
                    "End disposable recording pipeline fixture"
                ),
                PolicyOperator
            );
        }
    }

    [Test]
    public async Task RetainedRecordingReachesAcceptedPeopleAliasesAndVenueShowsAndReplaysOnce()
    {
        var suffix = new string(
            Guid.NewGuid()
                .ToString("N")
                .Select(c => char.IsDigit(c) ? (char)('k' + c - '0') : c)
                .ToArray()
        );
        var taperName = "Pipeline Taper " + suffix;
        var robinName = "Pipeline Robin " + suffix;
        var alias = "Finch " + suffix;
        var venueName = "Pipeline Hall " + suffix;
        var context = await Recording();
        var taper = await KnownPerson(taperName);
        var robin = await KnownPerson(robinName);
        var venue = await KnownVenue(venueName);
        var show = await PlayableShow(context, venueName);
        await ActivateFixturePolicies();
        var input = await Capture(
            context,
            taper: taperName,
            description: $"Musician: {robinName}\nPersonnel:\n{robinName} aka {alias} - vocals",
            venue: venueName
        );
        var request = await Request(input);

        var result = await pipeline.ProcessAsync(request);

        result
            .Disposition.Should()
            .Be(
                NormalizationRecordingPipelineDisposition.Completed,
                "all guarded child receipts should settle: {0}",
                JsonSerializer.Serialize(result)
            );
        result.Completion!.Status.Should().Be(NormalizationPublicationStatus.Published);
        var credits = (await Credits(context.Source.SourceUuid)).items;
        credits.Should().Contain(credit => credit.person_uuid == taper && credit.role == "taper");
        credits
            .Should()
            .Contain(credit => credit.person_uuid == robin && credit.role == "musician");
        credits
            .SelectMany(credit => credit.evidence)
            .Should()
            .Contain(value => value.input_revision_uuid == input.InputRevisionUuid);
        var controller = new NormalizationPeopleVenuesController(reads)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };
        var person = (await controller.Person(robin.ToString("D")))
            .Should()
            .BeOfType<JsonResult>()
            .Subject;
        person.Value.Should().BeOfType<NormalizedPerson>().Which.aliases.Should().Contain(alias);
        var venueShows = (await controller.VenueShows(venue.ToString("D")))
            .Should()
            .BeOfType<JsonResult>()
            .Subject;
        venueShows
            .Value.Should()
            .BeOfType<NormalizationPage<NormalizedShowReference>>()
            .Which.items.Should()
            .Contain(value => value.show_uuid == show);
        (await reads.ListPersonRecordingsAsync(taper))!
            .items.Should()
            .Contain(value =>
                value.source_uuid == context.Source.SourceUuid && value.show_uuid == show
            );

        var replay = await pipeline.ProcessAsync(request);
        replay.Completion!.Replayed.Should().BeTrue();
        replay.People!.AttemptUuid.Should().Be(result.People!.AttemptUuid);
        replay.Aliases!.AttemptUuid.Should().Be(result.Aliases!.AttemptUuid);
        replay.Venues.Single().AttemptUuid.Should().Be(result.Venues.Single().AttemptUuid);
        (await Credits(context.Source.SourceUuid))
            .items.Select(value => value.uuid)
            .Should()
            .BeEquivalentTo(credits.Select(value => value.uuid));
        var owner = (
            await fixture.Work.GetSubjectAsync(
                NormalizationRecordingPipeline.Owner(request.HandoffUuid)
            )
        )!;
        owner.PublishedWorkUuid.Should().Be(request.AttemptUuid);
        owner.PublishedGeneration.Should().Be(owner.DesiredGeneration);
    }

    private async Task<Guid> KnownPerson(string name)
    {
        var input = await fixture.Capture(name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        return person.Uuid;
    }

    private async Task<Guid> KnownVenue(string name)
    {
        var input = await fixture.CaptureJson(
            JsonSerializer.Serialize(new { name, locality = "San Francisco, CA" }),
            "venues",
            "/name"
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), name);
        await fixture.PublishVenues(
            venueService,
            input,
            new(
                [venue],
                [],
                [
                    new(
                        Guid.NewGuid(),
                        venue.Uuid,
                        "San Francisco, CA",
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        input.Reference with
                        {
                            JsonPointer = "/locality",
                        }
                    ),
                ],
                [],
                [],
                []
            )
        );
        return venue.Uuid;
    }

    private async Task<Guid> PlayableShow(RecordingContext context, string venueName)
    {
        var showUuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(async c =>
        {
            var venueId = await c.QuerySingleAsync<long>(
                """
                INSERT INTO venues(uuid,name,location,slug,upstream_identifier,created_at,updated_at)
                VALUES(@uuid,@venueName,'San Francisco, CA',@key,@key,now(),now()) RETURNING id
                """,
                new
                {
                    uuid = Guid.NewGuid(),
                    venueName,
                    key = Guid.NewGuid().ToString("N"),
                }
            );
            var showId = await c.QuerySingleAsync<long>(
                """
                INSERT INTO shows(artist_id,venue_id,date,display_date,performance_key,performance_type,is_legacy_survivor,uuid,updated_at)
                VALUES(@ArtistId,@venueId,'2026-10-17','2026-10-17',@key,'unknown',false,@showUuid,now()) RETURNING id
                """,
                new
                {
                    context.Source.ArtistId,
                    venueId,
                    key = Guid.NewGuid().ToString("N"),
                    showUuid,
                }
            );
            await c.ExecuteAsync(
                """
                UPDATE sources SET show_id=@showId,venue_id=@venueId WHERE id=@SourceId;
                INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
                VALUES(@SourceId,@showId,'accepted','2026-10-17','{}')
                """,
                new
                {
                    context.Source.SourceId,
                    showId,
                    venueId,
                }
            );
        });
        return showUuid;
    }

    // These attestations exercise registry contracts only in the explicitly enabled
    // disposable test database. They are not evidence for production qualification.
    private async Task ActivateFixturePolicies()
    {
        var applicability = new SourceActorAliasPolicyApplicability(
            PeopleResolutionPolicyEvaluator.SourceInputForm,
            NormalizationRecordingProcessor.Version,
            NormalizationRecordingExtractor.Version,
            SourceActorAliasExtractor.Version,
            PeopleActorProjection.SourceAliasVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            PeopleResolutionPolicyEvaluator.ScorerVersion
        );
        var artifact = new PeopleAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "recording-pipeline-fixture-v1",
            "",
            PeopleResolutionPolicyEvaluator.ScorerVersion,
            PeopleResolutionPolicyEvaluator.RetrievalVersion,
            NormalizationCandidateService.PeopleFactsVersion,
            NormalizationRecordingExtractor.Version,
            NormalizedPeopleService.CreditContextVersion,
            ["retained_credit", PeopleResolutionPolicyEvaluator.SourceInputForm],
            new(0, 1, 1, 1, 1.5, .5),
            FixtureOnly: true,
            SourceActorAliasApplicability: applicability
        );
        artifact = artifact with
        {
            ContentHash = PeopleResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new PeopleAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-evaluator",
            "fixture-reviewer",
            PolicyOperator,
            new(100, 0),
            FixtureOnly: true,
            SourceActorAliasEvaluation: new(
                PeopleResolutionPolicyService.ComputeSourceActorApplicabilityHash(applicability),
                new string('a', 64),
                new string('b', 64),
                new string('c', 64),
                "fixture-source-evaluator",
                "fixture-source-reviewer",
                new(100, 0)
            )
        );
        evaluation = evaluation with
        {
            ReceiptHash = PeopleResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        await peoplePolicies.RegisterEvaluatedPolicyAsync(artifact, evaluation, PolicyOperator);
        var state = await peoplePolicies.ReadStateAsync();
        await peoplePolicies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.OwnerObservation.SubjectUuid,
                state.OwnerObservation.EditorialRevision,
                null,
                artifact.Uuid,
                "Enable isolated recording pipeline fixture"
            ),
            PolicyOperator
        );

        var venueArtifact = new VenueAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "recording-pipeline-venue-fixture-v1",
            "",
            VenueResolutionPolicyEvaluator.ProcessorVersion,
            VenueResolutionPolicyEvaluator.SourceContextVersion,
            NormalizationCandidateService.FactsVersion,
            VenueResolutionPolicyEvaluator.MatcherVersion,
            VenueResolutionPolicyEvaluator.FixtureRuleId,
            null,
            FixtureOnly: true
        );
        venueArtifact = venueArtifact with
        {
            ContentHash = VenueResolutionPolicyService.ComputeArtifactHash(venueArtifact),
        };
        var venueEvaluation = new VenueAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            venueArtifact.Uuid,
            venueArtifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-venue-evaluator",
            "fixture-venue-reviewer",
            PolicyOperator,
            new(100, 0),
            null,
            FixtureOnly: true
        );
        venueEvaluation = venueEvaluation with
        {
            ReceiptHash = VenueResolutionPolicyService.ComputeEvaluationHash(venueEvaluation),
        };
        await venuePolicies.RegisterEvaluatedPolicyAsync(
            venueArtifact,
            venueEvaluation,
            PolicyOperator
        );
        var venueState = await venuePolicies.ReadStateAsync();
        await venuePolicies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                venueState.OwnerObservation.SubjectUuid,
                venueState.OwnerObservation.EditorialRevision,
                null,
                venueArtifact.Uuid,
                "Enable isolated recording pipeline venue fixture"
            ),
            PolicyOperator
        );
    }
}
