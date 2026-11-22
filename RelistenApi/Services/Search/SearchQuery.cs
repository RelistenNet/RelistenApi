using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Relisten.Search;

internal enum SearchScope
{
    All,
    Artists,
    Songs,
    Venues,
    Shows,
    Sources,
    Reviews,
}

internal sealed record SearchInput(
    string Text,
    SearchScope Scope = SearchScope.All,
    bool AddTracks = false,
    bool Typing = false,
    string? ArtistId = null,
    string? Country = null,
    IReadOnlyList<string>? BoostArtistIds = null,
    IReadOnlySet<string>? Ignore = null,
    int Page = 1,
    string? RequestId = null
);

internal sealed record SearchQuery(
    string Terms,
    string? SelectedArtist = null,
    string? ImpliedArtist = null,
    string? Country = null,
    int? Date = null,
    int? Year = null,
    int? Month = null,
    int? DayKey = null,
    bool Soundboard = false,
    bool Matrix = false,
    string Order = "regular",
    string? Tour = null,
    bool Chronological = false,
    string? Reading = null,
    string? ReadingArtist = null,
    string? CuratedRoute = null
)
{
    public string? Artist => SelectedArtist ?? ImpliedArtist;
    public bool HasDate => Date.HasValue || Year.HasValue || Month.HasValue || DayKey.HasValue;

    public bool MatchesDate(int value) =>
        (Date == null || Date == value)
        && (Year == null || Year == value / 10_000)
        && (Month == null || Month == value / 100 % 100)
        && (DayKey == null || DayKey == value % 10_000);

    public bool SameConstraints(SearchQuery other) =>
        SelectedArtist == other.SelectedArtist
        && ImpliedArtist == other.ImpliedArtist
        && Country == other.Country
        && Date == other.Date
        && Year == other.Year
        && Month == other.Month
        && DayKey == other.DayKey
        && Order == other.Order
        && Soundboard == other.Soundboard
        && Matrix == other.Matrix
        && Tour == other.Tour;

    public SearchInterpretation Interpretation(IReadOnlyList<SearchChip> chips) =>
        new(
            Terms,
            SelectedArtist ?? "",
            ImpliedArtist ?? "",
            Country ?? "",
            NumberFilters(),
            Order,
            Soundboard,
            Matrix,
            Chronological,
            Tour,
            chips
        );

    private Dictionary<string, int> NumberFilters()
    {
        var numbers = new Dictionary<string, int>();
        if (Date is int date)
        {
            numbers["date"] = date;
        }
        if (Year is int year)
        {
            numbers["year"] = year;
        }
        if (Month is int month)
        {
            numbers["month"] = month;
        }
        if (DayKey is int dayKey)
        {
            numbers["day_key"] = dayKey;
        }

        return numbers;
    }
}

internal sealed record SearchChip(string Key, string Label, string Value);

internal sealed record SearchInterpretation(
    string Terms,
    string Selected,
    string Artist,
    string Country,
    IReadOnlyDictionary<string, int> Numbers,
    string Order,
    bool Soundboard,
    bool Matrix,
    bool Chronological,
    string? TourFilter,
    IReadOnlyList<SearchChip> Chips
);

internal sealed record SearchDocument
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string Name { get; init; } = "";
    public string Aliases { get; init; } = "";
    public string Artist { get; init; } = "";
    public string ArtistLabel { get; init; } = "";
    public string ArtistSlug { get; init; } = "";

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? ArtistId { get; init; }
    public ImmutableArray<string> ArtistSlugs { get; init; } = [];
    public int Date { get; init; }
    public int DayKey { get; init; }
    public string Place { get; init; } = "";
    public string Run { get; init; } = "";
    public string Country { get; init; } = "";
    public string CityLabel { get; init; } = "";
    public string VenueLabel { get; init; } = "";
    public string VenueId { get; init; } = "";
    public string ShowId { get; init; } = "";
    public string TourLabel { get; init; } = "";
    public string SongId { get; init; } = "";

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public CatalogSearchProjection.SearchFile? Preview { get; init; }

    [Newtonsoft.Json.JsonIgnore]
    public CatalogSearchProjection.SearchFile[]? Files { get; init; }

    public bool Soundboard { get; init; }
    public bool Soundcheck { get; init; }
    public string SourceId { get; init; } = "";
    public string SourceUpstream { get; init; } = "";
    public bool Matrix { get; init; }
    public long TrackPlays { get; init; }
    public int ReviewMentions { get; init; }
    public double Longest { get; init; }
    public double Popularity { get; init; }
    public long Score { get; init; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? CatalogMatch { get; init; }
    public double CatalogQuality { get; init; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? SuggestedQuery { get; init; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? BrowseReason { get; init; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? Collection { get; init; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public IReadOnlyDictionary<string, string>? Highlights { get; init; }
    public string? PerformanceKey => Kind == "performance" ? Id : null;
}
