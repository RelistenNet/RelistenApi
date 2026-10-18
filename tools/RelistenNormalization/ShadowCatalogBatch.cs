using System.Security.Cryptography;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs domain adapters over every retained catalog input. This entrypoint has no
/// application host, canonical writer, database connection or external client.
/// Each input/domain gets one outcome even when a sibling adapter fails.
/// </summary>
public sealed class ShadowCatalogBatch(IReadOnlyList<IShadowCatalogAdapter> adapters)
{
    public async Task RunAsync(
        string snapshotPath,
        string extractionsPath,
        string outputDirectory,
        bool resume = false,
        string? providerInputsPath = null,
        CancellationToken ct = default
    )
    {
        var inputHash = await HashAsync(snapshotPath, ct);
        var extractionHash = await HashAsync(extractionsPath, ct);
        var extractionManifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(extractionsPath)!, "manifest.json"),
                ct
            )
        );
        using (extractionManifest)
        {
            if (extractionManifest.RootElement.GetProperty("inputSha256").GetString() != inputHash)
            {
                throw new InvalidOperationException(
                    "Extraction and catalog snapshot do not correspond."
                );
            }
        }

        var providerHash =
            providerInputsPath == null ? "none" : await HashAsync(providerInputsPath, ct);
        var identity = inputHash + "/" + extractionHash + "/" + providerHash;
        var methods = adapters.ToDictionary(
            adapter => adapter.GetType().Name,
            adapter => adapter.Version
        );
        await using var run = new ShadowCatalogRun(outputDirectory, identity, methods, resume);
        var inputs = new ShadowCatalogInputs(snapshotPath, providerInputsPath);
        await run.SaveAsync(
            run.CompletedLine,
            "indexing",
            new
            {
                snapshotPath,
                extractionsPath,
                providerInputsPath,
            },
            CancellationToken.None
        );
        await inputs.ObserveAsync(adapters, extractionsPath, ct);
        var population = new
        {
            snapshotPath = Path.GetFullPath(snapshotPath),
            extractionsPath = Path.GetFullPath(extractionsPath),
            providerInputsPath,
            inputKinds = inputs.InputKinds,
            expectedOutcomes = inputs.ExpectedOutcomes,
            census = inputs.Population,
            configuredProviderArtists = inputs.ConfiguredProviders,
            preparationFailures = inputs.PreparationFailures,
            artists = inputs.Artists.Count,
            scope = "Complete retained raw inputs. Census/profile totals describe a larger catalog and are not processed raw-input counts.",
        };
        await run.SaveAsync(run.CompletedLine, "running", population, ct);

        await using var extractionRows = ShadowCatalogInputs
            .ReadJsonLinesAsync(extractionsPath, ct)
            .GetAsyncEnumerator(ct);
        long line = 0;
        var completedLine = run.CompletedLine;
        try
        {
            await foreach (var record in inputs.ReadRecordsAsync(ct))
            {
                line++;
                var kind = record.GetProperty("kind").GetString()!;
                JsonElement? extraction = null;
                JsonElement? extractionError = null;
                if (kind is "source_sample" or "track_sample")
                {
                    if (!await extractionRows.MoveNextAsync())
                    {
                        throw new InvalidOperationException(
                            "Extraction stream ended before its corresponding catalog row."
                        );
                    }

                    var extracted = extractionRows.Current;
                    var extractedUuid =
                        extracted.GetProperty("kind").GetString() == "extraction_error"
                            ? extracted
                                .GetProperty("input")
                                .GetProperty("row")
                                .GetProperty("uuid")
                                .GetGuid()
                            : extracted.GetProperty("resourceUuid").GetGuid();
                    if (extractedUuid != record.GetProperty("row").GetProperty("uuid").GetGuid())
                    {
                        throw new InvalidOperationException(
                            "Extraction stream no longer matches catalog input order."
                        );
                    }

                    if (extracted.GetProperty("kind").GetString() == "extraction_error")
                    {
                        extractionError = extracted;
                    }
                    else
                    {
                        extraction = extracted;
                    }
                }

                if (line <= run.CompletedLine)
                {
                    continue;
                }

                var inputResults = new List<ShadowCatalogResult>();
                foreach (var adapter in adapters)
                {
                    var domains = adapter.DomainsFor(kind);
                    if (domains.Count == 0)
                    {
                        continue;
                    }

                    var input = inputs.Project(record, extraction, extractionError);
                    ShadowCatalogResult[] results;
                    try
                    {
                        if (
                            inputs.PreparationFailures.TryGetValue(
                                adapter.GetType().Name,
                                out var preparationError
                            )
                        )
                        {
                            throw new InvalidOperationException(
                                "adapter_index_failed: " + preparationError
                            );
                        }

                        results = adapter.Process(input).ToArray();
                        if (
                            !results
                                .Select(result => result.Domain)
                                .Order()
                                .SequenceEqual(domains.Order())
                            || results.Any(result =>
                                result.InputKey != input.Key
                                || result.Outcome
                                    is not (
                                        "proposed"
                                        or "unresolved"
                                        or "failed"
                                        or "not_applicable"
                                    )
                            )
                        )
                        {
                            throw new InvalidOperationException(
                                "Adapter did not produce exactly one outcome per applicable domain."
                            );
                        }
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        results = domains
                            .Select(domain =>
                                ShadowCatalogResult.For(
                                    input,
                                    domain,
                                    "failed",
                                    adapter.Version,
                                    null,
                                    new { record = input.Record },
                                    error.GetType().Name + ": " + error.Message
                                )
                            )
                            .ToArray();
                    }

                    inputResults.AddRange(
                        extractionError == null
                            ? results
                            : results.Select(result =>
                                result with
                                {
                                    Evidence = new
                                    {
                                        interpretationEvidence = result.Evidence,
                                        extractionError,
                                    },
                                    Reason =
                                        "local_extraction_error: "
                                        + ExtractionErrorReason(extractionError.Value)
                                        + (result.Reason == null ? "" : "; " + result.Reason),
                                }
                            )
                    );
                }

                await run.WriteInputAsync(inputResults);
                completedLine = line;
                if (line % 2000 == 0)
                {
                    await run.SaveAsync(completedLine, "running", population, ct);
                }
            }

            if (await extractionRows.MoveNextAsync())
            {
                throw new InvalidOperationException(
                    "Extraction stream has unmatched trailing rows."
                );
            }

            await run.SaveAsync(
                completedLine,
                run.HasFailures ? "completed_with_failures" : "completed",
                population,
                ct
            );
            File.Copy(
                Path.Combine(outputDirectory, "progress.json"),
                Path.Combine(outputDirectory, "manifest.json"),
                overwrite: true
            );
        }
        catch
        {
            await run.SaveAsync(completedLine, "interrupted", population, CancellationToken.None);
            throw;
        }
    }

    private static string ExtractionErrorReason(JsonElement error) =>
        error.GetProperty("reason").GetString() ?? "unknown";

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
