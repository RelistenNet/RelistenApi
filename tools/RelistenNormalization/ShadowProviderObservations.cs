using System.Security.Cryptography;
using System.Text.Json;
using Relisten.Api.Models.Normalization;

namespace Relisten.Normalization.Batch;

/// <summary>
/// Decodes retained provider inputs for shadow domains. Local roots are supporting evidence,
/// selected by exact directory tuple. Historical catalog links come only from internally
/// consistent admitted HTTP/Local bindings; this reader creates no current catalog receipts.
/// </summary>
public sealed class ShadowProviderObservations
{
    public const string Version =
        "shadow-provider-observations-v1/" + NormalizationProviderDecoder.Version;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<
        Guid,
        (NormalizationInputRevision Input, ProviderDecodeResult Decoded)
    > roots = [];
    private readonly HashSet<Guid> invalidRoots = [];

    /// <summary>Remembers supporting roots once. Multiple domain adapters may observe the same row.</summary>
    public void Observe(JsonElement record)
    {
        if (!record.TryGetProperty("kind", out var kind) || kind.GetString() != "local_root_input")
        {
            return;
        }
        var row = Object(record, "row");
        if (
            row == null
            || Uuid(row.Value, "uuid") is not { } uuid
            || uuid == Guid.Empty
            || invalidRoots.Contains(uuid)
        )
        {
            return;
        }
        try
        {
            var input = Revision(record, uuid);
            if (roots.TryGetValue(uuid, out var prior))
            {
                if (prior.Input.Sha256 != input.Sha256 || prior.Input.Resource != input.Resource)
                {
                    roots.Remove(uuid);
                    invalidRoots.Add(uuid);
                }
                return;
            }
            roots.Add(
                uuid,
                (
                    input,
                    NormalizationLocalRecordingSelection.DecodeRoot(
                        input,
                        new NormalizationProviderDecoder()
                    )
                )
            );
        }
        catch (Exception error)
            when (error
                    is JsonException
                        or ArgumentException
                        or InvalidDataException
                        or NotSupportedException
                        or KeyNotFoundException
                        or InvalidOperationException
            )
        {
            // An unsafe root affects only its directories. A fresh replay can retry its
            // original pair without turning unrelated native inputs into preparation failures.
            roots.Remove(uuid);
            invalidRoots.Add(uuid);
        }
    }

    /// <summary>
    /// Returns original decoded observations, including unbound recordings. Missing Local
    /// support is an explicit issue; its directory facts remain usable. Domain adapters own
    /// extraction and must validate consumed evidence against Input or SupportingRootInput.
    /// </summary>
    public ShadowProviderObservationSet Read(ShadowCatalogInput input)
    {
        var revision = Revision(input.Record, input.Uuid);
        var decoded = new NormalizationProviderDecoder().Decode(revision);
        var issues = new List<ShadowProviderObservationIssue>();
        if (
            revision.Resource.Provider == "local"
            && revision.Resource.ResourceType == "metadata_root"
        )
        {
            // A root is an inventory of supporting accounts, not one recording. Admitted
            // Local work arrives as directory inputs plus a local_root_input observation.
            issues.Add(new("local_root_requires_directory_input"));
            return new(revision, decoded, [], issues);
        }
        var recordings = decoded
            .Recordings.Select(recording =>
                revision.Resource.Provider == "local"
                && revision.Resource.ResourceType == "directory_metadata"
                    ? LocalRecording(input, revision, recording, issues)
                    : HttpRecording(input, revision, recording)
            )
            .ToArray();
        return new(revision, decoded, recordings, issues);
    }

    private static NormalizationInputRevision Revision(JsonElement record, Guid expectedUuid)
    {
        var input =
            record.GetProperty("input").Deserialize<NormalizationInputRevision>(Json)
            ?? throw new InvalidDataException("Missing retained provider revision.");
        if (
            input.Uuid == Guid.Empty
            || input.Uuid != expectedUuid
            || !StringComparer.OrdinalIgnoreCase.Equals(
                input.Sha256,
                Convert.ToHexStringLower(SHA256.HashData(input.Payload))
            )
        )
        {
            throw new InvalidDataException("Retained provider revision identity or bytes differ.");
        }
        return input;
    }

