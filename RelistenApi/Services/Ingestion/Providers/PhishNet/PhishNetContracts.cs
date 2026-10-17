namespace Relisten.Ingestion.Providers.PhishNet;

/// <summary>Show metadata used for enrichment, without the vendor's arbitrary created_at field.</summary>
public sealed record PhishNetShowInput(
    string Date,
    string? SetlistNotes,
    int? ShowId = null,
    string? Permalink = null
);

/// <summary>Ambiguous dates remain selected so operators can see the skipped outcome.</summary>
public sealed record PhishNetSourceInput(
    PhishNetShowInput? Show,
    string? SkipReason = null,
    string? SourceDate = null
);

public sealed record PhishNetItemResult(
    CatalogSourceId Source,
    ItemDisposition Disposition,
    string? SkipReason = null
);
