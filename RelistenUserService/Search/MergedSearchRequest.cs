using Microsoft.AspNetCore.Mvc;

namespace RelistenUserService.Search;

public sealed record MergedSearchRequest
{
    public string? Q { get; init; }
    public string Scope { get; init; } = "all";
    public string Mode { get; init; } = "explore";

    [FromQuery(Name = "artist_id")]
    public string? ArtistId { get; init; }
    public string? Country { get; init; }

    [FromQuery(Name = "boost_artist_ids")]
    public string[]? BoostArtistIds { get; init; }
    public string[]? Ignore { get; init; }
    public bool Typing { get; init; }
    public int Page { get; init; } = 1;

    [FromQuery(Name = "request_id")]
    public string? RequestId { get; init; }

    [FromQuery(Name = "reading_id")]
    public string? ReadingId { get; init; }

    internal bool IsValid()
    {
        var boosts = Values(BoostArtistIds).ToArray();
        return (Q?.Length ?? 0) <= 256
            && (RequestId?.Length ?? 0) <= 128
            && (ReadingId?.Length ?? 0) <= 192
            && Page is >= 1 and <= 100
            && (Mode is "explore" or "add_tracks")
            && (
                Scope.ToLowerInvariant()
                is "all"
                    or "artists"
                    or "songs"
                    or "venues"
                    or "shows"
                    or "sources"
                    or "reviews"
                    or "playlists"
            )
            && (ArtistId == null || Relisten.Identifiers.ArtistId.TryParse(ArtistId, out _))
            && (Country == null || (Country.Length == 2 && Country.All(char.IsAsciiLetter)))
            && boosts.Length <= 32
            && boosts.All(id => Relisten.Identifiers.ArtistId.TryParse(id, out _));
    }

    internal bool WantsCatalog => !Scope.Equals("playlists", StringComparison.OrdinalIgnoreCase);
    internal bool IsAll => Scope.Equals("all", StringComparison.OrdinalIgnoreCase);
    internal bool WantsPlaylists =>
        (!WantsCatalog || IsAll)
        && Mode != "add_tracks"
        && !Values(Ignore).Contains("playlists", StringComparer.OrdinalIgnoreCase);

    internal string CatalogPath()
    {
        var query = new List<KeyValuePair<string, string?>>();
        query.Add(new("q", Q));
        query.Add(new("scope", Scope));
        query.Add(new("mode", Mode));
        query.Add(new("artist_id", ArtistId));
        query.Add(new("country", Country));
        query.Add(new("typing", Typing ? "true" : "false"));
        query.Add(new("page", Page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        query.Add(new("request_id", RequestId));
        query.Add(new("reading_id", ReadingId));
        query.AddRange(
            Values(BoostArtistIds)
                .Select(value => new KeyValuePair<string, string?>("boost_artist_ids", value))
        );
        query.AddRange(
            (Ignore ?? []).Select(value => new KeyValuePair<string, string?>("ignore", value))
        );
        return Relisten.Search.CatalogSearchExchange.Path.TrimStart('/')
            + QueryString.Create(query.Where(pair => pair.Value != null));
    }

    private static IEnumerable<string> Values(string[]? values) =>
        (values ?? [])
            .OfType<string>()
            .SelectMany(value =>
                value.Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
            );
}
