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
using RelistenIngestionWorker;
using Sentry;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;

// This host owns workers and operator commands. It deliberately does not construct
// Startup's HTTP server, authentication middleware, Hangfire server, or migrations.
var registerSchedules = args.Contains("--register-schedules");
var migrate = args.Contains("--migrate");
var builder = CreateWorkerBuilder(args);
var sentryOptions = WorkerLogging.Configure(builder);
using var sentry = sentryOptions == null ? null : SentrySdk.Init(sentryOptions);
using var host = builder.Build();

var operations = new[] { "import", "batch", "cancel", "rebuild-artist", "repair-jerry-venues" }
    .Where(key => builder.Configuration[key] != null)
    .ToArray();

if (!ValidateOperationSelection(builder.Configuration, operations, migrate, registerSchedules))
{
    Environment.ExitCode = 1;
    return;
}

// Schema changes and schedule registration are explicit one-shot operations.
if (migrate)
{
    MigrateCatalog(host.Services.GetRequiredService<DbService>(), builder.Configuration);
    return;
}

if (registerSchedules)
{
    await host.Services.GetRequiredService<TemporalIngestionClient>().RegisterSchedulesAsync();
    return;
}

try
{
    if (operations.Length == 0)
    {
        await host.RunAsync();
        return;
    }

    using var scope = host.Services.CreateScope();
    var commands = scope.ServiceProvider.GetRequiredService<IngestionOperatorCommands>();
    Console.WriteLine(await commands.RunAsync(operations[0]));
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

static HostApplicationBuilder CreateWorkerBuilder(string[] arguments)
{
    var builder = Host.CreateApplicationBuilder(
        arguments
            .Where(argument => argument != "--register-schedules" && argument != "--migrate")
            .ToArray()
    );
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
    builder.Services.AddScoped<IngestionOperatorCommands>();

    return builder;
}

static bool ValidateOperationSelection(
    IConfiguration configuration,
    string[] operations,
    bool migrate,
    bool registerSchedules
)
{
    var operationCount = operations.Length + (migrate ? 1 : 0) + (registerSchedules ? 1 : 0);
    if (operationCount > 1)
    {
        Console.Error.WriteLine(
            "Choose one import, batch, cancel, rebuild, repair, migration, or schedule operation."
        );
        return false;
    }

    if (
        configuration["import"] == null
        && new[] { "provider", "item", "year" }.Any(key => configuration[key] != null)
    )
    {
        Console.Error.WriteLine("Provider, item, and year options require --import.");
        return false;
    }

    return true;
}

static void MigrateCatalog(DbService database, IConfiguration configuration)
{
    using var connection = database.CreateConnection(true, false);
    var migrator = new SimpleMigrator(
        typeof(Startup).Assembly,
        new PostgresqlDatabaseProvider(connection)
    );
    migrator.Load();

    // The local schema fixture already contains the catalog through migration 12.
    // Existing installations retain their recorded migration version.
    if (migrator.CurrentMigration == null || migrator.CurrentMigration.Version == 0)
    {
        if (configuration["Ingestion:LocalSchemaBaseline"] != "12")
        {
            throw new InvalidOperationException(
                "No migration baseline. Use the documented local schema setup or restore the existing migration history."
            );
        }

        migrator.Baseline(12);
    }

    migrator.MigrateTo(migrator.LatestMigration.Version);
}
