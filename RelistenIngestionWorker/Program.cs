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

var operations = new[] { "import", "batch", "cancel", "rebuild-artist", "repair-jerry-venues" }
    .Where(key => builder.Configuration[key] != null)
    .ToArray();
var operationCount = operations.Length + (migrate ? 1 : 0) + (registerSchedules ? 1 : 0);
if (operationCount > 1)
{
    Console.Error.WriteLine(
        "Choose one import, batch, cancel, rebuild, repair, migration, or schedule operation."
    );
    Environment.ExitCode = 1;
    return;
}
if (
    builder.Configuration["import"] == null
    && new[] { "provider", "item", "year" }.Any(key => builder.Configuration[key] != null)
)
{
    Console.Error.WriteLine("Provider, item, and year options require --import.");
    Environment.ExitCode = 1;
    return;
}

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

    migrator.MigrateTo(migrator.LatestMigration.Version);
    return;
}

if (registerSchedules)
{
    await host.Services.GetRequiredService<TemporalIngestionClient>().RegisterSchedulesAsync();
    return;
}

// Operator commands use the same admission rules as the HTTP adapters.
try
{
    if (operations.Length == 0)
    {
        await host.RunAsync();
        return;
    }

    var imports = host.Services.GetRequiredService<TemporalIngestionClient>();
    if (builder.Configuration["cancel"] is { } workflowId)
    {
        await host
            .Services.GetRequiredService<ITemporalClient>()
            .GetWorkflowHandle(workflowId)
            .CancelAsync();
        Console.WriteLine(
            $"Cancellation requested for the currently active execution of {workflowId}"
        );
        return;
    }
    if (builder.Configuration["batch"] is { } mode)
    {
        var id = mode switch
        {
            "full" => (await imports.RefreshCatalogAsync(new RefreshCatalog())).WorkflowId,
            "thin" => (
                await imports.RefreshRecentlyActiveArtistsAsync(new RefreshRecentlyActiveArtists())
            ).WorkflowId,
            "rebuild" => (await imports.RebuildCatalogAsync(new RebuildCatalog())).WorkflowId,
            "discover" => (
                await imports.DiscoverArchiveArtistsAsync(new DiscoverArchiveArtists())
            ).WorkflowId,
            _ => throw new InvalidImportRequest("Expected batch full, thin, rebuild, or discover."),
        };
        Console.WriteLine(id);
        return;
    }

    using var scope = host.Services.CreateScope();
    var identifier = builder.Configuration[operations[0]]!;
    var artist =
        await scope
            .ServiceProvider.GetRequiredService<ArtistService>()
            .FindArtistWithIdOrSlug(identifier)
        ?? throw new InvalidImportRequest("Artist was not found.");
    var artistId = new ArtistId(artist.uuid);
    if (operations[0] == "rebuild-artist")
    {
        Console.WriteLine(
            (await imports.RebuildArtistCatalogAsync(new RebuildArtistCatalog(artistId))).WorkflowId
        );
        return;
    }
    if (operations[0] == "repair-jerry-venues")
    {
        Console.WriteLine(
            (await imports.Jerry.RepairVenuesAsync(new RepairJerryVenues(artistId))).WorkflowId
        );
        return;
    }

    UpstreamProvider? provider = null;
    if (builder.Configuration["provider"] is { } providerName)
    {
        try
        {
            provider = UpstreamProviders.Parse(providerName);
        }
        catch (ArgumentException error)
        {
            throw new InvalidImportRequest(error.Message);
        }
    }
    int? year = null;
    if (builder.Configuration["year"] is { } yearText)
    {
        if (!int.TryParse(yearText, out var value) || value is < 1 or > 9999)
            throw new InvalidImportRequest("Year must be between 1 and 9999.");
        year = value;
    }
    if (builder.Configuration["item"] is { } item)
    {
        if (year != null)
            throw new InvalidImportRequest("A targeted item cannot also specify a year.");
        if (provider is not (null or UpstreamProvider.ArchiveOrg))
            throw new UnsupportedImportOperation("Only Archive supports targeted item imports.");
        Console.WriteLine(
            (
                await imports.Archive.ImportItemAsync(
                    new ImportArchiveItem(artistId, new ArchiveItemId(item))
                )
            ).WorkflowId
        );
        return;
    }
    if (
        year != null
        && provider
            is not (
                null
                or UpstreamProvider.ArchiveOrg
                or UpstreamProvider.Phishin
                or UpstreamProvider.PhishNet
            )
    )
        throw new InvalidImportRequest("This provider does not support a year filter.");
    ArtistRefreshCommand request = provider switch
    {
        null => year.HasValue
            ? new RefreshFilteredArtist(artistId, year.Value)
            : new RefreshArtist(artistId),
        UpstreamProvider.ArchiveOrg => new RefreshArchiveArtist(artistId, year),
        UpstreamProvider.Phishin => new RefreshPhishInArtist(artistId, year),
        UpstreamProvider.PhishNet => new RefreshPhishNetArtist(artistId, year),
        UpstreamProvider.SetlistFm => new RefreshSetlistFmArtist(artistId),
        UpstreamProvider.PhantasyTour => new RefreshPhantasyTourArtist(artistId),
        UpstreamProvider.JerryGarcia => new RefreshJerryArtist(artistId),
        UpstreamProvider.Local => new RefreshLocalArtist(artistId),
        _ => throw new UnsupportedImportOperation(
            "This provider has no active ingestion implementation."
        ),
    };
    Console.WriteLine((await imports.RefreshAsync(request)).WorkflowId);
}
catch (Exception error)
    when (error
            is ImportAlreadyRunning
                or InvalidImportRequest
                or UnsupportedImportOperation
                or ArtistNotFound
    )
{
    Console.Error.WriteLine(
        error is ImportAlreadyRunning conflict
            ? $"{conflict.Message} Active workflow: {conflict.WorkflowId}"
            : error.Message
    );
    Environment.ExitCode = 1;
}
