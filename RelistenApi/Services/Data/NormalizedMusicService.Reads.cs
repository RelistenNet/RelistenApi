using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    public Task<MusicRecord<RepertoireSongChange>?> GetRepertoireAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) => GetIdentity<RepertoireSongChange>("repertoire_songs", uuid, cancellationToken);

    public Task<MusicRecord<CompositionChange>?> GetCompositionAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) => GetIdentity<CompositionChange>("compositions", uuid, cancellationToken);

    public Task<IReadOnlyList<MusicRecord<RepertoireSongChange>>> SearchRepertoireAsync(
        Guid artistUuid,
        string? query = null,
        string? basis = null,
        string? afterTitle = null,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        ValidatePage(query, limit, afterTitle, afterUuid);
        if (basis != null && basis is not ("provider_assertion" or "reviewed" or "title_derived"))
        {
            throw new ArgumentException("Unknown repertoire basis.", nameof(basis));
        }
        return ReadMany<RepertoireSongChange>(
            "repertoire_songs",
            """
            x.artist_uuid=@artistUuid AND x.redirect_uuid IS NULL AND (@basis::text IS NULL OR r.basis=@basis)
            AND (@query::text IS NULL OR EXISTS(
                WITH RECURSIVE names AS (
                    SELECT uuid,display_title,aliases FROM normalization.repertoire_songs WHERE uuid=x.uuid
                    UNION ALL SELECT ancestor.uuid,ancestor.display_title,ancestor.aliases FROM normalization.repertoire_songs ancestor
                        JOIN names n ON ancestor.redirect_uuid=n.uuid WHERE NOT ancestor.retracted)
                SELECT 1 FROM names n WHERE strpos(lower(n.display_title),lower(@query))>0
                    OR EXISTS(SELECT 1 FROM unnest(n.aliases) alias WHERE strpos(lower(alias),lower(@query))>0)))
            AND (@afterTitle::text IS NULL OR (lower(x.display_title),x.uuid)>(lower(@afterTitle),@afterUuid))
            """,
            "lower(x.display_title),x.uuid",
            new
            {
                artistUuid,
                query = query?.Trim(),
                basis,
                afterTitle,
                afterUuid,
                limit,
            },
            cancellationToken
        );
    }

    public Task<IReadOnlyList<MusicRecord<CompositionChange>>> SearchCompositionsAsync(
        string? query = null,
        string? afterTitle = null,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        ValidatePage(query, limit, afterTitle, afterUuid);
        return ReadMany<CompositionChange>(
            "compositions",
            """
            x.redirect_uuid IS NULL AND (@query::text IS NULL OR EXISTS(
                WITH RECURSIVE names AS (
                    SELECT uuid,display_title,aliases FROM normalization.compositions WHERE uuid=x.uuid
                    UNION ALL SELECT ancestor.uuid,ancestor.display_title,ancestor.aliases FROM normalization.compositions ancestor
                        JOIN names n ON ancestor.redirect_uuid=n.uuid WHERE NOT ancestor.retracted)
                SELECT 1 FROM names n WHERE strpos(lower(n.display_title),lower(@query))>0
                    OR EXISTS(SELECT 1 FROM unnest(n.aliases) alias WHERE strpos(lower(alias),lower(@query))>0)))
            AND (@afterTitle::text IS NULL OR (lower(x.display_title),x.uuid)>(lower(@afterTitle),@afterUuid))
            """,
            "lower(x.display_title),x.uuid",
            new
            {
                query = query?.Trim(),
                afterTitle,
                afterUuid,
                limit,
            },
            cancellationToken
        );
    }

    public Task<IReadOnlyList<MusicRecord<ImportedSongAssignmentChange>>> GetImportedSongsAsync(
        Guid repertoireSongUuid,
        int limit = 200,
        CancellationToken cancellationToken = default
    ) =>
        ReadMany<ImportedSongAssignmentChange>(
            "imported_song_assignments",
            "x.repertoire_song_uuid=@repertoireSongUuid",
            "x.uuid",
            new { repertoireSongUuid, limit = ValidateLimit(limit) },
            cancellationToken
        );

    public Task<IReadOnlyList<MusicRecord<RepertoireCompositionChange>>> GetCompositionLinksAsync(
        Guid? repertoireSongUuid = null,
        Guid? compositionUuid = null,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    ) =>
        ReadMany<RepertoireCompositionChange>(
            "repertoire_compositions",
            "(@repertoireSongUuid::uuid IS NULL OR x.repertoire_song_uuid=@repertoireSongUuid) AND (@compositionUuid::uuid IS NULL OR x.composition_uuid=@compositionUuid) AND (@afterUuid::uuid IS NULL OR x.uuid>@afterUuid)",
            "x.uuid",
            new
            {
                repertoireSongUuid,
                compositionUuid,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );

    public Task<IReadOnlyList<MusicRecord<WorkCreditChange>>> GetWorkCreditsAsync(
        Guid compositionUuid,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    ) =>
        ReadMany<WorkCreditChange>(
            "work_credits",
            "x.composition_uuid=@compositionUuid AND (@afterUuid::uuid IS NULL OR x.uuid>@afterUuid)",
            "x.uuid",
            new
            {
                compositionUuid,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );

    /// <summary>Reads each accepted source author-list assertion without combining incompatible lists.</summary>
    public Task<
        IReadOnlyList<MusicRecord<CompositionAuthorshipChange>>
    > GetCompositionAuthorshipAsync(
        Guid compositionUuid,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    ) =>
        ReadMany<CompositionAuthorshipChange>(
            "composition_authorship_assertions",
            "x.composition_uuid=@compositionUuid AND (@afterUuid::uuid IS NULL OR x.uuid>@afterUuid)",
            "x.uuid",
            new
            {
                compositionUuid,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );

    public Task<IReadOnlyList<MusicRecord<SetlistEditionChange>>> GetArtistEditionsAsync(
        Guid artistUuid,
        string? afterLiteralDate = null,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        ValidateCursorPair(afterLiteralDate, afterUuid);
        return ReadMany<SetlistEditionChange>(
            "setlist_editions",
            """
            x.artist_uuid=@artistUuid AND (@afterLiteralDate::text IS NULL OR x.literal_date<@afterLiteralDate OR (x.literal_date=@afterLiteralDate AND x.uuid>@afterUuid))
            """,
            "x.literal_date DESC,x.uuid",
            new
            {
                artistUuid,
                afterLiteralDate,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );
    }

    public Task<IReadOnlyList<MusicRecord<SetlistEditionChange>>> GetShowEditionsAsync(
        Guid showUuid,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    ) =>
        ReadMany<SetlistEditionChange>(
            "setlist_editions",
            "x.show_uuid=@showUuid AND (@afterUuid::uuid IS NULL OR x.uuid>@afterUuid)",
            "x.uuid",
            new
            {
                showUuid,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );

    public Task<MusicRecord<SetlistEditionChange>?> GetEditionAsync(
        Guid uuid,
        CancellationToken cancellationToken = default
    ) => GetRecord<SetlistEditionChange>("setlist_editions", uuid, cancellationToken);

    public Task<IReadOnlyList<MusicRecord<SongOccurrenceChange>>> GetOccurrencesAsync(
        Guid editionUuid,
        int? afterPosition = null,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    ) =>
        ReadMany<SongOccurrenceChange>(
            "song_occurrences",
            """
            x.edition_uuid=@editionUuid AND (@afterUuid::uuid IS NULL OR
                (COALESCE(x.position,2147483647),x.uuid)>(COALESCE(@afterPosition::integer,2147483647),@afterUuid))
            """,
            "x.position NULLS LAST,x.uuid",
            new
            {
                editionUuid,
                afterPosition,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );

    public Task<IReadOnlyList<MusicRecord<TrackContentChange>>> GetTrackContentsAsync(
        Guid trackUuid,
        int? afterPosition = null,
        Guid? afterUuid = null,
        int limit = 50,
        CancellationToken cancellationToken = default
    )
    {
        if (afterPosition.HasValue != afterUuid.HasValue)
        {
            throw new ArgumentException("A file content cursor needs position and UUID together.");
        }
        return ReadMany<TrackContentChange>(
            "track_contents",
            "x.source_track_uuid=@trackUuid AND (@afterUuid::uuid IS NULL OR (x.position,x.uuid)>(@afterPosition,@afterUuid))",
            "x.position,x.uuid",
            new
            {
                trackUuid,
                afterPosition,
                afterUuid,
                limit = ValidateLimit(limit),
            },
            cancellationToken
        );
    }

    private Task<MusicRecord<T>?> GetIdentity<T>(string table, Guid requested, CancellationToken ct)
        where T : MusicChange =>
        db.WithWriteConnection(async c =>
        {
            var uuid = requested;
            var visited = new HashSet<Guid>();
            while (visited.Add(uuid))
            {
                var row = await c.QuerySingleOrDefaultAsync<MusicReadRow>(
                    new CommandDefinition(
                        $"""
                        SELECT x.uuid AS Uuid,r.payload::text AS Payload,r.revision AS Revision,r.created_at AS UpdatedAt,
                            r.decision_uuid AS DecisionUuid,x.redirect_uuid AS RedirectUuid
                        FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                        WHERE x.uuid=@uuid AND NOT x.retracted
                        """,
                        new { uuid },
                        cancellationToken: ct
                    )
                );
                if (row == null)
                {
                    return null;
                }
                if (row.RedirectUuid is { } redirect)
                {
                    uuid = redirect;
                    continue;
                }
                var result = ToRecord<T>(row);
                return uuid == requested
                    ? result
                    : result with
                    {
                        RequestedUuid = requested,
                        RedirectUuid = uuid,
                    };
            }
            throw new InvalidOperationException("Music identity redirects contain a cycle.");
        });

    private async Task<MusicRecord<T>?> GetRecord<T>(string table, Guid uuid, CancellationToken ct)
        where T : MusicChange =>
        (
            await ReadMany<T>(table, "x.uuid=@uuid", "x.uuid", new { uuid, limit = 1 }, ct)
        ).SingleOrDefault();

    private Task<IReadOnlyList<MusicRecord<T>>> ReadMany<T>(
        string table,
        string predicate,
        string ordering,
        object parameters,
        CancellationToken ct
    )
        where T : MusicChange => db.WithWriteConnection<IReadOnlyList<MusicRecord<T>>>(async c => (
                await c.QueryAsync<MusicReadRow>(
                    new CommandDefinition(
                        $"""
                        SELECT x.uuid AS Uuid,r.payload::text AS Payload,r.revision AS Revision,r.created_at AS UpdatedAt,
                            r.decision_uuid AS DecisionUuid,NULL::uuid AS RedirectUuid
                        FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid
                        WHERE NOT x.retracted AND ({predicate}) ORDER BY {ordering} LIMIT @limit
                        """,
                        parameters,
                        cancellationToken: ct
                    )
                )
            ).Select(ToRecord<T>).ToArray());

    private static MusicRecord<T> ToRecord<T>(MusicReadRow row)
        where T : MusicChange =>
        new(
            JsonSerializer.Deserialize<T>(row.Payload)!,
            row.Revision,
            row.UpdatedAt,
            row.DecisionUuid
        );

    private sealed record MusicReadRow(
        Guid Uuid,
        string Payload,
        long Revision,
        DateTime UpdatedAt,
        Guid DecisionUuid,
        Guid? RedirectUuid
    );

    private static int ValidateLimit(int limit) =>
        limit is >= 1 and <= 200 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    private static void ValidateCursorPair(string? text, Guid? uuid)
    {
        if ((text != null) != uuid.HasValue)
        {
            throw new ArgumentException(
                "A music cursor needs its ordering value and UUID together."
            );
        }
    }

    private static void ValidatePage(string? query, int limit, string? title, Guid? uuid)
    {
        ValidateLimit(limit);
        ValidateCursorPair(title, uuid);
        if (query != null && query.Trim().EnumerateRunes().Count() is < 2 or > 200)
        {
            throw new ArgumentException(
                "A music search query needs 2–200 Unicode code points.",
                nameof(query)
            );
        }
    }
}
