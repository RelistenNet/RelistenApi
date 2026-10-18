using System.Data;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPeopleAcceptance
{
    [Test]
    public async Task SnapshotReaderPreservesEachQueriesActualHitHintsProofAndDiagnostics()
    {
        await ActivatePolicy();
        var contact = RandomWord() + "@example.test";
        var profile = RandomWord();
        var alias = UniqueName();
        var actor = await PublishActorContext(
            $"aka {alias} ({contact}) (https://archive.org/details/@{profile})"
        );
        var person = new SnapshotReaderPerson(actor.PersonUuid, actor.Name);
        var identifier = RandomWord();
        await SnapshotReaderAddIdentifier(person, identifier);
        var queries = new[]
        {
            SnapshotReaderQuery(person.Name),
            SnapshotReaderQuery(alias),
            new NormalizationCandidateQuery(
                IdentityMatchingDomain.People,
                [],
                [new("snapshot-reader", "person", identifier)]
            ),
            new NormalizationCandidateQuery(
                IdentityMatchingDomain.People,
                [],
                [],
                PeopleHints: new([new("email", contact)], [new("archive.org", "account", profile)])
            ),
        };

        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var results = new List<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>>();
                foreach (var query in queries)
                {
                    var result = await ComparePeopleSnapshotReader(c, t, reader, query);
                    result.Facts.Should().ContainSingle().Which.Hit.Uuid.Should().Be(person.Uuid);
                    result
                        .Facts.Single()
                        .ActorMatchingFacts!.Contacts.Should()
                        .Contain(value => value.LookupKey == contact);
                    result
                        .Facts.Single()
                        .ActorMatchingFacts!.Profiles.Should()
                        .Contain(value => value.LookupKey == profile);
                    result
                        .Issues.Should()
                        .Contain(issue => issue.Kind == CandidateFactsIssueKind.UnknownAuthority);
                    results.Add(result);
                }
                results
                    .Select(value => value.QueryHash)
                    .Distinct()
                    .Should()
                    .HaveCount(queries.Length);
                results[0]
                    .Facts.Single()
                    .Hit.Reasons.Should()
                    .NotBeEquivalentTo(results[^1].Facts.Single().Hit.Reasons);
                results[^1]
                    .Facts.Single()
                    .Hit.Reasons.Should()
                    .Contain(["recording_contact", "recording_profile"]);
            }
        );
    }

    [Test]
    public async Task SnapshotReaderRebuildsOverlappingPoolsOwnersArtistsAndEmptyNegativeScope()
    {
        var persons = new[]
        {
            await SnapshotReaderCreatePerson(),
            await SnapshotReaderCreatePerson(),
            await SnapshotReaderCreatePerson(),
        };
        var firstCreditSource = await fixture.Source();
        var lastCreditSource = await fixture.Source();
        await SnapshotReaderPublishCredit(persons[0], firstCreditSource.SourceUuid);
        await SnapshotReaderPublishCredit(persons[2], lastCreditSource.SourceUuid);
        await SnapshotReaderAddIdentifier(persons[1], RandomWord());
        var firstOnly = await candidates.FindPeopleFactsAsync(SnapshotReaderQuery(persons[0].Name));
        var omittedOwners = firstOnly
            .ReadSet.OwnerObservations.Select(value => value.SubjectUuid)
            .ToHashSet();
        var omittedArtists = firstOnly
            .ReadSet.CatalogInputs.Select(value => value.ArtistUuid)
            .ToHashSet();

        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var pq = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery(persons[0].Name, persons[1].Name)
                );
                pq.Facts.Select(value => value.Hit.Uuid)
                    .Should()
                    .BeEquivalentTo([persons[0].Uuid, persons[1].Uuid]);
                var qr = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery(persons[1].Name, persons[2].Name)
                );
                qr.Facts.Select(value => value.Hit.Uuid)
                    .Should()
                    .BeEquivalentTo([persons[1].Uuid, persons[2].Uuid]);
                qr.ReadSet.OwnerObservations.Should()
                    .NotContain(value => omittedOwners.Contains(value.SubjectUuid));
                qr.ReadSet.CatalogInputs.Should()
                    .NotContain(value => omittedArtists.Contains(value.ArtistUuid));
                var empty = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery("No candidate " + Guid.NewGuid().ToString("N"))
                );
                empty.Facts.Should().BeEmpty();
                empty.ReadSet.OwnerObservations.Should().BeEmpty();
                empty.ReadSet.CatalogInputs.Should().BeEmpty();
                empty
                    .ReadSet.CandidateUniverses.Should()
                    .ContainSingle(value =>
                        value.Universe == NormalizationCandidateUniverseKey.People
                    );
            }
        );
    }

    [Test]
    public async Task SnapshotReaderRespectsEveryNormalizedLimitAndNeverUpgradesBoundedFacts()
    {
        await ActivatePolicy();
        var actor = await PublishActorContext($"aka {UniqueName()} ({RandomWord()}@example.test)");
        await PublishActorContext(
            $"aka {UniqueName()} ({RandomWord()}@example.test)",
            actor.PersonUuid,
            actor.Name
        );
        var person = new SnapshotReaderPerson(actor.PersonUuid, actor.Name);
        await SnapshotReaderAddIdentifier(person, RandomWord());
        await SnapshotReaderAddIdentifier(person, RandomWord());
        var limits = new CandidateFactsLimits[]
        {
            new() { MaximumCandidates = 1 },
            new() { MaximumIdentityClosure = 1 },
            new() { MaximumAssertionsPerKind = 1 },
            new() { MaximumActorContexts = 1 },
            new() { MaximumActorEvidenceBytes = 1 },
            new() { MaximumActorMatchingBytes = 256 },
            new() { MaximumAliasMatchingNames = 1 },
            new() { MaximumAliasMatchingBytes = 1 },
            new() { MaximumAliasEvidenceBytes = 1 },
            new() { MaximumEvidencePerAssertion = 1 },
            new() { MaximumArtistContexts = 1 },
            new() { MaximumRoleBasisGroupsPerArtist = 1 },
            new() { MaximumWitnessesPerArtist = 0 },
            new() { MaximumMissingContextWitnesses = 0 },
            new() { MaximumParentClosure = 1 },
        };

        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var query = SnapshotReaderQuery(actor.Name);
                var baseline = await ComparePeopleSnapshotReader(c, t, reader, query);
                var hashes = new HashSet<string> { baseline.QueryHash };
                foreach (var limit in limits)
                {
                    var bounded = await ComparePeopleSnapshotReader(c, t, reader, query, limit);
                    hashes
                        .Add(bounded.QueryHash)
                        .Should()
                        .BeTrue("each normalized limit tuple belongs to its own query proof");
                    await ComparePeopleSnapshotReader(c, t, reader, query, limit);
                }
                var omitted = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    query,
                    new() { MaximumActorEvidenceBytes = 1, MaximumWitnessesPerArtist = 0 }
                );
                omitted.Facts.Single().ActorEvidence.Should().BeEmpty();
                omitted.Facts.Single().ActorEvidenceTruncated.Should().BeTrue();
                omitted
                    .ReadSet.OwnerObservations.Should()
                    .Contain(value =>
                        value.SubjectUuid
                        == baseline.Facts.Single().ActorEvidence!.First().OwnerSubjectUuid
                    );
            }
        );
    }

    [Test]
    public async Task SnapshotReaderKeepsOriginalReviewVisibilityUntilANewReadOnlySnapshot()
    {
        var person = await SnapshotReaderCreatePerson();
        var alias = UniqueName();
        var assertion = await SnapshotReaderPublishAlias(person, alias);
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>? before = null;
        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                before = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery(person.Name)
                );
                var owner = (
                    await fixture.Work.GetSubjectAsync(
                        new NormalizationSubjectKey("people", "person", person.Uuid)
                    )
                )!;
                var aliasRevision = before
                    .Facts.Single()
                    .Aliases.Single(value => value.Provenance.AssertionUuid == assertion.Uuid)
                    .Provenance.Revision;
                await people.ApplyMetadataDecisionAsync(
                    new(
                        Guid.NewGuid(),
                        owner.SubjectUuid,
                        owner.EditorialRevision,
                        person.Uuid,
                        before.Facts.Single().Header.Revision,
                        "reject",
                        "Snapshot reader must retain its original view",
                        Alias: assertion,
                        ExpectedAssertionRevision: aliasRevision
                    ),
                    Operator
                );

                var original = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery(person.Name)
                );
                SnapshotReaderValuesEqual(before, original);
                (await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(alias)))
                    .Facts.Should()
                    .ContainSingle();
            }
        );

        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var current = await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery(person.Name)
                );
                current.Facts.Single().AcceptedAliasMatchingNames.Should().NotContain(alias);
                current
                    .ReadSet.CandidateUniverses.Single()
                    .Generation.Should()
                    .BeGreaterThan(before!.ReadSet.CandidateUniverses.Single().Generation);
                var personOwner = before.Facts.Single().Header.OwnerSubjectUuid;
                current
                    .ReadSet.OwnerObservations.Single(value => value.SubjectUuid == personOwner)
                    .EditorialRevision.Should()
                    .BeGreaterThan(
                        before
                            .ReadSet.OwnerObservations.Single(value =>
                                value.SubjectUuid == personOwner
                            )
                            .EditorialRevision
                    );
                (await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(alias)))
                    .Facts.Should()
                    .BeEmpty();
            }
        );
    }

    [TestCase(IsolationLevel.RepeatableRead, false)]
    [TestCase(IsolationLevel.ReadCommitted, true)]
    [TestCase(IsolationLevel.Serializable, true)]
    public async Task SnapshotReaderFactoryRequiresTheActualReadOnlyRepeatableReadTransaction(
        IsolationLevel isolation,
        bool readOnly
    )
    {
        await using var c = fixture.Db.CreateConnection(false, false);
        await c.OpenAsync();
        await using var t = await c.BeginTransactionAsync(isolation);
        if (readOnly)
        {
            await c.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
        }

        var create = () => candidates.CreatePeopleFactsSnapshotReaderAsync(c, t);

        await create.Should().ThrowAsync<ArgumentException>();
        await t.RollbackAsync();
    }

    [Test]
    public async Task SnapshotReaderFactoryRejectsAConnectionOtherThanItsTransactionsOwner()
    {
        await using var owner = fixture.Db.CreateConnection(false, false);
        await using var other = fixture.Db.CreateConnection(false, false);
        await owner.OpenAsync();
        await other.OpenAsync();
        await using var t = await owner.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await owner.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
        var create = () => candidates.CreatePeopleFactsSnapshotReaderAsync(other, t);

        await create.Should().ThrowAsync<ArgumentException>();
        await t.RollbackAsync();
    }

    [TestCase("commit")]
    [TestCase("rollback")]
    [TestCase("dispose_transaction")]
    [TestCase("dispose_reader")]
    public async Task SnapshotReaderCannotOutliveItsExactTransactionOrItsOwnDisposal(string finish)
    {
        var person = await SnapshotReaderCreatePerson();
        await using var c = fixture.Db.CreateConnection(false, false);
        await c.OpenAsync();
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await c.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
        using var reader = await candidates.CreatePeopleFactsSnapshotReaderAsync(c, t);
        await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(person.Name));
        switch (finish)
        {
            case "commit":
                await t.CommitAsync();
                break;
            case "rollback":
                await t.RollbackAsync();
                break;
            case "dispose_transaction":
                await t.DisposeAsync();
                break;
            case "dispose_reader":
                reader.Dispose();
                break;
        }
        var read = () => reader.FindAsync(SnapshotReaderQuery(person.Name));

        if (finish == "dispose_reader")
        {
            await read.Should().ThrowAsync<ObjectDisposedException>();
            await t.RollbackAsync();
        }
        else
        {
            await read.Should().ThrowAsync<InvalidOperationException>();
            var recreate = () => candidates.CreatePeopleFactsSnapshotReaderAsync(c, t);
            await recreate.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Test]
    public async Task SnapshotReaderEvictionAndLimitChangesReacquireBodiesWithoutChangingResults()
    {
        var first = await SnapshotReaderCreatePerson();
        var second = await SnapshotReaderCreatePerson();
        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(first.Name));
                var warm = reader.BodyAcquisitionAttempts;
                await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(first.Name));
                reader.BodyAcquisitionAttempts.Should().Be(warm);
                await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(second.Name));
                reader.BodyAcquisitionAttempts.Should().Be(warm + 1);
                await ComparePeopleSnapshotReader(c, t, reader, SnapshotReaderQuery(first.Name));
                reader.BodyAcquisitionAttempts.Should().Be(warm + 2);
                await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    SnapshotReaderQuery(first.Name),
                    new() { MaximumAssertionsPerKind = 1 }
                );
                reader.BodyAcquisitionAttempts.Should().Be(warm + 3);
            }
        );
    }

    [Test]
    public async Task SnapshotReaderClosureOverflowUsesNormalReadsAndPreservesTheBlockingDiagnostic()
    {
        var original = await SnapshotReaderCreatePerson();
        var survivor = await SnapshotReaderCreatePerson();
        var old = await candidates.FindPeopleFactsAsync(SnapshotReaderQuery(original.Name));
        var target = await candidates.FindPeopleFactsAsync(SnapshotReaderQuery(survivor.Name));
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Uuid)
            )
        )!;
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "merge",
                original.Uuid,
                old.Facts.Single().Header.Revision,
                new(survivor.Uuid, survivor.Name),
                target.Facts.Single().Header.Revision,
                [],
                "Exercise the actual reverse identity closure"
            ),
            Operator
        );
        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var query = SnapshotReaderQuery(survivor.Name);
                var limits = new CandidateFactsLimits { MaximumIdentityClosure = 1 };
                var first = await ComparePeopleSnapshotReader(c, t, reader, query, limits);
                first.Complete.Should().BeFalse();
                first
                    .Issues.Should()
                    .Contain(value => value.Kind == CandidateFactsIssueKind.IdentityClosureLimit);
                var attempts = reader.BodyAcquisitionAttempts;
                await ComparePeopleSnapshotReader(c, t, reader, query, limits);
                reader.BodyAcquisitionAttempts.Should().Be(attempts + 1);
            }
        );
    }

    [Test]
    public async Task SnapshotReaderCancellationDoesNotReturnOrReplaceAWarmBody()
    {
        var person = await SnapshotReaderCreatePerson();
        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var query = SnapshotReaderQuery(person.Name);
                var original = await ComparePeopleSnapshotReader(c, t, reader, query);
                var attempts = reader.BodyAcquisitionAttempts;
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var cancelled = () => reader.FindAsync(query, ct: cancellation.Token);
                await cancelled.Should().ThrowAsync<OperationCanceledException>();
                reader.BodyAcquisitionAttempts.Should().Be(attempts);
                SnapshotReaderValuesEqual(
                    original,
                    await ComparePeopleSnapshotReader(c, t, reader, query)
                );
            }
        );
    }

    [TestCase("owner")]
    [TestCase("catalog")]
    [TestCase("actor_hash")]
    public async Task SnapshotReaderRetainsMissingAndMalformedProofDiagnosticsWithoutInventingAuthority(
        string fault
    )
    {
        SnapshotReaderPerson person;
        Guid sourceUuid;
        if (fault == "catalog")
        {
            // Ordinary public credit publication does not bind an extraction Work row to
            // this artist clock. Its absence can therefore be faulted without deleting FK
            // dependencies or inventing an accepted credit that lacks an original owner.
            person = await SnapshotReaderCreatePerson();
            var source = await fixture.Source();
            sourceUuid = source.SourceUuid;
            await SnapshotReaderPublishCredit(person, sourceUuid);
        }
        else
        {
            await ActivatePolicy();
            var actor = await PublishActorContext($"({RandomWord()}@example.test)");
            person = new(actor.PersonUuid, actor.Name);
            sourceUuid = actor.Source.SourceUuid;
        }
        var query = SnapshotReaderQuery(person.Name);
        var original = await candidates.FindPeopleFactsAsync(query);
        var actorOwner = original.Facts.Single().ActorEvidence!.FirstOrDefault()?.OwnerSubjectUuid;
        var artist = original.Facts.Single().ArtistContexts.Single().ArtistUuid;
        await using var c = fixture.Db.CreateConnection(false, false);
        await c.OpenAsync();
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        try
        {
            // Only genuine public accepted data exists before this privileged fault injection.
            // Switch to enforced read-only mode before constructing the reader; rollback
            // restores every original row, clock and immutable guard after the checks.
            if (fault == "owner")
            {
                await c.ExecuteAsync(
                    "UPDATE normalization.work_subjects SET subject_kind='snapshot_reader_missing_owner' WHERE uuid=@actorOwner",
                    new { actorOwner },
                    t
                );
            }
            else if (fault == "catalog")
            {
                await c.ExecuteAsync(
                    "DELETE FROM normalization.catalog_artist_revisions WHERE artist_uuid=@artist",
                    new { artist },
                    t
                );
            }
            else
            {
                await c.ExecuteAsync(
                    "ALTER TABLE normalization.recording_actor_contexts DISABLE TRIGGER recording_actor_context_retained",
                    transaction: t
                );
                await c.ExecuteAsync(
                    "UPDATE normalization.recording_actor_contexts SET facts_hash=repeat('0',64) WHERE person_uuid=@PersonUuid AND source_uuid=@SourceUuid",
                    new { PersonUuid = person.Uuid, SourceUuid = sourceUuid },
                    t
                );
                await c.ExecuteAsync(
                    "ALTER TABLE normalization.recording_actor_contexts ENABLE TRIGGER recording_actor_context_retained",
                    transaction: t
                );
            }
            await c.ExecuteAsync("SET TRANSACTION READ ONLY", transaction: t);
            using var reader = await candidates.CreatePeopleFactsSnapshotReaderAsync(c, t);
            var expectedIssue =
                fault == "owner" ? CandidateFactsIssueKind.MissingOwner
                : fault == "catalog" ? CandidateFactsIssueKind.MissingCatalogClock
                : CandidateFactsIssueKind.InvalidStoredFact;
            var first = await ComparePeopleSnapshotReader(c, t, reader, query);
            first.Complete.Should().BeFalse();
            first.Issues.Should().Contain(value => value.Kind == expectedIssue);
            var repeated = await ComparePeopleSnapshotReader(
                c,
                t,
                reader,
                query with
                {
                    NameLimit = 2,
                }
            );
            repeated.Issues.Should().Equal(first.Issues);
        }
        finally
        {
            await t.RollbackAsync();
        }
        SnapshotReaderValuesEqual(original, await candidates.FindPeopleFactsAsync(query));
    }

    [Test, Category("PeopleFactsSnapshotScale"), CancelAfter(600_000)]
    public async Task SnapshotReaderOversizedRealCreditCitationBypassesRetentionWithoutTruncatingItsSnapshot()
    {
        var ct = TestContext.CurrentContext.CancellationToken;
        var person = await SnapshotReaderCreatePerson();
        var source = await fixture.Source();
        var text = person.Name + " " + new string('x', 33 * 1024 * 1024);
        var resource = new NormalizationResourceKey(
            "test",
            "snapshot-reader",
            Guid.NewGuid().ToString()
        );
        var capture = await new NormalizationEvidenceService(fixture.Db).CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(new { text }),
                "application/json",
                "test",
                0,
                DateTimeOffset.UtcNow
            ),
            ct
        );
        var owner = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", person.Uuid),
                ct
            )
        )!;
        var ticket = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "recording", source.SourceUuid),
            "policy-1",
            [resource],
            [owner.SubjectUuid],
            cancellationToken: ct
        );
        var evidence = new NormalizedEvidenceReference(
            capture.Revision.Uuid,
            "/text",
            0,
            text.Length,
            text
        );
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source.SourceUuid,
            "taper",
            person.Name,
            person.Uuid,
            null,
            [evidence]
        );
        (await people.PublishAsync(ticket.WorkUuid, new([], [], [], [credit]), ct))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);

        await WithPeopleSnapshotReader(
            async (c, t, reader) =>
            {
                var query = SnapshotReaderQuery(person.Name);
                var first = await ComparePeopleSnapshotReader(c, t, reader, query, ct: ct);
                first
                    .Facts.Single()
                    .ArtistContexts.Single()
                    .Witnesses.Single()
                    .Provenance.Evidence.Single()
                    .Quote.Should()
                    .Be(text);
                var attempts = reader.BodyAcquisitionAttempts;
                await ComparePeopleSnapshotReader(
                    c,
                    t,
                    reader,
                    query with
                    {
                        NameLimit = 2,
                    },
                    ct: ct
                );
                reader
                    .BodyAcquisitionAttempts.Should()
                    .Be(
                        attempts + 1,
                        "a body larger than the reader's retention budget must take the ordinary full path again"
                    );
            },
            ct
        );
    }

    private async Task WithPeopleSnapshotReader(
        Func<
            NpgsqlConnection,
            NpgsqlTransaction,
            NormalizationCandidateService.PeopleFactsSnapshotReader,
            Task
        > check,
        CancellationToken ct = default
    )
    {
        await using var c = fixture.Db.CreateConnection(false, false);
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await c.ExecuteAsync(
            new CommandDefinition(
                "SET TRANSACTION READ ONLY",
                transaction: t,
                cancellationToken: ct
            )
        );
        using var reader = await candidates.CreatePeopleFactsSnapshotReaderAsync(c, t, ct);
        await check(c, t, reader);
        await t.CommitAsync(ct);
    }

    private async Task<
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts>
    > ComparePeopleSnapshotReader(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        NormalizationCandidateService.PeopleFactsSnapshotReader reader,
        NormalizationCandidateQuery query,
        CandidateFactsLimits? limits = null,
        CancellationToken ct = default
    )
    {
        var expected = await candidates.FindPeopleFactsInSnapshotAsync(c, t, query, limits, ct);
        var actual = await reader.FindAsync(query, limits, ct);
        SnapshotReaderValuesEqual(expected, actual);
        return actual;
    }

    private static void SnapshotReaderValuesEqual<T>(T expected, T actual) =>
        JsonElement
            .DeepEquals(
                JsonSerializer.SerializeToElement(expected),
                JsonSerializer.SerializeToElement(actual)
            )
            .Should()
            .BeTrue();

    private async Task<SnapshotReaderPerson> SnapshotReaderCreatePerson()
    {
        var name = UniqueName();
        return new(await Person(name), name);
    }

    private async Task<PersonAlias> SnapshotReaderPublishAlias(
        SnapshotReaderPerson person,
        string wording
    )
    {
        var input = await fixture.Capture(wording, "people");
        var alias = new PersonAlias(Guid.NewGuid(), person.Uuid, wording, input.Reference);
        (await fixture.PublishPeople(people, input, new([], [alias], [], [])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        return alias;
    }

    private async Task SnapshotReaderAddIdentifier(SnapshotReaderPerson person, string key)
    {
        var input = await fixture.Capture(person.Name, "people");
        (
            await fixture.PublishPeople(
                people,
                input,
                new(
                    [],
                    [],
                    [new(person.Uuid, "snapshot-reader", "person", key, input.Reference)],
                    []
                )
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private async Task SnapshotReaderPublishCredit(SnapshotReaderPerson person, Guid sourceUuid)
    {
        var input = await fixture.Capture(person.Name, "people");
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            sourceUuid,
            "taper",
            person.Name,
            person.Uuid,
            null,
            [input.Reference]
        );
        (await fixture.PublishPeople(people, input, new([], [], [], [credit])))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private static NormalizationCandidateQuery SnapshotReaderQuery(params string[] names) =>
        new(IdentityMatchingDomain.People, names, [], MinimumNameSimilarity: .99);

    private sealed record SnapshotReaderPerson(Guid Uuid, string Name);
}
