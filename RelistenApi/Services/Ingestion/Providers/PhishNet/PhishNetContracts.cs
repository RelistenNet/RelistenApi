namespace Relisten.Ingestion.Providers.PhishNet;

/// <summary>Show metadata used for enrichment, without the vendor's arbitrary created_at field.</summary>
public sealed record PhishNetShowInput(string Date, string? SetlistNotes);

public sealed record PhishNetSourceInput(PhishNetShowInput? Show);

public sealed record PhishNetItemResult(CatalogSourceId Source, ItemDisposition Disposition);
