using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;
using Temporalio.Client;

// This host deliberately does not construct Startup: it owns no HTTP server,
// authentication middleware, Hangfire server, or automatic catalog migrations.
var registerSchedules = args.Contains("--register-schedules");
var migrate = args.Contains("--migrate");
var builder = Host.CreateApplicationBuilder(
    args.Where(a => a != "--register-schedules" && a != "--migrate").ToArray()
);
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);
SqlMapper.AddTypeHandler(new PersistentIdentifierHandler());
SqlMapper.AddTypeHandler(new DateTimeHandler());
JsonConvert.DefaultSettings = () =>
    new JsonSerializerSettings { DateTimeZoneHandling = DateTimeZoneHandling.Utc };
builder.Services.AddSingleton(sp => new DbService(
    builder.Configuration["DATABASE_URL"]
        ?? throw new InvalidOperationException("DATABASE_URL is required"),
    builder.Configuration["PGBOUNCER_WRITE_HOST"],
    builder.Configuration["PGBOUNCER_READ_ONLY_HOST"],
    builder.Environment,
    sp.GetRequiredService<ILogger<DbService>>()
));
builder.Services.AddSingleton(_ => new RedisService(
    RedisService.BuildConfiguration(
        builder.Configuration["REDIS_URL"]
            ?? throw new InvalidOperationException("REDIS_URL is required")
    )
));
builder.Services.AddRelistenCatalogServices();
builder.Services.AddTemporalIngestionWorkers(builder.Configuration);
using var host = builder.Build();

if (migrate)
{
    using var connection = host
        .Services.GetRequiredService<DbService>()
        .CreateConnection(true, false);
    var migrator = new SimpleMigrator(
        typeof(Startup).Assembly,
        new PostgresqlDatabaseProvider(connection)
    );
    migrator.Load();

    // The local schema fixture already contains the catalog through migration 12.
    // Existing installations retain their recorded migration version.
    if (migrator.CurrentMigration == null || migrator.CurrentMigration.Version == 0)
    {
        if (builder.Configuration["Ingestion:LocalSchemaBaseline"] != "12")
        {
            throw new InvalidOperationException(
                "No migration baseline. Use the documented local schema setup or restore the existing migration history."
            );
        }

        migrator.Baseline(12);
    }

    migrator.MigrateTo(13);
    return;
}

if (registerSchedules)
{
    await host.Services.GetRequiredService<TemporalIngestionClient>().RegisterSchedulesAsync();
    return;
}

// Operator commands share the API client and accept the host's normal --key=value configuration.
if (builder.Configuration["cancel"] is { } workflowId)
{
    await host
        .Services.GetRequiredService<ITemporalClient>()
        .GetWorkflowHandle(workflowId)
        .CancelAsync();
    Console.WriteLine($"Cancellation requested for {workflowId}");
    return;
}

if (builder.Configuration["import"] is { } artistIdentifier)
{
    using var scope = host.Services.CreateScope();
    var artist =
        await scope
            .ServiceProvider.GetRequiredService<ArtistService>()
            .FindArtistWithIdOrSlug(artistIdentifier)
        ?? throw new InvalidOperationException("Artist not found.");
    var request = new ArtistImportRequest(
        artist.uuid.ToString(),
        Guid.NewGuid().ToString(),
        builder.Configuration["provider"] is { } provider
            ? UpstreamProviders.Parse(provider)
            : null,
        builder.Configuration["item"],
        builder.Configuration.GetValue<int?>("year")
    );
    Console.WriteLine(
        await host.Services.GetRequiredService<TemporalIngestionClient>().StartArtistAsync(request)
    );
    return;
}

if (builder.Configuration["batch"] is { } mode)
{
    Console.WriteLine(
        await host.Services.GetRequiredService<TemporalIngestionClient>().StartBatchAsync(mode)
    );
    return;
}

await host.RunAsync();
