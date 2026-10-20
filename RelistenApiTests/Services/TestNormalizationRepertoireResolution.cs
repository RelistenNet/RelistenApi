using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>
/// Retained source claims become repertoire decisions through public durable entrypoints.
/// Any evaluated policy used here is a disposable fixture and authorizes no production rule.
/// </summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed partial class TestNormalizationRepertoireResolution
{
    private const string Operator = "repertoire-fixture-operator";
    private NormalizationDomainFixture fixture = null!;
    private NormalizedMusicService music = null!;
    private NormalizationReadService reads = null!;
    private RepertoireResolutionPolicyService policies = null!;
    private string? savedPhishMappings;
    private int? phishFixtureArtistId;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        music = new(fixture.Db, fixture.Work);
        reads = new(fixture.Db, Options.Create(new NormalizationReadOptions { Enabled = true }));
        policies = new(fixture.Db, fixture.Work, allowFixturePolicies: true);
    }

    [TearDown]
    public async Task ResetPolicy()
    {
        try
        {
            if (await policies.ReadActiveAsync() != null)
            {
                await SetPolicy(null);
            }
        }
        finally
        {
            await RestorePhishMappings();
        }
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task CompleteImportedDictionaryRetainsEveryStoredIdentityAndUnqualifiedProviderText()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var songUuid = await ImportedSong(artist, title, "unqualified-legacy-text");
        var unknownUuid = await ImportedSong(artist, "");
        var request = await Dictionary(artist);
        var before = await LegacySongs(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        resolved.Preparation!.Source.ArtistUuid.Should().Be(artist.Uuid);
        resolved
            .Preparation.Source.Claims.Select(claim => claim.ImportedSongUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { songUuid, unknownUuid });
        var decisions = await Decisions(resolved);
        var known = decisions.Single(decision =>
            Claim(resolved, decision).ImportedSongUuid == songUuid
        );
        known.RepertoireSongUuid.Should().NotBeNull();
        var accepted = (await music.GetRepertoireAsync(known.RepertoireSongUuid!.Value))!;
        accepted.Value.DisplayTitle.Should().Be(title);
        accepted.Value.ArtistUuid.Should().Be(artist.Uuid);
        (await music.GetImportedSongsAsync(accepted.Value.Uuid))
            .Should()
            .ContainSingle(mapping => mapping.Value.ImportedSongUuid == songUuid);
        decisions
            .Single(decision => Claim(resolved, decision).ImportedSongUuid == unknownUuid)
            .Disposition.Should()
            .Be(RepertoireResolutionClaimDisposition.Unresolved);
        (await music.GetCompositionLinksAsync(accepted.Value.Uuid)).Should().BeEmpty();
        (await LegacySongs(artist)).Should().Be(before);
        var identities = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM normalization.music_external_identities WHERE repertoire_song_uuid=@uuid AND provider_key='unqualified-legacy-text'",
                new { uuid = accepted.Value.Uuid }
            )
        );
        identities.Should().Be(0);
    }

    [Test]
    public async Task KnownEmptyDictionaryCompletesAndEnrollsItsObservationWithoutAllocatingSongs()
    {
        var artist = await Artist();
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        resolved.Preparation!.Source.Claims.Should().BeEmpty();
        (await Decisions(resolved)).Should().BeEmpty();
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        await AssertSourceSettled(resolved);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("!!! ???")]
    public async Task MissingOrNoiseDictionaryWordingStaysAnExplicitUnresolvedClaim(string title)
    {
        var artist = await Artist();
        var imported = await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decision = (await Decisions(resolved)).Should().ContainSingle().Which;
        decision.Disposition.Should().Be(RepertoireResolutionClaimDisposition.Unresolved);
        decision.RepertoireSongUuid.Should().BeNull();
        resolved.Preparation!.Source.Claims.Single().ImportedSongUuid.Should().Be(imported);
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        await AssertSourceSettled(resolved);
    }

    [Test]
    public async Task EqualTitlesAcrossArtistsNeverShareARepertoireIdentity()
    {
        var firstArtist = await Artist();
        var otherArtist = await Artist();
        var title = UniqueTitle();
        await ImportedSong(firstArtist, title);
        await ImportedSong(otherArtist, title);
        var firstRequest = await Dictionary(firstArtist);
        var otherRequest = await Dictionary(otherArtist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var otherUuid = Guid.NewGuid();

        var first = await Drain(() => processor.ResolveAsync(firstUuid, firstRequest));
        var other = await Drain(() => processor.ResolveAsync(otherUuid, otherRequest));

        var firstSong = (await Decisions(first)).Single().RepertoireSongUuid!.Value;
        var otherSong = (await Decisions(other)).Single().RepertoireSongUuid!.Value;
        firstSong.Should().NotBe(otherSong);
        (await music.GetRepertoireAsync(firstSong))!.Value.ArtistUuid.Should().Be(firstArtist.Uuid);
        (await music.GetRepertoireAsync(otherSong))!.Value.ArtistUuid.Should().Be(otherArtist.Uuid);
    }

    [Test]
    public async Task DistinctSameArtistDictionaryMembersWithTheSameTitleRemainSeparate()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var first = await ImportedSong(artist, title);
        var second = await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var decisions = await Decisions(resolved);
        decisions.Should().HaveCount(2);
        decisions.Select(decision => decision.RepertoireSongUuid).Should().OnlyHaveUniqueItems();
        decisions.Should().OnlyContain(decision => decision.RepertoireSongUuid != null);
        resolved
            .Preparation!.Source.Claims.Select(claim => claim.ImportedSongUuid)
            .Should()
            .BeEquivalentTo(new Guid?[] { first, second });
        (await music.SearchRepertoireAsync(artist.Uuid, title)).Should().HaveCount(2);
    }

    [Test]
    public async Task ExactReplayAndUnchangedNewAttemptsKeepAcceptedUuidsRevisionsAndCandidateEpochs()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        var before = (await music.SearchRepertoireAsync(artist.Uuid)).Single();
        var beforeEpoch = await Epoch(artist, title);
        var beforeOwner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;

        var replay = await processor.ResolveAsync(firstUuid, request);
        var nextUuid = Guid.NewGuid();
        var unchanged = await Drain(() => processor.ResolveAsync(nextUuid, request));

        replay.Result.Should().BeEquivalentTo(first.Result! with { Replayed = true });
        unchanged
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(unchanged));
        unchanged.Registration.RegistrationUuid.Should().Be(first.Registration.RegistrationUuid);
        (await Decisions(unchanged)).Single().RepertoireSongUuid.Should().Be(before.Value.Uuid);
        (await music.GetRepertoireAsync(before.Value.Uuid)).Should().BeEquivalentTo(before);
        (await Epoch(artist, title)).Should().BeEquivalentTo(beforeEpoch);
        (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!
            .PublishedWorkUuid.Should()
            .Be(beforeOwner.PublishedWorkUuid);
    }

    [Test]
    public async Task RetryUsesTheCurrentDictionaryWithoutRefreshingAnUnchangedRetainedHead()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        var resource = first.Preparation!.Source.Lineage;
        var before = (
            await new NormalizationEvidenceService(fixture.Db).GetCurrentInputAsync(resource)
        )!;
        var nextUuid = Guid.NewGuid();

        var retry = await processor.RetryAsync(nextUuid, first.Registration.RegistrationUuid);
        var resolved = await Drain(() => processor.ResolveAsync(nextUuid, retry.Request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        resolved.PreviousAttemptUuid.Should().Be(firstUuid);
        var current = (
            await new NormalizationEvidenceService(fixture.Db).GetCurrentInputAsync(resource)
        )!;
        current.Generation.Should().Be(before.Generation);
        current.Revision.Uuid.Should().Be(before.Revision.Uuid);
    }

    [TestCase("request")]
    [TestCase("scope")]
    public async Task AttemptReplayCannotChangeItsExactSourceOrArtistConstraint(string change)
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        await processor.ResolveAsync(attemptUuid, request);
        var changed =
            change == "scope"
                ? request with
                {
                    ExpectedArtistUuid = artist.Uuid,
                }
                : new RepertoireResolutionRequest(
                    new ImportedRepertoireResolutionSource(Guid.NewGuid())
                );

        await FluentActions
            .Invoking(() => processor.ResolveAsync(attemptUuid, changed))
            .Should()
            .ThrowAsync<NormalizationConflictException>();

        (await processor.GetAsync(attemptUuid))!.Request.Should().Be(request);
    }

    [Test]
    public async Task AnExpectedArtistCannotSupplyOrReplaceTheSourceArtist()
    {
        var artist = await Artist();
        var other = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = (await Dictionary(artist)) with { ExpectedArtistUuid = other.Uuid };

        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var unresolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        unresolved.Preparation!.Source.ArtistUuid.Should().BeNull();
        unresolved.Preparation.Source.Issues.Should().Contain("expected_artist_conflict");
        (await Decisions(unresolved))
            .Should()
            .ContainSingle()
            .Which.Disposition.Should()
            .Be(RepertoireResolutionClaimDisposition.Unresolved);
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        (await music.SearchRepertoireAsync(other.Uuid)).Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PublishedTitleSourcesCreateVisibleProvisionalsWithoutAnAcceptancePolicy(
        bool setlist
    )
    {
        var title = UniqueTitle();
        var source = await PublishedTitle(title, setlist);
        var before = await Playback(source.Catalog);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, source.Request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        resolved.Result.Provisional.Should().Be(1);
        var decision = (await Decisions(resolved)).Should().ContainSingle().Which;
        decision.Disposition.Should().Be(RepertoireResolutionClaimDisposition.Provisional);
        var accepted = (await music.GetRepertoireAsync(decision.RepertoireSongUuid!.Value))!;
        accepted.Value.Basis.Should().Be("title_derived");
        accepted.Value.DisplayTitle.Should().Be(title);
        accepted.Value.ArtistUuid.Should().Be(source.ArtistUuid);
        (await music.SearchRepertoireAsync(source.ArtistUuid, basis: "title_derived"))
            .Should()
            .ContainSingle(row => row.Value.Uuid == accepted.Value.Uuid);
        (await music.GetCompositionLinksAsync(accepted.Value.Uuid)).Should().BeEmpty();
        var acceptedStage = await FullStage(resolved, RepertoireResolutionStageKind.AcceptedWrite);
        var original = resolved.Preparation!.Source.ReadSet;
        original
            .Inputs.All(input => acceptedStage.ReadSet.Inputs.Contains(input))
            .Should()
            .BeTrue();
        original
            .Dependencies.All(dependency => acceptedStage.ReadSet.Dependencies.Contains(dependency))
            .Should()
            .BeTrue();
        original
            .CatalogInputs.All(input => acceptedStage.ReadSet.CatalogInputs.Contains(input))
            .Should()
            .BeTrue();
        original
            .SourceIdentities.All(identity =>
                acceptedStage.ReadSet.SourceIdentities.Contains(identity)
            )
            .Should()
            .BeTrue();
        acceptedStage.ReadSet.MappingRegistry.Should().Be(original.MappingRegistry);
        original
            .ImportedVenues.All(venue => acceptedStage.ReadSet.ImportedVenues.Contains(venue))
            .Should()
            .BeTrue();
        original
            .CandidateUniverses.All(universe =>
                acceptedStage.ReadSet.CandidateUniverses.Contains(universe)
            )
            .Should()
            .BeTrue();
        original
            .OwnerObservations.All(owner => acceptedStage.ReadSet.OwnerObservations.Contains(owner))
            .Should()
            .BeTrue();
        original
            .SourceUuids.All(source => acceptedStage.ReadSet.SourceUuids.Contains(source))
            .Should()
            .BeTrue();
        (await Playback(source.Catalog)).Should().Be(before);
        await AssertSourceSettled(resolved);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CompletedProvisionalSourceObservesInactivePolicyAndReplaysWithoutFresheningItsProof(
        bool setlist
    )
    {
        var source = await PublishedTitle(UniqueTitle(), setlist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, source.Request));
        resolved.Preparation!.Policy.Active.Should().BeNull();
        var acknowledgment = await FullStage(
            resolved,
            RepertoireResolutionStageKind.AcknowledgeSource
        );
        var before = (await music.SearchRepertoireAsync(source.ArtistUuid)).Single();
        await AssertSourceSettled(resolved);

        await ActivatePolicy();

        (await Pending(acknowledgment.Ticket!.SubjectUuid)).Should().BeTrue();
        var replay = await processor.ResolveAsync(attemptUuid, source.Request);
        replay.Result.Should().BeEquivalentTo(resolved.Result! with { Replayed = true });
        replay.Preparation.Should().BeEquivalentTo(resolved.Preparation);
        (await music.GetRepertoireAsync(before.Value.Uuid)).Should().BeEquivalentTo(before);
        (await Pending(acknowledgment.Ticket.SubjectUuid)).Should().BeTrue();
    }

    [Test]
    public async Task AChangedImportedTitleReusesItsAcceptedCatalogMappingWithoutMovingLegacyIdentity()
    {
        var artist = await Artist();
        var importedUuid = await ImportedSong(artist, UniqueTitle());
        var originalRequest = await Dictionary(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var original = await Drain(() => processor.ResolveAsync(firstUuid, originalRequest));
        var target = (await Decisions(original)).Single().RepertoireSongUuid!.Value;
        var revisedTitle = UniqueTitle();
        await Execute(
            "UPDATE setlist_songs SET name=@revisedTitle WHERE uuid=@importedUuid",
            new { revisedTitle, importedUuid }
        );
        var currentRequest = await Dictionary(artist);
        var nextUuid = Guid.NewGuid();

        var current = await Drain(() => processor.ResolveAsync(nextUuid, currentRequest));

        current
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(current));
        current.Registration.RegistrationUuid.Should().Be(original.Registration.RegistrationUuid);
        var decision = (await Decisions(current)).Single();
        decision.RepertoireSongUuid.Should().Be(target);
        current.Preparation!.Source.Claims.Single().Title!.Value.Should().Be(revisedTitle);
        current.Preparation.Source.Claims.Single().ImportedSongUuid.Should().Be(importedUuid);
        (await music.GetImportedSongsAsync(target))
            .Should()
            .ContainSingle(row => row.Value.ImportedSongUuid == importedUuid);
        AssertPublicSummary((await processor.GetAsync(firstUuid))!, original.Preparation!);
    }

    [Test]
    public async Task CheckedProviderDictionaryCarriesQualifiedIdsAndItsMappedArtist()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var request = await ProviderDictionary(artist, title);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        resolved.Preparation!.Source.ArtistUuid.Should().Be(artist.Uuid);
        var decision = (await Decisions(resolved)).Single();
        var accepted = (await music.GetRepertoireAsync(decision.RepertoireSongUuid!.Value))!;
        accepted.Value.DisplayTitle.Should().Be(title);
        accepted.Value.ArtistUuid.Should().Be(artist.Uuid);
        var identities = await fixture.Db.WithWriteConnection(c =>
            c.QueryAsync<string>(
                "SELECT provider_key FROM normalization.music_external_identities WHERE repertoire_song_uuid=@uuid AND provider='phish.in' AND resource_type='song' AND NOT retracted",
                new { uuid = accepted.Value.Uuid }
            )
        );
        identities
            .Should()
            .Contain(
                resolved
                    .Preparation.Source.Claims.Single()
                    .Identities.Single(identity =>
                        identity.Identity.Provider == "phish.in"
                        && identity.Identity.ResourceType == "song"
                    )
                    .Identity.ProviderKey
            );
        resolved.Preparation.Source.ReadSet.MappingRegistry.Should().NotBeNull();
        resolved
            .Preparation.Source.ReadSet.CatalogInputs.Should()
            .Contain(input => input.ArtistUuid == artist.Uuid);
    }

    [Test]
    public async Task ExplicitProviderAliasRetainsItsOwnClaimAndCitedRelationshipToTheAcceptedSong()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var request = await ProviderDictionary(artist, title);
        await ActivatePolicy();
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        var target = (await Decisions(first)).Single().RepertoireSongUuid!.Value;
        var baseId = int.Parse(
            first
                .Preparation!.Source.Claims.Single()
                .Identities.Single(identity => identity.Identity.Provider == "phish.in")
                .Identity.ProviderKey
        );
        var aliasTitle = UniqueTitle();
        var aliasedRequest = await CaptureProviderDictionary(
            new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = baseId,
                        title,
                        alias_for = (int?)null,
                    },
                    new
                    {
                        id = Random.Shared.Next(1_000_000, int.MaxValue),
                        title = aliasTitle,
                        alias_for = (int?)baseId,
                    },
                },
            }
        );
        var nextUuid = Guid.NewGuid();

        var aliased = await Drain(() => processor.ResolveAsync(nextUuid, aliasedRequest));

        aliased
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(aliased));
        var decisions = await Decisions(aliased);
        decisions
            .Should()
            .HaveCount(2)
            .And.OnlyContain(decision => decision.RepertoireSongUuid == target);
        var aliasClaim = aliased.Preparation!.Source.Claims.Single(claim => claim.AliasOf != null);
        aliasClaim.Title!.Value.Should().Be(aliasTitle);
        aliasClaim.AliasOf!.Identity.ProviderKey.Should().Be(baseId.ToString());
        decisions
            .Single(decision => decision.ClaimUuid == aliasClaim.ClaimUuid)
            .Evidence.Should()
            .Contain(aliasClaim.AliasOf.Evidence);
        (await music.GetRepertoireAsync(target))!.Value.Aliases.Should().Contain(aliasTitle);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReviewedRelationshipRetractionCannotBeBypassedByAllocatingAnotherIdentity(
        bool externalIdentity
    )
    {
        var artist = await Artist();
        var title = UniqueTitle();
        RepertoireResolutionRequest request;
        if (externalIdentity)
        {
            request = await ProviderDictionary(artist, title);
        }
        else
        {
            await ImportedSong(artist, title);
            request = await Dictionary(artist);
        }
        await ActivatePolicy(titleReuse: true);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        var songUuid = (await Decisions(first)).Single().RepertoireSongUuid!.Value;
        var accepted = (await music.GetRepertoireAsync(songUuid))!;
        var owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        Guid relationshipUuid;
        MusicPublication retraction;
        if (externalIdentity)
        {
            var current = await fixture.Db.WithWriteConnection(c =>
                c.QuerySingleAsync<HistoricalIdentity>(
                    """
                    SELECT x.uuid AS Uuid,x.revision_uuid AS RevisionUuid,r.revision AS Revision,r.payload::text AS Payload
                    FROM normalization.music_external_identities x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                    WHERE x.repertoire_song_uuid=@songUuid AND x.provider='phish.in' AND NOT x.retracted
                    """,
                    new { songUuid }
                )
            );
            relationshipUuid = current.Uuid;
            var value = JsonSerializer.Deserialize<MusicExternalIdentityChange>(current.Payload)!;
            retraction = new()
            {
                ExternalIdentities =
                [
                    value with
                    {
                        ExpectedRevision = current.Revision,
                        Retracted = true,
                        Basis = "reviewed",
                    },
                ],
            };
        }
        else
        {
            var current = (await music.GetImportedSongsAsync(songUuid)).Single();
            relationshipUuid = current.Value.Uuid;
            retraction = new()
            {
                ImportedSongs =
                [
                    current.Value with
                    {
                        ExpectedRevision = current.Revision,
                        Retracted = true,
                        Basis = "reviewed",
                    },
                ],
            };
        }
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "retract",
            retraction,
            "Reviewed removal of this exact source relationship",
            Operator
        );
        var before = await RelationshipState(relationshipUuid, externalIdentity);
        (await music.GetRepertoireAsync(songUuid)).Should().BeEquivalentTo(accepted);
        var nextUuid = Guid.NewGuid();

        var reviewed = await Drain(() => processor.ResolveAsync(nextUuid, request));

        reviewed
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(reviewed));
        var decision = (await Decisions(reviewed)).Should().ContainSingle().Which;
        decision.Disposition.Should().Be(RepertoireResolutionClaimDisposition.Review);
        decision.RepertoireSongUuid.Should().BeNull();
        decision
            .Reason.Should()
            .Be(
                externalIdentity
                    ? "reviewed_external_identity_retracted"
                    : "reviewed_imported_assignment_retracted"
            );
        reviewed
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcceptedWrite);
        (await RelationshipState(relationshipUuid, externalIdentity)).Should().Be(before);
        (await music.SearchRepertoireAsync(artist.Uuid))
            .Should()
            .ContainSingle()
            .Which.Value.Uuid.Should()
            .Be(songUuid);
        (await music.GetRepertoireAsync(songUuid)).Should().BeEquivalentTo(accepted);
        await AssertSourceSettled(reviewed);
    }

    [Test]
    public async Task LaterPageCreatingAnAliasTargetMakesTheEarlierNegativeStaleAndRetryReusesEveryAcceptedSong()
    {
        var artist = await Artist();
        await IsolatePhishMappings(artist);
        await MapArtist(artist, "phish.in", Guid.NewGuid().ToString("N"));
        var firstId = Random.Shared.Next(2_000_000, 1_000_000_000);
        var targetId = firstId + 100;
        var aliasTitle = UniqueTitle();
        var targetTitle = UniqueTitle();
        var rows = new[]
        {
            new
            {
                id = firstId,
                title = aliasTitle,
                alias_for = (int?)targetId,
            },
        }.ToList();
        for (var index = 1; index <= 99; index++)
        {
            rows.Add(
                new
                {
                    id = firstId + index,
                    title = UniqueTitle(),
                    alias_for = (int?)null,
                }
            );
        }
        rows.Add(
            new
            {
                id = targetId,
                title = targetTitle,
                alias_for = (int?)null,
            }
        );
        var request = (await CaptureProviderDictionary(new { success = true, data = rows })) with
        {
            ExpectedArtistUuid = artist.Uuid,
        };
        await ActivatePolicy();
        var processor = Processor();
        var firstUuid = Guid.NewGuid();

        var stale = await Drain(() => processor.ResolveAsync(firstUuid, request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        stale
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource);
        var aliasClaim = stale.Preparation!.Source.Claims.First();
        aliasClaim.AliasOf!.Identity.ProviderKey.Should().Be(targetId.ToString());
        stale
            .Preparation.Source.Claims.Last()
            .Identities.Should()
            .Contain(identity => identity.Identity.ProviderKey == targetId.ToString());
        var saved = await Decisions(stale);
        var priorAccepted = saved.Where(decision => decision.RepertoireSongUuid != null).ToArray();
        priorAccepted.Should().HaveCount(100);
        var before = await music.SearchRepertoireAsync(artist.Uuid, limit: 200);
        before.Should().HaveCount(100);
        var targetClaim = stale.Preparation.Source.Claims.Single(claim =>
            claim.Identities.Any(identity => identity.Identity.ProviderKey == targetId.ToString())
        );
        var targetUuid = priorAccepted
            .Single(decision => decision.ClaimUuid == targetClaim.ClaimUuid)
            .RepertoireSongUuid!.Value;
        var store = new NormalizationRepertoireResolutionStore(fixture.Db);
        (await store.GetRegistrationAsync(stale.Registration.RegistrationUuid))!
            .CurrentAttemptUuid.Should()
            .BeNull();
        var retryUuid = Guid.NewGuid();

        var retry = await processor.RetryAsync(retryUuid, stale.Registration.RegistrationUuid);
        var completed = await Drain(() => processor.ResolveAsync(retryUuid, retry.Request));

        completed
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(completed));
        var current = await Decisions(completed);
        current.Should().HaveCount(101);
        foreach (var previous in priorAccepted)
        {
            current
                .Single(decision => decision.CorrespondenceKey == previous.CorrespondenceKey)
                .RepertoireSongUuid.Should()
                .Be(previous.RepertoireSongUuid);
        }
        current
            .Single(decision => decision.CorrespondenceKey == aliasClaim.CorrespondenceKey)
            .RepertoireSongUuid.Should()
            .Be(targetUuid);
        var after = await music.SearchRepertoireAsync(artist.Uuid, limit: 200);
        after.Should().HaveCount(100);
        after
            .Select(song => song.Value.Uuid)
            .Should()
            .BeEquivalentTo(before.Select(song => song.Value.Uuid));
        (await music.GetRepertoireAsync(targetUuid))!.Value.Aliases.Should().Contain(aliasTitle);
        (await processor.GetAsync(firstUuid))!.Result.Should().BeEquivalentTo(stale.Result);
        await AssertSourceSettled(completed);
    }

    [Test]
    public async Task ExpectedArtistCannotTurnAMappedProviderDictionaryIntoAnotherArtistsDictionary()
    {
        var artist = await Artist();
        var other = await Artist();
        var request = (await ProviderDictionary(artist, UniqueTitle())) with
        {
            ExpectedArtistUuid = other.Uuid,
        };

        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var unresolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        unresolved.Preparation!.Source.ArtistUuid.Should().BeNull();
        unresolved.Preparation.Source.Issues.Should().Contain("expected_artist_conflict");
        (await Decisions(unresolved))
            .Should()
            .ContainSingle()
            .Which.Disposition.Should()
            .Be(RepertoireResolutionClaimDisposition.Unresolved);
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        (await music.SearchRepertoireAsync(other.Uuid)).Should().BeEmpty();
    }

    [Test]
    public async Task EquivalentProviderSubscriptionsDoNotRepublishArtistStateAndRetireTheirTransientKeys()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        var request = await ProviderDictionary(artist, title);
        await ActivatePolicy();
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        var target = (await Decisions(first)).Single().RepertoireSongUuid;
        var beforeOwner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        var beforeEpoch = await Epoch(artist, title);
        var secondRequest = request with { ExpectedArtistUuid = artist.Uuid };
        var nextUuid = Guid.NewGuid();

        var second = await Drain(() => processor.ResolveAsync(nextUuid, secondRequest));

        second
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(second));
        second.Registration.RegistrationUuid.Should().NotBe(first.Registration.RegistrationUuid);
        (await Decisions(second)).Single().RepertoireSongUuid.Should().Be(target);
        (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!
            .PublishedWorkUuid.Should()
            .Be(beforeOwner.PublishedWorkUuid);
        (await Epoch(artist, title)).Should().BeEquivalentTo(beforeEpoch);
        second
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcceptedWrite);
        var membership = await fixture.Work.GetInputContributionsAsync(ArtistOwner(artist.Uuid));
        membership
            .Contributions.SelectMany(contribution => contribution.Resources)
            .Should()
            .NotContain(first.Preparation!.Source.Lineage);
        membership
            .Contributions.Should()
            .OnlyContain(contribution =>
                contribution.Resources.Count == 0
                && contribution.DependencySubjectUuids.Count == 0
                && contribution.CatalogArtistUuids.Count == 0
                && (
                    contribution.OwnerObservationSubjectUuids == null
                    || contribution.OwnerObservationSubjectUuids.Count == 0
                )
            );
        await AssertSourceSettled(second);
    }

    [Test]
    public async Task MaterializationAndRetirementPreserveExistingArtistBaselineAndPeerMembership()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var baseline = await fixture.Capture("Unmanaged artist baseline", "songs");
        baseline = await fixture.Own(baseline, "artist_repertoire", artist.Uuid);
        var peerInput = await fixture.Capture("Independent artist peer", "songs");
        var peerKey = new NormalizationResourceKey(
            "test",
            "repertoire-peer",
            Guid.NewGuid().ToString("N")
        );
        var owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            ArtistOwner(artist.Uuid),
            "repertoire-peer-fixture-v1",
            new(peerKey, 0, [peerInput.Resource], []),
            owner.DesiredGeneration,
            expectedInputRevisions: [new(peerInput.Reference.InputRevisionUuid, peerInput.Resource)]
        );
        var before = await fixture.Work.GetInputContributionsAsync(ArtistOwner(artist.Uuid));
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        var after = await fixture.Work.GetInputContributionsAsync(ArtistOwner(artist.Uuid));
        foreach (var prior in before.Contributions)
        {
            var preserved = after.Contributions.Single(contribution =>
                contribution.Contributor == prior.Contributor
            );
            preserved
                .Should()
                .BeEquivalentTo(
                    prior,
                    options => options.Excluding(contribution => contribution.Revision)
                );
            preserved.Revision.Should().BeGreaterThan(prior.Revision);
        }
        var materializer = after.Contributions.Single(contribution =>
            contribution.Contributor.Provider == "relisten"
            && contribution.Contributor.ResourceType == "repertoire_materializer"
        );
        AssertRetiredBindings(materializer);
        after
            .Contributions.SelectMany(contribution => contribution.Resources)
            .Should()
            .Contain([baseline.Resource, peerInput.Resource]);
        await AssertSourceSettled(resolved);
    }

    [TestCase("input")]
    [TestCase("catalog")]
    [TestCase("candidate")]
    [TestCase("owner_editorial")]
    [TestCase("policy")]
    public async Task OriginalReadFamiliesRejectChangesBeforeAcceptedPublication(string change)
    {
        var artist = await Artist();
        var title = UniqueTitle();
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, request);
        var stage = admitted.Stages.Single(stage =>
            stage.Kind == RepertoireResolutionStageKind.AcceptedWrite
        );
        if (change == "input")
        {
            await AdvanceInput(stage.ReadSet.Inputs.First());
        }
        else if (change == "catalog")
        {
            await Execute("UPDATE artists SET name=name||' changed' WHERE uuid=@Uuid", artist);
        }
        else if (change == "candidate")
        {
            await AcceptedSong(artist, title);
        }
        else if (change == "owner_editorial")
        {
            var owner = (await fixture.Work.GetSubjectAsync(stage.Ticket!.SubjectUuid))!;
            await fixture.Work.ApplyReviewAsync(
                new(
                    Guid.NewGuid(),
                    owner.SubjectUuid,
                    owner.EditorialRevision,
                    "accept",
                    JsonSerializer.SerializeToElement(new { note = "parallel repertoire review" }),
                    "Changed owner after resolver admission",
                    Operator
                ),
                (_, _, _) => Task.CompletedTask
            );
        }
        else
        {
            await ActivatePolicy();
        }

        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        stale.Preparation.Should().BeEquivalentTo(admitted.Preparation);
        stale
            .Stages.Where(value => value.Kind == RepertoireResolutionStageKind.AcceptedWrite)
            .Should()
            .OnlyContain(value => value.Receipt == null);
        stale
            .Stages.Should()
            .NotContain(value => value.Kind == RepertoireResolutionStageKind.AcknowledgeSource);
        foreach (var song in stage.Publication.RepertoireSongs)
        {
            (await music.GetRepertoireAsync(song.Uuid)).Should().BeNull();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OriginalPublishedSourceOwnerCannotBeFreshenedAfterItsDecisionWasAdmitted(
        bool setlist
    )
    {
        var source = await PublishedTitle(UniqueTitle(), setlist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, source.Request);
        var publicationUuid = source.Request.Source switch
        {
            RecordingRepertoireResolutionSource recording =>
                recording.PublishedProcessingAttemptUuid,
            SetlistRepertoireResolutionSource account => account.PublishedProcessingAttemptUuid,
            _ => throw new AssertionException("Expected a published title source."),
        };
        var originalOwner = admitted.Preparation!.Source.ReadSet.Dependencies.Single(dependency =>
            dependency.PublishedWorkUuid == publicationUuid
        );
        var owner = (await fixture.Work.GetSubjectAsync(originalOwner.SubjectUuid))!;
        await fixture.Work.ApplyReviewAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                "accept",
                JsonSerializer.SerializeToElement(new { note = "changed source interpretation" }),
                "Reviewed source after repertoire preparation",
                Operator
            ),
            (_, _, _) => Task.CompletedTask
        );

        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, source.Request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        stale.Preparation.Should().BeEquivalentTo(admitted.Preparation);
        (await music.SearchRepertoireAsync(source.ArtistUuid)).Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SourceUuidAndMappingRegistryFamiliesFenceTheirActualPublishedSourceForms(
        bool setlist
    )
    {
        var source = await PublishedTitle(UniqueTitle(), setlist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, source.Request);
        if (setlist)
        {
            admitted.Preparation!.Source.ReadSet.MappingRegistry.Should().NotBeNull();
            await MapArtist(
                await Artist(),
                "repertoire-registry-fixture-" + Guid.NewGuid().ToString("N"),
                "new-association"
            );
        }
        else
        {
            admitted
                .Preparation!.Source.ReadSet.SourceUuids.Should()
                .Contain(dependency => dependency.SourceUuid == source.Catalog.SourceUuid);
            await Execute(
                "UPDATE sources SET uuid=@replacement WHERE id=@SourceId",
                new { replacement = Guid.NewGuid(), source.Catalog.SourceId }
            );
        }

        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, source.Request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        stale.Preparation.Should().BeEquivalentTo(admitted.Preparation);
        (await music.SearchRepertoireAsync(source.ArtistUuid)).Should().BeEmpty();
    }

    [Test]
    public async Task FailedAcceptedPublicationRetiresItsArtistBindingsBeforeBecomingARecoverableStaleAttempt()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, request);
        var accepted = admitted.Stages.Single(stage =>
            stage.Kind == RepertoireResolutionStageKind.AcceptedWrite
        );
        await AdvanceInput(admitted.Preparation!.Source.ReadSet.Inputs.First());

        var failed = await processor.ResolveAsync(attemptUuid, request);

        failed
            .Result.Should()
            .BeNull("failed publication must release its temporary artist subscription first");
        failed
            .Stages.Single(stage => stage.StageUuid == accepted.StageUuid)
            .FailureReason.Should()
            .NotBeNullOrWhiteSpace();
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, request));
        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        stale
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource);
        var store = new NormalizationRepertoireResolutionStore(fixture.Db);
        var originalStage = (await store.GetStageAsync(attemptUuid, accepted.StageUuid))!;
        originalStage.FailureReason.Should().NotBeNullOrWhiteSpace();
        originalStage.Receipt.Should().BeNull();
        var retirement = await FullStage(
            stale,
            RepertoireResolutionStageKind.RetireArtistContribution
        );
        retirement.Receipt!.Status.Should().Be(NormalizationPublicationStatus.Published);
        retirement.Publication.Should().BeEquivalentTo(new MusicPublication());
        var membership = await fixture.Work.GetInputContributionsAsync(ArtistOwner(artist.Uuid));
        AssertRetiredBindings(
            membership.Contributions.Single(contribution =>
                contribution.Contributor.Provider == "relisten"
                && contribution.Contributor.ResourceType == "repertoire_materializer"
            )
        );
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        (await Decisions(stale)).Should().BeEmpty();
        (await Recovery(store))
            .Select(attempt => attempt.AttemptUuid)
            .Should()
            .Contain(attemptUuid);
        AssertPublicSummary((await processor.GetAsync(attemptUuid))!, admitted.Preparation!);
    }

    [Test]
    public async Task EqualByteRecaptureBeforeAdmissionRecordsTheActualTicketAndReleasesItsBindings()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var decided = await UntilStage(
            processor,
            attemptUuid,
            request,
            RepertoireResolutionStageKind.DecisionPage,
            admitted: false
        );
        decided
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcceptedWrite);
        var original = decided.Preparation!.Source.ReadSet.Inputs.First();
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var retained = (await evidence.GetInputAsync(original.RevisionUuid))!;

        var recaptured = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                retained.Resource,
                retained.Payload,
                retained.MediaType,
                retained.CaptureVersion,
                original.Generation,
                DateTimeOffset.UtcNow
            )
        );
        recaptured.Revision.Uuid.Should().Be(original.RevisionUuid);
        recaptured.Fetch.ResourceGeneration.Should().BeGreaterThan(original.Generation);
        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        var failed = await FullStage(stale, RepertoireResolutionStageKind.AcceptedWrite);
        failed.FailureReason.Should().NotBeNullOrWhiteSpace();
        failed.Receipt.Should().BeNull();
        failed
            .Ticket!.Inputs.Single(input => input.ResourceUuid == original.ResourceUuid)
            .Generation.Should()
            .Be(recaptured.Fetch.ResourceGeneration);
        failed
            .ReadSet.Inputs.Single(input => input.ResourceUuid == original.ResourceUuid)
            .Generation.Should()
            .Be(original.Generation);
        var membership = await fixture.Work.GetInputContributionsAsync(ArtistOwner(artist.Uuid));
        AssertRetiredBindings(
            membership.Contributions.Single(contribution =>
                contribution.Contributor.Provider == "relisten"
                && contribution.Contributor.ResourceType == "repertoire_materializer"
            )
        );
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        stale
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource);
    }

    [Test]
    public async Task ANewCandidateAfterANegativePageCannotBeFreshenedIntoItsSourceAcknowledgment()
    {
        var artist = await Artist();
        await ImportedSong(artist, "!!! ???");
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var decided = await UntilStage(
            processor,
            attemptUuid,
            request,
            RepertoireResolutionStageKind.DecisionPage,
            admitted: false
        );
        decided
            .Stages.Single(stage => stage.Kind == RepertoireResolutionStageKind.DecisionPage)
            .Decisions.Should()
            .ContainSingle()
            .Which.Disposition.Should()
            .Be(RepertoireResolutionClaimDisposition.Unresolved);

        var reviewed = await AcceptedSong(artist, UniqueTitle());
        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        stale.Preparation.Should().BeEquivalentTo(decided.Preparation);
        stale
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource);
        (await Decisions(stale)).Should().BeEmpty();
        (await music.SearchRepertoireAsync(artist.Uuid))
            .Should()
            .ContainSingle()
            .Which.Value.Uuid.Should()
            .Be(reviewed);
        (
            await new NormalizationRepertoireResolutionStore(fixture.Db).GetRegistrationAsync(
                stale.Registration.RegistrationUuid
            )
        )!
            .CurrentAttemptUuid.Should()
            .BeNull();
    }

    [Test]
    public async Task StaleRetirementPeerSnapshotIsRetriedWithFreshPeersBeforeFailedSourceCompletion()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var peer = await fixture.Capture("Original peer resource", "songs");
        var peerKey = new NormalizationResourceKey(
            "test",
            "repertoire-retirement-peer",
            Guid.NewGuid().ToString("N")
        );
        await fixture.Work.BeginContributedWorkAsync(
            Guid.NewGuid(),
            ArtistOwner(artist.Uuid),
            "retirement-peer-fixture-v1",
            new(peerKey, 0, [peer.Resource], []),
            0,
            expectedInputRevisions: [new(peer.Reference.InputRevisionUuid, peer.Resource)]
        );
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, request);
        await AdvanceInput(admitted.Preparation!.Source.ReadSet.Inputs.First());
        var retiring = await UntilStage(
            processor,
            attemptUuid,
            request,
            RepertoireResolutionStageKind.RetireArtistContribution,
            admitted: true
        );
        var retirement = retiring.Stages.Single(stage =>
            stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution
        );
        var peerDependency = retirement.Ticket!.Inputs.Single(input =>
            input.RevisionUuid == peer.Reference.InputRevisionUuid
        );

        await AdvanceInput(peerDependency);
        var stale = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale, Audit(stale));
        var cleanupStages = stale
            .Stages.Where(stage =>
                stage.Kind == RepertoireResolutionStageKind.RetireArtistContribution
            )
            .ToArray();
        cleanupStages.Should().HaveCountGreaterThanOrEqualTo(2);
        cleanupStages
            .Should()
            .Contain(stage =>
                stage.StageUuid == retirement.StageUuid && stage.FailureReason != null
            );
        cleanupStages
            .Should()
            .Contain(stage =>
                stage.Receipt != null
                && stage.Receipt.Status == NormalizationPublicationStatus.Published
            );
        var membership = await fixture.Work.GetInputContributionsAsync(ArtistOwner(artist.Uuid));
        AssertRetiredBindings(
            membership.Contributions.Single(contribution =>
                contribution.Contributor.Provider == "relisten"
                && contribution.Contributor.ResourceType == "repertoire_materializer"
            )
        );
        membership
            .Contributions.Single(contribution => contribution.Contributor == peerKey)
            .Resources.Should()
            .Equal(peer.Resource);
        var owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        (await Pending(owner.SubjectUuid)).Should().BeFalse();
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        stale
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcknowledgeSource);
    }

    [Test]
    public async Task HistoricalAncestorRelationshipsResolveToTheSurvivorWithoutRewritingTheirStoredProof()
    {
        var artist = await Artist();
        var title = UniqueTitle();
        await ImportedSong(artist, title);
        var request = await Dictionary(artist);
        var processor = Processor();
        var firstUuid = Guid.NewGuid();
        var first = await Drain(() => processor.ResolveAsync(firstUuid, request));
        var originalUuid = (await Decisions(first)).Single().RepertoireSongUuid!.Value;
        var original = (await music.GetRepertoireAsync(originalUuid))!;
        var assignment = (await music.GetImportedSongsAsync(originalUuid)).Single();
        var assignmentRevision = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>(
                "SELECT revision_uuid FROM normalization.imported_song_assignments WHERE uuid=@Uuid",
                assignment.Value
            )
        );
        var identity = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<HistoricalIdentity>(
                """
                SELECT x.uuid AS Uuid,x.revision_uuid AS RevisionUuid,r.revision AS Revision,r.payload::text AS Payload
                FROM normalization.music_external_identities x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                WHERE x.repertoire_song_uuid=@originalUuid AND NOT x.retracted
                """,
                new { originalUuid }
            )
        );
        var identityValue = JsonSerializer.Deserialize<MusicExternalIdentityChange>(
            identity.Payload
        )!;
        var survivorUuid = await AcceptedSong(artist, title);
        var owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "replace",
            new()
            {
                ImportedSongs =
                [
                    assignment.Value with
                    {
                        ExpectedRevision = assignment.Revision,
                        RepertoireSongUuid = survivorUuid,
                        Basis = "reviewed",
                    },
                ],
                ExternalIdentities =
                [
                    identityValue with
                    {
                        ExpectedRevision = identity.Revision,
                        Retracted = true,
                        Basis = "reviewed",
                    },
                ],
            },
            "Reassign the imported mapping and retract the external assertion before the reviewed merge",
            Operator
        );
        owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        var survivor = (await music.GetRepertoireAsync(survivorUuid))!;
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "merge",
            new()
            {
                Merges =
                [
                    new("songs", originalUuid, survivorUuid, original.Revision, survivor.Revision),
                ],
            },
            "Reviewed equivalent song identities",
            Operator
        );

        // Historical repair fixture: normal review creates the canonical redirect after each
        // active relationship is reassigned or retracted. Restore only the pre-existing current
        // relationships to their exact pre-merge owner, revision and active state; the external
        // identity keeps its immutable owner. No evidence or immutable history is fabricated or changed.
        await Execute(
            """
            UPDATE normalization.imported_song_assignments
            SET repertoire_song_uuid=@originalUuid,revision_uuid=@assignmentRevision WHERE uuid=@assignmentUuid;
            UPDATE normalization.music_external_identities
            SET repertoire_song_uuid=@originalUuid,revision_uuid=@identityRevision,retracted=false WHERE uuid=@identityUuid
            """,
            new
            {
                originalUuid,
                assignmentRevision,
                assignmentUuid = assignment.Value.Uuid,
                identityRevision = identity.RevisionUuid,
                identityUuid = identity.Uuid,
            }
        );
        var before = await HistoricalRelationships(assignment.Value.Uuid, identity.Uuid);
        var beforeSong = (await music.GetRepertoireAsync(survivorUuid))!;
        var beforeEpoch = await Epoch(artist, title);
        var nextUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(nextUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        (await Decisions(resolved)).Single().RepertoireSongUuid.Should().Be(survivorUuid);
        resolved
            .Stages.Should()
            .NotContain(stage => stage.Kind == RepertoireResolutionStageKind.AcceptedWrite);
        (await HistoricalRelationships(assignment.Value.Uuid, identity.Uuid)).Should().Be(before);
        (await music.GetRepertoireAsync(survivorUuid)).Should().BeEquivalentTo(beforeSong);
        (await Epoch(artist, title)).Should().BeEquivalentTo(beforeEpoch);
        (await music.SearchRepertoireAsync(artist.Uuid))
            .Should()
            .ContainSingle()
            .Which.Value.Uuid.Should()
            .Be(survivorUuid);
        (await music.GetRepertoireAsync(originalUuid))!.RedirectUuid.Should().Be(survivorUuid);
    }

    [Test]
    public async Task AcceptedRowsAssociationsAndStageReceiptRollbackTogetherAndReuseSavedUuids()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, request);
        var stage = admitted.Stages.Single(value =>
            value.Kind == RepertoireResolutionStageKind.AcceptedWrite
        );
        var targetUuid = stage.Publication.RepertoireSongs.Single().Uuid;
        var trigger = "repertoire_receipt_failure_" + Guid.NewGuid().ToString("N");
        await Execute(
            $"""
            CREATE FUNCTION normalization.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.stage_uuid='{stage.StageUuid:D}'::uuid THEN
                    RAISE EXCEPTION 'synthetic repertoire receipt failure';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER {trigger} BEFORE UPDATE OF receipt ON normalization.repertoire_resolution_stages
                FOR EACH ROW EXECUTE FUNCTION normalization.{trigger}();
            """
        );
        try
        {
            var failure = await FluentActions
                .Invoking(() =>
                    music.PublishRepertoireResolutionAsync(
                        stage.WorkUuid,
                        attemptUuid,
                        stage.StageUuid
                    )
                )
                .Should()
                .ThrowAsync<Exception>();
            failure
                .Which.InnerException.Should()
                .BeOfType<PostgresException>()
                .Which.MessageText.Should()
                .Be("synthetic repertoire receipt failure");
            (await music.GetRepertoireAsync(targetUuid)).Should().BeNull();
            (await Decisions(admitted)).Should().BeEmpty();
            (await processor.GetAsync(attemptUuid))!
                .Stages.Single(value => value.Kind == RepertoireResolutionStageKind.AcceptedWrite)
                .Receipt.Should()
                .BeNull();
        }
        finally
        {
            await Execute(
                $"""
                DROP TRIGGER {trigger} ON normalization.repertoire_resolution_stages;
                DROP FUNCTION normalization.{trigger}();
                """
            );
        }

        var recovered = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        recovered
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(recovered));
        (await Decisions(recovered)).Single().RepertoireSongUuid.Should().Be(targetUuid);
        (await music.GetRepertoireAsync(targetUuid)).Should().NotBeNull();
    }

    [Test]
    public async Task SavedAcceptedStageRequiresItsOwnAttemptWorkAndStageCoordinates()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, attemptUuid, request);
        var stage = admitted.Stages.Single(value =>
            value.Kind == RepertoireResolutionStageKind.AcceptedWrite
        );

        await FluentActions
            .Invoking(() =>
                music.PublishRepertoireResolutionAsync(Guid.NewGuid(), attemptUuid, stage.StageUuid)
            )
            .Should()
            .ThrowAsync<Exception>();
        await FluentActions
            .Invoking(() =>
                music.PublishRepertoireResolutionAsync(
                    stage.WorkUuid,
                    Guid.NewGuid(),
                    stage.StageUuid
                )
            )
            .Should()
            .ThrowAsync<Exception>();
        await FluentActions
            .Invoking(() =>
                music.PublishRepertoireResolutionAsync(stage.WorkUuid, attemptUuid, Guid.NewGuid())
            )
            .Should()
            .ThrowAsync<Exception>();
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        (await processor.GetAsync(attemptUuid))!
            .Stages.Single(value => value.Kind == RepertoireResolutionStageKind.AcceptedWrite)
            .Receipt.Should()
            .BeNull();

        (await music.PublishRepertoireResolutionAsync(stage.WorkUuid, attemptUuid, stage.StageUuid))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        (await music.PublishRepertoireResolutionAsync(stage.WorkUuid, attemptUuid, stage.StageUuid))
            .Replayed.Should()
            .BeTrue();
    }

    [Test]
    public async Task CancellationLeavesARecoverableExactAttemptWithoutAcceptedRows()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();
        var prepared = await processor.ResolveAsync(attemptUuid, request);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await FluentActions
            .Invoking(() => processor.ResolveAsync(attemptUuid, request, cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        AssertPublicSummary((await processor.GetAsync(attemptUuid))!, prepared.Preparation!);
        (await music.SearchRepertoireAsync(artist.Uuid)).Should().BeEmpty();
        var recovered = await Drain(() => processor.ResolveAsync(attemptUuid, request));
        recovered
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(recovered));
    }

    [Test]
    public async Task CompleteDictionaryAndDecisionPagesDoNotDropTheClaimBeyondOneHundred()
    {
        var artist = await Artist();
        await Execute(
            """
            INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at)
            SELECT @Id,'T'||replace(uuid::text,'-',''),uuid::text,uuid::text,uuid,now()
            FROM (SELECT gen_random_uuid() AS uuid FROM generate_series(1,101)) fixture_songs
            """,
            artist
        );
        var request = await Dictionary(artist);
        var processor = Processor();
        var attemptUuid = Guid.NewGuid();

        var resolved = await Drain(() => processor.ResolveAsync(attemptUuid, request));

        resolved
            .Result!.Disposition.Should()
            .Be(RepertoireResolutionDisposition.Completed, Audit(resolved));
        resolved.Preparation!.Source.Claims.Should().HaveCount(101);
        var store = new NormalizationRepertoireResolutionStore(fixture.Db);
        var decisions = new List<RepertoireResolutionAssociation>();
        Guid? cursor = null;
        do
        {
            var page = await store.GetDecisionsAsync(attemptUuid, 37, cursor);
            page.Decisions.Count.Should().BeLessThanOrEqualTo(37);
            decisions.AddRange(page.Decisions);
            if (page.NextCursor != null)
            {
                page.NextCursor.Should().Be(page.Decisions[^1].ClaimUuid);
            }
            cursor = page.NextCursor;
        } while (cursor != null);
        decisions.Should().HaveCount(101);
        decisions.Select(decision => decision.ClaimUuid).Should().OnlyHaveUniqueItems();
        decisions.Select(decision => decision.RepertoireSongUuid).Should().OnlyHaveUniqueItems();
        var acceptedStages = resolved
            .Stages.Where(stage => stage.Kind == RepertoireResolutionStageKind.AcceptedWrite)
            .ToArray();
        acceptedStages.Should().HaveCount(2);
        foreach (var accepted in acceptedStages)
        {
            var full = (await store.GetStageAsync(attemptUuid, accepted.StageUuid))!;
            full.Context.Claims.Count.Should().BeLessThanOrEqualTo(100);
        }
        (await music.SearchRepertoireAsync(artist.Uuid, limit: 200)).Should().HaveCount(101);
        await AssertSourceSettled(resolved);
    }

    [Test]
    public async Task RecoveryFindsUnfinishedAndLeafStaleAttemptsWhileCompletedSourcesUsePendingRegistrations()
    {
        var artist = await Artist();
        await ImportedSong(artist, UniqueTitle());
        var request = await Dictionary(artist);
        var processor = Processor();
        var unfinishedUuid = Guid.NewGuid();
        await processor.ResolveAsync(unfinishedUuid, request);
        var staleUuid = Guid.NewGuid();
        var admitted = await UntilAcceptedTicket(processor, staleUuid, request);
        await AdvanceInput(admitted.Preparation!.Source.ReadSet.Inputs.First());
        var stale = await Drain(() => processor.ResolveAsync(staleUuid, request));
        stale.Result!.Disposition.Should().Be(RepertoireResolutionDisposition.Stale);
        var store = new NormalizationRepertoireResolutionStore(fixture.Db);
        var beforeRetry = await Recovery(store);
        beforeRetry
            .Select(attempt => attempt.AttemptUuid)
            .Should()
            .Contain([unfinishedUuid, staleUuid]);
        var retryUuid = Guid.NewGuid();
        var retry = await processor.RetryAsync(retryUuid, stale.Registration.RegistrationUuid);
        retry.PreviousAttemptUuid.Should().Be(staleUuid);
        var afterRetry = await Recovery(store);
        afterRetry.Select(attempt => attempt.AttemptUuid).Should().NotContain(staleUuid);
        afterRetry.Select(attempt => attempt.AttemptUuid).Should().Contain(retryUuid);
        var emptyArtist = await Artist();
        var emptyRequest = await Dictionary(emptyArtist);
        var completedUuid = Guid.NewGuid();
        var completed = await Drain(() => processor.ResolveAsync(completedUuid, emptyRequest));
        await AssertSourceSettled(completed);
        await ActivatePolicy();

        var recovery = await Recovery(store);

        recovery.Select(attempt => attempt.AttemptUuid).Should().NotContain(completedUuid);
        var pending = new List<RepertoireResolutionRegistration>();
        Guid? cursor = null;
        do
        {
            var page = await store.GetPendingRegistrationsAsync(100, cursor);
            pending.AddRange(page.Registrations);
            cursor = page.NextCursor;
        } while (cursor != null);
        pending
            .Should()
            .Contain(registration =>
                registration.RegistrationUuid == completed.Registration.RegistrationUuid
            );
        await FluentActions
            .Invoking(() => store.GetRecoveryAttemptsAsync(0))
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
        await FluentActions
            .Invoking(() => store.GetRecoveryAttemptsAsync(201))
            .Should()
            .ThrowAsync<ArgumentOutOfRangeException>();
    }

    private NormalizationRepertoireResolutionProcessor Processor()
    {
        return new(
            fixture.Db,
            Sources(),
            new(fixture.Db),
            policies,
            new(fixture.Db),
            fixture.Work,
            music
        );
    }

    private NormalizationRepertoireSourceService Sources()
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var imported = new NormalizationImportedCatalogService(fixture.Db, evidence);
        return new(
            fixture.Db,
            evidence,
            new(),
            imported,
            new(fixture.Db),
            new(fixture.Db),
            new(fixture.Db)
        );
    }

    private static async Task<RepertoireResolutionAttempt> Drain(
        Func<Task<RepertoireResolutionAttempt>> advance
    )
    {
        for (var phase = 0; phase < 30; phase++)
        {
            var attempt = await advance();
            if (attempt.Result is { Disposition: not RepertoireResolutionDisposition.Pending })
            {
                return attempt;
            }
        }
        throw new AssertionException(
            "Repertoire resolution did not settle within its bounded phases."
        );
    }

    private static async Task<RepertoireResolutionAttempt> UntilAcceptedTicket(
        NormalizationRepertoireResolutionProcessor processor,
        Guid attemptUuid,
        RepertoireResolutionRequest request
    )
    {
        for (var phase = 0; phase < 30; phase++)
        {
            var attempt = await processor.ResolveAsync(attemptUuid, request);
            if (
                attempt.Stages.Any(stage =>
                    stage.Kind == RepertoireResolutionStageKind.AcceptedWrite
                    && stage.Ticket != null
                    && stage.Receipt == null
                )
            )
            {
                return attempt;
            }
            attempt.Result.Should().BeNull("the accepted write must be staged before publication");
        }
        throw new AssertionException("No admitted repertoire accepted stage was retained.");
    }

    private static async Task<RepertoireResolutionAttempt> UntilStage(
        NormalizationRepertoireResolutionProcessor processor,
        Guid attemptUuid,
        RepertoireResolutionRequest request,
        RepertoireResolutionStageKind kind,
        bool admitted
    )
    {
        for (var phase = 0; phase < 30; phase++)
        {
            var attempt = await processor.ResolveAsync(attemptUuid, request);
            if (
                attempt.Stages.Any(stage =>
                    stage.Kind == kind
                    && (stage.Ticket != null) == admitted
                    && stage.Receipt == null
                    && stage.FailureReason == null
                    && !stage.Superseded
                )
            )
            {
                return attempt;
            }
            attempt
                .Result.Should()
                .BeNull("the selected durable boundary must be observable before completion");
        }
        throw new AssertionException("The requested repertoire checkpoint was not retained.");
    }

    private async Task<IReadOnlyList<RepertoireResolutionAssociation>> Decisions(
        RepertoireResolutionAttempt attempt
    )
    {
        var store = new NormalizationRepertoireResolutionStore(fixture.Db);
        var result = new List<RepertoireResolutionAssociation>();
        Guid? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var selected = await store.GetDecisionsAsync(attempt.AttemptUuid, 100, cursor);
            result.AddRange(selected.Decisions);
            if (selected.NextCursor == null)
            {
                return result;
            }
            (selected.NextCursor != cursor).Should().BeTrue("the decision cursor must advance");
            cursor = selected.NextCursor;
        }
        throw new AssertionException("Repertoire decision pagination did not terminate.");
    }

    private static async Task<IReadOnlyList<RepertoireResolutionAttempt>> Recovery(
        NormalizationRepertoireResolutionStore store
    )
    {
        var attempts = new List<RepertoireResolutionAttempt>();
        Guid? cursor = null;
        for (var pageIndex = 0; pageIndex < 1000; pageIndex++)
        {
            var page = await store.GetRecoveryAttemptsAsync(3, cursor);
            page.Attempts.Count.Should().BeLessThanOrEqualTo(3);
            page.Attempts.Should()
                .OnlyContain(attempt =>
                    attempt.Result == null
                    || attempt.Result.Disposition == RepertoireResolutionDisposition.Stale
                );
            attempts.AddRange(page.Attempts);
            if (page.NextCursor == null)
            {
                attempts.Select(attempt => attempt.AttemptUuid).Should().OnlyHaveUniqueItems();
                return attempts;
            }
            page.NextCursor.Should().Be(page.Attempts[^1].AttemptUuid);
            (page.NextCursor != cursor).Should().BeTrue("the recovery cursor must advance");
            cursor = page.NextCursor;
        }
        throw new AssertionException("Repertoire recovery pagination did not terminate.");
    }

    private async Task AssertSourceSettled(RepertoireResolutionAttempt attempt)
    {
        var acknowledgment = await FullStage(
            attempt,
            RepertoireResolutionStageKind.AcknowledgeSource
        );
        acknowledgment.Receipt!.Status.Should().Be(NormalizationPublicationStatus.Published);
        (await Pending(acknowledgment.Ticket!.SubjectUuid)).Should().BeFalse();
    }

    private async Task<RepertoireResolutionStage> FullStage(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionStageKind kind
    )
    {
        var saved = attempt.Stages.Single(stage => stage.Kind == kind);
        return (
            await new NormalizationRepertoireResolutionStore(fixture.Db).GetStageAsync(
                attempt.AttemptUuid,
                saved.StageUuid
            )
        )!;
    }

    private static void AssertPublicSummary(
        RepertoireResolutionAttempt summary,
        RepertoireResolutionPreparation original
    )
    {
        summary
            .Preparation.Should()
            .BeEquivalentTo(
                original with
                {
                    Source = original.Source with
                    {
                        Claims = [],
                        TotalClaims = original.Source.Claims.Count,
                        ClaimsOmitted = true,
                    },
                }
            );
    }

    private static void AssertRetiredBindings(NormalizationInputContributionState contribution)
    {
        contribution.Resources.Should().BeEmpty();
        contribution.DependencySubjectUuids.Should().BeEmpty();
        contribution.CatalogArtistUuids.Should().BeEmpty();
        contribution.SourceIdentifiers.Should().BeEmpty();
        contribution.UsesMappingRegistry.Should().BeFalse();
        (contribution.ImportedVenueUuids ?? []).Should().BeEmpty();
        (contribution.CandidateUniverses ?? []).Should().BeEmpty();
        (contribution.OwnerObservationSubjectUuids ?? []).Should().BeEmpty();
        (contribution.SourceUuids ?? []).Should().BeEmpty();
    }

    private Task<CatalogArtist> Artist() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CatalogArtist>(
                "INSERT INTO artists(name,slug,uuid,updated_at) VALUES(@name,@slug,@uuid,now()) RETURNING id AS Id,uuid AS Uuid",
                new
                {
                    name = UniqueTitle(),
                    slug = Guid.NewGuid().ToString("N"),
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private async Task<Guid> ImportedSong(
        CatalogArtist artist,
        string title,
        string? upstream = null
    )
    {
        var uuid = Guid.NewGuid();
        await Execute(
            "INSERT INTO setlist_songs(artist_id,name,slug,upstream_identifier,uuid,updated_at) VALUES(@Id,@title,@slug,@upstream,@uuid,now())",
            new
            {
                artist.Id,
                title,
                slug = uuid.ToString("D"),
                upstream = upstream ?? uuid.ToString("D"),
                uuid,
            }
        );
        return uuid;
    }

    private async Task<RepertoireResolutionRequest> Dictionary(CatalogArtist artist)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var snapshot = (
            await new NormalizationImportedCatalogService(
                fixture.Db,
                evidence
            ).RefreshArtistRepertoireAsync(artist.Uuid)
        )!;
        return new(new ImportedRepertoireResolutionSource(snapshot.Capture.Fetch.FetchUuid));
    }

    private async Task<Guid> AcceptedSong(
        CatalogArtist artist,
        string title,
        IReadOnlyList<string>? aliases = null
    )
    {
        var input = await fixture.Capture(title, "songs");
        var owner = await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid));
        if (owner == null)
        {
            await fixture.Work.BeginWorkAsync(
                Guid.NewGuid(),
                ArtistOwner(artist.Uuid),
                "review-fixture-v1",
                [],
                []
            );
            owner = (await fixture.Work.GetSubjectAsync(ArtistOwner(artist.Uuid)))!;
        }
        var uuid = Guid.NewGuid();
        await music.ApplyDecisionAsync(
            Guid.NewGuid(),
            owner.SubjectUuid,
            owner.EditorialRevision,
            "accept",
            new()
            {
                RepertoireSongs =
                [
                    new()
                    {
                        Uuid = uuid,
                        ArtistUuid = artist.Uuid,
                        DisplayTitle = title,
                        Aliases = aliases ?? [],
                        Evidence = [input.Reference],
                    },
                ],
            },
            "Independently reviewed new repertoire alternative",
            Operator
        );
        return uuid;
    }

    private async Task<TitleSource> PublishedTitle(string title, bool setlist)
    {
        var source = await fixture.Source();
        await Execute(
            "UPDATE source_tracks SET title=@title WHERE uuid=@TrackUuid",
            new { title, source.TrackUuid }
        );
        var artistUuid = await fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var decoder = new NormalizationProviderDecoder();
        var attemptUuid = Guid.NewGuid();
        if (!setlist)
        {
            var processor = new NormalizationRecordingProcessor(
                new(fixture.Db, evidence),
                evidence,
                new(fixture.Db),
                decoder,
                new(new(), new()),
                fixture.Work,
                new(fixture.Db),
                new(fixture.Db)
            );
            var published = await processor.ProcessAsync(attemptUuid, source.SourceUuid);
            published
                .Disposition.Should()
                .Be(NormalizationRecordingProcessingDisposition.Published);
            return new(
                source,
                artistUuid,
                new(new RecordingRepertoireResolutionSource(attemptUuid))
            );
        }

        var artistKey = Guid.NewGuid().ToString("D");
        await MapArtist(new(source.ArtistId, artistUuid), "setlist.fm", artistKey);
        await Execute(
            "UPDATE artists SET musicbrainz_id=@artistKey WHERE id=@ArtistId",
            new { artistKey, source.ArtistId }
        );
        var accountKey = "repertoire-fixture-" + Guid.NewGuid().ToString("N");
        var resource = new NormalizationResourceKey(
            "setlist.fm",
            "setlist-page",
            artistKey + ":page=1"
        );
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        page = 1,
                        itemsPerPage = 20,
                        total = 1,
                        setlist = new[]
                        {
                            new
                            {
                                id = accountKey,
                                eventDate = "17-10-2026",
                                artist = new { mbid = artistKey, name = "Fixture performer" },
                                sets = new
                                {
                                    set = new[] { new { song = new[] { new { name = title } } } },
                                },
                            },
                        },
                    }
                ),
                "application/json",
                "setlist-fm-http-v1",
                0,
                DateTimeOffset.UtcNow
            )
        );
        var setlists = new NormalizationSetlistProcessor(
            new(fixture.Db),
            evidence,
            new(fixture.Db),
            decoder,
            new(),
            fixture.Work
        );
        var result = await setlists.ProcessAsync(
            attemptUuid,
            new(capture.Revision.Uuid, resource),
            new("setlist.fm", "setlist", accountKey)
        );
        result.Result!.Disposition.Should().Be(NormalizationSetlistProcessingDisposition.Published);
        return new(source, artistUuid, new(new SetlistRepertoireResolutionSource(attemptUuid)));
    }

    private async Task<RepertoireResolutionRequest> ProviderDictionary(
        CatalogArtist artist,
        string title
    )
    {
        await IsolatePhishMappings(artist);
        await MapArtist(artist, "phish.in", Guid.NewGuid().ToString("N"));
        return await CaptureProviderDictionary(
            new
            {
                success = true,
                data = new[]
                {
                    new
                    {
                        id = Random.Shared.Next(1_000_000, int.MaxValue),
                        title,
                        alias_for = (int?)null,
                    },
                },
            }
        );
    }

    private async Task<RepertoireResolutionRequest> CaptureProviderDictionary(object body)
    {
        var resource = new NormalizationResourceKey("phish.in", "catalog", "songs");
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var current = await evidence.GetCurrentInputAsync(resource);
        var capture = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                resource,
                JsonSerializer.SerializeToUtf8Bytes(body),
                "application/json",
                "raw-http:1",
                current?.Generation ?? 0,
                DateTimeOffset.UtcNow
            )
        );
        return new(
            new ProviderRepertoireResolutionSource(new(capture.Revision.Uuid, resource), resource)
        );
    }

    private async Task IsolatePhishMappings(CatalogArtist artist)
    {
        // The supported catalog has no artist wrapper. Its checked binding therefore requires
        // one mapped artist globally. Preserve this disposable database's other fixture maps
        // while the nonparallel test exercises that real namespace contract.
        await fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            c.Database.Should().StartWith("relisten_test_");
            await using var transaction = await c.BeginTransactionAsync();
            var saved = await c.QuerySingleAsync<string>(
                """
                SELECT COALESCE(jsonb_agg(to_jsonb(m) ORDER BY m.artist_id,m.upstream_source_id),'[]'::jsonb)::text
                FROM artists_upstream_sources m JOIN upstream_sources p ON p.id=m.upstream_source_id
                WHERE p.name='phish.in'
                """,
                transaction: transaction
            );
            await c.ExecuteAsync(
                "DELETE FROM artists_upstream_sources m USING upstream_sources p WHERE p.id=m.upstream_source_id AND p.name='phish.in'",
                transaction: transaction
            );
            await transaction.CommitAsync();
            savedPhishMappings = saved;
            phishFixtureArtistId = artist.Id;
        });
    }

    private async Task RestorePhishMappings()
    {
        if (savedPhishMappings == null)
        {
            return;
        }
        await fixture.Db.WithWriteConnection(async connection =>
        {
            var c = (NpgsqlConnection)connection;
            await using var transaction = await c.BeginTransactionAsync();
            await c.ExecuteAsync(
                """
                DELETE FROM artists_upstream_sources m USING upstream_sources p
                WHERE p.id=m.upstream_source_id AND p.name='phish.in' AND m.artist_id=@artistId;
                INSERT INTO artists_upstream_sources SELECT *
                FROM jsonb_populate_recordset(NULL::artists_upstream_sources,@saved::jsonb)
                """,
                new { artistId = phishFixtureArtistId, saved = savedPhishMappings },
                transaction
            );
            await transaction.CommitAsync();
        });
        savedPhishMappings = null;
        phishFixtureArtistId = null;
    }

    private Task MapArtist(CatalogArtist artist, string providerName, string key) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var provider =
                await c.QuerySingleOrDefaultAsync<int?>(
                    "SELECT id FROM upstream_sources WHERE name=@providerName ORDER BY id LIMIT 1",
                    new { providerName }
                )
                ?? await c.QuerySingleAsync<int>(
                    "INSERT INTO upstream_sources(id,name,url) VALUES((SELECT GREATEST(1000,COALESCE(max(id),0)+1) FROM upstream_sources),@providerName,'https://provider.invalid') RETURNING id",
                    new { providerName }
                );
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@Id,@provider,@key)",
                new
                {
                    artist.Id,
                    provider,
                    key,
                }
            );
        });

    private async Task<IReadOnlyList<NormalizationCandidateUniverseDependency>> Epoch(
        CatalogArtist artist,
        string title
    ) =>
        (
            await new NormalizationCandidateService(fixture.Db).FindRepertoireFactsAsync(
                new(IdentityMatchingDomain.Repertoire, [title], [], ArtistUuid: artist.Uuid)
            )
        )
            .ReadSet
            .CandidateUniverses;

    private Task<string> LegacySongs(CatalogArtist artist) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY uuid),'[]'::jsonb)::text FROM setlist_songs s WHERE artist_id=@Id",
                artist
            )
        );

    private Task<string> HistoricalRelationships(Guid assignmentUuid, Guid identityUuid) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                """
                SELECT jsonb_build_object('assignment',
                    (SELECT to_jsonb(x) FROM normalization.imported_song_assignments x WHERE uuid=@assignmentUuid),
                    'identity',(SELECT to_jsonb(x) FROM normalization.music_external_identities x WHERE uuid=@identityUuid))::text
                """,
                new { assignmentUuid, identityUuid }
            )
        );

    private Task<string> RelationshipState(Guid uuid, bool externalIdentity) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                $"SELECT jsonb_build_object('row',to_jsonb(x),'revision',to_jsonb(r))::text FROM normalization.{(externalIdentity ? "music_external_identities" : "imported_song_assignments")} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@uuid",
                new { uuid }
            )
        );

    private Task<string> Playback(CatalogSource source) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<string>(
                "SELECT jsonb_build_object('source',to_jsonb(s),'tracks',(SELECT jsonb_agg(to_jsonb(t) ORDER BY t.uuid) FROM source_tracks t WHERE t.source_id=s.id))::text FROM sources s WHERE s.id=@SourceId",
                source
            )
        );

    private async Task AdvanceInput(NormalizationInputDependency dependency)
    {
        var evidence = new NormalizationEvidenceService(fixture.Db);
        var original = (await evidence.GetInputAsync(dependency.RevisionUuid))!;
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            original.Payload
        )!;
        payload["_fixtureChange"] = JsonSerializer.SerializeToElement(Guid.NewGuid());
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                original.Resource,
                JsonSerializer.SerializeToUtf8Bytes(payload),
                original.MediaType,
                original.CaptureVersion,
                dependency.Generation,
                DateTimeOffset.UtcNow
            )
        );
    }

    private async Task<bool> Pending(Guid subjectUuid)
    {
        Guid? cursor = null;
        for (var page = 0; page < 1000; page++)
        {
            var pending = await fixture.Work.GetPendingSubjectsAsync(cursor, 1000);
            if (pending.Any(subject => subject.SubjectUuid == subjectUuid))
            {
                return true;
            }
            if (pending.Count < 1000)
            {
                return false;
            }
            cursor = pending[^1].SubjectUuid;
        }
        throw new AssertionException("Pending repertoire work pagination did not terminate.");
    }

    private async Task ActivatePolicy(bool titleReuse = false)
    {
        var artifact = new RepertoireAcceptancePolicyArtifact(
            Guid.NewGuid(),
            "repertoire-fixture-v1",
            "",
            NormalizationRepertoireResolutionProcessor.Version,
            RepertoireResolutionPolicyEvaluator.SourceVersion,
            NormalizationCandidateService.FactsVersion,
            RepertoireResolutionPolicyEvaluator.MatcherVersion,
            titleReuse
                ?
                [
                    IdentityMatchRule.QualifiedEntityIdentifier,
                    IdentityMatchRule.ArtistScopedNormalizedTitle,
                ]
                : [IdentityMatchRule.QualifiedEntityIdentifier],
            FixtureOnly: true
        );
        artifact = artifact with
        {
            ContentHash = RepertoireResolutionPolicyService.ComputeArtifactHash(artifact),
        };
        var evaluation = new RepertoireAcceptanceEvaluationReceipt(
            Guid.NewGuid(),
            artifact.Uuid,
            artifact.ContentHash,
            "",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "fixture-repertoire-labels-v1",
            "independent-fixture-adjudicator",
            Operator,
            new(100, 0),
            FixtureOnly: true
        );
        evaluation = evaluation with
        {
            ReceiptHash = RepertoireResolutionPolicyService.ComputeEvaluationHash(evaluation),
        };
        await policies.RegisterEvaluatedPolicyAsync(artifact, evaluation, Operator);
        await SetPolicy(artifact.Uuid);
    }

    private async Task SetPolicy(Guid? artifactUuid)
    {
        var state = await policies.ReadStateAsync();
        var owner = (await fixture.Work.GetSubjectAsync(RepertoireResolutionPolicyService.Owner))!;
        await policies.ApplyDecisionAsync(
            new(
                Guid.NewGuid(),
                owner.SubjectUuid,
                owner.EditorialRevision,
                state.Active?.Policy.Artifact.Uuid,
                artifactUuid,
                "Isolated repertoire policy fixture"
            ),
            Operator
        );
    }

    private static RepertoireResolutionClaim Claim(
        RepertoireResolutionAttempt attempt,
        RepertoireResolutionAssociation decision
    ) => attempt.Preparation!.Source.Claims.Single(claim => claim.ClaimUuid == decision.ClaimUuid);

    private Task Execute(string sql, object? parameters = null) =>
        fixture.Db.WithWriteConnection(c => c.ExecuteAsync(sql, parameters));

    private static NormalizationSubjectKey ArtistOwner(Guid artistUuid) =>
        new("songs", "artist_repertoire", artistUuid);

    private static string UniqueTitle() => "T" + Guid.NewGuid().ToString("N");

    private static string Audit(RepertoireResolutionAttempt attempt) =>
        JsonSerializer.Serialize(attempt.Result);

    private sealed record CatalogArtist(int Id, Guid Uuid);

    private sealed record TitleSource(
        CatalogSource Catalog,
        Guid ArtistUuid,
        RepertoireResolutionRequest Request
    );

    private sealed record HistoricalIdentity(
        Guid Uuid,
        Guid RevisionUuid,
        long Revision,
        string Payload
    );
}
