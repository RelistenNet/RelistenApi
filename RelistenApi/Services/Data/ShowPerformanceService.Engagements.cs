using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data;

public sealed partial class ShowPerformanceService
{
    /// <summary>
    /// Creates or selects a supported engagement without a recording. The caller must explicitly
    /// establish a distinct new engagement or name the existing show UUID. An unresolved provider
    /// account stays unassigned. Date equality never chooses a show, and recordings never move.
    /// </summary>
    /// <remarks>
    /// Existing identity keys keep their registered UUID across metadata corrections. A target
    /// conflicting with that registration fails. Date is asserted on every call. Omitted
    /// presentation preserves an existing type/label; an explicit presentation updates both,
    /// including Unknown/null to clear them. Retain supporting provider/review evidence
    /// before calling. This catalog operation does not accept or select a normalized edition.
    /// Rebuild/invalidate derived artist data after the surrounding catalog transaction commits.
    /// </remarks>
    public async Task<Show> CreateOrSelectPerformanceAsync(
        Artist artist,
        PerformanceEngagementIdentity identity,
        PerformanceEngagementTarget target,
        string displayDate,
        PerformanceEngagementPresentation? presentation = null,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Artist is mutable. Locking and every later lookup must use the same identity even
        // if the caller updates its DTO while this operation waits for the artist lock.
        var artistId = artist.id;
        var artistUuid = artist.uuid;
        ValidateEngagementRequest(artistId, artistUuid, identity, target, presentation);
        var date = ParseDate(displayDate);
        var key = identity.PerformanceKey;

        using var scope = CreateScope();
        var show = await db.WithWriteConnection(async connection =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(@lockNamespace,@artistId)",
                    new { lockNamespace = LockNamespace, artistId },
                    cancellationToken: cancellationToken
                )
            );
            var currentArtist = await connection.QuerySingleOrDefaultAsync<Guid?>(
                new CommandDefinition(
                    "SELECT uuid FROM artists WHERE id=@id",
                    new { id = artistId },
                    cancellationToken: cancellationToken
                )
            );
            if (currentArtist != artistUuid)
            {
                throw new ArgumentException(
                    "The requested catalog artist is missing or has changed identity.",
                    nameof(artist)
                );
            }
            var registered = await ReadRegisteredPerformanceAsync(
                connection,
                artistId,
                key,
                cancellationToken
            );
            if (registered != null && registered.artist_id != artistId)
            {
                throw new InvalidOperationException(
                    "The registered engagement points outside its artist; repair the association before using it."
                );
            }
            if (
                registered != null
                && target is PerformanceEngagementTarget.Existing existing
                && existing.ShowUuid != registered.uuid
            )
            {
                throw new InvalidOperationException(
                    "The engagement key already identifies another show; review correspondence before changing it."
                );
            }
            if (registered == null)
            {
                registered = await ResolveUnregisteredEngagementAsync(
                    connection,
                    artistId,
                    target,
                    key,
                    displayDate,
                    date,
                    cancellationToken
                );
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO show_performance_keys(artist_id,performance_key,show_id) VALUES(@artistId,@key,@showId)
                        """,
                        new
                        {
                            artistId,
                            key,
                            showId = registered.id,
                        },
                        cancellationToken: cancellationToken
                    )
                );
            }
            await UpdatePerformanceAsync(
                connection,
                registered.id,
                key,
                displayDate,
                date,
                presentation?.Type
                    ?? Enum.Parse<PerformanceType>(registered.performance_type, ignoreCase: true),
                presentation == null ? registered.performance_label : presentation.Label,
                cancellationToken
            );
            cancellationToken.ThrowIfCancellationRequested();
            return await connection.QuerySingleAsync<Show>(
                new CommandDefinition(
                    "SELECT * FROM shows WHERE id=@id",
                    new { registered.id },
                    cancellationToken: cancellationToken
                )
            );
        });
        cancellationToken.ThrowIfCancellationRequested();
        scope.Complete();
        return show;
    }

    private static void ValidateEngagementRequest(
        int artistId,
        Guid artistUuid,
        PerformanceEngagementIdentity identity,
        PerformanceEngagementTarget target,
        PerformanceEngagementPresentation? presentation
    )
    {
        if (artistId <= 0 || artistUuid == Guid.Empty)
        {
            throw new ArgumentException(
                "A current catalog artist ID and UUID are required.",
                "artist"
            );
        }
        if (presentation != null && !Enum.IsDefined(presentation.Type))
        {
            throw new ArgumentOutOfRangeException(nameof(presentation));
        }
        switch (identity)
        {
            case PerformanceEngagementIdentity.Provider provider:
                ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderName);
                ArgumentException.ThrowIfNullOrWhiteSpace(provider.ResourceType);
                ArgumentException.ThrowIfNullOrWhiteSpace(provider.ProviderKey);
                break;
            case PerformanceEngagementIdentity.Reviewed reviewed
                when reviewed.Key.StartsWith("review:", StringComparison.Ordinal)
                    && reviewed.Key.Length > "review:".Length:
                break;
            default:
                throw new ArgumentException(
                    "An engagement requires a qualified provider identity or an explicit review-prefixed key.",
                    nameof(identity)
                );
        }
        if (
            target is PerformanceEngagementTarget.Existing { ShowUuid: var showUuid }
                && showUuid == Guid.Empty
            || target
                is not (
                    PerformanceEngagementTarget.CreateNew
                    or PerformanceEngagementTarget.Existing
                )
        )
        {
            throw new ArgumentException(
                "Explicitly create a supported engagement or select a stable existing show UUID.",
                nameof(target)
            );
        }
    }

    private static Task<Show?> ReadRegisteredPerformanceAsync(
        System.Data.IDbConnection connection,
        int artistId,
        string performanceKey,
        CancellationToken ct = default
    ) =>
        connection.QuerySingleOrDefaultAsync<Show>(
            new CommandDefinition(
                """
                SELECT sh.* FROM shows sh JOIN show_performance_keys k ON k.show_id=sh.id
                WHERE k.artist_id=@artistId AND k.performance_key=@performanceKey
                """,
                new { artistId, performanceKey },
                cancellationToken: ct
            )
        );

    private static async Task<Show> ResolveUnregisteredEngagementAsync(
        System.Data.IDbConnection connection,
        int artistId,
        PerformanceEngagementTarget target,
        string key,
        string displayDate,
        DateTime date,
        CancellationToken ct
    )
    {
        if (target is PerformanceEngagementTarget.Existing selected)
        {
            return await connection.QuerySingleOrDefaultAsync<Show>(
                    new CommandDefinition(
                        "SELECT * FROM shows WHERE artist_id=@artistId AND uuid=@ShowUuid FOR UPDATE",
                        new { artistId, selected.ShowUuid },
                        cancellationToken: ct
                    )
                )
                ?? throw new ArgumentException(
                    "The selected performance must belong to the requested artist.",
                    nameof(target)
                );
        }
        // No source supplies a correspondence anchor. In particular, do not claim a legacy
        // survivor merely because its literal date equals this supported new engagement's date.
        return await connection.QuerySingleAsync<Show>(
            new CommandDefinition(
                """
                INSERT INTO shows(artist_id,date,display_date,uuid,updated_at,performance_key,is_legacy_survivor)
                VALUES(@artistId,@date,@displayDate,@uuid,now(),@key,
                    NOT EXISTS(SELECT 1 FROM shows WHERE artist_id=@artistId AND display_date=@displayDate)) RETURNING *
                """,
                new
                {
                    artistId,
                    date,
                    displayDate,
                    uuid = Guid.NewGuid(),
                    key,
                },
                cancellationToken: ct
            )
        );
    }
}
