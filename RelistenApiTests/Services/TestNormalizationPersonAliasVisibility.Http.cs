using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Relisten.Controllers;
using Relisten.Data;

namespace RelistenApiTests.Services;

public sealed partial class TestNormalizationPersonAliasVisibility
{
    [Test]
    public async Task HttpSearchAndDetailsFollowOriginalAliasReviewThroughMergeRestorationAndReversal()
    {
        var name = AliasName();
        var original = await Person(name);
        var survivor = await Person(name);
        var unrelated = await Person(name);
        var originalUuid = original.Person.Uuid;
        var survivorUuid = survivor.Person.Uuid;
        var unrelatedUuid = unrelated.Person.Uuid;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(
            new NormalizationReadService(
                fixture.Db,
                Options.Create(new NormalizationReadOptions { Enabled = true })
            )
        );
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationPeopleVenuesController).Assembly)
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

        await AssertHttpAliasSearch(client, name, originalUuid, survivorUuid, unrelatedUuid);
        await AssertHttpAliasDetail(client, originalUuid, originalUuid, name, visible: true);
        await AssertHttpAliasDetail(client, survivorUuid, survivorUuid, name, visible: true);

        var merge = await Merge(original, survivor);

        await AssertHttpAliasSearch(client, name, survivorUuid, unrelatedUuid);
        await AssertHttpAliasDetail(client, originalUuid, survivorUuid, name, visible: true);
        await AssertHttpAliasDetail(client, survivorUuid, survivorUuid, name, visible: true);

        await Review(original, "reject");

        await AssertHttpAliasSearch(client, name, unrelatedUuid);
        await AssertHttpAliasDetail(client, originalUuid, survivorUuid, name, visible: false);
        await AssertHttpAliasDetail(client, survivorUuid, survivorUuid, name, visible: false);
        await AssertHttpAliasDetail(client, unrelatedUuid, unrelatedUuid, name, visible: true);

        var restored = original.Alias with { Uuid = Guid.NewGuid() };
        await Review(original, "accept", restored);

        await AssertHttpAliasSearch(client, name, survivorUuid, unrelatedUuid);
        await AssertHttpAliasDetail(client, originalUuid, survivorUuid, name, visible: true);
        await AssertHttpAliasDetail(client, survivorUuid, survivorUuid, name, visible: true);

        await Reverse(original, survivor, merge);

        await AssertHttpAliasSearch(client, name, originalUuid, survivorUuid, unrelatedUuid);
        await AssertHttpAliasDetail(client, originalUuid, originalUuid, name, visible: true);
        await AssertHttpAliasDetail(client, survivorUuid, survivorUuid, name, visible: true);
        await AssertHttpAliasDetail(client, unrelatedUuid, unrelatedUuid, name, visible: true);
        await app.StopAsync();
    }

    private static async Task AssertHttpAliasSearch(
        HttpClient client,
        string alias,
        params Guid[] expected
    )
    {
        using var response = await client.GetAsync(
            $"/api/v3/normalization/people?q={Uri.EscapeDataString(alias)}"
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        var items = json["items"]!.ToArray();
        items.Select(item => item["uuid"]!.ToObject<Guid>()).Should().BeEquivalentTo(expected);
        foreach (var item in items)
        {
            item["aliases"]!.Values<string>().Should().Contain(alias);
        }
    }

    private static async Task AssertHttpAliasDetail(
        HttpClient client,
        Guid requested,
        Guid resolved,
        string alias,
        bool visible
    )
    {
        using var response = await client.GetAsync($"/api/v3/normalization/people/{requested}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        json["uuid"]!.ToObject<Guid>().Should().Be(resolved);
        json["requested_uuid"]!
            .ToObject<Guid?>()
            .Should()
            .Be(requested == resolved ? null : requested);
        json["redirect_uuid"]!
            .ToObject<Guid?>()
            .Should()
            .Be(requested == resolved ? null : resolved);
        json["aliases"]!.Values<string>().Contains(alias).Should().Be(visible);
    }
}
