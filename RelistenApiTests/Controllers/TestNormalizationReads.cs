using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models.Api;
using Relisten.Api.Models.Normalization;
using Relisten.Controllers;
using Relisten.Data;

namespace RelistenApiTests.Controllers;

[TestFixture]
public sealed class TestNormalizationReads
{
    private static NormalizationPeopleVenuesController Controller(bool enabled = false) =>
        new(
            new NormalizationReadService(
                null!,
                Options.Create(new NormalizationReadOptions { Enabled = enabled })
            )
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Test]
    public async Task InvalidUuidAndFiltersReturnStableProblemAndNoStore()
    {
        var controller = Controller();
        var invalid = (ObjectResult)await controller.Person("not-a-uuid");
        invalid.StatusCode.Should().Be(400);
        ((ProblemDetails)invalid.Value!).Extensions["code"].Should().Be("invalid_request");
        controller.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        var role = (ObjectResult)
            await controller.PersonRecordings(Guid.NewGuid().ToString(), role: "singer");
        role.StatusCode.Should().Be(400);
        var filter = (ObjectResult)
            await controller.VenueShows(Guid.NewGuid().ToString(), artistUuid: "invalid");
        filter.StatusCode.Should().Be(400);
        var limit = (ObjectResult)await controller.People(limit: 0);
        limit.StatusCode.Should().Be(400);
    }

    [Test]
    public async Task DisabledReadsReturnUnavailableRatherThanEmptyCatalog()
    {
        var controller = Controller();
        var result = (ObjectResult)await controller.People();
        result.StatusCode.Should().Be(503);
        ((ProblemDetails)result.Value!).Extensions["code"].Should().Be("normalization_unavailable");
    }

    [Test]
    public async Task HttpBindingReturnsProblemCodesForMalformedLimitsAndEmptySearch()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(
            new NormalizationReadService(null!, Options.Create(new NormalizationReadOptions()))
        );
        builder
            .Services.AddControllers()
            .AddApplicationPart(typeof(NormalizationPeopleVenuesController).Assembly)
            .AddNewtonsoftJson();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        var addresses = app
            .Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        using var client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.Single()) };
        foreach (
            var path in new[]
            {
                "people?limit=abc",
                "people?q=",
                "venues/not-a-uuid",
                "people?limit=201",
                "people?cursor=",
                "people/11111111-1111-1111-1111-111111111111/recordings?role=",
                "venues/11111111-1111-1111-1111-111111111111/shows?artist_uuid=",
                "recordings/11111111-1111-1111-1111-111111111111/credits?cursor=",
            }
        )
        {
            using var response = await client.GetAsync("/api/v3/normalization/" + path);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, path);
            var problem = JObject.Parse(await response.Content.ReadAsStringAsync());
            problem["code"]!.Value<string>().Should().Be("invalid_request");
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
        using var unavailable = await client.GetAsync("/api/v3/normalization/people");
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        await app.StopAsync();
    }

    [Test]
    public void PublicJsonKeepsNullFieldsAndSnakeCasePlaybackReferences()
    {
        var json = JObject.Parse(
            JsonConvert.SerializeObject(
                new NormalizationPage<NormalizedPerson>
                {
                    items =
                    [
                        new NormalizedPerson
                        {
                            uuid = Guid.NewGuid(),
                            display_name = "Pat",
                            kind = "person",
                        },
                    ],
                },
                RelistenApiJsonOptionsWrapper.ApiV3SerializerSettings
            )
        );
        json["next_cursor"]!.Type.Should().Be(JTokenType.Null);
        json["items"]![0]!["requested_uuid"]!.Type.Should().Be(JTokenType.Null);
        json["items"]![0]!["display_name"]!.Value<string>().Should().Be("Pat");
    }
}
