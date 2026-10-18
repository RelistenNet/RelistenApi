using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;
using Relisten.Data;
using Relisten.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A local, explicitly resumed search plan. The file freezes page commands before dispatch;
/// the acquisition database remains authoritative for their progress and original evidence.
/// </summary>
public sealed class MusicBrainzSearchBatch(
    NormalizationMusicBrainzInputService service,
    NormalizationMusicBrainzInputStore store,
    NormalizationEvidenceService evidence
)
{
    public const string Version = "musicbrainz-search-batch-v1";
    private const int MaximumRequestBytes = 64 * 1024;
    private const int MaximumReceiptBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) },
    };

    /// <summary>Drives at most one page acquisition; resume explicitly with a new output directory.</summary>
    public async Task<MusicBrainzSearchBatchReport> RunAsync(
        string requestPath,
        string outputDirectory,
        Guid planUuid,
        string? priorReceiptPath = null,
        CancellationToken ct = default
    )
    {
        var prepared = await PreparePlan(requestPath, planUuid, priorReceiptPath, ct);
        outputDirectory = ClaimOutputDirectory(outputDirectory);

        try
        {
            var artifacts = new Dictionary<string, MusicBrainzAcquisitionArtifact>();
            await SaveFrozenPlan(outputDirectory, prepared, artifacts, ct);
            var (pages, coverage) = await AdvanceOnePage(prepared.Plan, ct);
            var receipt = CreateReceipt(prepared.Plan, pages, coverage, outputDirectory);
            await ExportReceipt(outputDirectory, receipt, artifacts, ct);
            return receipt.Report;
        }
        catch (Exception exception)
        {
            await SaveInterruption(outputDirectory, prepared.Plan, exception.GetType().Name);
            throw;
        }
    }

    private async Task<PreparedSearch> PreparePlan(
        string requestPath,
        Guid planUuid,
        string? priorReceiptPath,
        CancellationToken ct
    )
    {
        ct.ThrowIfCancellationRequested();
        if (planUuid == Guid.Empty)
        {
            throw new ArgumentException("Use a nonempty search plan UUID.");
        }
        var requestBytes = await ReadBounded(requestPath, MaximumRequestBytes, ct);
        var request = ParseRequest(requestBytes);
        var plan = FreezePlan(request, Hash(requestBytes), planUuid);
        var priorBytes =
            priorReceiptPath == null
                ? null
                : await ReadBounded(priorReceiptPath, MaximumReceiptBytes, ct);
        ValidatePriorPlan(priorBytes, plan);
        var sources = await ValidateSeed(request, ct);
        return new(plan, requestBytes, priorBytes, sources);
    }

    private static async Task SaveFrozenPlan(
        string output,
        PreparedSearch prepared,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        await WriteBytes(output, "request.json", prepared.RequestBytes, artifacts, ct);
        if (prepared.PriorBytes != null)
        {
            await WriteBytes(output, "prior-plan-receipt.json", prepared.PriorBytes, artifacts, ct);
        }
        await ExportSources(output, prepared.Sources, artifacts, ct);
        // Every child identity and exact descriptor reaches disk before the first page call.
        await WriteJson(output, "plan.json", prepared.Plan, artifacts, ct);
    }

    private async Task<(
        IReadOnlyList<MusicBrainzSearchBatchPage>,
        MusicBrainzSearchCoverage
    )> AdvanceOnePage(MusicBrainzSearchFrozenPlan plan, CancellationToken ct)
    {
        var pages = await ReadPages(plan, ct);
        var coverage = AnalyzeCoverage(plan, pages);
        if (coverage.NextPage is { } next)
        {
            await service.AcquireSearchAsync(next.CommandUuid, next.Request, ct);
            pages = await ReadPages(plan, ct);
            coverage = AnalyzeCoverage(plan, pages);
        }
        return (pages, coverage);
    }

    private static MusicBrainzSearchPlanReceipt CreateReceipt(
        MusicBrainzSearchFrozenPlan plan,
        IReadOnlyList<MusicBrainzSearchBatchPage> pages,
        MusicBrainzSearchCoverage coverage,
        string output
    )
    {
        var state = "completed";
        if (coverage.NextPage != null)
        {
            state = "pending";
        }
        else if (coverage.Variants.Any(x => !x.WindowComplete) || plan.Issues.Count != 0)
        {
            state = "completed_partial";
        }
        var notBefore =
            coverage.NextPage == null
                ? null
                : pages
                    .Single(x => x.CommandUuid == coverage.NextPage.CommandUuid)
                    .Acquisition?.NotBefore;
        var unhydrated = pages
            .Where(x => x.Observation?.Hits != null)
            .SelectMany(x => x.Observation!.Hits!)
            .Select(x => x.Entity.Identity.Identity)
            .Distinct()
            .ToArray();
        return new(
            1,
            plan,
            new(state, plan.PlanUuid, notBefore, output),
            pages,
            coverage.Variants,
            unhydrated,
            coverage.Issues
        );
    }

    private async Task ExportReceipt(
        string output,
        MusicBrainzSearchPlanReceipt receipt,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        foreach (var page in receipt.Pages.Where(x => x.Acquisition != null))
        {
            await ExportPage(output, page, artifacts, ct);
        }
        var receiptBytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        if (receiptBytes.Length > MaximumReceiptBytes)
        {
            throw new InvalidDataException("The search plan receipt exceeds 16 MiB.");
        }
        await WriteBytes(output, "plan-receipt.json", receiptBytes, artifacts, ct);
        var manifest = new
        {
            schemaVersion = 1,
            receipt.Report,
            externalUniverseComplete = false,
            versions = new
            {
                batch = Version,
                queryBuilder = receipt.Plan.QueryBuilderVersion,
                acquisitions = receipt
                    .Pages.Where(x => x.Acquisition != null)
                    .Select(x => x.Acquisition!.AcquisitionVersion)
                    .Distinct()
                    .ToArray(),
                decoder = NormalizationProviderDecoder.Version,
                toolAssembly = await AssemblyReceipt(typeof(MusicBrainzSearchBatch).Assembly, ct),
                apiAssembly = await AssemblyReceipt(
                    typeof(NormalizationMusicBrainzInputService).Assembly,
                    ct
                ),
            },
            artifacts,
        };
        await WriteJson(output, "manifest.partial.json", manifest, null, ct);
        ct.ThrowIfCancellationRequested();
        File.Move(
            Path.Combine(output, "manifest.partial.json"),
            Path.Combine(output, "manifest.json")
        );
    }

    private sealed record PreparedSearch(
        MusicBrainzSearchFrozenPlan Plan,
        byte[] RequestBytes,
        byte[]? PriorBytes,
        IReadOnlyList<NormalizationInputRevision> Sources
    );

    private static MusicBrainzSearchBatchRequest ParseRequest(byte[] bytes)
    {
        var request = ParseStrict<MusicBrainzSearchBatchRequest>(bytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("seed", out var seed)
            || !root.TryGetProperty("cacheMode", out _)
            || !root.TryGetProperty("inputs", out _)
            || request.Inputs == null
            || request.Inputs.Any(x => x == null || x.Resource == null)
            || seed.ValueKind != JsonValueKind.Object
            || !seed.TryGetProperty("kind", out _)
            || !seed.TryGetProperty("text", out _)
            || !seed.TryGetProperty("relatedArtists", out _)
            || request.Seed == null
            || request.Seed.RelatedArtists == null
            || request.Seed.RelatedArtists.Any(x => x == null)
            || !Enum.IsDefined(request.CacheMode)
            || (
                request.CacheMode == MusicBrainzCacheMode.ReuseFresh
                && request.MaximumCacheAge is not { Ticks: > 0 }
            )
            || (
                request.CacheMode == MusicBrainzCacheMode.Refresh && request.MaximumCacheAge != null
            )
        )
        {
            throw new InvalidDataException(
                "Supply a typed seed and an explicit valid cache policy."
            );
        }
        var literals = new[] { request.Seed.Text }.Concat(
            request.Seed.RelatedArtists.SelectMany(x => new[] { x.Name, x.Role })
        );
        if (
            literals.Any(x => x != null && (x.Value == null || x.Evidence == null))
            || request.Seed.RelatedArtists.Any(x =>
                x.Identity is { } identity
                && (identity.Identity == null || identity.Evidence == null)
            )
        )
        {
            throw new InvalidDataException(
                "Search seed assertions require literal values and retained citations."
            );
        }
        return request;
    }

    private static MusicBrainzSearchFrozenPlan FreezePlan(
        MusicBrainzSearchBatchRequest request,
        string hash,
        Guid planUuid
    )
    {
        var variants = MusicBrainzCandidateQueryBuilder.BuildVariants(request.Seed);
        if (variants.Queries.Count > 3)
        {
            throw new InvalidDataException("A search plan supports at most three variants.");
        }
        var frozen = variants
            .Queries.Select(
                (query, index) =>
                    new MusicBrainzSearchFrozenVariant(
                        index,
                        query,
                        new[] { 0, 50, 100, 150 }
                            .Select(offset =>
                            {
                                var page = new MusicBrainzSearchPageRequest(
                                    query,
                                    offset,
                                    request.CacheMode,
                                    request.MaximumCacheAge,
                                    new(planUuid, hash)
                                );
                                return new MusicBrainzSearchFrozenPage(
                                    ChildCommand(planUuid, index, offset),
                                    page,
                                    MusicBrainzCandidateQueryBuilder.CreateDescriptor(page)
                                );
                            })
                            .ToArray()
                    )
            )
            .ToArray();
        return new(Version, variants.Version, planUuid, hash, frozen, variants.Issues);
    }

    private static void ValidatePriorPlan(byte[]? bytes, MusicBrainzSearchFrozenPlan plan)
    {
        if (bytes == null)
        {
            return;
        }
        using var document = JsonDocument.Parse(bytes);
        MusicBrainzSearchFrozenPlan priorPlan;
        if (document.RootElement.TryGetProperty("schemaVersion", out _))
        {
            var prior = ParseStrict<MusicBrainzSearchPlanReceipt>(bytes);
            if (prior.SchemaVersion != 1)
            {
                throw new InvalidDataException("The prior search receipt version is unsupported.");
            }
            priorPlan = prior.Plan;
        }
        else
        {
            priorPlan = ParseStrict<MusicBrainzSearchFrozenPlan>(bytes);
        }
        if (JsonSerializer.Serialize(priorPlan, Json) != JsonSerializer.Serialize(plan, Json))
        {
            throw new InvalidDataException(
                "The prior receipt does not contain this exact frozen search plan."
            );
        }
    }

    private async Task<IReadOnlyList<NormalizationInputRevision>> ValidateSeed(
        MusicBrainzSearchBatchRequest request,
        CancellationToken ct
    )
    {
        var seed = request.Seed;
        var literals = new List<ObservedValue<string>>();
        if (seed.Text != null)
        {
            literals.Add(seed.Text);
        }
        foreach (var artist in seed.RelatedArtists)
        {
            if (artist.Name != null)
            {
                literals.Add(artist.Name);
            }
            if (artist.Role != null)
            {
                literals.Add(artist.Role);
            }
        }
        var identities = seed
            .RelatedArtists.Where(x => x.Identity != null)
            .Select(x => x.Identity!)
            .ToArray();
        var citations = literals
            .Select(x => x.Evidence)
            .Concat(identities.Select(x => x.Evidence))
            .ToArray();
        if (
            citations.Any(x =>
                x == null || x.InputRevisionUuid == Guid.Empty || x.JsonPointer == null
            )
            || !citations
                .Select(x => x.InputRevisionUuid)
                .ToHashSet()
                .SetEquals(request.Inputs.Select(x => x.InputRevisionUuid))
        )
        {
            throw new InvalidDataException(
                "List the exact immutable input references for every seed citation."
            );
        }
        var sources = (
            await evidence.GetInputsAsync(request.Inputs, new NormalizationInputReadLimits(), ct)
        ).ToDictionary(x => x.Uuid);
        foreach (var literal in literals)
        {
            var input = sources[literal.Evidence.InputRevisionUuid];
            using var document = NormalizationRetainedDocument.Open(input, ct);
            document.ValidateEvidence(literal.Evidence);
            var value = document.Resolve(literal.Evidence.JsonPointer);
            if (
                value.ValueKind != JsonValueKind.String
                || (literal.Evidence.Quote ?? value.GetString()) != literal.Value
            )
            {
                throw new InvalidDataException(
                    "A search seed literal does not match its retained citation."
                );
            }
        }
        foreach (var identity in identities)
        {
            var input = sources[identity.Evidence.InputRevisionUuid];
            using var document = NormalizationRetainedDocument.Open(input, ct);
            document.ValidateEvidence(identity.Evidence);
            var value = document.Resolve(identity.Evidence.JsonPointer);
            if (
                identity.Identity.Provider != "musicbrainz.org"
                || identity.Identity.ResourceType != "artist"
                || !Guid.TryParse(identity.Identity.ProviderKey, out var expected)
                || expected == Guid.Empty
                || value.ValueKind != JsonValueKind.String
                || !Guid.TryParse(identity.Evidence.Quote ?? value.GetString(), out var actual)
                || actual != expected
            )
            {
                throw new InvalidDataException(
                    "A related artist identity does not match its retained citation."
                );
            }
        }
        return sources.Values.OrderBy(x => x.Uuid).ToArray();
    }

    private async Task<IReadOnlyList<MusicBrainzSearchBatchPage>> ReadPages(
        MusicBrainzSearchFrozenPlan plan,
        CancellationToken ct
    )
    {
        var pages = new List<MusicBrainzSearchBatchPage>();
        foreach (var variant in plan.Variants)
        {
            foreach (var frozen in variant.Pages)
            {
                var acquisition = await service.GetSearchAsync(frozen.CommandUuid, ct);
                MusicBrainzSearchPageObservation? observation = null;
                if (acquisition != null)
                {
                    if (
                        JsonSerializer.Serialize(acquisition.Request, Json)
                            != JsonSerializer.Serialize(frozen.Request, Json)
                        || acquisition.Descriptor != frozen.Descriptor
                    )
                    {
                        throw new NormalizationConflictException(
                            "A saved page command differs from the frozen search plan."
                        );
                    }
                    if (acquisition.State == MusicBrainzAcquisitionState.Completed)
                    {
                        var capture = await RequirePageCapture(acquisition, ct);
                        var decoded = new NormalizationProviderDecoder().Decode(
                            capture.Revision,
                            ct
                        );
                        if (decoded.MusicBrainzSearchPages?.Count != 1)
                        {
                            throw new InvalidDataException(
                                "A completed page did not decode into one search observation."
                            );
                        }
                        observation = decoded.MusicBrainzSearchPages[0];
                    }
                }
                pages.Add(
                    new(
                        variant.Ordinal,
                        frozen.Request.Offset,
                        frozen.CommandUuid,
                        acquisition,
                        observation
                    )
                );
            }
        }
        return pages;
    }

    private async Task<NormalizationCaptureResult> RequirePageCapture(
        MusicBrainzSearchAcquisition acquisition,
        CancellationToken ct
    )
    {
        if (acquisition.Result?.Input == null || acquisition.Result.FetchUuid == null)
        {
            throw new InvalidDataException(
                "A completed search page has no original capture reference."
            );
        }
        var capture =
            await evidence.GetCaptureAsync(acquisition.Result.FetchUuid.Value, ct)
            ?? throw new InvalidDataException("The original search page capture is unavailable.");
        if (
            capture.Revision.Resource != acquisition.Descriptor.Resource
            || capture.Revision.Resource != acquisition.Result.Input.Resource
            || capture.Revision.Uuid != acquisition.Result.Input.InputRevisionUuid
            || capture.Revision.CaptureVersion != acquisition.Descriptor.CaptureVersion
            || !capture.Fetch.AcceptedAsCurrent
            || capture.Fetch.ResourceGeneration != acquisition.Result.ResourceGeneration
        )
        {
            throw new InvalidDataException(
                "The original capture does not support this successful search page."
            );
        }
        return capture;
    }

    private static MusicBrainzSearchCoverage AnalyzeCoverage(
        MusicBrainzSearchFrozenPlan plan,
        IReadOnlyList<MusicBrainzSearchBatchPage> pages
    )
    {
        var variants = new List<MusicBrainzSearchVariantCoverage>();
        var allIssues = new List<string>(plan.Issues);
        MusicBrainzSearchFrozenPage? next = null;
        foreach (var variant in plan.Variants)
        {
            var issues = new List<string>();
            var seen = new HashSet<NormalizationResourceKey>();
            var variantPages = pages
                .Where(x => x.VariantOrdinal == variant.Ordinal)
                .OrderBy(x => x.Offset)
                .ToArray();
            var gap = false;
            foreach (var page in variantPages)
            {
                if (gap && page.Acquisition != null)
                {
                    issues.Add("OffsetGap");
                }
                gap |= page.Acquisition?.State != MusicBrainzAcquisitionState.Completed;
            }
            long? initialCount = null;
            var observedEnd = false;
            foreach (var frozen in variant.Pages)
            {
                var page = pages.Single(x => x.CommandUuid == frozen.CommandUuid);
                if (page.Acquisition?.State == MusicBrainzAcquisitionState.Failed)
                {
                    issues.Add("PageFailed");
                    break;
                }
                if (page.Acquisition?.State != MusicBrainzAcquisitionState.Completed)
                {
                    if (issues.Count == 0)
                    {
                        next ??= frozen;
                    }
                    issues.Add(page.Acquisition == null ? "PageNotRequested" : "PagePending");
                    break;
                }
                var observation = page.Observation!;
                if (
                    observation.Count == null
                    || observation.Offset?.Value != frozen.Request.Offset
                    || observation.Hits == null
                )
                {
                    issues.Add("InvalidPageStructure");
                    break;
                }
                var count = observation.Count.Value;
                initialCount ??= count;
                if (initialCount != count)
                {
                    issues.Add("ChangingResultCount");
                }
                if (observation.Issues.Count != 0)
                {
                    issues.Add("PageDecodeIssues");
                }
                foreach (var hit in observation.Hits)
                {
                    if (!seen.Add(hit.Entity.Identity.Identity))
                    {
                        issues.Add("DuplicateHit");
                    }
                }
                var end = (long)frozen.Request.Offset + observation.Hits.Count;
                observedEnd = end >= count;
                if (end > count)
                {
                    issues.Add("CountBelowReturnedSlots");
                }
                if (observation.Hits.Count < frozen.Descriptor.Limit && end < count)
                {
                    issues.Add("ShortPageBeforeCount");
                }
                if (
                    observedEnd
                    && variantPages.Any(x =>
                        x.Offset > frozen.Request.Offset && x.Acquisition != null
                    )
                )
                {
                    issues.Add("UnexpectedPageAfterEnd");
                }
                if (observedEnd || issues.Count != 0)
                {
                    break;
                }
                if (frozen.Request.Offset == 150)
                {
                    issues.Add("PageLimit");
                }
            }
            var distinctIssues = issues.Distinct().ToArray();
            variants.Add(
                new(
                    variant.Ordinal,
                    observedEnd,
                    observedEnd && distinctIssues.Length == 0,
                    distinctIssues
                )
            );
            allIssues.AddRange(distinctIssues);
        }
        var candidates = pages
            .Where(x => x.Observation?.Hits != null)
            .SelectMany(x => x.Observation!.Hits!)
            .Select(x => x.Entity.Identity.Identity)
            .Distinct()
            .Count();
        if (candidates != 0)
        {
            allIssues.Add("HydrationNotImplemented");
        }
        if (candidates > 20)
        {
            allIssues.Add("HydrationLimit");
        }
        return new(variants, next, allIssues.Distinct().ToArray());
    }

    private static async Task ExportSources(
        string output,
        IReadOnlyList<NormalizationInputRevision> sources,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        foreach (var source in sources)
        {
            var prefix = $"seed-inputs/{source.Uuid:D}";
            await WriteBytes(output, prefix + "/input.bin", source.Payload, artifacts, ct);
            await WriteJson(
                output,
                prefix + "/revision.json",
                new
                {
                    source.Uuid,
                    source.Resource,
                    source.Sha256,
                    source.MediaType,
                    source.CaptureVersion,
                    source.CapturedAt,
                },
                artifacts,
                ct
            );
        }
    }

    private async Task ExportPage(
        string output,
        MusicBrainzSearchBatchPage page,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        var acquisition = page.Acquisition!;
        var prefix = $"pages/{page.VariantOrdinal}-{page.Offset}-{page.CommandUuid:D}";
        await WriteJson(output, prefix + "/command.json", acquisition, artifacts, ct);
        foreach (var attempt in acquisition.Attempts)
        {
            var attemptPrefix = prefix + $"/attempts/{attempt.Ordinal}-{attempt.AttemptUuid:D}";
            var response = await store.GetResponseAsync(attempt.AttemptUuid, ct);
            if (response != null)
            {
                await WriteJson(
                    output,
                    attemptPrefix + "/response.json",
                    response with
                    {
                        Response = response.Response with { Body = null },
                    },
                    artifacts,
                    ct
                );
                if (response.Response.Body != null)
                {
                    await WriteBytes(
                        output,
                        attemptPrefix + "/body.bin",
                        response.Response.Body,
                        artifacts,
                        ct
                    );
                }
            }
            var capture = await evidence.GetCaptureAsync(attempt.AttemptUuid, ct);
            if (capture != null)
            {
                await ExportCapture(output, attemptPrefix, capture, artifacts, ct);
            }
        }
        if (
            acquisition.Result?.FetchUuid is { } fetchUuid
            && !acquisition.Attempts.Any(x => x.AttemptUuid == fetchUuid)
        )
        {
            var capture = await RequirePageCapture(acquisition, ct);
            await ExportCapture(output, prefix + "/cached", capture, artifacts, ct);
        }
    }

    private static async Task ExportCapture(
        string output,
        string prefix,
        NormalizationCaptureResult capture,
        Dictionary<string, MusicBrainzAcquisitionArtifact> artifacts,
        CancellationToken ct
    )
    {
        await WriteBytes(output, prefix + "/input.bin", capture.Revision.Payload, artifacts, ct);
        var decoded = new NormalizationProviderDecoder().Decode(capture.Revision, ct);
        var decodedBytes = JsonSerializer.SerializeToUtf8Bytes(decoded, Json);
        if (decodedBytes.Length > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Decoded MusicBrainz observations exceed 32 MiB.");
        }
        await WriteBytes(output, prefix + "/decoded.json", decodedBytes, artifacts, ct);
        await WriteJson(
            output,
            prefix + "/capture.json",
            new
            {
                revision = new
                {
                    capture.Revision.Uuid,
                    capture.Revision.Resource,
                    capture.Revision.Sha256,
                    capture.Revision.MediaType,
                    capture.Revision.CaptureVersion,
                    capture.Revision.CapturedAt,
                },
                capture.Fetch,
            },
            artifacts,
            ct
        );
    }

    private async Task SaveInterruption(
        string output,
        MusicBrainzSearchFrozenPlan plan,
        string exceptionType
    )
    {
        using var audit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await WriteJson(
                output,
                "interruption.json",
                new { plan, exceptionType },
                null,
                audit.Token
            );
            var pages = await ReadPages(plan, audit.Token);
            await WriteJson(output, "recovery/pages.json", pages, null, audit.Token);
            foreach (var page in pages.Where(x => x.Acquisition != null))
            {
                await ExportPage(Path.Combine(output, "recovery"), page, new(), audit.Token);
            }
        }
        catch
        {
            // The durable command survives an unavailable local audit; no manifest claims success.
        }
    }

    private static async Task<byte[]> ReadBounded(string path, int maximum, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        if (stream.Length > maximum)
        {
            throw new InvalidDataException("The local search input exceeds its byte bound.");
        }
        var bytes = new byte[maximum + 1];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(read), ct);
            if (count == 0)
            {
                break;
            }
            read += count;
        }
        if (read > maximum)
        {
            throw new InvalidDataException("The local search input exceeds its byte bound.");
        }
        return bytes[..read];
    }

    private static T ParseStrict<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            RequireUniqueProperties(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, Json)
                ?? throw new InvalidDataException("Use a typed search JSON object.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("Use a valid typed search JSON object.", exception);
        }
    }

    private static void RequireUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate JSON properties are not supported.");
                }
                RequireUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RequireUniqueProperties(item);
            }
        }
    }

    private static string ClaimOutputDirectory(string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("Choose a new search output directory.");
        }
        Directory.CreateDirectory(output);
        using var claim = new FileStream(Path.Combine(output, ".search-owner"), FileMode.CreateNew);
        return output;
    }

    private static Guid ChildCommand(Guid planUuid, int ordinal, int offset)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Version}\n{planUuid:D}\n{ordinal}\n{offset}")
        );
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static async Task WriteJson(
        string output,
        string relative,
        object value,
        Dictionary<string, MusicBrainzAcquisitionArtifact>? artifacts,
        CancellationToken ct
    ) =>
        await WriteBytes(
            output,
            relative,
            JsonSerializer.SerializeToUtf8Bytes(value, Json),
            artifacts,
            ct
        );

    private static async Task WriteBytes(
        string output,
        string relative,
        byte[] bytes,
        Dictionary<string, MusicBrainzAcquisitionArtifact>? artifacts,
        CancellationToken ct
    )
    {
        var path = Path.Combine(output, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read
        );
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
        artifacts?.Add(relative, new(bytes.LongLength, Hash(bytes)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<object> AssemblyReceipt(Assembly assembly, CancellationToken ct)
    {
        await using var stream = File.OpenRead(assembly.Location);
        return new
        {
            version = assembly.GetName().Version?.ToString(),
            sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)),
        };
    }
}

