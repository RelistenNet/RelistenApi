using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Relisten.Normalization.Batch;

/// <summary>
/// A retained catalog row and its actual local extraction, when available. The
/// shadow runner owns both JSON documents until the adapter's results are written.
/// Adapters must clone any JSON they retain across rows.
/// </summary>
public sealed record ShadowCatalogInput(
    JsonElement Record,
    JsonElement? Extraction,
    ShadowCatalogArtist Artist,
    string Provider,
    JsonElement? ExtractionError = null
)
{
    public string Kind => Record.GetProperty("kind").GetString()!;
    public JsonElement Row => Record.GetProperty("row");
    public Guid Uuid => Row.GetProperty("uuid").GetGuid();
    public string Key => Kind + ":" + Uuid.ToString("D");
}

public sealed record ShadowCatalogArtist(
    long Id,
    Guid Uuid,
    string Name,
    IReadOnlyList<string> ConfiguredProviders
);

/// <summary>
/// Adds inspection and accounting to existing domain proposals. Proposed means a
/// shadow interpretation, never accepted production authority. Entity keys join
/// related results in the reader; they must not imply identity from name equality.
/// </summary>
public sealed record ShadowCatalogResult(
    string Domain,
    string InputKey,
    string InputKind,
    Guid InputUuid,
    Guid ArtistUuid,
    string ArtistName,
    string Provider,
    string Outcome,
    string? Reason,
    string Method,
    IReadOnlyList<string> EntityKeys,
    object? Proposal,
    object Evidence
)
{
    public static ShadowCatalogResult For(
        ShadowCatalogInput input,
        string domain,
        string outcome,
        string method,
        object? proposal,
        object evidence,
        string? reason = null,
        IReadOnlyList<string>? entityKeys = null
    ) =>
        new(
            domain,
            input.Key,
            input.Kind,
            input.Uuid,
            input.Artist.Uuid,
            input.Artist.Name,
            input.Provider,
            outcome,
            reason,
            method,
            entityKeys ?? [],
            proposal,
            evidence
        );
}

/// <summary>
/// A shadow-only adapter. Observe builds compact indexes in a first streaming
/// pass. Process must emit one outcome for each domain returned by DomainsFor.
/// Exceptions become failed input outcomes; they do not stop other adapters.
/// Network/model acquisition runs separately from these deterministic projections.
/// </summary>
public interface IShadowCatalogAdapter
{
    string Version { get; }
    IReadOnlyList<string> DomainsFor(string inputKind);
    void Observe(JsonElement record);
    IEnumerable<ShadowCatalogResult> Process(ShadowCatalogInput input);
}

public static class ShadowCatalogIdentity
{
    /// <summary>Creates a stable shadow reference without changing any legacy UUID.</summary>
    public static string Key(string domain, string originalIdentity)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes("relisten-shadow-v1\n" + domain + "\n" + originalIdentity)
        );
        return domain + ":" + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}

/// <summary>Outcome scope is distinct from context observation and snapshot provenance.</summary>
internal enum ShadowCatalogProcessingScope
{
    AllApplicableInputs,
    ProviderInputsOnly,
    SourceObservationsOnly,
}

internal static class ShadowCatalogProcessingScopes
{
    internal static bool Includes(this ShadowCatalogProcessingScope scope, string kind) =>
        scope switch
        {
            ShadowCatalogProcessingScope.ProviderInputsOnly => kind == "provider_input",
            ShadowCatalogProcessingScope.SourceObservationsOnly => kind == "source_sample",
            _ => true,
        };

    internal static string ContractName(this ShadowCatalogProcessingScope scope) =>
        scope switch
        {
            ShadowCatalogProcessingScope.ProviderInputsOnly => "provider_inputs_only",
            ShadowCatalogProcessingScope.SourceObservationsOnly => "source_actor_observations",
            _ => "all_applicable_inputs",
        };
}
