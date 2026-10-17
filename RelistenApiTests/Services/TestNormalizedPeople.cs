using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizedPeople
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new NormalizationDomainFixture();
        people = new NormalizedPeopleService(fixture.Db, fixture.Work);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task EqualNamesRemainDistinctAndOnePersonCanHaveCreditsAcrossArtists()
    {
        var input = await fixture.Capture("Pat 🎤", "people");
        var pat = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var namesake = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var a = await fixture.Source();
        var b = await fixture.Source();
        input = await fixture.Own(input, "recording", a.SourceUuid);
        var credits = new[]
        {
            Credit(a.SourceUuid, pat.Uuid, input.Reference),
            Credit(b.SourceUuid, pat.Uuid, input.Reference, "transferrer"),
        };
        var result = await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication(
                [pat, namesake],
                [
                    new(Guid.NewGuid(), pat.Uuid, "Pat", input.Reference),
                    new(Guid.NewGuid(), namesake.Uuid, "Pat", input.Reference),
                ],
                [],
                [credits[0]]
            )
        );
        var other = await fixture.Own(input, "recording", b.SourceUuid);
        await fixture.PublishPeople(people, other, new PeoplePublication([], [], [], [credits[1]]));
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await people.FindCandidatesAsync("PAT"))
            .Select(p => p.Uuid)
            .Should()
            .Contain([pat.Uuid, namesake.Uuid]);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(DISTINCT source_uuid) FROM normalization.recording_credits WHERE person_uuid=@uuid AND state='accepted'",
                    new { uuid = pat.Uuid }
                )
            )
        )
            .Should()
            .Be(2);
    }

    [Test]
    public async Task UnicodeEvidenceAndScopeFailureRollBackTheWholePublication()
    {
        var input = await fixture.Capture("A 🎤 Pat", "people");
        var source = await fixture.Source();
        input = await fixture.Own(input, "recording", source.SourceUuid);
        var other = await fixture.Source();
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var invalid = input.Reference with { StartCodePoint = 2, EndCodePoint = 3, Quote = "🎤" };
        var badQuote = invalid with { Quote = "Pat" };
        var publish = () =>
            fixture.PublishPeople(
                people,
                input,
                new PeoplePublication(
                    [person],
                    [],
                    [],
                    [Credit(source.SourceUuid, person.Uuid, badQuote)]
                )
            );
        await publish.Should().ThrowAsync<ArgumentException>();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.recording_credits WHERE person_uuid=@Uuid)",
                    person
                )
            )
        )
            .Should()
            .BeFalse();
        var badScope = () =>
            fixture.PublishPeople(
                people,
                input,
                new PeoplePublication(
                    [person],
                    [],
                    [],
                    [
                        Credit(source.SourceUuid, person.Uuid, invalid) with
                        {
                            ScopeTrackUuid = other.TrackUuid,
                        },
                    ]
                )
            );
        await badScope.Should().ThrowAsync<ArgumentException>();
        (
            await fixture.PublishPeople(
                people,
                input,
                new PeoplePublication(
                    [person],
                    [],
                    [],
                    [
                        Credit(source.SourceUuid, person.Uuid, invalid) with
                        {
                            ScopeTrackUuid = source.TrackUuid,
                        },
                    ]
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task ReviewedRejectionSurvivesAlternateGeneratedUuidAndChangedEvidenceNeedsReview()
    {
        var input = await fixture.Capture("Pat", "people");
        var source = await fixture.Source();
        input = await fixture.Own(input, "recording", source.SourceUuid);
        var pat = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var credit = Credit(source.SourceUuid, pat.Uuid, input.Reference);
        await fixture.PublishPeople(people, input, new PeoplePublication([pat], [], [], [credit]));
        var decision = new RecordingCreditDecision(
            Guid.NewGuid(),
            input.Ticket.SubjectUuid,
            0,
            1,
            "reject",
            credit,
            "Namesake cannot be identified"
        );
        await people.ApplyDecisionAsync(decision, "test-operator");
        (await people.ApplyDecisionAsync(decision, "test-operator")).Replayed.Should().BeTrue();
        await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication([], [], [], [credit with { Uuid = Guid.NewGuid() }])
        );
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.recording_credits WHERE source_uuid=@SourceUuid AND state='accepted'",
                    credit
                )
            )
        )
            .Should()
            .Be(0);
        var changed = await fixture.Refresh(input, "Pat, transfer credit corrected");
        await fixture.PublishPeople(
            people,
            changed,
            new PeoplePublication(
                [],
                [],
                [],
                [credit with { Uuid = Guid.NewGuid(), Evidence = [changed.Reference] }]
            )
        );
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.recording_credits WHERE source_uuid=@SourceUuid AND state='pending'",
                    credit
                )
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task UnreviewedCreditChangesUnderFreshEvidenceAndRetainsPriorValue()
    {
        var input = await fixture.Capture("Pat A; Pat B", "people");
        var source = await fixture.Source();
        input = await fixture.Own(input, "recording", source.SourceUuid);
        var a = new CanonicalPerson(Guid.NewGuid(), "Pat A");
        var b = new CanonicalPerson(Guid.NewGuid(), "Pat B");
        var credit = Credit(source.SourceUuid, a.Uuid, input.Reference);
        await fixture.PublishPeople(people, input, new PeoplePublication([a, b], [], [], [credit]));
        var changed = await fixture.Refresh(input, "Pat identity supplied");
        await fixture.PublishPeople(
            people,
            changed,
            new PeoplePublication(
                [],
                [],
                [],
                [
                    credit with
                    {
                        Uuid = Guid.NewGuid(),
                        PersonUuid = b.Uuid,
                        Evidence = [changed.Reference],
                    },
                ]
            )
        );
        var actual = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT person_uuid FROM normalization.recording_credits WHERE uuid=@Uuid",
                credit
            )
        );
        actual.Should().Be(b.Uuid);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT person_uuid FROM normalization.recording_credit_history WHERE uuid=@Uuid",
                    credit
                )
            )
        )
            .Should()
            .Be(a.Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "DELETE FROM source_tracks WHERE source_id=@SourceId;DELETE FROM source_sets WHERE source_id=@SourceId;DELETE FROM sources WHERE id=@SourceId",
                source
            )
        );
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT person_uuid FROM normalization.recording_credits WHERE uuid=@Uuid",
                    credit
                )
            )
        )
            .Should()
            .Be(b.Uuid);
    }

    [Test]
    public async Task ProviderIdentityContradictionCannotReassignItsOwner()
    {
        var input = await fixture.Capture("Pat", "people");
        var a = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var b = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var external = new PersonExternalIdentity(
            a.Uuid,
            "provider",
            "person",
            "person-1",
            input.Reference
        );
        await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication([a, b], [], [external], [])
        );
        var assign = () =>
            fixture.PublishPeople(
                people,
                input,
                new PeoplePublication([], [], [external with { PersonUuid = b.Uuid }], [])
            );
        await assign.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task ReviewedRoleAndWordingCorrectionExcludesThePriorClaimAcrossNewUuids()
    {
        var input = await fixture.Capture("Pat", "people");
        var source = await fixture.Source();
        input = await fixture.Own(input, "recording", source.SourceUuid);
        var pat = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var credit = Credit(source.SourceUuid, pat.Uuid, input.Reference);
        await fixture.PublishPeople(people, input, new PeoplePublication([pat], [], [], [credit]));
        await people.ApplyDecisionAsync(
            new RecordingCreditDecision(
                Guid.NewGuid(),
                input.Ticket.SubjectUuid,
                0,
                1,
                "replace",
                credit with
                {
                    Role = "transferrer",
                    OriginalText = "Pat Smith",
                },
                "Role and full name corrected"
            ),
            "test-operator"
        );
        await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication([], [], [], [credit with { Uuid = Guid.NewGuid() }])
        );
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.recording_credits WHERE source_uuid=@SourceUuid AND state='accepted'",
                    credit
                )
            )
        )
            .Should()
            .Be(1);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT role FROM normalization.recording_credits WHERE source_uuid=@SourceUuid AND state='accepted'",
                    credit
                )
            )
        )
            .Should()
            .Be("transferrer");
    }

    [Test]
    public async Task SurvivorConsumerStartedBeforeMergeBecomesStale()
    {
        var input = await fixture.Capture("Pat original; Pat survivor; Pat handle", "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Pat original");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Pat survivor");
        await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication([original, survivor], [], [], [])
        );
        var source = await fixture.Source();
        var credit = Credit(source.SourceUuid, original.Uuid, input.Reference);
        await fixture.PublishPeople(people, input, new PeoplePublication([], [], [], [credit]));
        var another = await fixture.Source();
        var targetOwner = await fixture.OwnerUuid("people", "person", survivor.Uuid);
        var consumer = await fixture.Own(input, "recording", another.SourceUuid, [targetOwner]);
        var owner = await fixture.Own(input, "person", original.Uuid);
        await people.ApplyIdentityDecisionAsync(
            new PersonIdentityDecision(
                Guid.NewGuid(),
                owner.Ticket.SubjectUuid,
                0,
                "merge",
                original.Uuid,
                1,
                survivor,
                1,
                [credit.Uuid],
                "Same documented person"
            ),
            "test-operator"
        );
        var result = await people.PublishAsync(
            consumer.Ticket.WorkUuid,
            new PeoplePublication(
                [],
                [],
                [],
                [Credit(another.SourceUuid, survivor.Uuid, input.Reference)]
            )
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task AliasAdditionAdvancesTargetEntityRevisionAndDuplicateReplayDoesNot()
    {
        var input = await fixture.Capture("Pat original; Pat survivor; Pat handle", "people");
        var original = new CanonicalPerson(Guid.NewGuid(), "Pat original");
        var survivor = new CanonicalPerson(Guid.NewGuid(), "Pat survivor");
        await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication([original, survivor], [], [], [])
        );
        var owner = await fixture.Own(input, "person", original.Uuid);
        var alias = new PersonAlias(Guid.NewGuid(), survivor.Uuid, "Pat handle", input.Reference);
        await fixture.PublishPeople(people, input, new PeoplePublication([], [alias], [], []));
        await fixture.PublishPeople(people, input, new PeoplePublication([], [alias], [], []));
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<long>(
                    "SELECT revision FROM normalization.people WHERE uuid=@Uuid",
                    survivor
                )
            )
        )
            .Should()
            .Be(2);
        var stale = () =>
            people.ApplyIdentityDecisionAsync(
                new PersonIdentityDecision(
                    Guid.NewGuid(),
                    owner.Ticket.SubjectUuid,
                    0,
                    "merge",
                    original.Uuid,
                    1,
                    survivor,
                    1,
                    [],
                    "Prepared before alias correction"
                ),
                "test-operator"
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task RecordingPublicationRequiresIdentityDependencyAndCannotCreatePeople()
    {
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        await fixture.PublishPeople(people, input, new PeoplePublication([person], [], [], []));
        var source = await fixture.Source();
        var owner = await fixture.Own(input, "recording", source.SourceUuid);
        var missingDependency = () =>
            people.PublishAsync(
                owner.Ticket.WorkUuid,
                new PeoplePublication(
                    [],
                    [],
                    [],
                    [Credit(source.SourceUuid, person.Uuid, input.Reference)]
                )
            );
        await missingDependency.Should().ThrowAsync<ArgumentException>();
        var mixed = () =>
            people.PublishAsync(
                owner.Ticket.WorkUuid,
                new PeoplePublication([new CanonicalPerson(Guid.NewGuid(), "Other")], [], [], [])
            );
        await mixed.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task AutomaticIdentityCreationWithoutNameEvidenceLeavesNoRows()
    {
        var input = await fixture.Capture("Pat", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var owner = await fixture.Own(input, "person", person.Uuid);
        var create = () =>
            people.PublishAsync(owner.Ticket.WorkUuid, new PeoplePublication([person], [], [], []));
        await create.Should().ThrowAsync<ArgumentException>();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@Uuid)",
                    person
                )
            )
        )
            .Should()
            .BeFalse();
    }

    [TestCase("/duration", false)]
    [TestCase("/unrelated", false)]
    [TestCase("/names/0", true)]
    public async Task QuoteFreeNameEvidenceMustCiteTheActualNameString(
        string pointer,
        bool accepted
    )
    {
        var input = await fixture.CaptureJson(
            """{"names":["Pat"],"duration":42,"unrelated":"Other person"}""",
            "people",
            pointer
        );
        var person = new CanonicalPerson(Guid.NewGuid(), "Pat");
        var owner = await fixture.Own(input, "person", person.Uuid);
        var publish = () =>
            people.PublishAsync(
                owner.Ticket.WorkUuid,
                new PeoplePublication(
                    [person],
                    [new PersonAlias(Guid.NewGuid(), person.Uuid, "Pat", input.Reference)],
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
                    "SELECT EXISTS(SELECT 1 FROM normalization.people WHERE uuid=@Uuid)",
                    person
                )
            )
        )
            .Should()
            .Be(accepted);
    }

    [Test]
    public async Task HtmlEntityNameKeepsExactRawEvidenceAndSupportsDecodedDisplayName()
    {
        var input = await fixture.Capture("Fran&#231;ois", "people");
        var person = new CanonicalPerson(Guid.NewGuid(), "François");
        var result = await fixture.PublishPeople(
            people,
            input,
            new PeoplePublication([person], [], [], [])
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await people.FindCandidatesAsync("François"))
            .Should()
            .ContainSingle(p => p.Uuid == person.Uuid);
    }

    private static RecordingCredit Credit(
        Guid source,
        Guid? person,
        NormalizedEvidenceReference evidence,
        string role = "taper"
    ) => new(Guid.NewGuid(), source, role, "Pat", person, null, [evidence]);
}

