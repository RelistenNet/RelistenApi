using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestReviewedVenueCreation
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedVenueService venues = null!;
    private NormalizationReadService reads = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        venues = new(fixture.Db, fixture.Work);
        reads = new(fixture.Db, Options.Create(new NormalizationReadOptions { Enabled = true }));
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task FirstIdentityAndMetadataAreReviewedAndVisibleThroughPublicReads()
    {
        var setup = await Prepare();
        var result = await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer");
        result.EditorialRevision.Should().Be(1);
        result.Replayed.Should().BeFalse();
        var venue = (await reads.GetVenueAsync(setup.Command.VenueUuid))!;
        venue.display_name.Should().Be(setup.Command.DisplayName);
        venue.kind.Should().Be("site");
        venue.revision.Should().Be(1);
        venue.names.Should().ContainSingle().Which.basis.Should().Be("reviewed");
        venue
            .locations.Should()
            .ContainSingle()
            .Which.original_locality.Should()
            .Be("San Francisco, CA");
        venue.locations.Single().country.Should().BeNull();
        venue
            .external_identities.Should()
            .ContainSingle()
            .Which.provider_key.Should()
            .Be(setup.Command.ExternalIdentities[0].ProviderKey);
        var counts = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<(bool Reviewed, long Reviews, long Assignments)>(
                """
                SELECT v.metadata_reviewed,
                    (SELECT count(*) FROM normalization.review_operations WHERE uuid=@OperationUuid),
                    (SELECT count(*) FROM normalization.imported_venue_assignments WHERE canonical_venue_uuid=@VenueUuid)
                FROM normalization.canonical_venues v WHERE v.uuid=@VenueUuid
                """,
                setup.Command
            )
        );
        counts.Should().Be((true, 1, 0));
    }

    [Test]
    public async Task ExactReplayPreservesReceiptAndDifferentCommandOrOperationCannotReuseIdentity()
    {
        var setup = await Prepare();
        var first = await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer");
        var replay = await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer");
        replay.Replayed.Should().BeTrue();
        replay.EditorialRevision.Should().Be(first.EditorialRevision);
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(
                    setup.Command with
                    {
                        Reason = "Changed operation content",
                    },
                    "venue-reviewer"
                )
            )
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(
                    setup.Command with
                    {
                        OperationUuid = Guid.NewGuid(),
                        ExpectedEditorialRevision = 1,
                    },
                    "venue-reviewer"
                )
            )
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await reads.GetVenueAsync(setup.Command.VenueUuid))!.revision.Should().Be(1);
    }

    [Test]
    public async Task StaleEditorialRevisionAndWrongOwnerCannotCreateVenue()
    {
        var setup = await Prepare();
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                setup.Command.SubjectUuid,
                0,
                "accept",
                System.Text.Json.JsonSerializer.SerializeToElement(
                    new { note = "parallel review" }
                ),
                "Editorial change",
                "venue-reviewer"
            ),
            (_, _, _) => Task.CompletedTask
        );
        await FluentActions
            .Invoking(() => venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer"))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        (await reads.GetVenueAsync(setup.Command.VenueUuid)).Should().BeNull();
        var other = await Prepare();
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(
                    other.Command with
                    {
                        SubjectUuid = setup.Command.SubjectUuid,
                        ExpectedEditorialRevision = 1,
                    },
                    "venue-reviewer"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        (await reads.GetVenueAsync(other.Command.VenueUuid)).Should().BeNull();
    }

    [Test]
    public async Task UnsupportedLateMetadataRollsBackIdentityReviewAndEpochTogether()
    {
        var setup = await Prepare();
        var candidates = new NormalizationCandidateService(fixture.Db);
        var before = await candidates.FindVenueFactsAsync(
            new(IdentityMatchingDomain.Venues, [setup.Command.DisplayName], [])
        );
        var invalid = setup.Command with
        {
            Locations = [setup.Command.Locations[0] with { Country = "US" }],
        };
        await FluentActions
            .Invoking(() => venues.CreateReviewedVenueAsync(invalid, "venue-reviewer"))
            .Should()
            .ThrowAsync<ArgumentException>();
        (await reads.GetVenueAsync(setup.Command.VenueUuid)).Should().BeNull();
        (await fixture.Work.GetSubjectAsync(setup.Command.SubjectUuid))!
            .EditorialRevision.Should()
            .Be(0);
        var after = await candidates.FindVenueFactsAsync(
            new(IdentityMatchingDomain.Venues, [setup.Command.DisplayName], [])
        );
        after.ReadSet.CandidateUniverses.Should().Equal(before.ReadSet.CandidateUniverses);
        (await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer"))
            .Replayed.Should()
            .BeFalse();
    }

    [Test]
    public async Task EvidenceMustBelongToConfiguredOwnerAndExplicitKindIsRequired()
    {
        var setup = await Prepare();
        var foreign = await fixture.Capture(setup.Command.DisplayName, "venues");
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(
                    setup.Command with
                    {
                        IdentityEvidence = foreign.Reference,
                    },
                    "venue-reviewer"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(
                    setup.Command with
                    {
                        Kind = "unknown",
                    },
                    "venue-reviewer"
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>();
        (await reads.GetVenueAsync(setup.Command.VenueUuid)).Should().BeNull();
    }

    [Test]
    public async Task RoomRequiresReviewedTerminalSiteAndParentRevision()
    {
        var site = await Prepare();
        await venues.CreateReviewedVenueAsync(site.Command, "venue-reviewer");
        var room = await Prepare();
        var command = room.Command with { Kind = "room", Parent = new(site.Command.VenueUuid, 2) };
        await FluentActions
            .Invoking(() => venues.CreateReviewedVenueAsync(command, "venue-reviewer"))
            .Should()
            .ThrowAsync<NormalizationConflictException>();
        command = command with { Parent = new(site.Command.VenueUuid, 1) };
        await venues.CreateReviewedVenueAsync(command, "venue-reviewer");
        (await reads.GetVenueAsync(room.Command.VenueUuid))!
            .parent_venue_uuid.Should()
            .Be(site.Command.VenueUuid);
        var nested = await Prepare();
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(
                    nested.Command with
                    {
                        Kind = "room",
                        Parent = new(room.Command.VenueUuid, 1),
                    },
                    "venue-reviewer"
                )
            )
            .Should()
            .ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task LaterMetadataReviewKeepsCreationAndPriorAssertionHistory()
    {
        var setup = await Prepare();
        await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer");
        var name = setup.Command.Names[0];
        await venues.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                setup.Command.SubjectUuid,
                1,
                setup.Command.VenueUuid,
                1,
                "retract",
                "Incorrect historical name",
                Name: name,
                ExpectedAssertionRevision: 1
            ),
            "venue-reviewer"
        );
        (await reads.GetVenueAsync(setup.Command.VenueUuid))!.names.Should().BeEmpty();
        var history = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<(string Basis, string State, long Revision)>(
                "SELECT basis,state,revision FROM normalization.venue_names_history WHERE uuid=@Uuid",
                name
            )
        );
        history.Should().Be(("reviewed", "accepted", 1));
        (await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer"))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task CancelledCreationRemainsRetryable()
    {
        var setup = await Prepare();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await FluentActions
            .Invoking(() =>
                venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer", cancelled.Token)
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();
        (await reads.GetVenueAsync(setup.Command.VenueUuid)).Should().BeNull();
        (await venues.CreateReviewedVenueAsync(setup.Command, "venue-reviewer"))
            .Replayed.Should()
            .BeFalse();
    }

    private async Task<(VenueCreationDecision Command, DomainInput Input)> Prepare()
    {
        var uuid = Guid.NewGuid();
        var name = "Review venue " + uuid.ToString("N");
        var json = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                name,
                location = "San Francisco, CA",
                venue_key = "reviewed-venue-" + uuid.ToString("N"),
            }
        );
        var input = await fixture.CaptureJson(json, "venues", "/name");
        input = await fixture.Own(input, "venue", uuid);
        var location = new VenueLocationAssertion(
            Guid.NewGuid(),
            uuid,
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
            new(input.Reference.InputRevisionUuid, "/location")
        );
        return (
            new(
                Guid.NewGuid(),
                input.Ticket.SubjectUuid,
                0,
                uuid,
                name,
                "site",
                input.Reference,
                [new(Guid.NewGuid(), uuid, name, null, null, null, input.Reference)],
                [location],
                [
                    new(
                        uuid,
                        "reviewed-fixture",
                        "venue",
                        "reviewed-venue-" + uuid.ToString("N"),
                        new(input.Reference.InputRevisionUuid, "/venue_key"),
                        Guid.NewGuid()
                    ),
                ],
                "First physical identity"
            ),
            input
        );
    }
}
