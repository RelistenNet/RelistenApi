using System.Text.Json;
using Relisten.Normalization.Batch;

if (args.Length != 3 || args[0] != "extract")
{
    Console.Error.WriteLine(
        "Usage: RelistenNormalization extract <snapshot.jsonl> <new-output-directory>"
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
    when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
