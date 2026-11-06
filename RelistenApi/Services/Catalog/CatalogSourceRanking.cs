namespace Relisten.Catalog;

/// <summary>
/// The "best source first" order clients use to pick a show's default recording, mirrored
/// from relisten-web's <c>sortSources</c> (src/lib/sortSources.ts): soundboards, then
/// Charlie Miller (GD) / Peter Costello (JRAD) transfers, then the higher etree ID, then
/// the higher weighted rating. relisten-mobile ranks favorited or downloaded sources first,
/// which is per-listener state the server cannot see, and skips the etree step.
/// </summary>
public static class CatalogSourceRanking
{
    /// <summary>
    /// ORDER BY terms for a <c>sources</c> row aliased <paramref name="alias"/>. The etree ID
    /// is the last all-digit dot segment of the upstream identifier. Web compares a missing
    /// ID as equal to any ID, which is not a total order, so missing IDs rank last here.
    /// Like web, an unrated source counts as a zero rating. UUID makes the order total.
    /// </summary>
    public static string OrderBy(string alias) =>
        $"""
            {alias}.is_soundboard DESC,
            concat({alias}.taper,{alias}.transferrer,{alias}.source) ~* '(charlie miller)|(peter costello)' DESC,
            (SELECT segment::numeric FROM unnest(string_to_array({alias}.upstream_identifier,'.'))
                 WITH ORDINALITY etree(segment,position)
             WHERE segment ~ '^[0-9]+$' ORDER BY position DESC LIMIT 1) DESC NULLS LAST,
            COALESCE({alias}.avg_rating_weighted,0) DESC,
            {alias}.uuid
            """;
}
