using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Recompute venue hypotheses over the original frozen corpus, retaining only
/// changed browse fields. Original outcomes and catalog ownership remain intact.
/// </summary>
public sealed class VenueNormalizationRefreshBatch
{
    public const string RequestVersion = "venue-normalization-refresh-input-v1";
    public const string Version = "venue-normalization-corrections-v1";
    private const long MaximumBytes = 8 * 1024 * 1024;
    private const long MaximumLogicalBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = ShadowCatalogRun.Json;

    public async Task<VenueNormalizationCorrections> RunAsync(
        string requestPath,
        string outputDirectory,
        string requestSha256,
        CancellationToken ct = default
    )
    {
        var prepared = await AuthenticateAsync(requestPath, outputDirectory, requestSha256, ct);
        var previousChunks = new List<VenueCorrectionArtifact>();
        var previous = await ReadPreviousAsync(prepared.OriginalDirectory, previousChunks, ct);
        if (previous.Count != prepared.OriginalVenueCount)
        {
            throw new InvalidDataException("The original venue population is incomplete.");
        }

        var inputs = new ShadowCatalogInputs(prepared.Capture);
        await ObserveCorpusAsync(inputs, prepared.Adapter, ct);
        var changes = await WriteChangedProjectionsAsync(prepared, inputs, previous, ct);
        return await FinalizeAsync(prepared, changes, previousChunks, requestSha256, ct);
    }

    private static async Task<PreparedRefresh> AuthenticateAsync(
        string requestPath,
        string outputDirectory,
        string requestSha256,
        CancellationToken ct
    )
    {
        var requestBytes = await File.ReadAllBytesAsync(requestPath, ct);
        if (Hash(requestBytes) != requestSha256)
        {
            throw new InvalidDataException("Venue refresh request differs from its pin.");
        }
        var request = JsonSerializer.Deserialize<VenueNormalizationRefreshRequest>(
            requestBytes,
            Json
        )!;
        if (request.Version != RequestVersion || request.MaximumBytes is < 1 or > MaximumBytes)
        {
            throw new InvalidDataException("Use a bounded venue normalization refresh request.");
        }
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Choose a new venue correction output directory.");
        }
        CheckSpace(outputDirectory, request.MinimumFreeBytes);
        foreach (var pin in request.Pins)
        {
            await VerifyAsync(pin, ct);
        }
        using var oldManifest = JsonDocument.Parse(
            await File.ReadAllBytesAsync(request.PreviousRunManifest.Path, ct)
        );
        var old = oldManifest.RootElement;
        if (old.GetProperty("state").GetString() != "completed")
        {
            throw new InvalidDataException("Venue refresh requires a completed original run.");
        }
        var oldDirectory = Path.GetDirectoryName(
            Path.GetFullPath(request.PreviousRunManifest.Path)
        )!;
        if (
            Path.GetFullPath(request.CaptureDescriptor.Path)
            != Path.Combine(oldDirectory, "input-descriptor.json")
        )
        {
            throw new InvalidDataException(
                "The capture descriptor must belong to the original venue run."
            );
        }
        using var descriptor = JsonDocument.Parse(
            await File.ReadAllBytesAsync(request.CaptureDescriptor.Path, ct)
        );
        var captureInput = descriptor.RootElement.GetProperty("inputPath").GetString()!;
        // Resume authenticates the saved selection without saving a descriptor,
        // checkpoint or progress file back into the original run.
        var capture = await ShadowCatalogCapture.OpenAsync(
            captureInput,
            oldDirectory,
            true,
            null,
            ct
        );
        if (old.GetProperty("inputIdentity").GetString() != "inline/" + capture.Identity)
        {
            throw new InvalidDataException("The original outcomes and frozen capture differ.");
        }
        var adapter = new ShadowVenueAdapter(
            request.GeographyManifest.Path + "#sha256=" + request.GeographyManifest.Sha256
        );
        var oldMethod = old.GetProperty("methods")
            .GetProperty(nameof(ShadowVenueAdapter))
            .GetString()!;
        if (
            oldMethod == adapter.Version
            || !oldMethod.EndsWith(
                "/geography=" + request.GeographyManifest.Sha256,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "Refresh requires the same geography and a new venue method."
            );
        }

