using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Relisten.Identifiers;
using Relisten.Search;
using RelistenUserService.Authentication;
using RelistenUserService.Search;
using static RelistenUserService.Mcp.CatalogToolResponse;
using static RelistenUserService.Mcp.McpCatalogClient;

namespace RelistenUserService.Mcp;

// The SDK uses method parameter names as wire names. Keep public tool arguments in snake_case.
internal sealed class CatalogBrowseTools(
    McpCatalogClient catalog,
    MergedSearch search,
    McpToolAuthorization authorization
)
{
    [McpServerTool(Name = "find", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuthOrLibraryRead)]
    [Description(
        "Find artists, shows, songs, song sequences, venues, tours or playlists by text. Connect with library.read to include your private playlists."
    )]
    public Task<CallToolResult> Find(
        string text,
        string? artist = null,
        CancellationToken cancellationToken = default
    ) =>
        Read(async () =>
        {
            if (text.Trim().Length is < 3 or > 200)
            {
                throw new McpCatalogException("Use a search term between 3 and 200 characters.");
            }

            var request = new MergedSearchRequest { Q = text, ArtistId = artist };
            if (!request.IsValid())
            {
                throw new McpCatalogException("Use a valid artist ID to narrow the search.");
            }

            var result = await search.Run(
                request,
                token => authorization.OptionalViewerAsync(RelistenScopes.LibraryRead, token),
                cancellationToken
            );
            if (result.Availability is { Catalog: "unavailable", Playlists: "unavailable" })
            {
                throw new McpCatalogException(
                    "Relisten's catalog search is temporarily unavailable. Playlist search is also unavailable."
                );
            }

            return new
            {
                catalog_availability = result.Availability.Catalog,
                playlist_availability = result.Availability.Playlists,
                playlists = result
                    .Results.OfType<PlaylistSearchResult>()
                    .Select(item => new
                    {
                        id = item.Playlist.Id,
                        untrusted_listener_text = new
                        {
                            name = item.Playlist.Name,
                            item.Playlist.Description,
                            item.Playlist.Curator,
                        },
                        notice = McpResults.UntrustedTextNotice,
                        track_count = item.Playlist.TrackCount,
                        owned = item.Playlist.Owned,
                        public_code = item.Playlist.PublicCode,
                    }),
                artists = result
                    .Results.OfType<ArtistSearchResult>()
                    .Take(8)
                    .Select(item => new
                    {
                        id = Field(item.Artist, "id"),
                        name = Field(item.Artist, "name"),
                        slug = Field(item.Artist, "slug"),
                    }),
                shows = result
                    .Results.Select<SearchResult, (JsonElement Artist, JsonElement Show)?>(item =>
                        item switch
                        {
                            ShowSearchResult show => (show.Artist, show.Show),
                            PerformanceSearchResult performance => (
                                performance.Artist,
                                performance.Show
                            ),
                            _ => null,
                        }
                    )
                    .OfType<(JsonElement Artist, JsonElement Show)>()
                    .DistinctBy(item => Field(item.Show, "id"))
                    .Take(8)
                    .Select(item => new
                    {
                        id = Field(item.Show, "id"),
                        date = ShowDate(item.Show),
                        display_date = Field(item.Show, "display_date"),
                        artist_id = Field(item.Artist, "id"),
                        artist = Field(item.Artist, "name"),
                        venue_id = Field(ShowVenue(item.Show), "id"),
                        venue = Field(ShowVenue(item.Show), "name"),
                    }),
                songs = result
                    .Results.OfType<SongSearchResult>()
                    .Take(8)
                    .Select(item => new
                    {
                        id = Field(item.Song, "id"),
                        name = Field(item.Song, "name"),
                        artist_id = Field(item.Artist, "id"),
                        artist = Field(item.Artist, "name"),
                    }),
                sequences = result
                    .Results.OfType<PassageSearchResult>()
                    .Select(item => new
                    {
                        label = string.Join(
                            " → ",
                            item.Passage.Parts.Select(part => Field(part.Song, "name"))
                        ),
                        song_ids = item.Passage.Parts.Select(part => Field(part.Song, "id")),
                        song_titles = item.Passage.Parts.Select(part => Field(part.Song, "name")),
                        artist_slug = Field(item.Artist, "slug"),
                        show_id = Field(item.Show, "id"),
                        source_id = item.Passage.SourceId,
                        track_ids = item.Passage.Tracks.Select(track => Field(track, "id")),
                        item.Popularity,
                        item.Passage.Parts,
                        date = ShowDate(item.Show),
                        venue = Field(ShowVenue(item.Show), "name"),
                    }),
                venues = result
                    .Results.OfType<VenueSearchResult>()
                    .Take(8)
                    .Select(item => new
                    {
                        id = Field(item.Venue, "id"),
                        name = Field(item.Venue, "name"),
                        venue_id = Field(item.Venue, "id"),
                        city = Field(item.Venue, "city"),
                    }),
                tours = result
                    .Results.OfType<TourSearchResult>()
                    .Take(8)
                    .Select(item => new
                    {
                        id = Field(item.Tour, "id"),
                        name = Field(item.Tour, "name"),
                        artist_id = Field(item.Artist, "id"),
                    }),
            };
        });

    private static string? ShowDate(JsonElement show) =>
        Field(show, "date") is { Length: >= 10 } date ? date[..10] : null;

    private static JsonElement ShowVenue(JsonElement show) =>
        show.TryGetProperty("artist_venue", out var artistVenue)
        && artistVenue.ValueKind == JsonValueKind.Object
        && artistVenue.TryGetProperty("venue", out var venue)
            ? venue
        : show.TryGetProperty("imported_venue", out var imported) ? imported
        : default;

    private static string? Field(JsonElement resource, string name) =>
        resource.ValueKind == JsonValueKind.Object
        && resource.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [McpServerTool(Name = "browse", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuth)]
    [Description("Browse an artist by years, tours, venues, or eras.")]
    public Task<CallToolResult> Browse(
        string artist,
        string by = "years",
        int cursor = 0,
        int limit = 25,
        CancellationToken cancellationToken = default
    ) =>
        Read(async () =>
        {
            if (
                by is not ("years" or "tours" or "venues" or "eras")
                || cursor < 0
                || limit is < 1 or > 50
            )
            {
                throw new McpCatalogException(
                    "Choose years, tours, venues or eras and use a valid page limit (1–50)."
                );
            }
            if (by == "eras")
            {
                // The eras resource contains names and order. Count membership from the show summaries.
                var shows = await catalog.Get(
                    $"artists/{Segment(artist)}/shows",
                    cancellationToken
                );
                var eras = await catalog.Get($"artists/{Segment(artist)}/eras", cancellationToken);
                var counts = shows
                    .EnumerateArray()
                    .GroupBy(show => NestedText(show, "era", "name") ?? "")
                    .ToDictionary(group => group.Key, group => group.Count());
                return Page(
                    eras.EnumerateArray()
                        .Select(era =>
                            (object)
                                new
                                {
                                    name = String(era, "name"),
                                    show_count = counts.GetValueOrDefault(
                                        String(era, "name") ?? ""
                                    ),
                                }
                        )
                        .ToArray(),
                    cursor,
                    limit
                );
            }
            var groups = await catalog.Get($"artists/{Segment(artist)}/{by}", cancellationToken);
            var rows =
                by == "venues"
                    ? groups.EnumerateArray().Select(ProjectVenue)
                    : groups
                        .EnumerateArray()
                        .Select(item =>
                            (object)Fields(
                                item,
                                "id",
                                "name",
                                "year",
                                "show_count",
                                "start_date",
                                "end_date"
                            )
                        );
            return Page(rows.ToArray(), cursor, limit);
        });

    [McpServerTool(Name = "get_song", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuth)]
    [Description(
        "Get a song's aliases, recording counts and a page of per-show timing statistics. Use a canonical song ID or artist song slug."
    )]
    public Task<CallToolResult> GetSong(
        string artist,
        string song,
        int cursor = 0,
        int limit = 15,
        CancellationToken cancellationToken = default
    ) =>
        Read(async () =>
        {
            if (cursor < 0 || limit is < 1 or > 50)
            {
                throw new McpCatalogException(
                    "Use a nonnegative cursor and a limit between 1 and 50."
                );
            }
            var escapedArtist = Segment(artist);
            var path = song.StartsWith("song_", StringComparison.Ordinal)
                ? $"songs/{Segment(song)}"
                : $"artists/{escapedArtist}/songs/by-slug/{Segment(song)}";
            var result = await catalog.Get(path, cancellationToken);
            if (String(result, "artist_id") != artist)
            {
                throw new McpCatalogException("This song does not belong to that artist.");
            }
            var slug = await catalog.GetArtistSlug(artist, cancellationToken);
            return new
            {
                song = Fields(
                    result,
                    "id",
                    "name",
                    "slug",
                    "aliases",
                    "matched_track_count",
                    "matched_show_count"
                ),
                timings = Page(
                    Rows(result, "shows")
                        .Select(item =>
                            (object)
                                new
                                {
                                    show = ShowSummary(item.GetProperty("show")),
                                    timings = item.GetProperty("timings"),
                                    link = ShowLink(slug, item.GetProperty("show")),
                                }
                        )
                        .ToArray(),
                    cursor,
                    limit
                ),
            };
        });

    [McpServerTool(Name = "get_show", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuth)]
    [Description(
        "Get a show's recordings and ordered sets. Supply a show ID, or an artist ID and YYYY-MM-DD date."
    )]
    public Task<CallToolResult> GetShow(
        string? artist = null,
        string? date = null,
        [Description("Canonical show ID")] string? show_id = null,
        int cursor = 0,
        int limit = 4,
        CancellationToken cancellationToken = default
    ) =>
        Read(async () =>
        {
            if (cursor < 0 || limit is < 1 or > 10)
            {
                throw new McpCatalogException(
                    "Use a nonnegative cursor and a limit between 1 and 10."
                );
            }
            var path =
                show_id is not null ? $"shows/{Segment(show_id)}"
                : artist is not null && date is not null
                    ? $"artists/{Segment(artist)}/shows/by-date/{Segment(date)}"
                : throw new McpCatalogException("Supply a show ID or an artist ID and a date.");
            var show = await catalog.Get(path, cancellationToken);
            var slug = await catalog.GetArtistSlug(String(show, "artist_id")!, cancellationToken);
            return new
            {
                show = ShowSummary(show),
                link = ShowLink(slug, show),
                sources = Page(
                    Rows(show, "sources").Select(item => (object)Source(item, slug)).ToArray(),
                    cursor,
                    limit
                ),
            };
        });

    [McpServerTool(Name = "get_source", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuth)]
    [Description("Get one recording's playable tracks, ordered sets, durations and song parts.")]
    public Task<CallToolResult> GetSource(string source_id, CancellationToken cancellationToken) =>
        Read(async () =>
        {
            var source = await catalog.Get($"sources/{Segment(source_id)}", cancellationToken);
            return Source(
                source,
                await catalog.GetArtistSlug(String(source, "artist_id")!, cancellationToken)
            );
        });

    [McpServerTool(Name = "get_tracks", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuth)]
    [Description(
        "Check up to 500 canonical track IDs for availability, duration, playable URLs and song parts."
    )]
    public Task<CallToolResult> GetTracks(string[] ids, CancellationToken cancellationToken) =>
        Read(async () =>
        {
            if (ids.Length is < 1 or > 500)
            {
                throw new McpCatalogException("Supply between 1 and 500 canonical track IDs.");
            }
            var response = await catalog.ResolveTracks(ids, cancellationToken);
            return new
            {
                tracks = Rows(response.GetProperty("entities"), "tracks").Select(Track),
                references = Rows(response, "references"),
            };
        });

    private static object Source(JsonElement source, string slug) =>
        new
        {
            source = Fields(
                source,
                "id",
                "is_soundboard",
                "avg_rating",
                "review_count",
                "duration",
                "upstream_identifier",
                "links"
            ),
            link = ShowLink(slug, source) is { } link
            && String(source, "id") is { } id
            && UuidTypeId.TryParse(id, "source", out var uuid)
                ? link + $"?source={uuid:D}"
                : null,
            sets = Rows(source, "sets")
                .Select(set => new
                {
                    name = String(set, "name"),
                    tracks = Rows(set, "tracks").Select(Track),
                }),
        };

    private static object Track(JsonElement track) =>
        Fields(
            track,
            "id",
            "source_id",
            "show_id",
            "title",
            "duration",
            "mp3_url",
            "flac_url",
            "normalization"
        );

    private static object ProjectVenue(JsonElement row)
    {
        if (Object(row, "artist_venue") is { } relationship)
        {
            return new
            {
                id = String(relationship, "id"),
                name = NestedText(relationship, "venue", "name"),
                venue_id = NestedText(relationship, "venue", "id"),
                show_count = relationship.GetProperty("artist_show_count"),
            };
        }
        return Fields(row.GetProperty("imported_venue"), "id", "name", "show_count");
    }
}
