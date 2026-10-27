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
)
{
    /// Another venue step held the lock and this match didn't wait (wait: false).
    public bool Deferred { get; init; }
}

/// Rows: how many placements were forgotten. Held: the rows a re-ask named, left to the model
/// for the rest of the run. Venues: those the forgotten rows were placed on, retired at the
/// end when nothing points at them.
public sealed record VenueForgetResult(
    int Rows,
    IReadOnlyList<Guid> Held,
    IReadOnlyList<Guid> Venues
);

/// A row a person asks about again: "Name@Location" (that town as written) or "Name" (any
/// town), compared by the venue key, so case and "The" don't matter.
public sealed record ReaskSpec(string Key, string? Location)
{
    public static ReaskSpec Parse(string spec)
    {
        var at = spec.LastIndexOf('@');
        var (name, location) = at < 0 ? (spec, null) : (spec[..at], spec[(at + 1)..].Trim());
        return new ReaskSpec(VenueNormalizer.Key(name) ?? name.Trim().ToLowerInvariant(), location);
    }

    public bool Matches(string name, string? location) =>
        VenueNormalizer.Key(name) == Key
        && (
            Location == null
            || string.Equals(location?.Trim(), Location, StringComparison.OrdinalIgnoreCase)
        );
}

/// The deterministic venue pass (no LLM): maps legacy venue rows onto venues and creates the
/// venues nothing is in doubt about, as one run. It starts and ends every venue catalogue run
/// and is cheap enough to run after imports.
public sealed class VenueMatch(INormalizationDatabase db)
{
    /// wait: false gives way when another venue step holds the lock (a best-effort match
    /// after an import; the nightly run converges) instead of waiting for it.
    public async Task<VenueMatchResult> RunAsync(
        IReadOnlyCollection<Guid>? artists,
        CancellationToken cancellationToken,
        bool wait = true,
        IReadOnlyCollection<Guid>? held = null
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        // Venues are cross-artist: one venue run at a time.
        if (wait)
        {
            await connection.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(hashtext('norm-venues'))",
                transaction: tx
            );
        }
        else if (
            !await connection.ExecuteScalarAsync<bool>(
                "SELECT pg_try_advisory_xact_lock(hashtext('norm-venues'))",
                transaction: tx
            )
        )
        {
            return new VenueMatchResult(0, 0, 0, 0) { Deferred = true };
        }

        var state = await VenueState.LoadAsync(connection, tx, artists, held);
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
        await store.MergeTwinVenuesAsync();
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
        await AskAboutNamesakesAsync(connection, tx, store);
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

