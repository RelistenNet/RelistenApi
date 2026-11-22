using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Relisten.Identifiers;
using static RelistenUserService.Mcp.CatalogToolResponse;
using static RelistenUserService.Mcp.McpCatalogClient;

namespace RelistenUserService.Mcp;

// The SDK uses method parameter names as wire names. Keep public tool arguments in snake_case.
internal sealed class CatalogBrowseTools(McpCatalogClient catalog)
{
    [McpServerTool(Name = "find", ReadOnly = true, OpenWorld = false)]
    [McpMeta("securitySchemes", JsonValue = McpSecuritySchemes.NoAuth)]
    [Description(
        "Find artists, shows, songs, venues or tours by text. Provide an artist ID to narrow a search."
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

            var results = await catalog.Get(
                "search",
                cancellationToken,
                ("q", text),
                ("artist_id", artist)
            );
            if (
                String(results, "availability") != "full"
                || Object(results, "sections") is not { } sections
            )
            {
                throw new McpCatalogException(
                    "Relisten's catalog search is temporarily unavailable."
                );
            }

            var showsAndTours = Rows(sections, "shows");
            return new
            {
                artists = Rows(sections, "artists")
                    .Take(8)
                    .Select(item => new
                    {
                        id = String(item, "id"),
                        name = String(item, "label"),
                        slug = String(item, "artist_slug"),
                    }),
                // All search can lead a show with one of its performances.
                shows = showsAndTours
                    .Where(item => String(item, "kind") == "show")
                    .Concat(Rows(sections, "performances"))
                    .Select(item =>
                    {
                        var date = SearchDate(item);
                        return new
                        {
                            id = String(item, "kind") == "performance"
                                ? String(item, "show_id")
                                : String(item, "id"),
                            date,
                            display_date = date,
                            artist_id = String(item, "artist_id"),
                            artist = String(item, "artist_label"),
                            venue_id = String(item, "venue_id"),
                            venue = String(item, "venue_label"),
                        };
                    })
                    .DistinctBy(show => show.id)
                    .Take(8),
                songs = Rows(sections, "songs")
                    .Take(8)
                    .Select(item => new
                    {
                        id = String(item, "id"),
                        name = String(item, "label"),
                        artist_id = String(item, "artist_id"),
                        artist = String(item, "artist_label"),
                    }),
                venues = Rows(sections, "venues")
                    .Take(8)
                    .Select(item => new
                    {
                        id = String(item, "id"),
                        name = String(item, "label"),
                        venue_id = String(item, "venue_id"),
                        city = String(item, "city_label"),
                    }),
                tours = showsAndTours
                    .Where(item => String(item, "kind") == "tour")
                    .Take(8)
                    .Select(item => new
                    {
                        id = String(item, "id"),
                        name = String(item, "label"),
                        artist_id = String(item, "artist_id"),
                    }),
            };
        });

    private static string? SearchDate(JsonElement show) =>
        show.TryGetProperty("date", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var date)
        && DateOnly.TryParseExact(
            date.ToString("D8", CultureInfo.InvariantCulture),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed
        )
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
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
