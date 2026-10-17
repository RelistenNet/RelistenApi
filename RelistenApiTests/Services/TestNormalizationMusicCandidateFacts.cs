using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using DomainInput = RelistenApiTests.Services.NormalizationDomainFixture.DomainInput;

namespace RelistenApiTests.Services;

[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicCandidateFacts
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;
    private NormalizationCandidateService candidates = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
        candidates = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task RepertoireRetainsAncestorTitlesQualifiedIdsAndEveryStoredWorkLink()
    {
        var scenario = await RepertoireScenario();
        await Redirect("repertoire_songs", scenario.OldSong.Uuid, scenario.CurrentSong.Uuid);
        await Redirect("compositions", scenario.OldWork.Uuid, scenario.CurrentWork.Uuid);
        var snapshot = await candidates.FindRepertoireFactsAsync(scenario.Query);
        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Single();
        fact.Hit.Uuid.Should().Be(scenario.CurrentSong.Uuid);
        fact.IdentityClosure.Select(header => header.Uuid)
            .Should()
            .BeEquivalentTo(new[] { scenario.OldSong.Uuid, scenario.CurrentSong.Uuid });
        fact.Titles.Select(title => title.DisplayTitle)
            .Should()
            .BeEquivalentTo(
                new[] { scenario.OldSong.DisplayTitle, scenario.CurrentSong.DisplayTitle }
            );
        fact.Titles.Single(title => title.Provenance.StoredIdentityUuid == scenario.OldSong.Uuid)
            .Aliases.Should()
            .Equal(scenario.OldSong.Aliases);
        fact.Titles.Should()
            .OnlyContain(title =>
                title.Provenance.MusicRevisionUuid.HasValue && title.Provenance.Revision == 1
            );
        fact.ExternalIdentities.Should().HaveCount(2);
        fact.ExternalIdentities.Select(identity => identity.Provenance.StoredIdentityUuid)
            .Should()
            .BeEquivalentTo(new[] { scenario.OldSong.Uuid, scenario.CurrentSong.Uuid });
        fact.CompositionLinks.Should().HaveCount(2);
        fact.CompositionLinks.Should()
            .OnlyContain(link =>
                link.ResolvedCompositionUuid == scenario.CurrentWork.Uuid
                && link.MatchReason == "qualified_work_identity"
            );
        fact.CompositionLinks.Select(link => link.StoredCompositionUuid)
            .Should()
            .BeEquivalentTo(new[] { scenario.OldWork.Uuid, scenario.CurrentWork.Uuid });
        fact.CompositionLinks.Should()
            .OnlyContain(link => link.MatchedWorkIdentity != null && !link.CompleteAuthorship);
        fact.CompositionIdentities.Select(header => header.Uuid)
            .Should()
            .BeEquivalentTo(new[] { scenario.OldWork.Uuid, scenario.CurrentWork.Uuid });
        fact.CompositionRedirects.Should()
            .ContainSingle(redirect =>
                redirect.OriginalUuid == scenario.OldWork.Uuid
                && redirect.SurvivorUuid == scenario.CurrentWork.Uuid
            );
        snapshot
            .ReadSet.CandidateUniverses.Select(input => input.Universe)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    NormalizationCandidateUniverseKey.Compositions,
                    NormalizationCandidateUniverseKey.ForRepertoire(scenario.Artist),
                }
            );
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    scenario.RepertoireOwner.SubjectUuid,
                    scenario.OldWorkOwner.SubjectUuid,
                    scenario.CurrentWorkOwner.SubjectUuid,
                }
            );
        snapshot
            .ReadSet.CatalogInputs.Should()
            .ContainSingle(input => input.ArtistUuid == scenario.Artist);
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.UnknownAuthority
                && !issue.PreventsAutomaticAcceptance
            );
    }

    [Test]
    public async Task CompositionRetainsConflictingAndNonAuthorCreditsWithoutConflatingPeopleAndArtists()
    {
        var input = await fixture.Capture("Music credits", "compositions");
        var artist = await Artist();
        var original = await Person("Original Writer");
        var survivor = await Person("Resolved Writer");
        var other = await Person("Other Writer");
        var oldWork = Work(input.Reference);
        var currentWork = Work(input.Reference);
        var oldOwner = await Ticket(
            input,
            "compositions",
            "composition",
            oldWork.Uuid,
            [original.Owner.SubjectUuid]
        );
        var writer = Credit(oldWork.Uuid, "writer", input.Reference) with
        {
            PersonUuid = original.Person.Uuid,
        };
        var identifier = Identity(oldWork.Uuid, false, input.Reference);
        await music.PublishAsync(
            oldOwner.WorkUuid,
            new MusicPublication
            {
                Compositions = [oldWork],
                WorkCredits = [writer],
                ExternalIdentities = [identifier],
            }
        );
        var credits = new[]
        {
            Credit(currentWork.Uuid, "writer", input.Reference) with
            {
                PersonUuid = other.Person.Uuid,
            },
            Credit(currentWork.Uuid, "composer", input.Reference) with
            {
                ArtistUuid = artist,
            },
            Credit(currentWork.Uuid, "arranger", input.Reference) with
            {
                PersonUuid = survivor.Person.Uuid,
            },
            Credit(currentWork.Uuid, "cover_artist", input.Reference) with
            {
                ArtistUuid = artist,
            },
            Credit(currentWork.Uuid, "original_performer", input.Reference),
            Credit(currentWork.Uuid, "lyricist", input.Reference),
        };
        var currentOwner = await Ticket(
            input,
            "compositions",
            "composition",
            currentWork.Uuid,
            [survivor.Owner.SubjectUuid, other.Owner.SubjectUuid]
        );
        await music.PublishAsync(
            currentOwner.WorkUuid,
            new MusicPublication { Compositions = [currentWork], WorkCredits = credits }
        );
        await Redirect("compositions", oldWork.Uuid, currentWork.Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.person_redirects(retired_uuid,survivor_uuid,decision_uuid) VALUES(@original,@survivor,@decision)",
                new
                {
                    original = original.Person.Uuid,
                    survivor = survivor.Person.Uuid,
                    decision = Guid.NewGuid(),
                }
            )
        );
        var snapshot = await candidates.FindCompositionFactsAsync(
            Query(IdentityMatchingDomain.Compositions, identifier)
        );
        snapshot.Complete.Should().BeTrue();
        var fact = snapshot.Facts.Single();
        fact.CompleteAuthorship.Should().BeFalse();
        fact.WorkCredits.Should().HaveCount(7);
        var resolvedWriter = fact.WorkCredits.Single(credit =>
            credit.Provenance.AssertionUuid == writer.Uuid
        );
        resolvedWriter.StoredPersonUuid.Should().Be(original.Person.Uuid);
        resolvedWriter.ResolvedPersonUuid.Should().Be(survivor.Person.Uuid);
        resolvedWriter.ArtistUuid.Should().BeNull();
        var composer = fact.WorkCredits.Single(credit => credit.Role == "composer");
        composer.ArtistUuid.Should().Be(artist);
        composer.StoredPersonUuid.Should().BeNull();
        composer.ResolvedPersonUuid.Should().BeNull();
        fact.WorkCredits.Where(credit => credit.Role is "original_performer" or "lyricist")
            .Should()
            .OnlyContain(credit =>
                credit.StoredPersonUuid == null
                && credit.ArtistUuid == null
                && credit.OriginalText.Length > 0
            );
        fact.WorkCredits.Count(credit => credit.Role == "writer")
            .Should()
            .Be(
                2,
                "conflicting individual assertions do not become one chosen or complete author list"
            );
        fact.PersonCreditTargets.Should()
            .Contain(target =>
                target.StoredPersonUuid == original.Person.Uuid
                && target.ResolvedPersonUuid == survivor.Person.Uuid
                && target.IdentityClosure.Count == 2
            );
        fact.PersonCreditTargets.Should()
            .Contain(target => target.StoredPersonUuid == survivor.Person.Uuid);
        var artistContext = fact.ArtistCreditTargets.Single();
        artistContext.ArtistUuid.Should().Be(artist);
        artistContext.Exists.Should().BeTrue();
        artistContext.Name.Should().NotBeNullOrEmpty();
        artistContext.Slug.Should().NotBeNullOrEmpty();
        snapshot.ReadSet.CatalogInputs.Should().ContainSingle(clock => clock.ArtistUuid == artist);
        snapshot
            .ReadSet.OwnerObservations.Select(owner => owner.SubjectUuid)
            .Should()
            .BeEquivalentTo(
                new[]
                {
                    oldOwner.SubjectUuid,
                    currentOwner.SubjectUuid,
                    original.Owner.SubjectUuid,
                    survivor.Owner.SubjectUuid,
                    other.Owner.SubjectUuid,
                }
            );
    }

    [Test]
    public async Task WithdrawnRelatedCompositionKeepsStoredLinkWithUnresolvedEndpointAndBlockingIssue()
    {
        var scenario = await RepertoireScenario();
        var next = await Ticket(
            scenario.Input,
            "compositions",
            "composition",
            scenario.OldWork.Uuid
        );
        await music.PublishAsync(
            next.WorkUuid,
            new MusicPublication
            {
                Compositions = [scenario.OldWork with { ExpectedRevision = 1, Retracted = true }],
            }
        );
        var snapshot = await candidates.FindRepertoireFactsAsync(scenario.Query);
        var link = snapshot.Facts.Single().CompositionLinks.Single();
        link.StoredCompositionUuid.Should().Be(scenario.OldWork.Uuid);
        link.ResolvedCompositionUuid.Should().BeNull();
        snapshot.Complete.Should().BeFalse();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.MissingRelatedIdentity
                && issue.PreventsAutomaticAcceptance
            );
        snapshot
            .ReadSet.OwnerObservations.Should()
            .Contain(owner =>
                owner.SubjectUuid == next.SubjectUuid && owner.PublishedWorkUuid == next.WorkUuid
            );
    }

    [Test]
    public async Task UnselectedRevisionAndEvidenceIndexAppendCannotChangeCurrentMusicFacts()
    {
        var scenario = await RepertoireScenario();
        var before = await candidates.FindRepertoireFactsAsync(scenario.Query);
        var title = before.Facts.Single().Titles.Single();
        var extra = await fixture.Capture("Unsupported new evidence", "songs");
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence)
                    VALUES(@revision,@entity,'repertoire_songs',2,@decision,'provider_assertion',@payload::jsonb,@evidence::jsonb);
                INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer)
                    VALUES(@current,1,@input,'/text')
                """,
                new
                {
                    revision = Guid.NewGuid(),
                    entity = scenario.OldSong.Uuid,
                    decision = Guid.NewGuid(),
                    payload = JsonSerializer.Serialize(
                        scenario.OldSong with
                        {
                            ExpectedRevision = 1,
                            DisplayTitle = "Unselected revision",
                        }
                    ),
                    evidence = JsonSerializer.Serialize(new[] { extra.Reference }),
                    current = title.Provenance.MusicRevisionUuid,
                    input = extra.Reference.InputRevisionUuid,
                }
            )
        );
        var after = await candidates.FindRepertoireFactsAsync(scenario.Query);
        after.Facts.Single().Titles.Single().Should().BeEquivalentTo(title);
        after.ReadSet.CandidateUniverses.Should().Equal(before.ReadSet.CandidateUniverses);
        after.ReadSet.OwnerObservations.Should().Equal(before.ReadSet.OwnerObservations);
    }

    [Test]
    public async Task AliasAndImmutableEvidenceListsAreBoundedAndReportOmittedFacts()
    {
        var input = await fixture.CaptureJson(
            "{\"a\":\"Song\",\"b\":\"Song\",\"c\":\"Song\"}",
            "songs",
            "/a"
        );
        var artist = await Artist();
        var song = Song(artist, input.Reference) with
        {
            Aliases = ["First alias", "Second alias"],
            Evidence =
            [
                input.Reference,
                input.Reference with
                {
                    JsonPointer = "/b",
                },
                input.Reference with
                {
                    JsonPointer = "/c",
                },
            ],
        };
        var owner = await Ticket(input, "songs", "artist_repertoire", artist);
        var identifier = Identity(song.Uuid, true, input.Reference);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication { RepertoireSongs = [song], ExternalIdentities = [identifier] }
        );
        var snapshot = await candidates.FindRepertoireFactsAsync(
            Query(IdentityMatchingDomain.Repertoire, identifier, artist),
            new() { MaximumAssertionsPerKind = 1, MaximumEvidencePerAssertion = 1 }
        );
        snapshot.Complete.Should().BeFalse();
        snapshot.Facts.Single().Titles.Single().Aliases.Should().ContainSingle();
        snapshot.Facts.Single().Titles.Single().Provenance.Evidence.Should().ContainSingle();
        snapshot
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.AssertionLimit && issue.FactKind == "aliases"
            );
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.EvidenceLimit);
    }

    [TestCase("ancestors")]
    [TestCase("linked_work")]
    public async Task IdentityBoundsBlockAcceptanceWithoutInventingMissingResolution(string kind)
    {
        var scenario = await RepertoireScenario();
        if (kind == "ancestors")
        {
            await Redirect("repertoire_songs", scenario.OldSong.Uuid, scenario.CurrentSong.Uuid);
        }
        else
        {
            await Redirect("compositions", scenario.OldWork.Uuid, scenario.CurrentWork.Uuid);
        }
        var snapshot = await candidates.FindRepertoireFactsAsync(
            scenario.Query,
            new() { MaximumIdentityClosure = 1 }
        );
        snapshot.Complete.Should().BeFalse();
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.IdentityClosureLimit);
        if (kind == "ancestors")
        {
            snapshot
                .Facts.Single()
                .IdentityClosure.Should()
                .ContainSingle(header => header.Uuid == scenario.CurrentSong.Uuid);
        }
        else
        {
            snapshot
                .Facts.Single()
                .CompositionLinks.Single()
                .ResolvedCompositionUuid.Should()
                .BeNull();
            snapshot.Facts.Single().CompositionIdentities.Should().ContainSingle();
        }
    }

    [Test]
    public async Task WorkCreditAndPersonResolutionBoundsRemainExplicit()
    {
        var input = await fixture.Capture("Credit", "compositions");
        var original = await Person("Old credit target");
        var current = await Person("Current credit target");
        var work = Work(input.Reference);
        var identifier = Identity(work.Uuid, false, input.Reference);
        var owner = await Ticket(
            input,
            "compositions",
            "composition",
            work.Uuid,
            [original.Owner.SubjectUuid, current.Owner.SubjectUuid]
        );
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication
            {
                Compositions = [work],
                ExternalIdentities = [identifier],
                WorkCredits =
                [
                    Credit(work.Uuid, "writer", input.Reference) with
                    {
                        PersonUuid = original.Person.Uuid,
                    },
                    Credit(work.Uuid, "cover_artist", input.Reference),
                    Credit(work.Uuid, "lyricist", input.Reference) with
                    {
                        PersonUuid = current.Person.Uuid,
                    },
                ],
            }
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO normalization.person_redirects(retired_uuid,survivor_uuid,decision_uuid) VALUES(@original,@current,@decision)",
                new
                {
                    original = original.Person.Uuid,
                    current = current.Person.Uuid,
                    decision = Guid.NewGuid(),
                }
            )
        );
        var limitedCredits = await candidates.FindCompositionFactsAsync(
            Query(IdentityMatchingDomain.Compositions, identifier),
            new() { MaximumAssertionsPerKind = 1 }
        );
        limitedCredits.Facts.Single().WorkCredits.Should().ContainSingle();
        limitedCredits
            .Issues.Should()
            .Contain(issue =>
                issue.Kind == CandidateFactsIssueKind.AssertionLimit
                && issue.FactKind == "work_credits"
            );
        var limitedIdentity = await candidates.FindCompositionFactsAsync(
            Query(IdentityMatchingDomain.Compositions, identifier),
            new() { MaximumIdentityClosure = 1 }
        );
        limitedIdentity.Complete.Should().BeFalse();
        limitedIdentity
            .Facts.Single()
            .PersonCreditTargets.Single(target => target.StoredPersonUuid == original.Person.Uuid)
            .ResolvedPersonUuid.Should()
            .BeNull();
        limitedIdentity
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.IdentityClosureLimit);
        var sharedBudget = await candidates.FindCompositionFactsAsync(
            Query(IdentityMatchingDomain.Compositions, identifier),
            new() { MaximumIdentityClosure = 2 }
        );
        sharedBudget
            .Facts.Single()
            .PersonCreditTargets.Sum(target => target.IdentityClosure.Count)
            .Should()
            .BeLessThanOrEqualTo(2);
        sharedBudget.Complete.Should().BeFalse();
        sharedBudget
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.IdentityClosureLimit);
    }

    [Test]
    public async Task RemovedCatalogArtistKeepsItsDistinctCreditTargetWithoutInventedPersonIdentity()
    {
        var input = await fixture.Capture("Artist writer", "compositions");
        var artist = await Artist();
        var work = Work(input.Reference);
        var identifier = Identity(work.Uuid, false, input.Reference);
        var owner = await Ticket(input, "compositions", "composition", work.Uuid);
        await music.PublishAsync(
            owner.WorkUuid,
            new MusicPublication
            {
                Compositions = [work],
                ExternalIdentities = [identifier],
                WorkCredits =
                [
                    Credit(work.Uuid, "writer", input.Reference) with
                    {
                        ArtistUuid = artist,
                    },
                ],
            }
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync("DELETE FROM artists WHERE uuid=@artist", new { artist })
        );

        var snapshot = await candidates.FindCompositionFactsAsync(
            Query(IdentityMatchingDomain.Compositions, identifier)
        );
        var credit = snapshot.Facts.Single().WorkCredits.Single();
        credit.ArtistUuid.Should().Be(artist);
        credit.StoredPersonUuid.Should().BeNull();
        credit.ResolvedPersonUuid.Should().BeNull();
        snapshot.Facts.Single().PersonCreditTargets.Should().BeEmpty();
        snapshot
            .Facts.Single()
            .ArtistCreditTargets.Should()
            .ContainSingle(target =>
                target.ArtistUuid == artist
                && !target.Exists
                && target.Name == null
                && target.Slug == null
            );
        snapshot.ReadSet.CatalogInputs.Should().ContainSingle(clock => clock.ArtistUuid == artist);
        snapshot
            .Issues.Should()
            .Contain(issue => issue.Kind == CandidateFactsIssueKind.MissingCatalogContext);
        snapshot.Facts.Single().CompleteAuthorship.Should().BeFalse();
    }

    [Test]
    public async Task OriginalFactsReadSetRejectsAdmissionAfterANewCompositionArrives()
    {
        var scenario = await RepertoireScenario();
        var snapshot = await candidates.FindRepertoireFactsAsync(scenario.Query);
        var incoming = Work(scenario.Input.Reference);
        var workOwner = await Ticket(scenario.Input, "compositions", "composition", incoming.Uuid);
        await music.PublishAsync(
            workOwner.WorkUuid,
            new MusicPublication { Compositions = [incoming] }
        );
        var consumer = new NormalizationSubjectKey("songs", "artist_repertoire", scenario.Artist);
        var state = (await fixture.Work.GetSubjectAsync(consumer))!;
        var stale = () =>
            fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                consumer,
                "music-facts-v1",
                [scenario.Input.Resource],
                [],
                state.DesiredGeneration,
                catalogInputs: snapshot.ReadSet.CatalogInputs,
                candidateUniverses: snapshot.ReadSet.CandidateUniverses,
                expectedOwnerObservations: snapshot.ReadSet.OwnerObservations
            );
        await stale.Should().ThrowAsync<NormalizationConflictException>();
        var fresh = await candidates.FindRepertoireFactsAsync(scenario.Query);
        var admitted = await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            consumer,
            "music-facts-v1",
            [scenario.Input.Resource],
            [],
            state.DesiredGeneration,
            catalogInputs: fresh.ReadSet.CatalogInputs,
            candidateUniverses: fresh.ReadSet.CandidateUniverses,
            expectedOwnerObservations: fresh.ReadSet.OwnerObservations
        );
        admitted.OwnerObservations.Should().Equal(fresh.ReadSet.OwnerObservations);
        (await fixture.Work.TryPublishAsync(admitted.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
    }

    private async Task<RepertoireFixture> RepertoireScenario()
    {
        var input = await fixture.Capture("Song and works", "songs");
        var artist = await Artist();
        var oldWork = Work(input.Reference);
        var currentWork = Work(input.Reference);
        var oldIdentity = Identity(oldWork.Uuid, false, input.Reference);
        var currentIdentity = Identity(currentWork.Uuid, false, input.Reference);
        var oldOwner = await Ticket(input, "compositions", "composition", oldWork.Uuid);
        await music.PublishAsync(
            oldOwner.WorkUuid,
            new MusicPublication { Compositions = [oldWork], ExternalIdentities = [oldIdentity] }
        );
        var currentOwner = await Ticket(input, "compositions", "composition", currentWork.Uuid);
        await music.PublishAsync(
            currentOwner.WorkUuid,
            new MusicPublication
            {
                Compositions = [currentWork],
                ExternalIdentities = [currentIdentity],
            }
        );
        var oldSong = Song(artist, input.Reference) with { Aliases = ["Old alias"] };
        var currentSong = Song(artist, input.Reference);
        var identifier = Identity(oldSong.Uuid, true, input.Reference);
        var repertoire = await Ticket(
            input,
            "songs",
            "artist_repertoire",
            artist,
            [oldOwner.SubjectUuid, currentOwner.SubjectUuid]
        );
        await music.PublishAsync(
            repertoire.WorkUuid,
            new MusicPublication
            {
                RepertoireSongs = [oldSong, currentSong],
                ExternalIdentities =
                [
                    identifier,
                    Identity(currentSong.Uuid, true, input.Reference),
                ],
                CompositionLinks =
                [
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        RepertoireSongUuid = oldSong.Uuid,
                        CompositionUuid = oldWork.Uuid,
                        MatchedWorkIdentity = new(
                            oldIdentity.Provider,
                            oldIdentity.ResourceType,
                            oldIdentity.ProviderKey
                        ),
                        Evidence = [input.Reference],
                    },
                    new()
                    {
                        Uuid = Guid.NewGuid(),
                        RepertoireSongUuid = currentSong.Uuid,
                        CompositionUuid = currentWork.Uuid,
                        MatchedWorkIdentity = new(
                            currentIdentity.Provider,
                            currentIdentity.ResourceType,
                            currentIdentity.ProviderKey
                        ),
                        Evidence = [input.Reference],
                    },
                ],
            }
        );
        return new(
            input,
            artist,
            oldSong,
            currentSong,
            oldWork,
            currentWork,
            repertoire,
            oldOwner,
            currentOwner,
            Query(IdentityMatchingDomain.Repertoire, identifier, artist)
        );
    }

    private async Task<Guid> Artist()
    {
        var source = await fixture.Source();
        return await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
    }

    private async Task<(CanonicalPerson Person, NormalizationWorkTicket Owner)> Person(string name)
    {
        var input = await fixture.Capture(name, "people");
        var person = new CanonicalPerson(Guid.NewGuid(), name);
        var owner = await Ticket(input, "people", "person", person.Uuid);
        await new NormalizedPeopleService(fixture.Db, fixture.Work).PublishAsync(
            owner.WorkUuid,
            new([person], [new(Guid.NewGuid(), person.Uuid, name, input.Reference)], [], [])
        );
        return (person, owner);
    }

    private async Task<NormalizationWorkTicket> Ticket(
        DomainInput input,
        string domain,
        string kind,
        Guid entity,
        Guid[]? dependencies = null
    )
    {
        var subject = new NormalizationSubjectKey(domain, kind, entity);
        var state = await fixture.Work.GetSubjectAsync(subject);
        return await fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            subject,
            "music-facts-v1",
            [input.Resource],
            dependencies ?? [],
            state?.DesiredGeneration
        );
    }

    // Synthetic retained redirects exercise historical ancestor facts that a strict new merge
    // command would require an operator to move explicitly. Only public facts reads are asserted.
    private Task Redirect(string table, Guid original, Guid survivor) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                $"UPDATE normalization.{table} SET redirect_uuid=@survivor WHERE uuid=@original",
                new { original, survivor }
            )
        );

    private static NormalizationCandidateQuery Query(
        IdentityMatchingDomain domain,
        MusicExternalIdentityChange identity,
        Guid? artist = null
    ) =>
        new(
            domain,
            [],
            [new(identity.Provider, identity.ResourceType, identity.ProviderKey)],
            artist
        );

    private static CompositionChange Work(NormalizedEvidenceReference evidence) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            DisplayTitle = "Work " + Guid.NewGuid().ToString("N"),
            Evidence = [evidence],
        };

    private static RepertoireSongChange Song(Guid artist, NormalizedEvidenceReference evidence) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            DisplayTitle = "Song " + Guid.NewGuid().ToString("N"),
            Basis = "title_derived",
            Evidence = [evidence],
        };

    private static MusicExternalIdentityChange Identity(
        Guid owner,
        bool song,
        NormalizedEvidenceReference evidence
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = song ? null : owner,
            RepertoireSongUuid = song ? owner : null,
            Provider = "fixture",
            ResourceType = song ? "song" : "work",
            ProviderKey = Guid.NewGuid().ToString("D"),
            Evidence = [evidence],
        };

    private static WorkCreditChange Credit(
        Guid work,
        string role,
        NormalizedEvidenceReference evidence
    ) =>
        new()
        {
            Uuid = Guid.NewGuid(),
            CompositionUuid = work,
            Role = role,
            OriginalText = "Retained " + role,
            Evidence = [evidence],
        };

    private sealed record RepertoireFixture(
        DomainInput Input,
        Guid Artist,
        RepertoireSongChange OldSong,
        RepertoireSongChange CurrentSong,
        CompositionChange OldWork,
        CompositionChange CurrentWork,
        NormalizationWorkTicket RepertoireOwner,
        NormalizationWorkTicket OldWorkOwner,
        NormalizationWorkTicket CurrentWorkOwner,
        NormalizationCandidateQuery Query
    );
}
