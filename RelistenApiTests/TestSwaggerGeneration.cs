using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models.Api;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestSwaggerGeneration
{
    [TestCase("v2", true)]
    [TestCase("v3", false)]
    public async Task OpenApi_documents_serialize_real_controller_contracts(string version, bool includesNumericIds)
    {
        // Exercise the production controllers and serializers without Startup's database/Redis connections.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Startup).Assembly.GetName().Name
        });
        builder.Services.AddTransient<IConfigureOptions<MvcNewtonsoftJsonOptions>, RelistenApiJsonOptionsWrapper>();
        builder.Services.AddMvc()
            .AddApplicationPart(typeof(Startup).Assembly)
            .AddNewtonsoftJson();
        builder.Services.AddOpenApi(version, options =>
        {
            options.CreateSchemaReferenceId = typeInfo => typeInfo.Type.IsEnum
                ? null : OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
            options.AddDocumentTransformer<RelistenOpenApiDocumentTransformer>();
            options.AddSchemaTransformer<SkipV2PropertySchemaTransformer>();
        });

        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();

        var provider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>(version);
        var document = await provider.GetOpenApiDocumentAsync(CancellationToken.None);
        using var output = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(output));
        var json = JObject.Parse(output.ToString());

        json["info"]!["version"]!.Value<string>().Should().Be(version);
        json["info"]!["title"]!.Value<string>().Should().Be("Relisten API");
        json["paths"]![$"/api/{version}/artists"]!["get"].Should().NotBeNull();
        json["paths"]!["/api/v3/popular/artists"]!["get"].Should().NotBeNull();
        json["paths"]!["/relisten-admin/login"].Should().BeNull();
        json["paths"]!["/api/v2/artists"]!["post"].Should().BeNull();
        var artistProperties = (JObject)json["components"]!["schemas"]!["ArtistWithCounts"]!["properties"]!;
        artistProperties.ContainsKey("uuid").Should().BeTrue();
        artistProperties.ContainsKey("id").Should().Be(includesNumericIds);

        var playProperties = json["components"]!["schemas"]!["SourceTrackPlay"]!["properties"]!;
        playProperties["app_type"]!["type"]!.Value<string>().Should().Be("integer");
        playProperties["app_type_description"]!["enum"]!.Values<string>().Should().Contain("Web");
        json["components"]!["schemas"]!["SourceFull"]!["properties"]!["flac_type"]!["enum"]!
            .Values<string>().Should().Contain("Flac16Bit");
    }
}
