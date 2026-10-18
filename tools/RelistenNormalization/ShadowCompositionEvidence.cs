using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A source-reviewed work correspondence is input to shadow proposals, never publication
/// authority. Literal writer roles stay with their cited source; external people are not
/// converted to canonical person UUIDs.
/// </summary>
internal sealed record ShadowCompositionProof(
    Guid CatalogUuid,
    Guid ArtistUuid,
    string Title,
    Guid WorkMbid,
    string WorkTitle,
    string ClaimId,
    string ManifestSha256,
    JsonElement Evidence,
    JsonElement Writers,
    JsonElement? ProviderWriterRoles,
    string Rationale,
    string CompetingIdentityAssessment,
    IReadOnlyList<string> RemainingGaps
);

/// <summary>
/// Reads a caller-pinned, offline source-adjudication packet. Its original source files and
/// exact citations are authenticated before a correspondence enters the compact index.
/// This reader does not decide which musical work a title denotes.
/// </summary>
internal sealed class ShadowCompositionEvidence
{
    private readonly Dictionary<string, JsonElement> sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> catalog = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> packetDirectories = new(StringComparer.Ordinal);
    private JsonElement sourceRegistry;

    internal string? ManifestSha256 { get; private set; }
    internal IReadOnlyList<ShadowCompositionProof> Proofs { get; private set; } = [];
    internal Dictionary<
        (Guid Artist, string Title),
        IReadOnlyList<ShadowCompositionCandidate>
    > CandidateContexts { get; } = new();

    internal ShadowCompositionEvidence(string? pinnedManifest)
    {
        if (pinnedManifest == null)
        {
            return;
        }

        var separator = pinnedManifest.LastIndexOf("#sha256=", StringComparison.Ordinal);
        if (separator <= 0 || pinnedManifest.Length - separator != 72)
        {
            throw new ArgumentException(
                "Composition evidence requires <manifest-path>#sha256=<64 hexadecimal digits>."
            );
        }

        var path = Path.GetFullPath(pinnedManifest[..separator]);
        var expected = pinnedManifest[(separator + 8)..];
        if (!expected.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The composition evidence digest is not hexadecimal.");
        }

        var bytes = File.ReadAllBytes(path);
        ManifestSha256 = Hash(bytes);
        if (!ManifestSha256.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Composition evidence manifest differs from the supplied digest."
            );
        }

        var directory = Path.GetDirectoryName(path)!;
        using var manifest = JsonDocument.Parse(bytes);
        if (
            manifest.RootElement.GetProperty("version").GetString()
            != "composition120-primary-adjudication-v1"
        )
        {
            throw new InvalidDataException("Unsupported composition evidence packet version.");
        }

