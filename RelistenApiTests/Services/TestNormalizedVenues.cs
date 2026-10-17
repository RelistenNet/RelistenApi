using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizedVenues
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedVenueService venues = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        venues = new NormalizedVenueService(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task HistoricalNamesAndRoomRemainSeparateFromSite()
    {
        var input = await fixture.Capture("Old Hall, New Hall, Small Room", "venues");
        var site = new CanonicalVenue(Guid.NewGuid(), "New Hall");
        var room = new CanonicalVenue(Guid.NewGuid(), "Small Room", "room", site.Uuid);
        var old = new VenueNameAssertion(
            Guid.NewGuid(),
            site.Uuid,
            "Old Hall",
            null,
            new DateOnly(1980, 1, 1),
            new DateOnly(2000, 1, 1),
            input.Reference
        );
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            site.Uuid,
            "San Francisco, CA",
            "US",
            "CA",
            "San Francisco",
            null,
            37.77,
            -122.42,
            "locality",
            null,
            null,
            input.Reference
        );
        await fixture.PublishVenues(
            venues,
            input,
            new VenuePublication([room, site], [old], [location], [], [], [])
        );
        (await venues.FindCandidatesAsync("Old Hall"))
            .Should()
            .ContainSingle(v => v.Uuid == site.Uuid);
        (await venues.FindCandidatesAsync("Small Room"))
            .Should()
            .ContainSingle(v => v.ParentVenueUuid == site.Uuid);
        var city = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT coordinate_precision FROM normalization.venue_locations WHERE uuid=@Uuid",
                location
            )
        );
        city.Should().Be("locality");
    }

    [Test]
    public async Task ConflictPreservesAcceptedAssignmentAndReviewReplacementSurvivesReimport()
    {
        var input = await fixture.Capture("Hall", "venues");
        var catalog = await CatalogVenue();
        input = await fixture.Own(input, "imported_venue", catalog);
        var a = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var b = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var assignment = new VenueAssignment(Guid.NewGuid(), catalog, a.Uuid, [input.Reference]);
        await fixture.PublishVenues(
            venues,
            input,
            new VenuePublication([a, b], [], [], [], [assignment], [])
        );
        var proposal = assignment with { Uuid = Guid.NewGuid(), VenueUuid = b.Uuid };
        await fixture.PublishVenues(
            venues,
            input,
            new VenuePublication([], [], [], [], [proposal], [])
        );
        (await State(proposal.Uuid)).Should().Be("pending");
        await venues.ApplyDecisionAsync(
            new VenueAssignmentDecision(
                Guid.NewGuid(),
                input.Ticket.SubjectUuid,
                0,
                1,
                "replace",
                false,
                proposal,
                "Different physical hall"
            ),
            "test-operator"
        );
        (await State(assignment.Uuid)).Should().Be("superseded");
        (await State(proposal.Uuid)).Should().Be("accepted");
        await fixture.PublishVenues(
            venues,
            input,
            new VenuePublication([], [], [], [], [assignment with { Uuid = Guid.NewGuid() }], [])
        );
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT canonical_venue_uuid FROM normalization.imported_venue_assignments WHERE imported_venue_uuid=@catalog AND state='accepted'",
                    new { catalog }
                )
            )
        )
            .Should()
            .Be(b.Uuid);
    }

    [Test]
    public async Task MergeRequiresExplicitRelationsAndInstallsStableRedirect()
    {
        var input = await fixture.Capture("Old Hall; New Hall; Room under old hall", "venues");
        var catalog = await CatalogVenue();
        input = await fixture.Own(input, "imported_venue", catalog);
        var original = new CanonicalVenue(Guid.NewGuid(), "Old Hall");
        var survivor = new CanonicalVenue(Guid.NewGuid(), "New Hall");
        var room = new CanonicalVenue(Guid.NewGuid(), "Room under old hall", "room", original.Uuid);
        var assignment = new VenueAssignment(
            Guid.NewGuid(),
            catalog,
            original.Uuid,
            [input.Reference]
        );
        await fixture.PublishVenues(
            venues,
            input,
            new VenuePublication([original, survivor, room], [], [], [], [assignment], [])
        );
        var identityOwner = await fixture.Own(input, "venue", original.Uuid);
        var command = new VenueIdentityDecision(
            Guid.NewGuid(),
            identityOwner.Ticket.SubjectUuid,
            0,
            "merge",
            original.Uuid,
            1,
            survivor,
            1,
            [],
            [],
            "Same physical site renamed"
        );
        var missing = () => venues.ApplyIdentityDecisionAsync(command, "test-operator");
        await missing.Should().ThrowAsync<NormalizationConflictException>();
        command = command with { ImportedAssignmentUuids = [assignment.Uuid] };
        await venues.ApplyIdentityDecisionAsync(command, "test-operator");
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT survivor_uuid FROM normalization.venue_redirects WHERE retired_uuid=@Uuid",
                    original
                )
            )
        )
            .Should()
            .Be(survivor.Uuid);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT canonical_venue_uuid FROM normalization.imported_venue_assignments WHERE uuid=@Uuid",
                    assignment
                )
            )
        )
            .Should()
            .Be(survivor.Uuid);
        (await venues.ApplyIdentityDecisionAsync(command, "test-operator"))
            .Replayed.Should()
            .BeTrue();
        (await venues.FindCandidatesAsync(room.DisplayName))
            .Single()
            .ParentVenueUuid.Should()
            .Be(survivor.Uuid);
        var reversal = new IdentityMergeReversal(
            Guid.NewGuid(),
            identityOwner.Ticket.SubjectUuid,
            1,
            command.OperationUuid,
            original.Uuid,
            survivor.Uuid,
            2,
            2,
            "Undo mistaken merge"
        );
        await venues.ReverseMergeAsync(reversal, "test-operator");
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT canonical_venue_uuid FROM normalization.imported_venue_assignments WHERE uuid=@Uuid",
                    assignment
                )
            )
        )
            .Should()
            .Be(original.Uuid);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.venue_redirects WHERE retired_uuid=@Uuid)",
                    original
                )
            )
        )
            .Should()
            .BeFalse();
    }

    [Test]
    public async Task ChangedEvidenceForReviewedVenueClaimCreatesOnePendingProposalAcrossRetries()
    {
        var input = await fixture.Capture("Hall", "venues");
        var catalog = await CatalogVenue();
        input = await fixture.Own(input, "imported_venue", catalog);
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var assignment = new VenueAssignment(
            Guid.NewGuid(),
            catalog,
            venue.Uuid,
            [input.Reference]
        );
        await fixture.PublishVenues(
            venues,
            input,
            new VenuePublication([venue], [], [], [], [assignment], [])
        );
        await venues.ApplyDecisionAsync(
            new VenueAssignmentDecision(
                Guid.NewGuid(),
                input.Ticket.SubjectUuid,
                0,
                1,
                "reject",
                false,
                assignment,
                "Conflated sites"
            ),
            "test-operator"
        );
        var changed = await fixture.Refresh(input, "Hall with changed locality evidence");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await fixture.PublishVenues(
                venues,
                changed,
                new VenuePublication(
                    [],
                    [],
                    [],
                    [],
                    [assignment with { Uuid = Guid.NewGuid(), Evidence = [changed.Reference] }],
                    []
                )
            );
        }
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.imported_venue_assignments WHERE imported_venue_uuid=@catalog AND state='pending'",
                    new { catalog }
                )
            )
        )
            .Should()
            .Be(1);
    }

    [TestCase("/duration", false)]
    [TestCase("/unrelated", false)]
    [TestCase("/names/0", true)]
    public async Task QuoteFreeVenueNameEvidenceMustCiteTheActualNameString(
        string pointer,
        bool accepted
    )
    {
        var input = await fixture.CaptureJson(
            """{"names":["Hall"],"duration":42,"unrelated":"Other venue"}""",
            "venues",
            pointer
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var owner = await fixture.Own(input, "venue", venue.Uuid);
        var publish = () =>
            venues.PublishAsync(
                owner.Ticket.WorkUuid,
                new VenuePublication(
                    [venue],
                    [
                        new VenueNameAssertion(
                            Guid.NewGuid(),
                            venue.Uuid,
                            "Hall",
                            null,
                            null,
                            null,
                            input.Reference
                        ),
                    ],
                    [],
                    [],
                    [],
                    []
                )
            );
        if (accepted)
        {
            (await publish()).Status.Should().Be(NormalizationPublicationStatus.Published);
        }
        else
        {
            await publish.Should().ThrowAsync<ArgumentException>();
        }
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.canonical_venues WHERE uuid=@Uuid)",
                    venue
                )
            )
        )
            .Should()
            .Be(accepted);
    }

    private Task<string> State(Guid uuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT state FROM normalization.imported_venue_assignments WHERE uuid=@uuid",
                new { uuid }
            )
        );

    private async Task<Guid> CatalogVenue()
    {
        var source = await fixture.Source();
        var uuid = Guid.NewGuid();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO venues(artist_id,name,slug,uuid,updated_at) VALUES(@ArtistId,'Hall',@slug,@uuid,now())",
                new
                {
                    source.ArtistId,
                    slug = uuid.ToString(),
                    uuid,
                }
            )
        );
        return uuid;
    }
}
