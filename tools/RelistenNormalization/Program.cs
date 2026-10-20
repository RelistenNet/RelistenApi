using System.Text.Json;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;
using Relisten.Normalization.Batch;

if (args.Length == 2 && args[0] == "review-serve")
{
    await NormalizationReviewHost.RunAsync(args[1]);
    return 0;
}

if (
    !(
        (
            args.Length == 3
            && args[0]
                is "extract"
                    or "extract-source-actors"
                    or "venue-pair-baseline"
                    or "people-shared"
                    or "phish-file-reference"
        )
        || (args.Length == 5 && args[0] == "luna-credits")
        || (
            args.Length == 4
            && args[0] is "people-context" or "people-context-recover" or "people-context-continue"
        )
        || (args.Length == 5 && args[0] == "admit-retained-local")
        || (args.Length == 4 && args[0] == "file-title-replay")
        || (args.Length == 7 && args[0] == "file-title-replay" && args[4] == "--knowledge")
        || (args.Length == 3 && args[0] is "shadow-compositions-acquire" or "retained-http-inputs")
        || (
            args.Length == 4
            && args[0]
                is "musicbrainz-acquire"
                    or "people-adjudicated-baseline"
                    or "people-view-baseline"
                    or "composition-evaluation"
        )
        || (args.Length is 4 or 5 && args[0] == "musicbrainz-search")
        || (
            args.Length == 4
            && args[0]
                is "venue-normalization-refresh"
                    or "go-set-composition-sources"
                    or "go-set-composition-supplement"
        )
        || (
            args.Length is 4 or 5
            && args[0]
                is "music-consolidate"
                    or "composition-hypotheses"
                    or "composition-work-searches"
            && (args.Length == 4 || args[4] == "--resume")
        )
        || (
            args.Length is 7 or 8
            && args[0] == "music-consolidate"
            && args[4] == "--knowledge"
            && (args.Length == 7 || args[7] == "--resume")
        )
        || (
            args.Length is 4 or 5
            && args[0] == "composition-select"
            && (args.Length == 4 || args[4] is "--resume" or "--preflight")
        )
        || (args.Length == 6 && args[0] == "luna-credit-fields")
        || (
            args.Length is 4 or 5
            && args[0] == "shadow"
            && (args.Length == 4 || args[4] == "--resume")
        )
        || (
            args.Length is 3 or 4
            && args[0]
                is "shadow-inline"
                    or "shadow-rows"
                    or "shadow-native"
                    or "shadow-source-observations"
                    or "shadow-venues"
            && (args.Length == 3 || args[3] == "--resume")
        )
        || (args.Length == 5 && args[0] == "luna-credit-cohort")
        || (
            args.Length == 5
            && args[0]
                is "actor-fields"
                    or "people-pair-baseline"
                    or "people-source-context-baseline"
        )
    )
)
{
    Console.Error.WriteLine(
        "Usage: RelistenNormalization extract <snapshot.jsonl> <new-output-directory>\n"
            + "       RelistenNormalization shadow <snapshot.jsonl[.gz]> <extractions.jsonl[.gz]> <output-directory> [--resume]\n"
            + "       RelistenNormalization shadow-inline <snapshot.jsonl[.gz] OR capture-manifest.json> <output-directory> [--resume]\n"
            + "       RelistenNormalization shadow-venues <snapshot.jsonl[.gz] OR capture-manifest.json> <output-directory> [--resume]\n"
            + "       RelistenNormalization go-set-composition-supplement <request.json> <new-output-directory> <frozen-request-sha256>\n"
            + "       RelistenNormalization go-set-composition-sources <request.json> <new-output-directory> <frozen-request-sha256>\n"
            + "       RelistenNormalization venue-normalization-refresh <request.json> <new-output-directory> <frozen-request-sha256>\n"
            + "       RelistenNormalization shadow-source-observations <snapshot.jsonl[.gz] OR capture-manifest.json> <output-directory> [--resume]\n"
            + "       RelistenNormalization music-consolidate <input-manifest.json> <output-directory> <frozen-manifest-sha256> [--knowledge <snapshot.json> <snapshot-sha256>] [--resume]\n"
            + "       RelistenNormalization file-title-replay <input-manifest.json> <new-output-directory> <frozen-manifest-sha256> [--knowledge <snapshot.json> <snapshot-sha256>]\n"
            + "       RelistenNormalization composition-hypotheses <request.json> <output-directory> <frozen-request-sha256> [--resume]\n"
            + "       RelistenNormalization composition-work-searches <request.json> <output-directory> <frozen-request-sha256> [--resume]\n"
            + "       RelistenNormalization composition-select <plan.json> <output-directory> <frozen-plan-sha256> [--resume|--preflight]\n"
            + "       RelistenNormalization people-shared <completed-source-observations-run> <new-output-directory>\n"
            + "       RelistenNormalization phish-file-reference <request.json> <new-output-directory>\n"
            + "       RelistenNormalization shadow-rows <catalog-rows.jsonl[.gz]> <output-directory> [--resume]\n"
            + "       RelistenNormalization shadow-native <catalog-rows.jsonl[.gz]> <output-directory> [--resume]\n"
            + "       RelistenNormalization extract-source-actors <snapshot.jsonl> <new-output-directory>\n"
            + "       RelistenNormalization venue-pair-baseline <bundle-directory> <new-output-directory>\n"
            + "       RelistenNormalization composition-evaluation <package-directory> <new-output-directory> <frozen-manifest-sha256>\n"
            + "       RelistenNormalization musicbrainz-acquire <request.json> <new-output-directory> <command-uuid>\n"
            + "       RelistenNormalization shadow-compositions-acquire <request.json> <output-directory>\n"
            + "       RelistenNormalization retained-http-inputs <request.json> <new-output-directory>\n"
            + "       RelistenNormalization admit-retained-local <catalog-manifest.json> <root-capture-directory> <directory-capture-directory> <new-output-directory>\n"
            + "       RelistenNormalization musicbrainz-search <plan-request.json> <new-output-directory> <plan-uuid> [<prior-plan-receipt.json>]\n"
            + "       RelistenNormalization people-view-baseline <prepared-package-directory> <new-output-directory> <frozen-manifest-sha256>\n"
            + "       RelistenNormalization people-adjudicated-baseline <prepared-package-directory> <new-output-directory> <frozen-manifest-sha256>\n"
            + "       RelistenNormalization people-pair-baseline <repository-root> <bundle-directory> <new-output-directory> <frozen-manifest-sha256>\n"
            + "       RelistenNormalization people-source-context-baseline <repository-root> <bundle-directory> <new-output-directory> <frozen-manifest-sha256>\n"
            + "       RelistenNormalization actor-fields <fields.jsonl> <export-receipt.json> <frozen-bundle-directory> <new-output-directory>\n"
            + "       RelistenNormalization luna-credits <extractions.jsonl> <new-output-directory> <job-uuid> <records-per-cohort>\n"
            + "       RelistenNormalization luna-credit-fields <labels.jsonl> <fields.jsonl> <receipt.json> <new-output-directory> <job-uuid>\n"
            + "       RelistenNormalization luna-credit-cohort <plan.json> <plan-sha256> <new-output-directory> <job-uuid>"
            + "\n       RelistenNormalization people-context <batches.jsonl> <new-output-directory> <job-uuid>"
            + "\n       RelistenNormalization people-context-recover <completed-run-directory> <new-output-directory> <job-uuid>"
            + "\n       RelistenNormalization people-context-continue <plan.json> <plan-sha256> <new-output-directory>"
    );
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) =>
{
    signal.Cancel = true;
    cancellation.Cancel();
};

