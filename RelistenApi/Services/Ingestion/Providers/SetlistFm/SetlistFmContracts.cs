namespace Relisten.Ingestion.Providers.SetlistFm;

public sealed record SetlistFmPage(int Number);

/// <summary>The persisted setlist identity, including on a timestamp-based no-op.</summary>
public sealed record SetlistFmItemResult(
    SetlistFmId Id,
    CatalogSetlistId Setlist,
    ItemDisposition Disposition
);
