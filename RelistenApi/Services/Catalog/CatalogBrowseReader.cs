using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>Reads artist/year navigation and show summaries in one snapshot per request.</summary>
public sealed class CatalogBrowseReader(DbService db, CatalogShowMetadataReader metadata)
{
    /// <summary>Preserves the legacy autocreated filter and featured/name ordering.</summary>
    public async Task<IReadOnlyList<CatalogArtist>> ReadArtists(
        bool includeAutoCreated = false,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var artists = (
            await session.Connection.QueryAsync<CatalogArtist>(
                session.Command(
                    ArtistSelect
                        + """
                         WHERE (NOT @includeAutoCreated AND (a.featured & 2)=0)
                            OR (@includeAutoCreated AND (COALESCE(sh.show_count,0)>0 OR COALESCE(src.source_count,0)>0))
                         ORDER BY a.featured DESC,a.name,a.uuid
                        """,
                    new { includeAutoCreated },
                    cancellationToken
                )
            )
        ).ToList();
        await ReadArtistMetadata(session, artists, cancellationToken);
        return artists;
    }

    public async Task<CatalogArtist?> ReadArtist(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadArtist(session, artistUuid, cancellationToken);
    }

    internal async Task<CatalogArtist?> ReadArtist(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        var artist = await session.Connection.QuerySingleOrDefaultAsync<CatalogArtist>(
            session.Command(
                SelectedArtistSelect + " WHERE a.uuid=@artistUuid",
                new { artistUuid },
                cancellationToken
            )
        );
        if (artist is not null)
        {
            await ReadArtistMetadata(session, [artist], cancellationToken);
        }
        return artist;
    }

    /// <summary>Hydrates only explicitly requested resolver artists, within the caller's snapshot.</summary>
    internal static async Task<IReadOnlyList<CatalogArtist>> ReadArtists(
        CatalogReadSession session,
        IReadOnlyList<Guid> artistUuids,
        CancellationToken cancellationToken
    )
    {
        var artists = (
            await session.Connection.QueryAsync<CatalogArtist>(
                session.Command(
                    SelectedArtistSelect + " WHERE a.uuid=ANY(@artistUuids) ORDER BY a.uuid",
                    new { artistUuids = artistUuids.ToArray() },
                    cancellationToken
                )
            )
        ).ToArray();
        await ReadArtistMetadata(session, artists, cancellationToken);
        return artists;
    }

    /// <summary>Null means an unknown artist; an empty list means the artist has no years.</summary>
    public async Task<IReadOnlyList<CatalogYear>?> ReadYears(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadYears(session, artistUuid, cancellationToken);
    }

    internal async Task<IReadOnlyList<CatalogYear>?> ReadYears(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        if (!await ArtistExists(session, artistUuid, cancellationToken))
        {
            return null;
        }
        return (
            await session.Connection.QueryAsync<CatalogYear>(
                session.Command(
                    YearSelect + " WHERE a.uuid=@artistUuid ORDER BY y.year,y.uuid",
                    new { artistUuid },
                    cancellationToken
                )
            )
        ).ToArray();
    }

    /// <summary>Eras are ordered artist-owned metadata, without a separate public identity.</summary>
    public async Task<IReadOnlyList<CatalogEra>?> ReadEras(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadEras(session, artistUuid, cancellationToken);
    }

    internal async Task<IReadOnlyList<CatalogEra>?> ReadEras(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        if (!await ArtistExists(session, artistUuid, cancellationToken))
        {
            return null;
        }

        return (
            await session.Connection.QueryAsync<CatalogEra>(
                session.Command(
                    """
                    SELECT e."order",e.name FROM eras e JOIN artists a ON a.id=e.artist_id
                    WHERE a.uuid=@artistUuid
                    ORDER BY e."order" NULLS LAST,e.name COLLATE "C" NULLS LAST,e.id
                    """,
                    new { artistUuid },
                    cancellationToken
                )
            )
        ).ToArray();
    }

    /// <summary>Both typed-year and calendar-year lookups are scoped to their owning artist.</summary>
    public async Task<CatalogYearDetail?> ReadYear(
        Guid artistUuid,
        Guid? yearUuid,
        int? yearNumber,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadYear(session, artistUuid, yearUuid, yearNumber, cancellationToken);
    }

