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
catch (FormatException) when (args.Any(IsNormalizationArgument))
{
    Console.Error.WriteLine("Invalid normalization command options. Each option requires a value.");
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
    "normalize-repertoire",
    "normalize-setlists",
    "normalize-recording-contents",
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

    if (IsNormalizationOperation(operations[0]))
    {
        Environment.ExitCode = await RunNormalizationCommandAsync(
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
    RequireNormalizationOptionValues(arguments);
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
    builder.Services.AddScoped<NormalizationRepertoireCommand>();
    builder.Services.AddScoped<NormalizationMusicMaterializationCommand>();
    if (
        new[]
        {
            "normalize-recordings",
            "normalize-repertoire",
            "normalize-setlists",
            "normalize-recording-contents",
        }.Any(operation => builder.Configuration[operation] != null)
    )
    {
        // Operator receipts use stdout as a JSON stream; diagnostics remain on stderr.
        builder.Services.Configure<ConsoleLoggerOptions>(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace
        );
    }

    return builder;
}

static void RequireNormalizationOptionValues(string[] arguments)
{
    if (!arguments.Any(IsNormalizationArgument))
    {
        return;
    }

    // The configuration provider silently ignores a trailing key without a value. Reject
    // incomplete normalization selectors before resolving services or opening request files.
    var options = new[]
    {
        "--normalize-recordings",
        "--normalize-repertoire",
        "--normalize-setlists",
        "--normalize-recording-contents",
        "--repertoire-attempt",
        "--artist",
        "--after",
        "--limit",
        "--attempt",
        "--request",
        "--registration",
    };
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
            throw new FormatException("Normalization command options require values.");
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

    if (configuration["normalize-recordings"] == null && configuration["artist"] != null)
    {
        Console.Error.WriteLine(
            "Artist selection requires --normalize-recordings. Use the repertoire source request's artist constraint for resolution."
        );
        return false;
    }

    if (
        configuration["normalize-recordings"] == null
        && configuration["normalize-repertoire"] == null
        && configuration["normalize-setlists"] == null
        && configuration["normalize-recording-contents"] == null
        && new[] { "after", "limit" }.Any(key => configuration[key] != null)
    )
    {
        Console.Error.WriteLine("After and limit options require a normalization command.");
        return false;
    }

    if (
        configuration["normalize-repertoire"] == null
        && configuration["normalize-setlists"] == null
        && configuration["normalize-recording-contents"] == null
        && new[] { "attempt", "registration" }.Any(key => configuration[key] != null)
    )
    {
        Console.Error.WriteLine(
            "Attempt and registration options require repertoire resolution or music materialization."
        );
        return false;
    }

    if (configuration["request"] != null && configuration["normalize-repertoire"] == null)
    {
        Console.Error.WriteLine("Request files require --normalize-repertoire.");
        return false;
    }

    if (
        configuration["repertoire-attempt"] != null
        && configuration["normalize-setlists"] == null
        && configuration["normalize-recording-contents"] == null
    )
    {
        Console.Error.WriteLine(
            "Completed repertoire attempt selection requires a music materialization command."
        );
        return false;
    }

    return true;
}

static bool IsNormalizationOperation(string operation) =>
    operation
        is "normalize-recordings"
            or "normalize-repertoire"
            or "normalize-setlists"
            or "normalize-recording-contents";

static bool IsNormalizationArgument(string argument) =>
    argument.StartsWith("--", StringComparison.Ordinal)
    && IsNormalizationOperation(argument[2..].Split('=', 2)[0]);

static async Task<int> RunNormalizationCommandAsync(
    IServiceProvider services,
    IConfiguration configuration
)
{
    var repertoire = configuration["normalize-repertoire"] != null;
    var materialization =
        configuration["normalize-setlists"] != null
        || configuration["normalize-recording-contents"] != null;
    using var cancellation = new CancellationTokenSource();
    ConsoleCancelEventHandler cancel = (_, args) =>
    {
        args.Cancel = true;
        cancellation.Cancel();
    };
    Console.CancelKeyPress += cancel;
    try
    {
        // Parse selectors before resolving catalog services or admitting any durable attempt.
        if (materialization)
        {
            var request = NormalizationMusicMaterializationCommand.Parse(configuration);
            using var scope = services.CreateScope();
            return await scope
                .ServiceProvider.GetRequiredService<NormalizationMusicMaterializationCommand>()
                .RunAsync(request, Console.Out, cancellation.Token);
        }
        if (repertoire)
        {
            var request = NormalizationRepertoireCommand.Parse(configuration);
            using var scope = services.CreateScope();
            return await scope
                .ServiceProvider.GetRequiredService<NormalizationRepertoireCommand>()
                .RunAsync(request, Console.Out, cancellation.Token);
        }
        var recordingRequest = NormalizationRecordingCommand.Parse(configuration);
        using var recordingScope = services.CreateScope();
        return await recordingScope
            .ServiceProvider.GetRequiredService<NormalizationRecordingCommand>()
            .RunAsync(recordingRequest, Console.Out, cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine(
            materialization
                ? "Music materialization canceled. Resume the same saved operation and attempt; inspect retirement and publication receipts before retrying."
            : repertoire
                ? "Repertoire processing canceled. Resume the same attempt and source request; inspect its retained receipt before creating a retry."
            : "Recording processing canceled. Retry the same page UUID and selectors to resume."
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
            materialization
                ? "Music materialization failed. Inspect the retained attempt, publication and retirement receipts before retrying."
            : repertoire
                ? "Repertoire processing failed. Inspect the retained attempt and stages before retrying."
            : "Recording processing failed. Inspect the retained page, attempts and failure receipts before retrying."
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
