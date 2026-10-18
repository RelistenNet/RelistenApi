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

public sealed partial class TestNormalizationSourceActorAliasUpgrade
{
    [Test]
    public async Task Nonempty53And54HistoryKeepsOriginalAuthorityWhen58ChangesAliasVisibility()
    {
        if (Environment.GetEnvironmentVariable("RELISTEN_LOCAL_INGESTION_TESTS") != "1")
        {
            Assert.Ignore("Run ./test-local.sh to include disposable local database coverage.");
        }

        var database = "relisten_alias_upgrade_" + Guid.NewGuid().ToString("N");
        var previousWrite = DbService.ConnStr;
        var previousRead = DbService.ReadOnlyConnStr;
        var created = false;
        try
        {
            await CreateSchema52(database);
            created = true;
            var environment = Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
            ).Environment;
            var db = new DbService(
                $"postgresql://relisten:local_dev_password@127.0.0.1:15432/{database}",
                null,
                null,
                environment,
                NullLogger<DbService>.Instance
            );
            var work = new NormalizationWorkService(db);
            var evidence = new NormalizationEvidenceService(db);
            var people = new NormalizedPeopleService(db, work);
            var legacy = await SeedHistoricalAliases(db, work, evidence);
            await ApplyActual53(db);

            // These are labeled legacy migration rows, not invented human decisions. Use an
            // actual public publication for the second original before changing topology.
            var original = await PublishVisibilityUpgradeOriginal(
                db,
                work,
                evidence,
                people,
                legacy.Aliases[0].Wording
            );
            var before54 = await VisibilityUpgradeImage(db);
            await ApplyActual54(db);
            (await VisibilityUpgradeImage(db)).Should().Be(before54);

            await MergeVisibilityUpgradeOriginal(
                db,
                work,
                people,
                original.Person,
                legacy.PersonUuid
            );
            var owner = (await work.GetSubjectAsync(legacy.Owner))!;
            var alias = legacy.Aliases[0];
            var rejected = new PersonAlias(
                alias.Uuid,
                legacy.PersonUuid,
                alias.Wording,
                alias.Evidence
            );
            await people.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    legacy.PersonUuid,
                    await VisibilityUpgradeRevision(db, legacy.PersonUuid),
                    "reject",
                    "Confirm withdrawal after merging the two preexisting alias claims",
                    Alias: rejected,
                    ExpectedAssertionRevision: alias.Revision
                ),
                "fixture-operator"
            );

            // The old view exposes the other original's accepted row. This is the actual
            // nonempty upgrade discrepancy; the insertion guard alone cannot remove it.
            (await people.FindCandidatesAsync(alias.Wording))
                .Should()
                .ContainSingle();
            var candidates = new NormalizationCandidateService(db);
            var beforeFacts = await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, ["UpgradeFixturePerson"], [])
            );
            beforeFacts
                .Facts.Single(fact => fact.Hit.Uuid == legacy.PersonUuid)
                .AcceptedAliasMatchingNames.Should()
                .Contain(alias.Wording);

            // Keep real migration order. No source application is fabricated just to create
            // a nonempty schema; the existing canonical rows and receipts remain the fixture.
            await ApplyVisibilityUpgradeSchema(db, Migrations.AddSourceActorResolution.SchemaSql);
            await ApplyVisibilityUpgradeSchema(
                db,
                Migrations.AddSourceActorReviewPreparations.SchemaSql
            );
            await ApplyVisibilityUpgradeSchema(
                db,
                Migrations.AddSourceActorAliasPublication.SchemaSql
            );
            var probe = await work.BeginWorkAsync(
                Guid.NewGuid(),
                new("people", "alias_visibility_upgrade_probe", Guid.NewGuid()),
                "alias-visibility-upgrade-v1",
                [],
                [],
                candidateUniverses: beforeFacts.ReadSet.CandidateUniverses,
                expectedOwnerObservations: beforeFacts.ReadSet.OwnerObservations,
                catalogInputs: beforeFacts.ReadSet.CatalogInputs
            );
            var before58 = await VisibilityUpgradeImage(db);
            var epochs = await VisibilityUpgradeEpochs(db);

            await ApplyVisibilityUpgradeSchema(
                db,
                Migrations.AddCanonicalAliasReviewVisibility.SchemaSql
            );

            (await VisibilityUpgradeImage(db))
                .Should()
                .Be(
                    before58,
                    "a derived visibility change preserves all original canonical rows, support and review history"
                );
            var afterEpochs = await VisibilityUpgradeEpochs(db);
            afterEpochs["people"].Should().Be(epochs["people"] + 1);
            afterEpochs["compositions"].Should().Be(epochs["compositions"] + 1);
            afterEpochs["venues"].Should().Be(epochs["venues"]);
            (await people.FindCandidatesAsync(alias.Wording)).Should().BeEmpty();
            await AssertVisibilityUpgradeAliasRows(db, alias.Wording, expected: 0);
            var afterFacts = await candidates.FindPeopleFactsAsync(
                new(IdentityMatchingDomain.People, ["UpgradeFixturePerson"], [])
            );
            var person = afterFacts.Facts.Single(fact => fact.Hit.Uuid == legacy.PersonUuid);
            person.AcceptedAliasMatchingNames.Should().NotContain(alias.Wording);
            person
                .AcceptedAliasMatchingNames.Should()
                .Contain(
                    legacy.Aliases[2].Wording,
                    "a current reviewed acceptance overrides that same original's retained automatic history"
                );
            var originalOwner = (
                await work.GetSubjectAsync(
                    new NormalizationSubjectKey("people", "person", original.Person)
                )
            )!;
            afterFacts
                .ReadSet.OwnerObservations.Select(value => value.SubjectUuid)
                .Should()
                .Contain([owner.SubjectUuid, originalOwner.SubjectUuid]);
            var callbackCalled = false;
            var publication = await work.TryPublishAsync(
                probe.WorkUuid,
                (_, _, _) =>
                {
                    callbackCalled = true;
                    return Task.CompletedTask;
                }
            );
            publication.Status.Should().Be(NormalizationPublicationStatus.Stale);
            callbackCalled
                .Should()
                .BeFalse("58 must invalidate a real matching proof from the old view");

            // Accepting a new UUID does not rewrite the earlier rejected row. Its explicit
            // same-original acceptance restores visibility through the derived verdict.
            owner = (await work.GetSubjectAsync(legacy.Owner))!;
            await people.ApplyMetadataDecisionAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    legacy.PersonUuid,
                    await VisibilityUpgradeRevision(db, legacy.PersonUuid),
                    "accept",
                    "Restore this original's canonical name explicitly",
                    Alias: rejected with
                    {
                        Uuid = Guid.NewGuid(),
                    },
                    ExpectedAssertionRevision: 0
                ),
                "fixture-operator"
            );
            (await people.FindCandidatesAsync(alias.Wording))
                .Should()
                .ContainSingle()
                .Which.Uuid.Should()
                .Be(legacy.PersonUuid);
            var retained = await db.WithWriteConnection(c =>
                c.QuerySingleAsync<string>(
                    "SELECT state FROM normalization.person_aliases WHERE uuid=@Uuid",
                    rejected
                )
            );
            retained.Should().Be("rejected");
            var originalAlias = await db.WithWriteConnection(c =>
                c.QuerySingleAsync<Guid>(
                    "SELECT person_uuid FROM normalization.person_aliases WHERE uuid=@Alias",
                    new { original.Alias }
                )
            );
            originalAlias.Should().Be(original.Person);
        }
        finally
        {
            DbService.ConnStr = previousWrite;
            DbService.ReadOnlyConnStr = previousRead;
            if (created)
            {
                await DropOwnedDatabase(database);
            }
        }
    }

    private static async Task<(Guid Person, Guid Alias)> PublishVisibilityUpgradeOriginal(
        DbService db,
        NormalizationWorkService work,
        NormalizationEvidenceService evidence,
        NormalizedPeopleService people,
        string alias
    )
    {
        var person = new CanonicalPerson(Guid.NewGuid(), "Independent Upgrade Original");
        var resource = new NormalizationResourceKey(
            "test",
            "alias-visibility-upgrade",
            person.Uuid.ToString("D")
        );
        var input = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { display = person.DisplayName, alias }),
                "application/json",
                "visibility-upgrade-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var ticket = await work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", person.Uuid),
            "visibility-upgrade-v1",
            [resource],
            []
        );
        var assertion = Guid.NewGuid();
        await people.PublishAsync(
            ticket.WorkUuid,
            new(
                [person],
                [
                    new(
                        Guid.NewGuid(),
                        person.Uuid,
                        person.DisplayName,
                        new(
                            input.Revision.Uuid,
                            "/display",
                            0,
                            person.DisplayName.Length,
                            person.DisplayName
                        )
                    ),
                    new(
                        assertion,
                        person.Uuid,
                        alias,
                        new(input.Revision.Uuid, "/alias", 0, alias.Length, alias)
                    ),
                ],
                [],
                []
            )
        );
        return (person.Uuid, assertion);
    }

    private static async Task MergeVisibilityUpgradeOriginal(
        DbService db,
        NormalizationWorkService work,
        NormalizedPeopleService people,
        Guid original,
        Guid survivor
    )
    {
        var owner = (
            await work.GetSubjectAsync(new NormalizationSubjectKey("people", "person", original))
        )!;
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original,
                await VisibilityUpgradeRevision(db, original),
                new(survivor, "UpgradeFixturePerson"),
                await VisibilityUpgradeRevision(db, survivor),
                [],
                "These upgrade fixture originals describe one identity"
            ),
            "fixture-operator"
        );
    }

    private static Task<long> VisibilityUpgradeRevision(DbService db, Guid person) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@person",
                new { person }
            )
        );

    private static Task<string> VisibilityUpgradeImage(DbService db) =>
        db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object(
                    'people',(SELECT jsonb_agg(to_jsonb(p) ORDER BY uuid) FROM normalization.people p),
                    'aliases',(SELECT jsonb_agg(to_jsonb(a) ORDER BY uuid) FROM normalization.person_aliases a),
                    'history',(SELECT jsonb_agg(to_jsonb(h) ORDER BY uuid,revision) FROM normalization.person_aliases_history h),
                    'support',(SELECT jsonb_agg(to_jsonb(s) ORDER BY alias_uuid) FROM normalization.person_alias_unmanaged_support s),
                    'redirects',(SELECT jsonb_agg(to_jsonb(r) ORDER BY retired_uuid) FROM normalization.person_redirects r),
                    'owners',(SELECT jsonb_agg(to_jsonb(o) ORDER BY uuid) FROM normalization.work_subjects o),
                    'reviews',(SELECT jsonb_agg(to_jsonb(r) ORDER BY uuid) FROM normalization.review_operations r)
                )::text
                """
            )
        );

    private static async Task<Dictionary<string, long>> VisibilityUpgradeEpochs(DbService db) =>
        (
            await db.WithWriteConnection(c =>
                c.QueryAsync<(string Domain, long Generation)>(
                    """
                    SELECT domain,generation FROM normalization.candidate_universe_revisions
                    WHERE scope_uuid='00000000-0000-0000-0000-000000000000'::uuid ORDER BY domain
                    """
                )
            )
        ).ToDictionary(value => value.Domain, value => value.Generation);

    private static Task ApplyVisibilityUpgradeSchema(DbService db, string sql) =>
        db.WithWriteConnection(async connection =>
        {
            await using var transaction = await (
                (NpgsqlConnection)connection
            ).BeginTransactionAsync();
            await connection.ExecuteAsync(sql, transaction: transaction, commandTimeout: 120);
            await transaction.CommitAsync();
        });

    private static async Task AssertVisibilityUpgradeAliasRows(
        DbService db,
        string wording,
        int expected
    )
    {
        var counts = await db.WithWriteConnection(c =>
            c.QuerySingleAsync<(int Current, int Resolved)>(
                """
                SELECT (SELECT count(*)::integer FROM normalization.current_person_aliases WHERE wording=@wording),
                    (SELECT count(*)::integer FROM normalization.resolved_person_aliases WHERE wording=@wording)
                """,
                new { wording }
            )
        );
        counts.Current.Should().Be(expected);
        counts.Resolved.Should().Be(expected);
    }
}
