using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>
/// Public, disposable-database examples of durable venue publication. Fixture literal-locality
/// matching and the explicit site marker do not establish a production geographic policy.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationVenueResolution
{
    private const string Operator = "venue-fixture-operator";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedVenueService venues = null!;
    private NormalizationReadService reads = null!;
    private VenueResolutionPolicyService policies = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        venues = new(fixture.Db, fixture.Work);
        reads = new(fixture.Db, Options.Create(new NormalizationReadOptions { Enabled = true }));
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
    }

    [TearDown]
    public async Task ResetPolicy()
    {
        if (await policies.ReadActiveAsync() != null)
        {
            await SetActivePolicy(null);
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RetainedProviderSupportIsDerivedAndItsOriginalGenerationsFencePublication()
    {
        var name = UniqueName();
        var imported = await Imported(name, "San Francisco, CA");
        var placeUuid = Guid.NewGuid();
        var input = await fixture.CaptureJson(
            JsonSerializer.Serialize(
                new
                {
                    name,
                    city = "San Francisco",
                    country = "US",
                    region = "CA",
                    address = "1805 Geary Boulevard",
                    place = placeUuid,
                }
            ),
            "venues",
            "/name"
        );
        var target = new CanonicalVenue(Guid.NewGuid(), name);
        await fixture.PublishVenues(
            venues,
            input,
            new(
                [target],
                [new(Guid.NewGuid(), target.Uuid, name, null, null, null, input.Reference)],
                [
                    new(
                        Guid.NewGuid(),
                        target.Uuid,
                        "San Francisco, CA",
                        "US",
                        "CA",
                        "San Francisco",
                        "1805 Geary Boulevard",
                        null,
                        null,
                        null,
                        null,
                        null,
                        input.Reference with
                        {
                            JsonPointer = "/city",
                        }
                    ),
                ],
                [
                    new(
                        target.Uuid,
                        "musicbrainz.org",
                        "place",
                        placeUuid.ToString("D"),
                        input.Reference with
                        {
                            JsonPointer = "/place",
                        }
                    ),
                ],
                [],
                []
            )
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var geographyResource = NormalizationVenueSupportIntake.GeographyResource(
            "San Francisco",
            "US",
            "CA"
        );
        var geographyPayload = JsonSerializer.Serialize(
            new
            {
                totalResultsCount = 1,
                geonames = new[]
                {
                    new
                    {
                        geonameId = 5391959,
                        name = "San Francisco",
                        toponymName = "San Francisco",
                        countryCode = "US",
                        adminCode1 = "CA",
                        fcl = "P",
                    },
                },
            }
        );
        var geography = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                geographyResource,
                Encoding.UTF8.GetBytes(geographyPayload),
                "application/json",
                NormalizationVenueSupportIntake.GeographyCaptureVersion,
                0,
                DateTimeOffset.UtcNow
            )
        );
        var place = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                new("musicbrainz.org", "place", placeUuid.ToString("D")),
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        new
                        {
                            id = placeUuid,
                            type = "Venue",
                            name,
                        }
                    )
                ),
                "application/json",
                NormalizationVenueSupportIntake.PlaceCaptureVersion,
                0,
                DateTimeOffset.UtcNow
            )
        );
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        VenueResolutionSupportReference[] support =
        [
            new(target.Uuid, geography.Revision.Uuid, place.Revision.Uuid),
        ];

        var prepared = await processor.ResolveImportedVenueAsync(attemptUuid, imported, support);

        var projection = prepared.Preparation!.Support!.Facts.Candidates.Single(value =>
            value.Match.Uuid == target.Uuid
        );
        projection.Match.ExternalIdentities.Single().IdentifiesEntity.Should().BeTrue();
        projection
            .Match.LocationAssertions!.Single()
            .Location.LocalityKey.Should()
            .Be("geonames:5391959");
        prepared
            .Preparation.ReadSet.Inputs.Should()
            .Contain(value =>
                value.RevisionUuid == geography.Revision.Uuid
                && value.Generation == geography.Fetch.ResourceGeneration
            );
        prepared
            .Preparation.ReadSet.Inputs.Should()
            .Contain(value =>
                value.RevisionUuid == place.Revision.Uuid
                && value.Generation == place.Fetch.ResourceGeneration
            );
        // Legacy imported rows have no structured city/address or supported physical ID. Supplying
        // another venue's evidence must not turn the source's matching name into an association.
        prepared
            .Preparation.Support.Facts.Source.Match.ExternalIdentities.Should()
            .NotContain(value => value.IdentifiesEntity);
        var evaluated = await processor.ResolveImportedVenueAsync(
            attemptUuid,
            imported,
            support.ToArray()
        );
        evaluated
            .Decision!.Facts.SupportedFacts.Should()
            .BeEquivalentTo(prepared.Preparation.Support.Facts);
        evaluated.Decision.Decision.Reason.Should().Be("acceptance_policy_inactive");

        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                geographyResource,
                Encoding.UTF8.GetBytes(geographyPayload),
                "application/json",
                NormalizationVenueSupportIntake.GeographyCaptureVersion,
                geography.Fetch.ResourceGeneration,
                DateTimeOffset.UtcNow
            )
        );
        var finished = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported, support)
        );

        finished.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale);
        finished.Preparation!.Support.Should().BeEquivalentTo(prepared.Preparation.Support);
        (await Assignments(imported)).Should().BeEmpty();
    }

    [Test]
    public async Task ArtistlessImportedClaimMatchesGlobalVenueAndRetainsItsOriginalEvidence()
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var first = await processor.ResolveImportedVenueAsync(attemptUuid, imported);

        first.Preparation.Should().NotBeNull();
        first.Result.Should().BeNull();
        first.Preparation!.Source.Claim!.ArtistUuid.Should().BeNull();
        first.Preparation.Source.Claim.Observation.Names.Single().Value.Should().Be(name);
        first.Preparation.Source.Claim.Observation.OriginalLocality!.Value.Should().Be("Town A");
        (await processor.GetAsync(attemptUuid))!
            .Preparation.Should()
            .BeEquivalentTo(first.Preparation);

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved
            .Stages.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(VenueResolutionStageKind.Assignment);
        resolved.Decision!.TargetVenueUuid.Should().Be(target.Uuid);
        var assignment = (await Assignments(imported)).Should().ContainSingle().Which;
        assignment.VenueUuid.Should().Be(target.Uuid);
        assignment.State.Should().Be("accepted");
        resolved
            .Stages.Single()
            .Publication.ImportedVenues.Single()
            .Evidence.Should()
            .Contain(first.Preparation.Source.Claim.Evidence);
        resolved.Stages.Single().ReadSet.Inputs.Should().Contain(first.Preparation.ReadSet.Inputs);
        resolved.Preparation.Should().BeEquivalentTo(first.Preparation);
        var accepted = await Assignments(imported);

        var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);

        replay.Result!.Replayed.Should().BeTrue();
        replay.Preparation.Should().BeEquivalentTo(first.Preparation);
        (await Assignments(imported)).Should().BeEquivalentTo(accepted);
    }

    [Test]
    public async Task SameFactsInANewAttemptSettleWithoutRevisingTheAcceptedAssignment()
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Finish(() => processor.ResolveImportedVenueAsync(firstUuid, imported));
        first.Result!.Disposition.Should().Be(VenueResolutionDisposition.Applied, Audit(first));
        var before = await Assignments(imported);
        var secondUuid = Guid.NewGuid();

        var second = await Finish(() => processor.ResolveImportedVenueAsync(secondUuid, imported));

        second.Result!.Disposition.Should().Be(VenueResolutionDisposition.Applied, Audit(second));
        (await Assignments(imported)).Should().BeEquivalentTo(before);
    }

    [Test]
    public async Task NoActivePolicyRetainsAKnownEmptyPoolWithoutCreatingASite()
    {
        var imported = await Imported(UniqueName(), "Town A");
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Unresolved, Audit(resolved));
        resolved.Preparation!.Context.Candidates.Complete.Should().BeTrue();
        resolved.Preparation.Context.Candidates.Facts.Should().BeEmpty();
        resolved.Preparation.Policy.Should().BeNull();
        var owner = await AssertAcknowledged(resolved);
        owner.Subject.Uuid.Should().Be(imported);
        (await Assignments(imported)).Should().BeEmpty();
        var beforeReplay = (await processor.GetAsync(attemptUuid))!;

        var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);

        replay
            .Result.Should()
            .BeEquivalentTo(
                resolved.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Stages.Should().BeEquivalentTo(beforeReplay.Stages);
        (await venues.PublishVenueResolutionAsync(resolved.Stages.Single().WorkUuid, attemptUuid))
            .Replayed.Should()
            .BeTrue();
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid)).Should().BeEquivalentTo(owner);
        (await Assignments(imported)).Should().BeEmpty();
        await ActivatePolicy();
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        (await processor.ResolveImportedVenueAsync(attemptUuid, imported))
            .Preparation.Should()
            .BeEquivalentTo(resolved.Preparation);
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task UnsupportedImportedNamesRetainFullLiteralPreparationAndSettleWithoutTruncation()
    {
        var processor = Processor();
        foreach (var name in new[] { "!!! " + new string('X', 201) + " 🎤?", "!!! 🎤???" })
        {
            var imported = await Imported(name, "Town A");
            var attemptUuid = Guid.NewGuid();

            var prepared = await processor.ResolveImportedVenueAsync(attemptUuid, imported);

            prepared.Result.Should().BeNull();
            var original = prepared.Preparation!.Source.Claim!.Observation.Names.Single();
            original.Value.Should().Be(name);
            var input = (
                await new NormalizationEvidenceService(fixture.Db).GetInputAsync(
                    original.Evidence.InputRevisionUuid
                )
            )!;
            using var retained = NormalizationRetainedDocument.Open(input);
            retained.Resolve(original.Evidence.JsonPointer).GetString().Should().Be(name);

            var unresolved = await Finish(() =>
                processor.ResolveImportedVenueAsync(attemptUuid, imported)
            );

            unresolved
                .Result!.Disposition.Should()
                .Be(VenueResolutionDisposition.Unresolved, Audit(unresolved));
            unresolved.Result.Reason.Should().Be("source_query_unsupported");
            unresolved.Preparation.Should().BeEquivalentTo(prepared.Preparation);
            unresolved.Decision.Should().BeNull();
            var owner = await AssertAcknowledged(unresolved);
            owner.Subject.Uuid.Should().Be(imported);
            unresolved
                .Stages.Single()
                .ReadSet.CandidateUniverses.Should()
                .Contain(universe => universe.Universe == NormalizationCandidateUniverseKey.Venues);
            (await Assignments(imported)).Should().BeEmpty();
            var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);
            replay
                .Result.Should()
                .BeEquivalentTo(
                    unresolved.Result with
                    {
                        Replayed = true,
                    },
                    options => options.WithStrictOrdering()
                );
        }
    }

    [Test]
    public async Task ExplicitFixtureSiteCreationUsesStableSeparateIdentityAndAssignmentStages()
    {
        var title = UniqueName();
        var imported = await Imported("[site] " + title, "Town A");
        await ActivatePolicy(creation: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var staged = await UntilAdmitted(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        var creation = staged.Stages.Single();
        creation
            .Kind.Should()
            .Be(
                VenueResolutionStageKind.CreateVenue,
                JsonSerializer.Serialize(staged.Decision?.Decision)
            );
        var venueUuid = creation.Publication.Venues.Single().Uuid;
        (await reads.GetVenueAsync(venueUuid)).Should().BeNull();

        var published = await venues.PublishVenueResolutionAsync(creation.WorkUuid, attemptUuid);

        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        var interrupted = (await processor.GetAsync(attemptUuid))!;
        interrupted.Stages.Single().Receipt.Should().NotBeNull();
        (await Assignments(imported)).Should().BeEmpty();
        var recovered = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        recovered
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(recovered));
        recovered
            .Stages.Select(stage => stage.Kind)
            .Should()
            .Equal(VenueResolutionStageKind.CreateVenue, VenueResolutionStageKind.Assignment);
        recovered.Stages.First().Publication.Venues.Single().Uuid.Should().Be(venueUuid);
        recovered.Stages.Should().OnlyContain(stage => stage.Receipt != null);
        var created = (await reads.GetVenueAsync(venueUuid))!;
        created.display_name.Should().Be(title);
        created.kind.Should().Be("site");
        created
            .locations.Should()
            .OnlyContain(location => location.city == null && location.country == null);
        (await Assignments(imported))
            .Should()
            .ContainSingle()
            .Which.VenueUuid.Should()
            .Be(venueUuid);
        var accepted = await Assignments(imported);
        var nextUuid = Guid.NewGuid();

        var settled = await Finish(() => processor.ResolveImportedVenueAsync(nextUuid, imported));

        settled.Result!.Disposition.Should().Be(VenueResolutionDisposition.Applied, Audit(settled));
        settled.Decision!.Decision.Action.Should().Be(VenueCorrespondenceAction.ProposeMatch);
        settled
            .Stages.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(VenueResolutionStageKind.Assignment);
        (await Assignments(imported)).Should().BeEquivalentTo(accepted);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OrdinaryUnknownKindNeverDefaultsToANewSite(bool allowCreation)
    {
        var name = UniqueName();
        var imported = await Imported(name, "Town A");
        await ActivatePolicy(creation: allowCreation);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(resolved));
        resolved.Decision!.Facts.Source.Kind.Should().BeEmpty();
        await AssertAcknowledged(resolved);
        (await venues.FindCandidatesAsync(name)).Should().BeEmpty();
    }

    [Test]
    public async Task ExplicitKindWithoutASeparateCreationCapabilityStillRequiresReview()
    {
        var imported = await Imported("[site] " + UniqueName(), "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(resolved));
        resolved.Decision!.Decision.Action.Should().Be(VenueCorrespondenceAction.NeedsReview);
        await AssertAcknowledged(resolved);
        (await Assignments(imported)).Should().BeEmpty();
    }

    [Test]
    public async Task SameNameInAnotherCityRemainsAnAlternativeWithoutDefeatingTheSupportedTarget()
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        var other = await Canonical(name, "Town B");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved
            .Decision!.Facts.Candidates.Select(candidate => candidate.Header.Uuid)
            .Should()
            .BeEquivalentTo(new[] { target.Uuid, other.Uuid });
        resolved
            .Decision.Decision.Candidates.Single(candidate => candidate.VenueUuid == other.Uuid)
            .Eligible.Should()
            .BeFalse();
        (await Assignments(imported)).Single().VenueUuid.Should().Be(target.Uuid);
    }

    [Test]
    public async Task TwoSupportedPhysicalTargetsRequireReview()
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(resolved));
        resolved
            .Decision!.Decision.Candidates.Should()
            .HaveCount(2)
            .And.OnlyContain(candidate => candidate.Eligible);
        var owner = await AssertAcknowledged(resolved);
        var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);
        replay
            .Result.Should()
            .BeEquivalentTo(
                resolved.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Stages.Should().BeEquivalentTo(resolved.Stages);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid)).Should().BeEquivalentTo(owner);
        (await Assignments(imported)).Should().BeEmpty();
    }

    [TestCase("candidate")]
    [TestCase("source_evidence")]
    [TestCase("canonical_metadata")]
    [TestCase("policy")]
    public async Task SettledAmbiguousDecisionBecomesPendingWhenItsOriginalFactsChange(
        string change
    )
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var negative = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        negative
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(negative));
        var owner = await AssertAcknowledged(negative);
        if (change == "candidate")
        {
            await Canonical(name, "Town A");
        }
        else if (change == "source_evidence")
        {
            await AdvanceInput(negative.Preparation!.ReadSet.Inputs.First());
        }
        else if (change == "canonical_metadata")
        {
            var alias = await fixture.Capture(name + " alias", "venues");
            await fixture.PublishVenues(
                venues,
                alias,
                new(
                    [],
                    [
                        new(
                            Guid.NewGuid(),
                            target.Uuid,
                            name + " alias",
                            null,
                            null,
                            null,
                            alias.Reference
                        ),
                    ],
                    [],
                    [],
                    [],
                    []
                )
            );
        }
        else
        {
            await SetActivePolicy(null);
        }

        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);

        replay
            .Result.Should()
            .BeEquivalentTo(
                negative.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Preparation.Should().BeEquivalentTo(negative.Preparation);
        replay.Stages.Should().BeEquivalentTo(negative.Stages);
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        (await Assignments(imported)).Should().BeEmpty();
    }

    [Test]
    public async Task HistoricalAcceptedVenueWithoutAnOwnerCannotBecomeASettledDecision()
    {
        var name = UniqueName();
        var venueUuid = Guid.NewGuid();
        var input = await fixture.CaptureJson(
            JsonSerializer.Serialize(new { name, locality = "Town A" }),
            "venues",
            "/name"
        );
        // Simulate historical accepted data predating the owner protocol. Positive fixture
        // setup uses normal writers; this deliberately corrupt row tests the unsafe boundary.
        await Execute(
            """
            INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind)
            VALUES(@venueUuid,@name,@nameKey,'site');
            INSERT INTO normalization.venue_names(uuid,venue_uuid,wording,name_key,input_revision_uuid,json_pointer)
            VALUES(@nameUuid,@venueUuid,@name,@nameKey,@revision,'/name');
            INSERT INTO normalization.venue_locations(uuid,venue_uuid,original_locality,input_revision_uuid,json_pointer)
            VALUES(@locationUuid,@venueUuid,'Town A',@revision,'/locality')
            """,
            new
            {
                venueUuid,
                name,
                nameKey = NormalizedPeopleService.NameKey(name),
                revision = input.Reference.InputRevisionUuid,
                nameUuid = Guid.NewGuid(),
                locationUuid = Guid.NewGuid(),
            }
        );
        try
        {
            var imported = await Imported(name, "Town A");
            var before = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT to_jsonb(v)::text FROM venues v WHERE uuid=@imported",
                    new { imported }
                )
            );
            await ActivatePolicy();
            var processor = Processor();
            var attemptUuid = Guid.NewGuid();

            var stale = await Finish(() =>
                processor.ResolveImportedVenueAsync(attemptUuid, imported)
            );

            stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale, Audit(stale));
            stale.Result.Reason.Should().Be("context_freshness_unavailable");
            stale
                .Preparation!.Context.Candidates.Issues.Should()
                .Contain(issue =>
                    issue.Kind == CandidateFactsIssueKind.MissingOwner
                    && issue.CandidateUuid == venueUuid
                );
            stale.Stages.Should().BeEmpty();
            stale.Decision.Should().BeNull();
            (await Assignments(imported)).Should().BeEmpty();
            var after = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT to_jsonb(v)::text FROM venues v WHERE uuid=@imported",
                    new { imported }
                )
            );
            after.Should().Be(before);
        }
        finally
        {
            // Do not leave the deliberately missing owner in another test's candidate pool.
            await Execute(
                """
                DELETE FROM normalization.venue_locations WHERE venue_uuid=@venueUuid;
                DELETE FROM normalization.venue_names WHERE venue_uuid=@venueUuid;
                DELETE FROM normalization.canonical_venues WHERE uuid=@venueUuid
                """,
                new { venueUuid }
            );
        }
    }

    [Test]
    public async Task UndatedLocalClaimCannotChooseTheFavorableHistoricalLocationAccount()
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        var historyInput = await fixture.Capture("Town B", "venues");
        await fixture.PublishVenues(
            venues,
            historyInput,
            new(
                [],
                [],
                [
                    Location(
                        target.Uuid,
                        "Town B",
                        historyInput.Reference,
                        new(1980, 1, 1),
                        new(1990, 1, 1)
                    ),
                ],
                [],
                [],
                []
            )
        );
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(resolved));
        resolved.Decision!.Facts.Source.ObservedDate.Should().BeNull();
        resolved
            .Decision.Decision.Candidates.Single(candidate => candidate.VenueUuid == target.Uuid)
            .Contradictions.Should()
            .Contain("alternative_raw_locality_account");
        await AssertAcknowledged(resolved);
        (await Assignments(imported)).Should().BeEmpty();
    }

    [TestCase("input")]
    [TestCase("imported")]
    [TestCase("candidate")]
    [TestCase("owner_editorial")]
    [TestCase("policy")]
    public async Task OriginalReadSetsRejectInterveningChangesBeforeAssignment(string change)
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var prepared = await UntilAdmitted(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        if (change == "input")
        {
            await AdvanceInput(prepared.Preparation!.ReadSet.Inputs.First());
        }
        else if (change == "imported")
        {
            await Execute(
                "UPDATE venues SET location='Town B' WHERE uuid=@imported",
                new { imported }
            );
        }
        else if (change == "candidate")
        {
            await Canonical(name, "Town A");
        }
        else if (change == "owner_editorial")
        {
            var owner = (
                await fixture.Work.GetSubjectAsync(prepared.Stages.Single().Ticket!.SubjectUuid)
            )!;
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(new { note = "parallel review" }),
                    "Intervening venue review",
                    Operator
                ),
                (_, _, _) => Task.CompletedTask
            );
        }
        else
        {
            await SetActivePolicy(null);
        }

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale, Audit(resolved));
        resolved.Stages.Should().OnlyContain(stage => stage.Receipt == null);
        resolved.Preparation.Should().BeEquivalentTo(prepared.Preparation);
        (await Assignments(imported)).Should().BeEmpty();
    }

    [Test]
    public async Task RetryReadsNewPolicyWhileOldTerminalReplayRemainsHistorical()
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var prepared = await UntilAdmitted(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        await SetActivePolicy(null);
        var stale = await Finish(() => processor.ResolveImportedVenueAsync(attemptUuid, imported));
        stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale);
        await ActivatePolicy();
        var nextUuid = Guid.NewGuid();

        var retry = await processor.RetryAsync(nextUuid, attemptUuid);
        retry.PreviousAttemptUuid.Should().Be(attemptUuid);
        var resolved = await Finish(() => processor.ResolveImportedVenueAsync(nextUuid, imported));

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved
            .Preparation!.Policy!.Policy.Artifact.Uuid.Should()
            .NotBe(prepared.Preparation!.Policy!.Policy.Artifact.Uuid);
        var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);
        replay
            .Result.Should()
            .BeEquivalentTo(
                stale.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Preparation.Should().BeEquivalentTo(prepared.Preparation);
    }

    [Test]
    public async Task RetryRequiresANewAttemptUuidEvenWhenTheOriginalRequestMatches()
    {
        var imported = await Imported(UniqueName(), "Town A");
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var original = await processor.ResolveImportedVenueAsync(attemptUuid, imported);

        await FluentActions
            .Invoking(() => processor.RetryAsync(attemptUuid, attemptUuid))
            .Should()
            .ThrowAsync<ArgumentException>();

        (await processor.GetAsync(attemptUuid)).Should().BeEquivalentTo(original);
    }

    [Test]
    public async Task RetryLineageIsImmutableWhileOrdinaryResolveCanResumeTheSavedRetry()
    {
        var imported = await Imported(UniqueName(), "Town A");
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var otherUuid = Guid.NewGuid();
        await processor.ResolveImportedVenueAsync(firstUuid, imported);
        await processor.ResolveImportedVenueAsync(otherUuid, imported);
        var retryUuid = Guid.NewGuid();
        var retry = await processor.RetryAsync(retryUuid, firstUuid);
        retry.PreviousAttemptUuid.Should().Be(firstUuid);

        await FluentActions
            .Invoking(() => processor.RetryAsync(retryUuid, otherUuid))
            .Should()
            .ThrowAsync<NormalizationConflictException>();

        (await processor.GetAsync(retryUuid)).Should().BeEquivalentTo(retry);
        var resumed = await processor.ResolveImportedVenueAsync(retryUuid, imported);
        resumed.PreviousAttemptUuid.Should().Be(firstUuid);
        resumed.Preparation.Should().BeEquivalentTo(retry.Preparation);
        resumed.Request.Should().Be(retry.Request);
    }

    [Test]
    public async Task RecoveryFindsUnfinishedAndLeafStaleAttemptsWhilePendingTerminalDecisionsUseTheirWorkOwner()
    {
        var processor = Processor();
        var store = new NormalizationVenueResolutionStore(fixture.Db);
        var unfinishedUuid = Guid.NewGuid();
        await processor.ResolveImportedVenueAsync(
            unfinishedUuid,
            await Imported(UniqueName(), "Town A")
        );
        var staleUuid = Guid.NewGuid();
        var unavailable = new VenueShowClaimReference(
            VenueShowClaimSourceKind.Recording,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/source_venue"
        );
        var stale = await Finish(() => processor.ResolveShowAsync(staleUuid, unavailable));
        stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale);
        stale.Stages.Should().BeEmpty();
        var parentUuid = Guid.NewGuid();
        await Finish(() => processor.ResolveShowAsync(parentUuid, unavailable));
        var childUuid = Guid.NewGuid();
        var child = await processor.RetryAsync(childUuid, parentUuid);
        child.PreviousAttemptUuid.Should().Be(parentUuid);
        child.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale);
        var unresolvedUuid = Guid.NewGuid();
        var unresolvedImported = await Imported(UniqueName(), "Town A");
        var unresolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(unresolvedUuid, unresolvedImported)
        );
        unresolved.Result!.Disposition.Should().Be(VenueResolutionDisposition.Unresolved);
        var unresolvedOwner = await AssertAcknowledged(unresolved);
        await ActivatePolicy();
        var reviewName = UniqueName();
        var reviewImported = await Imported(reviewName, "Town A");
        var reviewUuid = Guid.NewGuid();
        var review = await Finish(() =>
            processor.ResolveImportedVenueAsync(reviewUuid, reviewImported)
        );
        review.Result!.Disposition.Should().Be(VenueResolutionDisposition.Review);
        var reviewOwner = await AssertAcknowledged(review);
        await Canonical(reviewName, "Town A");
        (await Pending(unresolvedOwner.SubjectUuid)).Should().BeTrue();
        (await Pending(reviewOwner.SubjectUuid)).Should().BeTrue();

        var recovery = await ReadRecovery(store, 11);

        var ours = new HashSet<Guid>
        {
            unfinishedUuid,
            staleUuid,
            parentUuid,
            childUuid,
            unresolvedUuid,
            reviewUuid,
        };
        recovery
            .Where(attempt => ours.Contains(attempt.AttemptUuid))
            .Select(attempt => attempt.AttemptUuid)
            .Should()
            .BeEquivalentTo(new[] { unfinishedUuid, staleUuid, childUuid });
        (await store.GetForSubjectAsync(unresolvedOwner.SubjectUuid))!
            .AttemptUuid.Should()
            .Be(unresolvedUuid);
        (await store.GetForSubjectAsync(reviewOwner.SubjectUuid))!
            .AttemptUuid.Should()
            .Be(reviewUuid);
        (await processor.GetAsync(parentUuid))!
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Stale);
    }

    [Test]
    public async Task RecoveryPaginationUsesBoundedUuidOrderAndRejectsUnsupportedPageRequests()
    {
        var processor = Processor();
        var store = new NormalizationVenueResolutionStore(fixture.Db);
        var prefix = Guid.NewGuid().ToString("N")[..24];
        var ownUuids = Enumerable
            .Range(1, 3)
            .Select(index => Guid.ParseExact(prefix + index.ToString("x8"), "N"))
            .ToArray();
        foreach (var uuid in ownUuids)
        {
            await processor.ResolveImportedVenueAsync(uuid, await Imported(UniqueName(), "Town A"));
        }

        var recovery = await ReadRecovery(store, 1);

        recovery
            .Select(attempt => attempt.AttemptUuid)
            .Where(ownUuids.Contains)
            .Should()
            .Equal(ownUuids);
        await FluentActions
            .Invoking(() => store.GetRecoveryAttemptsAsync(0))
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions
            .Invoking(() => store.GetRecoveryAttemptsAsync(201))
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions
            .Invoking(() => store.GetRecoveryAttemptsAsync(afterUuid: Guid.Empty))
            .Should()
            .ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task RecoveryAppliesItsByteBoundAfterWaitingForACheckpointWriter()
    {
        var store = new NormalizationVenueResolutionStore(fixture.Db);
        var attemptUuid = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe");
        var cursor = Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffd");
        await store.GetOrCreateAsync(
            attemptUuid,
            new(VenueResolutionRequestKind.ImportedVenue, Guid.NewGuid()),
            NormalizationVenueResolutionProcessor.Version,
            null,
            CancellationToken.None
        );
        await using var writer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await writer.OpenAsync();
        await using var transaction = await writer.BeginTransactionAsync();
        await writer.ExecuteAsync(
            "SELECT attempt_uuid FROM normalization.venue_resolution_attempts WHERE attempt_uuid=@attemptUuid FOR UPDATE",
            new { attemptUuid },
            transaction
        );
        await using var observer = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await observer.OpenAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = store.GetRecoveryAttemptsAsync(1, cursor, cancellation.Token);
        var committed = false;

        try
        {
            var waiting = false;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!waiting && DateTime.UtcNow < deadline)
            {
                waiting = await observer.QuerySingleAsync<bool>(
                    """
                    SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                        WHERE datname=current_database() AND wait_event_type='Lock'
                        AND @blockerPid=ANY(pg_blocking_pids(pid))
                        AND query LIKE '%normalization.venue_resolution_attempts%')
                    """,
                    new { blockerPid = writer.ProcessID }
                );
                if (!waiting)
                {
                    await Task.Delay(10);
                }
            }
            waiting
                .Should()
                .BeTrue(
                    "recovery must wait for the checkpoint writer before it measures saved bytes"
                );

            // Keep the locked attempt row unchanged. A stage committed during the wait is
            // invisible to the waiting statement's snapshot, so its old byte count is unsafe.
            await writer.ExecuteAsync(
                "INSERT INTO normalization.venue_resolution_stages(work_uuid,attempt_uuid,ordinal,stage) VALUES(@workUuid,@attemptUuid,0,jsonb_build_object('padding',repeat('x',@bytes)))",
                new
                {
                    attemptUuid,
                    workUuid = Guid.NewGuid(),
                    bytes = NormalizationVenueResolutionStore.MaximumCheckpointBytes + 1,
                },
                transaction
            );
            await transaction.CommitAsync();
            committed = true;

            await FluentActions
                .Invoking(() => pending)
                .Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("Venue recovery page exceeds 32 MiB*");
        }
        finally
        {
            cancellation.Cancel();
            if (!committed)
            {
                await transaction.RollbackAsync();
            }
            var result = JsonSerializer.Serialize(
                new VenueResolutionResult(
                    attemptUuid,
                    VenueResolutionDisposition.Unresolved,
                    "oversized-checkpoint-fixture",
                    []
                )
            );
            // Keep the fixture's retained row out of later recovery scans in this disposable DB.
            await observer.ExecuteAsync(
                "UPDATE normalization.venue_resolution_attempts SET result=@result::jsonb WHERE attempt_uuid=@attemptUuid AND result IS NULL",
                new { attemptUuid, result }
            );
        }
    }

    [Test]
    public async Task AttemptCannotBeRepurposedAndPublicationRequiresItsSavedWorkAndProof()
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        var otherImported = await Imported(UniqueName(), "Town B");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var staged = await UntilAdmitted(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        var stage = staged.Stages.Single();

        await FluentActions
            .Invoking(() => processor.ResolveImportedVenueAsync(attemptUuid, otherImported))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        await FluentActions
            .Invoking(() => venues.PublishVenueResolutionAsync(Guid.NewGuid(), attemptUuid))
            .Should()
            .ThrowAsync<Exception>();
        await FluentActions
            .Invoking(() => venues.PublishVenueResolutionAsync(stage.WorkUuid, Guid.NewGuid()))
            .Should()
            .ThrowAsync<Exception>();
        (await processor.GetAsync(attemptUuid))!.Stages.Single().Receipt.Should().BeNull();
        (await Assignments(imported)).Should().BeEmpty();

        var publication = await venues.PublishVenueResolutionAsync(stage.WorkUuid, attemptUuid);
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await venues.PublishVenueResolutionAsync(stage.WorkUuid, attemptUuid))
            .Replayed.Should()
            .BeTrue();
        (await Assignments(imported)).Should().ContainSingle();
    }

    [Test]
    public async Task AcceptedVenueAndItsStageReceiptRollbackTogetherThenRecover()
    {
        var imported = await Imported("[site] " + UniqueName(), "Town A");
        await ActivatePolicy(creation: true);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var staged = await UntilAdmitted(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );
        var stage = staged.Stages.Single();
        var createdUuid = stage.Publication.Venues.Single().Uuid;
        var trigger = "venue_receipt_failure_" + Guid.NewGuid().ToString("N");
        await Execute(
            $"""
            CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.attempt_uuid='{attemptUuid:D}'::uuid THEN
                    RAISE EXCEPTION 'synthetic venue receipt failure';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE OF receipt ON normalization.venue_resolution_stages
                FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}();
            """
        );
        try
        {
            var failure = await FluentActions
                .Invoking(() => venues.PublishVenueResolutionAsync(stage.WorkUuid, attemptUuid))
                .Should()
                .ThrowAsync<Exception>();
            failure
                .Which.InnerException.Should()
                .BeOfType<PostgresException>()
                .Which.MessageText.Should()
                .Be("synthetic venue receipt failure");
            (await reads.GetVenueAsync(createdUuid)).Should().BeNull();
            (await processor.GetAsync(attemptUuid))!.Stages.Single().Receipt.Should().BeNull();
        }
        finally
        {
            await Execute(
                $"""
                DROP TRIGGER {trigger} ON normalization.venue_resolution_stages;
                DROP FUNCTION normalization.{trigger}();
                """
            );
        }

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved.Stages.First().Publication.Venues.Single().Uuid.Should().Be(createdUuid);
        (await reads.GetVenueAsync(createdUuid)).Should().NotBeNull();
        (await Assignments(imported)).Should().ContainSingle();
    }

    [Test]
    public async Task PolicyRegistrationRequiresAuthenticatedEvaluationAndExplicitFixtureHostOptIn()
    {
        var policy = Policy();
        await FluentActions
            .Invoking(() =>
                policies.RegisterEvaluatedPolicyAsync(
                    policy.Artifact,
                    policy.Evaluation,
                    "other-operator"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        var productionHost = new VenueResolutionPolicyService(fixture.Db, fixture.Work);
        await FluentActions
            .Invoking(() =>
                productionHost.RegisterEvaluatedPolicyAsync(
                    policy.Artifact,
                    policy.Evaluation,
                    Operator
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();

        var registered = await policies.RegisterEvaluatedPolicyAsync(
            policy.Artifact,
            policy.Evaluation,
            Operator
        );

        registered.Should().BeEquivalentTo(policy);
        (await policies.ReadActiveAsync()).Should().BeNull();
    }

    [Test]
    public async Task MissingImportedClaimSettlesItsAbsenceClockAndArrivalBecomesPending()
    {
        var imported = Guid.NewGuid();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var missing = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        missing.Result!.Disposition.Should().Be(VenueResolutionDisposition.Missing, Audit(missing));
        missing.Preparation!.Source.Claim.Should().BeNull();
        missing
            .Preparation.Source.Absence.Should()
            .Be(new VenueResolutionAbsenceProbe(imported, false, 0));
        var stage = missing.Stages.Should().ContainSingle().Which;
        stage.Kind.Should().Be(VenueResolutionStageKind.AcknowledgeMissing);
        stage.ReadSet.Inputs.Should().BeEmpty();
        stage
            .ReadSet.ImportedVenues.Should()
            .Contain(new NormalizationImportedVenueDependency(imported, 0));
        (await Pending(stage.Ticket!.SubjectUuid)).Should().BeFalse();

        await Imported(UniqueName(), "Town A", imported);

        (await Pending(stage.Ticket.SubjectUuid)).Should().BeTrue();
        var replay = await processor.ResolveImportedVenueAsync(attemptUuid, imported);
        replay
            .Result.Should()
            .BeEquivalentTo(
                missing.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Preparation.Should().BeEquivalentTo(missing.Preparation);
    }

    [Test]
    public async Task ImportedArrivalBeforeMissingAcknowledgmentInvalidatesTheOriginalAbsence()
    {
        var imported = Guid.NewGuid();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var staged = await UntilAdmitted(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        await Imported(UniqueName(), "Town A", imported);
        var stale = await Finish(() => processor.ResolveImportedVenueAsync(attemptUuid, imported));

        stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale, Audit(stale));
        stale.Stages.Single().Receipt.Should().BeNull();
        (await Assignments(imported)).Should().BeEmpty();
    }

    [Test]
    public async Task DeletedPreviouslyResolvedImportedClaimAcknowledgesItsNewAbsenceWithoutFictitiousEvidence()
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Finish(() => processor.ResolveImportedVenueAsync(firstUuid, imported));
        first.Result!.Disposition.Should().Be(VenueResolutionDisposition.Applied, Audit(first));
        var accepted = await Assignments(imported);
        await Execute("DELETE FROM venues WHERE uuid=@imported", new { imported });
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("venues", "imported_venue", imported)
            )
        )!;
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        var missingUuid = Guid.NewGuid();

        var missing = await Finish(() =>
            processor.ResolveImportedVenueAsync(missingUuid, imported)
        );

        missing.Result!.Disposition.Should().Be(VenueResolutionDisposition.Missing, Audit(missing));
        missing.Preparation!.Source.Claim.Should().BeNull();
        missing.Preparation.Source.Absence!.Exists.Should().BeFalse();
        missing.Preparation.Source.Absence.Generation.Should().BeGreaterThan(0);
        missing.Preparation.ReadSet.Inputs.Should().BeEmpty();
        var stage = missing.Stages.Should().ContainSingle().Which;
        stage.Kind.Should().Be(VenueResolutionStageKind.AcknowledgeMissing);
        stage.Publication.Should().BeEquivalentTo(new VenuePublication([], [], [], [], [], []));
        stage.Ticket!.Inputs.Should().BeEmpty();
        (await Assignments(imported)).Should().BeEquivalentTo(accepted);
        (await Pending(owner.SubjectUuid)).Should().BeFalse();

        await Imported(name, "Town A", imported);

        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        (await processor.ResolveImportedVenueAsync(firstUuid, imported))
            .Result!.Replayed.Should()
            .BeTrue();
        (await processor.GetAsync(firstUuid))!
            .Preparation.Should()
            .BeEquivalentTo(first.Preparation);
    }

    [Test]
    public async Task ReviewedTargetExclusionSurvivesAutomaticResolution()
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        var input = await fixture.Capture(name, "venues");
        var owner = await fixture.Own(input, "imported_venue", imported);
        var rejection = new VenueAssignment(
            Guid.NewGuid(),
            imported,
            target.Uuid,
            [input.Reference]
        );
        await venues.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.Ticket.SubjectUuid,
                0,
                0,
                "reject",
                false,
                rejection,
                "Independently reviewed different site"
            ),
            Operator
        );
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(resolved));
        resolved
            .Decision!.Decision.Candidates.Single(candidate => candidate.VenueUuid == target.Uuid)
            .Contradictions.Should()
            .Contain("reviewed_target_exclusion");
        await AssertAcknowledged(resolved);
        var assignment = (await Assignments(imported)).Should().ContainSingle().Which;
        assignment.Uuid.Should().Be(rejection.Uuid);
        assignment.Basis.Should().Be("reviewed");
        assignment.State.Should().Be("rejected");
        assignment.Revision.Should().Be(1);
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task PublishedSourceAuthenticatesAnIndividualShowWithoutResolvingItsImportedLabel(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        // The catalog label can be shared by several physical performances. Source-specific
        // evidence supplies this performance's venue, without assigning the imported label.
        var source = await ShowSource(sourceKind, name, "Town A", conflatedFallback: true);
        var playbackBefore = await Playback(source.Source);
        await ActivatePolicy();
        var processor = Processor();
        var importedAttemptUuid = Guid.NewGuid();
        var unresolvedImported = await Finish(() =>
            processor.ResolveImportedVenueAsync(importedAttemptUuid, source.ImportedUuid)
        );
        unresolvedImported
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(unresolvedImported));
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved
            .Stages.Should()
            .ContainSingle()
            .Which.Kind.Should()
            .Be(VenueResolutionStageKind.Assignment);
        resolved.Preparation!.Source.Claim!.CatalogUuid.Should().Be(source.Show.Uuid);
        resolved.Preparation.Source.Claim.IsShow.Should().BeTrue();
        resolved.Preparation.Source.ShowReference.Should().Be(source.Claim);
        resolved.Preparation.Source.OriginalTicket.Should().NotBeNull();
        var original = resolved.Preparation.Source.OriginalTicket!;
        var sourceReadSet = resolved.Preparation.ReadSet;
        original.Inputs.Except(sourceReadSet.Inputs).Should().BeEmpty();
        original.Dependencies.Except(sourceReadSet.Dependencies).Should().BeEmpty();
        (original.CatalogInputs ?? []).Except(sourceReadSet.CatalogInputs).Should().BeEmpty();
        (original.SourceIdentities ?? []).Except(sourceReadSet.SourceIdentities).Should().BeEmpty();
        (original.ImportedVenues ?? []).Except(sourceReadSet.ImportedVenues).Should().BeEmpty();
        (original.CandidateUniverses ?? [])
            .Except(sourceReadSet.CandidateUniverses)
            .Should()
            .BeEmpty();
        (original.OwnerObservations ?? [])
            .Except(sourceReadSet.OwnerObservations)
            .Should()
            .BeEmpty();
        (original.SourceUuids ?? []).Except(sourceReadSet.SourceUuids).Should().BeEmpty();
        sourceReadSet.MappingRegistry.Should().Be(original.MappingRegistry);
        resolved
            .Preparation.ReadSet.OwnerObservations.Should()
            .Contain(resolved.Preparation.Source.ProposalOwner!);
        (await Assignments(source.Show.Uuid, show: true))
            .Should()
            .ContainSingle()
            .Which.VenueUuid.Should()
            .Be(target.Uuid);
        (await Assignments(source.ImportedUuid)).Should().BeEmpty();
        (await Playback(source.Source)).Should().Be(playbackBefore);
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task PublishedSourceWithoutAPerformanceRemainsAnUnresolvedProposal(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var source = await ShowSource(sourceKind, name, "Town A", associateShow: false);
        var before = await Playback(source.Source);
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Unresolved, Audit(resolved));
        resolved.Preparation!.Source.Claim!.CatalogUuid.Should().BeNull();
        resolved.Preparation.Source.Claim.Observation.Names.Single().Value.Should().Be(name);
        var owner = await AssertAcknowledged(resolved, "observation");
        owner.Subject.Uuid.Should().NotBe(source.Show.Uuid);
        owner
            .Subject.Should()
            .Be(VenueResolutionOwner.For(resolved.Request, resolved.Preparation.Source));
        resolved
            .Preparation.ReadSet.Inputs.Should()
            .Contain(resolved.Preparation.Source.OriginalTicket!.Inputs);
        resolved
            .Stages.Single()
            .ReadSet.CandidateUniverses.Should()
            .Contain(universe => universe.Universe == NormalizationCandidateUniverseKey.Venues);
        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
        (await Playback(source.Source)).Should().Be(before);
    }

    [TestCase(VenueShowClaimSourceKind.Recording, "association")]
    [TestCase(VenueShowClaimSourceKind.Setlist, "association")]
    [TestCase(VenueShowClaimSourceKind.Recording, "proposal_owner")]
    [TestCase(VenueShowClaimSourceKind.Setlist, "proposal_owner")]
    public async Task UnboundSourceObservationBecomesPendingWhenItsMissingAssociationOrSourceOwnerChanges(
        VenueShowClaimSourceKind sourceKind,
        string change
    )
    {
        var source = await ShowSource(sourceKind, UniqueName(), "Town A", associateShow: false);
        var before = await Playback(source.Source);
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var negative = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));
        negative
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Unresolved, Audit(negative));
        var owner = await AssertAcknowledged(negative, "observation");
        owner.Subject.Uuid.Should().NotBe(source.Show.Uuid);
        var original = negative.Preparation!.Source.OriginalTicket!;
        var read = negative.Stages.Single().ReadSet;
        read.Inputs.Should().Contain(original.Inputs);
        (original.CatalogInputs ?? []).Except(read.CatalogInputs).Should().BeEmpty();
        (original.SourceIdentities ?? []).Except(read.SourceIdentities).Should().BeEmpty();
        (original.SourceUuids ?? []).Except(read.SourceUuids).Should().BeEmpty();
        read.CandidateUniverses.Should()
            .Contain(universe => universe.Universe == NormalizationCandidateUniverseKey.Venues);
        if (change == "association")
        {
            await AssociateShow(source, sourceKind);
        }
        else
        {
            var proposal = negative.Preparation.Source.ProposalOwner!;
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    proposal.SubjectUuid,
                    proposal.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(
                        new { note = "changed unbound source interpretation" }
                    ),
                    "Changed source owner after its negative venue acknowledgment",
                    Operator
                ),
                (_, _, _) => Task.CompletedTask
            );
            (await Playback(source.Source)).Should().Be(before);
        }

        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        var replay = await processor.ResolveShowAsync(attemptUuid, source.Claim);

        replay
            .Result.Should()
            .BeEquivalentTo(
                negative.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Preparation.Should().BeEquivalentTo(negative.Preparation);
        replay.Stages.Should().BeEquivalentTo(negative.Stages);
        (await Pending(owner.SubjectUuid)).Should().BeTrue();
        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task FreshBoundSourceResolutionSettlesItsPriorUnboundObservationWithoutRewritingTheOldProof(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        var source = await ShowSource(sourceKind, name, "Town A", associateShow: false);
        await ActivatePolicy();
        var processor = Processor();
        var negativeUuid = Guid.NewGuid();
        var negative = await Finish(() => processor.ResolveShowAsync(negativeUuid, source.Claim));
        negative
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Unresolved, Audit(negative));
        var observation = await AssertAcknowledged(negative, "observation");
        await AssociateShow(source, sourceKind);
        (await Pending(observation.SubjectUuid)).Should().BeTrue();

        var freshReference = await RepublishShowSource(source);

        freshReference.PublishedAttemptUuid.Should().NotBe(source.Claim.PublishedAttemptUuid);
        var boundUuid = Guid.NewGuid();
        var resolved = await Finish(() => processor.ResolveShowAsync(boundUuid, freshReference));
        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved.Preparation!.Source.ShowReference.Should().Be(freshReference);
        resolved.Preparation.Source.Claim!.CatalogUuid.Should().Be(source.Show.Uuid);
        resolved
            .Stages.Select(stage => stage.Kind)
            .Should()
            .Equal(
                VenueResolutionStageKind.Assignment,
                VenueResolutionStageKind.SupersedeObservation
            );
        var supersession = resolved.Stages.Last();
        supersession
            .Publication.Should()
            .BeEquivalentTo(new VenuePublication([], [], [], [], [], []));
        supersession.Ticket!.SubjectUuid.Should().Be(observation.SubjectUuid);
        supersession
            .DecisionReadSet.Inputs.Should()
            .Contain(resolved.Preparation.Source.OriginalTicket!.Inputs);
        supersession
            .DecisionReadSet.OwnerObservations.Should()
            .Contain(resolved.Preparation.Source.ProposalOwner!);
        supersession.Receipt!.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await Pending(observation.SubjectUuid)).Should().BeFalse();
        var retired = (
            await fixture.Work.GetInputContributionsAsync(observation.Subject)
        ).Contributions.Single(contribution =>
            contribution.Contributor.Provider == "relisten"
            && contribution.Contributor.ResourceType == "venue_resolution"
        );
        retired.Resources.Should().BeEmpty();
        retired.DependencySubjectUuids.Should().BeEmpty();
        retired.CatalogArtistUuids.Should().BeEmpty();
        retired.SourceIdentifiers.Should().BeEmpty();
        (retired.OwnerObservationSubjectUuids ?? []).Should().BeEmpty();
        (retired.CandidateUniverses ?? []).Should().BeEmpty();
        (retired.ImportedVenueUuids ?? []).Should().BeEmpty();
        (retired.SourceUuids ?? []).Should().BeEmpty();
        retired.UsesMappingRegistry.Should().BeFalse();
        (await Assignments(source.Show.Uuid, show: true))
            .Should()
            .ContainSingle()
            .Which.VenueUuid.Should()
            .Be(target.Uuid);
        (await Assignments(source.ImportedUuid)).Should().BeEmpty();
        var historical = (await processor.GetAsync(negativeUuid))!;
        historical.Should().BeEquivalentTo(negative);
        var replay = await processor.ResolveShowAsync(negativeUuid, source.Claim);
        replay
            .Result.Should()
            .BeEquivalentTo(
                negative.Result with
                {
                    Replayed = true,
                },
                options => options.WithStrictOrdering()
            );
        replay.Preparation.Should().BeEquivalentTo(negative.Preparation);
        (await Pending(observation.SubjectUuid)).Should().BeFalse();
        var boundOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("venues", "show", source.Show.Uuid)
            )
        )!;
        await Execute(
            "UPDATE sources SET description='Changed after the venue observation handoff' WHERE id=@SourceId",
            source.Source
        );
        (await Pending(boundOwner.SubjectUuid)).Should().BeTrue();
        (await Pending(observation.SubjectUuid)).Should().BeFalse();
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task AlreadyChangedSourceAssociationIsStaleWithoutAcknowledgingANegativeDecision(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var source = await ShowSource(sourceKind, name, "Town A");
        var sources = new NormalizationVenueSourceService(
            fixture.Db,
            new NormalizationEvidenceService(fixture.Db),
            new NormalizationProviderDecoder()
        );
        var originalSource = (await sources.PrepareShowClaimAsync(source.Claim))!;
        var replacement = await Show(source.Source, source.ImportedUuid);
        await MoveShowAssociation(source, sourceKind, replacement);
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var stale = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));

        stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale, Audit(stale));
        stale.Result.Reason.Should().Be("source_observation_changed");
        stale.Stages.Should().BeEmpty();
        (await sources.PrepareShowClaimAsync(source.Claim)).Should().BeEquivalentTo(originalSource);
        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
        (await Assignments(replacement.Uuid, show: true)).Should().BeEmpty();
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task ChangedPerformanceAssociationMakesTheOriginalShowProofStale(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var source = await ShowSource(sourceKind, name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var staged = await UntilAdmitted(() =>
            processor.ResolveShowAsync(attemptUuid, source.Claim)
        );
        var replacement = await Show(source.Source, source.ImportedUuid);
        await MoveShowAssociation(source, sourceKind, replacement);

        var stale = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));

        stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale, Audit(stale));
        stale.Preparation.Should().BeEquivalentTo(staged.Preparation);
        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
        (await Assignments(replacement.Uuid, show: true)).Should().BeEmpty();
    }

    [TestCase("attempt")]
    [TestCase("input")]
    [TestCase("pointer")]
    [TestCase("account")]
    public async Task CallerCannotSubstituteAPublishedSetlistAccountCoordinate(string changed)
    {
        var source = await ShowSource(VenueShowClaimSourceKind.Setlist, UniqueName(), "Town A");
        var reference = changed switch
        {
            "attempt" => source.Claim with { PublishedAttemptUuid = Guid.NewGuid() },
            "input" => source.Claim with { InputRevisionUuid = Guid.NewGuid() },
            "pointer" => source.Claim with { VenueJsonPointer = "/setlist/0/artist" },
            _ => source.Claim with { AccountIndex = 1 },
        };
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        if (changed == "attempt")
        {
            var unresolved = await Finish(() => processor.ResolveShowAsync(attemptUuid, reference));
            unresolved
                .Result!.Disposition.Should()
                .BeOneOf(VenueResolutionDisposition.Unresolved, VenueResolutionDisposition.Stale);
        }
        else
        {
            await FluentActions
                .Invoking(() => processor.ResolveShowAsync(attemptUuid, reference))
                .Should()
                .ThrowAsync<ArgumentException>();
        }
        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
        (await Assignments(source.ImportedUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task RecordingClaimCannotClaimASetlistAccountIndex()
    {
        var source = await ShowSource(VenueShowClaimSourceKind.Recording, UniqueName(), "Town A");

        await FluentActions
            .Invoking(() =>
                Processor().ResolveShowAsync(Guid.NewGuid(), source.Claim with { AccountIndex = 0 })
            )
            .Should()
            .ThrowAsync<ArgumentException>();

        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
    }

    [Test]
    public async Task AmbiguousRoomAndSiteStaySeparateWhileTheImportedClaimAwaitsReview()
    {
        var name = UniqueName();
        var site = await Canonical(name, "Town A");
        var room = await Canonical(name, "Town A", "room", site.Uuid);
        var imported = await Imported(name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Review, Audit(resolved));
        var facts = resolved.Decision!.Facts.Candidates;
        facts
            .Single(candidate => candidate.Header.Uuid == site.Uuid)
            .Header.Kind.Should()
            .Be("site");
        var roomFacts = facts.Single(candidate => candidate.Header.Uuid == room.Uuid);
        roomFacts.Header.Kind.Should().Be("room");
        roomFacts
            .Parents.Should()
            .ContainSingle()
            .Which.ResolvedParentVenueUuid.Should()
            .Be(site.Uuid);
        await AssertAcknowledged(resolved);
        (await reads.GetVenueAsync(room.Uuid))!.parent_venue_uuid.Should().Be(site.Uuid);
        (await Assignments(imported)).Should().BeEmpty();
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task ExactPublishedPerformanceDateFiltersHistoricalLocationAlternatives(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        var target = await Canonical(name, "Town A");
        var historical = await fixture.Capture("Town B", "venues");
        await fixture.PublishVenues(
            venues,
            historical,
            new(
                [],
                [],
                [
                    Location(
                        target.Uuid,
                        "Town B",
                        historical.Reference,
                        new(1980, 1, 1),
                        new(1990, 1, 1)
                    ),
                ],
                [],
                [],
                []
            )
        );
        var source = await ShowSource(sourceKind, name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        resolved.Decision!.Facts.Source.ObservedDate.Should().Be(new DateOnly(2026, 10, 17));
        resolved
            .Decision.Facts.Candidates.Single(candidate => candidate.Header.Uuid == target.Uuid)
            .Locations.Should()
            .HaveCount(2);
        resolved
            .Decision.Decision.Candidates.Single(candidate => candidate.VenueUuid == target.Uuid)
            .Contradictions.Should()
            .NotContain("alternative_raw_locality_account");
        (await Assignments(source.Show.Uuid, show: true))
            .Single()
            .VenueUuid.Should()
            .Be(target.Uuid);
    }

    [Test]
    public async Task ResolverPreservesUnmanagedBaselineAndPeerContributorWithoutClaimingTheirResources()
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var imported = await Imported(name, "Town A");
        var baseline = await fixture.Capture("Existing unrelated baseline", "venues");
        baseline = await fixture.Own(baseline, "imported_venue", imported);
        var subject = new NormalizationSubjectKey("venues", "imported_venue", imported);
        var peerInput = await fixture.Capture("Independent peer account", "venues");
        var peerKey = new NormalizationResourceKey(
            "test",
            "venue-peer",
            Guid.NewGuid().ToString("N")
        );
        var owner = (await fixture.Work.GetSubjectAsync(subject))!;
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            subject,
            "venue-peer-fixture-v1",
            new(peerKey, 0, [peerInput.Resource], []),
            owner.DesiredGeneration,
            expectedInputRevisions: [new(peerInput.Reference.InputRevisionUuid, peerInput.Resource)]
        );
        var before = await fixture.Work.GetInputContributionsAsync(subject);
        before
            .Contributions.SelectMany(contribution => contribution.Resources)
            .Should()
            .Contain([baseline.Resource, peerInput.Resource]);
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Finish(() =>
            processor.ResolveImportedVenueAsync(attemptUuid, imported)
        );

        resolved
            .Result!.Disposition.Should()
            .Be(VenueResolutionDisposition.Applied, Audit(resolved));
        var after = await fixture.Work.GetInputContributionsAsync(subject);
        foreach (var prior in before.Contributions)
        {
            // Contributed admission republishes each preserved peer at its next CAS revision;
            // its complete resource and dependency membership must remain unchanged.
            after
                .Contributions.Single(contribution => contribution.Contributor == prior.Contributor)
                .Should()
                .BeEquivalentTo(prior with { Revision = prior.Revision + 1 });
        }
        var resolver = after.Contributions.Single(contribution =>
            contribution.Contributor.Provider == "relisten"
            && contribution.Contributor.ResourceType == "venue_resolution"
        );
        resolver.Resources.Should().NotContain([baseline.Resource, peerInput.Resource]);
        var stage = resolved.Stages.Single();
        stage
            .Ticket!.Inputs.Should()
            .Contain(input => input.RevisionUuid == baseline.Reference.InputRevisionUuid);
        stage
            .Ticket.Inputs.Should()
            .Contain(input => input.RevisionUuid == peerInput.Reference.InputRevisionUuid);
    }

    [TestCase(VenueShowClaimSourceKind.Recording)]
    [TestCase(VenueShowClaimSourceKind.Setlist)]
    public async Task SourceProposalOwnerChangesInvalidateTheAdmittedShowAssignment(
        VenueShowClaimSourceKind sourceKind
    )
    {
        var name = UniqueName();
        await Canonical(name, "Town A");
        var source = await ShowSource(sourceKind, name, "Town A");
        await ActivatePolicy();
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var staged = await UntilAdmitted(() =>
            processor.ResolveShowAsync(attemptUuid, source.Claim)
        );
        var proposal = staged.Preparation!.Source.ProposalOwner!;
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                proposal.SubjectUuid,
                proposal.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(new { note = "parallel extraction review" }),
                "Changed interpretation of the source account",
                Operator
            ),
            (_, _, _) => Task.CompletedTask
        );

        var stale = await Finish(() => processor.ResolveShowAsync(attemptUuid, source.Claim));

        stale.Result!.Disposition.Should().Be(VenueResolutionDisposition.Stale, Audit(stale));
        stale.Preparation.Should().BeEquivalentTo(staged.Preparation);
        (await Assignments(source.Show.Uuid, show: true)).Should().BeEmpty();
    }

    private async Task<VenueResolutionAttempt> Finish(Func<Task<VenueResolutionAttempt>> advance)
    {
        for (var phase = 0; phase < 24; phase++)
        {
            var attempt = await advance();
            if (attempt.Result is { Disposition: not VenueResolutionDisposition.Pending })
            {
                return attempt;
            }
        }
        throw new AssertionException(
            "Venue resolution did not settle within its bounded durable phases."
        );
    }

    private async Task<NormalizationSubjectState> AssertAcknowledged(
        VenueResolutionAttempt attempt,
        string ownerKind = "imported_venue"
    )
    {
        var stage = attempt.Stages.Should().ContainSingle().Which;
        stage.Kind.Should().Be(VenueResolutionStageKind.AcknowledgeDecision);
        stage.Publication.Should().BeEquivalentTo(new VenuePublication([], [], [], [], [], []));
        stage.Receipt!.Status.Should().Be(NormalizationPublicationStatus.Published);
        var owner = (await fixture.Work.GetSubjectAsync(stage.Ticket!.SubjectUuid))!;
        owner.Subject.Domain.Should().Be("venues");
        owner.Subject.Kind.Should().Be(ownerKind);
        owner.PublishedWorkUuid.Should().Be(stage.WorkUuid);
        (await Pending(owner.SubjectUuid)).Should().BeFalse();
        return owner;
    }

    private static async Task<IReadOnlyList<VenueResolutionAttempt>> ReadRecovery(
        NormalizationVenueResolutionStore store,
        int limit
    )
    {
        var attempts = new List<VenueResolutionAttempt>();
        Guid? cursor = null;
        for (var pageIndex = 0; pageIndex < 1000; pageIndex++)
        {
            var page = await store.GetRecoveryAttemptsAsync(limit, cursor);
            page.Items.Count.Should().BeLessThanOrEqualTo(limit);
            page.Items.Should()
                .OnlyContain(attempt =>
                    attempt.Result == null
                    || attempt.Result.Disposition == VenueResolutionDisposition.Stale
                );
            attempts.AddRange(page.Items);
            if (page.NextCursor == null)
            {
                var uuids = attempts.Select(attempt => attempt.AttemptUuid).ToArray();
                uuids.Should().OnlyHaveUniqueItems();
                uuids
                    .Should()
                    .Equal(uuids.OrderBy(uuid => uuid.ToString("D"), StringComparer.Ordinal));
                return attempts;
            }
            page.Items.Should().NotBeEmpty();
            page.NextCursor.Should().Be(page.Items[^1].AttemptUuid);
            if (cursor != null)
            {
                string.CompareOrdinal(
                        page.NextCursor.Value.ToString("D"),
                        cursor.Value.ToString("D")
                    )
                    .Should()
                    .BeGreaterThan(0);
            }
            cursor = page.NextCursor;
        }
        throw new AssertionException("Venue recovery pagination did not terminate.");
    }

    private async Task<VenueResolutionAttempt> UntilAdmitted(
        Func<Task<VenueResolutionAttempt>> advance
    )
    {
        for (var phase = 0; phase < 24; phase++)
        {
            var attempt = await advance();
            if (attempt.Stages.Any(stage => stage.Ticket != null && stage.Receipt == null))
            {
                return attempt;
            }
            if (attempt.Result != null)
            {
                attempt
                    .Result.Disposition.Should()
                    .Be(VenueResolutionDisposition.Pending, Audit(attempt));
            }
        }
        throw new AssertionException(
            "Venue resolution did not retain an admitted publication stage."
        );
    }

    private async Task<CanonicalVenue> Canonical(
        string name,
        string locality,
        string kind = "site",
        Guid? parentUuid = null
    )
    {
        var input = await fixture.CaptureJson(
            JsonSerializer.Serialize(new { name, locality }),
            "venues",
            "/name"
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), name, kind, parentUuid);
        await fixture.PublishVenues(
            venues,
            input,
            new(
                [venue],
                [],
                [
                    Location(
                        venue.Uuid,
                        locality,
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
        return venue;
    }

    private async Task<ShowFixture> ShowSource(
        VenueShowClaimSourceKind kind,
        string name,
        string locality,
        bool associateShow = true,
        bool conflatedFallback = false
    )
    {
        var source = await fixture.Source();
        var imported = await Imported(
            conflatedFallback ? "Conflated catalog label " + Guid.NewGuid().ToString("N") : name,
            conflatedFallback ? "Several physical sites" : locality
        );
        await Execute(
            "UPDATE venues SET artist_id=@ArtistId WHERE uuid=@imported",
            new { source.ArtistId, imported }
        );
        var show = await Show(source, imported);
        var sourceVenue = imported;
        if (conflatedFallback && kind == VenueShowClaimSourceKind.Recording)
        {
            sourceVenue = await Imported(name, locality);
            await Execute(
                "UPDATE venues SET artist_id=@ArtistId WHERE uuid=@sourceVenue",
                new { source.ArtistId, sourceVenue }
            );
        }
        var venueId = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT id FROM venues WHERE uuid=@sourceVenue",
                new { sourceVenue }
            )
        );
        await Execute(
            "UPDATE sources SET venue_id=@venueId WHERE id=@SourceId",
            new { venueId, source.SourceId }
        );
        if (associateShow)
        {
            await Execute(
                """
                UPDATE sources SET show_id=@Id WHERE id=@SourceId;
                INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
                VALUES(@SourceId,@Id,'accepted','2026-10-17','{}')
                """,
                new { show.Id, source.SourceId }
            );
        }
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var decoder = new NormalizationProviderDecoder();
        var attemptUuid = Guid.NewGuid();
        if (kind == VenueShowClaimSourceKind.Recording)
        {
            var proposals = new NormalizationRecordingProposalService(fixture.Db);
            var processor = new NormalizationRecordingProcessor(
                new(fixture.Db, evidence),
                evidence,
                new(fixture.Db),
                decoder,
                new(new(), new()),
                fixture.Work,
                proposals,
                new(fixture.Db)
            );
            var published = await processor.ProcessAsync(attemptUuid, source.SourceUuid);
            published
                .Disposition.Should()
                .Be(NormalizationRecordingProcessingDisposition.Published);
            var batch = (await proposals.GetAttemptBatchAsync(attemptUuid))!;
            var revisionUuid = batch.Preparation.Binding.InputRevisionUuid;
            var input = (await evidence.GetInputAsync(revisionUuid))!;
            var venue = decoder.Decode(input).Recordings.Single().Venue!;
            return new(
                source,
                imported,
                show,
                new(kind, attemptUuid, revisionUuid, venue.Origin.Evidence.JsonPointer),
                null
            );
        }

        var artistKey = Guid.NewGuid().ToString("D");
        await fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name='setlist.fm' ORDER BY id LIMIT 1"
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),'setlist.fm','https://provider.invalid') RETURNING id"
                );
            await c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId; INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@ArtistId,@provider,@artistKey)",
                new
                {
                    artistKey,
                    source.ArtistId,
                    provider,
                }
            );
        });
        var accountKey = "venue-fixture-" + Guid.NewGuid().ToString("N");
        var setlistId = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                """
                INSERT INTO setlist_shows(artist_id,venue_id,date,upstream_identifier,uuid,updated_at)
                VALUES(@ArtistId,@venueId,'2026-10-17',@accountKey,@uuid,now()) RETURNING id
                """,
                new
                {
                    source.ArtistId,
                    venueId,
                    accountKey,
                    uuid = Guid.NewGuid(),
                }
            )
        );
        if (associateShow)
        {
            await Execute(
                "INSERT INTO setlist_show_assignments(setlist_show_id,show_id,evidence) VALUES(@setlistId,@Id,'{}')",
                new { setlistId, show.Id }
            );
        }
        var resource = new NormalizationResourceKey(
            "setlist.fm",
            "setlist-page",
            artistKey + ":page=1"
        );
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        page = 1,
                        itemsPerPage = 20,
                        total = 1,
                        setlist = new[]
                        {
                            new
                            {
                                id = accountKey,
                                eventDate = "17-10-2026",
                                artist = new { mbid = artistKey, name = "Fixture performer" },
                                venue = new
                                {
                                    id = "venue-" + imported,
                                    name,
                                    locale = locality,
                                },
                                sets = new
                                {
                                    set = new[] { new { song = new[] { new { name = "Song" } } } },
                                },
                            },
                        },
                    }
                ),
                "application/json",
                "setlist-fm-http-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var setlists = new NormalizationSetlistProcessor(
            new(fixture.Db),
            evidence,
            new(fixture.Db),
            decoder,
            new(),
            fixture.Work
        );
        var extraction = await setlists.ProcessAsync(
            attemptUuid,
            new(capture.Revision.Uuid, resource),
            new("setlist.fm", "setlist", accountKey)
        );
        extraction
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var observed = decoder.Decode(capture.Revision).Setlists.Single().Venue!;
        return new(
            source,
            imported,
            show,
            new(kind, attemptUuid, capture.Revision.Uuid, observed.Origin.Evidence.JsonPointer, 0),
            setlistId
        );
    }

    private Task<CatalogShow> Show(CatalogSource source, Guid imported) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CatalogShow>(
                """
                INSERT INTO shows(artist_id,venue_id,date,display_date,performance_key,performance_type,is_legacy_survivor,uuid,updated_at)
                VALUES(@ArtistId,(SELECT id FROM venues WHERE uuid=@imported),'2026-10-17','2026-10-17',@key,@performanceType,false,@uuid,now())
                RETURNING id AS Id,uuid AS Uuid
                """,
                new
                {
                    source.ArtistId,
                    imported,
                    key = "venue-fixture-" + Guid.NewGuid(),
                    performanceType = PerformanceType.Unknown.ToString().ToLowerInvariant(),
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private async Task<VenueShowClaimReference> RepublishShowSource(ShowFixture source)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var decoder = new NormalizationProviderDecoder();
        var newAttemptUuid = Guid.NewGuid();
        if (source.Claim.SourceKind == VenueShowClaimSourceKind.Recording)
        {
            var proposals = new NormalizationRecordingProposalService(fixture.Db);
            var recordings = new NormalizationRecordingProcessor(
                new(fixture.Db, evidence),
                evidence,
                new(fixture.Db),
                decoder,
                new(new(), new()),
                fixture.Work,
                proposals,
                new(fixture.Db)
            );
            var result = await recordings.ProcessAsync(newAttemptUuid, source.Source.SourceUuid);
            result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
            var batch = (await proposals.GetAttemptBatchAsync(newAttemptUuid))!;
            var revisionUuid = batch.Preparation.Binding.InputRevisionUuid;
            var retained = (await evidence.GetInputAsync(revisionUuid))!;
            var venue = decoder.Decode(retained).Recordings.Single().Venue!;
            return new(
                VenueShowClaimSourceKind.Recording,
                newAttemptUuid,
                revisionUuid,
                venue.Origin.Evidence.JsonPointer
            );
        }

        var store = new NormalizationSetlistProposalService(fixture.Db);
        var previous = (await store.GetAttemptAsync(source.Claim.PublishedAttemptUuid))!;
        var setlists = new NormalizationSetlistProcessor(
            store,
            evidence,
            new(fixture.Db),
            decoder,
            new(),
            fixture.Work
        );
        var published = await setlists.RetryAsync(
            newAttemptUuid,
            previous.Registration.RegistrationUuid
        );
        published
            .Result!.Disposition.Should()
            .Be(NormalizationSetlistProcessingDisposition.Published);
        var input = published.Preparation!.Inputs.Single().Input;
        var account = published.Preparation.Inputs.Single().Accounts.Single().Account;
        return new(
            VenueShowClaimSourceKind.Setlist,
            newAttemptUuid,
            input.InputRevisionUuid,
            account.Venue!.Origin.Evidence.JsonPointer,
            0
        );
    }

    private Task AssociateShow(ShowFixture source, VenueShowClaimSourceKind sourceKind) =>
        sourceKind == VenueShowClaimSourceKind.Recording
            ? Execute(
                """
                UPDATE sources SET show_id=@Id WHERE id=@SourceId;
                INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
                VALUES(@SourceId,@Id,'accepted','2026-10-17','{}')
                """,
                new { source.Show.Id, source.Source.SourceId }
            )
            : Execute(
                "INSERT INTO setlist_show_assignments(setlist_show_id,show_id,evidence) VALUES(@setlistId,@Id,'{}')",
                new { setlistId = source.SetlistId!.Value, source.Show.Id }
            );

    private Task MoveShowAssociation(
        ShowFixture source,
        VenueShowClaimSourceKind sourceKind,
        CatalogShow replacement
    ) =>
        sourceKind == VenueShowClaimSourceKind.Recording
            ? Execute(
                "UPDATE sources SET show_id=@Id WHERE id=@SourceId; UPDATE source_show_assignments SET show_id=@Id WHERE source_id=@SourceId",
                new { replacement.Id, source.Source.SourceId }
            )
            : Execute(
                "UPDATE setlist_show_assignments SET show_id=@Id WHERE setlist_show_id=@setlistId",
                new { replacement.Id, setlistId = source.SetlistId!.Value }
            );

    private Task<string> Playback(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object('source',to_jsonb(s),'tracks',
                    (SELECT jsonb_agg(to_jsonb(t) ORDER BY t.uuid) FROM source_tracks t WHERE t.source_id=s.id))::text
                FROM sources s WHERE s.id=@SourceId
                """,
                source
            )
        );

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? cursor = null;
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await fixture.Work.GetPendingSubjectsAsync(cursor, 1000);
            if (page.Any(subject => subject.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (page.Count < 1000)
            {
                return false;
            }
            cursor = page[^1].SubjectUuid;
        }
        throw new AssertionException("Pending venue work pagination did not terminate.");
    }

    private static VenueLocationAssertion Location(
        Guid venueUuid,
        string locality,
        NormalizedEvidenceReference evidence,
        DateOnly? from = null,
        DateOnly? to = null
    ) =>
        new(
            Guid.NewGuid(),
            venueUuid,
            locality,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            from,
            to,
            evidence
        );

    private Task<Guid> Imported(string name, string locality, Guid? uuid = null) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                """
                INSERT INTO venues(uuid,name,location,slug,upstream_identifier,created_at,updated_at)
                VALUES(@uuid,@name,@locality,@slug,@slug,now(),now()) RETURNING uuid
                """,
                new
                {
                    uuid = uuid ?? Guid.NewGuid(),
                    name,
                    locality,
                    slug = Guid.NewGuid().ToString("N"),
                }
            )
        );

    private async Task<AssignmentRow[]> Assignments(Guid catalogUuid, bool show = false) =>
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QueryAsync<AssignmentRow>(
                    $"SELECT uuid AS Uuid,canonical_venue_uuid AS VenueUuid,state AS State,basis AS Basis,revision AS Revision FROM normalization.{(show ? "show_venue_assignments" : "imported_venue_assignments")} WHERE {(show ? "show_uuid" : "imported_venue_uuid")}=@catalogUuid ORDER BY uuid",
                    new { catalogUuid }
                )
            )
        ).ToArray();

    private async Task AdvanceInput(NormalizationInputDependency dependency)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var input = (await evidence.GetInputAsync(dependency.RevisionUuid))!;
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                input.Resource,
                Encoding.UTF8.GetBytes("{\"changed\":true}"),
                "application/json",
                input.CaptureVersion,
                dependency.Generation,
                DateTimeOffset.UtcNow
            )
        );
    }

    private NormalizationVenueResolutionProcessor Processor()
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var decoder = new NormalizationProviderDecoder();
        return new(
            fixture.Db,
            new(fixture.Db),
            new(fixture.Db, evidence),
            new(fixture.Db),
            evidence,
            decoder,
            policies,
            new(fixture.Db),
            fixture.Work,
            venues,
            new(fixture.Db, evidence, decoder)
        );
    }

    private async Task ActivatePolicy(bool creation = false)
    {
        var policy = Policy(creation);
        await policies.RegisterEvaluatedPolicyAsync(policy.Artifact, policy.Evaluation, Operator);
        await SetActivePolicy(policy.Artifact.Uuid);
    }

    private async Task SetActivePolicy(Guid? artifactUuid)
    {
        var active = await policies.ReadActiveAsync();
        var owner = await fixture.Work.GetSubjectAsync(VenueResolutionPolicyService.Owner);
        if (owner == null)
        {
            await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                VenueResolutionPolicyService.Owner,
                VenueResolutionPolicyService.RegistryVersion,
                [],
                []
            );
            owner = (await fixture.Work.GetSubjectAsync(VenueResolutionPolicyService.Owner))!;
        }
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                active?.Policy.Artifact.Uuid,
                artifactUuid,
                "Isolated venue correspondence fixture"
            ),
            Operator
        );
    }

    private static RegisteredVenueAcceptancePolicy Policy(bool creation = false)
    {
        var artifact = new VenueAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "venue-fixture-v1",
            "",
            VenueResolutionPolicyEvaluator.ProcessorVersion,
            VenueResolutionPolicyEvaluator.SourceContextVersion,
            NormalizationCandidateService.FactsVersion,
            VenueResolutionPolicyEvaluator.MatcherVersion,
            VenueResolutionPolicyEvaluator.FixtureRuleId,
            creation ? VenueResolutionPolicyEvaluator.FixtureCreationRuleId : null,
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = VenueResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new VenueAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-venue-labels-v1",
            "independent-fixture-adjudicator",
            Operator,
            new(100, 0),
            creation ? new(100, 0) : null,
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = VenueResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        return new(artifact, evaluation);
    }

    private Task Execute(string sql, object? parameters = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, parameters));

    // A shared word such as "Hall" is itself a retrieval seed and contaminates later pools.
    private static string UniqueName() => "V" + Guid.NewGuid().ToString("N");

    private static string Audit(VenueResolutionAttempt attempt) =>
        JsonSerializer.Serialize(attempt.Result);

    private sealed record AssignmentRow(
        Guid Uuid,
        Guid VenueUuid,
        string State,
        string Basis,
        long Revision
    );

    private sealed record CatalogShow(long Id, Guid Uuid);

    private sealed record ShowFixture(
        CatalogSource Source,
        Guid ImportedUuid,
        CatalogShow Show,
        VenueShowClaimReference Claim,
        long? SetlistId
    );
}
