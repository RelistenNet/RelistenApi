using System;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data;

/// <summary>The category describes a performance; it does not identify it.</summary>
public enum PerformanceType
{
    Unknown,
    Afternoon,
    Early,
    Late,
    Other,
}

/// <summary>
/// Owns durable performance identity and evidence-backed recording/setlist assignments.
/// A source keeps one primary show for existing playback contracts. Combined or unresolved
/// coverage retains that association without authorizing performance-specific enrichment.
/// </summary>
public sealed class ShowPerformanceService(DbService db)
{
    // Rebuilds and explicit assignments share this artist lock. It serializes the one-time
    // survivor claim and source moves, including when called inside an import transaction.
    internal const int LockNamespace = 0x52535046;

    /// <summary>
    /// Locks before reading or writing recording rows in an existing catalog transaction.
    /// Fetch HTTP inputs first. Consistent lock order prevents a rebuild and item writer
    /// from waiting on one another's source and artist locks.
    /// </summary>
    public Task LockArtistAsync(Artist artist)
    {
        if (Transaction.Current == null)
        {
            throw new InvalidOperationException(
                "Acquire the performance lock inside a catalog transaction."
            );
        }

        return db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(@lockNamespace,@artistId)",
                new { lockNamespace = LockNamespace, artistId = artist.id }
            )
        );
    }

    /// <summary>Whether an existing primary association supports performance-specific enrichment.</summary>
    public Task<bool> CanEnrichSourceAsync(Artist artist, int sourceId) =>
        db.WithWriteConnection(connection =>
            connection.QuerySingleAsync<bool>(
                """
                SELECT EXISTS(SELECT 1 FROM source_show_assignments a JOIN sources src ON src.id=a.source_id
                    JOIN shows sh ON sh.id=a.show_id WHERE src.id=@sourceId AND src.artist_id=@artistId
                    AND src.display_date=sh.display_date AND a.basis_display_date=src.display_date
                    AND a.assignment_kind IN ('accepted','legacy')
                    AND upper(src.display_date) NOT LIKE '%X%'
                    AND (SELECT count(*) FROM shows other WHERE other.artist_id=@artistId AND other.display_date=sh.display_date
                        AND EXISTS(SELECT 1 FROM sources os WHERE os.show_id=other.id))=1)
                """,
                new { sourceId, artistId = artist.id }
            )
        );

    /// <summary>Retains additional provider evidence without replacing an accepted assignment.</summary>
    public Task RecordSourceEvidenceAsync(
        Artist artist,
        int sourceId,
        string provider,
        string evidenceJson
    ) =>
        db.WithWriteConnection(connection =>
            connection.ExecuteAsync(
                """
                UPDATE source_show_assignments a SET evidence=jsonb_set(a.evidence,'{provider_evidence}',
                    COALESCE(a.evidence->'provider_evidence','{}'::jsonb)||jsonb_build_object(@provider,@evidenceJson::jsonb)),
                    updated_at=statement_timestamp()
                FROM sources src WHERE src.id=a.source_id AND src.id=@sourceId AND src.artist_id=@artistId
                """,
                new
                {
                    sourceId,
                    artistId = artist.id,
                    provider,
                    evidenceJson,
                }
            )
        );

    /// <summary>
    /// Accepts a provider or reviewed performance key, retaining the old show UUID for
    /// the first supported performance of an existing date. The caller must retain the
    /// key across date/label corrections; evidence is a JSON object describing its basis.
    /// Recording, set and track identities are unchanged. Rebuild aggregates after commit.
    /// </summary>
    public async Task<Show> AssignSourceAsync(
        Artist artist,
        int sourceId,
        string performanceKey,
        string displayDate,
        PerformanceType performanceType = PerformanceType.Unknown,
        string? label = null,
        string evidence = "{}",
        string assignmentKind = "accepted"
    )
    {
        if (assignmentKind != "accepted")
        {
            return await AssignDefaultSourceAsync(
                artist,
                sourceId,
                displayDate,
                evidence,
                assignmentKind
            );
        }

        if (string.IsNullOrWhiteSpace(performanceKey) || performanceKey == "legacy")
        {
            throw new ArgumentException(
                "A stable provider or reviewed performance key is required.",
                nameof(performanceKey)
            );
        }

        var date = ParseDate(displayDate);
        using var scope = CreateScope();
        var result = await db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(@lockNamespace,@artistId)",
                new { lockNamespace = LockNamespace, artistId = artist.id }
            );
            var source = await connection.QuerySingleAsync<Source>(
                "SELECT * FROM sources WHERE id=@sourceId AND artist_id=@artistId FOR UPDATE",
                new { sourceId, artistId = artist.id }
            );
            var show = await ResolveAcceptedPerformanceAsync(
                connection,
                artist,
                source,
                performanceKey,
                displayDate,
                date
            );
            await UpdatePerformanceAsync(
                connection,
                show.id,
                performanceKey,
                displayDate,
                date,
                performanceType,
                label
            );

            await PersistPrimaryAssignmentAsync(connection, source, show, assignmentKind, evidence);
            return await connection.QuerySingleAsync<Show>(
                "SELECT * FROM shows WHERE id=@id",
                new { show.id }
            );
        });
        scope.Complete();
        return result;
    }

    /// <summary>
    /// Publishes uncertain performance coverage under the stable date default, retaining
    /// an existing primary association. Uncertainty is internal and does not block playback.
    /// Partial dates remain source-specific; their equality is not evidence of one event.
    /// </summary>
    public async Task<Show> AssignDefaultSourceAsync(
        Artist artist,
        int sourceId,
        string displayDate,
        string evidence = "{}",
        string assignmentKind = "unresolved"
    )
    {
        if (assignmentKind is not ("unresolved" or "combined" or "legacy"))
        {
            throw new ArgumentException(
                "A default association cannot accept a specific performance.",
                nameof(assignmentKind)
            );
        }

        var date = ParseDate(displayDate);
        using var scope = CreateScope();
        await LockArtistAsync(artist);
        var show = await db.WithWriteConnection(async connection =>
        {
            var source = await connection.QuerySingleAsync<Source>(
                "SELECT * FROM sources WHERE id=@sourceId AND artist_id=@artistId FOR UPDATE",
                new { sourceId, artistId = artist.id }
            );
            var isPartial = displayDate.Contains("X", StringComparison.OrdinalIgnoreCase);
            var current = source.show_id.HasValue
                ? await connection.QuerySingleAsync<Show>(
                    "SELECT * FROM shows WHERE id=@id AND artist_id=@artistId",
                    new { id = source.show_id.Value, artistId = artist.id }
                )
                : await connection.QuerySingleOrDefaultAsync<Show>(
                    """
                    SELECT sh.* FROM shows sh WHERE sh.artist_id=@artistId AND sh.display_date=@displayDate
                        AND (NOT @isPartial OR sh.performance_key=@sourceKey)
                    ORDER BY sh.is_legacy_survivor DESC,sh.id LIMIT 1
                    """,
                    new
                    {
                        artistId = artist.id,
                        displayDate,
                        isPartial,
                        sourceKey = "source:" + source.uuid,
                    }
                );
            if (current == null)
            {
                current = await connection.QuerySingleAsync<Show>(
                    """
                    INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor)
                    VALUES(@artistId,@date,@displayDate,@uuid,now(),@key,NOT @isPartial)
                    RETURNING *
                    """,
                    new
                    {
                        artistId = artist.id,
                        date,
                        displayDate,
                        uuid = Guid.NewGuid(),
                        key = isPartial ? "source:" + source.uuid : "legacy",
                        isPartial,
                    }
                );
            }

            await PersistPrimaryAssignmentAsync(
                connection,
                source,
                current,
                assignmentKind,
                evidence
            );
            return current;
        });
        scope.Complete();
        return show;
    }

    /// <summary>Links one provider setlist to a supported canonical performance.</summary>
    public async Task AssignSetlistAsync(
        Artist artist,
        long setlistShowId,
        Guid showUuid,
        string evidence = "{}"
    )
    {
        using var scope = CreateScope();
        await db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(@lockNamespace,@artistId)",
                new { lockNamespace = LockNamespace, artistId = artist.id }
            );
            var matched = await connection.ExecuteAsync(
                """
                INSERT INTO setlist_show_assignments(setlist_show_id,show_id,evidence)
                SELECT sl.id,sh.id,@evidence::jsonb FROM setlist_shows sl JOIN shows sh ON sh.artist_id=sl.artist_id
                WHERE sl.artist_id=@artistId AND sl.id=@setlistShowId AND sh.uuid=@showUuid
                ON CONFLICT(setlist_show_id) DO UPDATE SET show_id=EXCLUDED.show_id,evidence=EXCLUDED.evidence,updated_at=statement_timestamp()
                """,
                new
                {
                    artistId = artist.id,
                    setlistShowId,
                    showUuid,
                    evidence,
                }
            );
            if (matched != 1)
            {
                throw new InvalidOperationException(
                    "The setlist and performance must belong to the requested artist."
                );
            }
        });
        scope.Complete();
    }

    private static Task PersistPrimaryAssignmentAsync(
        IDbConnection connection,
        Source source,
        Show show,
        string assignmentKind,
        string evidence
    ) =>
        connection.ExecuteAsync(
            """
            INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
            VALUES(@sourceId,@showId,@assignmentKind,@basisDate,@evidence::jsonb)
            ON CONFLICT(source_id) DO UPDATE SET show_id=EXCLUDED.show_id,
                assignment_kind=EXCLUDED.assignment_kind,basis_display_date=EXCLUDED.basis_display_date,
                evidence=CASE WHEN source_show_assignments.show_id=EXCLUDED.show_id
                    AND source_show_assignments.evidence ? 'provider_evidence'
                    THEN EXCLUDED.evidence||jsonb_build_object('provider_evidence',source_show_assignments.evidence->'provider_evidence')
                    ELSE EXCLUDED.evidence END,updated_at=statement_timestamp();
            UPDATE sources SET show_id=@showId WHERE id=@sourceId;
            """,
            new
            {
                sourceId = source.id,
                showId = show.id,
                assignmentKind,
                // A fallback keeps the last supported date as its correction basis.
                // Rebuild can advance an unclaimed identity when its recordings agree.
                basisDate = assignmentKind == "accepted" ? source.display_date : show.display_date,
                evidence,
            }
        );

    private static Task UpdatePerformanceAsync(
        IDbConnection connection,
        int showId,
        string performanceKey,
        string displayDate,
        DateTime date,
        PerformanceType performanceType,
        string? label
    ) =>
        connection.ExecuteAsync(
            """
            UPDATE shows sh SET performance_key=@performanceKey,date=@date,display_date=@displayDate,
                performance_type=@type,performance_label=@label,
                is_legacy_survivor=sh.is_legacy_survivor AND NOT EXISTS(
                    SELECT 1 FROM shows other WHERE other.artist_id=sh.artist_id
                        AND other.display_date=@displayDate AND other.is_legacy_survivor AND other.id<>sh.id),
                updated_at=statement_timestamp()
            WHERE sh.id=@showId AND (sh.performance_key,sh.date,sh.display_date,sh.performance_type,sh.performance_label)
                IS DISTINCT FROM (@performanceKey,@date,@displayDate,@type,@label);
            """,
            new
            {
                performanceKey,
                date,
                displayDate,
                type = performanceType.ToString().ToLowerInvariant(),
                label,
                showId,
            }
        );

    private static async Task<Show> ResolveAcceptedPerformanceAsync(
        IDbConnection connection,
        Artist artist,
        Source source,
        string performanceKey,
        string displayDate,
        DateTime date
    )
    {
        var show = await connection.QuerySingleOrDefaultAsync<Show>(
            """
            SELECT sh.* FROM shows sh JOIN show_performance_keys k ON k.show_id=sh.id
            WHERE k.artist_id=@artistId AND k.performance_key=@performanceKey
            """,
            new { artistId = artist.id, performanceKey }
        );

        if (show == null)
        {
            // A claim persists in show_performance_keys. Reordering a later import can
            // never choose a different survivor, even if labels or dates have changed.
            show = await connection.QuerySingleOrDefaultAsync<Show>(
                """
                SELECT sh.* FROM shows sh
                WHERE sh.artist_id=@artistId
                    AND (sh.id=@existingShowId OR (sh.display_date=@displayDate AND sh.performance_key LIKE 'legacy%'))
                    AND NOT EXISTS(SELECT 1 FROM show_performance_keys k WHERE k.show_id=sh.id)
                ORDER BY (sh.id=@existingShowId) DESC NULLS LAST,sh.is_legacy_survivor DESC,sh.id
                LIMIT 1 FOR UPDATE
                """,
                new
                {
                    artistId = artist.id,
                    displayDate,
                    existingShowId = source.show_id,
                }
            );
            if (show == null)
            {
                show = await connection.QuerySingleAsync<Show>(
                    """
                    INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor)
                    VALUES(@artistId,@date,@displayDate,@uuid,now(),@performanceKey,
                        NOT EXISTS(SELECT 1 FROM shows WHERE artist_id=@artistId AND display_date=@displayDate))
                    RETURNING *
                    """,
                    new
                    {
                        artistId = artist.id,
                        date,
                        displayDate,
                        uuid = Guid.NewGuid(),
                        performanceKey,
                    }
                );
            }

            await connection.ExecuteAsync(
                "INSERT INTO show_performance_keys(artist_id,performance_key,show_id) VALUES(@artistId,@performanceKey,@showId)",
                new
                {
                    artistId = artist.id,
                    performanceKey,
                    showId = show.id,
                }
            );
        }

        return show!;
    }

    private static TransactionScope CreateScope() =>
        new(
            TransactionScopeOption.Required,
            new TransactionOptions
            {
                IsolationLevel =
                    Transaction.Current?.IsolationLevel
                    ?? System.Transactions.IsolationLevel.ReadCommitted,
                Timeout = TimeSpan.FromMinutes(5),
            },
            TransactionScopeAsyncFlowOption.Enabled
        );

    private static DateTime ParseDate(string displayDate)
    {
        var sortable = displayDate.ToUpperInvariant();
        if (sortable.Length >= 10)
        {
            sortable = sortable[..10];
            if (sortable.Substring(5, 2) == "XX")
            {
                sortable = sortable[..4] + "-01-01";
            }
            else if (sortable.Substring(8, 2) == "XX")
            {
                sortable = sortable[..7] + "-01";
            }
        }
        return DateTime.ParseExact(sortable, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    // Runs inside the rebuild transaction. Exact dates retain ordinary grouping; a new
    // partially dated source gets its own identity because equal unknown dates prove no match.
    // Existing partial-date associations are retained by migration, pending supported review.
    internal const string SynchronizeSql = """
        SELECT pg_advisory_xact_lock(1381191750,@id);
        CREATE TEMP TABLE performance_source_dates ON COMMIT DROP AS
        SELECT src.*,
            CASE WHEN sortable ~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' AND pg_input_is_valid(sortable,'date')
                THEN sortable::date END AS catalog_date
        FROM (SELECT src.*,
            CASE WHEN upper(display_date) LIKE '____-XX-%' THEN left(display_date,4)||'-01-01'
                WHEN upper(display_date) LIKE '____-__-XX%' THEN left(display_date,7)||'-01'
                WHEN upper(display_date) LIKE '%X%' THEN left(display_date,4)||'-01-01'
                ELSE left(display_date,10) END AS sortable
            FROM sources src WHERE artist_id=@id) src;

        -- An ordinary show's corrected date keeps its original UUID when all its recordings agree.
        WITH corrected AS (
            SELECT a.show_id,min(src.display_date) AS display_date
            FROM source_show_assignments a JOIN performance_source_dates src ON src.id=a.source_id
            JOIN shows current_show ON current_show.id=a.show_id
            WHERE src.artist_id=@id AND src.catalog_date IS NOT NULL
                AND (a.assignment_kind='legacy' OR current_show.performance_key LIKE 'legacy%'
                    OR current_show.performance_key='source:'||src.uuid::text)
            GROUP BY a.show_id HAVING count(DISTINCT src.display_date)=1
                AND bool_and(src.display_date<>a.basis_display_date)
        )
        UPDATE shows sh SET display_date=c.display_date,
            performance_key=CASE WHEN sh.performance_key LIKE 'legacy%' THEN 'legacy:'||sh.uuid::text ELSE sh.performance_key END,
            is_legacy_survivor=sh.is_legacy_survivor AND NOT EXISTS(SELECT 1 FROM shows other WHERE other.artist_id=@id
                AND other.display_date=c.display_date AND other.is_legacy_survivor AND other.id<>sh.id)
                AND NOT EXISTS(SELECT 1 FROM corrected other JOIN shows old ON old.id=other.show_id
                    WHERE other.display_date=c.display_date AND other.show_id<sh.id AND old.is_legacy_survivor),
            updated_at=statement_timestamp()
        FROM corrected c WHERE sh.id=c.show_id AND (sh.performance_key LIKE 'legacy%' OR sh.performance_key LIKE 'source:%');
        DELETE FROM source_show_assignments a USING performance_source_dates src,shows sh
        WHERE a.source_id=src.id AND a.show_id=sh.id AND src.artist_id=@id AND a.assignment_kind='legacy'
            AND src.catalog_date IS NOT NULL AND src.display_date<>a.basis_display_date AND src.display_date<>sh.display_date;
        UPDATE source_show_assignments a SET basis_display_date=src.display_date
        FROM performance_source_dates src,shows sh WHERE a.source_id=src.id AND sh.id=a.show_id AND src.artist_id=@id
            AND src.catalog_date IS NOT NULL AND src.display_date=sh.display_date
            AND (a.assignment_kind='legacy' OR sh.performance_key LIKE 'legacy%' OR sh.performance_key='source:'||src.uuid::text);

        INSERT INTO shows(artist_id,date,display_date,updated_at,uuid,performance_key,is_legacy_survivor)
        SELECT DISTINCT ON (src.display_date,CASE WHEN upper(src.display_date) LIKE '%X%' THEN src.uuid END)
            src.artist_id,src.catalog_date,src.display_date,statement_timestamp(),
            gen_random_uuid(),
            CASE WHEN upper(src.display_date) LIKE '%X%' THEN 'source:'||src.uuid::text ELSE 'legacy' END,
            upper(src.display_date) NOT LIKE '%X%'
        FROM performance_source_dates src
        WHERE src.catalog_date IS NOT NULL
            AND NOT EXISTS(SELECT 1 FROM source_show_assignments a WHERE a.source_id=src.id)
            AND (upper(src.display_date) LIKE '%X%' OR NOT EXISTS(
                SELECT 1 FROM shows sh WHERE sh.artist_id=@id AND sh.display_date=src.display_date))
        ON CONFLICT DO NOTHING;
        INSERT INTO source_show_assignments(source_id,show_id,assignment_kind,basis_display_date,evidence)
        SELECT src.id,sh.id,
            CASE WHEN upper(src.display_date) LIKE '%X%' OR counts.n>1 THEN 'unresolved' ELSE 'legacy' END,
            src.display_date,jsonb_build_object('basis',CASE WHEN counts.n>1 THEN 'unresolved legacy primary' WHEN upper(src.display_date) LIKE '%X%' THEN 'source-specific partial date' ELSE 'single exact date' END)
        FROM performance_source_dates src
        JOIN LATERAL(SELECT count(*) AS n FROM shows s WHERE s.artist_id=@id AND s.display_date=src.display_date) counts ON true
        JOIN LATERAL(SELECT s.id FROM shows s WHERE s.artist_id=@id AND s.display_date=src.display_date
            AND (upper(src.display_date) NOT LIKE '%X%' OR s.performance_key='source:'||src.uuid::text)
            ORDER BY s.is_legacy_survivor DESC,s.id LIMIT 1) sh ON true
        WHERE src.catalog_date IS NOT NULL
        ON CONFLICT(source_id) DO NOTHING;
        UPDATE source_show_assignments a SET assignment_kind='unresolved',updated_at=statement_timestamp()
        FROM shows sh WHERE a.show_id=sh.id AND sh.artist_id=@id AND a.assignment_kind='legacy'
            AND (SELECT count(*) FROM shows other WHERE other.artist_id=@id AND other.display_date=sh.display_date)>1;
        UPDATE sources src SET show_id=a.show_id FROM source_show_assignments a
        WHERE src.artist_id=@id AND src.id=a.source_id AND src.show_id IS DISTINCT FROM a.show_id;
        UPDATE shows sh SET date=src.catalog_date
        FROM performance_source_dates src JOIN source_show_assignments a ON a.source_id=src.id
        WHERE sh.id=a.show_id AND (sh.performance_key LIKE 'legacy%' OR sh.performance_key='source:'||src.uuid::text) AND src.catalog_date IS NOT NULL AND src.display_date=sh.display_date
            AND sh.date IS DISTINCT FROM src.catalog_date;
        -- Only one exact performance and one provider setlist permit a date-based bootstrap.
        -- Persist its basis so a later split can remove it without touching explicit assignments.
        DELETE FROM setlist_show_assignments a USING shows sh,setlist_shows sl
        WHERE a.show_id=sh.id AND a.setlist_show_id=sl.id AND sh.artist_id=@id
            AND a.evidence->>'basis'='unique exact date'
            AND (sh.display_date<>to_char(sl.date,'YYYY-MM-DD') OR
                (SELECT count(*) FROM shows other WHERE other.artist_id=@id AND other.display_date=sh.display_date)<>1 OR
                (SELECT count(*) FROM setlist_shows other WHERE other.artist_id=@id AND other.date=sl.date)<>1 OR
                NOT EXISTS(SELECT 1 FROM source_show_assignments source_assignment
                    JOIN sources src ON src.id=source_assignment.source_id
                    WHERE source_assignment.show_id=sh.id AND source_assignment.assignment_kind IN ('accepted','legacy')
                        AND src.display_date=sh.display_date AND source_assignment.basis_display_date=src.display_date));
        INSERT INTO setlist_show_assignments(setlist_show_id,show_id,evidence)
        SELECT sl.id,sh.id,'{"basis":"unique exact date"}'::jsonb
        FROM shows sh JOIN setlist_shows sl ON sl.artist_id=sh.artist_id AND to_char(sl.date,'YYYY-MM-DD')=sh.display_date
        WHERE sh.artist_id=@id
            AND (SELECT count(*) FROM shows other WHERE other.artist_id=@id AND other.display_date=sh.display_date)=1
            AND (SELECT count(*) FROM setlist_shows other WHERE other.artist_id=@id AND other.date=sl.date)=1
            AND EXISTS(SELECT 1 FROM source_show_assignments source_assignment
                JOIN sources src ON src.id=source_assignment.source_id
                WHERE source_assignment.show_id=sh.id AND source_assignment.assignment_kind IN ('accepted','legacy')
                    AND src.display_date=sh.display_date AND source_assignment.basis_display_date=src.display_date)
        ON CONFLICT(setlist_show_id) DO NOTHING;
        """;
}