        VerifyInventory(
            directory,
            manifest.RootElement.GetProperty("files"),
            "source-pins.json",
            "claim-primary-adjudications.jsonl"
        );
        ReadSources(directory);
        ReadProofs(directory);
        ReadRetainedCandidates();
    }

    private void ReadSources(string directory)
    {
        using var pins = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(directory, "source-pins.json"))
        );
        foreach (var packet in pins.RootElement.GetProperty("packets").EnumerateObject())
        {
            var packetDirectory = Path.GetFullPath(
                packet.Value.GetProperty("directory").GetString()!
            );
            var body = File.ReadAllBytes(Path.Combine(packetDirectory, "manifest.json"));
            if (Hash(body) != packet.Value.GetProperty("manifestSha256").GetString())
            {
                throw new InvalidDataException("An original composition source manifest changed.");
            }

            using var manifest = JsonDocument.Parse(body);
            VerifyInventory(packetDirectory, manifest.RootElement.GetProperty("files"));
            packetDirectories.Add(packet.Name, packetDirectory);
        }

        sourceRegistry = pins.RootElement.GetProperty("citationSources").Clone();
    }

    private void ReadProofs(string directory)
    {
        var proofs = new List<ShadowCompositionProof>();
        foreach (
            var line in File.ReadLines(Path.Combine(directory, "claim-primary-adjudications.jsonl"))
        )
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            ValidateCitations(row);
            if (
                row.GetProperty("status").GetString()
                != "source_supported_composition_correspondence_draft"
            )
            {
                continue;
            }

            var work = row.GetProperty("workCorrespondenceDraft");
            if (
                work.GetProperty("provider").GetString() != "musicbrainz.org"
                || work.GetProperty("resourceType").GetString() != "work"
                || work.GetProperty("acceptedForPublication").GetBoolean()
                || row.GetProperty("recordingIdentityClaimed").GetBoolean()
                || row.GetProperty("publicationPerformed").GetBoolean()
                || row.GetProperty("negativeMatchAsserted").GetBoolean()
                || row.GetProperty("canonicalIdsMinted").GetArrayLength() != 0
            )
            {
                throw new InvalidDataException(
                    "A composition draft exceeds its source-only scope."
                );
            }

            var mbid = Guid.Parse(work.GetProperty("providerKey").GetString()!);
            var evidence = row.GetProperty("primaryEvidence");
            var workTitles = evidence
                .EnumerateArray()
                .Where(citation => citation.TryGetProperty("value", out _))
                .Select(citation => WorkTitle(citation.GetProperty("value"), mbid))
                .Where(title => title != null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (mbid == Guid.Empty || workTitles.Length != 1 || evidence.GetArrayLength() == 0)
            {
                throw new InvalidDataException(
                    "A proposed work lacks an exact cited provider work target."
                );
            }

            var dependencies = row.GetProperty("sourceDependencyGroups");
            proofs.Add(
                new(
                    dependencies.GetProperty("catalogOriginalUuid").GetGuid(),
                    dependencies.GetProperty("artistUuid").GetGuid(),
                    row.GetProperty("literalTitle").GetString()!,
                    mbid,
                    workTitles[0]!,
                    row.GetProperty("claimId").GetString()!,
                    ManifestSha256!,
                    evidence.Clone(),
                    row.GetProperty("primaryWriterClaims").Clone(),
                    row.TryGetProperty("providerWriterRoleAssertions", out var roles)
                        ? roles.Clone()
                        : null,
                    row.GetProperty("rationale").GetString()!,
                    row.GetProperty("competingIdentityAssessment").GetString()!,
                    row.GetProperty("remainingGaps")
                        .EnumerateArray()
                        .Select(value => value.GetString()!)
                        .ToArray()
                )
            );
        }

        Proofs = proofs;
    }

    private void ReadRetainedCandidates()
    {
        if (!packetDirectories.TryGetValue("bridge", out var directory))
        {
            sources.Clear();
            catalog.Clear();
            return;
        }

        // Query ownership is retrieval context only. Even a complete retained first page
        // cannot turn title/performer similarity into a catalog-to-work correspondence.
        var candidates = new Dictionary<string, ShadowCompositionCandidate>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path.Combine(directory, "work-candidates.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            ValidateCitations(row);
            var work = row.GetProperty("providerWorkIdentity");
            if (
                work.GetProperty("provider").GetString() != "musicbrainz.org"
                || work.GetProperty("resourceType").GetString() != "work"
            )
            {
                throw new InvalidDataException("Unsupported retained work candidate namespace.");
            }

            candidates.Add(
                row.GetProperty("candidateId").GetString()!,
                new(
                    Guid.Parse(work.GetProperty("providerKey").GetString()!),
                    row.GetProperty("literalTitle").GetString()!,
                    row.GetProperty("evidence").Clone(),
                    row.GetProperty("writerRoleAssertions").Clone(),
                    "literal_provider_retrieval_assertion_only"
                )
            );
        }

        foreach (var line in File.ReadLines(Path.Combine(directory, "claims.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var key = (
                row.GetProperty("sourceDependencyGroups").GetProperty("artistUuid").GetGuid(),
                ShadowCompositionAdapter.NormalizeTitle(
                    row.GetProperty("literalTitle").GetString()!
                )
            );
            var owned = row.GetProperty("candidateWorkAssertionRefs")
                .EnumerateArray()
                .Select(reference => candidates[reference.GetString()!])
                .ToArray();
            CandidateContexts[key] = CandidateContexts.TryGetValue(key, out var previous)
                ? previous.Concat(owned).Distinct().ToArray()
                : owned;
        }

        sources.Clear();
        catalog.Clear();
    }

    private void ValidateCitations(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                ValidateCitations(child);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (
                value.TryGetProperty("jsonPointer", out var pointer)
                && (
                    value.TryGetProperty("sourceId", out var source)
                    || value.TryGetProperty("inputRevisionUuid", out source)
                )
            )
            {
                var observed = At(ReadSource(source.GetString()!), pointer.GetString()!);
                if (
                    value.TryGetProperty("value", out var expected)
                    && !JsonElement.DeepEquals(observed, expected)
                )
                {
                    throw new InvalidDataException("A composition citation value changed.");
                }

                if (
                    value.TryGetProperty("quote", out var quote)
                    && quote.ValueKind == JsonValueKind.String
                )
                {
                    var start = value.GetProperty("startCodePoint").GetInt32();
                    var end = value.GetProperty("endCodePoint").GetInt32();
                    var runes = observed.GetString()!.EnumerateRunes().ToArray();
                    if (
                        start < 0
                        || end < start
                        || end > runes.Length
                        || string.Concat(
                            runes.Skip(start).Take(end - start).Select(rune => rune.ToString())
                        ) != quote.GetString()
                    )
                    {
                        throw new InvalidDataException(
                            "A composition citation's Unicode range changed."
                        );
                    }
                }
            }

            foreach (var child in value.EnumerateObject())
            {
                ValidateCitations(child.Value);
            }
        }
    }

    private JsonElement ReadSource(string key)
    {
        if (sources.TryGetValue(key, out var saved))
        {
            return saved;
        }

        var location = sourceRegistry.GetProperty(key);
        var directory = packetDirectories[location.GetProperty("packet").GetString()!];
        if (location.TryGetProperty("inputRevisionUuid", out var inputUuid))
        {
            if (catalog.Count == 0)
            {
                foreach (
                    var line in File.ReadLines(Path.Combine(directory, "original-documents.jsonl"))
                )
                {
                    using var wrapper = JsonDocument.Parse(line);
                    var input = wrapper.RootElement.GetProperty("input");
                    var body = Convert.FromBase64String(input.GetProperty("payload").GetString()!);
                    if (Hash(body) != input.GetProperty("sha256").GetString())
                    {
                        throw new InvalidDataException("A catalog original payload changed.");
                    }

                    var uuid = input.GetProperty("uuid").GetString()!;
                    if (
                        sourceRegistry.TryGetProperty(uuid, out var registered)
                        && registered.TryGetProperty("payloadSha256", out var expected)
                        && expected.GetString() != Hash(body)
                    )
                    {
                        throw new InvalidDataException(
                            "A catalog original differs from its registered payload digest."
                        );
                    }

                    using var document = JsonDocument.Parse(body);
                    catalog.Add(uuid, document.RootElement.Clone());
                }
            }

            saved = catalog[inputUuid.GetString()!];
        }
        else
        {
            var path = ContainedPath(directory, location.GetProperty("path").GetString()!);
            var body = File.ReadAllBytes(path);
            if (Hash(body) != location.GetProperty("sha256").GetString())
            {
                throw new InvalidDataException("A cited composition document changed.");
            }

            var text = Encoding.UTF8.GetString(body);
            if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            {
                using var document = JsonDocument.Parse(body);
                saved = document.RootElement.Clone();
            }
            else
            {
                saved = JsonSerializer.SerializeToElement(text);
            }
        }

        sources.Add(key, saved);
        return saved;
    }

    private static string? WorkTitle(JsonElement value, Guid work)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty("work", out var target))
        {
            return WorkTitle(target, work);
        }

        return
            value.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && Guid.TryParse(id.GetString(), out var parsed)
            && parsed == work
            && value.TryGetProperty("title", out var title)
            ? title.GetString()
            : null;
    }

    private static JsonElement At(JsonElement value, string pointer)
    {
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            var part = segment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            value =
                value.ValueKind == JsonValueKind.Array
                    ? value[int.Parse(part)]
                    : value.GetProperty(part);
        }

        return value;
    }

    private static void VerifyInventory(
        string directory,
        JsonElement inventory,
        params string[] requiredFiles
    )
    {
        var entries =
            inventory.ValueKind == JsonValueKind.Array
                ? inventory
                    .EnumerateArray()
                    .Select(row => (row.GetProperty("path").GetString()!, row))
                    .ToArray()
                : inventory.EnumerateObject().Select(row => (row.Name, row.Value)).ToArray();
        if (requiredFiles.Any(required => !entries.Any(entry => entry.Item1 == required)))
        {
            throw new InvalidDataException(
                "A required composition evidence file is absent from its inventory."
            );
        }

        foreach (var (relative, metadata) in entries)
        {
            using var stream = File.OpenRead(ContainedPath(directory, relative));
            if (
                stream.Length != metadata.GetProperty("bytes").GetInt64()
                || Convert.ToHexStringLower(SHA256.HashData(stream))
                    != metadata.GetProperty("sha256").GetString()
            )
            {
                throw new InvalidDataException("A composition evidence inventory file changed.");
            }
        }
    }

    private static string ContainedPath(string directory, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (
            Path.IsPathRooted(relative)
            || !path.StartsWith(
                Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "Composition evidence path escapes its source directory."
            );
        }

        return path;
    }

    private static string Hash(byte[] body) => Convert.ToHexStringLower(SHA256.HashData(body));
}
