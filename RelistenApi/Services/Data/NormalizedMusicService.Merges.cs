using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;
using Relisten.Api.Models.Normalization;

namespace Relisten.Data;

public sealed partial class NormalizedMusicService
{
    private static async Task ApplyMerge(
        NpgsqlConnection c,
        NpgsqlTransaction t,
        MusicMerge merge,
        Guid decision,
        CancellationToken ct
    )
    {
        if (
            merge.RetiredUuid == merge.SurvivorUuid
            || merge.Domain is not ("songs" or "compositions")
        )
        {
            throw new ArgumentException(
                "A music merge identifies distinct identities in one supported domain."
            );
        }
        var table = merge.Domain == "songs" ? "repertoire_songs" : "compositions";
        await RequireUnredirected(c, t, table, merge.RetiredUuid, ct);
        await RequireUnredirected(c, t, table, merge.SurvivorUuid, ct);
        if (merge.ExpectedSurvivorRevision <= 0)
        {
            throw new ArgumentException(
                "A merge requires the survivor revision observed during review."
            );
        }
        var survivorRevision = await c.QuerySingleAsync<long>(
            new CommandDefinition(
                $"SELECT r.revision FROM normalization.{table} x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@SurvivorUuid",
                merge,
                t,
                cancellationToken: ct
            )
        );
        if (survivorRevision != merge.ExpectedSurvivorRevision)
        {
            throw new NormalizationConflictException(
                "The merge survivor changed after review was prepared."
            );
        }
        if (merge.Domain == "songs")
        {
            await Require(
                c,
                t,
                """
                SELECT retired.artist_uuid=survivor.artist_uuid FROM normalization.repertoire_songs retired
                CROSS JOIN normalization.repertoire_songs survivor WHERE retired.uuid=@RetiredUuid AND survivor.uuid=@SurvivorUuid
                """,
                merge,
                "A repertoire merge must stay within one artist.",
                ct
            );
            // Every reassignment must have its own revision in the same reviewed publication.
            // Requiring explicit correspondence also gives split/reversal enough history to recover it.
            await Require(
                c,
                t,
                """
                SELECT NOT EXISTS(SELECT 1 FROM normalization.imported_song_assignments WHERE repertoire_song_uuid=@RetiredUuid AND NOT retracted)
                AND NOT EXISTS(SELECT 1 FROM normalization.repertoire_compositions WHERE repertoire_song_uuid=@RetiredUuid AND NOT retracted)
                AND NOT EXISTS(SELECT 1 FROM normalization.song_occurrences WHERE repertoire_song_uuid=@RetiredUuid AND NOT retracted)
                AND NOT EXISTS(SELECT 1 FROM normalization.track_contents WHERE repertoire_song_uuid=@RetiredUuid AND NOT retracted)
                AND NOT EXISTS(SELECT 1 FROM normalization.music_external_identities WHERE repertoire_song_uuid=@RetiredUuid AND NOT retracted)
                """,
                merge,
                "A merge must explicitly reassign or retract each accepted relationship to the retired song.",
                ct
            );
            var payload = await c.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT r.payload::text FROM normalization.repertoire_songs x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@RetiredUuid",
                    merge,
                    t,
                    cancellationToken: ct
                )
            );
            var prior = JsonSerializer.Deserialize<RepertoireSongChange>(payload)!;
            await SaveRepertoire(
                c,
                t,
                prior with
                {
                    ExpectedRevision = merge.ExpectedRevision,
                    Basis = "reviewed",
                },
                decision,
                true,
                ct
            );
        }
        else
        {
            await Require(
                c,
                t,
                """
                SELECT NOT EXISTS(SELECT 1 FROM normalization.repertoire_compositions WHERE composition_uuid=@RetiredUuid AND NOT retracted)
                AND NOT EXISTS(SELECT 1 FROM normalization.work_credits WHERE composition_uuid=@RetiredUuid AND NOT retracted)
                AND NOT EXISTS(SELECT 1 FROM normalization.music_external_identities WHERE composition_uuid=@RetiredUuid AND NOT retracted)
                """,
                merge,
                "A merge must explicitly reassign or retract each accepted relationship to the retired composition.",
                ct
            );
            var payload = await c.QuerySingleAsync<string>(
                new CommandDefinition(
                    "SELECT r.payload::text FROM normalization.compositions x JOIN normalization.music_revisions r ON r.uuid=x.revision_uuid WHERE x.uuid=@RetiredUuid",
                    merge,
                    t,
                    cancellationToken: ct
                )
            );
            var prior = JsonSerializer.Deserialize<CompositionChange>(payload)!;
            await SaveComposition(
                c,
                t,
                prior with
                {
                    ExpectedRevision = merge.ExpectedRevision,
                    Basis = "reviewed",
                },
                decision,
                true,
                ct
            );
        }
        await c.ExecuteAsync(
            new CommandDefinition(
                $"UPDATE normalization.{table} SET redirect_uuid=@SurvivorUuid WHERE uuid=@RetiredUuid",
                merge,
                t,
                cancellationToken: ct
            )
        );
    }
}
