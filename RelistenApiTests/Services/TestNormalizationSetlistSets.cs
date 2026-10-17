using System.Net;
using System.Text.Json;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;

namespace RelistenApiTests.Services;

/// <summary>Public set pages retain empty sets and cite only the selected set's accepted evidence.</summary>
[TestFixture, NonParallelizable, Category("LocalIngestion")]
public sealed class TestNormalizationSetlistSets
{
    private NormalizationDomainFixture fixture = null!;
    private NormalizationEvidenceService evidence = null!;
    private NormalizationReadService reads = null!;
    private readonly NormalizationReadOptions options = new() { Enabled = true };

    [OneTimeSetUp]
    public void Start()
    {
        fixture = new();
        evidence = new(fixture.Db);
        reads = new(fixture.Db, Options.Create(options));
    }

    [OneTimeTearDown]
    public void Stop() => fixture?.Dispose();

    [Test]
    public async Task PagesRetainEmptyRepeatedAndUnnamedSetsWithUnknownPositionsAfterMaximumInteger()
    {
        var context = await Snapshot();
        var known = (await reads.ListSetlistSetsAsync(context.EditionUuid, 2))!;
        known.items.Select(set => set.position).Should().Equal(1, int.MaxValue);
        known.items.Select(set => set.label).Should().Equal("Repeated", "Repeated");
        known.items[1].encore_number.Should().Be(1);
        known.next_cursor.Should().NotBeNull();
        var unknown = (
            await reads.ListSetlistSetsAsync(context.EditionUuid, 1, known.next_cursor)
        )!;
        unknown.items.Should().ContainSingle().Which.position.Should().BeNull();
        var last = (await reads.ListSetlistSetsAsync(context.EditionUuid, 1, unknown.next_cursor))!;
        last.items.Should().ContainSingle().Which.position.Should().BeNull();
        last.next_cursor.Should().BeNull();
        var all = known.items.Concat(unknown.items).Concat(last.items).ToArray();
        all.Select(set => set.uuid).Should().OnlyHaveUniqueItems();
        all.Select(set => set.uuid)
            .Should()
            .BeEquivalentTo(
                context.Structure.Sets.Where(set => !set.Retracted).Select(set => set.Uuid)
            );
        all.Should().Contain(set => set.label == null);
        all.Should()
            .OnlyContain(set =>
                set.edition_uuid == context.EditionUuid
                && set.revision == 7
                && set.basis == "reviewed"
            );
        foreach (var set in all)
        {
            var original = context.Structure.Sets.Single(original => original.Uuid == set.uuid);
            set.evidence.Should().ContainSingle();
            set.evidence.Single().json_pointer.Should().Be(original.Evidence.Single().JsonPointer);
            set.evidence.Single().input_revision_uuid.Should().Be(context.Input.Uuid);
        }
    }

    [Test]
    public async Task ExactSetCitationsKeepTheirOriginalBytesAndReportChangedOrMissingResourceHeads()
    {
        var context = await Snapshot();
        var before = (await reads.ListSetlistSetsAsync(context.EditionUuid))!.items;
        before.Should().OnlyContain(set => set.evidence_status == "current");
        var first = before.Single(set => set.position == 1);
        first.evidence.Single().quote.Should().Be("Repeated");
        first.evidence.Single().start_code_point.Should().Be(0);
        first.evidence.Single().end_code_point.Should().Be(8);
        await evidence.CaptureInputAsync(
            new(
                Guid.NewGuid(),
                context.Input.Resource,
                JsonSerializer.SerializeToUtf8Bytes(
                    new { sets = new[] { new { name = "Corrected" } } }
                ),
                "application/json",
                "set-read-fixture-v1",
                1,
                DateTimeOffset.UtcNow
            )
        );

        var changed = (await reads.ListSetlistSetsAsync(context.EditionUuid))!.items;

        changed.Should().OnlyContain(set => set.evidence_status == "changed");
        changed
            .SelectMany(set => set.evidence)
            .Should()
            .OnlyContain(citation => citation.input_revision_uuid == context.Input.Uuid);
        changed
            .Single(set => set.uuid == first.uuid)
            .evidence.Single()
            .quote.Should()
            .Be("Repeated");
        changed
            .SelectMany(set => set.evidence)
            .Should()
            .NotContain(citation => citation.json_pointer == "/account_notes");
        // A rejected initial CAS retains bytes without inventing a current resource head.
        var missing = await Snapshot(captureCurrent: false);
        (await reads.ListSetlistSetsAsync(missing.EditionUuid))!
            .items.Should()
            .OnlyContain(set => set.evidence_status == "missing");
    }

