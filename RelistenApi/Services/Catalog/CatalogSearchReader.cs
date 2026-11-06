using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

public sealed record CatalogSearchOptions(
    bool Artists = true,
    bool Shows = true,
    bool Songs = true,
    bool Sources = true,
    bool Tours = true,
    bool Venues = true
)
{
    public bool AnyEnabled => Artists || Shows || Songs || Sources || Tours || Venues;
}

public sealed class CatalogSearchResults
{
    public IReadOnlyList<CatalogArtist> Artists { get; init; } = [];
    public IReadOnlyList<CatalogShow> Shows { get; init; } = [];
    public IReadOnlyList<CatalogArtistSongEntry> Songs { get; init; } = [];
    public IReadOnlyList<CatalogSource> Sources { get; init; } = [];
    public IReadOnlyList<CatalogTour> Tours { get; init; } = [];
    public IReadOnlyList<CatalogArtistVenueEntry> Venues { get; init; } = [];
}

/// <summary>
/// Six bounded search categories in one snapshot. Song/venue matches use the
/// canonical mappings with imported fallbacks, including aliases and imported labels.
/// Song counts use stored facts; recording hierarchies remain lazy.
/// </summary>
public sealed class CatalogSearchReader(
    DbService db,
    CatalogBrowseReader browse,
    CatalogShowMetadataReader shows,
    CatalogTourMetadataReader tours,
    CatalogImportedVenueMetadataReader importedVenues,
    CatalogSongReader songs,
    CatalogImportedSongMappingReader importedSongMappings,
    CatalogArtistVenueMetadataReader artistVenues,
    CatalogVenueMetadataReader venueMetadata
)
{
    public async Task<CatalogSearchResults> Read(
        string? query,
        Guid? artistUuid,
        CatalogSearchOptions options,
        CancellationToken cancellationToken
    )
    {
        var term = query?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 3 || !options.AnyEnabled)
        {
            return new();
        }
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        // Search is a literal substring, including a caller's percent or underscore.
        var parameters = new { pattern = Pattern(term), artistUuid };
        const string scope = " AND (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid)";
        var artistIds = options.Artists
            ? (
                await Query<Guid>(
                    session,
                    "SELECT a.uuid FROM artists a WHERE a.name ILIKE @pattern"
                        + scope
                        + " ORDER BY (SELECT count(*) FROM shows s WHERE s.artist_id=a.id) DESC,a.name COLLATE \"C\",a.uuid LIMIT 20",
                    parameters,
                    cancellationToken
                )
            ).ToArray()
            : [];
        var artistResources = (
            await browse.ReadArtists(session, artistIds, cancellationToken)
        ).ToDictionary(artist => artist.uuid);
        var showResults = options.Shows
            ? (
                await Query<CatalogShow>(
                    session,
                    CatalogShowMetadataReader.ShowSelect
                        + " WHERE s.display_date ILIKE @pattern"
                        + scope
                        + " ORDER BY COALESCE(cnt.source_count,0) DESC,s.display_date DESC,s.uuid LIMIT 20",
                    parameters,
                    cancellationToken
                )
            ).ToArray()
            : [];
        await shows.Apply(session, showResults, cancellationToken);
        var sourceResults = options.Sources
            ? (
                await Query<CatalogSource>(
                    session,
                    CatalogRecordingReader.SourceSelect
                        + " WHERE (src.upstream_identifier ILIKE @pattern OR src.description ILIKE @pattern OR src.taper_notes ILIKE @pattern OR src.source ILIKE @pattern OR src.taper ILIKE @pattern OR src.transferrer ILIKE @pattern OR src.lineage ILIKE @pattern)"
                        + scope
                        + " ORDER BY src.avg_rating_weighted DESC NULLS LAST,src.duration DESC NULLS LAST,src.display_date DESC,src.uuid LIMIT 20",
                    parameters,
                    cancellationToken
                )
            ).ToArray()
            : [];
        var sourceVenues = (
            await importedVenues.Read(
                session,
                sourceResults.Select(source => source.venue_id).OfType<long>().Distinct().ToArray(),
                cancellationToken
            )
        ).ToDictionary(venue => venue.id);
        foreach (var source in sourceResults)
        {
            source.ImportedVenue = source.venue_id is { } id
                ? sourceVenues.GetValueOrDefault(id)
                : null;
        }
        var tourIds = options.Tours
            ? (
                await Query<Guid>(
                    session,
                    "SELECT t.uuid FROM tours t JOIN artists a ON a.id=t.artist_id LEFT JOIN shows s ON s.tour_id=t.id WHERE t.name ILIKE @pattern"
                        + scope
                        + " GROUP BY t.id ORDER BY count(s.id) DESC,t.start_date DESC,t.name COLLATE \"C\",t.uuid LIMIT 20",
                    parameters,
                    cancellationToken
                )
            ).ToArray()
            : [];
        var tourResults = await tours.Read(session, tourIds, cancellationToken);
        return new()
        {
            Artists = artistIds.Select(id => artistResources[id]).ToArray(),
            Shows = showResults,
            Sources = sourceResults,
            Tours = tourIds.Select(id => tourResults[id]).ToArray(),
            Songs = options.Songs ? await ReadSongs(session, parameters, cancellationToken) : [],
            Venues = options.Venues ? await ReadVenues(session, parameters, cancellationToken) : [],
        };
    }

    private async Task<IReadOnlyList<CatalogArtistSongEntry>> ReadSongs(
        CatalogReadSession session,
        object parameters,
        CancellationToken token
    )
    {
        var canonicalIds = (
            await Query<string>(
                session,
                """
                WITH RECURSIVE lineage AS (
                  SELECT id canonical_id,id,name,artist_uuid,1 depth FROM norm.songs WHERE merged_into IS NULL
                  UNION ALL
                  SELECT l.canonical_id,s.id,s.name,s.artist_uuid,l.depth+1 FROM lineage l
                  JOIN norm.songs s ON s.merged_into=l.id AND s.artist_uuid=l.artist_uuid WHERE l.depth<32
                )
                SELECT l.canonical_id FROM lineage l JOIN artists a ON a.uuid=l.artist_uuid
                JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                LEFT JOIN norm.song_aliases n ON n.song_id=l.id AND n.artist_uuid=l.artist_uuid
                LEFT JOIN norm.song_counts c ON c.song_id=l.canonical_id
                WHERE (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid) AND (l.name ILIKE @pattern OR n.alias ILIKE @pattern)
                GROUP BY l.canonical_id ORDER BY max(COALESCE(c.matched_show_count,0)) DESC,l.canonical_id LIMIT 20
                """,
                parameters,
                token
            )
        ).ToArray();
        var imported = (
            await Query<CatalogImportedSong>(
                session,
                CatalogSongReader.ImportedSongSelect
                    + " WHERE s.name ILIKE @pattern AND (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid) ORDER BY COALESCE(counts.setlist_show_count,0) DESC,s.name COLLATE \"C\",s.uuid LIMIT 20",
                parameters,
                token
            )
        ).ToArray();
        await importedSongMappings.Apply(session, imported, token);
        var canonical = await songs.ReadSongDetails(
            session,
            canonicalIds
                .Concat(imported.Select(song => song.normalized_song_id).OfType<string>())
                .Distinct()
                .ToArray(),
            token
        );
        return canonical
            .Select(song => new CatalogArtistSongEntry(song, null))
            .Concat(
                imported
                    .Where(raw => raw.normalized_song_id is null)
                    .Select(raw => new CatalogArtistSongEntry(null, raw))
            )
            .OrderByDescending(entry =>
                entry.Song?.matched_show_count ?? entry.ImportedSong!.setlist_show_count
            )
            .ThenBy(entry => entry.Song?.name ?? entry.ImportedSong!.name, StringComparer.Ordinal)
            .ThenBy(
                entry => entry.Song?.id ?? entry.ImportedSong!.uuid.ToString(),
                StringComparer.Ordinal
            )
            .Take(20)
            .ToArray();
    }

    private async Task<IReadOnlyList<CatalogArtistVenueEntry>> ReadVenues(
        CatalogReadSession session,
        object parameters,
        CancellationToken token
    )
    {
        var direct = (
            await Query<VenueSearchMatch>(
                session,
                """
                WITH RECURSIVE lineage AS (
                  SELECT id canonical_id,id,name,1 depth FROM norm.venues WHERE merged_into IS NULL
                  UNION ALL
                  SELECT l.canonical_id,v.id,v.name,l.depth+1 FROM lineage l
                  JOIN norm.venues v ON v.merged_into=l.id WHERE l.depth<32
                )
                SELECT av.artist_uuid,av.venue_id FROM norm.artist_venues av
                JOIN artists a ON a.uuid=av.artist_uuid JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                JOIN norm.venues v ON v.id=av.venue_id JOIN lineage l ON l.canonical_id=v.id
                LEFT JOIN norm.venue_names n ON n.venue_id=l.id
                WHERE av.artist_show_count>0 AND (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid)
                  AND (l.name ILIKE @pattern OR n.name ILIKE @pattern OR v.city ILIKE @pattern OR v.region ILIKE @pattern OR v.country ILIKE @pattern)
                GROUP BY av.id ORDER BY av.artist_show_count DESC,av.id LIMIT 20
                """,
                parameters,
                token
            )
        ).ToArray();
        var imported = (
            await Query<CatalogImportedVenue>(
                session,
                CatalogImportedVenueMetadataReader.Select
                    + " WHERE (v.name ILIKE @pattern OR v.location ILIKE @pattern OR v.past_names ILIKE @pattern) AND (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid) ORDER BY counts.show_count DESC,v.name COLLATE \"C\",v.uuid LIMIT 20",
                parameters,
                token
            )
        ).ToArray();
        var assignments = (
            await Query<VenueSearchAssignment>(
                session,
                """
                SELECT s.venue_id imported_id,a.uuid artist_uuid,
                       CASE WHEN COALESCE(f.norm_catalogue,false) THEN sv.venue_id END venue_id
                FROM shows s JOIN artists a ON a.id=s.artist_id LEFT JOIN features f ON f.artist_id=a.id
                LEFT JOIN norm.show_venues sv ON sv.show_uuid=s.uuid WHERE s.venue_id=ANY(@ids)
                """,
                new { ids = imported.Select(venue => venue.id).ToArray() },
                token
            )
        ).ToArray();
        var canonical = await venueMetadata.Read(
            session,
            direct
                .Select(row => row.venue_id)
                .Concat(assignments.Select(row => row.venue_id).OfType<string>())
                .Distinct()
                .ToArray(),
            token
        );
        var canonicalById = canonical
            .Values.DistinctBy(venue => venue.id)
            .ToDictionary(venue => venue.id);
        var relationships = await artistVenues.Read(
            session,
            direct
                .Select(row => row.artist_uuid)
                .Concat(imported.Select(venue => venue.artist_uuid))
                .Distinct()
                .ToArray(),
            canonical.Values.Select(venue => venue.id).Distinct().ToArray(),
            token
        );
        var pairs = direct
            .Select(row => (Artist: row.artist_uuid, Venue: row.venue_id))
            .Concat(
                assignments
                    .Where(row => row.venue_id is not null && canonical.ContainsKey(row.venue_id))
                    .Select(row => (Artist: row.artist_uuid, Venue: canonical[row.venue_id!].id))
            )
            .Distinct();
        var results = new List<CatalogArtistVenueEntry>();
        foreach (var pair in pairs)
        {
            if (
                relationships.TryGetValue(pair, out var relationship)
                && canonicalById.TryGetValue(pair.Venue, out var venue)
            )
            {
                relationship.Venue = venue;
                results.Add(new(relationship, null));
            }
        }
        var assignmentsByImported = assignments.ToLookup(row => row.imported_id);
        results.AddRange(
            imported
                .Where(raw =>
                    raw.show_count == 0
                    || assignmentsByImported[raw.id]
                        .Any(row =>
                            row.venue_id is null
                            || !canonical.TryGetValue(row.venue_id, out var venue)
                            || !relationships.ContainsKey((raw.artist_uuid, venue.id))
                        )
                )
                .Select(raw => new CatalogArtistVenueEntry(null, raw))
        );
        return results
            .OrderByDescending(entry =>
                entry.ArtistVenue?.artist_show_count ?? entry.ImportedVenue!.show_count
            )
            .ThenBy(
                entry => entry.ArtistVenue?.Venue.name ?? entry.ImportedVenue!.name,
                StringComparer.Ordinal
            )
            .ThenBy(
                entry => entry.ArtistVenue?.id ?? entry.ImportedVenue!.uuid.ToString(),
                StringComparer.Ordinal
            )
            .Take(20)
            .ToArray();
    }

    private sealed class VenueSearchMatch
    {
        public Guid artist_uuid { get; set; }
        public string venue_id { get; set; } = null!;
    }

    private sealed class VenueSearchAssignment
    {
        public long imported_id { get; set; }
        public Guid artist_uuid { get; set; }
        public string? venue_id { get; set; }
    }

    private static string Pattern(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static Task<IEnumerable<T>> Query<T>(
        CatalogReadSession session,
        string sql,
        object parameters,
        CancellationToken token
    ) => session.Connection.QueryAsync<T>(session.Command(sql, parameters, token));
}
