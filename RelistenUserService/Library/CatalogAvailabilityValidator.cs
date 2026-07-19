using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RelistenUserService.Persistence;

namespace RelistenUserService.Library;

public readonly record struct CatalogReference(string CatalogType, Guid CatalogUuid);

public sealed class CatalogAvailabilityValidator(AccountsDbContext dbContext)
{
    public async Task<IReadOnlyList<CatalogReference>> FindUnavailableAsync(
        IReadOnlyCollection<CatalogReference> references,
        CancellationToken cancellationToken)
    {
        if (references.Count == 0)
        {
            return [];
        }

        var unique = references.Distinct().ToArray();
        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = Sql;

        var catalogTypes = command.CreateParameter();
        catalogTypes.ParameterName = "catalog_types";
        catalogTypes.Value = unique.Select(item => item.CatalogType).ToArray();
        command.Parameters.Add(catalogTypes);

        var catalogUuids = command.CreateParameter();
        catalogUuids.ParameterName = "catalog_uuids";
        catalogUuids.Value = unique.Select(item => item.CatalogUuid).ToArray();
        command.Parameters.Add(catalogUuids);

        var available = new HashSet<CatalogReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            available.Add(new(reader.GetString(0), reader.GetGuid(1)));
        }

        return unique.Where(reference => !available.Contains(reference)).ToArray();
    }

    // User data deliberately has no polymorphic foreign key into the catalog. Keep these rules in
    // lockstep with CatalogReferenceResolverSql: an accepted favorite must be immediately hydratable
    // by a fresh mobile install. Retained metadata may remain readable after licensing removal, but
    // it cannot become a new favorite once its dependencies or all streamable media are unavailable.
    private const string Sql = """
        WITH requested AS (
            SELECT catalog_type, catalog_uuid
            FROM unnest(
                CAST(@catalog_types AS text[]),
                CAST(@catalog_uuids AS uuid[])
            ) AS requested(catalog_type, catalog_uuid)
        )
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN artists entity ON r.catalog_type = 'artist' AND entity.uuid = r.catalog_uuid
        JOIN features feature ON feature.artist_id = entity.id
        UNION ALL
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN shows entity ON r.catalog_type = 'show' AND entity.uuid = r.catalog_uuid
        JOIN artists artist ON artist.id = entity.artist_id
        JOIN features feature ON feature.artist_id = artist.id
        JOIN show_source_information info ON info.show_id = entity.id
        JOIN years effective_year
            ON effective_year.artist_id = entity.artist_id
            AND (
                effective_year.id = entity.year_id
                OR (
                    entity.year_id IS NULL
                    AND effective_year.year = EXTRACT(YEAR FROM entity.date)::integer::text
                )
            )
        UNION ALL
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN sources entity ON r.catalog_type = 'source' AND entity.uuid = r.catalog_uuid
        JOIN shows show_entity ON show_entity.id = entity.show_id
        JOIN artists show_artist ON show_artist.id = show_entity.artist_id
        JOIN features show_feature ON show_feature.artist_id = show_artist.id
        JOIN show_source_information info ON info.show_id = show_entity.id
        JOIN years effective_year
            ON effective_year.artist_id = show_entity.artist_id
            AND (
                effective_year.id = show_entity.year_id
                OR (
                    show_entity.year_id IS NULL
                    AND effective_year.year =
                        EXTRACT(YEAR FROM show_entity.date)::integer::text
                )
            )
        JOIN artists source_artist ON source_artist.id = entity.artist_id
        JOIN features source_feature ON source_feature.artist_id = source_artist.id
        UNION ALL
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN source_tracks entity ON r.catalog_type = 'source_track' AND entity.uuid = r.catalog_uuid
        JOIN sources source ON source.id = entity.source_id
        JOIN source_sets source_set
            ON source_set.id = entity.source_set_id
            AND source_set.source_id = entity.source_id
        JOIN shows show_entity ON show_entity.id = source.show_id
        JOIN artists show_artist ON show_artist.id = show_entity.artist_id
        JOIN features show_feature ON show_feature.artist_id = show_artist.id
        JOIN show_source_information info ON info.show_id = show_entity.id
        JOIN years effective_year
            ON effective_year.artist_id = show_entity.artist_id
            AND (
                effective_year.id = show_entity.year_id
                OR (
                    show_entity.year_id IS NULL
                    AND effective_year.year =
                        EXTRACT(YEAR FROM show_entity.date)::integer::text
                )
            )
        JOIN artists source_artist ON source_artist.id = source.artist_id
        JOIN features source_feature ON source_feature.artist_id = source_artist.id
        WHERE entity.is_orphaned = false
            AND (entity.mp3_url IS NOT NULL OR entity.flac_url IS NOT NULL)
        UNION ALL
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN setlist_songs entity ON r.catalog_type = 'song' AND entity.uuid = r.catalog_uuid
        JOIN artists artist ON artist.id = entity.artist_id
        JOIN features feature ON feature.artist_id = artist.id
        UNION ALL
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN tours entity ON r.catalog_type = 'tour' AND entity.uuid = r.catalog_uuid
        JOIN artists artist ON artist.id = entity.artist_id
        JOIN features feature ON feature.artist_id = artist.id
        WHERE entity.start_date IS NOT NULL AND entity.end_date IS NOT NULL
        UNION ALL
        SELECT r.catalog_type, r.catalog_uuid
        FROM requested r
        JOIN venues entity ON r.catalog_type = 'venue' AND entity.uuid = r.catalog_uuid
        JOIN artists artist ON artist.id = entity.artist_id
        JOIN features feature ON feature.artist_id = artist.id
        WHERE entity.name IS NOT NULL
            AND entity.location IS NOT NULL
            AND entity.upstream_identifier IS NOT NULL
            AND entity.slug IS NOT NULL;
        """;
}
