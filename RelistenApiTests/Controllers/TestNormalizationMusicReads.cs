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

namespace RelistenApiTests.Controllers;

[TestFixture]
public sealed class TestNormalizationMusicReads
{
    [Test]
    public async Task HttpBindingRejectsInvalidMusicParametersBeforeDisabledReads()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(
            new NormalizationReadService(null!, Options.Create(new NormalizationReadOptions()))
        );
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
        foreach (
            var route in new[]
            {
                "songs/not-a-uuid",
                "artists/phish/songs?q=",
                "artists/phish/songs?basis=",
                "artists/phish/songs?basis=validated_match",
                "compositions?q=😀",
                "compositions?limit=abc",
                "compositions?cursor=",
                "tracks/not-a-uuid/contents",
                "compositions/11111111-1111-1111-1111-111111111111/shows?artist_uuid=",
                "setlists/11111111-1111-1111-1111-111111111111/occurrences?limit=201",
            }
        )
        {
            using var response = await client.GetAsync("/api/v3/normalization/" + route);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, route);
            JObject.Parse(await response.Content.ReadAsStringAsync())["code"]!
                .Value<string>()
                .Should()
                .Be("invalid_request");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        using var disabled = await client.GetAsync("/api/v3/normalization/compositions");
        disabled.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        JObject.Parse(await disabled.Content.ReadAsStringAsync())["code"]!
            .Value<string>()
            .Should()
            .Be("normalization_unavailable");
        await app.StopAsync();
    }
}
