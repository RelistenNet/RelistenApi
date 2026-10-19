using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

public sealed record GoSetSourceTextProof(
    CompositionSelectionTargetOrigin Origin,
    string TextSha256,
    int StartCodePoint,
    int EndCodePoint,
    string Quote
);

public sealed record GoSetPageTextProof(
    string Url,
    int StartCodePoint,
    int EndCodePoint,
    string Quote
);

/// <summary>Development adjudication, never a provider assertion or an accepted catalog binding.</summary>
public sealed record GoSetTargetCorrespondenceProposal(
    CompositionSelectionTargetKey Target,
    NormalizationResourceKey ProviderSong,
    string ProviderSongSlug,
    IReadOnlyList<string> OccurrenceKeys,
    IReadOnlyList<GoSetSourceTextProof> SourceEvidence,
    IReadOnlyList<GoSetPageTextProof> ProviderEvidence,
    string Rationale,
    IReadOnlyList<string> Conflicts
);

public sealed record GoSetCompositionSupplementRequest(
    MusicConsolidationArtifact SelectionPlan,
    MusicConsolidationArtifact AcquisitionManifest,
    IReadOnlyList<GoSetTargetCorrespondenceProposal> Correspondences,
    long MinimumFreeBytes = 4L * 1024 * 1024 * 1024
);

public sealed record GoSetCompositionSupplementResult(
    string PlanPath,
    string ManifestPath,
    int Targets,
    int AddedWitnesses
);

/// <summary>
/// Adds cited provider occurrences through explicit development correspondence proposals.
/// The old bundle remains intact. All original targets and witnesses survive, while the
/// new plan has its own job identity and pins the new context generation.
/// </summary>
public sealed class GoSetCompositionSupplement
{
    private const long MaximumOutputBytes = 32L * 1024 * 1024;
    private const long MaximumLogicalBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = ShadowCatalogRun.Json;
    private static readonly JsonSerializerOptions AcquisitionJson = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, MusicConsolidationArtifact> pins = new(
        StringComparer.Ordinal
    );

