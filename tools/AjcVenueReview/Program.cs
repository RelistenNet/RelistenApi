using Dapper;
using Npgsql;
using Relisten.Services.Collections;

const string DefaultOutputPath = "tools/collection-analysis/review/aadamjacobs_missing_venues.csv";
const string DefaultCollectionSlug = "aadam-jacobs";

var options = ParseOptions(args);
if (options.ShowHelp)
{
    Console.WriteLine("""
    Usage:
      dotnet run --project tools/AjcVenueReview/AjcVenueReview.csproj -- [--output <path>] [--connection-string <connection-string>]

    Defaults:
      output: tools/collection-analysis/review/aadamjacobs_missing_venues.csv
      database: local dev Postgres at 127.0.0.1:15432/relisten_db

    Environment overrides:
      AJC_VENUE_REVIEW_CONNECTION_STRING or RELISTEN_DB_CONNECTION_STRING
      RELISTEN_DB_HOST, RELISTEN_DB_PORT, RELISTEN_DB_USER, RELISTEN_DB_PASSWORD, RELISTEN_DB_NAME
    """);
    return 0;
}

var connectionString = options.ConnectionString ?? BuildConnectionStringFromEnvironment();
await using var connection = new NpgsqlConnection(connectionString);
var sourceRows = (await connection.QueryAsync<AjcVenueReviewSourceRow>(
    """
    SELECT
        ci.upstream_identifier AS archive_identifier,
        ci.title,
        COALESCE(a.name, ci.creator_raw, '') AS creator,
        COALESCE(s.display_date, ci.display_date, '') AS display_date,
        COALESCE(v.name, '') AS current_venue,
        COALESCE(v.location, '') AS current_coverage,
        COALESCE(s.description, '') AS description
    FROM collection_items ci
    JOIN collections c ON c.uuid = ci.collection_uuid
    JOIN sources s ON s.uuid = ci.source_uuid
    LEFT JOIN venues v ON v.id = s.venue_id
    LEFT JOIN artists a ON a.uuid = ci.artist_uuid
    WHERE c.slug = @CollectionSlug
      AND ci.removed_at IS NULL
      AND (
        v.id IS NULL
        OR v.name = 'Unknown Venue'
        OR v.location = 'Unknown Location'
      )
    ORDER BY ci.upstream_identifier
    """,
    new { CollectionSlug = DefaultCollectionSlug })).ToList();

var csvRows = sourceRows.Select(AjcVenueReviewCsv.BuildRow).ToList();
var csv = AjcVenueReviewCsv.ToCsv(csvRows);

var outputPath = Path.GetFullPath(options.OutputPath);
var outputDirectory = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrWhiteSpace(outputDirectory))
{
    Directory.CreateDirectory(outputDirectory);
}

await File.WriteAllTextAsync(outputPath, csv);
Console.WriteLine($"Wrote {csvRows.Count} rows to {outputPath}");

return 0;

static ToolOptions ParseOptions(string[] args)
{
    var options = new ToolOptions
    {
        OutputPath = DefaultOutputPath
    };

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--help":
            case "-h":
                options.ShowHelp = true;
                break;
            case "--output":
                options.OutputPath = RequiredValue(args, ref i, "--output");
                break;
            case "--connection-string":
                options.ConnectionString = RequiredValue(args, ref i, "--connection-string");
                break;
            default:
                throw new ArgumentException($"Unknown argument: {args[i]}");
        }
    }

    return options;
}

static string RequiredValue(string[] args, ref int index, string optionName)
{
    if (index + 1 >= args.Length)
    {
        throw new ArgumentException($"{optionName} requires a value.");
    }

    index++;
    return args[index];
}

static string BuildConnectionStringFromEnvironment()
{
    var explicitConnectionString = Environment.GetEnvironmentVariable("AJC_VENUE_REVIEW_CONNECTION_STRING")
                                   ?? Environment.GetEnvironmentVariable("RELISTEN_DB_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(explicitConnectionString))
    {
        return explicitConnectionString;
    }

    return new NpgsqlConnectionStringBuilder
    {
        Host = Environment.GetEnvironmentVariable("RELISTEN_DB_HOST") ?? "127.0.0.1",
        Port = int.TryParse(Environment.GetEnvironmentVariable("RELISTEN_DB_PORT"), out var port) ? port : 15432,
        Username = Environment.GetEnvironmentVariable("RELISTEN_DB_USER") ?? "relisten",
        Password = Environment.GetEnvironmentVariable("RELISTEN_DB_PASSWORD") ?? "local_dev_password",
        Database = Environment.GetEnvironmentVariable("RELISTEN_DB_NAME") ?? "relisten_db",
        Timeout = 30
    }.ConnectionString;
}

internal sealed class ToolOptions
{
    public string OutputPath { get; set; } = "";
    public string? ConnectionString { get; set; }
    public bool ShowHelp { get; set; }
}
