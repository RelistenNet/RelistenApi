using System.Text.Json.Serialization;
using Relisten.Import;

namespace Relisten.Ingestion.Providers.Jerry;

/// <summary>A fixed mirror index selection retained across workflow continuation.</summary>
public sealed record JerrySelection(
    DiscoveredItem<JerryShowId, JerryGarciaComImporter.FileMetaObject>[] Items
);

[JsonConverter(typeof(JsonStringEnumConverter<JerrySkipReason>))]
public enum JerrySkipReason
{
    MissingShowDetails,
    MissingCatalogSetlist,
}

/// <summary>Skipped pages explain why no setlist was applied; successful pages include the catalog identity.</summary>
public sealed record JerryItemResult(
    JerryShowId Id,
    CatalogSetlistId? Setlist,
    ItemDisposition Disposition,
    JerrySkipReason? SkipReason = null
);
