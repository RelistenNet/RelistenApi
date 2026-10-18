using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

if (
    !(
        (args.Length == 3 && args[0] is "extract" or "venue-pair-baseline")
        || (args.Length == 5 && args[0] == "luna-credits")
        || (args.Length == 4 && args[0] == "musicbrainz-acquire")
        || (args.Length == 6 && args[0] == "luna-credit-fields")
    )
)
{
    Console.Error.WriteLine(
        "Usage: RelistenNormalization extract <snapshot.jsonl> <new-output-directory>\n"
            + "       RelistenNormalization venue-pair-baseline <bundle-directory> <new-output-directory>\n"
            + "       RelistenNormalization musicbrainz-acquire <request.json> <new-output-directory> <command-uuid>\n"
            + "       RelistenNormalization luna-credits <extractions.jsonl> <new-output-directory> <job-uuid> <records-per-cohort>\n"
            + "       RelistenNormalization luna-credit-fields <labels.jsonl> <fields.jsonl> <receipt.json> <new-output-directory> <job-uuid>"
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
    if (args[0] == "musicbrainz-acquire")
    {
        if (!Guid.TryParse(args[3], out var commandUuid) || commandUuid == Guid.Empty)
        {
            throw new ArgumentException("Use a nonempty acquisition command UUID.");
        }
        var db = NormalizationLocalDatabase.Create();
        var evidence = new NormalizationEvidenceService(db);
        var store = new NormalizationMusicBrainzInputStore(db);
        using var http = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                MaxResponseHeadersLength = 64,
            }
        );
        var service = new NormalizationMusicBrainzInputService(
            http,
            store,
            evidence,
            new MusicBrainzAcquisitionOptions
            {
                UserAgent = "RelistenNormalization.Operator/1.0 (https://relisten.net)",
            }
        );
        var acquisition = await new MusicBrainzAcquisitionBatch(service, store, evidence).RunAsync(
            args[1],
            args[2],
            commandUuid,
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                acquisition,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return acquisition.State == "completed" ? 0 : 1;
    }
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
    if (args[0] is "luna-credits" or "luna-credit-fields")
    {
        var fieldsMode = args[0] == "luna-credit-fields";
        if (!Guid.TryParse(args[fieldsMode ? 5 : 3], out var jobUuid))
        {
            throw new ArgumentException("Use a valid bounded-job UUID.");
        }
        var db = NormalizationLocalDatabase.Create();
        var cache = new NormalizationExtractionCacheService(db);
        var options = new LunaCreditExtractionOptions();
        using var http = new HttpClient();
        if (fieldsMode)
        {
            var fieldsReport = await new LunaCreditFieldEvaluationBatch(
                http,
                cache,
                db,
                options,
                () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""
            ).RunAsync(args[1], args[2], args[3], args[4], jobUuid, cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(fieldsReport));
            return fieldsReport.State == "completed" ? 0 : 1;
        }
        if (!int.TryParse(args[4], out var perCohort))
        {
            throw new ArgumentException("Use an integer records per cohort.");
        }
        var extractor = new LunaRecordingCreditExtractor(
            http,
            cache,
            options,
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
                or NormalizationConflictException
                or OperationCanceledException
    )
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
