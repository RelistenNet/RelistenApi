using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Relisten;
using Relisten.Accounts.Contracts.Deletion;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Api.Models;
using Relisten.Data;
using Relisten.Ingestion;
using Relisten.Persistence;
using RelistenIngestionWorker;
using RelistenIngestionWorker.AccountDeletion;
using Sentry;
using SimpleMigrations;
using SimpleMigrations.DatabaseProvider;
using Temporalio.Extensions.Hosting;

// This host owns workers and operator commands. It deliberately does not construct
// Startup's HTTP server, authentication middleware, Hangfire server, or migrations.
var purgeAccount = args.FirstOrDefault() == "purge-account";
UserId purgeUser = default;
if (purgeAccount && (args is not [_, "--user", var user] || !UserId.TryParse(user, out purgeUser)))
{
    Console.Error.WriteLine("Usage: purge-account --user <user_id>");
    Environment.ExitCode = 1;
    return;
}

var registerSchedules = args.Contains("--register-schedules");
var migrate = args.Contains("--migrate");
var builder = CreateWorkerBuilder(purgeAccount ? [] : args);
var sentryOptions = WorkerLogging.Configure(builder);
using var sentry = sentryOptions == null ? null : SentrySdk.Init(sentryOptions);
using var host = builder.Build();

if (purgeAccount)
{
    var purge =
        host.Services.GetService<AccountPurge>()
        ?? throw new InvalidOperationException(
            "ACCOUNTS_DATABASE_URL is required for purge-account."
        );
    await purge.RunForUserAsync(purgeUser.ToString(), CancellationToken.None);
    Console.WriteLine("Account purge completed.");
    return;
}

var operations = new[]
{
    "import",
    "batch",
    "cancel",
    "rebuild-artist",
    "norm-songs",
    "norm-venues",
    "repair-jerry-venues",
    "search-build",
}
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
    await host
        .Services.GetRequiredService<Relisten.Normalization.Temporal.NormalizationClient>()
        .RegisterSchedulesAsync(
            builder.Configuration.GetValue("Normalization:NightlyVenueCalls", 60)
        );
    return;
}

try
{
    if (operations.Length == 0)
    {
        if (host.Services.GetService<AccountPurge>() is null)
        {
            // The User Service still accepts deletion requests; they wait in Temporal
            // until a worker with the accounts database polls this queue.
            host.Services.GetRequiredService<ILogger<AccountDeletionWorkflow>>()
                .LogWarning(
                    "ACCOUNTS_DATABASE_URL is not set; not polling the {TaskQueue} task queue. Account deletions will not run.",
                    AccountDeletionInput.TaskQueue
                );
        }

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
                or Relisten.Normalization.Temporal.NormalizationAlreadyRunning
                or InvalidImportRequest
                or UnsupportedImportOperation
                or ArtistNotFound
    )
{
    Console.Error.WriteLine(
        error switch
        {
            ImportAlreadyRunning conflict =>
                $"{conflict.Message} Active workflow: {conflict.WorkflowId}",
            Relisten.Normalization.Temporal.NormalizationAlreadyRunning running => running.Message,
            _ => error.Message,
        }
    );
    Environment.ExitCode = 1;
}

static HostApplicationBuilder CreateWorkerBuilder(string[] arguments)
{
    var builder = Host.CreateApplicationBuilder(
        arguments
            .Where(argument => argument != "--register-schedules" && argument != "--migrate")
            // A bare flag has no value for configuration: --rematch reads as --rematch=true.
            .Select(argument => argument == "--rematch" ? "--rematch=true" : argument)
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
    if (builder.Configuration["ACCOUNTS_DATABASE_URL"] is { Length: > 0 } accountsUrl)
    {
        builder.Services.AddSingleton(_ => new AccountPurge(
            PostgresConnectionString.Normalize(accountsUrl)
        ));
        builder
            .Services.AddHostedTemporalWorker(AccountDeletionInput.TaskQueue)
            .AddWorkflow<AccountDeletionWorkflow>()
            .AddScopedActivities<AccountDeletionActivities>();
    }

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
            "Choose one import, batch, cancel, rebuild, repair, search build, migration, or schedule operation."
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

    if (configuration["search-build"] == null && configuration["search-plays-export"] != null)
    {
        Console.Error.WriteLine("--search-plays-export requires --search-build.");
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
