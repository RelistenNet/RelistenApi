using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizationVenueResolutionStore
{
    /// <summary>
    /// Returns unfinished attempts and leaf stale attempts that may have failed before a Work
    /// subject existed. Resume unfinished UUIDs; retry stale receipts with a new UUID. Review,
    /// unresolved and missing outcomes are attention/history or use their enrolled Work owner.
    /// A newer explicit retry hides its parent. UUID pagination is independent per call, so a
    /// new eligible UUID before a cursor is found on the next scan rather than this page sequence.
    /// </summary>
    public Task<VenueResolutionRecoveryPage> GetRecoveryAttemptsAsync(
        int limit = 100,
        Guid? afterUuid = null,
        CancellationToken ct = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        if (afterUuid == Guid.Empty)
        {
            throw new ArgumentException("A recovery cursor is an existing nonempty attempt UUID.");
        }
        return InTransaction(
            async (c, t) =>
            {
                // A stage writer locks its attempt header. Acquire those locks first, then use
                // a separate statement's fresh snapshot for the byte preflight: a statement that
                // waits on a writer must not measure its earlier stage image before the wait.
                var ids = (
                    await c.QueryAsync<Guid>(
                        new CommandDefinition(
                            """
                            SELECT a.attempt_uuid
                            FROM normalization.venue_resolution_attempts a
                            WHERE (@afterUuid::uuid IS NULL OR a.attempt_uuid>@afterUuid)
                                AND (a.result IS NULL OR a.result->>'Disposition'=@stale)
                                AND NOT EXISTS(SELECT 1 FROM normalization.venue_resolution_attempts child
                                    WHERE child.previous_attempt_uuid=a.attempt_uuid)
                            ORDER BY a.attempt_uuid LIMIT @take FOR UPDATE OF a
                            """,
                            new
                            {
                                afterUuid,
                                stale = ((int)VenueResolutionDisposition.Stale).ToString(
                                    System.Globalization.CultureInfo.InvariantCulture
                                ),
                                take = limit + 1,
                            },
                            t,
                            cancellationToken: ct
                        )
                    )
                ).ToArray();
                var selected = ids.Take(limit).ToArray();
                var bytes = await c.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        """
                        SELECT COALESCE(sum(octet_length(a.request::text)+COALESCE(octet_length(a.preparation::text),0)
                            +COALESCE(octet_length(a.decision::text),0)+COALESCE(octet_length(a.result::text),0)
                            +COALESCE((SELECT sum(octet_length(s.stage::text)+COALESCE(octet_length(s.ticket::text),0)
                                +COALESCE(octet_length(s.receipt::text),0)+COALESCE(octet_length(s.outcome::text),0))
                                FROM normalization.venue_resolution_stages s WHERE s.attempt_uuid=a.attempt_uuid),0)),0)::bigint
                        FROM normalization.venue_resolution_attempts a WHERE a.attempt_uuid=ANY(@selected)
                        """,
                        new { selected },
                        t,
                        cancellationToken: ct
                    )
                );
                if (bytes > MaximumCheckpointBytes)
                {
                    throw new InvalidOperationException(
                        "Venue recovery page exceeds 32 MiB; request a smaller page."
                    );
                }
                var attempts = new VenueResolutionAttempt[selected.Length];
                for (var index = 0; index < selected.Length; index++)
                {
                    attempts[index] = (await Read(c, t, selected[index], false, ct))!;
                }
                return new VenueResolutionRecoveryPage(
                    attempts,
                    ids.Length > limit ? selected[^1] : null
                );
            },
            ct
        );
    }
}
