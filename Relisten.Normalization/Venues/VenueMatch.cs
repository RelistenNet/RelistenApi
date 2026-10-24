using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Normalization.Venues;

public sealed record VenueMatchResult(
    int RowsMapped,
    int VenuesCreated,
    int RowsLeft,
    int RowsUnplaced,
    int ShowsPlaced = 0
);

/// The deterministic venue pass (no LLM): maps legacy venue rows onto venues and creates the
/// venues nothing is in doubt about, as one run. It starts and ends every venue catalogue run
/// and is cheap enough to run after imports.
public sealed class VenueMatch(INormalizationDatabase db)
{
    public async Task<VenueMatchResult> RunAsync(
        IReadOnlyCollection<Guid>? artists,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        // Venues are cross-artist: one venue run at a time.
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('norm-venues'))",
            transaction: tx
        );

        var state = await VenueState.LoadAsync(connection, tx, artists);
        var plan = VenueMatcher.Match(state);
        var store = await NormStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Algorithm,
            VenueMatcher.Author,
            $"{plan.Evidence.Count} rows"
        );
        await store.WriteVenuesAsync(plan.NewVenues, NormWriteMode.FillOnly);
        await store.WriteVenueNamesAsync(plan.Names, NormWriteMode.FillOnly);
        await store.WriteVenueExternalIdsAsync(plan.ExternalIds, NormWriteMode.FillOnly);
        await store.WriteVenueEvidenceAsync(plan.Evidence, NormWriteMode.FillOnly);
        await store.RefreshVenueNamesAsync();
        var shows = await DeriveShowVenues(connection, tx, store, artists);
        // A machine's open question about a spelling it has now placed is answered.
        var mapped = plan.Evidence.Select(e => e.LegacyVenueUuid).ToHashSet();
        await store.CloseVenueReviewItemsAsync(
            state
                .Unmapped.Where(r => mapped.Contains(r.Uuid))
                .Select(VenueKeys.Of)
                .OfType<string>()
                .Distinct()
                .ToList(),
            "placed"
        );
        await store.FinishAsync();
        await tx.CommitAsync(cancellationToken);

        return new VenueMatchResult(
            plan.Evidence.Count,
            plan.NewVenues.Count,
            plan.Left.Sum(f => f.Count),
            plan.Unplaced.Count,
            shows
        );
    }

    /// Every show's venue in norm (the new tables hold the whole picture): derived from the
    /// mapping of the show's legacy venue row, resolved to a surviving venue. A ruling stays;
    /// a derived row whose mapping is gone is cleared.
    private static async Task<int> DeriveShowVenues(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction tx,
        NormStore store,
        IReadOnlyCollection<Guid>? artists
    )
    {
        var rows = (
            await connection.QueryAsync<(Guid Show, Guid? Venue, string? Basis, Guid? Current)>(
                """
                WITH RECURSIVE survivor AS (
                    SELECT uuid AS venue, uuid, merged_into, 0 AS depth FROM norm.venues
                    UNION ALL
                    SELECT s.venue, m.uuid, m.merged_into, s.depth + 1
                    FROM survivor s JOIN norm.venues m ON m.uuid = s.merged_into
                    WHERE s.depth < 20
                )
                SELECT sh.uuid, v.uuid, sv.basis, sv.venue_uuid
                FROM shows sh JOIN artists a ON a.id = sh.artist_id
                -- A show whose legacy venue is gone keeps no derived venue (a ruling stays).
                LEFT JOIN venues lv ON lv.id = sh.venue_id
                LEFT JOIN norm.venue_evidence e ON e.legacy_venue_uuid = lv.uuid
                LEFT JOIN survivor v ON v.venue = e.venue_uuid AND v.merged_into IS NULL
                LEFT JOIN norm.show_venues sv ON sv.show_uuid = sh.uuid
                WHERE (@all OR a.uuid = ANY(@artists))
                """,
                new { all = artists == null, artists = artists?.ToArray() ?? [] },
                tx
            )
        ).Where(r => r.Basis != ShowVenueBasis.Ruling).ToList();

        await store.ClearDerivedShowVenuesAsync(
            rows.Where(r => r.Venue == null && r.Basis != null).Select(r => r.Show).ToList()
        );
        var placed = rows.Where(r => r.Venue != null && (r.Basis == null || r.Current != r.Venue))
            .Select(r => new ShowVenueRecord(
                r.Show,
                r.Venue!.Value,
                VenueMatcher.Exact,
                ShowVenueBasis.Evidence
            ))
            .ToList();
        await store.WriteShowVenuesAsync(placed);
        return placed.Count;
    }
}

/// A venue question's key: a spelling within its locality, "fox theater@oakland|ca|US".
public static class VenueKeys
{
    public static string? Of(LegacyVenue row) =>
        row.Key == null ? null : $"{row.Key}@{row.Locality?.Key ?? "?"}";
}
