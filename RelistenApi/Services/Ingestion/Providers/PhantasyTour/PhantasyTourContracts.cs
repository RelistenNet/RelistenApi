using System;
using Relisten.Vendor.PhantasyTour;

namespace Relisten.Ingestion.Providers.PhantasyTour;

public sealed record PhantasyTourPage(uint Number);

/// <summary>Preserves the listed local date without conversion to UTC.</summary>
public sealed record PhantasyTourListedDate(PhantasyTourShowId Id, DateTime Date);

public sealed record PhantasyTourDiscovery(
    DiscoveredItem<PhantasyTourShowId, PhantasyTourShowListing>[] Items,
    PhantasyTourPage? Next,
    PhantasyTourListedDate[] ListedDates
);

public sealed record PhantasyTourItemResult(
    PhantasyTourShowId Id,
    CatalogSetlistId Setlist,
    ItemDisposition Disposition
);