    private static ShadowProviderRecordingObservation HttpRecording(
        ShadowCatalogInput input,
        NormalizationInputRevision revision,
        ProviderRecordingObservation recording
    )
    {
        if (
            !input.Record.TryGetProperty("retained_http", out var original)
            || !input.Record.TryGetProperty("catalog_binding", out var declared)
        )
        {
            return new(recording, null, null, null, null, "no_retained_catalog_binding", []);
        }
        var provenance = original.Deserialize<RetainedHttpProviderProvenance>(Json);
        var binding = declared.Deserialize<RetainedHttpCatalogBinding>(Json);
        if (
            provenance == null
            || binding == null
            || provenance.InputRevisionUuid != revision.Uuid
            || provenance.CatalogBinding != binding
            || provenance.HttpStatus != 200
            || provenance.Body.Bytes != revision.Payload.LongLength
            || !StringComparer.OrdinalIgnoreCase.Equals(provenance.Body.Sha256, revision.Sha256)
            || recording.Origin.Item != revision.Resource
        )
        {
            return new(recording, null, null, null, null, "invalid_retained_catalog_binding", []);
        }
        if (binding.Status != "bound")
        {
            return new(
                recording,
                null,
                null,
                null,
                null,
                binding.Reason ?? "retained_catalog_binding_unresolved",
                []
            );
        }
        var source = Object(input.Record, "source_context");
        var valid =
            binding.SourceUuid is { } sourceUuid
            && sourceUuid != Guid.Empty
            && binding.ExpectedSourceUuid == sourceUuid
            && source != null
            && Uuid(source.Value, "uuid") == sourceUuid
            && ArtistMatches(input, source.Value)
            && Text(source.Value, "upstream_identifier") == recording.Origin.Item.ProviderKey;
        if (!valid)
        {
            return new(recording, null, null, null, null, "retained_catalog_source_conflict", []);
        }
        var show = CatalogShow(input, source!.Value, binding.ShowUuid);
        return new(
            recording,
            null,
            null,
            binding.SourceUuid,
            show,
            show == null ? binding.ShowReason ?? "missing_or_conflicting_catalog_show" : null,
            []
        );
    }

    private ShadowProviderRecordingObservation LocalRecording(
        ShadowCatalogInput input,
        NormalizationInputRevision revision,
        ProviderRecordingObservation recording,
        List<ShadowProviderObservationIssue> issues
    )
    {
        if (!input.Record.TryGetProperty("retained_local", out var declared))
        {
            issues.Add(new("missing_retained_local_pair", recording.Origin));
            return new(recording, null, null, null, null, "missing_retained_local_pair", []);
        }
        var provenance =
            declared.Deserialize<RetainedLocalProviderProvenance>(Json)
            ?? throw new InvalidDataException("Missing retained Local pair.");
        var reason =
            provenance.InputRevisionUuid != revision.Uuid
            || provenance.Body.Bytes != revision.Payload.LongLength
            || !StringComparer.OrdinalIgnoreCase.Equals(provenance.Body.Sha256, revision.Sha256)
            || NormalizationProviderResources.LocalDirectory(provenance.Root, provenance.Directory)
                != revision.Resource
                ? "invalid_retained_local_pair"
                : null;
        if (reason == null && invalidRoots.Contains(provenance.RootInputRevisionUuid))
        {
            reason = "invalid_retained_local_root";
        }
        if (reason == null && !roots.ContainsKey(provenance.RootInputRevisionUuid))
        {
            reason = "missing_retained_local_root";
        }
        if (reason != null)
        {
            issues.Add(new(reason, recording.Origin));
            return new(recording, null, null, null, null, reason, []);
        }
        var root = roots[provenance.RootInputRevisionUuid];
        if (
            root.Input.Resource
                != NormalizationProviderResources.LocalRootForDirectory(revision.Resource)
            || root.Input.Payload.LongLength != provenance.RootBody.Bytes
            || !StringComparer.OrdinalIgnoreCase.Equals(
                root.Input.Sha256,
                provenance.RootBody.Sha256
            )
        )
        {
            issues.Add(new("invalid_retained_local_pair", recording.Origin));
            return new(recording, null, null, null, null, "invalid_retained_local_pair", []);
        }
        var accounts = root
            .Decoded.Recordings.Where(account => account.Origin.Item == revision.Resource)
            .ToArray();
        if (accounts.Length != 1)
        {
            reason = accounts.Length == 0 ? "directory_not_in_root" : "multiple_directory_accounts";
            issues.Add(new(reason, recording.Origin));
            return new(recording, root.Input, null, null, null, reason, []);
        }
        var binding = provenance.CatalogBinding;
        var accountOrigin = accounts[0].Origin;
        foreach (
            var issue in root.Decoded.Issues.Where(issue =>
                issue.Evidence.InputRevisionUuid == accountOrigin.Evidence.InputRevisionUuid
                && (
                    issue.Evidence.JsonPointer == accountOrigin.Evidence.JsonPointer
                    || issue.Evidence.JsonPointer.StartsWith(
                        accountOrigin.Evidence.JsonPointer + "/",
                        StringComparison.Ordinal
                    )
                )
            )
        )
        {
            issues.Add(new(issue.Code, accountOrigin, issue.Evidence));
        }
        var source = Object(input.Record, "source_context");
        var valid =
            binding.Status == "bound"
            && binding.SourceUuid is { } sourceUuid
            && sourceUuid != Guid.Empty
            && source != null
            && Uuid(source.Value, "uuid") == sourceUuid
            && ArtistMatches(input, source.Value)
            && binding.RootArtists.Count == 1
            && binding.RootArtists[0].ArtistUuid == input.Artist.Uuid
            && binding.RootArtists[0].ArtistId == input.Artist.Id
            && binding.RootArtists[0].Root == provenance.Root;
        var files = valid ? LocalFiles(recording, provenance) : [];
        if (!valid || files.Count == 0)
        {
            reason =
                binding.Status != "bound"
                    ? binding.Reason ?? "retained_catalog_binding_unresolved"
                    : "retained_local_catalog_conflict";
            return new(recording, root.Input, accounts[0], null, null, reason, []);
        }
        var show = CatalogShow(input, source!.Value, binding.ShowUuid);
        return new(
            recording,
            root.Input,
            accounts[0],
            binding.SourceUuid,
            show,
            show == null ? binding.ShowReason ?? "missing_or_conflicting_catalog_show" : null,
            files
        );
    }

