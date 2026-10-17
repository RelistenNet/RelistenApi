using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

/// <summary>Only explicit, consistent, fully resolved author assertions establish a complete author set.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationCompositionAuthorship
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;
    private NormalizedPeopleService people = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
        people = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task ExplicitCompleteAuthorshipSupportsWriterMatchingWithoutIndividualWorkCredits()
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Lyricist");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        var assertion = Assertion(
            work,
            true,
            Member(work, 0, pat),
            Member(work, 1, robin, "lyricist")
        );
        var owner = await PublishWork(
            work,
            [assertion],
            [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid]
        );

        var facts = await Facts(work);
        facts.CompleteAuthorship.Should().BeTrue();
        facts.AuthorUuids.Should().BeEquivalentTo([pat.Person.Uuid, robin.Person.Uuid]);
        facts.AuthorshipAssertions.Should().ContainSingle();
        facts.WorkCredits.Should().BeEmpty();
        var stored = (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Single();
        stored.Value.Should().BeEquivalentTo(assertion);
        stored.Revision.Should().Be(1);

        var link = await WriterLink(work, owner.SubjectUuid, [pat.Person.Uuid, robin.Person.Uuid]);
        link.Result.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await music.GetCompositionLinksAsync(link.SongUuid)).Should().ContainSingle();
    }

    [TestCase("partial")]
    [TestCase("unresolved")]
    [TestCase("conflicting")]
    [TestCase("additional_partial")]
    [TestCase("unresolved_partial")]
    [TestCase("extra_credit")]
    [TestCase("extra_composer")]
    [TestCase("extra_lyricist")]
    [TestCase("unresolved_credit")]
    [TestCase("artist_credit")]
    public async Task IncompleteOrConflictingAssertionsCannotBecomeACompleteWriterSet(
        string scenario
    )
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Writer");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        var assertions = scenario switch
        {
            "partial" => new[] { Assertion(work, false, Member(work, 0, pat)) },
            "unresolved" => new[] { Assertion(work, true, Member(work, 0, pat), Member(work, 1)) },
            "conflicting" => new[]
            {
                Assertion(work, true, Member(work, 0, pat)),
                Assertion(work, true, Member(work, 1, robin)),
            },
            "additional_partial" => new[]
            {
                Assertion(work, true, Member(work, 0, pat)),
                Assertion(work, false, Member(work, 1, robin)),
            },
            "unresolved_partial" => new[]
            {
                Assertion(work, true, Member(work, 0, pat)),
                Assertion(work, false, Member(work, 1)),
            },
            "extra_credit"
            or "extra_composer"
            or "extra_lyricist"
            or "unresolved_credit"
            or "artist_credit" => new[] { Assertion(work, true, Member(work, 0, pat)) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        WorkCreditChange[] credits = scenario switch
        {
            "extra_credit" => [Credit(work, robin, 1)],
            "extra_composer" => [Credit(work, robin, 1) with { Role = "composer" }],
            "extra_lyricist" => [Credit(work, robin, 1) with { Role = "lyricist" }],
            "unresolved_credit" => [Credit(work, robin, 1) with { PersonUuid = null }],
            "artist_credit" =>
            [
                Credit(work, pat, 0) with
                {
                    PersonUuid = null,
                    ArtistUuid = await Artist(pat.Person.Uuid),
                },
            ],
            _ => [],
        };
        var owner = await PublishWork(
            work,
            assertions,
            [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid],
            credits
        );

        var facts = await Facts(work);

        facts.CompleteAuthorship.Should().BeFalse();
        facts.AuthorUuids.Should().BeEmpty();
        facts.AuthorshipAssertions.Should().HaveCount(assertions.Length);
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid))
            .Should()
            .HaveCount(assertions.Length);
        Func<Task> match = () =>
            WriterLink(work, owner.SubjectUuid, [pat.Person.Uuid, robin.Person.Uuid]);
        await match.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task RoleDifferencesPreserveBothAssertionsWithoutInventingAnIdentityConflict()
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Writer");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        var first = Assertion(
            work,
            true,
            Member(work, 0, pat, "composer"),
            Member(work, 1, robin, "lyricist")
        );
        var second = Assertion(
            work,
            true,
            Member(work, 0, pat, "lyricist"),
            Member(work, 1, robin, "composer")
        );
        await PublishWork(work, [first, second], [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid]);

        var facts = await Facts(work);

        facts.CompleteAuthorship.Should().BeTrue();
        facts.AuthorUuids.Should().BeEquivalentTo([pat.Person.Uuid, robin.Person.Uuid]);
        facts.AuthorshipAssertions.Should().HaveCount(2);
        facts
            .AuthorshipAssertions.SelectMany(assertion => assertion.Authors)
            .Select(author => author.Role)
            .Should()
            .BeEquivalentTo(["composer", "lyricist", "lyricist", "composer"]);
    }

    [TestCase("arranger", "resolved")]
    [TestCase("arranger", "unresolved")]
    [TestCase("arranger", "artist")]
    [TestCase("cover_artist", "unresolved")]
    [TestCase("cover_artist", "artist")]
    public async Task NonAuthorCreditsDoNotInvalidateExplicitCompleteAuthorship(
        string role,
        string target
    )
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Arranger");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        var credit = Credit(work, robin, 1) with { Role = role };
        credit = target switch
        {
            "unresolved" => credit with { PersonUuid = null },
            "artist" => credit with { PersonUuid = null, ArtistUuid = await Artist() },
            _ => credit,
        };
        await PublishWork(
            work,
            [Assertion(work, true, Member(work, 0, pat))],
            [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid],
            [credit]
        );

        var facts = await Facts(work);

        facts.CompleteAuthorship.Should().BeTrue();
        facts.AuthorUuids.Should().BeEquivalentTo([pat.Person.Uuid]);
        facts.WorkCredits.Should().ContainSingle(credit => credit.Role == role);
    }

    [Test]
    public async Task APartialAssertionContainedInTheCompleteAuthorSetRemainsConsistent()
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Writer");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        await PublishWork(
            work,
            [
                Assertion(work, true, Member(work, 0, pat), Member(work, 1, robin)),
                Assertion(work, false, Member(work, 0, pat)),
            ],
            [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid]
        );

        var facts = await Facts(work);

        facts.CompleteAuthorship.Should().BeTrue();
        facts.AuthorUuids.Should().BeEquivalentTo([pat.Person.Uuid, robin.Person.Uuid]);
        facts.AuthorshipAssertions.Should().HaveCount(2);
    }

    [TestCase("members")]
    [TestCase("assertions")]
    public async Task BoundedAuthorshipFactsCannotEstablishAnApparentlyCompleteAuthorSubset(
        string bound
    )
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Writer");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        CompositionAuthorshipChange[] assertions =
            bound == "members"
                ? [Assertion(work, true, Member(work, 0, pat), Member(work, 1, robin))]
                :
                [
                    Assertion(work, true, Member(work, 0, pat)),
                    Assertion(work, true, Member(work, 1, robin)),
                ];
        await PublishWork(work, assertions, [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid]);

        var snapshot = await candidates.FindCompositionFactsAsync(
            Query(work),
            new() { MaximumAssertionsPerKind = 1 }
        );

        snapshot.Complete.Should().BeFalse();
        var facts = snapshot.Facts.Single();
        facts
            .AuthorshipAssertions.Should()
            .ContainSingle()
            .Which.CompleteAuthorship.Should()
            .BeTrue();
        facts.AuthorshipAssertions.Single().Authors.Should().ContainSingle();
        facts.CompleteAuthorship.Should().BeFalse();
        facts.AuthorUuids.Should().BeEmpty();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.AssertionLimit
                && issue.FactKind
                    == (bound == "members" ? "authorship_members" : "authorship_assertions")
            );
    }

    [Test]
    public async Task IndividualWriterCreditsNeverAssertThatTheAuthorListIsExhaustive()
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        var owner = await PublishWork(work, [], [pat.Owner.SubjectUuid], [Credit(work, pat, 0)]);

        var facts = await Facts(work);

        facts.WorkCredits.Should().ContainSingle();
        facts.AuthorshipAssertions.Should().BeEmpty();
        facts.CompleteAuthorship.Should().BeFalse();
        facts.AuthorUuids.Should().BeEmpty();
        Func<Task> match = () => WriterLink(work, owner.SubjectUuid, [pat.Person.Uuid]);
        await match.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task AnArtistUuidEqualToAPersonUuidCannotSupplyPersonAuthorship()
    {
        var person = await Person("Person sharing an artist UUID");
        await Artist(person.Person.Uuid);
        var work = await Work(person.Person.DisplayName);
        var artistCredit = new WorkCreditChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = work.Composition.Uuid,
            ArtistUuid = person.Person.Uuid,
            Role = "writer",
            OriginalText = person.Person.DisplayName,
            Evidence = [AuthorEvidence(work, 0)],
        };
        var owner = await PublishWork(work, [], [], [artistCredit]);

        var facts = await Facts(work);

        facts.WorkCredits.Single().ArtistUuid.Should().Be(person.Person.Uuid);
        facts.WorkCredits.Single().ResolvedPersonUuid.Should().BeNull();
        facts.PersonCreditTargets.Should().BeEmpty();
        facts.AuthorshipAssertions.Should().BeEmpty();
        facts.AuthorUuids.Should().BeEmpty();
        facts.CompleteAuthorship.Should().BeFalse();
        Func<Task> match = () => WriterLink(work, owner.SubjectUuid, [person.Person.Uuid]);
        await match.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task PersonRedirectsKeepStoredAuthorshipAndResolveTheCanonicalAuthorUnion()
    {
        var original = await Person("Original Writer");
        var survivor = await Person("Surviving Writer");
        var work = await Work(original.Person.DisplayName);
        var assertion = Assertion(work, true, Member(work, 0, original));
        await PublishWork(work, [assertion], [original.Owner.SubjectUuid]);
        await people.ApplyIdentityDecisionAsync(
            new(
                Guid.NewGuid(),
                original.Owner.SubjectUuid,
                0,
                "merge",
                original.Person.Uuid,
                1,
                survivor.Person,
                1,
                [],
                "Same documented writer"
            ),
            "test-operator"
        );

        var snapshot = await candidates.FindCompositionFactsAsync(Query(work));
        var facts = snapshot.Facts.Single();

        facts.CompleteAuthorship.Should().BeTrue();
        facts.AuthorUuids.Should().BeEquivalentTo([survivor.Person.Uuid]);
        var member = facts.AuthorshipAssertions.Single().Authors.Single();
        member.StoredPersonUuid.Should().Be(original.Person.Uuid);
        member.ResolvedPersonUuid.Should().Be(survivor.Person.Uuid);
        member.OriginalText.Should().Be(original.Person.DisplayName);
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .Contain([original.Owner.SubjectUuid, survivor.Owner.SubjectUuid]);
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid))
            .Single()
            .Value.Authors.Single()
            .PersonUuid.Should()
            .Be(original.Person.Uuid);
    }

    [TestCase("arranger")]
    [TestCase("cover_artist")]
    public async Task NonAuthorRolesCannotEnterAnAuthorshipAssertion(string role)
    {
        var pat = await Person("Pat Arranger");
        var work = await Work(pat.Person.DisplayName);
        var assertion = Assertion(work, true, Member(work, 0, pat, role));
        Func<Task> publish = () => PublishWork(work, [assertion], [pat.Owner.SubjectUuid]);

        await publish.Should().ThrowAsync<ArgumentException>();

        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Should().BeEmpty();
        (await music.GetCompositionAsync(work.Composition.Uuid)).Should().BeNull();
    }

    [TestCase("missing")]
    [TestCase("unrelated_scalar")]
    [TestCase("different_text")]
    public async Task EachMemberRequiresItsOwnExactRetainedNameCitation(string fault)
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        var member = Member(work, 0, pat);
        member = fault switch
        {
            "missing" => member with { Evidence = null },
            "unrelated_scalar" => member with { Evidence = work.Input.Reference },
            "different_text" => member with { OriginalText = "Invented author" },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        Func<Task> publish = () =>
            PublishWork(work, [Assertion(work, true, member)], [pat.Owner.SubjectUuid]);

        await publish.Should().ThrowAsync<ArgumentException>();

        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task TheCompositionOwnerCannotEditAnotherCompositionsAuthorship()
    {
        var pat = await Person("Pat Writer");
        var original = await Work(pat.Person.DisplayName);
        await PublishWork(original, [], []);
        var other = await Work(pat.Person.DisplayName);
        var owner = await Ticket(
            original.Input,
            "compositions",
            "composition",
            other.Composition.Uuid,
            [pat.Owner.SubjectUuid]
        );
        Func<Task> wrongOwner = () =>
            music.PublishAsync(
                owner.WorkUuid,
                new MusicPublication
                {
                    AuthorshipAssertions = [Assertion(original, true, Member(original, 0, pat))],
                }
            );

        await wrongOwner.Should().ThrowAsync<ArgumentException>();

        (await music.GetCompositionAuthorshipAsync(original.Composition.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task ResolvedAuthorsRequireTheirCanonicalPersonOwnerInTheConsumedDependencies()
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        var assertion = Assertion(work, true, Member(work, 0, pat));
        Func<Task> missingDependency = () => PublishWork(work, [assertion], []);

        await missingDependency.Should().ThrowAsync<ArgumentException>();

        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task RetractingAPartialAssertionAllowsTheRemainingCompleteAssertionToEstablishAuthors()
    {
        var pat = await Person("Pat Writer");
        var robin = await Person("Robin Writer");
        var work = await Work(pat.Person.DisplayName, robin.Person.DisplayName);
        var complete = Assertion(work, true, Member(work, 0, pat));
        var partial = Assertion(work, false, Member(work, 1, robin));
        await PublishWork(
            work,
            [complete, partial],
            [pat.Owner.SubjectUuid, robin.Owner.SubjectUuid]
        );
        (await Facts(work)).CompleteAuthorship.Should().BeFalse();
        var next = await Ticket(
            work.Input,
            "compositions",
            "composition",
            work.Composition.Uuid,
            [robin.Owner.SubjectUuid]
        );

        await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication
            {
                AuthorshipAssertions = [partial with { ExpectedRevision = 1, Retracted = true }],
            }
        );

        var facts = await Facts(work);
        facts.CompleteAuthorship.Should().BeTrue();
        facts.AuthorshipAssertions.Should().ContainSingle();
        facts.AuthorUuids.Should().BeEquivalentTo([pat.Person.Uuid]);
    }

    [Test]
    public async Task IdenticalAuthorshipPreservesRevisionReceiptsAndCandidateGeneration()
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        var assertion = Assertion(work, true, Member(work, 0, pat));
        await PublishWork(work, [assertion], [pat.Owner.SubjectUuid]);
        var before = (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Single();
        var generation = await CompositionGeneration();
        var next = await Ticket(
            work.Input,
            "compositions",
            "composition",
            work.Composition.Uuid,
            [pat.Owner.SubjectUuid]
        );
        var publication = new MusicPublication
        {
            AuthorshipAssertions = [assertion with { ExpectedRevision = 1 }],
        };

        var published = await music.PublishAsync(next.WorkUuid, publication);
        var replay = await music.PublishAsync(next.WorkUuid, publication);

        published.Status.Should().Be(NormalizationPublicationStatus.Published);
        published.Replayed.Should().BeFalse();
        replay.Replayed.Should().BeTrue();
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid))
            .Single()
            .Should()
            .BeEquivalentTo(before);
        (await CompositionGeneration()).Should().Be(generation);
        (await fixture.Work.GetSubjectAsync(next.SubjectUuid))!
            .PublishedWorkUuid.Should()
            .Be(next.WorkUuid);
    }

    [Test]
    public async Task IdenticalPayloadStillRequiresTheCurrentAssertionRevision()
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        var assertion = Assertion(work, true, Member(work, 0, pat));
        await PublishWork(work, [assertion], [pat.Owner.SubjectUuid]);
        var next = await Ticket(
            work.Input,
            "compositions",
            "composition",
            work.Composition.Uuid,
            [pat.Owner.SubjectUuid]
        );
        Func<Task> stale = () =>
            music.PublishAsync(
                next.WorkUuid,
                new MusicPublication { AuthorshipAssertions = [assertion] }
            );

        await stale.Should().ThrowAsync<NormalizationConflictException>();

        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid))
            .Single()
            .Revision.Should()
            .Be(1);
        (
            await music.PublishAsync(
                next.WorkUuid,
                new MusicPublication
                {
                    AuthorshipAssertions = [assertion with { ExpectedRevision = 1 }],
                }
            )
        )
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    [Test]
    public async Task ReviewedAuthorshipKeepsAuthorityAndRepeatedReviewDoesNotCreateAnotherDomainRevision()
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        var assertion = Assertion(work, true, Member(work, 0, pat));
        var owner = await PublishWork(work, [assertion], [pat.Owner.SubjectUuid]);
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            0,
            "accept",
            new MusicPublication
            {
                AuthorshipAssertions = [assertion with { ExpectedRevision = 1 }],
            },
            "Reviewed complete author list",
            "test-operator"
        );
        var reviewed = (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Single();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            1,
            "accept",
            new MusicPublication
            {
                AuthorshipAssertions = [assertion with { ExpectedRevision = reviewed.Revision }],
            },
            "Confirmed author list",
            "test-operator"
        );
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid))
            .Single()
            .Should()
            .BeEquivalentTo(reviewed);
        (await fixture.Work.GetSubjectAsync(owner.SubjectUuid))!.EditorialRevision.Should().Be(2);
        var next = await Ticket(
            work.Input,
            "compositions",
            "composition",
            work.Composition.Uuid,
            [pat.Owner.SubjectUuid]
        );
        Func<Task> overwrite = () =>
            music.PublishAsync(
                next.WorkUuid,
                new MusicPublication
                {
                    AuthorshipAssertions =
                    [
                        assertion with
                        {
                            ExpectedRevision = reviewed.Revision,
                        },
                    ],
                }
            );
        await overwrite.Should().ThrowAsync<NormalizationConflictException>();
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid))
            .Single()
            .Should()
            .BeEquivalentTo(reviewed);
    }

    [Test]
    public async Task InvalidLaterAssertionRollsBackTheCompositionAndEarlierAssertionAndCanRetry()
    {
        var pat = await Person("Pat Writer");
        var work = await Work(pat.Person.DisplayName);
        await PublishWork(work, [], []);
        var before = await music.GetCompositionAsync(work.Composition.Uuid);
        var generation = await CompositionGeneration();
        var valid = Assertion(work, true, Member(work, 0, pat));
        var invalid = Assertion(work, true, Member(work, 0, pat, "arranger"));
        var next = await Ticket(
            work.Input,
            "compositions",
            "composition",
            work.Composition.Uuid,
            [pat.Owner.SubjectUuid]
        );
        var changed = work.Composition with
        {
            ExpectedRevision = 1,
            DisplayTitle = "Changed title",
        };
        Func<Task> failing = () =>
            music.PublishAsync(
                next.WorkUuid,
                new MusicPublication
                {
                    Compositions = [changed],
                    AuthorshipAssertions = [valid, invalid],
                }
            );

        await failing.Should().ThrowAsync<ArgumentException>();

        (await music.GetCompositionAsync(work.Composition.Uuid)).Should().BeEquivalentTo(before);
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Should().BeEmpty();
        (await CompositionGeneration()).Should().Be(generation);
        var result = await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication
            {
                Compositions = [changed],
                AuthorshipAssertions =
                [
                    valid,
                    invalid with
                    {
                        Authors = [Member(work, 0, pat, "composer")],
                    },
                ],
            }
        );
        result.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await music.GetCompositionAuthorshipAsync(work.Composition.Uuid)).Should().HaveCount(2);
        (await music.GetCompositionAsync(work.Composition.Uuid))!
            .Value.DisplayTitle.Should()
            .Be("Changed title");
    }

    [Test]
    public async Task CompositionMergeRequiresExplicitAuthorshipRetractionAndPreservesItsStoredHistory()
    {
        var pat = await Person("Pat Writer");
        var retired = await Work(pat.Person.DisplayName);
        var survivor = await Work(pat.Person.DisplayName);
        var assertion = Assertion(retired, true, Member(retired, 0, pat));
        var retiredOwner = await Ticket(
            retired.Input,
            "compositions",
            "composition",
            retired.Composition.Uuid,
            [pat.Owner.SubjectUuid]
        );
        await music.PublishAsync(
            retiredOwner.WorkUuid,
            new MusicPublication
            {
                Compositions = [retired.Composition],
                AuthorshipAssertions = [assertion],
            }
        );
        var survivorOwner = await PublishWork(survivor, [], []);
        var beforeComposition = await music.GetCompositionAsync(retired.Composition.Uuid);
        var beforeAuthorship = await music.GetCompositionAuthorshipAsync(retired.Composition.Uuid);
        var beforeRetiredOwner = await fixture.Work.GetSubjectAsync(retiredOwner.SubjectUuid);
        var beforeSurvivorOwner = await fixture.Work.GetSubjectAsync(survivorOwner.SubjectUuid);
        var generation = await CompositionGeneration();
        var operationUuid = Guid.NewGuid();
        var merge = new MusicPublication
        {
            Merges =
            [
                new("compositions", retired.Composition.Uuid, survivor.Composition.Uuid, 1, 1),
            ],
        };
        Func<Task> implicitMove = () =>
            music.ApplyDecisionAsync(
                operationUuid,
                retiredOwner.SubjectUuid,
                0,
                "merge",
                merge,
                "Same documented composition",
                "test-operator"
            );

        await implicitMove.Should().ThrowAsync<ArgumentException>();

        (await music.GetCompositionAsync(retired.Composition.Uuid))
            .Should()
            .BeEquivalentTo(beforeComposition);
        (await music.GetCompositionAuthorshipAsync(retired.Composition.Uuid))
            .Should()
            .BeEquivalentTo(beforeAuthorship);
        (await fixture.Work.GetSubjectAsync(retiredOwner.SubjectUuid))
            .Should()
            .BeEquivalentTo(beforeRetiredOwner);
        (await fixture.Work.GetSubjectAsync(survivorOwner.SubjectUuid))
            .Should()
            .BeEquivalentTo(beforeSurvivorOwner);
        (await CompositionGeneration()).Should().Be(generation);

        var explicitRetraction = merge with
        {
            AuthorshipAssertions = [assertion with { ExpectedRevision = 1, Retracted = true }],
        };
        var merged = await music.ApplyDecisionAsync(
            operationUuid,
            retiredOwner.SubjectUuid,
            0,
            "merge",
            explicitRetraction,
            "Same documented composition; retract retired author assertion",
            "test-operator"
        );

        merged.Replayed.Should().BeFalse();
        (await music.GetCompositionAsync(retired.Composition.Uuid))!
            .Value.Uuid.Should()
            .Be(survivor.Composition.Uuid);
        (await music.GetCompositionAuthorshipAsync(retired.Composition.Uuid)).Should().BeEmpty();
        (await music.GetCompositionAuthorshipAsync(survivor.Composition.Uuid)).Should().BeEmpty();
        var retained = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<StoredAuthorship>(
                """
                SELECT a.composition_uuid AS CompositionUuid,a.retracted AS Retracted,r.revision AS Revision,
                    (SELECT count(*) FROM normalization.music_revisions WHERE entity_uuid=a.uuid) AS History
                FROM normalization.composition_authorship_assertions a
                JOIN normalization.music_revisions r ON r.uuid=a.revision_uuid WHERE a.uuid=@Uuid
                """,
                assertion
            )
        );
        retained.Should().Be(new StoredAuthorship(retired.Composition.Uuid, true, 2, 2));
    }

    private async Task<WorkContext> Work(params string[] authorNames)
    {
        var title = "Authorship work " + Guid.NewGuid().ToString("N");
        var input = await fixture.CaptureJson(
            JsonSerializer.Serialize(new { title, authors = authorNames }),
            "compositions",
            "/title"
        );
        var composition = new CompositionChange
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = title,
            Evidence = [input.Reference],
        };
        var identity = new MusicExternalIdentityChange
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = composition.Uuid,
            Provider = "authorship-fixture",
            ResourceType = "work",
            ProviderKey = Guid.NewGuid().ToString("D"),
            Evidence = [input.Reference],
        };
        return new(input, composition, identity, authorNames);
    }

    private async Task<PersonContext> Person(string name)
    {
        var input = await fixture.Capture(name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        var owner = await Ticket(input, "people", "person", person.Uuid);
        await people.PublishAsync(
            owner.WorkUuid,
            new([person], [new(Guid.NewGuid(), person.Uuid, name, input.Reference)], [], [])
        );
        return new(person, owner);
    }

    private async Task<Guid> Artist(Guid? uuid = null)
    {
        var source = await fixture.Source();
        if (uuid.HasValue)
        {
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteAsync(
                    "UPDATE artists SET uuid=@uuid WHERE id=@ArtistId",
                    new { uuid, source.ArtistId }
                )
            );
            return uuid.Value;
        }
        return await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
    }

    private async Task<NormalizationWorkTicket> PublishWork(
        WorkContext work,
        IReadOnlyList<CompositionAuthorshipChange> assertions,
        IReadOnlyList<Guid> dependencies,
        IReadOnlyList<WorkCreditChange>? credits = null
    )
    {
        var owner = await Ticket(
            work.Input,
            "compositions",
            "composition",
            work.Composition.Uuid,
            dependencies
        );
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication
            {
                Compositions = [work.Composition],
                ExternalIdentities = [work.Identity],
                AuthorshipAssertions = assertions,
                WorkCredits = credits ?? [],
            }
        );
        return owner;
    }

    private async Task<CompositionCandidateFacts> Facts(WorkContext work) =>
        (await candidates.FindCompositionFactsAsync(Query(work))).Facts.Single();

    private static NormalizationCandidateQuery Query(WorkContext work) =>
        new(
            IdentityMatchingDomain.Compositions,
            [],
            [new(work.Identity.Provider, work.Identity.ResourceType, work.Identity.ProviderKey)]
        );

    private static NormalizedEvidenceReference AuthorEvidence(WorkContext work, int index) =>
        new(
            work.Input.Reference.InputRevisionUuid,
            "/authors/" + index,
            0,
            work.AuthorNames[index].EnumerateRunes().Count(),
            work.AuthorNames[index]
        );

    private static CompositionAuthorMember Member(
        WorkContext work,
        int index,
        PersonContext? person = null,
        string role = "writer"
    ) => new(role, work.AuthorNames[index], person?.Person.Uuid, AuthorEvidence(work, index));

    private static CompositionAuthorshipChange Assertion(
        WorkContext work,
        bool complete,
        params CompositionAuthorMember[] authors
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = work.Composition.Uuid,
            CompleteAuthorship = complete,
            Authors = authors,
            Evidence = authors
                .Select(author => author.Evidence)
                .OfType<NormalizedEvidenceReference>()
                .Prepend(work.Input.Reference)
                .ToArray(),
        };

    private static WorkCreditChange Credit(WorkContext work, PersonContext person, int index) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = work.Composition.Uuid,
            PersonUuid = person.Person.Uuid,
            Role = "writer",
            OriginalText = work.AuthorNames[index],
            Evidence = [AuthorEvidence(work, index)],
        };

    private async Task<(Guid SongUuid, NormalizationPublicationResult Result)> WriterLink(
        WorkContext work,
        Guid compositionOwner,
        IReadOnlyList<Guid> authors
    )
    {
        var artist = await Artist();
        var song = new RepertoireSongChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            DisplayTitle = "Song",
            Evidence = [work.Input.Reference],
        };
        var dependencies = new List<Guid> { compositionOwner };
        foreach (var person in authors.Distinct())
        {
            dependencies.Add(
                (
                    await fixture.Work.GetSubjectAsync(
                        new NormalizationSubjectKey("people", "person", person)
                    )
                )!.SubjectUuid
            );
        }
        var owner = await Ticket(work.Input, "songs", "artist_repertoire", artist, dependencies);
        var link = new RepertoireCompositionChange
        {
            Uuid = Guid.NewGuid(),
            RepertoireSongUuid = song.Uuid,
            CompositionUuid = work.Composition.Uuid,
            MatchReason = "writer_evidence",
            CompleteAuthorship = true,
            MatchedAuthorUuids = authors,
            Evidence = Enumerable
                .Range(0, work.AuthorNames.Count)
                .Select(index => AuthorEvidence(work, index))
                .Prepend(work.Input.Reference)
                .ToArray(),
        };
        return (
            song.Uuid,
            await music.PublishAsync(
                owner.WorkUuid,
                new MusicPublication { RepertoireSongs = [song], CompositionLinks = [link] }
            )
        );
    }

    private async Task<NormalizationWorkTicket> Ticket(
        DomainInput input,
        string domain,
        string kind,
        Guid uuid,
        IReadOnlyList<Guid>? dependencies = null
    )
    {
        var subject = new NormalizationSubjectKey(domain, kind, uuid);
        var current = await fixture.Work.GetSubjectAsync(subject);
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "composition-authorship-test-v1",
            [input.Resource],
            dependencies ?? [],
            current?.DesiredGeneration
        );
    }

    private Task<long> CompositionGeneration() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT generation FROM normalization.candidate_universe_revisions WHERE domain='compositions' AND scope_uuid='00000000-0000-0000-0000-000000000000'"
            )
        );

    private sealed record WorkContext(
        DomainInput Input,
        CompositionChange Composition,
        MusicExternalIdentityChange Identity,
        IReadOnlyList<string> AuthorNames
    );

    private sealed record PersonContext(CanonicalPerson Person, NormalizationWorkTicket Owner);

    private sealed record StoredAuthorship(
        Guid CompositionUuid,
        bool Retracted,
        long Revision,
        long History
    );
}
