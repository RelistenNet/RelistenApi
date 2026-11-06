using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Relisten;
using Relisten.Controllers.V4;
using Swashbuckle.AspNetCore.Swagger;

if (
    args.Any(arg => arg.StartsWith("--") && arg != "--check")
    || args.Count(arg => arg != "--check") > 1
)
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tools/generate-artist-features -- [--check] [output.ts]"
    );
    return 1;
}

// Build only controller metadata and Swagger. Do not execute API Startup or resolve database services.
var builder = WebApplication.CreateBuilder(
    new WebApplicationOptions
    {
        ApplicationName = typeof(CatalogBrowseController).Assembly.GetName().Name,
    }
);
builder.Services.AddTransient<
    IConfigureOptions<MvcNewtonsoftJsonOptions>,
    RelistenApiJsonOptionsWrapper
>();
builder
    .Services.AddControllers()
    .AddApplicationPart(typeof(CatalogBrowseController).Assembly)
    .AddNewtonsoftJson();
builder.Services.AddSwaggerGen(V4CatalogRegistration.ConfigureSwagger);
builder.Services.AddSwaggerGenNewtonsoftSupport();

await using var services = builder.Services.BuildServiceProvider();
var document = await services.GetRequiredService<IAsyncSwaggerProvider>().GetSwaggerAsync("v4");
using var swagger = new StringWriter();
document.SerializeAsV3(new OpenApiJsonWriter(swagger));
var flags = JsonNode.Parse(swagger.ToString())!["components"]!["schemas"]!["ArtistResource"]![
    "properties"
]!["features"]!["x-feature-flags"]!.AsObject();
var constants = string.Join(
    "\n",
    flags.Select(flag => $"  {JsonSerializer.Serialize(flag.Key)}: {flag.Value!.ToJsonString()},")
);
var source = (
    $$"""
        // Generated from OpenAPI ArtistResource.features.x-feature-flags. Do not edit.
        // Regenerate: dotnet run --project tools/generate-artist-features

        export const ArtistFeature = {
        {{constants}}
        } as const;

        export type ArtistFeature = (typeof ArtistFeature)[keyof typeof ArtistFeature];

        /** Checks one named feature without losing precision or expanding the wire mask. */
        export function hasArtistFeature(
          artist: { readonly features: string },
          feature: ArtistFeature,
        ): boolean {
          return (BigInt(artist.features) & BigInt(feature)) !== BigInt(0);
        }
        """ + "\n"
).ReplaceLineEndings("\n");

var output = Path.GetFullPath(
    args.FirstOrDefault(arg => arg != "--check") ?? "clients/typescript/artist-features.ts"
);
if (args.Contains("--check"))
{
    if (!File.Exists(output) || await File.ReadAllTextAsync(output) != source)
    {
        Console.Error.WriteLine(
            $"{output} is out of date. Regenerate it with tools/generate-artist-features."
        );
        return 1;
    }
    Console.WriteLine($"{output} is up to date.");
    return 0;
}

Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, source);
Console.WriteLine($"Generated {output}.");
return 0;
