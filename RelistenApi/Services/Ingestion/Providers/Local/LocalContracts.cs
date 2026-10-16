using Relisten.Vendor.Local;

namespace Relisten.Ingestion.Providers.Local;

public sealed record LocalSelection(DiscoveredItem<LocalShowDate, LocalShow[]>[] Items);

public sealed record LocalItemResult(
    LocalShowDate Item,
    ItemDisposition Disposition,
    CatalogSourceId Source
);

public sealed record LocalProgress(
    ProviderStage Stage = ProviderStage.NotStarted,
    int NextItem = 0,
    ProviderSummary? Summary = null
);
