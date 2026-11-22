using Dapper;
using FluentAssertions;
using Npgsql;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Identifiers;
using RelistenIngestionWorker.AccountDeletion;

namespace RelistenUserServiceTests;

[TestFixture]
[NonParallelizable]
public sealed class TestAccountDeletionSchema
{
    [Test]
    public async Task PurgeRemovesTheDeletedUsersRowsAndPreservesTheSurvivorsRows()
    {
        var database = new PostgresIntegrationDatabase();
        try
        {
            await database.StartAsync();
            // Use a fresh backend so pg_stat_xact_user_tables sees only the fixture writes.
            var connectionString = new NpgsqlConnectionStringBuilder(database.ConnectionString)
            {
                Pooling = false,
            }.ConnectionString;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            var tables = await GetUserOwnedTablesAsync(connection);
            var survivorPlaylist = PlaylistId.New().ToString();
            var deletedPlaylist = PlaylistId.New().ToString();
            var survivor = await database.CreateUserAsync("surviving_listener");
            var seeded = await SeedAccountDataAsync(connection, survivor, survivorPlaylist);
            tables
                .Should()
                .BeEquivalentTo(
                    seeded.Append("identity.users"),
                    "every fixture table should be discoverable by foreign key or subject"
                );

            // Follows reference a playlist by ID only, so the survivor's follow of the
            // deleted user's playlist remains and reads as unavailable. The survivor's
            // grant to the shared MCP client must not depend on the deleted user's grant.
            var mcpApplication = await CreateMcpApplicationAsync(connection);
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_follows (user_id, playlist_id, created_at)
                VALUES (@UserId, @DeletedPlaylist, now());
                INSERT INTO identity.openiddict_authorizations (id, application_id, subject, status, type)
                VALUES (@AuthorizationId, @McpApplication, @UserId, 'valid', 'permanent');
                """,
                new
                {
                    UserId = survivor.ToString(),
                    DeletedPlaylist = deletedPlaylist,
                    AuthorizationId = Guid.CreateVersion7(),
                    McpApplication = mcpApplication,
                }
            );
            var survivors = new Dictionary<string, string[]>();
            foreach (var table in tables)
            {
                var rows = await ReadRowsAsync(connection, table);
                rows.Should().NotBeEmpty("the surviving fixture needs a row in {0}", table);
                survivors.Add(table, rows);
            }

            var deleted = await database.CreateUserAsync("deleted_listener");
            await SeedAccountDataAsync(connection, deleted, deletedPlaylist);
            // Cross-account rows that must go: the deleted user's follow, report and MCP
            // grant, and the survivor's report on the deleted user's playlist.
            await connection.ExecuteAsync(
                """
                INSERT INTO user_data.playlist_follows (user_id, playlist_id, created_at)
                VALUES (@DeletedUser, @SurvivorPlaylist, now());
                INSERT INTO user_data.playlist_reports (id, user_id, playlist_id, reason, created_at)
                VALUES (@DeletedUsersReport, @DeletedUser, @SurvivorPlaylist, 'Fixture report', now()),
                       (@SurvivorsReport, @Survivor, @DeletedPlaylist, 'Fixture report', now());
                INSERT INTO identity.openiddict_authorizations (id, application_id, subject, status, type)
                VALUES (@AuthorizationId, @McpApplication, @DeletedUser, 'valid', 'permanent');
                """,
                new
                {
                    DeletedUser = deleted.ToString(),
                    Survivor = survivor.ToString(),
                    SurvivorPlaylist = survivorPlaylist,
                    DeletedPlaylist = deletedPlaylist,
                    DeletedUsersReport = PlaylistReportId.New().ToString(),
                    SurvivorsReport = PlaylistReportId.New().ToString(),
                    AuthorizationId = Guid.CreateVersion7(),
                    McpApplication = mcpApplication,
                }
            );
            foreach (var table in tables)
            {
                var rows = await ReadRowsAsync(connection, table);
                rows.Length.Should()
                    .BeGreaterThan(
                        survivors[table].Length,
                        "the deleted fixture needs a row in {0}",
                        table
                    );
            }

            // Both accounts have a play in the same chunk. Purge must delete one
            // account through the foreign key even after the chunk is compressed.
            var chunk = await connection.QuerySingleAsync<string>(
                "SELECT show_chunks('user_data.plays')::text"
            );
            await connection.ExecuteAsync(
                "CALL public.convert_to_columnstore(CAST(@chunk AS regclass))",
                new { chunk }
            );

            // A workflow whose job row is missing, as after a backup restore, purges by
            // user ID. The restore replay then verifies that a missing user is safe.
            var purge = new AccountPurge(database.ConnectionString);
            await purge.RunJobAsync(
                new(Guid.CreateVersion7(), deleted.ToString()),
                CancellationToken.None
            );
            await purge.RunForUserAsync(deleted.ToString(), CancellationToken.None);

            foreach (var table in tables)
            {
                var rows = await ReadRowsAsync(connection, table);
                rows.Should()
                    .Equal(
                        survivors[table],
                        "only the survivor's rows should remain in {0}",
                        table
                    );
            }
            (
                await connection.QuerySingleAsync<bool>(
                    "SELECT EXISTS (SELECT FROM identity.openiddict_applications WHERE id = @mcpApplication)",
                    new { mcpApplication }
                )
            )
                .Should()
                .BeTrue("the MCP client is shared by every account that authorized it");
        }
        finally
        {
            await database.StopAsync();
        }
    }

    private static Task<IEnumerable<string>> GetUserOwnedTablesAsync(NpgsqlConnection connection) =>
        connection.QueryAsync<string>(
            """
            WITH RECURSIVE owned(table_oid) AS (
                SELECT unnest(ARRAY[
                    'identity.users'::regclass,
                    'identity.openiddict_authorizations'::regclass,
                    'identity.openiddict_tokens'::regclass
                ])::oid
                UNION
                SELECT fk.conrelid
                FROM pg_constraint fk
                JOIN owned ON fk.confrelid = owned.table_oid
                WHERE fk.contype = 'f'
                    AND NOT EXISTS (SELECT FROM pg_inherits WHERE inhrelid = fk.conrelid)
            )
            SELECT format('%I.%I', namespace.nspname, relation.relname)
            FROM owned
            JOIN pg_class relation ON relation.oid = owned.table_oid
            JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
            ORDER BY 1
            """
        );

    private static Task<Guid> CreateMcpApplicationAsync(NpgsqlConnection connection) =>
        connection.QuerySingleAsync<Guid>(
            """
            INSERT INTO identity.openiddict_applications (id, client_id, client_type, display_name)
            VALUES (@Id, 'https://chatgpt.com/oauth/client.json', 'public', 'ChatGPT')
            RETURNING id
            """,
            new { Id = Guid.CreateVersion7() }
        );

    private static async Task<string[]> ReadRowsAsync(NpgsqlConnection connection, string table) =>
        (
            await connection.QueryAsync<string>(
                $"SELECT to_jsonb(owned_row)::text FROM {table} AS owned_row ORDER BY 1"
            )
        ).ToArray();

    // Each call seeds all discovered tables for one user. Adding a user-owned table
    // without extending this fixture fails the pre-purge assertions.
    private static async Task<IEnumerable<string>> SeedAccountDataAsync(
        NpgsqlConnection connection,
        UserId user,
        string playlistId
    )
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO identity.external_identities
                (id, user_id, issuer, provider_subject, last_login_at, created_at, updated_at)
            VALUES (@IdentityId, @UserId, 'https://accounts.google.com', @UserId, now(), now(), now());

            INSERT INTO identity.openiddict_authorizations (id, subject, status, type)
            VALUES (@AuthorizationId, @UserId, 'valid', 'permanent');
            INSERT INTO identity.openiddict_tokens (id, subject, status, type, authorization_id)
            VALUES (@RefreshTokenId, @UserId, 'valid', 'refresh_token', @AuthorizationId),
                   (@AccessTokenId, @UserId, 'valid', 'access_token', NULL);

            INSERT INTO identity.native_sessions
                (id, user_id, authorization_id, client_id, security_version, authenticated_at,
                 last_used_at, absolute_expires_at, created_at, updated_at)
            VALUES (@NativeSessionId, @UserId, @AuthorizationId, 'relisten-mobile-ios-dev', 1,
                    now(), now(), now() + interval '1 day', now(), now());

            INSERT INTO identity.sessions
                (id, user_id, purpose, validator_hash, security_version, authenticated_at,
                 created_at, updated_at, last_seen_at, sliding_expires_at, absolute_expires_at,
                 auth_sso_session_id, web_origin, capabilities)
            VALUES
                (@AuthSsoSessionId, @UserId, 'auth_sso', @Hash, 1, now(), now(), now(), now(),
                 now() + interval '1 day', now() + interval '1 day', NULL, NULL, 0),
                (@WebSessionId, @UserId, 'web', @Hash, 1, now(), now(), now(), now(),
                 now() + interval '1 day', now() + interval '1 day', @AuthSsoSessionId,
                 'https://relisten.net', 7);

            INSERT INTO identity.username_holds (id, user_id, username, release_at, created_at)
            VALUES (@UsernameHoldId, @UserId, @OldUsername, now() + interval '1 day', now());
            INSERT INTO identity.username_command_receipts
                (id, user_id, expected_username_version, payload_hash, stored_result, created_at)
            VALUES (@UsernameCommandId, @UserId, 1, @Hash, '{}'::jsonb, now());

            INSERT INTO user_data.library_states (id, user_id, revision, created_at, updated_at)
            VALUES (@LibraryStateId, @UserId, 1, now(), now());
            INSERT INTO user_data.favorites (id, user_id, catalog_type, catalog_id, created_at, updated_at)
            VALUES (@FavoriteId, @UserId, 'track', @TrackId, now(), now());
            INSERT INTO user_data.favorite_mutation_receipts
                (id, user_id, catalog_type, catalog_id, desired_state, submitted_favorite_id,
                 payload_hash, changed, canonical_favorite_id, library_revision, created_at)
            VALUES (@MutationId, @UserId, 'track', @TrackId, 'favorite', @FavoriteId, @Hash, true,
                    @FavoriteId, 1, now());
            INSERT INTO user_data.library_changes
                (id, user_id, revision, change_type, favorite_id, catalog_type, catalog_id, changed_at)
            VALUES (@ChangeId, @UserId, 1, 'favorite_added', @FavoriteId, 'track', @TrackId, now());

            INSERT INTO user_data.playlists
                (id, owner_id, name, archived, revision, entry_count, created_at, updated_at)
            VALUES (@PlaylistId, @UserId, 'Test playlist', false, 2, 1, now(), now());
            INSERT INTO user_data.playlist_segments (id, playlist_id, rank, deleted)
            VALUES (@SegmentId, @PlaylistId, 'a', false);
            INSERT INTO user_data.playlist_entries (id, playlist_id, segment_id, track_id, rank, deleted)
            VALUES (@EntryId, @PlaylistId, @SegmentId, @TrackId, 'a', false);
            INSERT INTO user_data.playlist_artists (playlist_id, artist_uuid, track_count)
            VALUES (@PlaylistId, gen_random_uuid(), 1);
            INSERT INTO user_data.playlist_revisions
                (playlist_id, revision, kind, change_count, changes_json)
            VALUES (@PlaylistId, 2, 'content', 1, '[]'::jsonb);
            INSERT INTO user_data.playlist_receipts
                (id, actor_id, playlist_id, kind, payload_hash, status, revision, archived, changed, created_at)
            VALUES (@PlaylistCommandId, @UserId, @PlaylistId, 'create', @Hash, 'active', 2, false, true, now());
            INSERT INTO user_data.playlist_follows (user_id, playlist_id, created_at)
            VALUES (@UserId, @PlaylistId, now());
            INSERT INTO user_data.playlist_reports (id, user_id, playlist_id, reason, created_at)
            VALUES (@ReportId, @UserId, @PlaylistId, 'Fixture report', now());

            INSERT INTO user_data.sonos_grants (user_id, protected_token)
            VALUES (@UserId, 'fixture');
            INSERT INTO user_data.sonos_oauth_states (hash, user_id, session_id, expires_at)
            VALUES (@NativeSessionId, @UserId, @NativeSessionId, now() + interval '10 minutes');
            INSERT INTO user_data.sonos_queues
                (id, user_id, session_id, group_id, household_id, request_hash, credential_hash,
                 items_json, expires_at, status)
            VALUES (@SonosQueueId, @UserId, @NativeSessionId, 'office', 'home', 'request', 'credential',
                    '[]', now() + interval '2 hours', 'loaded');

            INSERT INTO user_data.sonos_commands
                (id, queue_id, request_hash, action, status, expected_item_id)
            VALUES (@SonosCommandId, @SonosQueueId, 'request', 'pause', 'accepted', 'item');

            INSERT INTO user_data.plays
                (user_id, play_id, track_id, qualified_at, started_at, received_at, app_type, offline, origin)
            VALUES (@UserId, @PlayId, @TrackId, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z',
                    now(), 1, false, 1);
            """,
            new
            {
                UserId = user.ToString(),
                IdentityId = Guid.CreateVersion7(),
                RefreshTokenId = Guid.CreateVersion7(),
                AccessTokenId = Guid.CreateVersion7(),
                UsernameHoldId = Guid.CreateVersion7(),
                LibraryStateId = Guid.CreateVersion7(),
                AuthorizationId = Guid.CreateVersion7(),
                NativeSessionId = SessionId.New().ToString(),
                AuthSsoSessionId = SessionId.New().ToString(),
                WebSessionId = SessionId.New().ToString(),
                OldUsername = $"old_{Guid.NewGuid():N}"[..30],
                Hash = new byte[32],
                UsernameCommandId = CommandId.New().ToString(),
                FavoriteId = FavoriteId.New().ToString(),
                TrackId = UuidTypeId.Format("track", Guid.NewGuid()),
                MutationId = MutationId.New().ToString(),
                ChangeId = LibraryChangeId.New().ToString(),
                PlaylistId = playlistId,
                SegmentId = PlaylistSegmentId.New().ToString(),
                EntryId = PlaylistEntryId.New().ToString(),
                PlaylistCommandId = CommandId.New().ToString(),
                ReportId = PlaylistReportId.New().ToString(),
                PlayId = PlayId.New().Value,
                SonosQueueId = Guid.CreateVersion7(),
                SonosCommandId = Guid.CreateVersion7(),
            },
            transaction
        );

        // Compare the tables seeded by this transaction with the foreign key walk, so
        // removing an existing foreign key cannot silently remove that table from coverage.
        // Timescale writes to chunks and maintains extension-owned catalogs for them.
        var seeded = await connection.QueryAsync<string>(
            """
            SELECT DISTINCT format('%I.%I', ns.nspname, relation.relname)
            FROM pg_stat_xact_user_tables stats
            LEFT JOIN pg_inherits child ON child.inhrelid = stats.relid
            JOIN pg_class relation ON relation.oid = coalesce(child.inhparent, stats.relid)
            JOIN pg_namespace ns ON ns.oid = relation.relnamespace
            WHERE stats.n_tup_ins > 0
                AND NOT EXISTS (
                    SELECT FROM pg_depend dep
                    WHERE dep.classid = 'pg_class'::regclass AND dep.objid = stats.relid
                        AND dep.deptype = 'e'
                )
            """,
            transaction: transaction
        );
        await transaction.CommitAsync();
        return seeded;
    }
}
