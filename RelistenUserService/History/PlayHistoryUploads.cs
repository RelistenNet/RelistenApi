using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Relisten.Accounts.Contracts.History;
using Relisten.Identifiers;
using RelistenUserService.Identity.Entities;
using RelistenUserService.Persistence;

namespace RelistenUserService.History;

public sealed class PlayHistoryUploads(AccountsDbContext database, TimeProvider clock)
{
    internal const int ContractVersion = 1;
    internal const int MaximumPlays = 500;

    private static readonly JsonSerializerOptions WireJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
    };

    // The purge locks the user FOR UPDATE. Recheck activity under the conflicting
    // lock so a request authenticated before purge cannot create a new play after it.
    // Only rows returned by the account insert can contribute to popularity.
    private const string InsertPlaysSql = """
        WITH owner AS MATERIALIZED (
            SELECT id FROM identity.users
            WHERE id = @user_id AND status = @active_status
            FOR SHARE
        ), submitted AS MATERIALIZED (
            SELECT * FROM jsonb_to_recordset(@plays) AS play (
                play_id text, qualified_at timestamptz, started_at timestamptz,
                track_id text, track_uuid uuid, playlist_id text, playlist_entry_id text,
                catalog_duration_ms integer, app_type smallint, offline boolean, origin smallint
            )
        ), added AS (
            INSERT INTO user_data.plays (
                user_id, play_id, qualified_at, started_at, received_at, track_id,
                playlist_id, playlist_entry_id, catalog_duration_ms, app_type, offline, origin
            )
            SELECT owner.id, play.play_id, play.qualified_at, play.started_at, @received_at,
                   play.track_id, play.playlist_id, play.playlist_entry_id, play.catalog_duration_ms,
                   play.app_type, play.offline, play.origin
            FROM owner CROSS JOIN submitted play
            -- Concurrent batches acquire overlapping play keys in the same order.
            ORDER BY play.qualified_at, play.play_id
            ON CONFLICT (user_id, qualified_at, play_id) DO NOTHING
            RETURNING play_id, received_at, app_type, origin
        ), recorded AS (
            INSERT INTO public.source_track_plays (source_track_uuid, app_type, created_at, user_uuid)
            SELECT submitted.track_uuid, added.app_type, added.received_at, NULL
            FROM added JOIN submitted USING (play_id)
            WHERE added.origin = 1
        )
        SELECT EXISTS (SELECT FROM owner), ARRAY(SELECT play_id FROM added);
        """;

    /// <summary>Returns null if the account is no longer active when a valid upload reaches the database.</summary>
    public async Task<PlayBatchUploaded?> UploadAsync(
        UserId userId,
        IReadOnlyList<JsonElement> plays,
        CancellationToken cancellationToken
    )
    {
        var receivedAt = clock.GetUtcNow();
        var results = new PlayUploadResult[plays.Count];
        var submitted = new Dictionary<string, (ValidatedPlay Play, int Position)>();
        var seen = new HashSet<PlayId>();
        for (var index = 0; index < plays.Count; index++)
        {
            // The first unambiguous ID owns the batch position even if another field is invalid.
            var playId = ReadPlayId(plays[index]);
            if (playId is { } validId && !seen.Add(validId))
            {
                results[index] = new(playId, "duplicate");
                continue;
            }

            var play = playId.HasValue ? Validate(plays[index], receivedAt) : null;
            if (play is null)
            {
                results[index] = new(playId, "rejected");
                continue;
            }

            submitted.Add(play.PlayId, (play, index));
            // The database returns only new rows. Other submitted IDs already exist.
            results[index] = new(playId, "duplicate");
        }

        if (submitted.Count == 0)
        {
            return new(ContractVersion, results);
        }

        var acceptedIds = await InsertAsync(
            userId,
            receivedAt,
            submitted.Values.Select(item => item.Play),
            cancellationToken
        );
        if (acceptedIds is null)
        {
            return null;
        }

        foreach (var id in acceptedIds)
        {
            var index = submitted[id].Position;
            results[index] = results[index] with { Status = "accepted" };
        }
        return new(ContractVersion, results);
    }

    private async Task<string[]?> InsertAsync(
        UserId userId,
        DateTimeOffset receivedAt,
        IEnumerable<ValidatedPlay> plays,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.Serialize(plays, WireJson);
        await using var transaction = await database.Database.BeginTransactionAsync(
            cancellationToken
        );
        await using var command = new NpgsqlCommand(
            InsertPlaysSql,
            (NpgsqlConnection)database.Database.GetDbConnection(),
            (NpgsqlTransaction)transaction.GetDbTransaction()
        );
        command.Parameters.AddWithValue("user_id", userId.Value);
        command.Parameters.AddWithValue("active_status", UserStatuses.Active);
        command.Parameters.AddWithValue("received_at", receivedAt);
        command.Parameters.AddWithValue("plays", NpgsqlDbType.Jsonb, payload);

        string[]? acceptedIds;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            acceptedIds = reader.GetBoolean(0) ? reader.GetFieldValue<string[]>(1) : null;
        }
        await transaction.CommitAsync(cancellationToken);
        return acceptedIds;
    }

    private static PlayId? ReadPlayId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        PlayId? result = null;
        try
        {
            foreach (var field in item.EnumerateObject())
            {
                if (!field.NameEquals("play_id"))
                {
                    continue;
                }

                // An ambiguous ID must not claim a position in the batch.
                if (
                    result.HasValue
                    || field.Value.ValueKind != JsonValueKind.String
                    || !PlayId.TryParse(field.Value.GetString(), out var id)
                )
                {
                    return null;
                }
                result = id;
            }
        }
        catch (InvalidOperationException)
        {
            // A JSON token may contain an invalid escaped Unicode string.
            return null;
        }
        return result;
    }

    private static ValidatedPlay? Validate(JsonElement item, DateTimeOffset receivedAt)
    {
        PlayUpload? play;
        try
        {
            play = item.Deserialize<PlayUpload>(WireJson);
        }
        catch (JsonException)
        {
            return null;
        }

        if (
            play is null
            || !TrackId.TryParse(play.TrackId, out var track)
            || play.StartedAt > play.QualifiedAt
            || play.QualifiedAt < receivedAt.AddMonths(-24)
            || play.QualifiedAt > receivedAt.AddDays(1)
            // The anonymous table uses the legacy app_type smallints, from Unknown to Android.
            || play.AppType is < 0 or > 4
            || play.CatalogDurationMs < 0
        )
        {
            return null;
        }

        var origin = play.Origin switch
        {
            "live" => PlayOrigin.Live,
            "anonymous_import" => PlayOrigin.AnonymousImport,
            _ => (PlayOrigin?)null,
        };
        if (origin is null)
        {
            return null;
        }

        return new(
            play.PlayId.Value,
            play.QualifiedAt,
            play.StartedAt,
            play.TrackId,
            track.Value,
            play.PlaylistId?.Value,
            play.PlaylistEntryId?.Value,
            play.CatalogDurationMs,
            play.AppType,
            play.Offline,
            origin.Value
        );
    }

    private enum PlayOrigin : short
    {
        Live = 1,
        AnonymousImport = 2,
    }

    private sealed record ValidatedPlay(
        string PlayId,
        DateTimeOffset QualifiedAt,
        DateTimeOffset StartedAt,
        string TrackId,
        Guid TrackUuid,
        string? PlaylistId,
        string? PlaylistEntryId,
        int? CatalogDurationMs,
        short AppType,
        bool Offline,
        PlayOrigin Origin
    );
}
