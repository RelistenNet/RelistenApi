using System.Security.Cryptography;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Runs domain adapters over retained catalog inputs without a host, database or
/// external client. Each applicable input/domain receives its own shadow outcome.
/// </summary>
public sealed class ShadowCatalogBatch(IReadOnlyList<IShadowCatalogAdapter> adapters)
{
    /// <summary>Consumes the existing snapshot and its corresponding extraction stream.</summary>
    /// <exception cref="InvalidDataException">
    /// The extraction receipt or resumed run belongs to different input bytes or method versions.
    /// Use a new output directory for changed inputs; existing outcomes remain intact.
    /// </exception>
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
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(Path.GetDirectoryName(extractionsPath)!, "manifest.json"),
                ct
            )
        );
        if (manifest.RootElement.GetProperty("inputSha256").GetString() != inputHash)
        {
            throw new InvalidDataException("Extraction and catalog snapshot do not correspond.");
        }

        var providerHash =
            providerInputsPath == null ? "none" : await HashAsync(providerInputsPath, ct);
        await RunPreparedAsync(
            new(snapshotPath, providerInputsPath),
            extractionsPath,
            outputDirectory,
            inputHash + "/" + extractionHash + "/" + providerHash,
            resume,
            null,
            ct
        );
    }

    /// <summary>
    /// Extracts each row locally from one snapshot or a completed capture-shard selection.
    /// The run freezes that selection before indexing. Resume verifies its saved files;
    /// additional shards in a growing capture require a new output directory.
    /// </summary>
    public async Task RunInlineAsync(
        string snapshotOrCaptureManifestPath,
        string outputDirectory,
        bool resume = false,
        string? providerInputsPath = null,
        CancellationToken ct = default
    )
    {
        var capture = await ShadowCatalogCapture.OpenAsync(
            snapshotOrCaptureManifestPath,
            outputDirectory,
            resume,
            providerInputsPath,
            ct
        );
        await RunPreparedAsync(
            new(capture),
            null,
            outputDirectory,
            "inline/" + capture.Identity,
            resume,
            capture,
            ct
        );
    }

    /// <summary>
    /// Extracts and normalizes ordinary JSONL rows without requiring replica snapshot
    /// framing. Input bytes and extractor versions identify the resumable run.
    /// </summary>
    /// <remarks>
    /// This entrypoint does not certify database snapshot provenance. Retain the
    /// export's original evidence separately, and use separate runs for catalogs
    /// whose numeric artist, source or show IDs have different meanings.
    /// When providerInputsOnly is true, all context is observed but only provider_input
    /// rows produce outcomes. The processing scope is part of the resumable run identity.
    /// </remarks>
    /// <exception cref="InvalidDataException">
    /// A resumed run has different input bytes, adapter versions or processing scope.
    /// Changed inputs or scope require a new output directory.
    /// </exception>
    public async Task RunRowsInlineAsync(
        string snapshotPath,
        string outputDirectory,
        bool resume = false,
        string? providerInputsPath = null,
        CancellationToken ct = default,
        bool providerInputsOnly = false
    )
    {
        var inputHash = await HashAsync(snapshotPath, ct);
        var providerHash =
            providerInputsPath == null ? "none" : await HashAsync(providerInputsPath, ct);

        await RunPreparedAsync(
            new(snapshotPath, providerInputsPath),
            null,
            outputDirectory,
            (providerInputsOnly ? "native-rows-inline/" : "rows-inline/")
                + inputHash
                + "/"
                + providerHash,
            resume,
            null,
            ct,
            providerInputsOnly
        );
    }

    private async Task RunPreparedAsync(
        ShadowCatalogInputs inputs,
        string? extractionsPath,
        string outputDirectory,
        string identity,
        bool resume,
        ShadowCatalogCapture? capture,
        CancellationToken ct,
        bool providerInputsOnly = false
    )
    {
        var methods = adapters.ToDictionary(
            adapter => adapter.GetType().Name,
            adapter => adapter.Version
        );
        if (extractionsPath == null)
        {
            methods.Add(
                nameof(CatalogRowExtraction),
                string.Join(
                    "/",
                    "catalog-row-inline-v1",
                    RecordingCreditExtractor.Version,
                    SourceActorAliasExtractor.Version,
                    TrackTitleExtractor.Version
                )
            );
        }

        await using var run = new ShadowCatalogRun(outputDirectory, identity, methods, resume);
        if (capture != null)
        {
            await capture.SaveAsync(outputDirectory);
        }

        // Persist an empty checkpoint before the potentially long indexing pass.
        // A cancelled first pass can then rebuild indexes in this same run directory.
        await run.SaveAsync(
            run.CompletedLine,
            "indexing",
            new
            {
                snapshotPaths = inputs.SnapshotPaths,
                extractionsPath,
                capture = capture?.Descriptor,
            },
            CancellationToken.None
        );
        await inputs.ObserveAsync(adapters, extractionsPath, ct, providerInputsOnly);
        var population = new
        {
            snapshotPath = inputs.SnapshotPaths.Count == 1
                ? Path.GetFullPath(inputs.SnapshotPaths[0])
                : null,
            snapshotPaths = inputs.SnapshotPaths,
            extractionsPath = extractionsPath == null ? null : Path.GetFullPath(extractionsPath),
            providerInputsPath = inputs.ProviderInputsPath,
            capture = capture?.Descriptor,
            extractionMode = extractionsPath == null ? "inline" : "external",
            processingScope = providerInputsOnly ? "provider_inputs_only" : "all_applicable_inputs",
            inputKinds = inputs.InputKinds,
            expectedOutcomes = inputs.ExpectedOutcomes,
            census = inputs.Population,
            snapshots = inputs.Frames,
            configuredProviderArtists = inputs.ConfiguredProviders,
            preparationFailures = inputs.PreparationFailures,
            inlineExtractionErrors = inputs.InlineExtractionErrors,
            artists = inputs.Artists.Count,
            scope = capture == null
                ? "All rows from a byte-pinned ordinary JSONL export. This run does not certify replica snapshot provenance. Keep catalog namespaces separate."
                : "All selected retained raw inputs. Each shard has its own snapshot. Census totals can describe a larger catalog.",
        };
        await run.SaveAsync(run.CompletedLine, "running", population, ct);
        await ProcessRowsAsync(
            inputs,
            extractionsPath,
            run,
            outputDirectory,
            population,
            ct,
            providerInputsOnly
        );
    }

    private async Task ProcessRowsAsync(
        ShadowCatalogInputs inputs,
        string? extractionsPath,
        ShadowCatalogRun run,
        string outputDirectory,
        object population,
        CancellationToken ct,
        bool providerInputsOnly
    )
    {
        long line = 0;
        var completedLine = run.CompletedLine;
        try
        {
            await foreach (
                var row in inputs.ReadPreparedAsync(extractionsPath, run.CompletedLine, ct)
            )
            {
                line++;
                if (line <= run.CompletedLine)
                {
                    continue;
                }

                if (
                    !providerInputsOnly
                    || row.Record.GetProperty("kind").GetString() == "provider_input"
                )
                {
                    await run.WriteInputAsync(ProcessAdapters(inputs, row));
                }

                // Checkpoints address the original stream, including context rows. Skipping
                // their output must not shift the provider boundaries used by a later resume.
                completedLine = line;
                if (line % 2000 == 0)
                {
                    await run.SaveAsync(completedLine, "running", population, ct);
                }
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

    private List<ShadowCatalogResult> ProcessAdapters(
        ShadowCatalogInputs inputs,
        ShadowCatalogInputs.PreparedRow row
    )
    {
        var kind = row.Record.GetProperty("kind").GetString()!;
        var inputResults = new List<ShadowCatalogResult>();
        foreach (var adapter in adapters)
        {
            var domains = adapter.DomainsFor(kind);
            if (domains.Count == 0)
            {
                continue;
            }

            var input = inputs.Project(row.Record, row.Extraction, row.ExtractionError);
            var results = ProcessAdapter(adapter, input, domains, inputs.PreparationFailures);
            inputResults.AddRange(
                row.ExtractionError == null
                    ? results
                    : results.Select(result =>
                        result with
                        {
                            Evidence = new
                            {
                                interpretationEvidence = result.Evidence,
                                extractionError = row.ExtractionError,
                            },
                            Reason =
                                "local_extraction_error: "
                                + row.ExtractionError.Value.GetProperty("reason").GetString()
                                + (result.Reason == null ? "" : "; " + result.Reason),
                        }
                    )
            );
        }
        return inputResults;
    }

    private static ShadowCatalogResult[] ProcessAdapter(
        IShadowCatalogAdapter adapter,
        ShadowCatalogInput input,
        IReadOnlyList<string> domains,
        IReadOnlyDictionary<string, string> preparationFailures
    )
    {
        try
        {
            if (preparationFailures.TryGetValue(adapter.GetType().Name, out var preparationError))
            {
                throw new InvalidOperationException("adapter_index_failed: " + preparationError);
            }

            var results = adapter.Process(input).ToArray();
            if (
                !results.Select(result => result.Domain).Order().SequenceEqual(domains.Order())
                || results.Any(result =>
                    result.InputKey != input.Key
                    || result.Outcome
                        is not ("proposed" or "unresolved" or "failed" or "not_applicable")
                )
            )
            {
                throw new InvalidOperationException(
                    "Adapter did not produce exactly one outcome per applicable domain."
                );
            }
            return results;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return domains
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
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
}