    /// Forgets the machine's placements of these artists' rows so the run places them again:
    /// the rows a re-ask names ("The Greek Theatre@Redlands, CA", or a name in any town), and
    /// with rematch every row an older matcher placed (a rule fixed since: the current one
    /// places it again, and a row placed right lands on the venue it had). People's
    /// placements stay.
    public async Task<VenueForgetResult> ForgetAsync(
        IReadOnlyCollection<Guid> artists,
        IReadOnlyCollection<string> reask,
        bool rematch,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('norm-venues'))",
            transaction: tx
        );
        var placed = await connection.QueryAsync<(
            Guid Row,
            string Name,
            string? Location,
            string AuthorRef
        )>(
            """
            SELECT v.uuid, v.name, v.location, e.author_ref
            FROM norm.venue_evidence e JOIN venues v ON v.uuid = e.legacy_venue_uuid
            JOIN artists a ON a.id = v.artist_id
            WHERE a.uuid = ANY(@artists) AND e.author_type NOT IN ('user', 'moderator')
            """,
            new { artists = artists.ToArray() },
            tx
        );
        var asked = reask.Select(ReaskSpec.Parse).ToList();
        var reasked = placed
            .Where(p => asked.Any(a => a.Matches(p.Name, p.Location)))
            .Select(p => p.Row)
            .ToList();
        var stale = rematch
            ? placed
                .Where(p =>
                    p.AuthorRef.StartsWith("venue-matcher@", StringComparison.Ordinal)
                    && p.AuthorRef != VenueMatcher.Author
                )
                .Select(p => p.Row)
                .ToList()
            : [];
        var rows = reasked.Concat(stale).Distinct().ToList();
        if (rows.Count == 0)
        {
            return new VenueForgetResult(0, [], []);
        }

        var store = await NormStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Algorithm,
            VenueMatcher.Author,
            $"forget {rows.Count} rows ({reasked.Count} asked again, {stale.Count} placed by older matchers)"
        );
        var venues = await store.ForgetVenueRowsAsync(rows);
        await store.FinishAsync();
        await tx.CommitAsync(cancellationToken);
        return new VenueForgetResult(rows.Count, reasked, venues);
    }

    /// After a forget's run: the venues its rows left that nothing points at any more.
    public async Task<int> RetireAsync(
        IReadOnlyCollection<Guid> venues,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('norm-venues'))",
            transaction: tx
        );
        var store = await NormStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Algorithm,
            VenueMatcher.Author,
            "retire venues a forget left empty"
        );
        var retired = await store.RetireEmptyVenuesAsync(venues);
        await store.FinishAsync();
        await tx.CommitAsync(cancellationToken);
        return retired;
    }

    /// One name in two towns of one state is a suburb, a move or two places, and one name twice
    /// in one town (a site and a room) one place filed two ways or two: no rule can tell, so
    /// each is an open question for a person ("Cat's Cradle — Carrboro, NC / Chapel Hill, NC"). A question a person has answered isn't asked again; one whose venues
    /// no longer share the name is closed.
    private static async Task AskAboutNamesakesAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction tx,
        NormStore store
    )
    {
        var venues = (
            await connection.QueryAsync<(
                Guid Uuid,
                string Name,
                string Kind,
                string? Parent,
                string City,
                string? Region,
                string? Country,
                int Rows,
                int? First,
                int? Last,
                int[] ShowYears
            )>(
                """
                SELECT v.uuid, v.name, v.kind, p.name, v.city, v.region, v.country,
                       (SELECT count(*) FROM norm.venue_evidence e WHERE e.venue_uuid = v.uuid)::int,
                       y.first, y.last, coalesce(y.years, '{}')
                FROM norm.venues v LEFT JOIN norm.venues p ON p.uuid = v.parent_uuid
                LEFT JOIN LATERAL (
                    SELECT extract(year FROM min(sh.date))::int AS first,
                           extract(year FROM max(sh.date))::int AS last,
                           array_agg(extract(year FROM sh.date)::int) AS years
                    FROM norm.show_venues sv JOIN shows sh ON sh.uuid = sv.show_uuid
                    WHERE sv.venue_uuid = v.uuid
                ) y ON true
                WHERE v.merged_into IS NULL AND v.city IS NOT NULL AND v.country IS NOT NULL
                """,
                transaction: tx
            )
        ).Where(v => VenueNormalizer.Key(v.Name) != null).Select(v => (Venue: v, Name: string.Concat(VenueNormalizer.Words(VenueNormalizer.Key(v.Name)!)), Locality: new Locality(v.City, v.Region, v.Country))).ToList();
        string Where(IEnumerable<string> parts) => string.Join(" / ", parts);
        string Years(int? first, int? last) =>
            first == null ? ""
            : first == last ? $", {first}"
            : $", {first}–{last}";
        ReviewRecord Question(
            string question,
            string key,
            string title,
            string note,
            IReadOnlyList<Guid> uuids
        ) =>
            new(
                null,
                title,
                key,
                note,
                Subject: ReviewSubjects.Venue,
                VenueUuid: uuids[0],
                Question: question,
                Refs: new System.Text.Json.Nodes.JsonObject
                {
                    ["venues"] = new System.Text.Json.Nodes.JsonArray(
                        uuids.Select(u => (System.Text.Json.Nodes.JsonNode)u.ToString()).ToArray()
                    ),
                }
            );

        // Sites of one name in two towns of a state: a suburb, a move, or two places.
        var questions = venues
            .Where(v => v.Venue.Kind == VenueKinds.Site)
            .GroupBy(v => $"same:{v.Name}@{v.Locality.RegionKey}")
            .Where(g => g.Select(v => v.Locality.Key).Distinct().Count() > 1)
            .Select(g =>
            {
                var list = g.OrderByDescending(v => v.Venue.Rows).ToList();
                return Question(
                    ReviewQuestions.Namesake,
                    g.Key,
                    $"{list[0].Venue.Name} — "
                        + Where(
                            list.Select(v =>
                                $"{v.Venue.City}, {v.Venue.Region ?? v.Venue.Country} ({v.Venue.Rows} rows{Years(v.Venue.First, v.Venue.Last)})"
                            )
                        ),
                    "One name in two towns of one state: one place under a neighbouring town, a club that moved (two venues), or two places?"
                        + YearsNote(list.Select(v => v.Venue.ShowYears).ToList()),
                    list.Select(v => v.Venue.Uuid).ToList()
                );
            })
            .ToList();

        // One name twice in one town, as a site and a room or inside two sites (the twin merge
        // leaves those): one place filed two ways, or two (a room named after its building)?
        questions.AddRange(
            venues
                .GroupBy(v => $"same:{v.Name}@{v.Locality.Key}")
                .Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var list = g.OrderByDescending(v => v.Venue.Rows).ToList();
                    return Question(
                        ReviewQuestions.SameTown,
                        g.Key,
                        $"{list[0].Venue.Name} — {list[0].Venue.City}: "
                            + Where(
                                list.Select(v =>
                                    (
                                        v.Venue.Parent == null
                                            ? v.Venue.Kind
                                            : $"room of {v.Venue.Parent}"
                                    ) + $" ({v.Venue.Rows} rows)"
                                )
                            ),
                        "One name twice in one town: one place filed as two (merge, or move the room), or two places?",
                        list.Select(v => v.Venue.Uuid).ToList()
                    );
                })
        );

        var asked = (
            await connection.QueryAsync<(string Key, bool Open, bool ByPerson)>(
                """
                SELECT key, resolved_at IS NULL, author_type IN ('user', 'moderator')
                FROM norm.review_items WHERE subject = 'venue' AND key LIKE 'same:%'
                """,
                transaction: tx
            )
        ).ToList();
        var answered = asked.Where(a => !a.Open && a.ByPerson).Select(a => a.Key).ToHashSet();
        await store.WriteReviewItemsAsync(
            questions.Where(q => !answered.Contains(q.Key!)).ToList()
        );
        var current = questions.Select(q => q.Key).ToHashSet();
        await store.CloseVenueReviewItemsAsync(
            asked.Where(a => a.Open && !current.Contains(a.Key)).Select(a => a.Key).ToList(),
            "resolved"
        );
    }

    /// Neighbouring towns whose shows fall in separate years look like a move (two venues):
    /// said only with three years of shows on each side, a stray show or two allowed.
    internal static string YearsNote(IReadOnlyList<int[]> years)
    {
        var sides = years.Where(y => y.Distinct().Count() >= 3).ToList();
        if (sides.Count < 2 || sides.Count < years.Count)
        {
            return "";
        }

        // Each side's span without its outer tenth of shows (a stray filed under the wrong
        // town shouldn't stretch it).
        static (int From, int To) Span(int[] years)
        {
            var sorted = years.Order().ToArray();
            var trim = sorted.Length / 10;
            return (sorted[trim], sorted[^(trim + 1)]);
        }

        var strays = sides
            .SelectMany(
                (a, i) =>
                    sides
                        .Where((_, j) => j != i)
                        .Select(Span)
                        .Select(b => a.Count(y => y >= b.From && y <= b.To))
            )
            .Sum();
        return strays <= 2 ? " Their years barely overlap: a move?" : " Their years overlap.";
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