public sealed record MusicBrainzSearchBatchRequest(
    MusicBrainzSearchSeed Seed,
    IReadOnlyList<NormalizationBindingInputReference> Inputs,
    MusicBrainzCacheMode CacheMode,
    TimeSpan? MaximumCacheAge = null
);

public sealed record MusicBrainzSearchFrozenPage(
    Guid CommandUuid,
    MusicBrainzSearchPageRequest Request,
    MusicBrainzSearchDescriptor Descriptor
);

public sealed record MusicBrainzSearchFrozenVariant(
    int Ordinal,
    MusicBrainzSearchQuery Query,
    IReadOnlyList<MusicBrainzSearchFrozenPage> Pages
);

public sealed record MusicBrainzSearchFrozenPlan(
    string Version,
    string QueryBuilderVersion,
    Guid PlanUuid,
    string RequestSha256,
    IReadOnlyList<MusicBrainzSearchFrozenVariant> Variants,
    IReadOnlyList<string> Issues
);

public sealed record MusicBrainzSearchBatchPage(
    int VariantOrdinal,
    int Offset,
    Guid CommandUuid,
    MusicBrainzSearchAcquisition? Acquisition,
    MusicBrainzSearchPageObservation? Observation
);

public sealed record MusicBrainzSearchVariantCoverage(
    int Ordinal,
    bool ObservedEnd,
    bool WindowComplete,
    IReadOnlyList<string> Issues
);

internal sealed record MusicBrainzSearchCoverage(
    IReadOnlyList<MusicBrainzSearchVariantCoverage> Variants,
    MusicBrainzSearchFrozenPage? NextPage,
    IReadOnlyList<string> Issues
);

public sealed record MusicBrainzSearchBatchReport(
    string State,
    Guid PlanUuid,
    DateTimeOffset? NotBefore,
    string OutputDirectory
);

public sealed record MusicBrainzSearchPlanReceipt(
    int SchemaVersion,
    MusicBrainzSearchFrozenPlan Plan,
    MusicBrainzSearchBatchReport Report,
    IReadOnlyList<MusicBrainzSearchBatchPage> Pages,
    IReadOnlyList<MusicBrainzSearchVariantCoverage> Variants,
    IReadOnlyList<NormalizationResourceKey> UnhydratedEntities,
    IReadOnlyList<string> Issues
)
{
    public bool ExternalUniverseComplete => false;
    public bool SeedContextAccepted => false;
    public bool HydrationImplemented => false;
    public int HydrationSelectionCeiling => 20;
}
