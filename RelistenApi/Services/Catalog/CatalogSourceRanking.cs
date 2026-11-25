namespace Relisten.Catalog;

/// <summary>
/// The "best source first" order clients use to pick a show's default recording, mirrored
/// from relisten-web's <c>sortSources</c> (src/lib/sortSources.ts): soundboards, then
/// Charlie Miller (GD) / Peter Costello (JRAD) transfers, then the higher etree ID, then
/// the higher weighted rating. relisten-mobile ranks favorited or downloaded sources first,
/// which is per-listener state the server cannot see, and skips the etree step.
/// </summary>
/// <remarks>
/// The order is defined once, by <c>norm.source_preference</c> (027_song_files.sql), because
/// <c>norm.refresh_song_counts</c> also stores it as <c>norm.song_files.source_rank</c>. The
/// etree ID is the last all-digit dot segment of the upstream identifier. Web compares a
/// missing ID as equal to any ID, which is not a total order, so missing IDs rank last here.
/// Like web, an unrated source counts as a zero rating. UUID makes the order total.
/// </remarks>
public static class CatalogSourceRanking
{
    /// <summary>ORDER BY term for a <c>sources</c> row aliased <paramref name="alias"/>.</summary>
    public static string OrderBy(string alias) => $"norm.source_preference({alias})";
}
