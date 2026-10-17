using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private static async Task SaveEdition(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SetlistEditionChange edition,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        await RequireCatalogBinding(
            c,
            t,
            "setlist_editions",
            edition.Uuid,
            "artist_uuid",
            edition.ArtistUuid,
            "artists",
            ct
        );
        await Require(
            c,
            t,
            """
            SELECT NOT EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE uuid=@Uuid AND artist_uuid<>@ArtistUuid)
            """,
            edition,
            "An edition cannot move to another artist.",
            ct
        );
        if (edition.ShowUuid is { } show)
        {
            await Require(
                c,
                t,
                """
                SELECT EXISTS(SELECT 1 FROM shows sh JOIN artists a ON a.id=sh.artist_id WHERE sh.uuid=@ShowUuid AND a.uuid=@ArtistUuid)
                OR EXISTS(SELECT 1 FROM normalization.setlist_editions WHERE uuid=@Uuid AND show_uuid=@ShowUuid AND artist_uuid=@ArtistUuid)
                """,
                edition,
                "The edition and its assigned show must belong to the same artist.",
                ct
            );
        }
        if (
            string.IsNullOrWhiteSpace(edition.Provider)
            || string.IsNullOrWhiteSpace(edition.ResourceType)
            || string.IsNullOrWhiteSpace(edition.ProviderKey)
        )
        {
            throw new ArgumentException(
                "A setlist edition requires a qualified provider resource identity."
            );
        }
        await Persist(
            c,
            t,
            "setlist_editions",
            edition,
            "artist_uuid,show_uuid,literal_date,ordered,provider,resource_type,provider_key,selected",
            "@ArtistUuid,@ShowUuid,@LiteralDate,@Ordered,@Provider,@ResourceType,@ProviderKey,@Selected",
            decision,
            reviewed,
            ct
        );
    }

    private static async Task SaveOccurrence(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        SongOccurrenceChange occurrence,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        var edition =
            await c.QuerySingleOrDefaultAsync<EditionScope>(
                new CommandDefinition(
                    "SELECT artist_uuid AS ArtistUuid,ordered AS Ordered FROM normalization.setlist_editions WHERE uuid=@EditionUuid AND NOT retracted",
                    occurrence,
                    t,
                    cancellationToken: ct
                )
            ) ?? throw new ArgumentException("The occurrence edition is unavailable.");
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE uuid=@Uuid AND edition_uuid<>@EditionUuid)",
            occurrence,
            "An occurrence identity cannot move to another edition.",
            ct
        );
        if (occurrence.RepertoireSongUuid is { } song)
        {
            await RequireArtistSong(c, t, song, edition.ArtistUuid, ct);
        }
        if (occurrence.ParentOccurrenceUuid is { } parent)
        {
            await Require(
                c,
                t,
                """
                SELECT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE uuid=@parent AND edition_uuid=@EditionUuid
                    AND parent_occurrence_uuid IS NULL AND NOT retracted)
                """,
                new { parent, occurrence.EditionUuid },
                "A component passage needs a root occurrence in the same edition.",
                ct
            );
            if (occurrence.Position != null || occurrence.ComponentPosition is null or <= 0)
            {
                throw new ArgumentException(
                    "A component passage requires its own component position and no edition position."
                );
            }
        }
        else if (edition.Ordered != occurrence.Position.HasValue)
        {
            throw new ArgumentException(
                "Ordered editions require passage positions; unordered editions must leave positions unknown."
            );
        }
        if (occurrence.SupersedesOccurrenceUuid is { } superseded)
        {
            await Require(
                c,
                t,
                "SELECT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE uuid=@superseded AND edition_uuid=@EditionUuid)",
                new { superseded, occurrence.EditionUuid },
                "A superseded passage must belong to the same edition.",
                ct
            );
            if (!reviewed)
            {
                throw new ArgumentException(
                    "Replacing an unrelated passage requires reviewed correspondence."
                );
            }
        }
        await Persist(
            c,
            t,
            "song_occurrences",
            occurrence,
            "edition_uuid,position,set_label,kind,repertoire_song_uuid,original_text,transition_after,parent_occurrence_uuid,component_position,supersedes_occurrence_uuid,provider_item_key",
            "@EditionUuid,@Position,@SetLabel,@Kind,@RepertoireSongUuid,@OriginalText,@TransitionAfter,@ParentOccurrenceUuid,@ComponentPosition,@SupersedesOccurrenceUuid,@ProviderItemKey",
            decision,
            reviewed,
            ct
        );
    }

    private static async Task SaveContent(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        TrackContentChange content,
        Guid decision,
        bool reviewed,
        CancellationToken ct
    )
    {
        // Only new catalog bindings need live validation. A disappeared file retains its prior
        // accepted contents and evidence; it simply cannot supply a playable public result.
        await Require(
            c,
            t,
            "SELECT NOT EXISTS(SELECT 1 FROM normalization.track_contents WHERE uuid=@Uuid AND source_track_uuid<>@SourceTrackUuid)",
            content,
            "A content identity cannot move to another file.",
            ct
        );
        var track = await c.QuerySingleOrDefaultAsync<TrackScope>(
            new CommandDefinition(
                """
                SELECT a.uuid AS ArtistUuid,t.duration::double precision AS Duration,s.uuid AS SourceUuid
                FROM source_tracks t JOIN sources s ON s.id=t.source_id
                JOIN source_sets sets ON sets.id=t.source_set_id AND sets.source_id=s.id
                JOIN artists a ON a.id=s.artist_id AND a.id=t.artist_id WHERE t.uuid=@SourceTrackUuid
                """,
                content,
                t,
                cancellationToken: ct
            )
        );
        if (track == null)
        {
            await Require(
                c,
                t,
                "SELECT EXISTS(SELECT 1 FROM normalization.track_contents WHERE uuid=@Uuid AND source_track_uuid=@SourceTrackUuid)",
                content,
                "The new file binding needs matching artist, source and set relationships.",
                ct
            );
            // Scope is recoverable from the prior accepted repertoire or occurrence even when the
            // catalog disappears. Changes that introduce another musical identity still need it.
            var prior = await c.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT r.payload::text FROM normalization.track_contents x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@Uuid",
                    content,
                    t,
                    cancellationToken: ct
                )
            );
            var priorContent = System.Text.Json.JsonSerializer.Deserialize<TrackContentChange>(
                prior
            )!;
            if (
                priorContent.RepertoireSongUuid != content.RepertoireSongUuid
                || priorContent.OccurrenceUuid != content.OccurrenceUuid
            )
            {
                throw new ArgumentException(
                    "A missing file cannot acquire a new musical binding without live scope validation."
                );
            }
        }
        if (
            content.StartMs is < 0
            || content.EndMs is < 0
            || (
                content.StartMs.HasValue
                && content.EndMs.HasValue
                && content.StartMs > content.EndMs
            )
        )
        {
            throw new ArgumentException("Known file bounds must be nonnegative and ordered.");
        }
        if (
            track?.Duration is { } duration
            && (content.StartMs > duration * 1000d || content.EndMs > duration * 1000d)
        )
        {
            throw new ArgumentException("A content bound exceeds the known file duration.");
        }
        if (content.RepertoireSongUuid is { } song && track != null)
        {
            await RequireArtistSong(c, t, song, track.ArtistUuid, ct);
        }
        if (content.OccurrenceUuid is { } occurrence)
        {
            var scope =
                await c.QuerySingleOrDefaultAsync<OccurrenceScope>(
                    new CommandDefinition(
                        """
                        SELECT e.artist_uuid AS ArtistUuid,o.repertoire_song_uuid AS RepertoireSongUuid
                        FROM normalization.song_occurrences o JOIN normalization.setlist_editions e ON e.uuid=o.edition_uuid
                        WHERE o.uuid=@occurrence AND NOT o.retracted AND NOT e.retracted
                        """,
                        new { occurrence },
                        t,
                        cancellationToken: ct
                    )
                ) ?? throw new ArgumentException("The content occurrence is unavailable.");
            if (track != null && scope.ArtistUuid != track.ArtistUuid)
            {
                throw new ArgumentException(
                    "A file and its occurrence must belong to the same artist."
                );
            }
            if (
                content.RepertoireSongUuid.HasValue
                && scope.RepertoireSongUuid.HasValue
                && scope.RepertoireSongUuid != content.RepertoireSongUuid
            )
            {
                throw new ArgumentException(
                    "A file segment and its occurrence cannot identify different repertoire songs."
                );
            }
        }
        var durableScope =
            track
            ?? await c.QuerySingleAsync<TrackScope>(
                new CommandDefinition(
                    "SELECT artist_uuid AS ArtistUuid,NULL::double precision AS Duration,source_uuid AS SourceUuid FROM normalization.track_contents WHERE uuid=@Uuid",
                    content,
                    t,
                    cancellationToken: ct
                )
            );
        await Persist(
            c,
            t,
            "track_contents",
            content,
            "source_track_uuid,artist_uuid,source_uuid,position,kind,original_text,repertoire_song_uuid,occurrence_uuid,start_ms,end_ms,match_evidence",
            "@SourceTrackUuid,@artist,@source,@Position,@Kind,@OriginalText,@RepertoireSongUuid,@OccurrenceUuid,@StartMs,@EndMs,@MatchEvidence",
            decision,
            reviewed,
            ct,
            new { artist = durableScope.ArtistUuid, source = durableScope.SourceUuid }
        );
    }

    private static Task RequireArtistSong(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        Guid song,
        Guid artist,
        CancellationToken ct
    ) =>
        Require(
            c,
            t,
            "SELECT EXISTS(SELECT 1 FROM normalization.repertoire_songs WHERE uuid=@song AND artist_uuid=@artist AND NOT retracted AND redirect_uuid IS NULL)",
            new { song, artist },
            "The repertoire song must belong to this artist.",
            ct
        );

    private static async Task ValidateEditionOrdering(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicPublication publication,
        CancellationToken ct
    )
    {
        var editions = publication
            .Editions.Select(e => e.Uuid)
            .Concat(publication.Occurrences.Select(o => o.EditionUuid))
            .Distinct()
            .ToArray();
        if (editions.Length == 0)
        {
            return;
        }
        await Require(
            c,
            t,
            """
            SELECT NOT EXISTS(SELECT 1 FROM normalization.song_occurrences o JOIN normalization.setlist_editions e ON e.uuid=o.edition_uuid
                WHERE e.uuid=ANY(@editions) AND NOT o.retracted AND o.parent_occurrence_uuid IS NULL
                AND e.ordered<>(o.position IS NOT NULL))
            """,
            new { editions },
            "An edition ordering change must update every affected occurrence in the same decision.",
            ct
        );
    }

    private sealed record EditionScope(Guid ArtistUuid, bool Ordered);

    private sealed record TrackScope(Guid ArtistUuid, double? Duration, Guid SourceUuid);

    private sealed record OccurrenceScope(Guid ArtistUuid, Guid? RepertoireSongUuid);
}
