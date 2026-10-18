using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>
/// Tests the additive authority substrate through current readers and real Work transactions.
/// Fixture SQL supplies managed authority; no resolver, complete producer publication or policy
/// acceptance is exercised by this fixture.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSourceActorAliasAuthority
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationCandidateService candidates = null!;

    internal static readonly NormalizationCandidateUniverseKey[] WriteScopes =
    [
        NormalizationCandidateUniverseKey.People,
        NormalizationCandidateUniverseKey.Compositions,
    ];

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        people = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ManagedAuthorityControlsEveryAliasReaderWithoutCreatingCreditContext()
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        await AssertVisible(scenario, alias, false);
        var source = await PrepareSource(fixture);
        await AddSupport(scenario, alias, source);

        await AssertVisible(scenario, alias, true);
        var snapshot = await candidates.FindPeopleFactsAsync(Query(scenario.Person.DisplayName));
        var fact = snapshot.Facts.Single();
        snapshot.Complete.Should().BeTrue();
        fact.AcceptedAliasMatchingNames.Should().Contain(alias.Wording);
        fact.AliasMatchingNamesComplete.Should().BeTrue();
        fact.ArtistContexts.Should().BeEmpty();
        fact.ActorEvidence.Should().BeEmpty();
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .Contain([source.SourceOwner, source.ExtractionOwner]);
    }

    [Test]
    public async Task LastSupportRetirementAndReactivationAdvanceBothUniversesAndRetainRawClaims()
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        var source = await PrepareSource(fixture);
        var first = await AddSupport(scenario, alias, source);
        var second = await AddSupport(scenario, alias, source);
        var before = await Clocks(scenario);
        var historyBefore = await AliasHistoryCount(alias);

        await Retire(scenario, first, assignment: false);
        await AssertVisible(scenario, alias, true);
        AssertAdvanced(before, await Clocks(scenario));
        before = await Clocks(scenario);

        // An active support on a retired assignment grants no current authority either.
        await Retire(scenario, second, assignment: true);
        await AssertVisible(scenario, alias, false);
        AssertAdvanced(before, await Clocks(scenario));
        (await AliasHistoryCount(alias)).Should().Be(historyBefore);
        (await RawAliasState(alias)).Should().Be("accepted");
        before = await Clocks(scenario);

        var reactivated = await AddSupport(scenario, alias, source);
        reactivated.AssignmentUuid.Should().NotBe(first.AssignmentUuid);
        await AssertVisible(scenario, alias, true);
        AssertAdvanced(before, await Clocks(scenario));
        (await AliasHistoryCount(alias)).Should().Be(historyBefore);
    }

    [Test]
    public async Task ExactAppendReuseAddsIndependentUnmanagedAuthorityThatSurvivesSourceRetirement()
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        var support = await AddSupport(scenario, alias, await PrepareSource(fixture));
        var before = await Clocks(scenario);
        await fixture.PublishPeople(people, scenario.Input, new([], [alias], [], []));

        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.person_alias_unmanaged_support WHERE alias_uuid=@Uuid",
                    alias
                )
            )
        )
            .Should()
            .Be(1);
        AssertAdvanced(before, await Clocks(scenario));
        await Retire(scenario, support, assignment: true);
        await AssertVisible(scenario, alias, true);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@PersonUuid AND wording=@Wording",
                    alias
                )
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task ReviewedAliasRemainsCurrentWhenManagedAuthorityDisappears()
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        var support = await AddSupport(scenario, alias, await PrepareSource(fixture));
        await ReviewAlias(scenario, alias, "accept");

        await Retire(scenario, support, assignment: true);

        await AssertVisible(scenario, alias, true);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT basis FROM normalization.person_aliases WHERE uuid=@Uuid",
                    alias
                )
            )
        )
            .Should()
            .Be("reviewed");
    }

    [TestCase("reject")]
    [TestCase("retract")]
    public async Task CanonicalNaturalNameVetoSurvivesNewUuidsAndChangedCitations(string action)
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        var support = await AddSupport(scenario, alias, await PrepareSource(fixture));
        await ReviewAlias(scenario, alias, action);
        await Retire(scenario, support, assignment: true);
        await fixture.PublishPeople(
            people,
            scenario.Input,
            new([], [alias with { Uuid = Guid.NewGuid() }], [], [])
        );
        await AssertVisible(scenario, alias, false);

        var changedInput = await fixture.Capture(alias.Wording, "people");
        await fixture.PublishPeople(
            people,
            changedInput,
            new(
                [],
                [alias with { Uuid = Guid.NewGuid(), Evidence = changedInput.Reference }],
                [],
                []
            )
        );

        await AssertVisible(scenario, alias, false);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@PersonUuid AND name_key=@key AND state='accepted'",
                    new { alias.PersonUuid, key = NormalizedPeopleService.NameKey(alias.Wording) }
                )
            )
        )
            .Should()
            .Be(0);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.person_aliases WHERE person_uuid=@PersonUuid AND name_key=@key AND state='pending'",
                    new { alias.PersonUuid, key = NormalizedPeopleService.NameKey(alias.Wording) }
                )
            )
        )
            .Should()
            .Be(1);
        var attach = () => AddSupport(scenario, alias, support.Source);
        await AssertStorageRefused(attach);
    }

    [Test]
    public async Task FailedAuthorityPublicationRollsBackVisibilityClocksAndCanRetryExactlyOnce()
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        var source = await PrepareSource(fixture);
        var ticket = (await fixture.Own(scenario.Input, "person", scenario.Person.Uuid)).Ticket;
        var assignment = Guid.NewGuid();
        var support = Guid.NewGuid();
        var before = await Clocks(scenario);
        async Task Write(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken ct
        )
        {
            await InsertSupport(
                connection,
                transaction,
                ticket.WorkUuid,
                scenario.Person.Uuid,
                alias.Uuid,
                source,
                assignment,
                support
            );
        }
        var fail = () =>
            fixture.Work.TryPublishAsync(
                ticket.WorkUuid,
                async (c, t, ct) =>
                {
                    await Write(c, t, ct);
                    throw new InvalidOperationException(
                        "Synthetic failure after support insertion"
                    );
                },
                candidateWriteScopes: WriteScopes
            );
        await fail.Should().ThrowAsync<InvalidOperationException>();
        await AssertVisible(scenario, alias, false);
        (await Clocks(scenario)).Should().Be(before);

        var publication = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            Write,
            candidateWriteScopes: WriteScopes
        );
        publication.Status.Should().Be(NormalizationPublicationStatus.Published);
        await AssertVisible(scenario, alias, true);
        var committed = await Clocks(scenario);
        var replay = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (_, _, _) =>
                throw new AssertionException("Committed publication must not execute twice"),
            candidateWriteScopes: WriteScopes
        );
        replay.Replayed.Should().BeTrue();
        (await Clocks(scenario)).Should().Be(committed);
    }

    [Test]
    public async Task AuthorityRowsPermitRetirementOnlyAndKeepImmutableHistory()
    {
        var scenario = await Person();
        var alias = await ManagedAlias(scenario);
        var support = await AddSupport(scenario, alias, await PrepareSource(fixture));
        foreach (
            var sql in new[]
            {
                "UPDATE normalization.source_actor_alias_assignments SET assertion_uuid=gen_random_uuid() WHERE uuid=@AssignmentUuid",
                "DELETE FROM normalization.source_actor_alias_assignments WHERE uuid=@AssignmentUuid",
                "UPDATE normalization.person_alias_contributor_support SET alias_uuid=gen_random_uuid() WHERE uuid=@SupportUuid",
                "DELETE FROM normalization.person_alias_contributor_support WHERE uuid=@SupportUuid",
            }
        )
        {
            var mutate = () => fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, support));
            await AssertStorageRefused(mutate);
        }
        await Retire(scenario, support, assignment: false);
        var restore = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.person_alias_contributor_support SET retired_work_uuid=NULL WHERE uuid=@SupportUuid",
                    support
                )
            );
        await AssertStorageRefused(restore);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<int>(
                    "SELECT count(*) FROM normalization.person_alias_contributor_support WHERE uuid=@SupportUuid AND retired_work_uuid IS NOT NULL",
                    support
                )
            )
        )
            .Should()
            .Be(1);
    }

    [Test]
    public async Task UnmanagedAuthorityIsAppendOnlyAndAnEmptyWriteDoesNotAdvanceClocks()
    {
        var scenario = await Person();
        var baseline = scenario.Person.Uuid;
        var before = await Clocks(scenario);
        await PersonPublication(
            scenario,
            async (c, t, work) =>
            {
                await c.ExecuteAsync(
                    "INSERT INTO normalization.person_alias_unmanaged_support(alias_uuid,introduced_work_uuid) SELECT @baseline,@work WHERE false",
                    new { baseline, work },
                    t
                );
            }
        );
        (await Clocks(scenario)).Should().Be(before);
        foreach (
            var sql in new[]
            {
                "DELETE FROM normalization.person_alias_unmanaged_support WHERE alias_uuid=@baseline",
                "UPDATE normalization.person_alias_unmanaged_support SET introduced_work_uuid=NULL WHERE alias_uuid=@baseline",
            }
        )
        {
            var mutate = () =>
                fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, new { baseline }));
            await AssertStorageRefused(mutate);
        }
        (await people.FindCandidatesAsync(scenario.Person.DisplayName))
            .Should()
            .Contain(person => person.Uuid == scenario.Person.Uuid);
    }

    [Test]
    public async Task SourceReviewCasRequiresTrustedReceiptNextRevisionAndRetainsEveryState()
    {
        var source = await PrepareSource(fixture);
        var state = Guid.NewGuid();
        var skipFirstRevision = () => SourceReview(source, state, 0, 2, "reject", insert: true);
        await AssertStorageRefused(skipFirstRevision);
        await SourceReview(source, state, 0, 1, "reject", insert: true);

        var stale = () => SourceReview(source, state, 0, 2, "retract", insert: false);
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        var mutateProof = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.source_actor_alias_review_state SET original_assertion='{}' WHERE uuid=@state",
                    new { state }
                )
            );
        await AssertStorageRefused(mutateProof);
        await SourceReview(source, state, 1, 2, "retract", insert: false);

        var mismatchedReceipt = () => SourceReview(source, state, 2, 3, "accept", insert: false);
        await AssertStorageRefused(mismatchedReceipt);
        var revisions = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<long>(
                "SELECT review_revision FROM normalization.source_actor_alias_review_state_history WHERE uuid=@state ORDER BY review_revision",
                new { state }
            )
        );
        revisions.Should().Equal(1, 2);
        var erase = () =>
            fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "DELETE FROM normalization.source_actor_alias_review_state_history WHERE uuid=@state",
                    new { state }
                )
            );
        await AssertStorageRefused(erase);
    }

    private Task<NormalizationReviewResult> SourceReview(
        PreparedSource source,
        Guid state,
        long expectedEditorial,
        long reviewRevision,
        string action,
        bool insert
    )
    {
        var operation = Guid.NewGuid();
        return fixture.Work.ApplyReviewAsync(
            new(
                operation,
                source.SourceOwner,
                expectedEditorial,
                action,
                JsonSerializer.SerializeToElement(new { state, reviewRevision }),
                "Fixture source review CAS",
                "fixture-operator"
            ),
            async (c, t, ct) =>
            {
                await c.ExecuteAsync(
                    insert
                        ? """
                        INSERT INTO normalization.source_actor_alias_review_state(
                            uuid,source_uuid,contributor_provider,contributor_type,contributor_key,
                            resource_uuid,input_revision_uuid,field_json_pointer,producer_version,
                            assertion_uuid,actor_reference_uuid,field_sha256,assertion_sha256,
                            original_assertion,original_scope,source_resolution_owner_uuid,
                            review_revision,state,selected_person_uuid,last_operation_uuid)
                        VALUES(@state,@SourceUuid,'fixture','source_actor_aliases','person_metadata',
                            @ResourceUuid,@InputRevisionUuid,'/fixture','fixture-authority-v1',
                            @state,@state,repeat('a',64),repeat('b',64),
                            '{"fixture":"review_cas_only"}','{}',@SourceOwner,
                            @reviewRevision,'rejected',NULL,@operation)
                        """
                        : """
                        UPDATE normalization.source_actor_alias_review_state
                        SET review_revision=@reviewRevision,state='retracted',last_operation_uuid=@operation
                        WHERE uuid=@state
                        """,
                    new
                    {
                        state,
                        operation,
                        reviewRevision,
                        source.SourceUuid,
                        source.ResourceUuid,
                        source.InputRevisionUuid,
                        source.SourceOwner,
                    },
                    t
                );
            }
        );
    }

    private async Task<PersonScenario> Person()
    {
        var person = new CanonicalPerson(
            Guid.NewGuid(),
            "Identity " + Guid.NewGuid().ToString("N")
        );
        var identityInput = await fixture.Capture(person.DisplayName, "people");
        await fixture.PublishPeople(people, identityInput, new([person], [], [], []));
        var aliasInput = await fixture.Capture("Alias " + Guid.NewGuid().ToString("N"), "people");
        return new(person, aliasInput);
    }

    private async Task<PersonAlias> ManagedAlias(PersonScenario scenario)
    {
        var alias = new PersonAlias(
            Guid.NewGuid(),
            scenario.Person.Uuid,
            scenario.Input.Reference.Quote!,
            scenario.Input.Reference
        );
        await PersonPublication(
            scenario,
            async (c, t, work) =>
            {
                var result = await NormalizedPeopleService.WriteAutomaticAliasAsync(
                    c,
                    t,
                    work,
                    alias,
                    new HashSet<Guid>(),
                    CancellationToken.None
                );
                result.Accepted.Should().BeTrue();
                result.AliasUuid.Should().Be(alias.Uuid);
            }
        );
        return alias;
    }

    private async Task<AuthoritySupport> AddSupport(
        PersonScenario scenario,
        PersonAlias alias,
        PreparedSource source
    )
    {
        var assignment = Guid.NewGuid();
        var support = Guid.NewGuid();
        await PersonPublication(
            scenario,
            (c, t, work) =>
                InsertSupport(
                    c,
                    t,
                    work,
                    scenario.Person.Uuid,
                    alias.Uuid,
                    source,
                    assignment,
                    support
                )
        );
        return new(assignment, support, source);
    }

    private async Task Retire(PersonScenario scenario, AuthoritySupport support, bool assignment)
    {
        await PersonPublication(
            scenario,
            (c, t, work) =>
                c.ExecuteAsync(
                    assignment
                        ? "UPDATE normalization.source_actor_alias_assignments SET retired_work_uuid=@work WHERE uuid=@AssignmentUuid"
                        : "UPDATE normalization.person_alias_contributor_support SET retired_work_uuid=@work WHERE uuid=@SupportUuid",
                    new
                    {
                        support.AssignmentUuid,
                        support.SupportUuid,
                        work,
                    },
                    t
                )
        );
    }

    private async Task PersonPublication(
        PersonScenario scenario,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, Task> write
    )
    {
        var ticket = (await fixture.Own(scenario.Input, "person", scenario.Person.Uuid)).Ticket;
        var result = await fixture.Work.TryPublishAsync(
            ticket.WorkUuid,
            (c, t, _) => write(c, t, ticket.WorkUuid),
            candidateWriteScopes: WriteScopes
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
    }

    private static async Task AssertStorageRefused(Func<Task> action)
    {
        // DbService retains PostgreSQL policy refusals as inner exceptions.
        var failure = await action.Should().ThrowAsync<Exception>();
        failure
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should()
            .Be(PostgresErrorCodes.RaiseException);
    }

    private async Task AssertVisible(PersonScenario scenario, PersonAlias alias, bool visible)
    {
        (await people.FindCandidatesAsync(alias.Wording))
            .Any(person => person.Uuid == alias.PersonUuid)
            .Should()
            .Be(visible);
        (await candidates.FindAsync(Query(alias.Wording)))
            .Items.Any(person => person.Uuid == alias.PersonUuid)
            .Should()
            .Be(visible);
        var fact = (
            await candidates.FindPeopleFactsAsync(Query(scenario.Person.DisplayName))
        ).Facts.Single();
        fact.Aliases.Any(name => name.Provenance.AssertionUuid == alias.Uuid).Should().Be(visible);
        fact.AcceptedAliasMatchingNames.Should().NotBeNull();
        fact.AcceptedAliasMatchingNames!.Contains(alias.Wording, StringComparer.Ordinal)
            .Should()
            .Be(visible);
        (
            await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM normalization.resolved_person_aliases WHERE uuid=@Uuid)",
                    alias
                )
            )
        )
            .Should()
            .Be(visible);
    }

    private async Task ReviewAlias(PersonScenario scenario, PersonAlias alias, string action)
    {
        var subject = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", scenario.Person.Uuid)
            )
        )!;
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@Uuid",
                scenario.Person
            )
        );
        var assertionRevision = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.person_aliases WHERE uuid=@Uuid",
                alias
            )
        );
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                subject.SubjectUuid,
                subject.EditorialRevision,
                scenario.Person.Uuid,
                revision,
                action,
                "Canonical alias review is independent of source support",
                Alias: alias,
                ExpectedAssertionRevision: assertionRevision
            ),
            "fixture-operator"
        );
    }

    private Task<string> RawAliasState(PersonAlias alias) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT state FROM normalization.person_aliases WHERE uuid=@Uuid",
                alias
            )
        );

    private Task<int> AliasHistoryCount(PersonAlias alias) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<int>(
                "SELECT count(*) FROM normalization.person_aliases_history WHERE uuid=@Uuid",
                alias
            )
        );

    private Task<AuthorityClocks> Clocks(PersonScenario scenario) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AuthorityClocks>(
                """
                SELECT person.revision AS "PersonRevision",
                    (SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='people' AND scope_uuid=@empty) AS "People",
                    (SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='compositions' AND scope_uuid=@empty) AS "Compositions"
                FROM normalization.people person WHERE person.uuid=@Uuid
                """,
                new { scenario.Person.Uuid, empty = Guid.Empty }
            )
        );

    private static void AssertAdvanced(AuthorityClocks before, AuthorityClocks after)
    {
        after.PersonRevision.Should().BeGreaterThan(before.PersonRevision);
        after.People.Should().BeGreaterThan(before.People);
        after.Compositions.Should().BeGreaterThan(before.Compositions);
    }

    private static NormalizationCandidateQuery Query(string name) =>
        new(IdentityMatchingDomain.People, [name], [], MinimumNameSimilarity: .99);

    internal static async Task<PreparedSource> PrepareSource(NormalizationDomainFixture fixture)
    {
        var source = await fixture.Source();
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var proposals = new NormalizationRecordingProposalService(fixture.Db);
        var processor = new NormalizationRecordingProcessor(
            new(fixture.Db, evidence),
            evidence,
            new(fixture.Db),
            new NormalizationProviderDecoder(),
            new(new(), new()),
            fixture.Work,
            proposals,
            new(fixture.Db)
        );
        var result = await processor.ProcessAsync(Guid.NewGuid(), source.SourceUuid);
        result.Disposition.Should().Be(NormalizationRecordingProcessingDisposition.Published);
        var batch = (await proposals.GetAttemptBatchAsync(result.AttemptUuid))!;
        var binding = batch.Preparation.Binding;
        var resource = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT resource_uuid FROM normalization.input_revisions WHERE uuid=@revision",
                new { revision = binding.InputRevisionUuid }
            )
        );
        var sourceTicket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "source_actor_resolution", source.SourceUuid),
            "fixture-source-authority",
            [],
            []
        );
        (await fixture.Work.TryPublishAsync(sourceTicket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return new(
            source.SourceUuid,
            sourceTicket.SubjectUuid,
            batch.Ticket.SubjectUuid,
            result.AttemptUuid,
            resource,
            binding.InputRevisionUuid
        );
    }

    // Synthetic JSON meets the reader's stored-proof shape contract. It deliberately does
    // not claim producer authentication, reviewed assignment or complete scope replacement.
    internal static Task InsertSupport(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid work,
        Guid person,
        Guid alias,
        PreparedSource source,
        Guid assignment,
        Guid support,
        bool malformed = false
    ) =>
        connection.ExecuteAsync(
            """
            WITH proof AS (
                SELECT jsonb_build_object(
                    'Version','fixture-authority-v1','AssertionUuid',@assignment::uuid,
                    'ActorReferenceUuid',@assignment::uuid,'FieldSha256',repeat('a',64),
                    'Name','Fixture actor','NameEvidence',jsonb_build_object(
                        'InputRevisionUuid',@InputRevisionUuid::uuid,'JsonPointer','/fixture'),
                    'Aliases',jsonb_build_array(jsonb_build_object('Wording','Fixture alias')),
                    'Relationships',jsonb_build_array(jsonb_build_object('Relationship','ExplicitAlias')),
                    'Context',jsonb_build_object('Heading','{}'::jsonb,'Qualifiers','[]'::jsonb,'Statement','{}'::jsonb),
                    'Unresolved','[]'::jsonb) AS assertion
            ), scope AS (
                SELECT proof.assertion,jsonb_build_object(
                    'ProducerVersion','fixture-authority-v1','InputRevisionUuid',@InputRevisionUuid::uuid,
                    'Resource',jsonb_build_object('Provider',resource.provider,
                        'ResourceType',resource.resource_type,'ProviderKey',resource.provider_key),
                    'RecordingOrigin','{}'::jsonb,'Extraction',jsonb_build_object(
                        'Assertions',jsonb_build_array(proof.assertion),'Unresolved','[]'::jsonb,'Scopes','[]'::jsonb)) AS original_scope
                FROM proof CROSS JOIN normalization.input_resources resource WHERE resource.uuid=@ResourceUuid
            )
            INSERT INTO normalization.source_actor_alias_assignments(
                uuid,source_uuid,contributor_provider,contributor_type,contributor_key,
                resource_uuid,input_revision_uuid,field_json_pointer,producer_version,
                assertion_uuid,actor_reference_uuid,field_sha256,assertion_sha256,
                original_assertion,original_scope,authority_receipt,person_uuid,
                source_resolution_owner_uuid,extraction_owner_uuid,extraction_attempt_uuid,
                application_attempt_uuid,stage_uuid,basis,introduced_work_uuid)
            SELECT @assignment,@SourceUuid,'fixture','source_actor_aliases','person_metadata',
                @ResourceUuid,@InputRevisionUuid,'/fixture','fixture-authority-v1',
                @assignment,@assignment,repeat('a',64),repeat('b',64),
                scope.assertion,CASE WHEN @malformed THEN '{}'::jsonb ELSE scope.original_scope END,'{}',@person,
                @SourceOwner,@ExtractionOwner,@ExtractionAttempt,
                @assignment,@assignment,'validated_match',@work FROM scope;
            INSERT INTO normalization.person_alias_contributor_support(
                uuid,assignment_uuid,alias_uuid,introduced_work_uuid)
            VALUES(@support,@assignment,@alias,@work);
            """,
            new
            {
                assignment,
                support,
                work,
                person,
                alias,
                malformed,
                source.SourceUuid,
                source.ResourceUuid,
                source.InputRevisionUuid,
                source.SourceOwner,
                source.ExtractionOwner,
                source.ExtractionAttempt,
            },
            transaction
        );

    private sealed record PersonScenario(CanonicalPerson Person, DomainInput Input);

    private sealed record AuthoritySupport(
        Guid AssignmentUuid,
        Guid SupportUuid,
        PreparedSource Source
    );

    private sealed record AuthorityClocks(long PersonRevision, long People, long Compositions);

    internal sealed record PreparedSource(
        Guid SourceUuid,
        Guid SourceOwner,
        Guid ExtractionOwner,
        Guid ExtractionAttempt,
        Guid ResourceUuid,
        Guid InputRevisionUuid
    );
}
