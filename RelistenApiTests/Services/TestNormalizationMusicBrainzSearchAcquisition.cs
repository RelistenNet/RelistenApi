using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace RelistenApiTests.Services;

[TestFixture]
public sealed class TestNormalizationMusicBrainzSearchAcquisitionQueries
{
    [TestCase(MusicBrainzLookupKind.Artist, false)]
    [TestCase(MusicBrainzLookupKind.Recording, false)]
    [TestCase(MusicBrainzLookupKind.Work, false)]
    [TestCase(MusicBrainzLookupKind.Work, true)]
    public void AbsentProgramPreservesLegacyQueryRequestAndDescriptorIdentity(
        MusicBrainzLookupKind kind,
        bool author
    )
    {
        var request = Page(kind, "Déjà Vu") with { Offset = 50 };
        if (author)
        {
            request = request with
            {
                Query = request.Query with
                {
                    RelatedArtist = new(MusicBrainzSearchArtistUse.WorkAuthor, "Author"),
                },
            };
        }
        var legacyQuery = new
        {
            request.Query.Kind,
            request.Query.Text,
            request.Query.RelatedArtist,
        };
        var legacyRequest = new
        {
            Query = legacyQuery,
            request.Offset,
            request.CacheMode,
            request.MaximumCacheAge,
        };
        JsonSerializer
            .SerializeToUtf8Bytes(request.Query)
            .Should()
            .Equal(JsonSerializer.SerializeToUtf8Bytes(legacyQuery));
        JsonSerializer
            .SerializeToUtf8Bytes(request)
            .Should()
            .Equal(JsonSerializer.SerializeToUtf8Bytes(legacyRequest));
        var field = kind.ToString().ToLowerInvariant();
        var rendered = field + ":\"Déjà Vu\"" + (author ? " AND artist:\"Author\"" : "");
        var legacyDescriptorBytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                version = "musicbrainz-literal-search-v1",
                kind = field,
                query = rendered,
                limit = 50,
                offset = 50,
            }
        );
        var expected = new MusicBrainzSearchDescriptor(
            "musicbrainz-literal-search-v1",
            kind,
            rendered,
            50,
            50,
            new(
                "musicbrainz.org",
                field + "-search",
                Convert.ToHexStringLower(SHA256.HashData(legacyDescriptorBytes))
            ),
            "musicbrainz-" + field + "-search-json-v1",
            "https://musicbrainz.org/ws/2/"
                + field
                + "?query="
                + Uri.EscapeDataString(rendered)
                + "&limit=50&offset=50&fmt=json"
        );

        MusicBrainzCandidateQueryBuilder.CreateDescriptor(request).Should().Be(expected);
        JsonSerializer.Serialize(request.Query).Should().NotContain("Program");
    }

    [Test]
    public void RecordingProgramLiteralPreservesUnicodeAndCannotInjectAnotherQueryClause()
    {
        const string literal = "The Last Waltz \"édition\" + (live) OR artist:* / 🎵";
        const string escaped =
            "The Last Waltz \\\"édition\\\" \\+ \\(live\\) OR artist\\:\\* \\/ 🎵";
        var request = Page(MusicBrainzLookupKind.Recording, "Such a Night") with
        {
            Query = new(
                MusicBrainzLookupKind.Recording,
                "Such a Night",
                Program: new(ReleaseTitle: literal)
            ),
        };
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);

        descriptor.BuilderVersion.Should().Be("musicbrainz-recording-program-search-v1");
        descriptor
            .RenderedQuery.Should()
            .Be("recording:\"Such a Night\" AND release:\"" + escaped + "\"");
        descriptor
            .RequestUrl.Should()
            .Be(
                "https://musicbrainz.org/ws/2/recording?query="
                    + Uri.EscapeDataString(descriptor.RenderedQuery)
                    + "&limit=50&offset=0&fmt=json"
            );
        descriptor.CaptureVersion.Should().Be("musicbrainz-recording-search-json-v1");
        descriptor
            .Resource.Should()
            .NotBe(
                MusicBrainzCandidateQueryBuilder
                    .CreateDescriptor(Page(MusicBrainzLookupKind.Recording, "Such a Night"))
                    .Resource
            );
        request.Query.RelatedArtist.Should().BeNull();
    }

    [Test]
    public void RecordingProgramIdentityUsesOnlyTheQualifiedReleaseId()
    {
        const string release = "5902973b-6cb4-4b78-9d1f-a6422f7ca191";
        var request = Page(MusicBrainzLookupKind.Recording, "Such a Night") with
        {
            Query = new(
                MusicBrainzLookupKind.Recording,
                "Such a Night",
                Program: new(ReleaseIdentity: new("musicbrainz.org", "release", release))
            ),
        };
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);

        descriptor.RenderedQuery.Should().Be("recording:\"Such a Night\" AND reid:" + release);
        descriptor.BuilderVersion.Should().Be("musicbrainz-recording-program-search-v1");
        request.Query.RelatedArtist.Should().BeNull();
        JsonSerializer
            .Deserialize<MusicBrainzSearchPageRequest>(JsonSerializer.Serialize(request))
            .Should()
            .Be(request);
    }

    [TestCase("work")]
    [TestCase("artist")]
    [TestCase("empty")]
    [TestCase("both")]
    [TestCase("neither")]
    [TestCase("wrong_provider")]
    [TestCase("wrong_type")]
    [TestCase("zero")]
    [TestCase("malformed")]
    [TestCase("compact")]
    [TestCase("control")]
    [TestCase("too_long")]
    public void InvalidRecordingProgramConstraintsAreRejectedBeforeAdmission(string fault)
    {
        const string release = "5902973b-6cb4-4b78-9d1f-a6422f7ca191";
        var program = new MusicBrainzRecordingProgramConstraint(ReleaseTitle: "The Last Waltz");
        var kind = MusicBrainzLookupKind.Recording;
        switch (fault)
        {
            case "work":
                kind = MusicBrainzLookupKind.Work;
                break;
            case "artist":
                kind = MusicBrainzLookupKind.Artist;
                break;
            case "empty":
                program = new(ReleaseTitle: " ");
                break;
            case "both":
                program = new("The Last Waltz", new("musicbrainz.org", "release", release));
                break;
            case "neither":
                program = new();
                break;
            case "wrong_provider":
                program = new(ReleaseIdentity: new("legacy_catalog", "release", release));
                break;
            case "wrong_type":
                program = new(ReleaseIdentity: new("musicbrainz.org", "work", release));
                break;
            case "zero":
                program = new(
                    ReleaseIdentity: new("musicbrainz.org", "release", Guid.Empty.ToString("D"))
                );
                break;
            case "malformed":
                program = new(ReleaseIdentity: new("musicbrainz.org", "release", "not-an-mbid"));
                break;
            case "compact":
                program = new(
                    ReleaseIdentity: new(
                        "musicbrainz.org",
                        "release",
                        Guid.Parse(release).ToString("N")
                    )
                );
                break;
            case "control":
                program = new(ReleaseTitle: "The Last\nWaltz");
                break;
            case "too_long":
                program = new(ReleaseTitle: new string('a', 513));
                break;
        }
        var request = Page(kind, "Such a Night") with
        {
            Query = new(kind, "Such a Night", Program: program),
        };
        var describe = () => MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);

        describe.Should().Throw<ArgumentException>();
    }

    [TestCase(MusicBrainzLookupKind.Artist, "artist")]
    [TestCase(MusicBrainzLookupKind.Recording, "recording")]
    [TestCase(MusicBrainzLookupKind.Work, "work")]
    public void LiteralQueriesPreserveUnicodeAndEscapeLuceneSyntaxExactlyOnce(
        MusicBrainzLookupKind kind,
        string field
    )
    {
        const string literal = "Beyoncé + - && || ! ( ) { } [ ] ^ \" ~ * ? : \\ / 🎵";
        const string escaped =
            "Beyoncé \\+ \\- \\&\\& \\|\\| \\! \\( \\) \\{ \\} \\[ \\] \\^ \\\" \\~ \\* \\? \\: \\\\ \\/ 🎵";
        var request = Page(kind, literal);

        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);

        descriptor.RenderedQuery.Should().Be(field + ":\"" + escaped + "\"");
        descriptor
            .RequestUrl.Should()
            .Be(
                "https://musicbrainz.org/ws/2/"
                    + field
                    + "?query="
                    + Uri.EscapeDataString(descriptor.RenderedQuery)
                    + "&limit=50&offset=0&fmt=json"
            );
        descriptor.Resource.Provider.Should().Be("musicbrainz.org");
        descriptor.Resource.ResourceType.Should().Be(field + "-search");
        descriptor.CaptureVersion.Should().Be("musicbrainz-" + field + "-search-json-v1");
        descriptor.Limit.Should().Be(50);
        descriptor.Offset.Should().Be(0);
        MusicBrainzCandidateQueryBuilder.CreateDescriptor(request).Should().Be(descriptor);
    }

    [Test]
    public void ResourceIdentityDependsOnExactExternalPageRatherThanCachePolicy()
    {
        var request = Page(MusicBrainzLookupKind.Work, "Déjà Vu");
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        var cached = request with
        {
            CacheMode = MusicBrainzCacheMode.ReuseFresh,
            MaximumCacheAge = TimeSpan.FromHours(1),
        };

        MusicBrainzCandidateQueryBuilder.CreateDescriptor(cached).Should().Be(descriptor);
        MusicBrainzCandidateQueryBuilder
            .CreateDescriptor(
                request with
                {
                    PlanIdentity = new(Guid.NewGuid(), new string('a', 64)),
                }
            )
            .Should()
            .Be(descriptor);
        foreach (
            var changed in new[]
            {
                request with
                {
                    Offset = 50,
                },
                request with
                {
                    Query = request.Query with { Text = "Déjà vu" },
                },
                request with
                {
                    Query = request.Query with { Kind = MusicBrainzLookupKind.Recording },
                },
                request with
                {
                    Query = request.Query with
                    {
                        RelatedArtist = new(MusicBrainzSearchArtistUse.WorkAuthor, "Author", null),
                    },
                },
            }
        )
        {
            MusicBrainzCandidateQueryBuilder
                .CreateDescriptor(changed)
                .Resource.Should()
                .NotBe(descriptor.Resource);
        }
    }

    [Test]
    public void ContextUsesExplicitRoleFieldsAndQualifiedExternalArtistIds()
    {
        var externalId = Guid.Parse("ad48102d-d173-4c9e-bafd-624ae631b419");
        var author = Page(MusicBrainzLookupKind.Work, "Title") with
        {
            Query = new(
                MusicBrainzLookupKind.Work,
                "Title",
                new(MusicBrainzSearchArtistUse.WorkAuthor, "A \"writer\"", null)
            ),
        };
        var performer = Page(MusicBrainzLookupKind.Recording, "Title") with
        {
            Query = new(
                MusicBrainzLookupKind.Recording,
                "Title",
                new(MusicBrainzSearchArtistUse.RecordingPerformer, "Performer", null)
            ),
        };
        var qualified = author with
        {
            Query = author.Query with
            {
                RelatedArtist = new(MusicBrainzSearchArtistUse.WorkAuthor, null, externalId),
            },
        };

        MusicBrainzCandidateQueryBuilder
            .CreateDescriptor(author)
            .RenderedQuery.Should()
            .Be("work:\"Title\" AND artist:\"A \\\"writer\\\"\"");
        MusicBrainzCandidateQueryBuilder
            .CreateDescriptor(performer)
            .RenderedQuery.Should()
            .Be("recording:\"Title\" AND artistname:\"Performer\"");
        MusicBrainzCandidateQueryBuilder
            .CreateDescriptor(qualified)
            .RenderedQuery.Should()
            .Be("work:\"Title\" AND arid:" + externalId.ToString("D"));
    }

    [TestCase(0)]
    [TestCase(50)]
    [TestCase(100)]
    [TestCase(150)]
    public void OnlyTheFourBoundedPagesCanBeDescribed(int offset)
    {
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(
            Page(MusicBrainzLookupKind.Work, "Title") with
            {
                Offset = offset,
            }
        );

        descriptor.Offset.Should().Be(offset);
        descriptor.Limit.Should().Be(50);
        descriptor.RequestUrl.Should().EndWith("&limit=50&offset=" + offset + "&fmt=json");
    }

    [TestCase("empty")]
    [TestCase("control")]
    [TestCase("too_long")]
    [TestCase("offset")]
    [TestCase("artist_context")]
    [TestCase("performer_as_author")]
    [TestCase("author_as_performer")]
    [TestCase("both_artist_terms")]
    [TestCase("empty_artist_mbid")]
    [TestCase("missing_cache_age")]
    [TestCase("refresh_cache_age")]
    public void InvalidTermsRolesAndBoundsAreRejectedBeforeAdmission(string fault)
    {
        var request = Page(MusicBrainzLookupKind.Work, "Title");
        request = fault switch
        {
            "empty" => request with { Query = request.Query with { Text = " " } },
            "control" => request with { Query = request.Query with { Text = "Line\nBreak" } },
            "too_long" => request with
            {
                Query = request.Query with { Text = new string('a', 513) },
            },
            "offset" => request with { Offset = 200 },
            "artist_context" => request with
            {
                Query = new(
                    MusicBrainzLookupKind.Artist,
                    "Name",
                    new(MusicBrainzSearchArtistUse.WorkAuthor, "Name", null)
                ),
            },
            "performer_as_author" => request with
            {
                Query = request.Query with
                {
                    RelatedArtist = new(
                        MusicBrainzSearchArtistUse.RecordingPerformer,
                        "Name",
                        null
                    ),
                },
            },
            "author_as_performer" => request with
            {
                Query = new(
                    MusicBrainzLookupKind.Recording,
                    "Title",
                    new(MusicBrainzSearchArtistUse.WorkAuthor, "Name", null)
                ),
            },
            "both_artist_terms" => request with
            {
                Query = request.Query with
                {
                    RelatedArtist = new(
                        MusicBrainzSearchArtistUse.WorkAuthor,
                        "Name",
                        Guid.NewGuid()
                    ),
                },
            },
            "empty_artist_mbid" => request with
            {
                Query = request.Query with
                {
                    RelatedArtist = new(MusicBrainzSearchArtistUse.WorkAuthor, null, Guid.Empty),
                },
            },
            "missing_cache_age" => request with { CacheMode = MusicBrainzCacheMode.ReuseFresh },
            "refresh_cache_age" => request with { MaximumCacheAge = TimeSpan.FromHours(1) },
            _ => throw new ArgumentException(nameof(fault)),
        };

        Action describe = () => MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);

        describe.Should().Throw<ArgumentException>();
    }

    [Test]
    public void TermLengthCountsUnicodeScalarsWithoutNormalizingOrTruncatingThem()
    {
        var literal = string.Concat(Enumerable.Repeat("🎵", 512));
        var request = Page(MusicBrainzLookupKind.Artist, literal);

        MusicBrainzCandidateQueryBuilder
            .CreateDescriptor(request)
            .RenderedQuery.Should()
            .Be("artist:\"" + literal + "\"");
        Action overLimit = () =>
            MusicBrainzCandidateQueryBuilder.CreateDescriptor(
                request with
                {
                    Query = request.Query with { Text = literal + "🎵" },
                }
            );
        overLimit.Should().Throw<ArgumentException>();
    }

    [TestCase("arranger")]
    [TestCase("original performer")]
    [TestCase("cover performer")]
    public void UnsupportedAuthorRolesRemainCitedButDoNotConstrainWorkRetrieval(string role)
    {
        var revision = Guid.NewGuid();
        var context = new MusicBrainzSearchArtistSeed(
            MusicBrainzSearchArtistUse.WorkAuthor,
            Name: new("Related artist", new(revision, "/artist/name")),
            Role: new(role, new(revision, "/artist/role"))
        );
        var seed = new MusicBrainzSearchSeed(
            MusicBrainzLookupKind.Work,
            new("Title", new(revision, "/title")),
            [context]
        );

        var variants = MusicBrainzCandidateQueryBuilder.BuildVariants(seed);

        variants
            .Queries.Should()
            .Equal(new MusicBrainzSearchQuery(MusicBrainzLookupKind.Work, "Title"));
        variants.Issues.Should().Equal("UnsupportedAuthorContext");
        variants.Seed.RelatedArtists.Should().ContainSingle().Which.Should().Be(context);
        variants
            .Seed.RelatedArtists[0]
            .Role!.Evidence.Should()
            .Be(new NormalizedEvidenceReference(revision, "/artist/role"));
    }

    [TestCase("archive.org", "artist")]
    [TestCase("legacy_catalog", "artist")]
    [TestCase("musicbrainz.org", "recording")]
    public void VariantContextsRejectIdentitiesOutsideTheExternalMusicBrainzArtistNamespace(
        string provider,
        string resourceType
    )
    {
        var revision = Guid.NewGuid();
        var seed = new MusicBrainzSearchSeed(
            MusicBrainzLookupKind.Work,
            new("Title", new(revision, "/title")),
            [
                new(
                    MusicBrainzSearchArtistUse.WorkAuthor,
                    Identity: new(
                        new(provider, resourceType, Guid.NewGuid().ToString("D")),
                        new(revision, "/artist/id")
                    ),
                    Role: new("composer", new(revision, "/artist/role"))
                ),
            ]
        );

        Action build = () => MusicBrainzCandidateQueryBuilder.BuildVariants(seed);

        build.Should().Throw<ArgumentException>();
    }

    [Test]
    public void MissingTextRetainsAnUnresolvedSeedWithoutInventingQueries()
    {
        var seed = new MusicBrainzSearchSeed(MusicBrainzLookupKind.Work, null, []);

        var variants = MusicBrainzCandidateQueryBuilder.BuildVariants(seed);

        variants.Queries.Should().BeEmpty();
        variants.Issues.Should().Equal("MissingSearchText");
        variants.Seed.Should().BeEquivalentTo(seed);
    }

    private static MusicBrainzSearchPageRequest Page(MusicBrainzLookupKind kind, string text) =>
        new(new(kind, text), 0, MusicBrainzCacheMode.Refresh);
}

