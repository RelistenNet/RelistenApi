using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizedMetadata
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizedVenueService venues = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        people = new NormalizedPeopleService(fixture.Db, fixture.Work);
        venues = new NormalizedVenueService(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task AliasRetractionSurvivesAlternateUuidAndChangedEvidenceIsOnlyPending()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; TapeWizard-{caseId}", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            person.Uuid,
            $"TapeWizard-{caseId}",
            input.Reference
        );
        await fixture.PublishPeople(
            people,
            input,
            new(
                [person],
                [alias, alias with { Uuid = Guid.NewGuid(), Basis = "validated_match" }],
                [],
                []
            )
        );
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        var command = new PersonMetadataDecision(
            Guid.NewGuid(),
            owner,
            0,
            person.Uuid,
            1,
            "retract",
            "Incorrect alias",
            Alias: alias,
            ExpectedAssertionRevision: 1
        );
        var result = await people.ApplyMetadataDecisionAsync(command, "operator");
        (await people.ApplyMetadataDecisionAsync(command, "operator")).Replayed.Should().BeTrue();
        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );
        (await people.FindCandidatesAsync($"TapeWizard-{caseId}")).Should().BeEmpty();
        var changed = await fixture.Capture($"Pat; TapeWizard-{caseId}; revised source", "people");
        await fixture.PublishPeople(
            people,
            changed,
            new([], [alias with { Evidence = changed.Reference }], [], [])
        );
        await fixture.PublishPeople(
            people,
            changed,
            new([], [alias with { Evidence = changed.Reference }], [], [])
        );
        (await people.FindCandidatesAsync($"TapeWizard-{caseId}")).Should().BeEmpty();
        (await Count("person_aliases", "person_uuid=@uuid AND state='pending'", person.Uuid))
            .Should()
            .Be(1);
        (await Count("person_aliases_history", "person_uuid=@uuid", person.Uuid)).Should().Be(2);
        result.Replayed.Should().BeFalse();
    }

    [Test]
    public async Task ReplacementPreservesOldClaimExclusionAndStaleReviewRollsBack()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; Wrong-{caseId}; Right-{caseId}", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            person.Uuid,
            $"Wrong-{caseId}",
            input.Reference
        );
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "replace",
                "Use explicit alias",
                Alias: alias with
                {
                    Wording = $"Right-{caseId}",
                },
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );
        (await people.FindCandidatesAsync($"Wrong-{caseId}")).Should().BeEmpty();
        (await people.FindCandidatesAsync($"Right-{caseId}"))
            .Should()
            .ContainSingle(p => p.Uuid == person.Uuid);
        var stale = () =>
            people.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner,
                    1,
                    person.Uuid,
                    1,
                    "retract",
                    "Stale review",
                    Alias: alias,
                    ExpectedAssertionRevision: 2
                ),
                "operator"
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        (await Count("person_aliases_history", "person_uuid=@uuid", person.Uuid)).Should().Be(1);
    }

    [Test]
    public async Task ReviewedDisplayCorrectionSurvivesProviderReplayAndFencesOldRun()
    {
        var input = await fixture.Capture("Pat; Patrick", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        var run = await fixture.Own(input, "person", person.Uuid);
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "replace",
                "Preferred credited name",
                Identity: person with
                {
                    DisplayName = "Patrick",
                },
                IdentityEvidence: input.Reference
            ),
            "operator"
        );
        (await people.PublishAsync(run.Ticket.WorkUuid, new([person], [], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        (await people.FindCandidatesAsync("Patrick"))
            .Should()
            .ContainSingle(p => p.Uuid == person.Uuid && p.DisplayName == "Patrick");
        (await Count("people_metadata_history", "uuid=@uuid", person.Uuid)).Should().Be(1);
    }

    [Test]
    public async Task RetractedExternalIdentityCanBeReplacedWithoutAutomaticResurrection()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; ID-17-{caseId}; ID-18-{caseId}", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var external = new PersonExternalIdentity(
            person.Uuid,
            "provider",
            "person",
            $"ID-17-{caseId}",
            input.Reference,
            Guid.NewGuid()
        );
        await fixture.PublishPeople(people, input, new([person], [], [external], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "replace",
                "Correct provider identifier",
                ExternalIdentity: external with
                {
                    ProviderKey = $"ID-18-{caseId}",
                },
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        await fixture.PublishPeople(
            people,
            input,
            new([], [], [external with { Uuid = Guid.NewGuid() }], [])
        );
        var keys = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                "SELECT provider_key FROM normalization.resolved_person_external_identities WHERE resolved_person_uuid=@uuid",
                new { uuid = person.Uuid }
            )
        );
        keys.Should().Equal($"ID-18-{caseId}");
    }

    [Test]
    public async Task VenueHistoryRetractionAndStructuredLocationReviewKeepEvidence()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Hall; Former Hall-{caseId}", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var name = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            $"Former Hall-{caseId}",
            null,
            null,
            null,
            input.Reference
        );
        await fixture.PublishVenues(
            venues,
            input,
            new([venue], [name, name with { Uuid = Guid.NewGuid() }], [], [], [], [])
        );
        var owner = await fixture.OwnerUuid("venues", "venue", venue.Uuid);
        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                venue.Uuid,
                1,
                "retract",
                "Unrelated historical name",
                Name: name,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        await fixture.PublishVenues(
            venues,
            input,
            new([], [name with { Uuid = Guid.NewGuid() }], [], [], [], [])
        );
        (await venues.FindCandidatesAsync($"Former Hall-{caseId}")).Should().BeEmpty();
        var locationInput = await fixture.CaptureJson(
            """{"location":{"original_locality":"New York, NY","city":"New York","country":"US"}}""",
            "venues",
            "/location"
        );
        await fixture.Own(locationInput, "venue", venue.Uuid);
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "New York, NY",
            "US",
            null,
            "New York",
            null,
            null,
            null,
            null,
            null,
            null,
            locationInput.Reference
        );
        var revision = await Revision("canonical_venues", venue.Uuid);
        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                1,
                venue.Uuid,
                revision,
                "accept",
                "Structured address",
                Location: location
            ),
            "operator"
        );
        (await Count("resolved_venue_locations", "resolved_venue_uuid=@uuid", venue.Uuid))
            .Should()
            .Be(1);
        var fabricated = location with { Uuid = Guid.NewGuid(), City = "Chicago" };
        var invalid = () =>
            venues.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner,
                    2,
                    venue.Uuid,
                    revision + 1,
                    "accept",
                    "Bad proposed city",
                    Location: fabricated
                ),
                "operator"
            );
        await invalid.Should().ThrowAsync<ArgumentException>();
        (await Count("resolved_venue_locations", "resolved_venue_uuid=@uuid", venue.Uuid))
            .Should()
            .Be(1);
    }

    [Test]
    public async Task RetiredAliasCorrectionUsesOriginalOwnerAndFencesSurvivorConsumer()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; Patrick; TapeWizard-{caseId}", "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Patrick");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            original.Uuid,
            $"TapeWizard-{caseId}",
            input.Reference
        );
        await fixture.PublishPeople(people, input, new([original, survivor], [alias], [], []));
        var originalOwner = await fixture.OwnerUuid("people", "person", original.Uuid);
        var survivorOwner = await fixture.OwnerUuid("people", "person", survivor.Uuid);
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                originalOwner,
                0,
                "merge",
                original.Uuid,
                1,
                survivor,
                1,
                [],
                "Same credited person"
            ),
            "operator"
        );
        var dependent = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", Guid.NewGuid()),
            "test",
            [],
            [survivorOwner]
        );
        (await people.FindCandidatesAsync($"TapeWizard-{caseId}"))
            .Should()
            .ContainSingle(p => p.Uuid == survivor.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                originalOwner,
                1,
                original.Uuid,
                2,
                "retract",
                "Unrelated nickname",
                Alias: alias,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        (await people.FindCandidatesAsync($"TapeWizard-{caseId}")).Should().BeEmpty();
        (await fixture.Work.TryPublishAsync(dependent.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Stale);
        (await Count("person_aliases_history", "person_uuid=@uuid", original.Uuid)).Should().Be(1);
        (await Revision("people", survivor.Uuid)).Should().Be(3);
    }

    [Test]
    public async Task MetadataReplayRemainsIdempotentAfterLaterMerge()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; Patrick; TapeWizard-{caseId}", "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Patrick");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            original.Uuid,
            $"TapeWizard-{caseId}",
            input.Reference
        );
        await fixture.PublishPeople(people, input, new([original, survivor], [alias], [], []));
        var owner = await fixture.OwnerUuid("people", "person", original.Uuid);
        var command = new PersonMetadataDecision(
            Guid.NewGuid(),
            owner,
            0,
            original.Uuid,
            1,
            "retract",
            "Wrong alias",
            Alias: alias,
            ExpectedAssertionRevision: 1
        );
        await people.ApplyMetadataDecisionAsync(command, "operator");
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                1,
                "merge",
                original.Uuid,
                2,
                survivor,
                1,
                [],
                "Same person"
            ),
            "operator"
        );
        (await people.ApplyMetadataDecisionAsync(command, "operator")).Replayed.Should().BeTrue();
        (await Revision("people", survivor.Uuid)).Should().Be(2);
    }

    [Test]
    public async Task ChangedExternalEvidenceRetainsPendingEvenWhenCallerReusesAssertionUuid()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; ID-17-{caseId}", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var external = new PersonExternalIdentity(
            person.Uuid,
            "provider",
            "person",
            $"ID-17-{caseId}",
            input.Reference,
            Guid.NewGuid()
        );
        await fixture.PublishPeople(people, input, new([person], [], [external], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "retract",
                "Disputed upstream link",
                ExternalIdentity: external,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        var changed = await fixture.Capture($"Pat; ID-17-{caseId}; corrected", "people");
        await fixture.PublishPeople(
            people,
            changed,
            new([], [], [external with { Evidence = changed.Reference }], [])
        );
        await fixture.PublishPeople(
            people,
            changed,
            new([], [], [external with { Evidence = changed.Reference }], [])
        );
        (
            await Count(
                "person_external_identities",
                "person_uuid=@uuid AND state='pending'",
                person.Uuid
            )
        )
            .Should()
            .Be(1);
        (
            await Count(
                "resolved_person_external_identities",
                "resolved_person_uuid=@uuid",
                person.Uuid
            )
        )
            .Should()
            .Be(0);
    }

    [Test]
    public async Task ExistingStructuredLocationWithTextEvidenceCanBeRetractedWithoutInventingNewEvidence()
    {
        var input = await fixture.Capture("Hall; New York, NY", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "New York, NY",
            "US",
            "NY",
            "New York",
            null,
            null,
            null,
            null,
            null,
            null,
            input.Reference
        );
        await fixture.PublishVenues(venues, input, new([venue], [], [location], [], [], []));
        var owner = await fixture.OwnerUuid("venues", "venue", venue.Uuid);
        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                venue.Uuid,
                1,
                "retract",
                "Incorrect locality parse",
                Location: location,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        (await Count("resolved_venue_locations", "resolved_venue_uuid=@uuid", venue.Uuid))
            .Should()
            .Be(0);
        var retained = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT city FROM normalization.venue_locations_history WHERE uuid=@Uuid",
                location
            )
        );
        retained.Should().Be("New York");
    }

    [Test]
    public async Task ConcurrentRoomReparentingAndSiteConversionCannotCreateARoomParent()
    {
        var input = await fixture.Capture("Parent Hall; Child Hall", "venues");
        var parent = new CanonicalVenue(Guid.NewGuid(), "Parent Hall");
        var child = new CanonicalVenue(Guid.NewGuid(), "Child Hall");
        await fixture.PublishVenues(venues, input, new([parent, child], [], [], [], [], []));
        var parentOwner = await fixture.OwnerUuid("venues", "venue", parent.Uuid);
        var childOwner = await fixture.OwnerUuid("venues", "venue", child.Uuid);
        var barrierKey = Random.Shared.Next(1, int.MaxValue);
        await using var barrier = fixture.Db.CreateConnection(false, false);
        await barrier.OpenAsync();
        await barrier.ExecuteAsync("SELECT pg_advisory_lock(@barrierKey)", new { barrierKey });
        // Stop the child after its parent-kind check, at the real database mutation boundary.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"""
                CREATE FUNCTION normalization.pause_child_metadata() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.uuid='{child.Uuid}'::uuid THEN PERFORM pg_advisory_xact_lock({barrierKey}); END IF; RETURN NEW; END $$;
                CREATE TRIGGER pause_child_metadata BEFORE UPDATE ON normalization.canonical_venues FOR EACH ROW EXECUTE FUNCTION normalization.pause_child_metadata()
                """
            )
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var childTask = venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                childOwner,
                0,
                child.Uuid,
                1,
                "replace",
                "Room belongs to parent site",
                Identity: child with
                {
                    Kind = "room",
                    ParentVenueUuid = parent.Uuid,
                },
                IdentityEvidence: input.Reference
            ),
            "operator",
            timeout.Token
        );
        Task<NormalizationReviewResult>? parentTask = null;
        try
        {
            await WaitFor(async () =>
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND objid=@barrierKey::oid AND NOT granted)",
                        new { barrierKey }
                    )
                )
            );
            parentTask = venues.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    parentOwner,
                    0,
                    parent.Uuid,
                    1,
                    "replace",
                    "Attempt site conversion",
                    Identity: parent with
                    {
                        Kind = "room",
                    },
                    IdentityEvidence: input.Reference
                ),
                "operator",
                timeout.Token
            );
            // Candidate-pool writers now serialize before the identity advisory lock.
            // Observe the parent waiting on the paused child at that earlier boundary.
            await WaitFor(async () =>
                parentTask.IsCompleted
                || await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        """
                        SELECT EXISTS(
                            SELECT 1 FROM pg_stat_activity waiting
                            JOIN pg_locks paused_child ON paused_child.pid=ANY(pg_blocking_pids(waiting.pid))
                            WHERE waiting.wait_event_type='Lock'
                                AND waiting.query LIKE '%normalization.candidate_universe_revisions%'
                                AND paused_child.locktype='advisory' AND paused_child.objid=@barrierKey::oid
                                AND NOT paused_child.granted)
                        """,
                        new { barrierKey }
                    )
                )
            );
        }
        finally
        {
            await barrier.ExecuteAsync(
                "SELECT pg_advisory_unlock(@barrierKey)",
                new { barrierKey }
            );
        }
        await childTask;
        var result = async () => await parentTask!;
        await result.Should().ThrowAsync<ArgumentException>();
        (await venues.FindCandidatesAsync("Parent Hall"))
            .Should()
            .ContainSingle(v => v.Kind == "site");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DROP TRIGGER pause_child_metadata ON normalization.canonical_venues; DROP FUNCTION normalization.pause_child_metadata()"
            )
        );
    }

    [Test]
    public async Task RejectRequiresStoredAssertionAndKeepsTheReviewedExclusion()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; Wrong Alias-{caseId}", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            person.Uuid,
            $"Wrong Alias-{caseId}",
            input.Reference
        );
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        var invalid = () =>
            people.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner,
                    0,
                    person.Uuid,
                    1,
                    "reject",
                    "Reject stored assertion, not an unrelated UUID",
                    Alias: alias with
                    {
                        Uuid = Guid.NewGuid(),
                    }
                ),
                "operator"
            );
        await invalid.Should().ThrowAsync<ArgumentException>();
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "reject",
                "Incorrect attribution",
                Alias: alias,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        await fixture.PublishPeople(
            people,
            input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );
        (await people.FindCandidatesAsync($"Wrong Alias-{caseId}")).Should().BeEmpty();
        (await Count("person_aliases", "person_uuid=@uuid AND state='rejected'", person.Uuid))
            .Should()
            .Be(1);
    }

    [Test]
    public async Task AutomaticMetadataRetainsValidatedMatchBasisAndCannotClaimOperatorAuthority()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture($"Pat; TapeWizard-{caseId}; ID-17-{caseId}", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            person.Uuid,
            $"TapeWizard-{caseId}",
            input.Reference,
            "validated_match"
        );
        var external = new PersonExternalIdentity(
            person.Uuid,
            "provider",
            "person",
            $"ID-17-{caseId}",
            input.Reference,
            Basis: "validated_match"
        );
        await fixture.PublishPeople(people, input, new([person], [alias], [external], []));
        var personBasis = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                "SELECT basis FROM normalization.person_aliases WHERE uuid=@Uuid UNION ALL SELECT basis FROM normalization.person_external_identities WHERE person_uuid=@PersonUuid",
                alias
            )
        );
        personBasis.Should().Equal("validated_match", "validated_match");
        var venueInput = await fixture.Capture(
            $"Hall; Former Hall-{caseId}; New York; venue-1-{caseId}",
            "venues"
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var name = new VenueNameAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            $"Former Hall-{caseId}",
            null,
            null,
            null,
            venueInput.Reference,
            "validated_match"
        );
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "New York",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            venueInput.Reference,
            "validated_match"
        );
        var venueExternal = new VenueExternalIdentity(
            venue.Uuid,
            "provider",
            "venue",
            $"venue-1-{caseId}",
            venueInput.Reference,
            Basis: "validated_match"
        );
        await fixture.PublishVenues(
            venues,
            venueInput,
            new([venue], [name], [location], [venueExternal], [], [])
        );
        var venueBasis = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                "SELECT basis FROM normalization.venue_names WHERE uuid=@nameUuid UNION ALL SELECT basis FROM normalization.venue_locations WHERE uuid=@locationUuid UNION ALL SELECT basis FROM normalization.venue_external_identities WHERE venue_uuid=@venueUuid",
                new
                {
                    nameUuid = name.Uuid,
                    locationUuid = location.Uuid,
                    venueUuid = venue.Uuid,
                }
            )
        );
        venueBasis.Should().Equal("validated_match", "validated_match", "validated_match");
        var attempted = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var spoof = () =>
            fixture.PublishPeople(
                people,
                input,
                new(
                    [attempted],
                    [new(Guid.NewGuid(), attempted.Uuid, "Pat", input.Reference, "reviewed")],
                    [],
                    []
                )
            );
        await spoof.Should().ThrowAsync<ArgumentException>();
        (await Count("people", "uuid=@uuid", attempted.Uuid)).Should().Be(0);
    }

    [Test]
    public async Task NewMetadataRequiresBoundResourceWhileHistoricalRevisionsAndRetractionRemainSupported()
    {
        var caseId = Guid.NewGuid().ToString("N");
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        await fixture.PublishPeople(people, input, new([person], [], [], []));
        var owner = await fixture.OwnerUuid("people", "person", person.Uuid);
        var extra = await fixture.Capture($"TapeWizard-{caseId}", "people");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            person.Uuid,
            $"TapeWizard-{caseId}",
            extra.Reference
        );
        var unbound = () =>
            people.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner,
                    0,
                    person.Uuid,
                    1,
                    "accept",
                    "Unbound source",
                    Alias: alias
                ),
                "operator"
            );
        await unbound.Should().ThrowAsync<ArgumentException>();
        (await Count("person_aliases", "uuid=@uuid", alias.Uuid)).Should().Be(0);
        await fixture.Own(extra, "person", person.Uuid);
        await fixture.Refresh(extra, "Changed upstream wording");
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                person.Uuid,
                1,
                "accept",
                "Retained historical evidence from bound source",
                Alias: alias
            ),
            "operator"
        );
        (await people.FindCandidatesAsync($"TapeWizard-{caseId}"))
            .Should()
            .ContainSingle(p => p.Uuid == person.Uuid);
        await fixture.Own(input, "person", person.Uuid);
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                1,
                person.Uuid,
                2,
                "retract",
                "Correct old claim after source binding changed",
                Alias: alias,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        (await people.FindCandidatesAsync($"TapeWizard-{caseId}")).Should().BeEmpty();
        (await Count("person_aliases_history", "uuid=@uuid", alias.Uuid)).Should().Be(1);
    }

    [TestCase("history")]
    [TestCase("address")]
    [TestCase("coordinates")]
    [TestCase("unknown_address")]
    public async Task LocationReviewPreservesDistinctClaimsWithinTheSameLocality(string difference)
    {
        var input = await fixture.Capture("Hall; New York, NY", "venues");
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var rejected = new VenueLocationAssertion(
            Guid.NewGuid(),
            venue.Uuid,
            "New York, NY",
            "US",
            "NY",
            "New York",
            "1 Main St",
            40.1,
            -73.1,
            "building",
            new DateOnly(1980, 1, 1),
            new DateOnly(1989, 12, 31),
            input.Reference,
            "validated_match"
        );
        var distinct = rejected with { Uuid = Guid.NewGuid() };
        distinct = difference switch
        {
            "history" => distinct with
            {
                ValidFrom = new DateOnly(1990, 1, 1),
                ValidTo = new DateOnly(1999, 12, 31),
            },
            "address" => distinct with { Address = "2 Main St" },
            "coordinates" => distinct with { Latitude = 40.2, Longitude = -73.2 },
            "unknown_address" => distinct with { Address = null },
            _ => throw new ArgumentOutOfRangeException(nameof(difference)),
        };
        await fixture.PublishVenues(
            venues,
            input,
            new([venue], [], [rejected, distinct], [], [], [])
        );
        var owner = await fixture.OwnerUuid("venues", "venue", venue.Uuid);
        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                owner,
                0,
                venue.Uuid,
                1,
                "retract",
                "Incorrect location claim",
                Location: rejected,
                ExpectedAssertionRevision: 1
            ),
            "operator"
        );
        var remaining = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT uuid FROM normalization.resolved_venue_locations WHERE resolved_venue_uuid=@uuid",
                new { uuid = venue.Uuid }
            )
        );
        remaining.Should().Equal(distinct.Uuid);
        // The same fact cannot return under a fresh UUID; a new historical claim remains eligible.
        var later = distinct with
        {
            Uuid = Guid.NewGuid(),
            ValidFrom = new DateOnly(2005, 1, 1),
            ValidTo = new DateOnly(2010, 12, 31),
        };
        await fixture.PublishVenues(
            venues,
            input,
            new(
                [],
                [],
                [rejected with { Uuid = Guid.NewGuid(), Basis = "provider_assertion" }, later],
                [],
                [],
                []
            )
        );
        var published = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT uuid FROM normalization.resolved_venue_locations WHERE resolved_venue_uuid=@uuid",
                new { uuid = venue.Uuid }
            )
        );
        published.Should().BeEquivalentTo(new[] { distinct.Uuid, later.Uuid });
        var changed = await fixture.Capture(
            "Hall; New York, NY; revised location evidence",
            "venues"
        );
        await fixture.PublishVenues(
            venues,
            changed,
            new(
                [],
                [],
                [rejected with { Uuid = Guid.NewGuid(), Evidence = changed.Reference }],
                [],
                [],
                []
            )
        );
        (await Count("venue_locations", "venue_uuid=@uuid AND state='pending'", venue.Uuid))
            .Should()
            .Be(1);
        (await Count("resolved_venue_locations", "resolved_venue_uuid=@uuid", venue.Uuid))
            .Should()
            .Be(2);
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!await condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Expected concurrent database wait did not appear.");
            }
            await Task.Delay(10);
        }
    }

    private Task<int> Count(string table, string predicate, Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                $"SELECT count(*) FROM normalization.{table} WHERE {predicate}",
                new { uuid }
            )
        );

    private Task<long> Revision(string table, Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                $"SELECT revision FROM normalization.{table} WHERE uuid=@uuid",
                new { uuid }
            )
        );
}
