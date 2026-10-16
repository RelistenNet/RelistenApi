using System;
using System.Collections.Generic;
using Relisten.Vendor.Phishin;

namespace Relisten.Ingestion.Providers.PhishIn;

public sealed record PhishInPage(int Number);

/// <summary>Stable prerequisites carried separately from changing page progress.</summary>
public sealed record PhishInCatalogs(
    IDictionary<string, IList<string>> Eras,
    PhishInSongMapping[] Songs
);

public sealed record PhishInSongMapping(int Id, string Slug);

/// <summary>Only show fields consumed by import travel in workflow history; arbitrary tag notes do not.</summary>
public sealed record PhishInShowInput(
    string Date,
    bool Soundboard,
    bool Remastered,
    int TourId,
    int VenueId,
    string? TaperNotes,
    DateTime UpdatedAt,
    string[] Tags,
    PhishinShowTrack[] Tracks
);

/// <summary>Identities are available before the artist's derived shows are rebuilt.</summary>
public sealed record PhishInItemResult(
    PhishInShowId Item,
    ItemDisposition Disposition,
    CatalogSourceId Source,
    CatalogSetlistId Setlist
);
