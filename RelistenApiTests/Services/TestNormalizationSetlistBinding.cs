using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Npgsql;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using CatalogSource = RelistenApiTests.Services.NormalizationDomainFixture.CatalogSource;

namespace RelistenApiTests.Services;

/// <summary>Retained provider accounts associate with existing catalog UUIDs without accepting song matches.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistBinding
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationCatalogBindingService bindings = null!;

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        bindings = new(fixture.Db);
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [TestCase("setlist.fm")]
    [TestCase("phantasytour.com")]
    public async Task ExactAccountAndAssignmentBindTheLateShowWithoutFlatteningRepeatedPassages(
        string provider
    )
    {
        var source = await fixture.Source();
        var context = await Account(provider, source);
        var imported = await Imported(source, context.LegacyKey);
        var early = await Show(source.ArtistId, "early");
        var late = await Show(source.ArtistId, "late");
        await Assign(imported.Id, late.Id);

        var result = await Bind(context);

        result.Artist.Value!.ArtistUuid.Should().Be(await ArtistUuid(source.ArtistId));
        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(imported.Uuid);
        result.Performance.Value!.ShowUuid.Should().Be(late.Uuid).And.NotBe(early.Uuid);
        result.ArtistScopeUuid.Should().BeNull();
        result
            .Inputs.Should()
            .ContainSingle()
            .Which.Should()
            .Be(new NormalizationBindingInputReference(context.Input.Uuid, context.Input.Resource));
        context.Account.Passages.Select(p => p.Title.Value).Should().Equal("Song", "Song");
        context.Account.Passages.Select(p => p.Ordinal).Should().Equal(1, 2);
    }

    [Test]
    public async Task MusicBrainzUuidTextCaseDoesNotChangeProviderIdentity()
    {
        var source = await fixture.Source();
        var key = Guid.NewGuid().ToString("D");
        var context = await Account(
            "setlist.fm",
            source,
            performerKey: key,
            configuredArtistKey: key.ToUpperInvariant()
        );
        var imported = await Imported(source, context.LegacyKey);

        var result = await Bind(context);

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(imported.Uuid);
        context.Input.Resource.ProviderKey.Should().StartWith(key.ToUpperInvariant());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PhantasyNumericMappingUsesTheSameIdentityAsTheImporter(
        bool includeWhitespaceAndPlus
    )
    {
        var source = await fixture.Source();
        var providerId = Random.Shared.Next(1, int.MaxValue).ToString();
        var configuredKey = includeWhitespaceAndPlus
            ? " +000" + providerId + " "
            : "000" + providerId;
        var context = await Account("phantasytour.com", source, configuredArtistKey: configuredKey);
        var imported = await Imported(source, context.LegacyKey);

        var result = await Bind(context);

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(imported.Uuid);
        context
            .Account.Performers.Single()
            .Identities.Single()
            .Identity.ProviderKey.Should()
            .Be(providerId);
    }

    [TestCase("setlist.fm")]
    [TestCase("phantasytour.com")]
    public async Task DuplicateArtistMappingsAreAmbiguousBeforeExpectedArtistFiltering(
        string provider
    )
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var context = await Account(provider, source);
        await Map(other, provider, context.ArtistKey);

        var result = await bindings.BindSetlistAsync(
            context.Input,
            context.Account,
            await ArtistUuid(source.ArtistId)
        );

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        result.Artist.Reason.Should().Be("ambiguous_artist_mapping");
        result.CatalogInputs.Should().HaveCount(2);
        result.Performance.Value.Should().BeNull();
    }

    [TestCase("setlist.fm")]
    [TestCase("phantasytour.com")]
    public async Task SameDateAndUnknownDateDoNotCreateAnAssignmentOrWithholdTheImportedAccount(
        string provider
    )
    {
        var source = await fixture.Source();
        var context = await Account(provider, source, "19XX-XX-XX");
        var imported = await Imported(source, context.LegacyKey);
        await Show(source.ArtistId, "early");
        await Show(source.ArtistId, "late");

        var result = await Bind(context);

        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(imported.Uuid);
        result.Performance.Status.Should().Be(NormalizationCatalogBindingStatus.Missing);
        result.Performance.Reason.Should().Be("missing_setlist_assignment");
        context.Account.LiteralDate!.Value.Should().Be("19XX-XX-XX");
    }

    [Test]
    public async Task MissingImportedAccountRemainsFencedAgainstLaterArrival()
    {
        var source = await fixture.Source();
        var context = await Account("setlist.fm", source);
        var snapshot = await Bind(context);
        snapshot.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.Bound);
        snapshot.ImportedSetlist.Reason.Should().Be("missing_imported_setlist");
        await Imported(source, context.LegacyKey);

        var begin = () => Begin(snapshot);
        await begin.Should().ThrowAsync<NormalizationConflictException>();
    }

    [Test]
    public async Task MusicBrainzMappingChangesInvalidateTheOriginalRegistryReadSet()
    {
        var source = await fixture.Source();
        var context = await Account("setlist.fm", source);
        var snapshot = await Bind(context);
        var other = await fixture.Source();
        // The provider mapping already exists. Only the artist's MusicBrainz ID changes here.
        await Map(other, "setlist.fm", Guid.NewGuid().ToString("D"));
        snapshot = await Bind(context);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE artists SET musicbrainz_id=@key WHERE id=@id",
                new { key = context.ArtistKey, id = other.ArtistId }
            )
        );

        var begin = () => Begin(snapshot);
        await begin.Should().ThrowAsync<NormalizationConflictException>();
        (await Bind(context))
            .Artist.Status.Should()
            .Be(NormalizationCatalogBindingStatus.Ambiguous);
    }

    [TestCase("forged_account")]
    [TestCase("unknown_revision")]
    [TestCase("changed_bytes")]
    public async Task UnretainedOrAlteredAssertionsCannotBorrowCatalogAssociations(string fault)
    {
        var context = await Account("setlist.fm", await fixture.Source());
        if (fault == "forged_account")
        {
            context = context with
            {
                Account = context.Account with
                {
                    LiteralDate = context.Account.LiteralDate! with { Value = "invented date" },
                },
            };
        }
        if (fault == "unknown_revision")
        {
            var input = context.Input;
            context = context with
            {
                Input = new()
                {
                    Uuid = Guid.NewGuid(),
                    Resource = input.Resource,
                    Payload = input.Payload,
                    MediaType = input.MediaType,
                    CaptureVersion = input.CaptureVersion,
                },
            };
        }
        if (fault == "changed_bytes")
        {
            context.Input.Payload[0] = (byte)' ';
        }

        var bind = () => Bind(context);
        await bind.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task AnAccountFromAnotherArtistOnASetlistFmPageIsExplicitlyConflicting()
    {
        var source = await fixture.Source();
        var context = await Account(
            "setlist.fm",
            source,
            performerKey: Guid.NewGuid().ToString("D")
        );

        var result = await Bind(context);

        result.Artist.Status.Should().Be(NormalizationCatalogBindingStatus.ProviderConflict);
        result.Artist.Reason.Should().Be("performer_resource_conflict");
        result.CatalogInputs.Should().ContainSingle();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PhishNetExplicitScopeDeduplicatesRecordingsThatSupportOnePerformance(
        bool paddedShowId
    )
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        await MoveSource(other, source.ArtistId);
        var show = await Show(source.ArtistId, "late");
        var showId = Random.Shared.Next(1, int.MaxValue);
        var context = await PhishNetJson(showId, paddedShowId);
        await SourceEvidence(source, show.Id, showId);
        await SourceEvidence(other, show.Id, showId);
        var scope = await ArtistUuid(source.ArtistId);

        var result = await bindings.BindSetlistAsync(context.Input, context.Account, scope);

        result.ArtistScopeUuid.Should().Be(scope);
        result.ImportedSetlist.Reason.Should().Be("not_represented_in_catalog");
        result.Performance.Value!.ShowUuid.Should().Be(show.Uuid);
        result
            .Performance.Value.SupportingSourceUuids.Should()
            .BeEquivalentTo([source.SourceUuid, other.SourceUuid]);
        var unscoped = () => Bind(context);
        await unscoped.Should().ThrowAsync<ArgumentException>();
    }

    [Test]
    public async Task PhishNetSameAccountWithTwoSupportedPerformancesIsAmbiguousWithinScope()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        await MoveSource(other, source.ArtistId);
        var early = await Show(source.ArtistId, "early");
        var late = await Show(source.ArtistId, "late");
        var showId = Random.Shared.Next(1, int.MaxValue);
        var context = await PhishNetJson(showId);
        await SourceEvidence(source, early.Id, showId);
        await SourceEvidence(other, late.Id, showId);

        var result = await bindings.BindSetlistAsync(
            context.Input,
            context.Account,
            await ArtistUuid(source.ArtistId)
        );

        result.Performance.Status.Should().Be(NormalizationCatalogBindingStatus.Ambiguous);
        result.Performance.Reason.Should().Be("ambiguous_provider_performance");
    }

    [Test]
    public async Task PhishNetHtmlUsesTheStoredOfficialPermalinkWithoutDateMatching()
    {
        var source = await fixture.Source();
        var show = await Show(source.ArtistId, "late");
        const string path = "/setlists/phish-synthetic-explicit-account.html";
        await SourceEvidence(
            source,
            show.Id,
            987,
            "https://www.phish.net" + path + "?ignored=true"
        );
        var input = await Capture(
            new("phish.net", "ratings", "2000-01-01"),
            "phish-net-http-v1",
            "text/html",
            """
            <div class="setlist-container"><div class="setlist-date"><a href="/setlists/phish-synthetic-explicit-account.html">Phish</a></div>
            <div class="setlist-body"><p><span class="set-label">Set 1:</span><a href="/song/song">Song</a></p></div></div>
            """
        );
        var decoded = new NormalizationProviderDecoder().Decode(input);
        var account = decoded.Setlists.Single();

        var result = await bindings.BindSetlistAsync(
            input,
            account,
            await ArtistUuid(source.ArtistId)
        );

        result.Performance.Value!.ShowUuid.Should().Be(show.Uuid);
        result.Resource.Should().Be(input.Resource);
        account.Origin.Item.ProviderKey.Should().Be(path);
    }

    [Test]
    public async Task UnrelatedPhishNetPermalinksDoNotConsumeTheMatchingAccountLimit()
    {
        var source = await fixture.Source();
        var targetUuid = Guid.Parse(
            "ffffffff-ffff-ffff-ffff-" + Guid.NewGuid().ToString("N")[..12]
        );
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE sources SET uuid=@targetUuid WHERE id=@id",
                new { targetUuid, id = source.SourceId }
            )
        );
        source = source with { SourceUuid = targetUuid };
        var early = await Show(source.ArtistId, "early");
        var late = await Show(source.ArtistId, "late");
        const string targetPath = "/setlists/phish-synthetic-after-unrelated.html";
        await SourceEvidence(source, late.Id, 991, "https://www.phish.net" + targetPath);

        var nonce = Guid.NewGuid().ToString("N")[..12];
        await fixture.Db.WithWriteConnection(async c =>
        {
            await using var transaction = await ((NpgsqlConnection)c).BeginTransactionAsync();
            await c.ExecuteAsync(
                """
                INSERT INTO sources(artist_id,show_id,upstream_identifier,display_date,uuid,updated_at,is_soundboard,is_remaster,has_jamcharts)
                SELECT @artist,@early,'unrelated-phishnet-'||@nonce||':'||n,'2000-01-01',
                    ('00000000'||@nonce||lpad(n::text,12,'0'))::uuid,now(),false,false,false
                FROM generate_series(1,10001) n;
                INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
                SELECT id,@early,'accepted','2000-01-01',
                    '{"provider_evidence":{"phish.net":{"show_id":992,"permalink":"https://phish.net/setlists/phish-unrelated.html"}}}'::jsonb
                FROM sources WHERE artist_id=@artist AND upstream_identifier LIKE 'unrelated-phishnet-'||@nonce||':%';
                """,
                new
                {
                    artist = source.ArtistId,
                    early = early.Id,
                    nonce,
                },
                transaction
            );
            await transaction.CommitAsync();
        });
        var input = await Capture(
            new("phish.net", "ratings", "2000-01-01"),
            "phish-net-http-v1",
            "text/html",
            """
            <div class="setlist-container"><div class="setlist-date"><a href="/setlists/phish-synthetic-after-unrelated.html">Phish</a></div>
            <div class="setlist-body"><p><span class="set-label">Set 1:</span><a href="/song/song">Song</a></p></div></div>
            """
        );
        var account = new NormalizationProviderDecoder().Decode(input).Setlists.Single();

        var result = await bindings.BindSetlistAsync(
            input,
            account,
            await ArtistUuid(source.ArtistId)
        );

        result.Performance.Value!.ShowUuid.Should().Be(late.Uuid);
        result.Performance.Value.SupportingSourceUuids.Should().Equal(targetUuid);
    }

    [Test]
    public async Task CallerMutationDuringCatalogLookupCannotRedirectTheReproducedAccount()
    {
        var source = await fixture.Source();
        var rival = await fixture.Source();
        var context = await Account("phantasytour.com", source);
        var original = await Imported(source, context.LegacyKey);
        var rivalKey = Random.Shared.Next(1, int.MaxValue).ToString();
        await Map(rival, "phantasytour.com", rivalKey);
        await Imported(rival, context.LegacyKey);
        var callerPerformers = context.Account.Performers.ToArray();
        var callerAccount = context.Account with { Performers = callerPerformers };
        await using var blocker = new NpgsqlConnection(LocalTestDatabase.ConnectionString);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync(
            "LOCK TABLE normalization.catalog_mapping_registry_revision IN ACCESS EXCLUSIVE MODE",
            transaction: transaction
        );
        var binding = bindings.BindSetlistAsync(context.Input, callerAccount);
        try
        {
            await RequireBlockedBy(blocker.ProcessID);
            var performer = callerPerformers[0];
            var identity = performer.Identities.Single();
            callerPerformers[0] = performer with
            {
                Identities =
                [
                    identity with
                    {
                        Identity = identity.Identity with { ProviderKey = rivalKey },
                    },
                ],
            };
        }
        finally
        {
            await transaction.RollbackAsync();
            await binding;
        }

        var result = await binding;
        result.Artist.Value!.ArtistUuid.Should().Be(await ArtistUuid(source.ArtistId));
        result.ImportedSetlist.Value!.SetlistShowUuid.Should().Be(original.Uuid);
    }

    [Test]
    public async Task AnotherArtistDoesNotAlterAnExplicitlyScopedPhishNetAssociation()
    {
        var source = await fixture.Source();
        var other = await fixture.Source();
        var show = await Show(source.ArtistId, "early");
        var rival = await Show(other.ArtistId, "late");
        var showId = Random.Shared.Next(1, int.MaxValue);
        var context = await PhishNetJson(showId);
        await SourceEvidence(source, show.Id, showId);
        var snapshot = await bindings.BindSetlistAsync(
            context.Input,
            context.Account,
            await ArtistUuid(source.ArtistId)
        );
        await SourceEvidence(other, rival.Id, showId);

        var ticket = await Begin(snapshot);

        (await fixture.Work.TryPublishAsync(ticket.WorkUuid, (_, _, _) => Task.CompletedTask))
            .Status.Should()
            .Be(NormalizationPublicationStatus.Published);
        snapshot.ArtistScopeUuid.Should().Be(await ArtistUuid(source.ArtistId));
        snapshot.CatalogInputs.Should().ContainSingle();
    }

    private Task<NormalizationSetlistBindingSnapshot> Bind(AccountContext context) =>
        bindings.BindSetlistAsync(context.Input, context.Account);

    private Task<NormalizationWorkTicket> Begin(NormalizationSetlistBindingSnapshot snapshot) =>
        fixture.Work.BeginWorkAsync(
            Guid.NewGuid(),
            new("songs", "setlist", Guid.NewGuid()),
            "setlist-binding-test-v1",
            snapshot.Inputs.Select(input => input.Resource).ToArray(),
            [],
            catalogInputs: snapshot.CatalogInputs,
            mappingRegistry: snapshot.MappingRegistry,
            expectedInputRevisions: snapshot.Inputs
        );

    private async Task<AccountContext> Account(
        string provider,
        CatalogSource source,
        string date = "01-01-2000",
        string? performerKey = null,
        string? configuredArtistKey = null
    )
    {
        var artistKey =
            configuredArtistKey
            ?? (
                provider == "setlist.fm"
                    ? Guid.NewGuid().ToString("D")
                    : Random.Shared.Next(1, int.MaxValue).ToString()
            );
        await Map(source, provider, artistKey);
        var showId = Random.Shared.Next(1, int.MaxValue);
        string json;
        NormalizationResourceKey resource;
        string version;
        if (provider == "setlist.fm")
        {
            resource = new(provider, "setlist-page", artistKey + ":page=1");
            version = "setlist-fm-http-v1";
            json = JsonSerializer.Serialize(
                new
                {
                    page = 1,
                    itemsPerPage = 20,
                    total = 1,
                    setlist = new[]
                    {
                        new
                        {
                            id = "sf-" + showId,
                            eventDate = date,
                            artist = new
                            {
                                mbid = performerKey ?? artistKey,
                                name = "Fixture artist",
                            },
                            sets = new
                            {
                                set = new[]
                                {
                                    new
                                    {
                                        song = new[]
                                        {
                                            new { name = "Song" },
                                            new { name = "Song" },
                                        },
                                    },
                                },
                            },
                        },
                    },
                }
            );
        }
        else
        {
            resource = new(provider, "show", showId.ToString());
            version = "phantasy-tour-http-v1";
            json = JsonSerializer.Serialize(
                new
                {
                    data = new
                    {
                        id = showId,
                        dateTime = date,
                        band = new
                        {
                            id = int.Parse(performerKey ?? artistKey),
                            name = "Fixture artist",
                        },
                        sets = new[]
                        {
                            new
                            {
                                id = 1,
                                name = "Set 1",
                                songs = new[]
                                {
                                    new { id = 1, name = "Song" },
                                    new { id = 1, name = "Song" },
                                },
                            },
                        },
                    },
                }
            );
        }
        var input = await Capture(resource, version, "application/json", json);
        return new(
            input,
            new NormalizationProviderDecoder().Decode(input).Setlists.Single(),
            artistKey,
            provider == "setlist.fm" ? "sf-" + showId : "pt:" + showId
        );
    }

    private async Task<AccountContext> PhishNetJson(int showId, bool paddedShowId = false)
    {
        var input = await Capture(
            new("phish.net", "shows", "v5"),
            "phish-net-http-v1",
            "application/json",
            JsonSerializer.Serialize(
                new
                {
                    data = new[]
                    {
                        new
                        {
                            showid = paddedShowId ? (object)("000" + showId) : showId,
                            artistid = 1,
                            artist_name = "Phish",
                            showdate = "2000-01-01",
                        },
                    },
                }
            )
        );
        return new(
            input,
            new NormalizationProviderDecoder().Decode(input).Setlists.Single(),
            "",
            ""
        );
    }

    private async Task<NormalizationInputRevision> Capture(
        NormalizationResourceKey resource,
        string version,
        string mediaType,
        string payload
    )
    {
        var current = await evidence.GetCurrentInputAsync(resource);
        return (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    Encoding.UTF8.GetBytes(payload),
                    mediaType,
                    version,
                    current?.Generation ?? 0,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
    }

    private Task Map(CatalogSource source, string providerName, string key) =>
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
            if (providerName == "setlist.fm")
            {
                await c.ExecuteAsync(
                    "UPDATE artists SET musicbrainz_id=@key WHERE id=@id",
                    new { key, id = source.ArtistId }
                );
            }
            await c.ExecuteAsync(
                "INSERT INTO artists_upstream_sources(artist_id,upstream_source_id,upstream_identifier) VALUES(@artist,@provider,@key)",
                new
                {
                    artist = source.ArtistId,
                    provider,
                    key,
                }
            );
        });

    private Task<Guid> ArtistUuid(int id) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@id", new { id })
        );

    private Task<CatalogRow> Imported(CatalogSource source, string key) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            var venue = await c.ExecuteScalarAsync<long>(
                "INSERT INTO venues(artist_id,name,location,uuid,updated_at) VALUES(@artist,'Fixture hall','Fixture town',@uuid,now()) RETURNING id",
                new { artist = source.ArtistId, uuid = Guid.NewGuid() }
            );
            return await c.QuerySingleAsync<CatalogRow>(
                "INSERT INTO setlist_shows(artist_id,venue_id,date,upstream_identifier,uuid,updated_at) VALUES(@artist,@venue,'2000-01-01',@key,@uuid,now()) RETURNING id AS Id,uuid AS Uuid",
                new
                {
                    artist = source.ArtistId,
                    venue,
                    key,
                    uuid = Guid.NewGuid(),
                }
            );
        });

    private Task<CatalogRow> Show(int artist, string label) =>
        fixture.Db.WithWriteConnection(c =>
            c.QuerySingleAsync<CatalogRow>(
                "INSERT INTO shows(artist_id,date,display_date,performance_key,performance_type,is_legacy_survivor,uuid,updated_at) VALUES(@artist,'2000-01-01','2000-01-01',@key,@label,false,@uuid,now()) RETURNING id AS Id,uuid AS Uuid",
                new
                {
                    artist,
                    key = "setlist-binding-" + Guid.NewGuid(),
                    label,
                    uuid = Guid.NewGuid(),
                }
            )
        );

    private Task Assign(long setlistId, long showId) =>
        fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "INSERT INTO setlist_show_assignments(setlist_show_id,show_id,evidence) VALUES(@setlistId,@showId,'{}')",
                new { setlistId, showId }
            )
        );

    private Task MoveSource(CatalogSource source, int artist) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE source_tracks SET artist_id=@artist,mp3_url=@url WHERE source_id=@id",
                new
                {
                    artist,
                    url = "https://example.test/" + source.SourceUuid + ".mp3",
                    id = source.SourceId,
                }
            );
            await c.ExecuteAsync(
                "UPDATE sources SET artist_id=@artist WHERE id=@id",
                new { artist, id = source.SourceId }
            );
        });

    private Task SourceEvidence(
        CatalogSource source,
        long showId,
        int providerShowId,
        string? permalink = null
    ) =>
        fixture.Db.WithWriteConnection(async c =>
        {
            await c.ExecuteAsync(
                "UPDATE sources SET show_id=@showId WHERE id=@id",
                new { showId, id = source.SourceId }
            );
            var json = JsonSerializer.Serialize(
                new
                {
                    provider_evidence = new Dictionary<string, object>
                    {
                        ["phish.net"] = new { show_id = providerShowId, permalink },
                    },
                }
            );
            await c.ExecuteAsync(
                "INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence) VALUES(@id,@showId,'accepted','2026-10-17',@json::jsonb)",
                new
                {
                    id = source.SourceId,
                    showId,
                    json,
                }
            );
        });

    private sealed record CatalogRow(long Id, Guid Uuid);

    private async Task RequireBlockedBy(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (
                await fixture.Db.WithWriteConnection(c =>
                    c.ExecuteScalarAsync<bool>(
                        "SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE @pid=ANY(pg_blocking_pids(pid)))",
                        new { pid }
                    )
                )
            )
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail("The binding did not reach the expected registry lock wait.");
    }

    private sealed record AccountContext(
        NormalizationInputRevision Input,
        ProviderSetlistObservation Account,
        string ArtistKey,
        string LegacyKey
    );
}
