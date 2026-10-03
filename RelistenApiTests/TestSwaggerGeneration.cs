using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Newtonsoft.Json.Linq;
using Relisten;
using Relisten.Api.Models.Api;
using Swashbuckle.AspNetCore.Swagger;

namespace RelistenApiTests;

[TestFixture]
public sealed class TestSwaggerGeneration
{
    [TestCase("v2", true)]
    [TestCase("v3", false)]
    public async Task Swagger_documents_serialize_real_controller_contracts(string version, bool includesNumericIds)
    {
        // Exercise the production controllers and serializers without Startup's database/Redis connections.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Startup).Assembly.GetName().Name
        });
        builder.Services.AddTransient<IConfigureOptions<MvcNewtonsoftJsonOptions>, RelistenApiJsonOptionsWrapper>();
        builder.Services.AddMvc(options => options.EnableEndpointRouting = false)
            .AddApplicationPart(typeof(Startup).Assembly)
            .AddNewtonsoftJson();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v2", new OpenApiInfo { Title = "Relisten API", Version = "v2" });
            options.SwaggerDoc("v3", new OpenApiInfo { Title = "Relisten API", Version = "v3" });
            options.SchemaFilter<SwaggerSkipV2PropertyFilter>();
        });
        builder.Services.AddSwaggerGenNewtonsoftSupport();

        await using var services = builder.Services.BuildServiceProvider();
        var document = await services.GetRequiredService<IAsyncSwaggerProvider>().GetSwaggerAsync(version);
        using var output = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(output));
        var json = JObject.Parse(output.ToString());

        json["info"]!["version"]!.Value<string>().Should().Be(version);
        json["paths"]![$"/api/{version}/artists"]!["get"].Should().NotBeNull();
        var artistProperties = (JObject)json["components"]!["schemas"]!["ArtistWithCounts"]!["properties"]!;
        artistProperties.ContainsKey("uuid").Should().BeTrue();
        artistProperties.ContainsKey("id").Should().Be(includesNumericIds);
    }
}