if (args[0] == "file-title-replay")
{
    var result = await new FileTitleReplayBatch().RunAsync(
        args[1],
        args[2],
        args[3],
        cancellation.Token,
        knowledgePath: args.Length == 7 ? args[5] : null,
        knowledgeSha256: args.Length == 7 ? args[6] : null
    );
    Console.WriteLine(
        JsonSerializer.Serialize(
            new
            {
                result.State,
                Titles = result.Titles.Rows,
                result.Outcomes,
            }
        )
    );
    return 0;
}

try
{
    if (args[0] == "venue-normalization-refresh")
    {
        var result = await new VenueNormalizationRefreshBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    result.TotalVenues,
                    result.ChangedVenues,
                    result.UnchangedVenues,
                    result.Method,
                }
            )
        );
        return 0;
    }

    if (args[0] == "go-set-composition-supplement")
    {
        var result = await new GoSetCompositionSupplement().RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        return 0;
    }

    if (args[0] == "go-set-composition-sources")
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        var result = await new GoSetCompositionSources(client).RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        return 0;
    }

    if (args[0] == "composition-select")
    {
        var result = await new CompositionSelectionBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            resume: args.Length == 5 && args[4] == "--resume",
            preflightOnly: args.Length == 5 && args[4] == "--preflight",
            ct: cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    result.State,
                    result.ProcessedInputs,
                    result.Outcomes,
                    result.CacheHits,
                    result.JobAccountedUsd,
                    result.PublicationPermitted,
                }
            )
        );
        return result.State is "completed" or "preflight_completed" ? 0 : 1;
    }

    if (args[0] == "composition-hypotheses")
    {
        var result = await new CompositionHypothesisBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            args.Length == 5,
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    result.Complete,
                    result.Songs,
                    result.Works,
                    result.Outcomes,
                }
            )
        );
        return result.Complete ? 0 : 1;
    }

    if (args[0] == "music-consolidate")
    {
        await new MusicRepertoireConsolidationBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            args[^1] == "--resume",
            cancellation.Token,
            args.Length >= 7 ? args[5] : null,
            args.Length >= 7 ? args[6] : null
        );
        return 0;
    }

    if (args[0] == "phish-file-reference")
    {
        var request =
            JsonSerializer.Deserialize<PhishFileReferenceBenchmarkRequest>(
                await File.ReadAllTextAsync(args[1], cancellation.Token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ) ?? throw new InvalidDataException("A Phish file reference request is required.");
        Console.WriteLine(
            await new PhishFileReferenceBenchmark().RunAsync(request, args[2], cancellation.Token)
        );
        return 0;
    }

    if (args[0] == "people-shared")
    {
        await new SharedPeopleExport().RunAsync(args[1], args[2], cancellation.Token);
        return 0;
    }

    if (args[0] == "admit-retained-local")
    {
        var result = await new RetainedLocalProviderInputs().PrepareAsync(
            new(args[1], args[2], args[3]),
            args[4],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        return 0;
    }

    if (args[0] == "retained-http-inputs")
    {
        var request =
            JsonSerializer.Deserialize<RetainedHttpProviderInputsRequest>(
                await File.ReadAllBytesAsync(args[1], cancellation.Token),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ) ?? throw new InvalidDataException("A retained HTTP input request is required.");
        var result = await new RetainedHttpProviderInputs().PrepareAsync(
            request,
            args[2],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        return 0;
    }

    if (
        args[0]
        is "shadow"
            or "shadow-inline"
            or "shadow-rows"
            or "shadow-native"
            or "shadow-source-observations"
            or "shadow-venues"
    )
    {
        var sourceObservationsOnly = args[0] == "shadow-source-observations";
        var venuesOnly = args[0] == "shadow-venues";
        if (
            sourceObservationsOnly
            && new[]
            {
                "RELISTEN_SHADOW_PEOPLE_MODEL",
                "RELISTEN_SHADOW_PEOPLE_CANDIDATES",
                "RELISTEN_SHADOW_VENUE_GEOGRAPHY",
                "RELISTEN_SHADOW_PROVIDER_INPUTS",
                "RELISTEN_SHADOW_COMPOSITION_EVIDENCE",
            }.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
        )
        {
            throw new ArgumentException(
                "Source observations accept cached Luna extraction only, without optional matching, provider, geography or composition inputs."
            );
        }
        var nativeObservations = new ShadowProviderObservations();
        var geographyManifest = Environment.GetEnvironmentVariable(
            "RELISTEN_SHADOW_VENUE_GEOGRAPHY"
        );
        var geography =
            geographyManifest == null ? null : ShadowVenueGeography.Load(geographyManifest);
        ShadowCatalogBatch shadow;
        if (venuesOnly)
        {
            // Observe the retained source/show contexts, but emit only venue outcomes.
            // Other domain adapters and their optional model inputs are not constructed.
            shadow = new([
                geography == null ? new ShadowVenueAdapter() : new ShadowVenueAdapter(geography),
            ]);
        }
        else
        {
            var people = new ShadowPeopleAdapter(
                Environment.GetEnvironmentVariable("RELISTEN_SHADOW_LUNA_CREDITS"),
                Environment.GetEnvironmentVariable("RELISTEN_SHADOW_LUNA_INPUT_SHA256"),
                Environment.GetEnvironmentVariable("RELISTEN_SHADOW_PEOPLE_MODEL"),
                Environment.GetEnvironmentVariable("RELISTEN_SHADOW_PEOPLE_CANDIDATES"),
                sourceObservationsOnly: sourceObservationsOnly
            );
            shadow = new ShadowCatalogBatch(
                sourceObservationsOnly
                    ? [people, new ShadowPerformanceAdapter()]
                    :
                    [
                        people,
                        geography == null
                            ? new ShadowVenueAdapter()
                            : new ShadowVenueAdapter(geography),
                        new ShadowNativePeopleAdapter(nativeObservations),
                        new ShadowNativeVenueAdapter(nativeObservations, geography),
                        new ShadowNativeFileContentsAdapter(nativeObservations),
                        new ShadowRepertoireAdapter(),
                        new ShadowCompositionAdapter(
                            Environment.GetEnvironmentVariable(
                                "RELISTEN_SHADOW_COMPOSITION_EVIDENCE"
                            )
                        ),
                        new ShadowPerformanceAdapter(),
                    ]
            );
        }
        var providerInputs = venuesOnly
            ? null
            : Environment.GetEnvironmentVariable("RELISTEN_SHADOW_PROVIDER_INPUTS");
        var outputDirectory = args[0] == "shadow" ? args[3] : args[2];
        if (args[0] is "shadow-rows" or "shadow-native")
        {
            await shadow.RunRowsInlineAsync(
                args[1],
                outputDirectory,
                resume: args.Length == 4,
                providerInputsPath: providerInputs,
                ct: cancellation.Token,
                providerInputsOnly: args[0] == "shadow-native"
            );
        }
        else if (args[0] is "shadow-inline" or "shadow-source-observations" or "shadow-venues")
        {
            await shadow.RunInlineAsync(
                args[1],
                outputDirectory,
                resume: args.Length == 4,
                providerInputsPath: providerInputs,
                ct: cancellation.Token,
                sourceObservationsOnly: sourceObservationsOnly
            );
        }
        else
        {
            await shadow.RunAsync(
                args[1],
                args[2],
                outputDirectory,
                resume: args.Length == 5,
                providerInputsPath: providerInputs,
                ct: cancellation.Token
            );
        }
        var manifestPath = Path.Combine(Path.GetFullPath(outputDirectory), "manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Console.WriteLine(
            JsonSerializer.Serialize(
                new { manifestPath, state = manifest.RootElement.GetProperty("state").GetString() }
            )
        );
        return manifest.RootElement.GetProperty("state").GetString() == "completed" ? 0 : 1;
    }

    if (args[0] == "composition-evaluation")
    {
        var evaluation = await new CompositionEvaluationBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                evaluation,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return 0;
    }
    if (args[0] == "people-view-baseline")
    {
        var views = await new PeopleViewBaselineBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(views, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        );
        return 0;
    }
    if (args[0] == "people-adjudicated-baseline")
    {
        var adjudicated = await new PeopleAdjudicatedBaselineBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                adjudicated,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return 0;
    }
    if (args[0] == "people-context")
    {
        if (!Guid.TryParse(args[3], out var contextJobUuid))
        {
            throw new ArgumentException("Use a valid bounded-job UUID.");
        }
        var contextReport = await new PeopleContextBatch().RunAsync(
            args[1],
            args[2],
            contextJobUuid,
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                contextReport,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return contextReport.State == "completed" ? 0 : 1;
    }
    if (args[0] == "people-context-continue")
    {
        var continuationReport = await new PeopleContextContinuation().RunAsync(
            args[1],
            args[2],
            args[3],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                continuationReport,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return continuationReport.State == "completed" ? 0 : 1;
    }
    if (args[0] == "people-context-recover")
    {
        if (!Guid.TryParse(args[3], out var contextJobUuid))
        {
            throw new ArgumentException("Use the original contextual job UUID.");
        }
        var recoveryReport = await new PeopleContextRecovery().RunAsync(
            args[1],
            args[2],
            contextJobUuid,
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                recoveryReport,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return 0;
    }
    if (args[0] == "people-pair-baseline")
    {
        var baseline = await new PeoplePairBaselineBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            args[4],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                baseline,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return 0;
    }
    if (args[0] == "people-source-context-baseline")
    {
        var contextBaseline = await new PeopleSourceContextBaselineBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            args[4],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                contextBaseline,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return 0;
    }
    if (args[0] == "actor-fields")
    {
        var actorReport = await new ActorFieldReplayBatch().RunAsync(
            args[1],
            args[2],
            args[3],
            args[4],
            cancellation.Token
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                actorReport,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        return actorReport.State == "completed" ? 0 : 1;
    }
    if (
        args[0]
        is "musicbrainz-acquire"
            or "musicbrainz-search"
            or "shadow-compositions-acquire"
            or "composition-work-searches"
    )
    {
        var commandUuid = Guid.Empty;
        if (
            args[0] is "musicbrainz-acquire" or "musicbrainz-search"
            && (!Guid.TryParse(args[3], out commandUuid) || commandUuid == Guid.Empty)
        )
        {
            throw new ArgumentException("Use a nonempty MusicBrainz command or plan UUID.");
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
        if (args[0] == "composition-work-searches")
        {
            var result = await new CompositionWorkSearchBatch(
                new CompositionWorkSearchClient(service, store, evidence)
            ).RunAsync(args[1], args[2], args[3], args.Length == 5, cancellation.Token);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        result.State,
                        result.ReservedServiceCalls,
                        tasks = result
                            .Tasks.GroupBy(task => task.State)
                            .ToDictionary(group => group.Key, group => group.Count()),
                        result.PublicationPermitted,
                    }
                )
            );
            return result.State == "completed" ? 0 : 1;
        }

        if (args[0] == "shadow-compositions-acquire")
        {
            var compositionAcquisition = await new ShadowCompositionAcquisitionBatch(
                new ShadowCompositionAcquisitionClient(service, evidence)
            ).RunAsync(args[1], args[2], cancellation.Token);
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        compositionAcquisition.State,
                        compositionAcquisition.ReservedServiceCalls,
                        retainedInputs = compositionAcquisition.Inputs.Count,
                        plannedCommands = compositionAcquisition.Commands.Count,
                        issues = compositionAcquisition.Issues.Count,
                        compositionAcquisition.PublicationPermitted,
                    },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )
            );
            return compositionAcquisition.State == "plan_exhausted" ? 0 : 1;
        }
        if (args[0] == "musicbrainz-search")
        {
            var search = await new MusicBrainzSearchBatch(service, store, evidence).RunAsync(
                args[1],
                args[2],
                commandUuid,
                args.Length == 5 ? args[4] : null,
                cancellation.Token
            );
            Console.WriteLine(
                JsonSerializer.Serialize(
                    search,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )
            );
            return search.State == "completed" ? 0 : 1;
        }
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
    if (args[0] is "luna-credits" or "luna-credit-fields" or "luna-credit-cohort")
    {
        var fieldsMode = args[0] == "luna-credit-fields";
        var cohortMode = args[0] == "luna-credit-cohort";
        var jobIndex =
            fieldsMode ? 5
            : cohortMode ? 4
            : 3;
        if (!Guid.TryParse(args[jobIndex], out var jobUuid))
        {
            throw new ArgumentException("Use a valid bounded-job UUID.");
        }
        var db = NormalizationLocalDatabase.Create();
        var cache = new NormalizationExtractionCacheService(db);
        var options = new LunaCreditExtractionOptions();
        using var http = new HttpClient();
        if (fieldsMode || cohortMode)
        {
            var runner = new LunaCreditFieldEvaluationBatch(
                http,
                cache,
                db,
                options,
                () => Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""
            );
            var fieldsReport = cohortMode
                ? await runner.RunCohortAsync(
                    args[1],
                    args[2],
                    args[3],
                    jobUuid,
                    cancellation.Token
                )
                : await runner.RunAsync(
                    args[1],
                    args[2],
                    args[3],
                    args[4],
                    jobUuid,
                    cancellation.Token
                );
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

    var report = await new NormalizationExtractionBatch(
        includeSourceActorAliases: args[0] == "extract-source-actors"
    ).RunAsync(args[1], args[2], cancellation.Token);
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