        return new(
            request,
            outputDirectory,
            oldDirectory,
            captureInput,
            capture,
            adapter,
            old.GetProperty("outputRows").GetInt64()
        );
    }

    private static async Task ObserveCorpusAsync(
        ShadowCatalogInputs inputs,
        ShadowVenueAdapter adapter,
        CancellationToken ct
    )
    {
        await foreach (var record in inputs.ReadRecordsAsync(ct, retainFrames: true))
        {
            adapter.Observe(record);
        }
    }

    private static async Task<ChangeSummary> WriteChangedProjectionsAsync(
        PreparedRefresh prepared,
        ShadowCatalogInputs inputs,
        IReadOnlyDictionary<Guid, PreviousVenue> previous,
        CancellationToken ct
    )
    {
        var request = prepared.Request;
        var outputDirectory = prepared.OutputDirectory;
        var adapter = prepared.Adapter;
        CheckSpace(outputDirectory, request.MinimumFreeBytes);
        Directory.CreateDirectory(outputDirectory);

        long changed = 0;
        var groups = new Dictionary<string, ShadowVenueHypothesis>(StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<Guid>();
        var budget = new OutputBudget(
            request.MaximumBytes,
            request.MinimumFreeBytes,
            outputDirectory
        );
        long logicalBytes = 0;
        var changesPath = Path.Combine(outputDirectory, "changes.jsonl.gz");
        await using (
            var stream = new GZipStream(
                new BoundedOutput(File.Create(changesPath + ".writing"), budget),
                CompressionLevel.SmallestSize
            )
        )
        {
            await foreach (var record in inputs.ReadRecordsAsync(ct))
            {
                if (adapter.DomainsFor(record.GetProperty("kind").GetString()!).Count == 0)
                {
                    continue;
                }
                var row = record.GetProperty("row");
                var uuid = row.GetProperty("uuid").GetGuid();
                if (
                    !seen.Add(uuid)
                    || !previous.TryGetValue(uuid, out var baseline)
                    || !baseline
                        .RawHash.AsSpan()
                        .SequenceEqual(
                            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row, Json))
                        )
                )
                {
                    throw new InvalidDataException(
                        "A venue differs from its original captured row."
                    );
                }
                var input = new ShadowCatalogInput(
                    record,
                    null,
                    new(
                        baseline.ArtistId,
                        baseline.ArtistUuid,
                        baseline.ArtistName,
                        [baseline.Provider]
                    ),
                    baseline.Provider
                );
                var result = adapter.Process(input).Single();
                var proposal = (ShadowVenueProposal)result.Proposal!;
                if (proposal.Normalization?.Hypothesis is { } group)
                {
                    groups.TryAdd(group.Key, group);
                }
                var current = Project(result, proposal);
                if (
                    JsonSerializer.Serialize(current, Json)
                    == JsonSerializer.Serialize(baseline.Projection, Json)
                )
                {
                    continue;
                }
                var correction = new VenueNormalizationCorrection(
                    uuid,
                    baseline.LegacyId,
                    baseline.ArtistId,
                    baseline.Projection,
                    current,
                    baseline.Origin
                );
                logicalBytes += await WriteAsync(
                    stream,
                    correction,
                    MaximumLogicalBytes - logicalBytes,
                    ct
                );
                changed++;
                foreach (
                    var key in new[]
                    {
                        baseline.Projection.NormalizationKey,
                        current.NormalizationKey,
                    }
                )
                {
                    if (key != null)
                    {
                        affected.Add(key);
                    }
                }
                CheckSpace(outputDirectory, request.MinimumFreeBytes);
            }
        }
        if (seen.Count != previous.Count)
        {
            throw new InvalidDataException("Venue refresh omitted original venue accounts.");
        }
        return new(seen.Count, changed, logicalBytes, groups, affected, budget);
    }

    private static async Task<VenueNormalizationCorrections> FinalizeAsync(
        PreparedRefresh prepared,
        ChangeSummary changes,
        IReadOnlyList<VenueCorrectionArtifact> previousChunks,
        string requestSha256,
        CancellationToken ct
    )
    {
        var request = prepared.Request;
        var outputDirectory = prepared.OutputDirectory;
        var logicalBytes = changes.LogicalBytes;
        var changesPath = Path.Combine(outputDirectory, "changes.jsonl.gz");
        var groupsPath = Path.Combine(outputDirectory, "groups.jsonl.gz");
        long groupCount = 0;
        await using (
            var stream = new GZipStream(
                new BoundedOutput(File.Create(groupsPath + ".writing"), changes.Budget),
                CompressionLevel.SmallestSize
            )
        )
        {
            foreach (var key in changes.Affected.Order(StringComparer.Ordinal))
            {
                if (changes.Groups.TryGetValue(key, out var group))
                {
                    logicalBytes += await WriteAsync(
                        stream,
                        group,
                        MaximumLogicalBytes - logicalBytes,
                        ct
                    );
                    groupCount++;
                }
            }
        }
        foreach (var pin in request.Pins.Concat(previousChunks))
        {
            await VerifyAsync(pin, ct);
        }
        await ShadowCatalogCapture.OpenAsync(
            prepared.CaptureInput,
            prepared.OriginalDirectory,
            true,
            null,
            ct
        );
        CheckSpace(outputDirectory, request.MinimumFreeBytes);
        File.Move(changesPath + ".writing", changesPath);
        File.Move(groupsPath + ".writing", groupsPath);
        var manifest = new VenueNormalizationCorrections(
            Version,
            "completed",
            true,
            prepared.Adapter.Version,
            requestSha256,
            request.BaseDirectory,
            request.PreviousRunManifest,
            request.CaptureDescriptor,
            request.GeographyManifest,
            previousChunks,
            await ArtifactAsync(changesPath, changes.Changed, ct),
            await ArtifactAsync(groupsPath, groupCount, ct),
            changes.Total,
            changes.Changed,
            changes.Total - changes.Changed
        );
        var completion = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        if (
            logicalBytes + completion.Length > MaximumLogicalBytes
            || new FileInfo(changesPath).Length
                + new FileInfo(groupsPath).Length
                + completion.Length
                > request.MaximumBytes
        )
        {
            throw new InvalidDataException(
                "The complete venue correction exceeds its output bound."
            );
        }
        changes.Budget.Reserve(completion.Length);
        await File.WriteAllBytesAsync(
            Path.Combine(outputDirectory, "manifest.json.writing"),
            completion,
            ct
        );
        CheckSpace(outputDirectory, request.MinimumFreeBytes);
        File.Move(
            Path.Combine(outputDirectory, "manifest.json.writing"),
            Path.Combine(outputDirectory, "manifest.json")
        );
        return manifest;
    }

    private static async Task<Dictionary<Guid, PreviousVenue>> ReadPreviousAsync(
        string directory,
        List<VenueCorrectionArtifact> chunks,
        CancellationToken ct
    )
    {
        var result = new Dictionary<Guid, PreviousVenue>();
        var strings = new ProjectionStrings();
        foreach (
            var path in Directory
                .EnumerateFiles(Path.Combine(directory, "venues"), "*.jsonl.gz")
                .Order(StringComparer.Ordinal)
        )
        {
            var artifact = await ArtifactAsync(path, 0, ct);
            long line = 0;
            await foreach (
                var text in ShadowJsonLines.ReadTextAsync(
                    path,
                    32 * 1024 * 1024,
                    1024L * 1024 * 1024,
                    ct
                )
            )
            {
                line++;
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                var row = root.GetProperty("evidence").GetProperty("nativeVenue");
                var uuid = row.GetProperty("uuid").GetGuid();
                if (
                    root.GetProperty("domain").GetString() != "venues"
                    || root.GetProperty("inputUuid").GetGuid() != uuid
                )
                {
                    throw new InvalidDataException(
                        "An original outcome changes its venue ownership."
                    );
                }
                var projection = ReadPreviousProjection(root, strings);
                var previous = new PreviousVenue(
                    row.GetProperty("id").GetInt64(),
                    row.GetProperty("artist_id").GetInt64(),
                    root.GetProperty("artistUuid").GetGuid(),
                    strings.Intern(root.GetProperty("artistName").GetString()!),
                    strings.Intern(root.GetProperty("provider").GetString()!),
                    SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row, Json)),
                    SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)),
                    projection,
                    new(
                        artifact.Path,
                        artifact.Bytes,
                        artifact.Sha256,
                        line,
                        "/evidence/nativeVenue"
                    )
                );
                if (!result.TryAdd(uuid, previous))
                {
                    if (!result[uuid].OutcomeHash.AsSpan().SequenceEqual(previous.OutcomeHash))
                    {
                        throw new InvalidDataException(
                            "An original venue replay changes its account."
                        );
                    }
                    continue;
                }
                if (result.Count > 500_000)
                {
                    throw new InvalidDataException(
                        "The venue population exceeds its refresh bound."
                    );
                }
            }
            chunks.Add(artifact with { Rows = line });
        }
        return result;
    }

    private static VenueCorrectionProjection ReadPreviousProjection(
        JsonElement outcome,
        ProjectionStrings strings
    )
    {
        var proposal = outcome.GetProperty("proposal");
        var normalization = proposal.TryGetProperty("normalization", out var normalized)
            ? normalized
            : default;
        var hypothesis =
            normalization.ValueKind == JsonValueKind.Object
            && normalization.TryGetProperty("hypothesis", out var selected)
                ? selected
                : default;
        var uncertainties =
            normalization.ValueKind == JsonValueKind.Object
            && normalization.TryGetProperty("uncertainties", out var values)
            && values.ValueKind != JsonValueKind.Null
                ? values
                    .EnumerateArray()
                    .Select(value => strings.Intern(value.GetString()!))
                    .ToArray()
                : [];

        // The original outcome can contain many alternative comparisons, history
        // and source examples. None belongs to the retained comparison map.
        return new(
            strings.Intern(proposal.GetProperty("proposedPlaceKey").GetString()!),
            strings.Read(hypothesis, "key"),
            strings.Read(normalization, "status"),
            strings.Read(normalization, "membershipBasis"),
            strings.Intern(proposal.GetProperty("groupingBasis").GetString()!),
            strings.Read(proposal, "displayName"),
            strings.Read(normalization, "displayLocation"),
            strings.Read(normalization, "geographicKey"),
            strings.Intern(outcome.GetProperty("outcome").GetString()!),
            strings.Read(outcome, "reason"),
            uncertainties
        );
    }

    private static VenueCorrectionProjection Project(
        ShadowCatalogResult result,
        ShadowVenueProposal proposal
    ) => Project(result.Outcome, result.Reason, proposal);

    private static VenueCorrectionProjection Project(
        string outcome,
        string? reason,
        ShadowVenueProposal proposal
    ) =>
        new(
            proposal.ProposedPlaceKey,
            proposal.Normalization?.Hypothesis?.Key,
            proposal.Normalization?.Status,
            proposal.Normalization?.MembershipBasis,
            proposal.GroupingBasis,
            proposal.DisplayName,
            proposal.Normalization?.DisplayLocation,
            proposal.Normalization?.GeographicKey,
            outcome,
            reason,
            proposal.Normalization?.Uncertainties ?? []
        );

    private static async Task<long> WriteAsync<T>(
        Stream stream,
        T value,
        long remaining,
        CancellationToken ct
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length + 1 > remaining)
        {
            throw new InvalidDataException("Venue correction output exceeds its byte bound.");
        }
        await stream.WriteAsync(bytes, ct);
        await stream.WriteAsync("\n"u8.ToArray(), ct);
        return bytes.Length + 1;
    }

    private static void CheckSpace(string directory, long minimum)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory))!;
        if (new DriveInfo(Path.GetPathRoot(parent)!).AvailableFreeSpace < minimum)
        {
            throw new IOException("Venue refresh stopped to preserve free disk space.");
        }
    }

    private static async Task<VenueCorrectionArtifact> ArtifactAsync(
        string path,
        long rows,
        CancellationToken ct
    )
    {
        await using var stream = File.OpenRead(path);
        return new(
            Path.GetFullPath(path),
            stream.Length,
            Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)),
            rows
        );
    }

    private static async Task VerifyAsync(VenueCorrectionArtifact artifact, CancellationToken ct)
    {
        var current = await ArtifactAsync(artifact.Path, artifact.Rows, ct);
        if (current != artifact)
        {
            throw new InvalidDataException("A venue correction input changed: " + artifact.Path);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record PreparedRefresh(
        VenueNormalizationRefreshRequest Request,
        string OutputDirectory,
        string OriginalDirectory,
        string CaptureInput,
        ShadowCatalogCapture Capture,
        ShadowVenueAdapter Adapter,
        long OriginalVenueCount
    );

    private sealed record ChangeSummary(
        long Total,
        long Changed,
        long LogicalBytes,
        IReadOnlyDictionary<string, ShadowVenueHypothesis> Groups,
        IReadOnlySet<string> Affected,
        OutputBudget Budget
    );

    // Both gzip files and the final manifest consume one physical output budget.
    // Reserve before writing, including gzip headers/trailers, so a bound failure
    // cannot exceed the disk allowance or publish a completion manifest.
    private sealed class OutputBudget(long maximum, long minimumFreeBytes, string outputDirectory)
    {
        private long written;

        internal void Reserve(int bytes)
        {
            if (bytes > maximum - written)
            {
                throw new InvalidDataException(
                    "Venue corrections exceed their physical byte bound."
                );
            }
            CheckSpace(outputDirectory, minimumFreeBytes + bytes);
            written += bytes;
        }
    }

    private sealed class BoundedOutput(Stream output, OutputBudget budget) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => output.Length;
        public override long Position
        {
            get => output.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => output.Flush();

        public override Task FlushAsync(CancellationToken ct) => output.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            budget.Reserve(count);
            output.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default
        )
        {
            budget.Reserve(buffer.Length);
            await output.WriteAsync(buffer, ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                output.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed record PreviousVenue(
        long LegacyId,
        long ArtistId,
        Guid ArtistUuid,
        string ArtistName,
        string Provider,
        byte[] RawHash,
        byte[] OutcomeHash,
        VenueCorrectionProjection Projection,
        VenueCorrectionOrigin Origin
    );

    // Repeated labels share exact strings within this load only. The pool itself
    // is released when loading finishes; there is no process-wide intern table.
    private sealed class ProjectionStrings
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        internal string Intern(string value)
        {
            if (values.TryGetValue(value, out var retained))
            {
                return retained;
            }
            values.Add(value, value);
            return value;
        }

        internal string? Read(JsonElement value, string property)
        {
            var text =
                value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty(property, out var field)
                    ? field.GetString()
                    : null;
            return text == null ? null : Intern(text);
        }
    }
}

/// <summary>Byte pins identify original evidence files; Rows is the declared JSONL census.</summary>
public sealed record VenueCorrectionArtifact(string Path, long Bytes, string Sha256, long Rows = 1);

public sealed record VenueNormalizationRefreshRequest(
    string Version,
    VenueCorrectionArtifact BaseDirectory,
    VenueCorrectionArtifact PreviousRunManifest,
    VenueCorrectionArtifact CaptureDescriptor,
    VenueCorrectionArtifact GeographyManifest,
    long MaximumBytes = 8 * 1024 * 1024,
    long MinimumFreeBytes = 11L * 1024 * 1024 * 1024 / 2
)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<VenueCorrectionArtifact> Pins =>
        [BaseDirectory, PreviousRunManifest, CaptureDescriptor, GeographyManifest];
}

/// <summary>Null NormalizationKey explicitly removes the previous shared membership.</summary>
public sealed record VenueCorrectionProjection(
    string PlaceKey,
    string? NormalizationKey,
    string? Status,
    string? Basis,
    string GroupingBasis,
    string? Name,
    string? NormalizedLocation,
    string? GeographicKey,
    string Outcome,
    string? Reason,
    IReadOnlyList<string> Uncertainties
);

public sealed record VenueCorrectionOrigin(
    string Path,
    long Bytes,
    string Sha256,
    long Line,
    string JsonPointer
);

public sealed record VenueNormalizationCorrection(
    Guid VenueUuid,
    long LegacyId,
    long ArtistId,
    VenueCorrectionProjection Previous,
    VenueCorrectionProjection Current,
    VenueCorrectionOrigin OriginalOutcome
);

public sealed record VenueNormalizationCorrections(
    string Version,
    string State,
    bool Complete,
    string Method,
    string RequestSha256,
    VenueCorrectionArtifact BaseDirectory,
    VenueCorrectionArtifact PreviousRunManifest,
    VenueCorrectionArtifact CaptureDescriptor,
    VenueCorrectionArtifact GeographyManifest,
    IReadOnlyList<VenueCorrectionArtifact> PreviousChunks,
    VenueCorrectionArtifact Changes,
    VenueCorrectionArtifact Groups,
    long TotalVenues,
    long ChangedVenues,
    long UnchangedVenues
);
