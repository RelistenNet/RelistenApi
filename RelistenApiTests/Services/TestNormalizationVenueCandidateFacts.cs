using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Venue facts preserve accepted history, its original owners and the exact publication read set.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationVenueCandidateFacts
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedVenueService venues = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        venues = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task AllAcceptedHistoriesKeepTheirDatesLanguageRawGeographyAndExactCitations()
    {
        var venue = await Create(includeExternalIdentity: true);
        var snapshot = await Find(venue);
        var facts = snapshot.Facts.Should().ContainSingle().Subject;

        snapshot.Complete.Should().BeTrue();
        facts.Header.Uuid.Should().Be(venue.Identity.Uuid);
        facts.Names.Should().HaveCount(3);
        foreach (var expected in venue.Names)
        {
            var name = facts.Names.Single(fact => fact.Provenance.AssertionUuid == expected.Uuid);
            name.Wording.Should().Be(expected.Wording);
            name.Language.Should().Be(expected.Language);
            name.ValidFrom.Should().Be(expected.ValidFrom);
            name.ValidTo.Should().Be(expected.ValidTo);
            AssertProvenance(
                name.Provenance,
                expected.Uuid,
                venue.Identity.Uuid,
                facts.Header.OwnerSubjectUuid,
                expected.Basis,
                expected.Evidence
            );
        }
        facts
            .Locations.Should()
            .HaveCount(2, "concurrent contradictory assertions must both remain visible");
        foreach (var expected in venue.Locations)
        {
            var location = facts.Locations.Single(fact =>
                fact.Provenance.AssertionUuid == expected.Uuid
            );
            location
                .Should()
                .BeEquivalentTo(
                    new CandidateVenueLocationFact(
                        new(
                            expected.Uuid,
                            venue.Identity.Uuid,
                            facts.Header.OwnerSubjectUuid,
                            1,
                            expected.Basis,
                            [expected.Evidence]
                        ),
                        expected.OriginalLocality,
                        expected.Country,
                        expected.Region,
                        expected.City,
                        expected.Address,
                        expected.Latitude,
                        expected.Longitude,
                        expected.CoordinatePrecision,
                        expected.ValidFrom,
                        expected.ValidTo
                    )
                );
        }
        facts
            .Locations.Select(location => location.Country)
            .Should()
            .BeEquivalentTo("United States", "USA");
        facts
            .Locations.Should()
            .OnlyContain(location => location.CoordinatePrecision == "locality");
        var external = facts.ExternalIdentities.Should().ContainSingle().Subject;
        external.Provider.Should().Be(venue.ExternalIdentity!.Provider);
        external.ResourceType.Should().Be(venue.ExternalIdentity.ResourceType);
        external.ProviderKey.Should().Be(venue.ExternalIdentity.ProviderKey);
        AssertProvenance(
            external.Provenance,
            venue.ExternalIdentity.Uuid!.Value,
            venue.Identity.Uuid,
            facts.Header.OwnerSubjectUuid,
            venue.ExternalIdentity.Basis,
            venue.ExternalIdentity.Evidence
        );
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.UnknownGeography);
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.UnknownAuthority);
        snapshot.ReadSet.CatalogInputs.Should().BeEmpty();
        snapshot
            .ReadSet.CandidateUniverses.Should()
            .ContainSingle(dependency =>
                dependency.Universe == NormalizationCandidateUniverseKey.Venues
            );
        await AssertOwnerStamps(snapshot, [venue.Identity.Uuid]);

        // Accepted claims cite retained revisions, even after their provider response changes.
        await fixture.Refresh(venue.Input, "Later provider response without those assertions");
        var afterRefresh = await Find(venue);
        afterRefresh.Facts.Single().Names.Should().BeEquivalentTo(facts.Names);
        afterRefresh.Facts.Single().Locations.Should().BeEquivalentTo(facts.Locations);
    }

    [Test]
    public async Task RejectedHistoricalAssertionsRemainOutsideTheAcceptedFacts()
    {
        var venue = await Create();
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("venues", "venue", venue.Identity.Uuid)
            )
        )!;
        await venues.ApplyMetadataDecisionAsync(
            new VenueMetadataDecision(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                venue.Identity.Uuid,
                1,
                "reject",
                "This historical name belongs to another site",
                Name: venue.Names[1],
                ExpectedAssertionRevision: 1
            ),
            "fixture-operator"
        );

        var snapshot = await Find(venue);

        snapshot.Complete.Should().BeTrue();
        snapshot.Facts.Single().Names.Should().HaveCount(2);
        snapshot
            .Facts.Single()
            .Names.Should()
            .NotContain(name => name.Provenance.AssertionUuid == venue.Names[1].Uuid);
        snapshot.Facts.Single().Header.Revision.Should().Be(2);
        await AssertOwnerStamps(snapshot, [venue.Identity.Uuid]);
    }

    [Test]
    public async Task MergedAssertionsRetainOriginalOwnershipAndCitations()
    {
        var original = await Create();
        var survivor = await Create();
        var operation = await Merge(original, survivor);

        var snapshot = await Find(original);
        var facts = snapshot.Facts.Should().ContainSingle().Subject;

        snapshot.Complete.Should().BeTrue();
        facts.Header.Uuid.Should().Be(survivor.Identity.Uuid);
        facts
            .IdentityClosure.Select(header => header.Uuid)
            .Should()
            .BeEquivalentTo(new[] { original.Identity.Uuid, survivor.Identity.Uuid });
        facts
            .Redirects.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new CandidateRedirectFact(original.Identity.Uuid, survivor.Identity.Uuid, operation)
            );
        facts.Names.Should().HaveCount(6);
        facts.Locations.Should().HaveCount(4);
        var originalOwner = await fixture.OwnerUuid("venues", "venue", original.Identity.Uuid);
        var oldName = facts.Names.Single(name =>
            name.Provenance.AssertionUuid == original.Names[1].Uuid
        );
        AssertProvenance(
            oldName.Provenance,
            original.Names[1].Uuid,
            original.Identity.Uuid,
            originalOwner,
            original.Names[1].Basis,
            original.Names[1].Evidence
        );
        var oldLocation = facts.Locations.Single(location =>
            location.Provenance.AssertionUuid == original.Locations[0].Uuid
        );
        AssertProvenance(
            oldLocation.Provenance,
            original.Locations[0].Uuid,
            original.Identity.Uuid,
            originalOwner,
            original.Locations[0].Basis,
            original.Locations[0].Evidence
        );
        await AssertOwnerStamps(snapshot, [original.Identity.Uuid, survivor.Identity.Uuid]);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HeaderNamesNeverBecomeInventedAcceptedAliases(bool registerOwner)
    {
        var uuid = Guid.NewGuid();
        var name = "Uncited Hall " + uuid.ToString("N");
        if (registerOwner)
        {
            var input = await fixture.Capture(name, "venues");
            await fixture.Own(input, "venue", uuid);
        }
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.canonical_venues(uuid,display_name,name_key,kind) VALUES(@uuid,@name,@key,'site')",
                new
                {
                    uuid,
                    name,
                    key = NormalizedPeopleService.NameKey(name),
                }
            )
        );

        var snapshot = await candidates.FindVenueFactsAsync(Query(name));
        var facts = snapshot.Facts.Should().ContainSingle().Subject;

        facts.Header.DisplayText.Should().Be(name);
        facts.Names.Should().BeEmpty();
        facts.IdentityClosure.Should().ContainSingle().Which.DisplayText.Should().Be(name);
        snapshot.Complete.Should().Be(registerOwner);
        if (!registerOwner)
        {
            facts.Header.OwnerSubjectUuid.Should().BeNull();
            snapshot
                .Issues.Should()
                .Contain(issue =>
                    issue.Kind == CandidateFactsIssueKind.MissingOwner
                    && issue.CandidateUuid == uuid
                );
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ParentResolutionRetainsStoredParentAndAllConsumedOwnerStamps(bool bounded)
    {
        var originalParent = await Create();
        var survivorParent = await Create();
        var room = await Create(parentUuid: originalParent.Identity.Uuid);
        var operation = await Merge(originalParent, survivorParent);
        var limits = bounded ? new CandidateFactsLimits { MaximumParentClosure = 1 } : null;

        var snapshot = await Find(room, limits);
        var parent = snapshot.Facts.Single().Parents.Should().ContainSingle().Subject;

        parent.StoredVenueUuid.Should().Be(room.Identity.Uuid);
        parent.StoredParentVenueUuid.Should().Be(originalParent.Identity.Uuid);
        if (bounded)
        {
            parent.ResolvedParentVenueUuid.Should().BeNull();
            parent.ParentIdentityClosure.Should().ContainSingle();
            snapshot.Complete.Should().BeFalse();
            snapshot
                .Issues.Should()
                .Contain(issue => issue.Kind == CandidateFactsIssueKind.ParentClosureLimit);
            return;
        }
        snapshot.Complete.Should().BeTrue();
        parent.ResolvedParentVenueUuid.Should().Be(survivorParent.Identity.Uuid);
        parent
            .ParentIdentityClosure.Select(header => header.Uuid)
            .Should()
            .BeEquivalentTo(new[] { originalParent.Identity.Uuid, survivorParent.Identity.Uuid });
        parent
            .ParentRedirects.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new CandidateRedirectFact(
                    originalParent.Identity.Uuid,
                    survivorParent.Identity.Uuid,
                    operation
                )
            );
        await AssertOwnerStamps(
            snapshot,
            [room.Identity.Uuid, originalParent.Identity.Uuid, survivorParent.Identity.Uuid]
        );
    }

    [Test]
    public async Task AParentRedirectCycleTerminatesWithABlockingIssue()
    {
        var firstParent = await Create();
        var secondParent = await Create();
        var room = await Create(parentUuid: firstParent.Identity.Uuid);
        // Typed review prevents cycles. Raw maintenance damage must still produce a finite,
        // incomplete snapshot rather than a chosen parent or an unbounded recursive read.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO normalization.venue_redirects(retired_uuid,survivor_uuid,decision_uuid)
                VALUES(@first,@second,@decision),(@second,@first,@decision)
                """,
                new
                {
                    first = firstParent.Identity.Uuid,
                    second = secondParent.Identity.Uuid,
                    decision = Guid.NewGuid(),
                }
            )
        );

        try
        {
            var snapshot = await Find(room);

            snapshot.Complete.Should().BeFalse();
            var parent = snapshot.Facts.Single().Parents.Should().ContainSingle().Subject;
            parent.ResolvedParentVenueUuid.Should().BeNull();
            parent.ParentIdentityClosure.Should().HaveCount(2);
            snapshot
                .Issues.Should()
                .Contain(issue =>
                    issue.Kind == CandidateFactsIssueKind.InvalidStoredFact
                    && issue.CandidateUuid == room.Identity.Uuid
                    && issue.FactKind == "venue_parent"
                );
        }
        finally
        {
            // Other fixtures also exercise the legacy unbounded redirect view.
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "DELETE FROM normalization.venue_redirects WHERE retired_uuid=ANY(@ids)",
                    new { ids = new[] { firstParent.Identity.Uuid, secondParent.Identity.Uuid } }
                )
            );
        }
    }

    [Test]
    public async Task ParentLimitAppliesAcrossTheWholeCandidateHistory()
    {
        var firstParent = await Create();
        var secondParent = await Create();
        var original = await Create(parentUuid: firstParent.Identity.Uuid);
        var survivor = await Create(parentUuid: firstParent.Identity.Uuid);
        await Merge(original, survivor);
        // A retained origin can carry a different historical parent after catalog maintenance.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.canonical_venues SET parent_venue_uuid=@parent WHERE uuid=@original",
                new { parent = secondParent.Identity.Uuid, original = original.Identity.Uuid }
            )
        );

        var snapshot = await Find(original, new CandidateFactsLimits { MaximumParentClosure = 1 });

        snapshot.Complete.Should().BeFalse();
        snapshot.Facts.Single().Parents.Should().HaveCount(2);
        snapshot
            .Facts.Single()
            .Parents.SelectMany(parent => parent.ParentIdentityClosure)
            .Select(header => header.Uuid)
            .Distinct()
            .Should()
            .ContainSingle();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.ParentClosureLimit
                && issue.PreventsAutomaticAcceptance
            );
    }

    [Test]
    public async Task AssertionLimitCannotPresentOneConvenientLocationAsACompleteHistory()
    {
        var venue = await Create();

        var snapshot = await Find(venue, new CandidateFactsLimits { MaximumAssertionsPerKind = 1 });

        snapshot.Complete.Should().BeFalse();
        snapshot.Facts.Single().Locations.Should().ContainSingle();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.AssertionLimit
                && issue.CandidateUuid == venue.Identity.Uuid
                && issue.FactKind == "venue_location"
                && issue.PreventsAutomaticAcceptance
            );
    }

    [Test]
    public async Task ReverseClosureLimitKeepsTheSurvivorAndMarksOmittedHistory()
    {
        var original = await Create();
        var survivor = await Create();
        await Merge(original, survivor);

        var snapshot = await Find(
            original,
            new CandidateFactsLimits { MaximumIdentityClosure = 1 }
        );

        snapshot.Complete.Should().BeFalse();
        var facts = snapshot.Facts.Should().ContainSingle().Subject;
        facts.Header.Uuid.Should().Be(survivor.Identity.Uuid);
        facts
            .IdentityClosure.Should()
            .ContainSingle()
            .Which.Uuid.Should()
            .Be(survivor.Identity.Uuid);
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.IdentityClosureLimit
                && issue.PreventsAutomaticAcceptance
            );
    }

    [Test]
    public async Task OriginalOwnerReceiptRejectsFreshAdmissionAfterAnUnchangedOwnerPublication()
    {
        var venue = await Create();
        var observed = await Find(venue);
        var owner = await fixture.Own(venue.Input, "venue", venue.Identity.Uuid);
        (
            await venues.PublishAsync(
                owner.Ticket.WorkUuid,
                new VenuePublication([], [], [], [], [], [])
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        var current = await Find(venue);
        current
            .ReadSet.CandidateUniverses.Should()
            .BeEquivalentTo(observed.ReadSet.CandidateUniverses);
        current
            .ReadSet.OwnerObservations.Should()
            .NotBeEquivalentTo(observed.ReadSet.OwnerObservations);

        var admitOld = () => Admit(observed);
        await admitOld.Should().ThrowAsync<NormalizationConflictException>();
        var fresh = await Admit(current);
        (await fixture.Work.TryPublishAsync(fresh.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private Task<NormalizationWorkTicket> Admit(
        NormalizationCandidateFactsSnapshot<VenueCandidateFacts> snapshot
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("venues", "imported_venue", Guid.NewGuid()),
            "venue-facts-test",
            [],
            [],
            candidateUniverses: snapshot.ReadSet.CandidateUniverses,
            expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
        );

    private Task<NormalizationCandidateFactsSnapshot<VenueCandidateFacts>> Find(
        CreatedVenue venue,
        CandidateFactsLimits? limits = null
    ) => candidates.FindVenueFactsAsync(Query(venue.Identity.DisplayName), limits);

    private static NormalizationCandidateQuery Query(string name) =>
        new(IdentityMatchingDomain.Venues, [name], [], MinimumNameSimilarity: 0.99);

    private async Task<CreatedVenue> Create(
        Guid? parentUuid = null,
        bool includeExternalIdentity = false
    )
    {
        var uuid = Guid.NewGuid();
        var name = (parentUuid.HasValue ? "Room " : "Hall ") + uuid.ToString("N");
        string[] wording = [name, "Historical " + name, "Salle " + uuid.ToString("N")];
        var input = await fixture.CaptureJson(
            JsonSerializer.Serialize(
                new
                {
                    names = wording,
                    locations = new[]
                    {
                        new
                        {
                            literal = "San Francisco, CA",
                            country = "United States",
                            city = "San Francisco",
                        },
                        new
                        {
                            literal = "Denver, CO",
                            country = "USA",
                            city = "Denver",
                        },
                    },
                    external_id = "venue-" + uuid.ToString("N"),
                }
            ),
            "venues",
            "/names/0"
        );
        var identity = new CanonicalVenue(
            uuid,
            name,
            parentUuid.HasValue ? "room" : "site",
            parentUuid
        );
        VenueNameAssertion[] names =
        [
            new(
                Guid.NewGuid(),
                uuid,
                wording[0],
                "en",
                null,
                null,
                input.Reference with
                {
                    StartCodePoint = 0,
                    EndCodePoint = wording[0].EnumerateRunes().Count(),
                    Quote = wording[0],
                }
            ),
            new(
                Guid.NewGuid(),
                uuid,
                wording[1],
                "en",
                new DateOnly(1970, 1, 1),
                new DateOnly(1979, 12, 31),
                input.Reference with
                {
                    JsonPointer = "/names/1",
                }
            ),
            new(
                Guid.NewGuid(),
                uuid,
                wording[2],
                "fr",
                null,
                null,
                input.Reference with
                {
                    JsonPointer = "/names/2",
                },
                "validated_match"
            ),
        ];
        VenueLocationAssertion[] locations =
        [
            new(
                Guid.NewGuid(),
                uuid,
                "San Francisco, CA",
                "United States",
                "CA",
                "San Francisco",
                "1805 Geary Boulevard",
                37.78,
                -122.4,
                "locality",
                new DateOnly(1970, 1, 1),
                new DateOnly(1990, 12, 31),
                input.Reference with
                {
                    JsonPointer = "/locations/0",
                }
            ),
            new(
                Guid.NewGuid(),
                uuid,
                "Denver, CO",
                "USA",
                "CO",
                "Denver",
                "1510 Clarkson Street",
                39.74,
                -104.99,
                "locality",
                new DateOnly(1980, 1, 1),
                new DateOnly(2000, 12, 31),
                input.Reference with
                {
                    JsonPointer = "/locations/1",
                },
                "validated_match"
            ),
        ];
        var external = includeExternalIdentity
            ? new VenueExternalIdentity(
                uuid,
                "venue-pages",
                "venue",
                "venue-" + uuid.ToString("N"),
                input.Reference with
                {
                    JsonPointer = "/external_id",
                },
                Guid.NewGuid()
            )
            : null;
        (
            await fixture.PublishVenues(
                venues,
                input,
                new VenuePublication(
                    [identity],
                    names,
                    locations,
                    external == null ? [] : [external],
                    [],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return new(input, identity, names, locations, external);
    }

    private async Task<Guid> Merge(CreatedVenue original, CreatedVenue survivor)
    {
        var originalOwner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("venues", "venue", original.Identity.Uuid)
            )
        )!;
        var operation = Guid.NewGuid();
        (
            await venues.ApplyIdentityDecisionAsync(
                new VenueIdentityDecision(
                    operation,
                    originalOwner.SubjectUuid,
                    originalOwner.EditorialRevision,
                    "merge",
                    original.Identity.Uuid,
                    1,
                    survivor.Identity,
                    1,
                    [],
                    [],
                    "Same physical site"
                ),
                "fixture-operator"
            )
        )
            .Replayed.Should()
            .BeFalse();
        return operation;
    }

    private async Task AssertOwnerStamps(
        NormalizationCandidateFactsSnapshot<VenueCandidateFacts> snapshot,
        Guid[] identities
    )
    {
        var expected = new List<NormalizationSubjectObservation>();
        foreach (var uuid in identities)
        {
            var owner = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey("venues", "venue", uuid)
                )
            )!;
            expected.Add(
                new(
                    owner.SubjectUuid,
                    owner.DesiredGeneration,
                    owner.EditorialRevision,
                    owner.PublishedWorkUuid
                )
            );
        }
        snapshot.ReadSet.OwnerObservations.Should().BeEquivalentTo(expected);
    }

    private static void AssertProvenance(
        CandidateAssertionProvenance actual,
        Guid uuid,
        Guid identity,
        Guid? owner,
        string basis,
        NormalizedEvidenceReference evidence
    )
    {
        actual
            .Should()
            .BeEquivalentTo(
                new CandidateAssertionProvenance(uuid, identity, owner, 1, basis, [evidence])
            );
        actual.Evidence.Should().ContainSingle().Which.Should().Be(evidence);
    }

    private sealed record CreatedVenue(
        DomainInput Input,
        CanonicalVenue Identity,
        VenueNameAssertion[] Names,
        VenueLocationAssertion[] Locations,
        VenueExternalIdentity? ExternalIdentity
    );
}
