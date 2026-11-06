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
    CatalogArtistVenueMetadataReader artistVenueMetadata,
    CatalogCollectionReader collections
)
{
    /// <summary>All surviving canonical venues, including places with no approved shows.</summary>
    public async Task<IReadOnlyList<CatalogVenue>> ReadVenues(
        CancellationToken cancellationToken = default
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var selection = await collections.ReadAll(
            session,
            new(CatalogReferenceKind.Venue),
            cancellationToken
        );
        var venues = await venueMetadata.Read(
            session,
            selection.Targets.Select(target => target.Id).ToArray(),
            cancellationToken
        );
        return selection.Targets.Select(target => venues[target.Id]).ToArray();
    }

    /// <summary>Approved artists browse canonical groups; other artists browse imported groups.</summary>
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

        var selection = await collections.ReadAll(
            session,
            new(
                CatalogReferenceKind.ArtistVenue,
                CatalogReferenceKind.Artist,
                Relisten.Identifiers.UuidTypeId.Format("artist", artistUuid),
                IncludeFallback: true
            ),
            cancellationToken
        );
        var selected = selection.Targets.Select(t => t.Id).ToHashSet();
        var relationships = (
            await artistVenueMetadata.Read(session, [artistUuid], null, cancellationToken)
        )
            .Values.Where(v => selected.Contains(v.id))
            .ToArray();
        var venues = await venueMetadata.Read(
            session,
            relationships.Select(v => v.venue_id).ToArray(),
            cancellationToken
        );
        foreach (var relationship in relationships)
        {
            relationship.Venue = venues[relationship.venue_id];
        }
        var imported = (
            await session.Connection.QueryAsync<CatalogImportedVenue>(
                session.Command(
                    CatalogImportedVenueMetadataReader.Select + " WHERE v.uuid=ANY(@uuids)",
                    new
                    {
                        uuids = selection
                            .Targets.Where(t => t.Kind == CatalogReferenceKind.ImportedVenue)
                            .Select(t => t.Uuid!.Value)
                            .ToArray(),
                    },
                    cancellationToken
                )
            )
        ).ToArray();
        var entries = relationships
            .Select(v => (Id: v.id, Entry: new CatalogArtistVenueEntry(v, null)))
            .Concat(
                imported.Select(v =>
                    (
                        Id: Relisten.Identifiers.UuidTypeId.Format("imported_venue", v.uuid),
                        Entry: new CatalogArtistVenueEntry(null, v)
                    )
                )
            )
            .ToDictionary(e => e.Id, e => e.Entry);
        return selection.Targets.Select(t => entries[t.Id]).ToArray();
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
        CancellationToken cancellationToken = default,
        bool includeShows = true
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        return await ReadArtistVenue(session, artistUuid, venueId, cancellationToken, includeShows);
    }

    /// <summary>Looks up a canonical slug within the artist's approved venue collections.</summary>
    public async Task<CatalogArtistVenueDetail?> ReadArtistVenueBySlug(
        Guid artistUuid,
        string slug,
        CancellationToken cancellationToken = default,
        bool includeShows = true
    )
    {
        await using var session = await CatalogReadSession.Open(db, cancellationToken);
        var venueId = await ReadVenueIdBySlug(session, slug, cancellationToken);
        return venueId is null
            ? null
            : await ReadArtistVenue(session, artistUuid, venueId, cancellationToken, includeShows);
    }

    private async Task<CatalogArtistVenueDetail?> ReadArtistVenue(
        CatalogReadSession session,
        Guid artistUuid,
        string venueId,
        CancellationToken cancellationToken,
        bool includeShows = true
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
        var shows = includeShows
            ? await ReadVenueShows(session, venue.id, artistUuid, cancellationToken)
            : [];
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
            CatalogShowMetadataReader.ShowSelect
                + " WHERE COALESCE(f.norm_catalogue,false) AND (@artistUuid::uuid IS NULL OR a.uuid=@artistUuid) AND "
                + ShowMembershipSql("@venueId"),
            new { venueId, artistUuid },
            cancellationToken
        );

    // The venue expression is server-owned SQL. Follow the survivor before including
    // all merged predecessors, so old assignments and old filter IDs keep working.
    internal static string ShowMembershipSql(string venue, string show = "s.uuid") =>
        $$"""
            EXISTS (SELECT 1 FROM ({{ShowMembershipRowsSql(
                venue
            )}}) membership WHERE membership.show_uuid={{show}})
            """;

    internal static string ShowMembershipRowsSql(string venue) =>
        $$"""
              WITH RECURSIVE forward AS (
                SELECT v.id,v.merged_into,ARRAY[v.id] path FROM norm.venues v WHERE v.id={{venue}}
                UNION ALL
                SELECT v.id,v.merged_into,f.path || v.id FROM forward f JOIN norm.venues v ON v.id=f.merged_into
                WHERE cardinality(f.path)<32 AND NOT v.id=ANY(f.path)
              ), owners AS (
                SELECT id,ARRAY[id] path FROM forward WHERE merged_into IS NULL
                UNION ALL
                SELECT v.id,o.path || v.id FROM owners o JOIN norm.venues v ON v.merged_into=o.id
                WHERE cardinality(o.path)<32 AND NOT v.id=ANY(o.path)
              ) SELECT sv.show_uuid FROM norm.show_venues sv JOIN owners o ON o.id=sv.venue_id
            """;

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

    private sealed class ArtistShowVenue
    {
        public long? imported_venue_id { get; set; }
        public string? venue_id { get; set; }
    }
}
