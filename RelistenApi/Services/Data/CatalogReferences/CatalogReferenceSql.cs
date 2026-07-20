namespace Relisten.Data
{
    internal static class CatalogReferenceSql
    {
        public const string Requested = @"
WITH requested AS (
    SELECT catalog_type, catalog_uuid
    FROM unnest(CAST(@catalog_types AS text[]), CAST(@catalog_uuids AS uuid[]))
        AS requested(catalog_type, catalog_uuid)
)";
    }
}