    [Test]
    public async Task KnownEditionWithoutStructureIsEmptyAndTombstonesStayOutOfPublicPages()
    {
        var empty = await Snapshot(includeStructure: false);
        var page = await reads.ListSetlistSetsAsync(empty.EditionUuid);
        page.Should().NotBeNull();
        page!.items.Should().BeEmpty();
        page.next_cursor.Should().BeNull();
        (await reads.ListSetlistSetsAsync(Guid.NewGuid())).Should().BeNull();
        var populated = await Snapshot();
        (await reads.ListSetlistSetsAsync(populated.EditionUuid))!
            .items.Select(set => set.uuid)
            .Should()
            .NotContain(populated.Structure.Sets.Single(set => set.Retracted).Uuid);
        await fixture.Db.WithWriteConnection(c =>
            c.ExecuteAsync(
                "UPDATE normalization.setlist_editions SET retracted=true WHERE uuid=@uuid",
                new { uuid = populated.EditionUuid }
            )
        );
        (await reads.ListSetlistSetsAsync(populated.EditionUuid)).Should().BeNull();
    }

    [Test]
    public async Task HttpRoutePreservesNullableFieldsScopedEvidenceAndExistingErrorContracts()
    {
        var context = await Snapshot();
        var empty = await Snapshot(includeStructure: false);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(reads);
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationMusicController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(
                app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single()
            ),
        };
        using var response = await client.GetAsync(
            $"/api/v3/normalization/setlists/{context.EditionUuid}/sets?limit=2"
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var first = JObject.Parse(await response.Content.ReadAsStringAsync());
        first["items"]!.Should().HaveCount(2);
        first["items"]![0]!["encore_number"]!.Type.Should().Be(JTokenType.Null);
        first["items"]![0]!["revision"]!.Value<long>().Should().Be(7);
        first["items"]![0]!["evidence"]!.Single()!["json_pointer"]!
            .Value<string>()
            .Should()
            .Be("/sets/0/name");
        var cursor = first["next_cursor"]!.Value<string>()!;
        using var next = await client.GetAsync(
            $"/api/v3/normalization/setlists/{context.EditionUuid}/sets?cursor={Uri.EscapeDataString(cursor)}"
        );
        var rest = JObject.Parse(await next.Content.ReadAsStringAsync());
        rest["items"]!.Should().HaveCount(2);
        rest["items"]!.Should().OnlyContain(item => item!["position"]!.Type == JTokenType.Null);
        rest["items"]!.Should().Contain(item => item!["label"]!.Type == JTokenType.Null);
        foreach (var item in first["items"]!.Concat(rest["items"]!))
        {
            item!["evidence"]!.Should().ContainSingle();
            item["evidence"]!.Single()!["json_pointer"]!
                .Value<string>()
                .Should()
                .StartWith("/sets/");
            ((JObject)item).Property("sources").Should().BeNull();
        }
        using var knownEmpty = await client.GetAsync(
            $"/api/v3/normalization/setlists/{empty.EditionUuid}/sets"
        );
        knownEmpty.StatusCode.Should().Be(HttpStatusCode.OK);
        JObject.Parse(await knownEmpty.Content.ReadAsStringAsync())["items"]!.Should().BeEmpty();
        using var missing = await client.GetAsync(
            $"/api/v3/normalization/setlists/{Guid.NewGuid()}/sets"
        );
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (
            var path in new[]
            {
                "not-a-uuid/sets",
                $"{context.EditionUuid}/sets?limit=abc",
                $"{context.EditionUuid}/sets?cursor=",
                $"{empty.EditionUuid}/sets?cursor={Uri.EscapeDataString(cursor)}",
            }
        )
        {
            using var invalid = await client.GetAsync("/api/v3/normalization/setlists/" + path);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            JObject.Parse(await invalid.Content.ReadAsStringAsync())["code"]!
                .Value<string>()
                .Should()
                .Be("invalid_request");
            invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        options.Enabled = false;
        try
        {
            using var disabled = await client.GetAsync(
                $"/api/v3/normalization/setlists/{context.EditionUuid}/sets"
            );
            disabled.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            JObject.Parse(await disabled.Content.ReadAsStringAsync())["code"]!
                .Value<string>()
                .Should()
                .Be("normalization_unavailable");
        }
        finally
        {
            options.Enabled = true;
        }
        await app.StopAsync();
    }

    // These read fixtures specify accepted snapshots directly. Complete-image publication,
    // correspondence and editorial validation are exercised by TestNormalizationSetlistStructure.
    private async Task<ReadSnapshot> Snapshot(
        bool includeStructure = true,
        bool captureCurrent = true
    )
    {
        var source = await fixture.Source();
        var artist = await fixture.Db.WithWriteConnection(c =>
            c.ExecuteScalarAsync<Guid>("SELECT uuid FROM artists WHERE id=@ArtistId", source)
        );
        var resource = new NormalizationResourceKey(
            "set-read-fixture",
            "account",
            Guid.NewGuid().ToString("D")
        );
        var labels = new string?[] { "Repeated", "Repeated", null, "Unknown order", "Retired" };
        var input = (
            await evidence.CaptureInputAsync(
                new(
                    Guid.NewGuid(),
                    resource,
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            sets = labels.Select(name => new { name }),
                            account_notes = "Whole account evidence is not a set citation",
                        }
                    ),
                    "application/json",
                    "set-read-fixture-v1",
                    captureCurrent ? 0 : 1,
                    DateTimeOffset.UtcNow
                )
            )
        ).Revision;
        var edition = new SetlistEditionChange
        {
            Uuid = Guid.NewGuid(),
            ArtistUuid = artist,
            LiteralDate = "XXXX-XX-XX",
            Ordered = true,
            Provider = resource.Provider,
            ResourceType = resource.ResourceType,
            ProviderKey = resource.ProviderKey,
        };
        var sets = labels
            .Select(
                (label, index) =>
                    new SetlistStructureSet
                    {
                        Uuid = Guid.NewGuid(),
                        Position =
                            index == 0 ? 1
                            : index == 1 ? int.MaxValue
                            : null,
                        Label = label,
                        EncoreNumber = index == 1 ? 1 : null,
                        Retracted = index == 4,
                        Evidence =
                        [
                            label == null
                                ? new(input.Uuid, $"/sets/{index}")
                                : new(input.Uuid, $"/sets/{index}/name", 0, label.Length, label),
                        ],
                    }
            )
            .ToArray();
        var structure = new SetlistStructureChange
        {
            Uuid = Guid.NewGuid(),
            Edition = edition,
            Sets = sets,
            Evidence = [new(input.Uuid, "/account_notes")],
        };
        await fixture.Db.WithWriteConnection(async c =>
        {
            var editionRevision = await Revision(
                c,
                edition.Uuid,
                "setlist_editions",
                1,
                JsonSerializer.Serialize(edition)
            );
            await c.ExecuteAsync(
                "INSERT INTO normalization.setlist_editions(uuid,artist_uuid,literal_date,ordered,provider,resource_type,provider_key,selected,revision_uuid) VALUES(@Uuid,@ArtistUuid,@LiteralDate,@Ordered,@Provider,@ResourceType,@ProviderKey,false,@revision)",
                new
                {
                    edition.Uuid,
                    edition.ArtistUuid,
                    edition.LiteralDate,
                    edition.Ordered,
                    edition.Provider,
                    edition.ResourceType,
                    edition.ProviderKey,
                    revision = editionRevision,
                }
            );
            if (!includeStructure)
            {
                return;
            }
            var revision = await Revision(
                c,
                structure.Uuid,
                "setlist_structures",
                7,
                JsonSerializer.Serialize(structure)
            );
            await c.ExecuteAsync(
                "INSERT INTO normalization.setlist_structures(uuid,edition_uuid,revision_uuid) VALUES(@Uuid,@edition,@revision)",
                new
                {
                    structure.Uuid,
                    edition = edition.Uuid,
                    revision,
                }
            );
            foreach (var set in sets)
            {
                await c.ExecuteAsync(
                    "INSERT INTO normalization.setlist_sets(uuid,edition_uuid,position,label,encore_number,retracted,revision_uuid) VALUES(@Uuid,@edition,@Position,@Label,@EncoreNumber,@Retracted,@revision)",
                    new
                    {
                        set.Uuid,
                        edition = edition.Uuid,
                        set.Position,
                        set.Label,
                        set.EncoreNumber,
                        set.Retracted,
                        revision,
                    }
                );
            }
            await c.ExecuteAsync(
                "INSERT INTO normalization.music_evidence_references(revision_uuid,ordinal,input_revision_uuid,json_pointer) VALUES(@revision,0,@input,'/account_notes')",
                new { revision, input = input.Uuid }
            );
        });
        return new(edition.Uuid, structure, input);
    }

    private static Task<Guid> Revision(
        System.Data.IDbConnection c,
        Guid entity,
        string kind,
        int number,
        string payload
    ) =>
        c.QuerySingleAsync<Guid>(
            "INSERT INTO normalization.music_revisions(uuid,entity_uuid,entity_kind,revision,decision_uuid,basis,payload,evidence) VALUES(gen_random_uuid(),@entity,@kind,@number,gen_random_uuid(),'reviewed',@payload::jsonb,'[]') RETURNING uuid",
            new
            {
                entity,
                kind,
                number,
                payload,
            }
        );

    private sealed record ReadSnapshot(
        Guid EditionUuid,
        SetlistStructureChange Structure,
        NormalizationInputRevision Input
    );
}