    internal async Task<CatalogYearDetail?> ReadYear(
        CatalogReadSession session,
        Guid artistUuid,
        Guid? yearUuid,
        int? yearNumber,
        CancellationToken cancellationToken
    )
    {
        // Imported year labels use four digits, including unusual dates such as 0023.
        var predicate = yearUuid.HasValue ? "y.uuid=@yearUuid" : "y.year=@year";
        var year = await session.Connection.QuerySingleOrDefaultAsync<CatalogYear>(
            session.Command(
                YearSelect + " WHERE a.uuid=@artistUuid AND " + predicate,
                new
                {
                    artistUuid,
                    yearUuid,
                    year = yearNumber?.ToString(
                        "D4",
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                },
                cancellationToken
            )
        );
        if (year is null)
        {
            return null;
        }
        var shows = await ReadShowSummaries(session, artistUuid, year.uuid, cancellationToken);
        return new(year, shows);
    }

    public async Task<IReadOnlyList<CatalogShow>?> ReadShows(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadShows(session, artistUuid, cancellationToken);
    }

    internal async Task<IReadOnlyList<CatalogShow>?> ReadShows(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        if (!await ArtistExists(session, artistUuid, cancellationToken))
        {
            return null;
        }
        return await ReadShowSummaries(session, artistUuid, null, cancellationToken);
    }

    private async Task<IReadOnlyList<CatalogShow>> ReadShowSummaries(
        CatalogReadSession session,
        Guid artistUuid,
        Guid? yearUuid,
        CancellationToken cancellationToken
    )
    {
        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    CatalogShowMetadataReader.ShowSelect
                        + " WHERE a.uuid=@artistUuid AND (@yearUuid::uuid IS NULL OR y.uuid=@yearUuid) ORDER BY s.display_date,s.uuid",
                    new { artistUuid, yearUuid },
                    cancellationToken
                )
            )
        ).ToArray();
        await metadata.Apply(session, shows, cancellationToken);
        return shows;
    }

    internal static Task<bool> ArtistExists(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QuerySingleAsync<bool>(
            session.Command(
                "SELECT EXISTS(SELECT 1 FROM artists WHERE uuid=@artistUuid)",
                new { artistUuid },
                cancellationToken
            )
        );

    private static async Task ReadArtistMetadata(
        CatalogReadSession session,
        IReadOnlyList<CatalogArtist> artists,
        CancellationToken cancellationToken
    )
    {
        if (artists.Count == 0)
        {
            return;
        }
        var ids = artists.Select(x => x.id).ToArray();
        var features = (
            await session.Connection.QueryAsync<CatalogFeatures>(
                session.Command(
                    "SELECT * FROM features WHERE artist_id=ANY(@ids)",
                    new { ids },
                    cancellationToken
                )
            )
        ).ToDictionary(x => x.artist_id);
        var providers = (
            await session.Connection.QueryAsync<CatalogProviderAssignment>(
                session.Command(
                    """
                    SELECT aus.artist_id,aus.upstream_identifier,up.name provider,up.url,up.description,up.credit_line
                    FROM artists_upstream_sources aus JOIN upstream_sources up ON up.id=aus.upstream_source_id
                    WHERE aus.artist_id=ANY(@ids) ORDER BY up.name,aus.upstream_identifier,up.id
                    """,
                    new { ids },
                    cancellationToken
                )
            )
        ).ToLookup(x => x.artist_id);
        foreach (var artist in artists)
        {
            artist.Features = features.GetValueOrDefault(artist.id) ?? new();
            artist.UpstreamSources = providers[artist.id].ToList();
        }
    }

    // Selected artists use bounded index counts; the all-artist list aggregates
    // once across the catalog instead of repeating those scans for every artist.
    private const string SelectedArtistSelect = """
        SELECT a.*,
               (SELECT count(*)::int FROM shows s WHERE s.artist_id=a.id) show_count,
               (SELECT count(*)::int FROM sources src WHERE src.artist_id=a.id) source_count
        FROM artists a
        """;

    private const string ArtistSelect = """
        SELECT a.*,COALESCE(sh.show_count,0)::int show_count,COALESCE(src.source_count,0)::int source_count
        FROM artists a
        LEFT JOIN (SELECT artist_id,count(*) show_count FROM shows GROUP BY artist_id) sh ON sh.artist_id=a.id
        LEFT JOIN (SELECT artist_id,count(*) source_count FROM sources GROUP BY artist_id) src ON src.artist_id=a.id
        """;

    private const string YearSelect = """
        SELECT y.*,a.uuid artist_uuid FROM years y JOIN artists a ON a.id=y.artist_id
        """;
}
