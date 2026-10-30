using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Catalog;

/// <summary>
/// Reads venue membership from catalog shows in one snapshot. Imported observations
/// remain artist-owned; canonical metadata is global and show membership is approval-gated.
/// </summary>
public sealed class CatalogVenueReader(
    DbService db,
    CatalogShowMetadataReader showMetadata,
    CatalogVenueMetadataReader venueMetadata
)
{
    /// <summary>Groups artist shows by approved canonical place, otherwise by their imported venue.</summary>
    public async Task<IReadOnlyList<CatalogArtistVenueEntry>?> ReadArtistVenues(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        if (await ReadArtistId(session, artistUuid, cancellationToken) is null)
        {
            return null;
        }

        var shows = await ReadShows(
            session,
            CatalogShowMetadataReader.ShowSelect + " WHERE a.uuid=@artistUuid",
            new { artistUuid },
            cancellationToken
        );
        return shows
            .Where(show => show.Venue is not null || show.ImportedVenue is not null)
            .GroupBy(show => show.Venue?.id ?? "imported:" + show.ImportedVenue!.uuid)
            .Select(group =>
            {
                var show = group.First();
                return new CatalogArtistVenueEntry(
                    group.Count(),
                    show.Venue,
                    show.Venue is null ? show.ImportedVenue : null
                );
            })
            .OrderBy(
                entry => entry.Venue?.name ?? entry.ImportedVenue?.name,
                StringComparer.Ordinal
            )
            .ThenBy(
                entry => entry.Venue?.id ?? entry.ImportedVenue!.uuid.ToString(),
                StringComparer.Ordinal
            )
            .ToArray();
    }

    /// <summary>Artist detail exists only when the approved show collection establishes membership.</summary>
    public async Task<CatalogArtistVenueEntry?> ReadArtistVenue(
        Guid artistUuid,
        string venueId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        if (await ReadArtistId(session, artistUuid, cancellationToken) is null)
        {
            return null;
        }
        var venue = await ReadVenue(session, venueId, cancellationToken);
        if (venue is null)
        {
            return null;
        }
        var shows = await ReadVenueShows(session, venue.id, artistUuid, cancellationToken);
        return shows.Count == 0 ? null : new(shows.Count, venue, null);
    }

    /// <summary>Global venue identity and aliases are independent of artist rollout flags.</summary>
    public async Task<CatalogVenue?> ReadVenue(
        string venueId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadVenue(session, venueId, cancellationToken);
    }

    /// <summary>Known parents with no approved assignments return an empty collection.</summary>
    public async Task<IReadOnlyList<CatalogShow>?> ReadVenueShows(
        string venueId,
        Guid? artistUuid = null,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        if (
            artistUuid is { } artist
            && await ReadArtistId(session, artist, cancellationToken) is null
        )
        {
            return null;
        }
        var venue = await ReadVenue(session, venueId, cancellationToken);
        return venue is null
            ? null
            : await ReadVenueShows(session, venue.id, artistUuid, cancellationToken);
    }

    /// <summary>All artist-owned imported rows, including unused rows with a zero show count.</summary>
    public async Task<IReadOnlyList<CatalogImportedVenue>?> ReadImportedVenues(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var artistId = await ReadArtistId(session, artistUuid, cancellationToken);
        if (artistId is null)
        {
            return null;
        }
        // Counts use the same show-level assignment as imported detail, rather than
        // the legacy aggregate that can count a source's different venue observation.
        return (
            await session.Connection.QueryAsync<CatalogImportedVenue>(
                session.Command(
                    """
                    SELECT v.*,a.uuid artist_uuid,COALESCE(sc.show_count,0)::int show_count
                    FROM venues v JOIN artists a ON a.id=v.artist_id
                    LEFT JOIN (
                      SELECT venue_id,count(*)::int show_count FROM shows
                      WHERE artist_id=@artistId GROUP BY venue_id
                    ) sc ON sc.venue_id=v.id
                    WHERE v.artist_id=@artistId ORDER BY v.name,v.uuid
                    """,
                    new { artistId },
                    cancellationToken
                )
            )
        ).ToArray();
    }

    /// <summary>Imported venue and its show-level collection; null for missing or foreign rows.</summary>
    public async Task<CatalogImportedVenueDetail?> ReadImportedVenue(
        Guid artistUuid,
        Guid venueUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var venue = await session.Connection.QuerySingleOrDefaultAsync<CatalogImportedVenue>(
            session.Command(
                "SELECT v.*,a.uuid artist_uuid FROM venues v JOIN artists a ON a.id=v.artist_id WHERE a.uuid=@artistUuid AND v.uuid=@venueUuid",
                new { artistUuid, venueUuid },
                cancellationToken
            )
        );
        if (venue is null)
        {
            return null;
        }
        var shows = await ReadShows(
            session,
            CatalogShowMetadataReader.ShowSelect
                + " WHERE a.uuid=@artistUuid AND s.venue_id=@venueId",
            new { artistUuid, venueId = venue.id },
            cancellationToken
        );
        return new(venue, shows);
    }

    private async Task<CatalogVenue?> ReadVenue(
        CatalogReadSession session,
        string venueId,
        CancellationToken cancellationToken
    ) =>
        (await venueMetadata.Read(session, [venueId], cancellationToken)).GetValueOrDefault(
            venueId
        );

    private async Task<IReadOnlyList<CatalogShow>> ReadVenueShows(
        CatalogReadSession session,
        string venueId,
        Guid? artistUuid,
        CancellationToken cancellationToken
    ) =>
        await ReadShows(
            session,
            """
            WITH RECURSIVE owners AS (
              SELECT id,ARRAY[id] path FROM norm.venues WHERE id=@venueId
              UNION ALL
              SELECT v.id,o.path || v.id FROM owners o JOIN norm.venues v ON v.merged_into=o.id
              WHERE cardinality(o.path)<32 AND NOT v.id=ANY(o.path)
            )
            """
                + CatalogShowMetadataReader.ShowSelect
                + """
                 WHERE COALESCE(f.norm_catalogue,false)
                   AND (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid)
                   AND EXISTS (
                     SELECT 1 FROM norm.show_venues sv JOIN owners o ON o.id=sv.venue_id WHERE sv.show_uuid=s.uuid
                   )
                """,
            new { venueId, artistUuid },
            cancellationToken
        );

    private async Task<IReadOnlyList<CatalogShow>> ReadShows(
        CatalogReadSession session,
        string sql,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        var shows = (
            await session.Connection.QueryAsync<CatalogShow>(
                session.Command(
                    sql + " ORDER BY s.display_date,s.uuid",
                    parameters,
                    cancellationToken
                )
            )
        ).ToArray();
        await showMetadata.Apply(session, shows, cancellationToken);
        return shows;
    }

    private static Task<long?> ReadArtistId(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QuerySingleOrDefaultAsync<long?>(
            session.Command(
                "SELECT id FROM artists WHERE uuid=@artistUuid",
                new { artistUuid },
                cancellationToken
            )
        );
}
