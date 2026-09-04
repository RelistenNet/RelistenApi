using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Relisten.Api.Models.Api;

namespace RelistenApiTests;

[TestFixture]
public class TestSwaggerGeneration
{
    [Test]
    public async Task V2_OpenApi_Doc_Can_Be_Generated()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddMvcCore()
            .AddNewtonsoftJson()
            .AddApiExplorer()
            .AddApplicationPart(typeof(Relisten.Api.RelistenBaseController).Assembly);
        builder.Services.AddOpenApi("v2", options =>
        {
            options.AddSchemaTransformer<SkipV2PropertySchemaTransformer>();
        });

        var app = builder.Build();
        app.MapOpenApi();

        var docProvider = app.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v2");
        var doc = await docProvider.GetOpenApiDocumentAsync(CancellationToken.None);
        Assert.That(doc, Is.Not.Null);
    }
}
