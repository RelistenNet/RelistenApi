using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Relisten;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

if (
    !(
        (args.Length == 3 && args[0] is "extract" or "venue-pair-baseline")
        || (args.Length == 5 && args[0] == "luna-credits")
    )
)
{
    Console.Error.WriteLine(
        "Usage: RelistenNormalization extract <snapshot.jsonl> <new-output-directory>\n"
            + "       RelistenNormalization venue-pair-baseline <bundle-directory> <new-output-directory>\n"
            + "       RelistenNormalization luna-credits <extractions.jsonl> <new-output-directory> <job-uuid> <records-per-cohort>"
    );
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) =>
{
    signal.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (args[0] == "venue-pair-baseline")
    {
        var baseline = await new VenuePairBaselineBatch().RunAsync(
            args[1],
            args[2],
            cancellation.Token
        );
        Console.WriteLine(JsonSerializer.Serialize(baseline));
        return 0;
    }
    if (args[0] == "luna-credits")
    {
        if (!Guid.TryParse(args[3], out var jobUuid) || !int.TryParse(args[4], out var perCohort))
        {
            throw new ArgumentException("Use a job UUID and integer records per cohort.");
        }
        var databaseUrl = Environment.GetEnvironmentVariable("RELISTEN_NORMALIZATION_DATABASE_URL");
        if (
            !Uri.TryCreate(databaseUrl, UriKind.Absolute, out var database)
            || database.Scheme != "postgresql"
            || database.Host is not ("127.0.0.1" or "localhost")
            || !database.AbsolutePath.StartsWith(
                "/relisten_normalization_",
                StringComparison.Ordinal
            )
        )
        {
            throw new ArgumentException(
                "Set RELISTEN_NORMALIZATION_DATABASE_URL to a dedicated local relisten_normalization_* database with migrations applied."
            );
        }
        var db = new DbService(
            databaseUrl!,
            null,
            null,
            Host.CreateApplicationBuilder(
                new HostApplicationBuilderSettings { EnvironmentName = Environments.Production }
            ).Environment,
            NullLogger<DbService>.Instance
        );
        var cache = new NormalizationExtractionCacheService(db);
        using var http = new HttpClient();
        var extractor = new LunaRecordingCreditExtractor(
            http,
            cache,
            new LunaCreditExtractionOptions(),
            () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""
        );
        var result = await new LunaCreditEvaluationBatch(extractor, cache).RunAsync(
            args[1],
            args[2],
            jobUuid,
            perCohort,
            cancellation.Token
        );
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.State == "completed" ? 0 : 1;
    }

    var report = await new NormalizationExtractionBatch().RunAsync(
        args[1],
        args[2],
        cancellation.Token
    );
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                report.State,
                report.InputSha256,
                report.OutputRecords,
                report.ErrorCount,
                outputDirectory = Path.GetFullPath(args[2]),
            }
        )
    );
    return report.State == "completed" ? 0 : 1;
}
catch (Exception exception)
    when (exception
            is IOException
                or InvalidDataException
                or UnauthorizedAccessException
                or ArgumentException
    )
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