    public async Task<GoSetCompositionSupplementResult> RunAsync(
        string requestPath,
        string outputDirectory,
        string requestSha256,
        CancellationToken ct = default
    )
    {
        pins.Clear();
        var requestPin = await PinAsync(requestPath, ct);
        if (requestPin.Bytes > 1024 * 1024 || requestPin.Sha256 != requestSha256)
        {
            throw new InvalidDataException("GoSet supplement request differs from its frozen pin.");
        }
        var request = JsonSerializer.Deserialize<GoSetCompositionSupplementRequest>(
            await File.ReadAllBytesAsync(requestPath, ct),
            Json
        )!;
        if (request.MinimumFreeBytes <= 0 || request.Correspondences.Count is < 1 or > 8)
        {
            throw new ArgumentException(
                "A supplement requires bounded, explicit correspondence proposals."
            );
        }
        await RequirePinAsync(request.SelectionPlan, ct);
        await RequirePinAsync(request.AcquisitionManifest, ct);
        var plan = JsonSerializer.Deserialize<CompositionSelectionPlan>(
            await File.ReadAllBytesAsync(request.SelectionPlan.Path, ct),
            Json
        )!;
        var contextPin =
            plan.TargetContextManifest
            ?? throw new InvalidDataException("A frozen original context bundle is required.");
        await RequirePinAsync(contextPin, ct);
        // Reuse the public offline loader: it authenticates the complete original plan,
        // scoped targets, original context closure and all retained source locators.
        var targets = await new CompositionSelectionBatch().ReadProgramTargetsAsync(
            request.SelectionPlan.Path,
            request.SelectionPlan.Sha256,
            ct
        );
        var original = JsonSerializer.Deserialize<CompositionSelectionTargetContextManifest>(
            await File.ReadAllBytesAsync(contextPin.Path, ct),
            Json
        )!;
        RememberOriginalPins(original);
        var captures = await ReadCapturesAsync(request.AcquisitionManifest, ct);
        var additions = BuildAdditions(request, targets, captures, requestPin);
        var contextRoot = Path.GetDirectoryName(Path.GetFullPath(contextPin.Path))!;
        var targetPath = Contained(contextRoot, original.Targets.Path);
        var textPath = Contained(contextRoot, original.Texts.Path);
        await RequirePinAsync(original.Targets with { Path = targetPath }, ct);
        await RequirePinAsync(original.Texts with { Path = textPath }, ct);
        var rows = await ReadRowsAsync(targetPath, ct);
        var texts = await ReadRowsAsync(textPath, ct);
        if (
            rows.Count != original.TargetCount
            || texts.Count != original.Texts.Rows
            || !rows.Select(row => row["key"]!.Deserialize<CompositionSelectionTargetKey>(Json)!)
                .ToHashSet()
                .SetEquals(targets.Select(target => target.Context.Key))
        )
        {
            throw new InvalidDataException(
                "Original context rows differ from their authenticated target population."
            );
        }
        AppendWitnesses(rows, texts, additions);
        await RecheckPinsAsync(ct);

        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new ArgumentException("A supplement requires a new output directory.");
        }
        Directory.CreateDirectory(outputDirectory);
        var output = new Output(outputDirectory, request.MinimumFreeBytes);
        var targetArtifact = await output.WriteRowsAsync("targets.jsonl.gz", rows, ct);
        var textArtifact = await output.WriteRowsAsync("texts.jsonl.gz", texts, ct);
        var manifest = original with
        {
            Targets = targetArtifact,
            Texts = textArtifact,
            SourceManifests = original
                .SourceManifests.Concat(
                    pins.Values.Select(pin => new CompositionSelectionContextSourceArtifact(
                        pin.Path,
                        pin.Bytes,
                        pin.Sha256,
                        pin.Rows
                    ))
                )
                .DistinctBy(pin => pin.Path)
                .ToArray(),
            WitnessChunks = original
                .WitnessChunks.Concat(captures.Select(capture => Chunk(capture.DecodedPin)))
                .Append(Chunk(requestPin))
                .DistinctBy(pin => pin.Path)
                .ToArray(),
        };
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var newPlan = plan with
        {
            JobUuid = Guid.NewGuid(),
            TargetContextManifest = new(
                manifestPath,
                manifestBytes.LongLength,
                Hash(manifestBytes),
                1
            ),
        };
        var planPath = Path.Combine(outputDirectory, "plan.json");
        await output.WriteAsync(
            "plan.json",
            JsonSerializer.SerializeToUtf8Bytes(newPlan, Json),
            ct
        );
        await RecheckPinsAsync(ct);
        // The plan pins these already computed bytes, so completion can be published
        // last without a circular plan/manifest hash or a partially usable generation.
        await output.WriteAsync("manifest.json", manifestBytes, ct);
        return new(planPath, manifestPath, rows.Count, additions.Count);
    }

    private async Task<Capture[]> ReadCapturesAsync(
        MusicConsolidationArtifact artifact,
        CancellationToken ct
    )
    {
        var manifest = JsonSerializer.Deserialize<GoSetCompositionSourcesResult>(
            await File.ReadAllBytesAsync(artifact.Path, ct),
            AcquisitionJson
        )!;
        if (
            manifest.Version != GoSetCompositionSources.Version
            || manifest.State != "completed"
            || manifest.Captures.Count is < 1 or > 8
        )
        {
            throw new InvalidDataException("A completed bounded GoSet acquisition is required.");
        }
        var root = Path.GetDirectoryName(Path.GetFullPath(artifact.Path))!;
        var acquisitionRequestPath = Contained(root, "request.json");
        var acquisitionRequestPin = await PinAsync(acquisitionRequestPath, ct);
        var acquisitionRequest = JsonSerializer.Deserialize<GoSetCompositionSourcesRequest>(
            await File.ReadAllBytesAsync(acquisitionRequestPath, ct),
            AcquisitionJson
        )!;
        if (
            acquisitionRequestPin.Sha256 != manifest.RequestSha256
            || !acquisitionRequest
                .Urls.Select(url => new Uri(url, UriKind.Absolute).AbsoluteUri)
                .SequenceEqual(
                    manifest.Captures.Select(capture => capture.RequestedUrl),
                    StringComparer.Ordinal
                )
        )
        {
            throw new InvalidDataException(
                "GoSet acquisition inventory differs from its original literal queue."
            );
        }
        var captures = new List<Capture>();
        foreach (var capture in manifest.Captures)
        {
            var bodyPin = await RequireCapturePin(capture.Body);
            var inputPin = await RequireCapturePin(capture.Input);
            var decodedPin = await RequireCapturePin(capture.Decoded);
            var input = JsonSerializer.Deserialize<NormalizationInputRevision>(
                await File.ReadAllBytesAsync(inputPin.Path, ct),
                AcquisitionJson
            )!;
            if (
                capture.StatusCode != 200
                || capture.RequestedUrl != capture.FinalUrl
                || capture.Resource.Provider != "go-set.net"
                || input.Uuid != capture.InputRevisionUuid
                || input.Resource != capture.Resource
                || input.Sha256 != bodyPin.Sha256
                || Hash(input.Payload) != bodyPin.Sha256
                || input.MediaType != capture.MediaType
                || input.CapturedAt != capture.CapturedAt.UtcDateTime
            )
            {
                throw new InvalidDataException(
                    "A GoSet capture does not own its original revision."
                );
            }
            var decoded = new NormalizationProviderDecoder().Decode(input, ct);
            if (
                Hash(JsonSerializer.SerializeToUtf8Bytes(decoded, AcquisitionJson))
                != decodedPin.Sha256
            )
            {
                throw new InvalidDataException(
                    "GoSet decoded assertions differ from their original input."
                );
            }
            using var retained = NormalizationRetainedDocument.Open(input);
            captures.Add(
                new(capture, input, decoded, decodedPin, retained.Resolve("").GetString()!)
            );

            async Task<MusicConsolidationArtifact> RequireCapturePin(GoSetCompositionArtifact value)
            {
                var pin = new MusicConsolidationArtifact(
                    Contained(root, value.Path),
                    value.Bytes,
                    value.Sha256,
                    1
                );
                await RequirePinAsync(pin, ct);
                return pin;
            }
        }
        if (
            captures.Select(capture => capture.Input.Uuid).Distinct().Count() != captures.Count
            || captures
                .Select(capture => capture.Receipt.RequestedUrl)
                .Distinct(StringComparer.Ordinal)
                .Count() != captures.Count
        )
        {
            throw new InvalidDataException(
                "GoSet acquisition contains duplicate revisions or URLs."
            );
        }
        return captures.ToArray();
    }

    private static List<Addition> BuildAdditions(
        GoSetCompositionSupplementRequest request,
        IReadOnlyList<CompositionProgramTarget> targets,
        Capture[] captures,
        MusicConsolidationArtifact requestPin
    )
    {
        var additions = new List<Addition>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Correspondences.Count; index++)
        {
            var proposal = request.Correspondences[index];
            var target =
                targets.SingleOrDefault(target => target.Context.Key == proposal.Target)
                ?? throw new InvalidDataException(
                    "Correspondence target is not in the frozen original context."
                );
            ValidateProof(proposal, target.Context, captures);
            var proposalOrigin = Origin(requestPin, "/correspondences/" + index);
            foreach (var occurrence in proposal.OccurrenceKeys)
            {
                var matches = captures
                    .SelectMany(capture =>
                        capture.Decoded.Setlists.SelectMany(
                            (setlist, setIndex) =>
                                setlist.Passages.Select(
                                    (passage, passageIndex) =>
                                        new
                                        {
                                            capture,
                                            setlist,
                                            setIndex,
                                            passage,
                                            passageIndex,
                                        }
                                )
                        )
                    )
                    .Where(row =>
                        row.passage.OccurrenceIdentity?.Identity
                            == new NormalizationResourceKey("go-set.net", "passage", occurrence)
                        && row.passage.SongIdentities.Any(identity =>
                            identity.Identity == proposal.ProviderSong
                        )
                        && row.passage.SongIdentities.Any(identity =>
                            identity.Identity
                            == new NormalizationResourceKey(
                                "go-set.net",
                                "song_slug",
                                proposal.ProviderSongSlug
                            )
                        )
                    )
                    .ToArray();
                if (
                    matches.Length != 1
                    || !used.Add(JsonSerializer.Serialize(proposal.Target, Json) + ":" + occurrence)
                )
                {
                    throw new InvalidDataException(
                        "Correspondence must select one original provider occurrence once."
                    );
                }
                var row = matches[0];
                var pointer = $"/setlists/{row.setIndex}/passages/{row.passageIndex}";
                var origin = Origin(row.capture.DecodedPin, pointer);
                var claims = row
                    .passage.PerformerAnnotations.Select(
                        (annotation, annotationIndex) =>
                            new CompositionSelectionTargetClaim(
                                "performance_annotation",
                                annotation.Kind,
                                annotation.Name?.Value,
                                JsonSerializer.SerializeToElement(annotation, AcquisitionJson),
                                Origin(
                                    row.capture.DecodedPin,
                                    pointer + "/performerAnnotations/" + annotationIndex
                                )
                            )
                    )
                    .ToList();
                if (claims.Count == 0)
                {
                    throw new InvalidDataException(
                        "The selected provider passage has no explicit performance attribution."
                    );
                }
                claims.Add(
                    new(
                        "target_source_correspondence",
                        null,
                        "unconfirmed development proposal",
                        JsonSerializer.SerializeToElement(
                            new
                            {
                                method = "codex_development_correspondence_proposal",
                                acceptedIdentity = false,
                                reviewAuthority = false,
                                proposal,
                                providerOccurrence = row.passage.OccurrenceIdentity,
                                providerSetlist = row.setlist.Origin.Item,
                                providerSongIdentities = row.passage.SongIdentities,
                                providerSiteUrl = "https://go-set.net/",
                            },
                            Json
                        ),
                        proposalOrigin
                    )
                );
                var title = row.passage.Title;
                var titleTexts =
                    title == null
                        ? Array.Empty<CompositionSelectionTargetText>()
                        :
                        [
                            new CompositionSelectionTargetText(
                                "title",
                                Hash(Encoding.UTF8.GetBytes(title.Value)),
                                title.Value,
                                Origin(row.capture.DecodedPin, pointer + "/title/value")
                            ),
                        ];
                additions.Add(
                    new(
                        proposal.Target,
                        new(
                            "native_passage",
                            origin,
                            null,
                            null,
                            null,
                            null,
                            null,
                            "go-set.net/show/" + row.setlist.Origin.Item.ProviderKey,
                            row.setlist.Ordered,
                            [proposal.Target.SongEntityKey],
                            titleTexts,
                            claims,
                            new[]
                            {
                                "catalog_recording_binding_not_asserted",
                                "development_correspondence_not_accepted",
                            }
                                .Concat(proposal.Conflicts)
                                .ToArray()
                        )
                    )
                );
            }
        }
        return additions;
    }

    private static void ValidateProof(
        GoSetTargetCorrespondenceProposal proposal,
        CompositionSelectionTargetContext context,
        Capture[] captures
    )
    {
        if (
            proposal.ProviderSong.Provider != "go-set.net"
            || proposal.ProviderSong.ResourceType != "song"
            || proposal.SourceEvidence.Count == 0
            || proposal.ProviderEvidence.Count == 0
            || proposal.OccurrenceKeys.Count == 0
            || string.IsNullOrWhiteSpace(proposal.Rationale)
        )
        {
            throw new InvalidDataException(
                "A proposed correspondence requires explicit source and provider evidence."
            );
        }
        foreach (var proof in proposal.SourceEvidence)
        {
            var text = context
                .Witnesses.SelectMany(witness => witness.Texts)
                .FirstOrDefault(text =>
                    text.Origin == proof.Origin && text.TextSha256 == proof.TextSha256
                );
            if (text == null || Hash(Encoding.UTF8.GetBytes(text.Value)) != proof.TextSha256)
            {
                throw new InvalidDataException(
                    "Correspondence source text is not owned by its original target."
                );
            }
            RequireQuote(text.Value, proof.StartCodePoint, proof.EndCodePoint, proof.Quote);
        }
        foreach (var proof in proposal.ProviderEvidence)
        {
            var capture =
                captures.SingleOrDefault(capture => capture.Receipt.RequestedUrl == proof.Url)
                ?? throw new InvalidDataException(
                    "Correspondence provider proof is outside its retained acquisition."
                );
            RequireQuote(capture.Html, proof.StartCodePoint, proof.EndCodePoint, proof.Quote);
        }
    }

    private static void RequireQuote(string text, int start, int end, string quote)
    {
        var points = text.EnumerateRunes().ToArray();
        if (
            start < 0
            || end <= start
            || end > points.Length
            || string.Concat(points[start..end].Select(point => point.ToString())) != quote
        )
        {
            throw new InvalidDataException(
                "Correspondence evidence does not reproduce its original Unicode span."
            );
        }
    }

    private static void AppendWitnesses(
        List<JsonObject> rows,
        List<JsonObject> texts,
        List<Addition> additions
    )
    {
        var knownTexts = texts
            .Select(text => text["sha256"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var addition in additions)
        {
            var row = rows.Single(row =>
                row["key"]!.Deserialize<CompositionSelectionTargetKey>(Json) == addition.Target
            );
            var node = JsonSerializer.SerializeToNode(addition.Witness, Json)!.AsObject();
            foreach (var text in node["texts"]!.AsArray())
            {
                var value = text!["value"]!.GetValue<string>();
                var sha = text["textSha256"]!.GetValue<string>();
                if (knownTexts.Add(sha))
                {
                    texts.Add(new JsonObject { ["sha256"] = sha, ["value"] = value });
                }
                text.AsObject().Remove("value");
            }
            row["witnesses"]!.AsArray().Add(node);
            var coverage = row["coverage"]!.AsObject();
            coverage["setlistWitnesses"] = coverage["setlistWitnesses"]!.GetValue<long>() + 1;
        }
    }

    private void RememberOriginalPins(CompositionSelectionTargetContextManifest original)
    {
        foreach (var artifact in original.SourceManifests.Concat(original.WitnessChunks))
        {
            pins.TryAdd(
                Path.GetFullPath(artifact.Path),
                new(artifact.Path, artifact.Bytes, artifact.Sha256, artifact.Rows)
            );
        }
        foreach (
            var artifact in new[]
            {
                original.SelectionPlan,
                original.HypothesisManifest,
                original.MusicInputManifest,
                original.MusicOutputManifest,
                original.Catalogue,
                original.Normalization,
            }
        )
        {
            pins.TryAdd(Path.GetFullPath(artifact.Path), artifact);
        }
    }

    private async Task RequirePinAsync(MusicConsolidationArtifact expected, CancellationToken ct)
    {
        var actual = await PinAsync(expected.Path, ct);
        if (actual.Bytes != expected.Bytes || actual.Sha256 != expected.Sha256)
        {
            throw new InvalidDataException("GoSet supplement source differs from its frozen pin.");
        }
    }

    private async Task<MusicConsolidationArtifact> PinAsync(string path, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        await using var stream = File.OpenRead(path);
        var pin = new MusicConsolidationArtifact(
            path,
            stream.Length,
            Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant(),
            1
        );
        if (
            pins.TryGetValue(path, out var existing)
            && (pin.Bytes != existing.Bytes || pin.Sha256 != existing.Sha256)
        )
        {
            throw new InvalidDataException("A GoSet supplement source changed while being read.");
        }
        pins[path] = pin;
        return pin;
    }

    private async Task RecheckPinsAsync(CancellationToken ct)
    {
        foreach (var pin in pins.Values.ToArray())
        {
            await RequirePinAsync(pin, ct);
        }
    }

    private static async Task<List<JsonObject>> ReadRowsAsync(string path, CancellationToken ct)
    {
        var rows = new List<JsonObject>();
        await foreach (
            var line in ShadowJsonLines.ReadTextAsync(
                path,
                32 * 1024 * 1024,
                MaximumLogicalBytes,
                ct
            )
        )
        {
            rows.Add(JsonNode.Parse(line)!.AsObject());
        }
        return rows;
    }

    private static CompositionSelectionTargetOrigin Origin(
        MusicConsolidationArtifact pin,
        string pointer
    ) => new(Path.GetDirectoryName(pin.Path)!, Path.GetFileName(pin.Path), 1, pin.Sha256, pointer);

    private static CompositionSelectionContextSourceArtifact Chunk(
        MusicConsolidationArtifact pin
    ) => new(pin.Path, pin.Bytes, pin.Sha256, 1, Path.GetDirectoryName(pin.Path));

    private static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (
            Path.IsPathRooted(relative)
            || !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || new FileInfo(path).LinkTarget != null
        )
        {
            throw new InvalidDataException(
                "GoSet artifacts must remain inside their original generation."
            );
        }
        return path;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Capture(
        GoSetCompositionCapture Receipt,
        NormalizationInputRevision Input,
        ProviderDecodeResult Decoded,
        MusicConsolidationArtifact DecodedPin,
        string Html
    );

    private sealed record Addition(
        CompositionSelectionTargetKey Target,
        CompositionSelectionTargetWitness Witness
    );

    private sealed class Output(string root, long floor)
    {
        private long written;

        public async Task<MusicConsolidationArtifact> WriteRowsAsync(
            string name,
            IReadOnlyList<JsonObject> rows,
            CancellationToken ct
        )
        {
            using var buffer = new MemoryStream();
            long logical = 0;
            await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, true))
            {
                foreach (var row in rows)
                {
                    var bytes = Encoding.UTF8.GetBytes(row.ToJsonString(Json) + "\n");
                    logical += bytes.LongLength;
                    if (logical > MaximumLogicalBytes)
                    {
                        throw new IOException(
                            "GoSet supplemented context exceeds its logical byte bound."
                        );
                    }
                    await gzip.WriteAsync(bytes, ct);
                }
            }
            return (await WriteAsync(name, buffer.ToArray(), ct)) with { Rows = rows.Count };
        }

        public async Task<MusicConsolidationArtifact> WriteAsync(
            string name,
            byte[] bytes,
            CancellationToken ct
        )
        {
            if (
                written + bytes.LongLength > MaximumOutputBytes
                || new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace
                    < floor + bytes.LongLength
            )
            {
                throw new IOException("GoSet supplement reached its output or free-space bound.");
            }
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(root, name);
            await File.WriteAllBytesAsync(path + ".partial", bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(path + ".partial", path);
            written += bytes.LongLength;
            return new(name, bytes.LongLength, Hash(bytes), 1);
        }
    }
}