internal sealed class NormalizationDomainFixture : IDisposable
{
    private readonly string previousWrite;
    private readonly string previousRead;
    public DbService Db { get; }
    public NormalizationWorkService Work { get; }
    private NormalizationEvidenceService Evidence { get; }

    public NormalizationDomainFixture()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include local integration tests.");
        }
        previousWrite = DbService.ConnStr;
        previousRead = DbService.ReadOnlyConnStr;
        Db = new DbService(
            LocalTestDatabase.DatabaseUrl,
            null,
            null,
            Host.CreateApplicationBuilder().Environment,
            NullLogger<DbService>.Instance
        );
        Work = new NormalizationWorkService(Db);
        Evidence = new NormalizationEvidenceService(Db);
    }

    public void Dispose()
    {
        DbService.ConnStr = previousWrite;
        DbService.ReadOnlyConnStr = previousRead;
    }

    public async Task<DomainInput> Capture(string text, string domain)
    {
        var resource = new NormalizationResourceKey("test", domain, Guid.NewGuid().ToString());
        var result = await Evidence.CaptureInputAsync(
            new NormalizationInputCapture(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { text }),
                "application/json",
                "test",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var subject = new NormalizationSubjectKey(
            domain,
            domain == "people" ? "person" : "venue",
            Guid.NewGuid()
        );
        var ticket = await Work.BeginWorkAsync(Guid.NewGuid(), subject, "policy-1", [resource], []);
        return new(
            resource,
            subject,
            ticket,
            new(result.Revision.Uuid, "/text", 0, text.EnumerateRunes().Count(), text)
        );
    }

    public async Task<DomainInput> CaptureJson(string json, string domain, string pointer)
    {
        var resource = new NormalizationResourceKey("test", domain, Guid.NewGuid().ToString());
        var result = await Evidence.CaptureInputAsync(
            new NormalizationInputCapture(
                Guid.NewGuid(),
                resource,
                Encoding.UTF8.GetBytes(json),
                "application/json",
                "test",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var subject = new NormalizationSubjectKey(
            domain,
            domain == "people" ? "person" : "venue",
            Guid.NewGuid()
        );
        var ticket = await Work.BeginWorkAsync(Guid.NewGuid(), subject, "policy-1", [resource], []);
        return new(resource, subject, ticket, new(result.Revision.Uuid, pointer));
    }

    public async Task<DomainInput> Own(
        DomainInput input,
        string kind,
        Guid uuid,
        IReadOnlyList<Guid>? dependencies = null
    )
    {
        var subject = new NormalizationSubjectKey(input.Subject.Domain, kind, uuid);
        var generation = await Db.WithWriteConnection(c =>
            c.QuerySingleOrDefaultAsync<long?>(
                "SELECT desired_generation FROM normalization.work_subjects WHERE domain=@Domain AND subject_kind=@Kind AND subject_uuid=@Uuid",
                subject
            )
        );
        return input with
        {
            Subject = subject,
            Ticket = await Work.BeginWorkAsync(
                Guid.NewGuid(),
                subject,
                "policy-1",
                [input.Resource],
                dependencies ?? [],
                generation
            ),
        };
    }

    public async Task<NormalizationPublicationResult> PublishPeople(
        NormalizedPeopleService service,
        DomainInput input,
        PeoplePublication publication
    )
    {
        NormalizationPublicationResult? result = null;
        var identityUuids = publication
            .People.Select(p => p.Uuid)
            .Concat(publication.Aliases.Select(a => a.PersonUuid))
            .Concat(publication.ExternalIdentities.Select(i => i.PersonUuid))
            .Distinct();
        foreach (var uuid in identityUuids)
        {
            var identity = await Own(input, "person", uuid);
            result = await service.PublishAsync(
                identity.Ticket.WorkUuid,
                new PeoplePublication(
                    publication.People.Where(p => p.Uuid == uuid).ToArray(),
                    publication
                        .Aliases.Where(a => a.PersonUuid == uuid)
                        .Concat(
                            publication
                                .People.Where(p =>
                                    p.Uuid == uuid
                                    && !publication.Aliases.Any(a =>
                                        a.PersonUuid == uuid && a.Wording == p.DisplayName
                                    )
                                )
                                .Select(p => new PersonAlias(
                                    p.Uuid,
                                    p.Uuid,
                                    p.DisplayName,
                                    input.Reference
                                ))
                        )
                        .ToArray(),
                    publication.ExternalIdentities.Where(i => i.PersonUuid == uuid).ToArray(),
                    []
                )
            );
        }
        if (publication.Credits.Count != 0)
        {
            var dependencies = new List<Guid>();
            foreach (
                var uuid in publication
                    .Credits.Where(c => c.PersonUuid.HasValue)
                    .Select(c => c.PersonUuid!.Value)
                    .Distinct()
            )
            {
                dependencies.Add(await OwnerUuid("people", "person", uuid));
            }
            var recording = await Own(
                input,
                "recording",
                publication.Credits[0].SourceUuid,
                dependencies
            );
            result = await service.PublishAsync(
                recording.Ticket.WorkUuid,
                new PeoplePublication([], [], [], publication.Credits)
            );
        }
        return result
            ?? throw new InvalidOperationException("Test publication must contain a phase.");
    }

    public async Task<NormalizationPublicationResult> PublishVenues(
        NormalizedVenueService service,
        DomainInput input,
        VenuePublication publication
    )
    {
        NormalizationPublicationResult? result = null;
        var identityUuids = publication
            .Venues.OrderBy(v => v.ParentVenueUuid.HasValue)
            .Select(v => v.Uuid)
            .Concat(publication.Names.Select(n => n.VenueUuid))
            .Concat(publication.Locations.Select(l => l.VenueUuid))
            .Concat(publication.ExternalIdentities.Select(i => i.VenueUuid))
            .Distinct();
        foreach (var uuid in identityUuids)
        {
            var venue = publication.Venues.SingleOrDefault(v => v.Uuid == uuid);
            var dependencies = venue?.ParentVenueUuid is Guid parent
                ? new[] { await OwnerUuid("venues", "venue", parent) }
                : Array.Empty<Guid>();
            var identity = await Own(input, "venue", uuid, dependencies);
            result = await service.PublishAsync(
                identity.Ticket.WorkUuid,
                new VenuePublication(
                    venue == null ? [] : [venue],
                    publication
                        .Names.Where(n => n.VenueUuid == uuid)
                        .Concat(
                            venue != null
                            && !publication.Names.Any(n =>
                                n.VenueUuid == uuid && n.Wording == venue.DisplayName
                            )
                                ? new[]
                                {
                                    new VenueNameAssertion(
                                        venue.Uuid,
                                        venue.Uuid,
                                        venue.DisplayName,
                                        null,
                                        null,
                                        null,
                                        input.Reference
                                    ),
                                }
                                : Array.Empty<VenueNameAssertion>()
                        )
                        .ToArray(),
                    publication.Locations.Where(l => l.VenueUuid == uuid).ToArray(),
                    publication.ExternalIdentities.Where(i => i.VenueUuid == uuid).ToArray(),
                    [],
                    []
                )
            );
        }
        foreach (var isShow in new[] { false, true })
        {
            var assignments = isShow ? publication.Shows : publication.ImportedVenues;
            if (assignments.Count == 0)
            {
                continue;
            }
            var dependencies = new List<Guid>();
            foreach (var uuid in assignments.Select(a => a.VenueUuid).Distinct())
            {
                dependencies.Add(await OwnerUuid("venues", "venue", uuid));
            }
            var owner = await Own(
                input,
                isShow ? "show" : "imported_venue",
                assignments[0].CatalogUuid,
                dependencies
            );
            result = await service.PublishAsync(
                owner.Ticket.WorkUuid,
                new VenuePublication(
                    [],
                    [],
                    [],
                    [],
                    isShow ? [] : assignments,
                    isShow ? assignments : []
                )
            );
        }
        return result
            ?? throw new InvalidOperationException("Test publication must contain a phase.");
    }

    public Task<Guid> OwnerUuid(string domain, string kind, Guid uuid) =>
        Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT uuid FROM normalization.work_subjects WHERE domain=@domain AND subject_kind=@kind AND subject_uuid=@uuid",
                new
                {
                    domain,
                    kind,
                    uuid,
                }
            )
        );

    public async Task<DomainInput> Refresh(DomainInput input, string text)
    {
        var current = await Evidence.GetCurrentInputAsync(input.Resource);
        var result = await Evidence.CaptureInputAsync(
            new NormalizationInputCapture(
                Guid.NewGuid(),
                input.Resource,
                JsonSerializer.SerializeToUtf8Bytes(new { text }),
                "application/json",
                "test",
                current!.Generation,
                DateTimeOffset.UtcNow
            )
        );
        return input with
        {
            Reference = new(result.Revision.Uuid, "/text", 0, text.EnumerateRunes().Count(), text),
        };
    }

    public async Task<CatalogSource> Source()
    {
        return await Db.WithWriteConnection(async c =>
        {
            var artist = await c.QuerySingleAsync<int>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES('Normalization test',@slug,@uuid,now()) RETURNING id",
                new { slug = Guid.NewGuid().ToString(), uuid = Guid.NewGuid() }
            );
            var sourceUuid = Guid.NewGuid();
            var source = await c.QuerySingleAsync<int>(
                "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@artist,@key,'2026-10-17',@uuid,now(),false,false,false) RETURNING id",
                new
                {
                    artist,
                    key = sourceUuid.ToString(),
                    uuid = sourceUuid,
                }
            );
            var set = await c.QuerySingleAsync<long>(
                "INSERT INTO source_sets(source_id,index,name,uuid,updated_at) VALUES(@source,0,'Set',@uuid,now()) RETURNING id",
                new { source, uuid = Guid.NewGuid() }
            );
            var trackUuid = Guid.NewGuid();
            await c.ExecuteAsync(
                "INSERT INTO source_tracks(source_id,source_set_id,artist_id,track_position,title,slug,mp3_url,uuid,updated_at) VALUES(@source,@set,@artist,1,'Song',@slug,'https://example.test/file.mp3',@uuid,now())",
                new
                {
                    source,
                    set,
                    artist,
                    slug = trackUuid.ToString(),
                    uuid = trackUuid,
                }
            );
            return new CatalogSource(source, sourceUuid, trackUuid, artist);
        });
    }

    public sealed record DomainInput(
        NormalizationResourceKey Resource,
        NormalizationSubjectKey Subject,
        NormalizationWorkTicket Ticket,
        NormalizedEvidenceReference Reference
    );

    public sealed record CatalogSource(int SourceId, Guid SourceUuid, Guid TrackUuid, int ArtistId);
}
