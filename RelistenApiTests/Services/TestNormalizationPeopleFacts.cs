using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>
/// Candidate facts retain original assertion ownership and exact cross-artist credit context.
/// The snapshot is a matching input, not the playable-recording display projection.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationPeopleFacts
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationCandidateService candidates = null!;

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
    public async Task RedirectClosureRetainsSearchableOriginalHeadersAndFullAssertionProvenance()
    {
        var original = await Person();
        var intermediate = await Person();
        var survivor = await Person();
        var providerKey = "@same-looking-account-" + Guid.NewGuid();
        await AddIdentifier(original, providerKey);
        await AddIdentifier(survivor, providerKey + "-other", "second-provider");
        await Merge(original, intermediate);
        await Merge(intermediate, survivor);

        // The current original header is independently searchable. A reviewed display edit
        // does not imply an alias assertion for that wording or citation for the header.
        var correctedHeader = "Corrected Person " + Guid.NewGuid().ToString("N");
        await CorrectDisplay(original, correctedHeader);
        var snapshot = await candidates.FindPeopleFactsAsync(Query(correctedHeader));
        var fact = snapshot.Facts.Single(item => item.Header.Uuid == survivor.Person.Uuid);

        snapshot.Complete.Should().BeTrue();
        fact.IdentityClosure.Select(header => header.Uuid)
            .Should()
            .BeEquivalentTo(
                new[] { original.Person.Uuid, intermediate.Person.Uuid, survivor.Person.Uuid }
            );
        fact.IdentityClosure.Single(header => header.Uuid == original.Person.Uuid)
            .Should()
            .Match<CandidateIdentityHeader>(header =>
                header.DisplayText == correctedHeader && header.MetadataReviewed == true
            );
        fact.Aliases.Should().NotContain(alias => alias.Wording == correctedHeader);
        fact.Redirects.Should()
            .Contain(edge =>
                edge.OriginalUuid == original.Person.Uuid
                && edge.SurvivorUuid == intermediate.Person.Uuid
            );
        fact.Redirects.Should()
            .Contain(edge =>
                edge.OriginalUuid == intermediate.Person.Uuid
                && edge.SurvivorUuid == survivor.Person.Uuid
            );
        var alias = fact.Aliases.Single(item =>
            item.Provenance.AssertionUuid == original.Alias.Uuid
        );
        alias.Provenance.StoredIdentityUuid.Should().Be(original.Person.Uuid);
        alias
            .Provenance.OwnerSubjectUuid.Should()
            .Be(await fixture.OwnerUuid("people", "person", original.Person.Uuid));
        alias.Provenance.Revision.Should().Be(1);
        alias.Provenance.Basis.Should().Be("provider_assertion");
        alias.Provenance.Evidence.Should().Equal(original.Input.Reference);
        fact.ExternalIdentities.Select(identity => identity.ProviderKey)
            .Should()
            .BeEquivalentTo(new[] { providerKey, providerKey + "-other" });
        fact.ExternalIdentities.Should()
            .OnlyContain(identity => identity.Provenance.Evidence.Count == 1);
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.UnknownAuthority);
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .BeEquivalentTo(fact.IdentityClosure.Select(header => header.OwnerSubjectUuid!.Value));
    }

    [Test]
    public async Task CreditsAcrossArtistsIncludeUnplayableRecordingsAndMissingSources()
    {
        var person = await Person();
        var first = await fixture.Source();
        var second = await fixture.Source();
        var orphanSource = await fixture.Source();
        var missing = orphanSource.SourceUuid;
        var taper = await Credit(person, first.SourceUuid, "taper");
        var transferInput = await fixture.Capture(
            "Tape transfer by " + person.Person.DisplayName,
            "people"
        );
        var transfer = await Credit(
            person,
            second.SourceUuid,
            "transferrer",
            second.TrackUuid,
            transferInput
        );
        var orphan = await Credit(person, missing, "collector");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@SourceId", orphanSource)
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET mp3_url=NULL,flac_url=@flac WHERE source_id=ANY(@sources)",
                new
                {
                    flac = "https://example.test/raw.flac",
                    sources = new[] { first.SourceId, second.SourceId },
                }
            )
        );

        var snapshot = await Read(person);
        var fact = snapshot.Facts.Single();
        var expectedArtists = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT uuid FROM artists WHERE id=ANY(@ids)",
                new { ids = new[] { first.ArtistId, second.ArtistId } }
            )
        );

        snapshot.Complete.Should().BeTrue();
        fact.ArtistContexts.Select(artist => artist.ArtistUuid)
            .Should()
            .BeEquivalentTo(expectedArtists);
        fact.ArtistContexts.Should()
            .OnlyContain(artist => artist.AcceptedCreditCount == 1 && !artist.WitnessesTruncated);
        fact.ArtistContexts.SelectMany(artist => artist.Witnesses)
            .Select(witness => witness.Provenance.AssertionUuid)
            .Should()
            .BeEquivalentTo(new[] { taper.Uuid, transfer.Uuid });
        fact.ArtistContexts.SelectMany(artist => artist.Witnesses)
            .Single(witness => witness.Role == "transferrer")
            .ScopeTrackUuid.Should()
            .Be(second.TrackUuid);
        fact.ArtistContexts.SelectMany(artist => artist.Witnesses)
            .Single(witness => witness.Provenance.AssertionUuid == taper.Uuid)
            .Provenance.Evidence.Should()
            .Equal(person.Input.Reference);
        fact.ArtistContexts.SelectMany(artist => artist.Witnesses)
            .Single(witness => witness.Provenance.AssertionUuid == transfer.Uuid)
            .Provenance.Evidence.Should()
            .Equal(transferInput.Reference);
        fact.MissingSourceCreditCount.Should().Be(1);
        fact.MissingArtistCreditCount.Should().Be(0);
        fact.MissingContextWitnesses.Single()
            .Should()
            .Match<RecordingCreditWitness>(witness =>
                witness.SourceUuid == missing
                && witness.ContextStatus == CandidateCreditContextStatus.MissingSource
                && witness.Provenance.AssertionUuid == orphan.Uuid
                && witness.ArtistUuid == null
            );
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.MissingCatalogContext
                && !issue.PreventsAutomaticAcceptance
            );
        snapshot
            .ReadSet.CatalogInputs.Select(input => input.ArtistUuid)
            .Should()
            .BeEquivalentTo(expectedArtists);
        snapshot.ReadSet.OwnerObservations.Should().HaveCount(4);
    }

    [TestCase("reject")]
    [TestCase("retract")]
    public async Task WithdrawnCreditsDoNotContributeArtistMembership(string action)
    {
        var person = await Person();
        var source = await fixture.Source();
        var credit = await Credit(person, source.SourceUuid, "taper");
        var state = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "recording", source.SourceUuid)
            )
        )!;
        await people.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                state.SubjectUuid,
                state.EditorialRevision,
                1,
                action,
                credit,
                "Withdraw the unsupported credit"
            ),
            "operator"
        );

        var snapshot = await Read(person);

        snapshot.Complete.Should().BeTrue();
        snapshot.Facts.Single().ArtistContexts.Should().BeEmpty();
        snapshot.Facts.Single().MissingContextWitnesses.Should().BeEmpty();
        snapshot.ReadSet.OwnerObservations.Should().HaveCount(1);
        snapshot.ReadSet.CatalogInputs.Should().BeEmpty();
    }

    [Test]
    public async Task PopularPersonKeepsExactArtistMembershipWhenWitnessesAreBounded()
    {
        var person = await Person();
        var source = await fixture.Source();
        await SeedCreditCohort(person, source, 513);
        // Older runs could retain duplicate exact citation rows. They are one supporting
        // locator, while a different tuple must remain a separate citation.
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.recording_credit_evidence SELECT evidence.* FROM normalization.recording_credit_evidence evidence JOIN normalization.recording_credits credit ON credit.uuid=evidence.credit_uuid WHERE credit.person_uuid=@Uuid",
                person.Person
            )
        );
        var snapshot = await Read(
            person,
            new() { MaximumWitnessesPerArtist = 2, MaximumEvidencePerAssertion = 1 }
        );
        var artist = snapshot.Facts.Single().ArtistContexts.Single();

        snapshot.Complete.Should().BeTrue();
        artist.AcceptedCreditCount.Should().Be(513);
        artist
            .Counts.Should()
            .BeEquivalentTo(
                new[]
                {
                    new CreditRoleBasisCount("taper", "provider_assertion", 257),
                    new CreditRoleBasisCount("transferrer", "reviewed", 256),
                }
            );
        artist
            .Witnesses.Should()
            .HaveCount(2)
            .And.OnlyContain(witness => witness.Provenance.Basis == "reviewed");
        artist.WitnessesTruncated.Should().BeTrue();
        artist.Witnesses.Should().OnlyContain(witness => witness.Provenance.Evidence.Count == 1);
        snapshot
            .ReadSet.OwnerObservations.Should()
            .HaveCount(
                3,
                "only returned witness owners are observed; the epoch fences all other credits"
            );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.recording_credits SET original_text=original_text || ' correction' WHERE uuid=(SELECT uuid FROM normalization.recording_credits WHERE person_uuid=@Uuid AND basis='provider_assertion' ORDER BY uuid LIMIT 1)",
                person.Person
            )
        );
        await Admission(snapshot).Should().ThrowAsync<NormalizationConflictException>();
    }

    [TestCase(1, false)]
    [TestCase(2, true)]
    public async Task DistinctArtistMembershipUsesLimitPlusOne(int artistCount, bool incomplete)
    {
        var person = await Person();
        for (var i = 0; i < artistCount; i++)
        {
            var source = await fixture.Source();
            await Credit(person, source.SourceUuid, "taper");
        }
        var snapshot = await Read(person, new() { MaximumArtistContexts = 1 });

        snapshot.Facts.Single().ArtistContexts.Should().HaveCount(1);
        snapshot.Complete.Should().Be(!incomplete);
        snapshot
            .Issues.Any(issue => issue.Kind == CandidateFactsIssueKind.ArtistContextLimit)
            .Should()
            .Be(incomplete);
    }

    [TestCase("closure")]
    [TestCase("alias")]
    [TestCase("identifier")]
    [TestCase("role_basis")]
    [TestCase("evidence")]
    public async Task OmittedMatchingFactsAreExplicitlyIncomplete(string bound)
    {
        var person = await Person();
        var limits = new CandidateFactsLimits();
        CandidateFactsIssueKind expected;
        if (bound == "closure")
        {
            var retired = await Person();
            await Merge(retired, person);
            limits = limits with { MaximumIdentityClosure = 1 };
            expected = CandidateFactsIssueKind.IdentityClosureLimit;
        }
        else if (bound == "alias")
        {
            await AddAlias(person, "Second " + person.Person.DisplayName);
            limits = limits with { MaximumAliasMatchingNames = 1 };
            expected = CandidateFactsIssueKind.AssertionLimit;
        }
        else if (bound == "identifier")
        {
            await AddIdentifier(person, Guid.NewGuid().ToString());
            await AddIdentifier(person, Guid.NewGuid().ToString());
            limits = limits with { MaximumAssertionsPerKind = 1 };
            expected = CandidateFactsIssueKind.AssertionLimit;
        }
        else
        {
            var source = await fixture.Source();
            var credit = await Credit(person, source.SourceUuid, "taper");
            if (bound == "role_basis")
            {
                await Credit(person, source.SourceUuid, "transferrer");
                limits = limits with { MaximumRoleBasisGroupsPerArtist = 1 };
                expected = CandidateFactsIssueKind.RoleBasisGroupLimit;
            }
            else
            {
                var second = await fixture.Capture(person.Person.DisplayName, "people");
                var subject = new NormalizationSubjectKey("people", "recording", source.SourceUuid);
                var state = (await fixture.Work.GetSubjectAsync(subject))!;
                var ticket = await fixture.Work.BeginWorkAsync(
                    Guid.NewGuid(),
                    subject,
                    "policy-1",
                    [person.Input.Resource, second.Resource],
                    [await fixture.OwnerUuid("people", "person", person.Person.Uuid)],
                    state.DesiredGeneration
                );
                await people.PublishAsync(
                    ticket.WorkUuid,
                    new(
                        [],
                        [],
                        [],
                        [credit with { Evidence = [person.Input.Reference, second.Reference] }]
                    )
                );
                limits = limits with { MaximumEvidencePerAssertion = 1 };
                expected = CandidateFactsIssueKind.EvidenceLimit;
            }
        }

        var snapshot = await Read(person, limits);

        snapshot.Complete.Should().BeFalse();
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == expected && issue.PreventsAutomaticAcceptance);
    }

    [Test]
    public async Task CurrentCreditRevisionKeepsAllFullCitationsWithoutReturningHistoricalEvidence()
    {
        var person = await Person();
        var source = await fixture.Source();
        var credit = await Credit(person, source.SourceUuid, "taper");
        var newText = person.Person.DisplayName + " new account";
        var nextInput = await fixture.CaptureJson(
            JsonSerializer.Serialize(new { first = newText, second = newText }),
            "people",
            "/first"
        );
        var citations = new NormalizedEvidenceReference[]
        {
            new(
                nextInput.Reference.InputRevisionUuid,
                "/first",
                0,
                newText.EnumerateRunes().Count(),
                newText
            ),
            new(
                nextInput.Reference.InputRevisionUuid,
                "/second",
                0,
                newText.EnumerateRunes().Count(),
                newText
            ),
        };
        var owner = await fixture.Own(
            nextInput,
            "recording",
            source.SourceUuid,
            [await fixture.OwnerUuid("people", "person", person.Person.Uuid)]
        );
        await people.PublishAsync(
            owner.Ticket.WorkUuid,
            new(
                [],
                [],
                [],
                [
                    credit with
                    {
                        OriginalText = person.Person.DisplayName + " new account",
                        Evidence = citations,
                    },
                ]
            )
        );
        var snapshot = await Read(person);
        var witness = snapshot.Facts.Single().ArtistContexts.Single().Witnesses.Single();

        witness.Provenance.Revision.Should().Be(2);
        witness.Provenance.Evidence.Should().BeEquivalentTo(citations);
        witness.Provenance.Evidence.Should().NotContain(person.Input.Reference);
        witness.OriginalText.Should().Be(person.Person.DisplayName + " new account");
    }

    [Test]
    public async Task ReviewOnlyIdentityOwnerIsObservedWithoutInventingPublication()
    {
        var original = await Person();
        var target = new CanonicalPerson(
            Guid.NewGuid(),
            "Reviewed split " + Guid.NewGuid().ToString("N")
        );
        await fixture.Own(original.Input, "person", target.Uuid);
        var state = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Person.Uuid)
            )
        )!;
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                state.SubjectUuid,
                state.EditorialRevision,
                "split",
                original.Person.Uuid,
                1,
                target,
                null,
                [],
                "Create the separately reviewed identity"
            ),
            "operator"
        );

        var snapshot = await candidates.FindPeopleFactsAsync(Query(target.DisplayName));

        snapshot.Complete.Should().BeTrue();
        snapshot.Facts.Single().Header.Uuid.Should().Be(target.Uuid);
        snapshot.ReadSet.OwnerObservations.Single().PublishedWorkUuid.Should().BeNull();
        snapshot.ReadSet.OwnerObservations.Single().EditorialRevision.Should().BeGreaterThan(0);
    }

    [Test]
    public async Task MissingWitnessOwnerIsReportedWithoutRegisteringOne()
    {
        var person = await Person();
        var source = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                WITH credit AS (
                    INSERT INTO normalization.recording_credits(uuid,source_uuid,role,original_text,person_uuid,state,basis,reason_code,decision_uuid)
                    VALUES(@credit,@source,'taper',@text,@person,'accepted','provider_assertion','explicit_credit',@decision) RETURNING uuid
                ) INSERT INTO normalization.recording_credit_evidence(credit_uuid,credit_revision,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
                    SELECT uuid,1,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote FROM credit
                """,
                new
                {
                    credit = Guid.NewGuid(),
                    source = source.SourceUuid,
                    text = person.Person.DisplayName,
                    person = person.Person.Uuid,
                    decision = Guid.NewGuid(),
                    person.Input.Reference.InputRevisionUuid,
                    person.Input.Reference.JsonPointer,
                    person.Input.Reference.StartCodePoint,
                    person.Input.Reference.EndCodePoint,
                    person.Input.Reference.Quote,
                }
            )
        );

        var snapshot = await Read(person);

        snapshot.Complete.Should().BeFalse();
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.MissingOwner);
        (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "recording", source.SourceUuid)
            )
        )
            .Should()
            .BeNull();
    }

    [Test]
    public async Task SourceArrivalAndNewCreditOwnersInvalidateTheOriginalReadSet()
    {
        var person = await Person();
        var source = await fixture.Source();
        var missingSource = await fixture.Source();
        var missingUuid = missingSource.SourceUuid;
        await Credit(person, missingUuid, "taper");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM sources WHERE id=@SourceId", missingSource)
        );
        var beforeArrival = await Read(person);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts) VALUES(@ArtistId,@key,'2026-10-17',@missingUuid,now(),false,false,false)",
                new
                {
                    source.ArtistId,
                    key = missingUuid.ToString(),
                    missingUuid,
                }
            )
        );
        await Admission(beforeArrival).Should().ThrowAsync<NormalizationConflictException>();
        var afterArrival = await Read(person);
        afterArrival.Facts.Single().MissingSourceCreditCount.Should().Be(0);
        afterArrival.Facts.Single().ArtistContexts.Should().HaveCount(1);
        var consumer = await Admit(afterArrival);
        await Credit(person, source.SourceUuid, "collector");
        var published = await fixture.Work.TryPublishAsync(
            consumer.WorkUuid,
            (_, _, _) => Task.CompletedTask
        );

        published.Status.Should().Be(NormalizationPublicationStatus.Stale);
    }

    [Test]
    public async Task SourceArtistReassignmentInvalidatesMembershipAndReturnsTheNewArtist()
    {
        var person = await Person();
        var original = await fixture.Source();
        var destination = await fixture.Source();
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE source_tracks SET mp3_url=@url WHERE uuid=@TrackUuid",
                new { url = $"https://example.test/{original.TrackUuid:D}.mp3", original.TrackUuid }
            )
        );
        await Credit(person, original.SourceUuid, "taper");
        var before = await Read(person);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET artist_id=@artist WHERE id=@source; UPDATE source_tracks SET artist_id=@artist WHERE source_id=@source",
                new { artist = destination.ArtistId, source = original.SourceId }
            )
        );

        await Admission(before).Should().ThrowAsync<NormalizationConflictException>();
        var current = await Read(person);
        var expectedArtist = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", destination)
        );
        current.Facts.Single().ArtistContexts.Single().ArtistUuid.Should().Be(expectedArtist);
        current.ReadSet.CatalogInputs.Single().ArtistUuid.Should().Be(expectedArtist);
    }

    [Test]
    public async Task EditorialOwnerChangeIsCheckedEvenWhenAcceptedFactsDoNotChange()
    {
        var person = await Person();
        var before = await Read(person);
        var owner = before.ReadSet.OwnerObservations.Single();
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(
                    new { note = "Reviewed existing accepted facts" }
                ),
                "Confirm the current identity",
                "operator"
            ),
            (_, _, _) => Task.CompletedTask
        );

        var current = await Read(person);

        current.ReadSet.CandidateUniverses.Should().Equal(before.ReadSet.CandidateUniverses);
        current
            .ReadSet.OwnerObservations.Single()
            .EditorialRevision.Should()
            .Be(owner.EditorialRevision + 1);
        await Admission(before).Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task SnapshotKeepsSelectionFactsAndEpochsTogetherAcrossConcurrentChange()
    {
        var person = await Person();
        await using var gateConnection = fixture.Db.CreateConnection(false, false);
        await gateConnection.OpenAsync();
        await using var gate = await gateConnection.BeginTransactionAsync();
        await gateConnection.ExecuteAsync(
            "LOCK normalization.person_aliases IN ACCESS EXCLUSIVE MODE",
            transaction: gate
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = candidates.FindPeopleFactsAsync(
            Query(person.Person.DisplayName),
            cancellationToken: cancellation.Token
        );
        var gateReleased = false;
        try
        {
            await WaitUntilAliasReadBlocks(cancellation.Token);
            var changedName = "Changed Person " + Guid.NewGuid().ToString("N");
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE normalization.people SET display_name=@changedName,name_key=@nameKey,revision=revision+1 WHERE uuid=@uuid",
                    new
                    {
                        changedName,
                        nameKey = NormalizedPeopleService.NameKey(changedName),
                        uuid = person.Person.Uuid,
                    }
                )
            );
            await gate.RollbackAsync();
            gateReleased = true;
            var snapshot = await pending;

            snapshot.Facts.Single().Header.DisplayText.Should().Be(person.Person.DisplayName);
            snapshot.Facts.Single().Header.Revision.Should().Be(1);
            await Admission(snapshot).Should().ThrowAsync<NormalizationConflictException>();
            (await Read(person)).Facts.Single().Header.DisplayText.Should().Be(changedName);
        }
        finally
        {
            try
            {
                // Npgsql retains Connection after completion; a successful release must
                // not roll the same transaction back again during cleanup.
                if (!gateReleased)
                {
                    await gate.RollbackAsync();
                }
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    await pending;
                }
                catch (OperationCanceledException) { }
            }
        }
    }

    [Test]
    public async Task CancelledReadDoesNotChangeFactsOrRegisterWork()
    {
        var person = await Person();
        var before = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>("SELECT count(*) FROM normalization.work_subjects")
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var read = () =>
            candidates.FindPeopleFactsAsync(
                Query(person.Person.DisplayName),
                cancellationToken: cancellation.Token
            );

        await read.Should().ThrowAsync<OperationCanceledException>();
        (
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<long>("SELECT count(*) FROM normalization.work_subjects")
            )
        )
            .Should()
            .Be(before);
        (await Read(person)).Complete.Should().BeTrue();
    }

    private Task<NormalizationCandidateFactsSnapshot<PersonCandidateFacts>> Read(
        PersonScenario person,
        CandidateFactsLimits? limits = null
    ) => candidates.FindPeopleFactsAsync(Query(person.Person.DisplayName), limits);

    private static NormalizationCandidateQuery Query(string name) =>
        new(IdentityMatchingDomain.People, [name], [], MinimumNameSimilarity: .99);

    private async Task<PersonScenario> Person()
    {
        var person = new CanonicalPerson(
            Guid.NewGuid(),
            "Recorder " + Guid.NewGuid().ToString("N")
        );
        var input = await fixture.Capture(person.DisplayName, "people");
        var alias = new PersonAlias(
            Guid.NewGuid(),
            person.Uuid,
            person.DisplayName,
            input.Reference
        );
        await fixture.PublishPeople(people, input, new([person], [alias], [], []));
        return new(person, alias, input);
    }

    private async Task AddAlias(PersonScenario person, string wording)
    {
        var input = await fixture.Capture(wording, "people");
        var owner = await fixture.Own(input, "person", person.Person.Uuid);
        await people.PublishAsync(
            owner.Ticket.WorkUuid,
            new([], [new(Guid.NewGuid(), person.Person.Uuid, wording, input.Reference)], [], [])
        );
    }

    private async Task AddIdentifier(
        PersonScenario person,
        string key,
        string provider = "test-provider"
    )
    {
        var owner = await fixture.Own(person.Input, "person", person.Person.Uuid);
        await people.PublishAsync(
            owner.Ticket.WorkUuid,
            new(
                [],
                [],
                [new(person.Person.Uuid, provider, "person", key, person.Input.Reference)],
                []
            )
        );
    }

    private async Task CorrectDisplay(PersonScenario person, string displayName)
    {
        var input = await fixture.Capture(displayName, "people");
        await fixture.Own(input, "person", person.Person.Uuid);
        var state = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", person.Person.Uuid)
            )
        )!;
        var revision = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT revision FROM normalization.people WHERE uuid=@Uuid",
                person.Person
            )
        );
        await people.ApplyMetadataDecisionAsync(
            new(
                Guid.NewGuid(),
                state.SubjectUuid,
                state.EditorialRevision,
                person.Person.Uuid,
                revision,
                "replace",
                "Correct the searchable display name",
                Identity: person.Person with
                {
                    DisplayName = displayName,
                },
                IdentityEvidence: input.Reference
            ),
            "operator"
        );
    }

    private async Task<RecordingCredit> Credit(
        PersonScenario person,
        Guid source,
        string role,
        Guid? track = null,
        DomainInput? input = null
    )
    {
        input ??= person.Input;
        var credit = new RecordingCredit(
            Guid.NewGuid(),
            source,
            role,
            person.Person.DisplayName,
            person.Person.Uuid,
            track,
            [input.Reference]
        );
        await fixture.PublishPeople(people, input, new([], [], [], [credit]));
        return credit;
    }

    private async Task Merge(PersonScenario original, PersonScenario target)
    {
        var state = (
            await fixture.Work.GetSubjectAsync(
                new NormalizationSubjectKey("people", "person", original.Person.Uuid)
            )
        )!;
        var revisions = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<IdentityRevisions>(
                "SELECT (SELECT revision FROM normalization.people WHERE uuid=@original) AS \"Original\",(SELECT revision FROM normalization.people WHERE uuid=@target) AS \"Target\"",
                new { original = original.Person.Uuid, target = target.Person.Uuid }
            )
        );
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                state.SubjectUuid,
                state.EditorialRevision,
                "merge",
                original.Person.Uuid,
                revisions.Original,
                target.Person,
                revisions.Target,
                [],
                "Reviewed identical person"
            ),
            "operator"
        );
    }

    private async Task SeedCreditCohort(PersonScenario person, CatalogSource source, int count)
    {
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                WITH recordings AS (
                    INSERT INTO sources(artist_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts)
                    SELECT @ArtistId,gen_random_uuid()::text,'2026-10-17',gen_random_uuid(),now(),false,false,false FROM generate_series(1,@count)
                    RETURNING uuid
                ), numbered AS (
                    SELECT uuid,row_number() OVER(ORDER BY uuid) AS position FROM recordings
                ), credits AS (
                    INSERT INTO normalization.recording_credits(uuid,source_uuid,role,original_text,person_uuid,state,basis,reason_code,decision_uuid)
                    SELECT gen_random_uuid(),uuid,CASE WHEN position%2=1 THEN 'taper' ELSE 'transferrer' END,
                        @text,@person,'accepted',CASE WHEN position%2=1 THEN 'provider_assertion' ELSE 'reviewed' END,
                        'explicit_credit',gen_random_uuid() FROM numbered RETURNING uuid
                )
                INSERT INTO normalization.recording_credit_evidence(credit_uuid,credit_revision,input_revision_uuid,json_pointer,start_code_point,end_code_point,quote)
                    SELECT uuid,1,@InputRevisionUuid,@JsonPointer,@StartCodePoint,@EndCodePoint,@Quote FROM credits
                """,
                new
                {
                    source.ArtistId,
                    count,
                    text = person.Person.DisplayName,
                    person = person.Person.Uuid,
                    person.Input.Reference.InputRevisionUuid,
                    person.Input.Reference.JsonPointer,
                    person.Input.Reference.StartCodePoint,
                    person.Input.Reference.EndCodePoint,
                    person.Input.Reference.Quote,
                }
            )
        );
        var witnessSources = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<Guid>(
                "SELECT source_uuid FROM normalization.recording_credits WHERE person_uuid=@uuid AND basis='reviewed' ORDER BY uuid LIMIT 2",
                new { uuid = person.Person.Uuid }
            )
        );
        foreach (var sourceUuid in witnessSources)
        {
            await fixture.Own(
                person.Input,
                "recording",
                sourceUuid,
                [await fixture.OwnerUuid("people", "person", person.Person.Uuid)]
            );
        }
    }

    private Func<Task<NormalizationWorkTicket>> Admission(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot
    ) => () => Admit(snapshot);

    private Task<NormalizationWorkTicket> Admit(
        NormalizationCandidateFactsSnapshot<PersonCandidateFacts> snapshot
    ) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("people", "person", Guid.NewGuid()),
            "facts-consumer-v1",
            [],
            [],
            catalogInputs: snapshot.ReadSet.CatalogInputs,
            candidateUniverses: snapshot.ReadSet.CandidateUniverses,
            expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
        );

    private async Task WaitUntilAliasReadBlocks(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var waiting = await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid() AND wait_event_type='Lock' AND query LIKE '%normalization.current_person_aliases%')"
                )
            );
            if (waiting)
            {
                return;
            }
            await Task.Delay(20, ct);
        }
    }

    private sealed record PersonScenario(
        CanonicalPerson Person,
        PersonAlias Alias,
        DomainInput Input
    );

    private sealed record IdentityRevisions(long Original, long Target);
}