    private static IReadOnlyList<ShadowProviderCatalogFile> LocalFiles(
        ProviderRecordingObservation recording,
        RetainedLocalProviderProvenance provenance
    ) =>
        provenance
            .CatalogBinding.Files.Where(file =>
                file.Status == "bound"
                && file.SourceUuid == provenance.CatalogBinding.SourceUuid
                && file.TrackUuid is { } track
                && track != Guid.Empty
                && file.SourceSetUuid is { } set
                && set != Guid.Empty
                && file.FileOrdinal >= 0
                && file.FileOrdinal < recording.Files.Count
                && file.MediaUrl
                    == RetainedLocalProviderInputs.MediaUrl(
                        provenance.Root,
                        provenance.Directory,
                        recording.Files[file.FileOrdinal]
                    )
            )
            .Select(file => new ShadowProviderCatalogFile(
                recording.Files[file.FileOrdinal].Origin,
                file.SourceUuid!.Value,
                file.TrackUuid!.Value,
                file.SourceSetUuid!.Value,
                file.MediaUrl!
            ))
            .ToArray();

    private static bool ArtistMatches(ShadowCatalogInput input, JsonElement source) =>
        input.Artist.Id > 0
        && input.Artist.Uuid != Guid.Empty
        && Number(source, "artist_id") == input.Artist.Id
        && Number(input.Row, "artist_id") == input.Artist.Id
        && Uuid(input.Row, "artist_uuid") == input.Artist.Uuid;

    private static Guid? CatalogShow(ShadowCatalogInput input, JsonElement source, Guid? expected)
    {
        var show = Object(input.Record, "show_context");
        return
            expected is { } uuid
            && uuid != Guid.Empty
            && show != null
            && Uuid(show.Value, "uuid") == uuid
            && Number(source, "show_id") == Number(show.Value, "id")
            && Number(show.Value, "artist_id") == input.Artist.Id
            ? uuid
            : null;
    }

    private static JsonElement? Object(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.Object
            ? found
            : null;

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String
            ? found.GetString()
            : null;

    private static long? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.Number
        && found.TryGetInt64(out var number)
            ? number
            : null;

    private static Guid? Uuid(JsonElement value, string name) =>
        value.TryGetProperty(name, out var found)
        && found.ValueKind == JsonValueKind.String
        && found.TryGetGuid(out var uuid)
            ? uuid
            : null;
}

/// <summary>Original provider decoding plus selected Local support; domain extraction remains separate.</summary>
public sealed record ShadowProviderObservationSet(
    NormalizationInputRevision Input,
    ProviderDecodeResult Decoded,
    IReadOnlyList<ShadowProviderRecordingObservation> Recordings,
    IReadOnlyList<ShadowProviderObservationIssue> Issues
);

/// <summary>Catalog UUIDs are optional historical associations, never identities of the input revision.</summary>
public sealed record ShadowProviderRecordingObservation(
    ProviderRecordingObservation Recording,
    NormalizationInputRevision? SupportingRootInput,
    ProviderRecordingObservation? SupportingRootAccount,
    Guid? CatalogSourceUuid,
    Guid? CatalogShowUuid,
    string? CatalogBindingReason,
    IReadOnlyList<ShadowProviderCatalogFile> CatalogFiles
);

/// <summary>One exact Local media association; generic provider files remain unbound without this proof.</summary>
public sealed record ShadowProviderCatalogFile(
    ProviderObservationOrigin Origin,
    Guid SourceUuid,
    Guid TrackUuid,
    Guid SourceSetUuid,
    string MediaUrl
);

public sealed record ShadowProviderObservationIssue(
    string Code,
    ProviderObservationOrigin? Origin = null,
    NormalizedEvidenceReference? Evidence = null
);
