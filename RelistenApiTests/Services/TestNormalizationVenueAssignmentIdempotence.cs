using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Fresh automatic work retains unchanged venue claims and their exact supporting citations.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationVenueAssignmentIdempotence
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedVenueService venues = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        venues = new(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task FreshWorkRetainsUnchangedAssignmentAndCandidateEpoch(
        bool isShow,
        bool freshAssignmentUuid
    )
    {
        var scenario = await Create(isShow);
        var before = await Read(scenario, scenario.Assignment.Uuid);
        var epoch = await Observe();
        var repeat = freshAssignmentUuid
            ? scenario.Assignment with
            {
                Uuid = Guid.NewGuid(),
            }
            : scenario.Assignment;

        await Publish(scenario, repeat);
        await Publish(scenario, repeat with { Uuid = Guid.NewGuid() });

        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(before);
        (await AssignmentCount(scenario)).Should().Be(1);
        (await Observe()).Should().Be(epoch);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DistinctCitationsAccumulateOnTheCurrentRevisionWithoutDuplicates(bool isShow)
    {
        var scenario = await Create(isShow);
        var before = await Read(scenario, scenario.Assignment.Uuid);
        var original = scenario.Input.Reference;
        // Text citations require both code-point offsets and the exact quoted substring.
        NormalizedEvidenceReference[] citations =
        [
            original with
            {
                StartCodePoint = 0,
                EndCodePoint = 4,
                Quote = "Hall",
            },
            original with
            {
                StartCodePoint = 0,
                EndCodePoint = 3,
                Quote = "Hal",
            },
            original with
            {
                StartCodePoint = 1,
                EndCodePoint = 4,
                Quote = "all",
            },
            original with
            {
                JsonPointer = "/duplicate",
            },
            original with
            {
                JsonPointer = "/long",
                StartCodePoint = 4,
                EndCodePoint = 8,
                Quote = "Hall",
            },
            original with
            {
                JsonPointer = "/long",
                StartCodePoint = 0,
                EndCodePoint = 8,
                Quote = "Old Hall",
            },
            original with
            {
                JsonPointer = "/long",
                StartCodePoint = 0,
                EndCodePoint = 3,
                Quote = "Old",
            },
        ];
        foreach (var citation in citations)
        {
            var epoch = await Observe();
            await Publish(
                scenario,
                scenario.Assignment with
                {
                    Uuid = Guid.NewGuid(),
                    Evidence = [citation, citation],
                }
            );
            (await Observe()).Generation.Should().BeGreaterThan(epoch.Generation);
        }
        var after = await Read(scenario, scenario.Assignment.Uuid);
        after.Revision.Should().Be(before.Revision);
        after.DecisionUuid.Should().Be(before.DecisionUuid);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
        after.HistoryCount.Should().Be(0);
        after.EvidenceCount.Should().Be(8);

        var beforeRepeat = await Observe();
        await Publish(
            scenario,
            scenario.Assignment with
            {
                Uuid = Guid.NewGuid(),
                Evidence = citations,
            }
        );
        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(after);
        (await Observe()).Should().Be(beforeRepeat);

        // Changing only the retained input UUID still creates a distinct citation.
        var changed = await fixture.CaptureJson(
            """{"name":"Hall","revision":2}""",
            "venues",
            "/name"
        );
        await Publish(
            scenario,
            scenario.Assignment with
            {
                Uuid = Guid.NewGuid(),
                Evidence = [changed.Reference],
            },
            changed
        );
        var newer = await Read(scenario, scenario.Assignment.Uuid);
        newer.Revision.Should().Be(before.Revision);
        newer.HistoryCount.Should().Be(0);
        newer.EvidenceCount.Should().Be(9);
        (await Observe()).Generation.Should().BeGreaterThan(beforeRepeat.Generation);
    }

    [TestCase(false, "basis")]
    [TestCase(false, "state")]
    [TestCase(false, "reason")]
    [TestCase(true, "basis")]
    [TestCase(true, "state")]
    [TestCase(true, "reason")]
    public async Task ChangedClaimFieldsCreateAnAuditedRevision(bool isShow, string field)
    {
        var scenario = await Create(isShow);
        var before = await Read(scenario, scenario.Assignment.Uuid);
        var epoch = await Observe();
        var changed = field switch
        {
            "basis" => scenario.Assignment with { Basis = "validated_match" },
            "state" => scenario.Assignment with { State = "pending" },
            "reason" => scenario.Assignment with { ReasonCode = "validated_venue_match" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        await Publish(scenario, changed);

        var after = await Read(scenario, scenario.Assignment.Uuid);
        after.Revision.Should().Be(before.Revision + 1);
        after.HistoryCount.Should().Be(1);
        after.State.Should().Be(changed.State);
        after.Basis.Should().Be(changed.Basis);
        after.ReasonCode.Should().Be(changed.ReasonCode);
        after.EvidenceCount.Should().Be(1, "each claim revision retains its own citations");
        (await Observe()).Generation.Should().BeGreaterThan(epoch.Generation);
        var stable = await Observe();
        await Publish(scenario, changed with { Uuid = Guid.NewGuid() });
        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(after);
        (await Observe()).Should().Be(stable);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task VenueChangesKeepExistingCorrectionAndConflictingProposalPolicy(
        bool isShow,
        bool freshAssignmentUuid
    )
    {
        var scenario = await Create(isShow);
        var before = await Read(scenario, scenario.Assignment.Uuid);
        var changed = scenario.Assignment with
        {
            Uuid = freshAssignmentUuid ? Guid.NewGuid() : scenario.Assignment.Uuid,
            VenueUuid = scenario.OtherVenueUuid,
        };

        await Publish(scenario, changed);

        var result = await Read(scenario, changed.Uuid);
        result.VenueUuid.Should().Be(scenario.OtherVenueUuid);
        if (!freshAssignmentUuid)
        {
            result.State.Should().Be("accepted");
            result.Revision.Should().Be(before.Revision + 1);
            result.HistoryCount.Should().Be(1);
            return;
        }
        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(before);
        result.State.Should().Be("pending");
        result.ReasonCode.Should().Be("conflicting_evidence");
        result.Revision.Should().Be(1);
        var epoch = await Observe();
        await Publish(scenario, changed with { Uuid = Guid.NewGuid() });
        (await Read(scenario, changed.Uuid)).Should().BeEquivalentTo(result);
        (await Observe()).Should().Be(epoch);
        (await AssignmentCount(scenario)).Should().Be(2);

        await Publish(
            scenario,
            changed with
            {
                Uuid = Guid.NewGuid(),
                Evidence = [scenario.Input.Reference with { JsonPointer = "/duplicate" }],
            }
        );
        var supported = await Read(scenario, changed.Uuid);
        supported.Revision.Should().Be(1);
        supported.HistoryCount.Should().Be(0);
        supported.EvidenceCount.Should().Be(2);
        (await AssignmentCount(scenario)).Should().Be(2);
        (await Observe()).Generation.Should().BeGreaterThan(epoch.Generation);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task KnownAssignmentCorrectionWinsOverAnExistingPendingProposal(bool isShow)
    {
        var scenario = await Create(isShow);
        var pending = scenario.Assignment with
        {
            Uuid = Guid.NewGuid(),
            VenueUuid = scenario.OtherVenueUuid,
        };
        await Publish(scenario, pending);
        var pendingBefore = await Read(scenario, pending.Uuid);
        pendingBefore.State.Should().Be("pending");

        var correction = scenario.Assignment with { VenueUuid = scenario.OtherVenueUuid };
        await Publish(scenario, correction);

        var accepted = await Read(scenario, correction.Uuid);
        accepted.State.Should().Be("accepted");
        accepted.VenueUuid.Should().Be(scenario.OtherVenueUuid);
        accepted.Revision.Should().Be(2);
        accepted.HistoryCount.Should().Be(1);
        (await Read(scenario, pending.Uuid)).Should().BeEquivalentTo(pendingBefore);
        var epoch = await Observe();
        await Publish(scenario, correction with { Uuid = Guid.NewGuid() });
        (await Read(scenario, correction.Uuid)).Should().BeEquivalentTo(accepted);
        (await Read(scenario, pending.Uuid)).Should().BeEquivalentTo(pendingBefore);
        (await AssignmentCount(scenario)).Should().Be(2);
        (await Observe()).Should().Be(epoch);
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task KnownAssignmentCannotMoveToAnotherCatalogObjectBeforeAnyEarlyReturn(
        bool isShow,
        bool reviewed
    )
    {
        var scenario = await Create(isShow);
        if (reviewed)
        {
            var owner = (
                await fixture.Work.GetSubjectAsync(
                    new NormalizationSubjectKey(
                        "venues",
                        isShow ? "show" : "imported_venue",
                        scenario.Assignment.CatalogUuid
                    )
                )
            )!;
            await venues.ApplyDecisionAsync(
                new VenueAssignmentDecision(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    1,
                    "accept",
                    isShow,
                    scenario.Assignment,
                    "Confirmed venue"
                ),
                "fixture-operator"
            );
        }
        var before = await Read(scenario, scenario.Assignment.Uuid);
        var otherCatalog = await Catalog(isShow);
        var epoch = await Observe();
        var rebind = () =>
            Publish(scenario, scenario.Assignment with { CatalogUuid = otherCatalog });

        await rebind.Should().ThrowAsync<NormalizationConflictException>();

        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(before);
        (await Observe()).Should().Be(epoch);
    }

    [TestCase(false, "accept")]
    [TestCase(false, "reject")]
    [TestCase(true, "accept")]
    [TestCase(true, "reject")]
    public async Task ReviewedClaimsRemainAuthoritativeAndNewEvidenceReusesOnePendingClaim(
        bool isShow,
        string action
    )
    {
        var scenario = await Create(isShow);
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey(
                    "venues",
                    isShow ? "show" : "imported_venue",
                    scenario.Assignment.CatalogUuid
                )
            )
        )!;
        await venues.ApplyDecisionAsync(
            new VenueAssignmentDecision(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                1,
                action,
                isShow,
                scenario.Assignment,
                "Checked the venue assignment"
            ),
            "fixture-operator"
        );
        var reviewed = await Read(scenario, scenario.Assignment.Uuid);
        var epoch = await Observe();

        // A different locator in the reviewed input is still covered by the existing decision.
        await Publish(
            scenario,
            scenario.Assignment with
            {
                Uuid = Guid.NewGuid(),
                Evidence = [scenario.Input.Reference with { JsonPointer = "/duplicate" }],
            }
        );
        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(reviewed);
        (await AssignmentCount(scenario)).Should().Be(1);
        (await Observe()).Should().Be(epoch);

        var changed = await fixture.Refresh(scenario.Input, "Hall revised");
        var proposal = scenario.Assignment with
        {
            Uuid = Guid.NewGuid(),
            Evidence = [changed.Reference],
        };
        await Publish(scenario, proposal, changed);
        var pending = await ReadPending(scenario);
        pending.State.Should().Be("pending");
        pending.Basis.Should().Be("provider_assertion");
        pending.ReasonCode.Should().Be("changed_evidence_reviewed_claim");
        var proposedEpoch = await Observe();
        await Publish(scenario, proposal with { Uuid = Guid.NewGuid() }, changed);
        (await ReadPending(scenario)).Should().BeEquivalentTo(pending);
        (await Observe()).Should().Be(proposedEpoch);
        (await Read(scenario, scenario.Assignment.Uuid)).Should().BeEquivalentTo(reviewed);
        (await AssignmentCount(scenario)).Should().Be(2);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TwoCandidateConsumersConvergeWhenTheirAssignmentsAreUnchanged(bool isShow)
    {
        var first = await Create(isShow);
        var second = first with
        {
            Assignment = first.Assignment with
            {
                Uuid = Guid.NewGuid(),
                CatalogUuid = await Catalog(isShow),
            },
        };
        await Publish(second, second.Assignment);
        var before = await Observe();
        var a = await AdmitConsumer(first, before);
        var b = await AdmitConsumer(second, before);

        (await venues.PublishAsync(a.WorkUuid, Publication(first, first.Assignment)))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await venues.PublishAsync(b.WorkUuid, Publication(second, second.Assignment)))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await Observe()).Should().Be(before);

        // Both next passes can consume the same generation; neither creates work for the other.
        a = await AdmitConsumer(first, before);
        b = await AdmitConsumer(second, before);
        (
            await venues.PublishAsync(
                a.WorkUuid,
                Publication(first, first.Assignment with { Uuid = Guid.NewGuid() })
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (
            await venues.PublishAsync(
                b.WorkUuid,
                Publication(second, second.Assignment with { Uuid = Guid.NewGuid() })
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await Observe()).Should().Be(before);
    }

    private async Task<Scenario> Create(bool isShow)
    {
        var input = await fixture.CaptureJson(
            """{"name":"Hall","duplicate":"Hall","long":"Old Hall"}""",
            "venues",
            "/name"
        );
        var venue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var otherVenue = new CanonicalVenue(Guid.NewGuid(), "Hall");
        var assignment = new VenueAssignment(
            Guid.NewGuid(),
            await Catalog(isShow),
            venue.Uuid,
            [input.Reference]
        );
        var scenario = new Scenario(isShow, input, assignment, otherVenue.Uuid);
        (
            await fixture.PublishVenues(
                venues,
                input,
                Publication(scenario, assignment) with
                {
                    Venues = [venue, otherVenue],
                }
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return scenario;
    }

    private async Task Publish(
        Scenario scenario,
        VenueAssignment assignment,
        DomainInput? input = null
    )
    {
        (
            await fixture.PublishVenues(
                venues,
                input ?? scenario.Input,
                Publication(scenario, assignment)
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private static VenuePublication Publication(Scenario scenario, VenueAssignment assignment) =>
        new(
            [],
            [],
            [],
            [],
            scenario.IsShow ? [] : [assignment],
            scenario.IsShow ? [assignment] : []
        );

    private async Task<Guid> Catalog(bool isShow)
    {
        var source = await fixture.Source();
        var uuid = Guid.NewGuid();
        var sql = isShow
            ? "INSERT INTO shows(artist_id,date,display_date,uuid,updated_at) VALUES(@ArtistId,'2026-10-17','2026-10-17',@uuid,now())"
            : "INSERT INTO venues(artist_id,name,slug,uuid,updated_at) VALUES(@ArtistId,'Hall',@uuid::text,@uuid,now())";
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(sql, new { source.ArtistId, uuid })
        );
        return uuid;
    }

    private Task<AssignmentState> Read(Scenario scenario, Guid uuid)
    {
        var table = Table(scenario);
        var evidenceColumn = EvidenceColumn(scenario);
        return fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AssignmentState>(
                $"""
                SELECT a.uuid AS "Uuid",a.canonical_venue_uuid AS "VenueUuid",a.revision AS "Revision",
                    a.state AS "State",a.basis AS "Basis",a.reason_code AS "ReasonCode",
                    a.decision_uuid AS "DecisionUuid",a.updated_at AS "UpdatedAt",
                    (SELECT count(*) FROM normalization.{table}_history h WHERE h.uuid=a.uuid) AS "HistoryCount",
                    (SELECT count(*) FROM normalization.venue_assignment_evidence e WHERE e.{evidenceColumn}=a.uuid AND e.assignment_revision=a.revision) AS "EvidenceCount"
                FROM normalization.{table} a WHERE a.uuid=@uuid
                """,
                new { uuid }
            )
        );
    }

    private async Task<AssignmentState> ReadPending(Scenario scenario)
    {
        var uuid = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                $"SELECT uuid FROM normalization.{Table(scenario)} WHERE {CatalogColumn(scenario)}=@CatalogUuid AND state='pending'",
                scenario.Assignment
            )
        );
        return await Read(scenario, uuid);
    }

    private Task<int> AssignmentCount(Scenario scenario) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<int>(
                $"SELECT count(*) FROM normalization.{Table(scenario)} WHERE {CatalogColumn(scenario)}=@CatalogUuid",
                scenario.Assignment
            )
        );

    private async Task<NormalizationCandidateUniverseDependency> Observe() =>
        new(
            NormalizationCandidateUniverseKey.Venues,
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<long>(
                    "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='venues' AND scope_uuid='00000000-0000-0000-0000-000000000000'"
                )
            )
        );

    private async Task<NormalizationWorkTicket> AdmitConsumer(
        Scenario scenario,
        NormalizationCandidateUniverseDependency observed
    )
    {
        var subject = new NormalizationSubjectKey(
            "venues",
            scenario.IsShow ? "show" : "imported_venue",
            scenario.Assignment.CatalogUuid
        );
        var owner = (await fixture.Work.GetSubjectAsync(subject))!;
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "policy-1",
            [scenario.Input.Resource],
            [await fixture.OwnerUuid("venues", "venue", scenario.Assignment.VenueUuid)],
            owner.DesiredGeneration,
            candidateUniverses: [observed]
        );
    }

    private static string Table(Scenario scenario) =>
        scenario.IsShow ? "show_venue_assignments" : "imported_venue_assignments";

    private static string CatalogColumn(Scenario scenario) =>
        scenario.IsShow ? "show_uuid" : "imported_venue_uuid";

    private static string EvidenceColumn(Scenario scenario) =>
        scenario.IsShow ? "show_assignment_uuid" : "imported_assignment_uuid";

    private sealed record Scenario(
        bool IsShow,
        DomainInput Input,
        VenueAssignment Assignment,
        Guid OtherVenueUuid
    );

    private sealed class AssignmentState
    {
        public Guid Uuid { get; init; }
        public Guid VenueUuid { get; init; }
        public long Revision { get; init; }
        public string State { get; init; } = null!;
        public string Basis { get; init; } = null!;
        public string ReasonCode { get; init; } = null!;
        public Guid DecisionUuid { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public int HistoryCount { get; init; }
        public int EvidenceCount { get; init; }
    }
}
