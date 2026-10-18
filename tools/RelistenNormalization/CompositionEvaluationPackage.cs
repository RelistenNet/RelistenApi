using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Authenticates a closed prediction package before projections run. Literal citations prove
/// what the retained bytes say; the exporter does not certify their identity interpretation.
/// </summary>
internal sealed class CompositionEvaluationPackage
{
    internal const string Version = "composition-evaluation-package-v1";
    internal const int MaximumTasks = 4096;
    internal const int MaximumRecords = 16_384;
    internal const int MaximumComparisons = 100_000;
    internal const int MaximumLineBytes = 12 * 1024 * 1024;
    private const int MaximumPackageBytes = 128 * 1024 * 1024;
    private static readonly string[] Names =
    [
        "documents.jsonl",
        "records.jsonl",
        "universes.jsonl",
        "tasks.jsonl",
    ];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, false) },
    };
    private readonly string directory;
    private readonly Dictionary<string, byte[]> originals;

    private CompositionEvaluationPackage(
        string directory,
        Dictionary<string, byte[]> originals,
        string hash,
        CompositionEvaluationPackageManifest manifest,
        IReadOnlyDictionary<Guid, CompositionEvaluationDocument> documents,
        IReadOnlyDictionary<string, CompositionEvaluationRecord> records,
        IReadOnlyDictionary<string, CompositionEvaluationUniverse> universes,
        IReadOnlyList<CompositionEvaluationTask> tasks
    )
    {
        this.directory = directory;
        this.originals = originals;
        ManifestHash = hash;
        Manifest = manifest;
        Documents = documents;
        Records = records;
        Universes = universes;
        Tasks = tasks;
    }

    internal string ManifestHash { get; }
    internal CompositionEvaluationPackageManifest Manifest { get; }
    internal IReadOnlyDictionary<Guid, CompositionEvaluationDocument> Documents { get; }
    internal IReadOnlyDictionary<string, CompositionEvaluationRecord> Records { get; }
    internal IReadOnlyDictionary<string, CompositionEvaluationUniverse> Universes { get; }
    internal IReadOnlyList<CompositionEvaluationTask> Tasks { get; }

    internal static async Task<CompositionEvaluationPackage> ReadAsync(
        string directory,
        string expectedHash,
        CancellationToken ct
    )
    {
        var originals = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var manifestBytes = await ReadBounded(
            Path.Combine(directory, "manifest.json"),
            1024 * 1024,
            ct
        );
        Require(
            IsHash(expectedHash) && Hash(manifestBytes) == expectedHash,
            "The package manifest does not match its external SHA-256."
        );
        var manifest = Read<CompositionEvaluationPackageManifest>(manifestBytes);
        Require(
            manifest.Version == Version
                && manifest.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Names),
            "Use the exact four-file composition evaluation package."
        );
        originals.Add("manifest.json", manifestBytes);
        long total = manifestBytes.Length;
        foreach (var name in Names)
        {
            var declared =
                manifest.Files[name]
                ?? throw new InvalidDataException("A manifest file receipt cannot be null.");
            Require(
                declared.Bytes >= 0
                    && declared.Bytes <= MaximumPackageBytes - total
                    && IsHash(declared.Sha256),
                "The package exceeds 128 MiB or has an invalid file receipt."
            );
            total += declared.Bytes;
            var bytes = await ReadBounded(
                Path.Combine(directory, name),
                checked((int)declared.Bytes),
                ct
            );
            Require(
                bytes.LongLength == declared.Bytes && Hash(bytes) == declared.Sha256,
                "A package file differs from its frozen receipt."
            );
            originals.Add(name, bytes);
        }

        var documentRows = Rows<CompositionEvaluationDocument>(
            originals[Names[0]],
            manifest.Files[Names[0]],
            4096,
            ct
        );
        var recordRows = Rows<CompositionEvaluationRecord>(
            originals[Names[1]],
            manifest.Files[Names[1]],
            MaximumRecords,
            ct
        );
        var universeRows = Rows<CompositionEvaluationUniverse>(
            originals[Names[2]],
            manifest.Files[Names[2]],
            1024,
            ct
        );
        var tasks = Rows<CompositionEvaluationTask>(
            originals[Names[3]],
            manifest.Files[Names[3]],
            MaximumTasks,
            ct
        );
        Require(
            documentRows.Count > 0
                && recordRows.Count > 0
                && universeRows.Count > 0
                && tasks.Count > 0,
            "A package needs documents, records, universes and tasks."
        );
        Require(
            documentRows.Select(row => row.Input.Uuid).Distinct().Count() == documentRows.Count,
            "Duplicate input UUIDs are not permitted."
        );
        Require(
            recordRows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count()
                == recordRows.Count,
            "Duplicate record IDs are not permitted."
        );
        Require(
            universeRows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count()
                == universeRows.Count,
            "Duplicate universe IDs are not permitted."
        );
        Require(
            tasks.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() == tasks.Count,
            "Duplicate task IDs are not permitted."
        );
        var package = new CompositionEvaluationPackage(
            directory,
            originals,
            expectedHash,
            manifest,
            documentRows.ToDictionary(row => row.Input.Uuid),
            recordRows.ToDictionary(row => row.Id, StringComparer.Ordinal),
            universeRows.ToDictionary(row => row.Id, StringComparer.Ordinal),
            tasks
        );
        package.Validate(ct);
        return package;
    }

    private void Validate(CancellationToken ct)
    {
        ValidateDocuments(ct);
        ValidateRecords(ct);
        ValidateUniverses();
        ValidateTasks(ct);
    }

    private void ValidateDocuments(CancellationToken ct)
    {
        foreach (var document in Documents.Values)
        {
            ct.ThrowIfCancellationRequested();
            var input = document.Input;
            Require(
                input.Uuid != Guid.Empty
                    && input.Payload.Length <= 8 * 1024 * 1024
                    && Hash(input.Payload) == input.Sha256,
                "A retained document has an invalid UUID, size or payload hash."
            );
            Require(
                input.Resource != null
                    && Word(document.Origin)
                    && Word(input.Resource.Provider)
                    && Word(input.Resource.ResourceType)
                    && Word(input.Resource.ProviderKey)
                    && Word(input.CaptureVersion),
                "Retained documents require original resource/profile and origin metadata."
            );
            using var reader = NormalizationRetainedDocument.Open(input, ct);
        }
    }

    private void ValidateRecords(CancellationToken ct)
    {
        foreach (var record in Records.Values)
        {
            ct.ThrowIfCancellationRequested();
            Require(
                Word(record.Id)
                    && MusicDomain(record.Domain)
                    && record.Titles.Count <= 128
                    && record.ExternalIdentities.Count <= 128
                    && record.AuthorUuids.Count <= 128
                    && record.ContextEvidence.Count is > 0 and <= 256
                    && record.Titles.All(value => value != null)
                    && record.ExternalIdentities.All(value => value != null)
                    && record.ContextEvidence.All(value => value != null),
                "A record exceeds the music facts profile or lacks source context."
            );
            Require(
                record.Domain != IdentityMatchingDomain.Repertoire
                    || (
                        record.ArtistUuid.HasValue
                        && record.AuthorUuids.Count == 0
                        && !record.CompleteAuthorship
                    ),
                "Repertoire records require artist scope and cannot carry global authorship."
            );
            Require(
                record.Domain != IdentityMatchingDomain.Compositions
                    || (record.ArtistUuid == null && record.CompositionUuid == null),
                "Global composition records cannot carry repertoire ownership."
            );
            foreach (var title in record.Titles)
            {
                Require(
                    !string.IsNullOrWhiteSpace(title.Value),
                    "A title assertion cannot be blank."
                );
                Require(
                    Literal(title.Evidence) == title.Value,
                    "A title differs from its exact cited wording."
                );
            }
            foreach (var identity in record.ExternalIdentities)
            {
                var key = identity.Identity;
                Require(
                    Word(key.Provider)
                        && Word(key.ResourceType)
                        && Word(key.ProviderKey)
                        && Literal(identity.Evidence) == key.ProviderKey,
                    "An identity key differs from its source citation."
                );
            }
            foreach (var evidence in record.ContextEvidence)
            {
                ValidateCitation(evidence);
            }
        }
    }

    private void ValidateUniverses()
    {
        foreach (var universe in Universes.Values)
        {
            Require(
                Word(universe.Id)
                    && MusicDomain(universe.Domain)
                    && UniqueWords(universe.RecordIds)
                    && universe.Issues.Count <= 128,
                "A universe has duplicate records or invalid metadata."
            );
            Require(
                universe.RecordIds.All(id =>
                    Records.TryGetValue(id, out var record) && record.Domain == universe.Domain
                ),
                "A universe includes a missing or wrong-domain record."
            );
        }
    }

    private void ValidateTasks(CancellationToken ct)
    {
        long comparisons = 0;
        foreach (var task in Tasks)
        {
            ct.ThrowIfCancellationRequested();
            Require(
                Word(task.Id)
                    && MusicDomain(task.Domain)
                    && Records.TryGetValue(task.ClaimId, out var claim)
                    && claim.Domain == task.Domain
                    && Universes.TryGetValue(task.UniverseId, out var universe)
                    && universe.Domain == task.Domain,
                "A task has a missing or wrong-domain claim/universe."
            );
            var selected = Universes[task.UniverseId];
            comparisons += selected.RecordIds.Count;
            Require(
                comparisons <= MaximumComparisons
                    && !selected.RecordIds.Contains(task.ClaimId, StringComparer.Ordinal),
                "Task comparisons exceed 100,000 or include the claim itself."
            );
            var retrieval = task.Retrieval;
            Require(
                Enum.IsDefined(retrieval.State)
                    && UniqueWords(retrieval.CandidateIds)
                    && retrieval.Issues.Count <= 128
                    && retrieval.Evidence.Count <= 256
                    && retrieval.UnrepresentedIdentities.Count <= 4096,
                "Retrieval coverage exceeds its profile."
            );
            Require(
                retrieval.CandidateIds.All(id =>
                    selected.RecordIds.Contains(id, StringComparer.Ordinal)
                ),
                "A retrieved candidate is outside the independently frozen universe."
            );
            Require(
                retrieval.State != CompositionEvaluationRetrievalState.NotRun
                    || (
                        retrieval.CandidateIds.Count == 0
                        && retrieval.UnrepresentedIdentities.Count == 0
                        && retrieval.Evidence.Count == 0
                    ),
                "A retrieval that was not run cannot supply results."
            );
            foreach (var evidence in retrieval.Evidence)
            {
                ValidateCitation(evidence);
            }
            foreach (var identity in retrieval.UnrepresentedIdentities)
            {
                Require(
                    Literal(identity.Evidence) == identity.Identity.ProviderKey,
                    "An unrepresented retrieval identity differs from its original key."
                );
            }
            if (task.SearchSeed is { } seed)
            {
                _ = MusicBrainzCandidateQueryBuilder.BuildVariants(seed);
                ValidateSeed(seed);
            }
        }
    }

    private void ValidateSeed(MusicBrainzSearchSeed seed)
    {
        if (seed.Text is { } text)
        {
            Require(
                Literal(text.Evidence) == text.Value,
                "Search wording differs from its original citation."
            );
        }
        foreach (var artist in seed.RelatedArtists)
        {
            if (artist.Name is { } name)
            {
                Require(
                    Literal(name.Evidence) == name.Value,
                    "Search artist wording differs from its citation."
                );
            }
            if (artist.Role is { } role)
            {
                Require(
                    Literal(role.Evidence) == role.Value,
                    "Search role wording differs from its citation."
                );
            }
            if (artist.Identity is { } identity)
            {
                Require(
                    Literal(identity.Evidence) == identity.Identity.ProviderKey,
                    "Search artist identity differs from its citation."
                );
            }
        }
    }

    private void ValidateCitation(NormalizedEvidenceReference evidence)
    {
        Require(
            Documents.TryGetValue(evidence.InputRevisionUuid, out var input),
            "A citation refers to an absent retained document."
        );
        using var document = NormalizationRetainedDocument.Open(input!.Input);
        document.ValidateEvidence(evidence);
    }

    private string Literal(NormalizedEvidenceReference evidence)
    {
        Require(
            Documents.TryGetValue(evidence.InputRevisionUuid, out var input),
            "A citation refers to an absent retained document."
        );
        using var document = NormalizationRetainedDocument.Open(input!.Input);
        document.ValidateEvidence(evidence);
        if (evidence.Quote != null)
        {
            return evidence.Quote;
        }
        var value = document.Resolve(evidence.JsonPointer);
        Require(
            value.ValueKind == JsonValueKind.String,
            "A literal assertion must cite a retained string."
        );
        return value.GetString()!;
    }

    internal async Task RecheckAsync(CancellationToken ct)
    {
        foreach (var (name, bytes) in originals)
        {
            var current = await ReadBounded(Path.Combine(directory, name), bytes.Length, ct);
            Require(
                current.AsSpan().SequenceEqual(bytes),
                "A frozen package file changed during evaluation."
            );
        }
    }

    internal byte[] ManifestBytes => originals["manifest.json"];

    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Word(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 4096;

    private static bool MusicDomain(IdentityMatchingDomain domain) =>
        domain is IdentityMatchingDomain.Compositions or IdentityMatchingDomain.Repertoire;

    private static bool UniqueWords(IReadOnlyList<string> values) =>
        values.Count <= MaximumRecords
        && values.All(Word)
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }

    private static async Task<byte[]> ReadBounded(string path, int limit, CancellationToken ct)
    {
        Require(
            new FileInfo(path).LinkTarget == null,
            "Package files must be regular retained files, not symbolic links."
        );
        await using var stream = File.OpenRead(path);
        Require(stream.Length <= limit, "An input exceeds its declared byte bound.");
        var bytes = new byte[checked((int)stream.Length + 1)];
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
        Require(read < bytes.Length, "An input grew during its bounded read.");
        return bytes.AsSpan(0, read).ToArray();
    }

    private static IReadOnlyList<T> Rows<T>(
        byte[] bytes,
        CompositionEvaluationFile receipt,
        int maximumRows,
        CancellationToken ct
    )
    {
        var result = new List<T>();
        var text = Utf8.GetString(bytes);
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var line = lines[index].TrimEnd('\r');
            if (index == lines.Length - 1 && line.Length == 0)
            {
                break;
            }
            Require(
                line.Length > 0
                    && Utf8.GetByteCount(line) <= MaximumLineBytes
                    && result.Count < maximumRows,
                "A package JSONL file exceeds its line/row profile or contains an empty row."
            );
            result.Add(Read<T>(Utf8.GetBytes(line)));
        }
        Require(result.Count == receipt.Rows, "A package row count differs from its receipt.");
        return result;
    }

    private static T Read<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions { MaxDepth = 64 }
            );
            RejectDuplicates(document.RootElement);
            return document.RootElement.Deserialize<T>(Json)
                ?? throw new InvalidDataException("A package value cannot be null.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "A package value violates its closed JSON contract.",
                error
            );
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Require(
                    names.Add(property.Name),
                    "A package object contains duplicate properties."
                );
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray())
            {
                RejectDuplicates(child);
            }
        }
    }
}
