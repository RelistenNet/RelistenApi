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
    CatalogVenueMetadataReader venueMetadata,
    CatalogArtistVenueMetadataReader artistVenueMetadata
)
{
    /// <summary>All surviving canonical venues, including places with no approved shows.</summary>
    public async Task<IReadOnlyList<CatalogVenue>> ReadVenues(
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var ids = (
            await session.Connection.QueryAsync<string>(
                session.Command(
                    "SELECT id FROM norm.venues WHERE merged_into IS NULL",
                    null,
                    cancellationToken
                )
            )
        ).ToArray();
        var venues = await venueMetadata.Read(session, ids, cancellationToken);

        return venues
            .Values.OrderBy(venue => venue.name, StringComparer.Ordinal)
            .ThenBy(venue => venue.id, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Approved artists browse canonical groups; other artists browse imported groups.</summary>
    public async Task<IReadOnlyList<CatalogArtistVenueEntry>?> ReadArtistVenues(
        Guid artistUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var approved = await session.Connection.QuerySingleOrDefaultAsync<bool?>(
            session.Command(
                "SELECT COALESCE(f.norm_catalogue,false) FROM artists a LEFT JOIN features f ON f.artist_id=a.id WHERE a.uuid=@artistUuid",
                new { artistUuid },
                cancellationToken
            )
        );
        if (approved is null)
        {
            return null;
        }
        if (!approved.Value)
        {
            return await ReadImportedBrowseEntries(session, artistUuid, cancellationToken);
        }

        var relationships = await artistVenueMetadata.Read(
            session,
            [artistUuid],
            null,
            cancellationToken
        );
        var venueIds = relationships
            .Values.Where(row => row.artist_show_count > 0)
            .Select(row => row.venue_id)
            .Distinct()
            .ToArray();
        var venues = await venueMetadata.Read(session, venueIds, cancellationToken);
        return CreateCanonicalEntries(relationships.Values, venues)
            .OrderBy(entry => entry.ArtistVenue!.Venue.name, StringComparer.Ordinal)
            .ThenBy(entry => entry.ArtistVenue!.id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<CatalogArtistVenueEntry> CreateCanonicalEntries(
        IEnumerable<CatalogArtistVenue> relationships,
        IReadOnlyDictionary<string, CatalogVenue> venues
    )
    {
        var entries = new List<CatalogArtistVenueEntry>();
        foreach (var relationship in relationships)
        {
            if (
                relationship.artist_show_count > 0
                && venues.TryGetValue(relationship.venue_id, out var venue)
                && venue.id == relationship.venue_id
            )
            {
                relationship.Venue = venue;
                entries.Add(new(relationship, null));
            }
        }
        return entries;
    }

    private static async Task<IReadOnlyList<CatalogArtistVenueEntry>> ReadImportedBrowseEntries(
        CatalogReadSession session,
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        // Browse includes only venues used by shows; the explicit inventory also
        // includes unused and source-only observations.
        var imported = await session.Connection.QueryAsync<CatalogImportedVenue>(
            session.Command(
                CatalogImportedVenueMetadataReader.Select
                    + " WHERE a.uuid=@artistUuid AND counts.show_count>0 ORDER BY v.name COLLATE \"C\",v.uuid",
                new { artistUuid },
                cancellationToken
            )
        );
        return imported.Select(venue => new CatalogArtistVenueEntry(null, venue)).ToArray();
    }

    /// <summary>Reads an approved artist/place collection by its stable relationship ID.</summary>
    public async Task<CatalogArtistVenueDetail?> ReadArtistVenue(
        string artistVenueId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var stored = await session.Connection.QuerySingleOrDefaultAsync<CatalogArtistVenue>(
            session.Command(
                """
                SELECT av.artist_uuid,av.venue_id
                FROM norm.artist_venues av JOIN artists a ON a.uuid=av.artist_uuid
                JOIN features f ON f.artist_id=a.id AND f.norm_catalogue
                WHERE av.id=@artistVenueId
                """,
                new { artistVenueId },
                cancellationToken
            )
        );
        return stored is null
            ? null
            : await ReadArtistVenue(
                session,
                stored.artist_uuid,
                stored.venue_id,
                cancellationToken
            );
    }

    /// <summary>The artist/place lookup resolves to the same resource as its relationship ID.</summary>
    public async Task<CatalogArtistVenueDetail?> ReadArtistVenue(
        Guid artistUuid,
        string venueId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadArtistVenue(session, artistUuid, venueId, cancellationToken);
    }

    /// <summary>Looks up a canonical slug within the artist's approved venue collections.</summary>
    public async Task<CatalogArtistVenueDetail?> ReadArtistVenueBySlug(
        Guid artistUuid,
        string slug,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var venueId = await ReadVenueIdBySlug(session, slug, cancellationToken);
        return venueId is null
            ? null
            : await ReadArtistVenue(session, artistUuid, venueId, cancellationToken);
    }

    private async Task<CatalogArtistVenueDetail?> ReadArtistVenue(
        CatalogReadSession session,
        Guid artistUuid,
        string venueId,
        CancellationToken cancellationToken
    )
    {
        var venue = await ReadVenue(session, venueId, cancellationToken);
        if (venue is null)
        {
            return null;
        }

        // A merge resolves to the current canonical pair, never rebinds the old pair ID.
        // Missing materialized relationships remain missing; reads do not repair counts.
        var relationships = await artistVenueMetadata.Read(
            session,
            [artistUuid],
            [venue.id],
            cancellationToken
        );
        var relationship = relationships.GetValueOrDefault((artistUuid, venue.id));
        if (relationship is null)
        {
            return null;
        }

        relationship.Venue = venue;
        var shows = await ReadVenueShows(session, venue.id, artistUuid, cancellationToken);
        return new(relationship, shows);
    }

    /// <summary>Global venue identity, aliases, and its complete approved show collection.</summary>
    public async Task<CatalogVenueDetail?> ReadVenue(
        string venueId,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadVenueDetail(session, venueId, cancellationToken);
    }

    /// <summary>Looks up a stored canonical slug, following merges to the current venue.</summary>
    public async Task<CatalogVenueDetail?> ReadVenueBySlug(
        string slug,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var venueId = await ReadVenueIdBySlug(session, slug, cancellationToken);
        return venueId is null ? null : await ReadVenueDetail(session, venueId, cancellationToken);
    }

    private static Task<string?> ReadVenueIdBySlug(
        CatalogReadSession session,
        string slug,
        CancellationToken cancellationToken
    ) =>
        session.Connection.QuerySingleOrDefaultAsync<string?>(
            session.Command(
                "SELECT id FROM norm.venues WHERE slug=@slug",
                new { slug },
                cancellationToken
            )
        );

    private async Task<CatalogVenueDetail?> ReadVenueDetail(
        CatalogReadSession session,
        string venueId,
        CancellationToken cancellationToken
    )
    {
        var venue = await ReadVenue(session, venueId, cancellationToken);
        if (venue is null)
        {
            return null;
        }

        var shows = await ReadVenueShows(session, venue.id, null, cancellationToken);
        return new(venue, shows);
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
                    CatalogImportedVenueMetadataReader.Select
                        + " WHERE v.artist_id=@artistId ORDER BY v.name,v.uuid",
                    new { artistId },
                    cancellationToken
                )
            )
        ).ToArray();
    }

    /// <summary>Imported venue and its complete artist-owned show collection.</summary>
    public async Task<CatalogImportedVenueDetail?> ReadImportedVenue(
        Guid venueUuid,
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var venue = await session.Connection.QuerySingleOrDefaultAsync<CatalogImportedVenue>(
            session.Command(
                CatalogImportedVenueMetadataReader.Select + " WHERE v.uuid=@venueUuid",
                new { venueUuid },
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
            new { artistUuid = venue.artist_uuid, venueId = venue.id },
            cancellationToken
        );
        return new(venue, shows);
    }

    private async Task<CatalogVenue?> ReadVenue(
        CatalogReadSession session,
        string venueId,
        CancellationToken cancellationToken
    )
    {
        var venue = (
            await venueMetadata.Read(session, [venueId], cancellationToken)
        ).GetValueOrDefault(venueId);
        if (venue is null)
        {
            await CatalogPublicIdentity.RequireNotRetired(session, venueId, cancellationToken);
        }
        return venue;
    }

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
