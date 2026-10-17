using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationReadService
{
    /// <summary>Lists sites and rooms individually. Accepted historical names participate in search.</summary>
    public Task<NormalizationPage<NormalizedVenue>> ListVenuesAsync(
        string? q = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var query = NormalizeQuery(q);
        var after = ParseCursor(cursor, "venues", Guid.Empty, query, "name_uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var rows = (
                    await c.QueryAsync<NormalizedVenue>(
                        new CommandDefinition(
                            """
                            SELECT v.uuid,v.display_name,v.kind,v.revision,v.updated_at,
                                COALESCE((SELECT resolved_uuid FROM normalization.venue_identity_resolution WHERE original_uuid=v.parent_venue_uuid),v.parent_venue_uuid) AS parent_venue_uuid,
                                ARRAY(SELECT DISTINCT wording FROM normalization.resolved_venue_names WHERE resolved_venue_uuid=v.uuid ORDER BY wording) AS aliases
                            FROM normalization.canonical_venues v WHERE NOT EXISTS(SELECT 1 FROM normalization.venue_redirects r WHERE r.retired_uuid=v.uuid)
                                AND (@query IS NULL OR strpos(v.name_key,@query)>0 OR EXISTS(
                                    SELECT 1 FROM normalization.venue_identity_resolution resolution JOIN normalization.canonical_venues original ON original.uuid=resolution.original_uuid
                                    WHERE resolution.resolved_uuid=v.uuid AND strpos(original.name_key,@query)>0) OR EXISTS(
                                    SELECT 1 FROM normalization.resolved_venue_names name WHERE name.resolved_venue_uuid=v.uuid AND strpos(name.name_key,@query)>0))
                                AND (@afterUuid IS NULL OR (v.name_key COLLATE "C",v.uuid)>(@sort COLLATE "C",@afterUuid))
                            ORDER BY v.name_key COLLATE "C",v.uuid LIMIT @take
                            """,
                            new
                            {
                                query,
                                afterUuid = after?.Uuid,
                                sort = after?.Sort,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                return Page(
                    rows,
                    limit,
                    v =>
                        EncodeCursor(
                            "venues",
                            Guid.Empty,
                            query,
                            "name_uuid",
                            NormalizedPeopleService.NameKey(v.display_name),
                            v.uuid
                        )
                );
            },
            cancellationToken
        );
    }

    /// <summary>Returns supported name/location assertions separately; unknown validity bounds remain null.</summary>
    public Task<NormalizedVenue?> GetVenueAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) =>
        ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveAsync(c, t, uuid, false, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                var venue = await c.QuerySingleAsync<NormalizedVenue>(
                    new CommandDefinition(
                        """
                        SELECT v.uuid,v.display_name,v.kind,v.revision,v.updated_at,
                                COALESCE((SELECT resolved_uuid FROM normalization.venue_identity_resolution WHERE original_uuid=v.parent_venue_uuid),v.parent_venue_uuid) AS parent_venue_uuid,
                                ARRAY(SELECT DISTINCT wording FROM normalization.resolved_venue_names WHERE resolved_venue_uuid=v.uuid ORDER BY wording) AS aliases
                        FROM normalization.canonical_venues v WHERE uuid=@survivor
                        """,
                        new { survivor },
                        t,
                        cancellationToken: cancellationToken
                    )
                );
                venue.requested_uuid = survivor != uuid ? uuid : null;
                venue.redirect_uuid = survivor != uuid ? survivor : null;
                venue.names = (
                    await c.QueryAsync<NormalizedVenueName>(
                        new CommandDefinition(
                            "SELECT e.uuid,e.wording,e.language,e.valid_from,e.valid_to,e.revision,e.basis,"
                                + EvidenceColumns
                                + " FROM normalization.resolved_venue_names e "
                                + EvidenceJoins
                                + " WHERE e.resolved_venue_uuid=@survivor ORDER BY e.valid_from NULLS FIRST,e.uuid",
                            new { survivor },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                venue.locations = (
                    await c.QueryAsync<NormalizedVenueLocation>(
                        new CommandDefinition(
                            "SELECT e.uuid,e.original_locality,e.country,e.region,e.city,e.address,e.latitude,e.longitude,e.coordinate_precision,e.valid_from,e.valid_to,e.revision,e.basis,"
                                + EvidenceColumns
                                + " FROM normalization.resolved_venue_locations e "
                                + EvidenceJoins
                                + " WHERE e.resolved_venue_uuid=@survivor ORDER BY e.valid_from NULLS FIRST,e.uuid",
                            new { survivor },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                venue.external_identities = (
                    await c.QueryAsync<NormalizationExternalIdentity>(
                        new CommandDefinition(
                            "SELECT e.provider,e.resource_type,e.provider_key,e.revision,e.basis,"
                                + EvidenceColumns
                                + " FROM normalization.resolved_venue_external_identities e "
                                + EvidenceJoins
                                + " WHERE e.resolved_venue_uuid=@survivor ORDER BY e.provider,e.resource_type,e.provider_key",
                            new { survivor },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                return venue;
            },
            cancellationToken
        );

    /// <summary>Uses a performance-specific override before its imported venue mapping. Sites do not include rooms.</summary>
    public Task<NormalizationPage<NormalizedShowReference>?> ListVenueShowsAsync(
        Guid venueUuid,
        Guid? artistUuid = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default
    )
    {
        var filter = artistUuid?.ToString("D");
        var after = ParseCursor(cursor, "venue_shows", venueUuid, filter, "date_desc_uuid", limit);
        return ReadAsync(
            async (c, t) =>
            {
                var survivor = await ResolveAsync(c, t, venueUuid, false, cancellationToken);
                if (survivor == null)
                {
                    return null;
                }
                await RequireArtistAsync(c, t, artistUuid, cancellationToken);
                var rows = (
                    await c.QueryAsync<NormalizedShowReference>(
                        new CommandDefinition(
                            "WITH playable AS ("
                                + PlayableSources
                                + """
                                )
                                SELECT sh.uuid AS show_uuid,a.uuid AS artist_uuid,sh.display_date,sh.performance_type,sh.performance_label,
                                    ARRAY[CASE WHEN explicit.uuid IS NOT NULL THEN 'show_venue' ELSE 'imported_venue' END] AS match_evidence,
                                    ARRAY[COALESCE(explicit.basis,imported.basis)] AS decision_basis
                                FROM shows sh JOIN artists a ON a.id=sh.artist_id
                                LEFT JOIN normalization.show_venue_assignments explicit ON explicit.show_uuid=sh.uuid AND explicit.state='accepted'
                                LEFT JOIN venues legacy ON legacy.id=sh.venue_id AND (legacy.artist_id IS NULL OR legacy.artist_id=sh.artist_id)
                                LEFT JOIN normalization.imported_venue_assignments imported ON imported.imported_venue_uuid=legacy.uuid AND imported.state='accepted'
                                WHERE COALESCE(explicit.canonical_venue_uuid,imported.canonical_venue_uuid)=@survivor
                                    AND EXISTS(SELECT 1 FROM playable p WHERE p.show_uuid=sh.uuid)
                                    AND (@artistUuid IS NULL OR a.uuid=@artistUuid)
                                    AND (@afterUuid IS NULL OR sh.display_date COLLATE "C"<@sort COLLATE "C" OR (sh.display_date=@sort AND sh.uuid>@afterUuid))
                                ORDER BY sh.display_date COLLATE "C" DESC,sh.uuid LIMIT @take
                                """,
                            new
                            {
                                survivor,
                                artistUuid,
                                afterUuid = after?.Uuid,
                                sort = after?.Sort,
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: cancellationToken
                        )
                    )
                ).ToArray();
                return Page(
                    rows,
                    limit,
                    show =>
                        EncodeCursor(
                            "venue_shows",
                            venueUuid,
                            filter,
                            "date_desc_uuid",
                            show.display_date,
                            show.show_uuid
                        )
                );
            },
            cancellationToken
        );
    }
}