/// <summary>One real PostgreSQL provider gate and evidence store, with HTTP entirely synthetic.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationMusicBrainzSearchAcquisition
{
    private const string UserAgent = "RelistenNormalization.SearchTests/1.0 (https://relisten.net)";
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationMusicBrainzInputStore store = null!;
    private readonly List<HttpClient> clients = [];

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        store = new(fixture.Db);
    }

    [SetUp]
    public Task ResetAdmission() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE normalization.musicbrainz_gate
                SET attempt_uuid=NULL,token=NULL,lease_until=NULL,not_before=clock_timestamp()-interval '10 seconds'
                WHERE singleton
                """
            )
        );

    [TearDown]
    public void DisposeClients()
    {
        foreach (var client in clients)
        {
            client.Dispose();
        }
        clients.Clear();
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase(MusicBrainzLookupKind.Artist, 0)]
    [TestCase(MusicBrainzLookupKind.Recording, 0)]
    [TestCase(MusicBrainzLookupKind.Work, 0)]
    [TestCase(MusicBrainzLookupKind.Work, 50)]
    public async Task ExactSearchProfilesRetainBoundedResultBytesWithoutAcceptedDomainWrites(
        MusicBrainzLookupKind kind,
        int resultCount
    )
    {
        var request = Page(kind);
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        var body = Body(kind, 0, resultCount);
        var before = await DomainCounts();
        var upstream = Success(body);

        var acquired = await Service(upstream).AcquireSearchAsync(Guid.NewGuid(), request);

        acquired.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        acquired.Descriptor.Should().Be(descriptor);
        acquired.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Captured);
        var sent = upstream.Requests.Should().ContainSingle().Subject;
        sent.Url.Should().Be(descriptor.RequestUrl);
        sent.Method.Should().Be("GET");
        sent.UserAgent.Should().Be(UserAgent);
        sent.Accept.Should().Equal("application/json");
        sent.Encodings.Should().Equal("identity");
        sent.Conditional.Should().BeFalse();
        var capture = (await evidence.GetCaptureAsync(acquired.Result.FetchUuid!.Value))!;
        capture.Revision.Payload.Should().Equal(body);
        capture.Revision.Resource.Should().Be(descriptor.Resource);
        capture.Revision.CaptureVersion.Should().Be(descriptor.CaptureVersion);
        capture.Fetch.ExpectedGeneration.Should().Be(0);
        (await DomainCounts()).Should().Be(before);
    }

    [Test]
    public async Task SearchAndLookupCommandsRejectCrossKindReuseBeforeHttpOrDeserialization()
    {
        var request = Page();
        var lookup = Lookup();
        var upstream = Success(Body(request.Query.Kind, 0));
        var service = Service(upstream);
        var search = await service.AcquireSearchAsync(Guid.NewGuid(), request);

        Func<Task> lookupAcquire = () => service.AcquireAsync(search.CommandUuid, lookup);
        Func<Task> lookupResume = () => service.ResumeAsync(search.CommandUuid);
        Func<Task> lookupGet = () => service.GetAsync(search.CommandUuid);
        await lookupAcquire.Should().ThrowAsync<NormalizationConflictException>();
        await lookupResume.Should().ThrowAsync<NormalizationConflictException>();
        await lookupGet.Should().ThrowAsync<NormalizationConflictException>();
        upstream.Calls.Should().Be(1);

        await ReadyGate();
        var lookupHandler = Success(
            JsonSerializer.SerializeToUtf8Bytes(new { id = lookup.Mbid, title = "Work" })
        );
        var lookupService = Service(lookupHandler);
        var acquiredLookup = await lookupService.AcquireAsync(Guid.NewGuid(), lookup);
        Func<Task> searchAcquire = () =>
            lookupService.AcquireSearchAsync(acquiredLookup.CommandUuid, request);
        Func<Task> searchResume = () => lookupService.ResumeSearchAsync(acquiredLookup.CommandUuid);
        Func<Task> searchGet = () => lookupService.GetSearchAsync(acquiredLookup.CommandUuid);
        await searchAcquire.Should().ThrowAsync<NormalizationConflictException>();
        await searchResume.Should().ThrowAsync<NormalizationConflictException>();
        await searchGet.Should().ThrowAsync<NormalizationConflictException>();
        lookupHandler.Calls.Should().Be(1);
    }

    [Test]
    public async Task SearchAndLookupShareAdmissionAndAPeerSearchCacheFillAvoidsAnotherSend()
    {
        var request = Page();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeHandler = new StubHandler(
            async (_, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return Response(Body(request.Query.Kind, 0));
            }
        );
        var active = Service(activeHandler).AcquireSearchAsync(Guid.NewGuid(), request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var peerHandler = Success(Body(request.Query.Kind, 0));
        var peerService = Service(peerHandler);
        var lookup = Lookup();
        var lookupHandler = Success(
            JsonSerializer.SerializeToUtf8Bytes(new { id = lookup.Mbid, title = "Work" })
        );
        var lookupService = Service(lookupHandler);
        MusicBrainzSearchAcquisition peer;
        MusicBrainzAcquisition waitingLookup;
        try
        {
            peer = await peerService.AcquireSearchAsync(
                Guid.NewGuid(),
                request with
                {
                    CacheMode = MusicBrainzCacheMode.ReuseFresh,
                    MaximumCacheAge = TimeSpan.FromHours(1),
                }
            );
            waitingLookup = await lookupService.AcquireAsync(Guid.NewGuid(), lookup);
            peer.State.Should().Be(MusicBrainzAcquisitionState.Pending);
            waitingLookup.State.Should().Be(MusicBrainzAcquisitionState.Pending);
            peer.Attempts.Should().BeEmpty();
            waitingLookup.Attempts.Should().BeEmpty();
            peer.NotBefore.Should().Be(waitingLookup.NotBefore);
            peerHandler.Calls.Should().Be(0);
            lookupHandler.Calls.Should().Be(0);
        }
        finally
        {
            release.TrySetResult();
            await active;
        }

        var cached = await peerService.ResumeSearchAsync(peer.CommandUuid);

        cached.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Cached);
        cached.Result.Input.Should().Be((await active).Result!.Input);
        cached.Attempts.Should().BeEmpty();
        peerHandler.Calls.Should().Be(0);
        await ReadyGate();
        var resumedLookup = await lookupService.ResumeAsync(waitingLookup.CommandUuid);
        var timing =
            resumedLookup.State == MusicBrainzAcquisitionState.Completed
                ? null
                : await MusicBrainzTimingDiagnostics.ReadAsync(
                    fixture.Db,
                    waitingLookup.CommandUuid
                );
        resumedLookup
            .State.Should()
            .Be(
                MusicBrainzAcquisitionState.Completed,
                "original response timing receipts: {0}",
                timing
            );
        lookupHandler.Calls.Should().Be(1);
    }

    [Test]
    public async Task ExactReplayReturnsTheOriginalDescriptorAndChangedQueriesPagesOrCachePolicyConflict()
    {
        var request = Page();
        var upstream = Success(Body(request.Query.Kind, 0));
        var service = Service(upstream);
        var acquired = await service.AcquireSearchAsync(Guid.NewGuid(), request);

        (await service.AcquireSearchAsync(acquired.CommandUuid, request))
            .Should()
            .BeEquivalentTo(acquired);
        (await service.ResumeSearchAsync(acquired.CommandUuid)).Should().BeEquivalentTo(acquired);
        (await service.GetSearchAsync(acquired.CommandUuid)).Should().BeEquivalentTo(acquired);
        foreach (
            var changed in new[]
            {
                request with
                {
                    Offset = 50,
                },
                request with
                {
                    Query = request.Query with { Text = "Changed query" },
                },
                request with
                {
                    CacheMode = MusicBrainzCacheMode.ReuseFresh,
                    MaximumCacheAge = TimeSpan.FromHours(1),
                },
            }
        )
        {
            Func<Task> reuse = () => service.AcquireSearchAsync(acquired.CommandUuid, changed);
            await reuse.Should().ThrowAsync<NormalizationConflictException>();
        }
        upstream.Calls.Should().Be(1);
    }

    [TestCase("missing_array")]
    [TestCase("wrong_array")]
    [TestCase("root_array")]
    [TestCase("missing_count")]
    [TestCase("missing_offset")]
    [TestCase("negative_offset")]
    [TestCase("negative_count")]
    [TestCase("fractional_count")]
    [TestCase("offset_mismatch")]
    [TestCase("too_many_results")]
    [TestCase("provider_error")]
    [TestCase("media")]
    [TestCase("encoding")]
    public async Task MalformedPagesRetainTheReceiptAndPreserveTheEarlierSuccessfulHead(
        string fault
    )
    {
        var request = Page();
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        await Service(Success(Body(request.Query.Kind, 0)))
            .AcquireSearchAsync(Guid.NewGuid(), request);
        var before = (await evidence.GetCurrentInputAsync(descriptor.Resource))!;
        await ReadyGate();
        var body = fault switch
        {
            "missing_array" => Encoding.UTF8.GetBytes("{\"count\":0,\"offset\":0}"),
            "wrong_array" => Encoding.UTF8.GetBytes("{\"count\":0,\"offset\":0,\"works\":{}}"),
            "root_array" => Encoding.UTF8.GetBytes("[]"),
            "missing_count" => Encoding.UTF8.GetBytes("{\"offset\":0,\"works\":[]}"),
            "missing_offset" => Encoding.UTF8.GetBytes("{\"count\":0,\"works\":[]}"),
            "negative_offset" => Encoding.UTF8.GetBytes("{\"count\":0,\"offset\":-1,\"works\":[]}"),
            "negative_count" => Encoding.UTF8.GetBytes("{\"count\":-1,\"offset\":0,\"works\":[]}"),
            "fractional_count" => Encoding.UTF8.GetBytes(
                "{\"count\":0.5,\"offset\":0,\"works\":[]}"
            ),
            "offset_mismatch" => Encoding.UTF8.GetBytes("{\"count\":0,\"offset\":50,\"works\":[]}"),
            "too_many_results" => Body(request.Query.Kind, 0, 51),
            "provider_error" => Encoding.UTF8.GetBytes(
                "{\"count\":0,\"offset\":0,\"works\":[],\"error\":\"busy\"}"
            ),
            _ => Body(request.Query.Kind, 0),
        };
        var upstream = new StubHandler(
            (_, _) =>
            {
                var response = Response(body);
                if (fault == "media")
                {
                    response.Content.Headers.ContentType = new("text/html");
                }
                if (fault == "encoding")
                {
                    response.Content.Headers.ContentEncoding.Add("gzip");
                }
                return Task.FromResult(response);
            }
        );

        var failed = await Service(upstream).AcquireSearchAsync(Guid.NewGuid(), request);

        failed.State.Should().Be(MusicBrainzAcquisitionState.Failed);
        failed.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Failed);
        var attempt = failed.Attempts.Should().ContainSingle().Subject;
        var saved = (await store.GetResponseAsync(attempt.AttemptUuid))!;
        saved.Response.Body.Should().Equal(body);
        saved.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(body)));
        saved.Response.BodyComplete.Should().BeTrue();
        (await evidence.GetCaptureAsync(attempt.AttemptUuid)).Should().BeNull();
        (await evidence.GetCurrentInputAsync(descriptor.Resource)).Should().BeEquivalentTo(before);
        upstream.Calls.Should().Be(1);
    }

    [Test]
    public async Task RefreshOfEqualBytesAdvancesGenerationWhileFreshReuseCreatesNoAttempt()
    {
        var request = Page();
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        var upstream = Success(Body(request.Query.Kind, 0));
        var service = Service(upstream);
        var first = await service.AcquireSearchAsync(Guid.NewGuid(), request);

        var cached = await service.AcquireSearchAsync(
            Guid.NewGuid(),
            request with
            {
                CacheMode = MusicBrainzCacheMode.ReuseFresh,
                MaximumCacheAge = TimeSpan.MaxValue,
            }
        );

        cached.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Cached);
        cached.Result.FetchUuid.Should().Be(first.Result!.FetchUuid);
        cached.Attempts.Should().BeEmpty();
        upstream.Calls.Should().Be(1);
        await ReadyGate();

        var refresh = await service.AcquireSearchAsync(Guid.NewGuid(), request);

        refresh.Result!.ResourceGeneration.Should().Be(2);
        refresh.Result.Input.Should().Be(first.Result.Input);
        refresh.Result.FetchUuid!.Value.Should().NotBe(first.Result.FetchUuid!.Value);
        refresh.Attempts.Single().ExpectedGeneration.Should().Be(1);
        (await evidence.GetCurrentInputAsync(descriptor.Resource))!.Generation.Should().Be(2);
        upstream.Calls.Should().Be(2);
    }

    [TestCase(429)]
    [TestCase(503)]
    public async Task SearchRetryAfterBlocksLookupTrafficAndRetainsExactFailureBytes(int status)
    {
        var request = Page();
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        var body = Encoding.UTF8.GetBytes("{\"error\":\"search unavailable\"}\n");
        var upstream = new StubHandler(
            (_, _) =>
            {
                var response = Response(body, (HttpStatusCode)status);
                response.Headers.TryAddWithoutValidation("Retry-After", "120");
                return Task.FromResult(response);
            }
        );
        var service = Service(upstream);
        var started = DateTimeOffset.UtcNow;

        var failed = await service.AcquireSearchAsync(Guid.NewGuid(), request);
        var lookup = await service.AcquireAsync(Guid.NewGuid(), Lookup());

        failed.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        failed.NotBefore.Should().BeOnOrAfter(started.AddSeconds(119));
        var attempt = failed.Attempts.Should().ContainSingle().Subject;
        var saved = (await store.GetResponseAsync(attempt.AttemptUuid))!;
        saved.Response.StatusCode.Should().Be(status);
        saved.Response.Body.Should().Equal(body);
        saved.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(body)));
        lookup.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        lookup.Attempts.Should().BeEmpty();
        lookup.NotBefore.Should().Be(failed.NotBefore);
        (await service.ResumeSearchAsync(failed.CommandUuid)).Attempts.Should().ContainSingle();
        upstream.Calls.Should().Be(1);
        (await evidence.GetCurrentInputAsync(descriptor.Resource)).Should().BeNull();
    }

    [Test]
    public async Task HistoricalTerminalSearchReplaysItsFrozenDescriptorWithoutCurrentBuilderChecks()
    {
        var request = Page();
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request) with
        {
            BuilderVersion = "historical-query-builder-v0",
        };
        var command = Guid.NewGuid();
        await store.GetOrCreateSearchAsync(
            command,
            request,
            descriptor,
            "historical-search-acquisition-v0"
        );
        var terminal = await store.FailSearchAsync(command, "historical_retained_failure");
        var upstream = Success(Body(request.Query.Kind, 0));
        var service = Service(upstream);

        (await service.AcquireSearchAsync(command, request)).Should().BeEquivalentTo(terminal);
        (await service.ResumeSearchAsync(command)).Should().BeEquivalentTo(terminal);
        (await service.GetSearchAsync(command)).Should().BeEquivalentTo(terminal);
        terminal.Descriptor.Should().Be(descriptor);
        upstream.Calls.Should().Be(0);
        (await evidence.GetCurrentInputAsync(descriptor.Resource)).Should().BeNull();
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task SavedSearchResponsesRecoverWithoutHttpAfterCaptureCommitOrAnotherLeaseExpiry(
        bool captureCommitted,
        bool expireAdmission
    )
    {
        var request = Page();
        var body = Body(request.Query.Kind, 0);
        var dispatch = await Dispatch(request);
        var response = await SavedResponse(dispatch, body);
        (await store.SaveResponseAsync(dispatch, response)).Eligible.Should().BeTrue();
        NormalizationCaptureResult? committed = null;
        if (captureCommitted)
        {
            committed = await Capture(
                dispatch,
                body,
                dispatch.AttemptUuid,
                dispatch.ExpectedGeneration,
                response.CompletedAt
            );
        }
        if (expireAdmission)
        {
            // An unrelated unsaved owner may expire while this eligible page awaits capture.
            // Its expiry must not downgrade the saved receipt or spend another page attempt.
            await ReadyGate();
            await Dispatch(Page(MusicBrainzLookupKind.Artist));
            await ExpireGate();
        }
        var upstream = Success(body);

        var resumed = await Service(upstream).ResumeSearchAsync(dispatch.CommandUuid);

        resumed.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        resumed.Result!.FetchUuid.Should().Be(dispatch.AttemptUuid);
        resumed.Result.ResourceGeneration.Should().Be(1);
        resumed.Attempts.Should().ContainSingle().Which.Phase.Should().Be("Captured");
        resumed.Descriptor.Should().Be(MusicBrainzCandidateQueryBuilder.CreateDescriptor(request));
        upstream.Calls.Should().Be(0);
        var retained = (await evidence.GetCaptureAsync(dispatch.AttemptUuid))!;
        retained.Revision.Payload.Should().Equal(body);
        if (committed != null)
        {
            retained.Should().BeEquivalentTo(committed);
        }
        (await store.GetResponseAsync(dispatch.AttemptUuid))!.Eligible.Should().BeTrue();
    }

    [Test]
    public async Task AStaleSavedPageKeepsItsOriginalGenerationAndTheNextAttemptReadsANewClock()
    {
        var request = Page();
        var body = Body(request.Query.Kind, 0);
        var dispatch = await Dispatch(request);
        await store.SaveResponseAsync(dispatch, await SavedResponse(dispatch, body));
        var newerBody = Body(request.Query.Kind, 0, 1);
        var newer = await Capture(dispatch, newerBody, Guid.NewGuid(), 0, await Now());
        var upstream = Success(body);
        var service = Service(upstream);

        var stale = await service.ResumeSearchAsync(dispatch.CommandUuid);

        stale.State.Should().Be(MusicBrainzAcquisitionState.Pending);
        var historical = (await evidence.GetCaptureAsync(dispatch.AttemptUuid))!;
        historical.Fetch.ExpectedGeneration.Should().Be(0);
        historical.Fetch.AcceptedAsCurrent.Should().BeFalse();
        historical.Revision.Payload.Should().Equal(body);
        (await evidence.GetCurrentInputAsync(dispatch.Profile.Resource))!
            .Revision.Uuid.Should()
            .Be(newer.Revision.Uuid);
        upstream.Calls.Should().Be(0);
        await ReadyGate();

        var retried = await service.ResumeSearchAsync(dispatch.CommandUuid);

        retried.State.Should().Be(MusicBrainzAcquisitionState.Completed);
        retried.Attempts.Select(attempt => attempt.ExpectedGeneration).Should().Equal(0, 1);
        retried.Result!.ResourceGeneration.Should().Be(2);
        retried.Result.FetchUuid!.Value.Should().NotBe(dispatch.AttemptUuid);
        upstream.Calls.Should().Be(1);
    }

    [Test]
    public async Task AManualCaptureOfTheSamePageCannotSupplyAdapterCacheProvenance()
    {
        var request = Page();
        var descriptor = MusicBrainzCandidateQueryBuilder.CreateDescriptor(request);
        var body = Body(request.Query.Kind, 0);
        var manual = await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                descriptor.Resource,
                body,
                "application/json",
                descriptor.CaptureVersion,
                0,
                await Now()
            )
        );
        var upstream = Success(body);

        var acquired = await Service(upstream)
            .AcquireSearchAsync(
                Guid.NewGuid(),
                request with
                {
                    CacheMode = MusicBrainzCacheMode.ReuseFresh,
                    MaximumCacheAge = TimeSpan.FromHours(1),
                }
            );

        acquired.Result!.Disposition.Should().Be(MusicBrainzAcquisitionDisposition.Captured);
        acquired.Result.Input!.InputRevisionUuid.Should().Be(manual.Revision.Uuid);
        acquired.Result.FetchUuid!.Value.Should().NotBe(manual.Fetch.FetchUuid);
        acquired.Result.ResourceGeneration.Should().Be(2);
        upstream.Calls.Should().Be(1);
    }

    [Test]
    public async Task InvalidPageBoundsNeverCreateACommandOrCallTheProvider()
    {
        var upstream = Success(Body(MusicBrainzLookupKind.Work, 0));
        var service = Service(upstream);
        foreach (var offset in new[] { -50, 1, 49, 151, 200 })
        {
            var command = Guid.NewGuid();
            Func<Task> acquire = () =>
                service.AcquireSearchAsync(command, Page() with { Offset = offset });

            await acquire.Should().ThrowAsync<ArgumentException>();
            (await service.GetSearchAsync(command)).Should().BeNull();
        }
        upstream.Calls.Should().Be(0);
    }

    private NormalizationMusicBrainzInputService Service(StubHandler upstream)
    {
        var client = new HttpClient(upstream) { Timeout = Timeout.InfiniteTimeSpan };
        clients.Add(client);
        return new(client, store, evidence, Options());
    }

    private static MusicBrainzAcquisitionOptions Options() => new() { UserAgent = UserAgent };

    private static MusicBrainzSearchPageRequest Page(
        MusicBrainzLookupKind kind = MusicBrainzLookupKind.Work
    ) =>
        new(
            new(kind, "Fixture search " + Guid.NewGuid().ToString("N")),
            0,
            MusicBrainzCacheMode.Refresh
        );

    private static MusicBrainzLookupRequest Lookup() =>
        new(MusicBrainzLookupKind.Work, Guid.NewGuid(), MusicBrainzCacheMode.Refresh);

    private static byte[] Body(MusicBrainzLookupKind kind, int offset, int resultCount = 0)
    {
        var results = Enumerable
            .Range(0, resultCount)
            .Select(_ => new { id = Guid.NewGuid(), title = "Observed candidate" })
            .ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object>
            {
                ["created"] = "2026-10-17T00:00:00.000Z",
                ["count"] = resultCount,
                ["offset"] = offset,
                [
                    kind switch
                    {
                        MusicBrainzLookupKind.Artist => "artists",
                        MusicBrainzLookupKind.Recording => "recordings",
                        _ => "works",
                    }
                ] = results,
            }
        );
    }

    private Task ReadyGate() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.musicbrainz_gate SET not_before=clock_timestamp()-interval '10 seconds' WHERE singleton"
            )
        );

    private Task ExpireGate() =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                """
                UPDATE normalization.musicbrainz_gate
                SET lease_until=CASE WHEN token IS NULL THEN NULL ELSE clock_timestamp()-interval '10 seconds' END,
                    not_before=clock_timestamp()-interval '10 seconds'
                WHERE singleton
                """
            )
        );

    private async Task<MusicBrainzDispatch> Dispatch(MusicBrainzSearchPageRequest request)
    {
        var command = Guid.NewGuid();
        await store.GetOrCreateSearchAsync(
            command,
            request,
            MusicBrainzCandidateQueryBuilder.CreateDescriptor(request),
            NormalizationMusicBrainzInputService.SearchVersion
        );
        var step = await store.AdvanceAsync(command, Options());
        step.Dispatch.Should().NotBeNull();
        return step.Dispatch!;
    }

    private async Task<MusicBrainzHttpResponse> SavedResponse(
        MusicBrainzDispatch dispatch,
        byte[] body
    ) =>
        new(
            200,
            dispatch.Profile.RequestUrl,
            new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
            body,
            true,
            await Now()
        );

    private Task<NormalizationCaptureResult> Capture(
        MusicBrainzDispatch dispatch,
        byte[] body,
        Guid fetchUuid,
        long generation,
        DateTimeOffset observedAt
    ) =>
        evidence.CaptureInputAsync(
            new(
                fetchUuid,
                dispatch.Profile.Resource,
                body,
                "application/json",
                dispatch.Profile.CaptureVersion,
                generation,
                observedAt
            )
        );

    private async Task<DateTimeOffset> Now() =>
        new(
            await fixture.Db.WithWriteConnection(c =>
                c.ExecuteScalarAsync<DateTime>("SELECT clock_timestamp()")
            )
        );

    private Task<AcceptedCounts> DomainCounts() =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<AcceptedCounts>(
                """
                SELECT (SELECT count(*) FROM normalization.people) AS "People",
                    (SELECT count(*) FROM normalization.music_revisions) AS "Music",
                    (SELECT count(*) FROM normalization.work_subjects) AS "Subjects",
                    (SELECT count(*) FROM sources) AS "Sources",
                    (SELECT count(*) FROM source_tracks) AS "Tracks",
                    (SELECT count(*) FROM source_track_plays) AS "Plays"
                """
            )
        );

    private static StubHandler Success(byte[] body) =>
        new((_, _) => Task.FromResult(Response(body)));

    private static HttpResponseMessage Response(
        byte[] body,
        HttpStatusCode status = HttpStatusCode.OK
    )
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed record AcceptedCounts(
        long People,
        long Music,
        long Subjects,
        long Sources,
        long Tracks,
        long Plays
    );

    private sealed record SentRequest(
        string Method,
        string Url,
        string UserAgent,
        IReadOnlyList<string> Accept,
        IReadOnlyList<string> Encodings,
        bool Conditional
    );

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send
    ) : HttpMessageHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ConcurrentQueue<SentRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref calls);
            Requests.Enqueue(
                new(
                    request.Method.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Headers.UserAgent.ToString(),
                    request.Headers.Accept.Select(v => v.ToString()).ToArray(),
                    request.Headers.AcceptEncoding.Select(v => v.ToString()).ToArray(),
                    request.Headers.Contains("If-None-Match")
                        || request.Headers.Contains("If-Modified-Since")
                )
            );
            var response = await send(request, ct);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
