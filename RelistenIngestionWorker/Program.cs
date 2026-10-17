using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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
var registerIntakeSchedules = args.Contains("--register-taperssection-schedules");
var migrate = args.Contains("--migrate");
HostApplicationBuilder builder;
try
{
    builder = CreateWorkerBuilder(args);
}
catch (FormatException)
    when (args.Any(argument =>
            argument == "--normalize-recordings"
            || argument.StartsWith("--normalize-recordings=", StringComparison.Ordinal)
        )
    )
{
    Console.Error.WriteLine("Invalid recording command options. Each option requires a value.");
    Environment.ExitCode = 1;
    return;
}
var sentryOptions = WorkerLogging.Configure(builder);
using var sentry = sentryOptions == null ? null : SentrySdk.Init(sentryOptions);
using var host = builder.Build();

var operations = new[]
{
    "import",
    "batch",
    "cancel",
    "rebuild-artist",
    "repair-jerry-venues",
    "taperssection-discover",
    "taperssection-refresh",
    "taperssection-artist",
    "taperssection-status",
    "collection-discover",
    "normalize-recordings",
}
    .Where(key => builder.Configuration[key] != null)
    .ToArray();

if (
    !ValidateOperationSelection(
        builder.Configuration,
        operations,
        migrate,
        registerSchedules,
        registerIntakeSchedules
    )
)
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

if (registerIntakeSchedules)
{
    await host
        .Services.GetRequiredService<TemporalIngestionClient>()
        .RegisterTapersSectionSchedulesAsync();
    return;
}

try
{
    if (operations.Length == 0)
    {
        await host.RunAsync();
        return;
    }

    if (operations[0] == "normalize-recordings")
    {
        Environment.ExitCode = await RunRecordingNormalizationAsync(
            host.Services,
            builder.Configuration
        );
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
    RequireRecordingOptionValues(arguments);
    var switches = new[]
    {
        "--register-schedules",
        "--register-taperssection-schedules",
        "--migrate",
        "--taperssection-discover",
        "--taperssection-refresh",
        "--taperssection-status",
    };
    var builder = Host.CreateApplicationBuilder(
        arguments.Where(argument => !switches.Contains(argument)).ToArray()
    );
    foreach (
        var operation in new[]
        {
            "taperssection-discover",
            "taperssection-refresh",
            "taperssection-status",
        }
    )
    {
        if (arguments.Contains("--" + operation))
        {
            builder.Configuration[operation] = "true";
        }
    }
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
    builder.Services.AddScoped<NormalizationRecordingCommand>();
    if (builder.Configuration["normalize-recordings"] != null)
    {
        // Operator receipts use stdout as a JSON stream; diagnostics remain on stderr.
        builder.Services.Configure<ConsoleLoggerOptions>(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace
        );
    }

    return builder;
}

static void RequireRecordingOptionValues(string[] arguments)
{
    if (
        !arguments.Any(argument =>
            argument == "--normalize-recordings"
            || argument.StartsWith("--normalize-recordings=", StringComparison.Ordinal)
        )
    )
    {
        return;
    }

    // The configuration provider silently ignores a trailing key without a value. A recording
    // command must reject that request before it can resolve services or select recordings.
    var options = new[] { "--normalize-recordings", "--artist", "--after", "--limit" };
    for (var index = 0; index < arguments.Length; index++)
    {
        if (
            options.Contains(arguments[index])
            && (
                index + 1 == arguments.Length
                || arguments[index + 1].StartsWith("--", StringComparison.Ordinal)
            )
        )
        {
            throw new FormatException("Recording command options require values.");
        }
    }
}

static bool ValidateOperationSelection(
    IConfiguration configuration,
    string[] operations,
    bool migrate,
    bool registerSchedules,
    bool registerIntakeSchedules
)
{
    var operationCount =
        operations.Length
        + (migrate ? 1 : 0)
        + (registerSchedules ? 1 : 0)
        + (registerIntakeSchedules ? 1 : 0);
    if (operationCount > 1)
    {
        Console.Error.WriteLine("Choose one worker operation.");
        return false;
    }

    if (
        configuration["import"] == null
        && (
            new[] { "provider", "year" }.Any(key => configuration[key] != null)
            || configuration["item"] != null && configuration["taperssection-status"] == null
        )
    )
    {
        Console.Error.WriteLine(
            "Provider and year options require --import. Item requires --import or --taperssection-status."
        );
        return false;
    }

    if (configuration["max-items"] != null && configuration["taperssection-discover"] == null)
    {
        Console.Error.WriteLine("Maximum items requires --taperssection-discover.");
        return false;
    }

    if (configuration["max-pages"] != null && configuration["collection-discover"] == null)
    {
        Console.Error.WriteLine("Maximum pages requires --collection-discover.");
        return false;
    }

    if (
        configuration["normalize-recordings"] == null
        && new[] { "artist", "after", "limit" }.Any(key => configuration[key] != null)
    )
    {
        Console.Error.WriteLine(
            "Artist, after-source and limit options require --normalize-recordings."
        );
        return false;
    }

    return true;
}

static async Task<int> RunRecordingNormalizationAsync(
    IServiceProvider services,
    IConfiguration configuration
)
{
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, args) =>
    {
        args.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        var request = NormalizationRecordingCommand.Parse(configuration);
        using var scope = services.CreateScope();
        return await scope
            .ServiceProvider.GetRequiredService<NormalizationRecordingCommand>()
            .RunAsync(request, Console.Out, cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine(
            "Recording processing canceled. Retry the same page UUID and selectors to resume."
        );
        return 130;
    }
    catch (Exception error)
        when (error
                is ArgumentException
                    or Relisten.Api.Models.Normalization.NormalizationConflictException
        )
    {
        Console.Error.WriteLine(error.Message);
        return 1;
    }
    catch (Exception error)
    {
        SentrySdk.CaptureException(error);
        Console.Error.WriteLine(
            "Recording processing failed. Inspect the retained page, attempts and failure receipts before retrying."
        );
        return 1;
    }
    finally
    {
        Console.CancelKeyPress -= cancel;
    }
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
