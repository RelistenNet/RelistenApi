using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Relisten.Normalization.Songs;

public sealed record CatalogueMatchResult(int TracksWritten, int EvidenceWritten);

/// The deterministic pass (no LLM): rebuilds every derived track row of an artist from its
/// songs and aliases as they stand, places tracks that now resolve, and maps setlist songs.
/// So a person's fix to a song or an alias reaches every track on the next match. It ends
/// every LLM catalogue run, and is cheap enough to run after imports and edits.
public sealed class CatalogueMatch(INormalizationDatabase db)
{
    public async Task<CatalogueMatchResult> RunAsync(
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateWriteConnection();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('norm-catalogue'), hashtext(@artist))",
            new { artist = artistUuid.ToString() },
            tx
        );

        var state = await CatalogueState.LoadAsync(connection, tx, artistUuid, allTracks: true);
        var store = await NormStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Algorithm,
            TrackMatcher.Author
        );
        var evidence = await store.WriteEvidenceAsync(
            TrackMatcher.Evidence(state),
            NormWriteMode.FillOnly
        );
        var match = TrackMatcher.Match(state);
        await store.ClearTracksAsync(match.Cleared);
        // Upsert: derived rows follow the songs and aliases; unchanged rows aren't written.
        await store.WriteTrackSongsAsync(match.Records);
        // A machine's open question about a title is answered once a name or alias places it,
        // or every occurrence is placed (by a ruling, or as not a song).
        var written = match.Records.Select(r => r.TrackUuid).ToHashSet();
        var open = match
            .Records.Where(r => !r.Resolved && r.Text != null)
            .Select(r => TitleNormalizer.Normalize(r.Text!))
            .Concat(
                state
                    .Tracks.Where(t => t.Existing.Count == 0 && !written.Contains(t.Uuid))
                    .SelectMany(t => t.Title.Parts.Select(p => p.Key))
            )
            .ToHashSet();
        var placed = match
            .Records.Where(r => r.Resolved && r.Text != null)
            .Select(r => TitleNormalizer.Normalize(r.Text!))
            .Where(k => !open.Contains(k));
        await store.CloseReviewItemsAsync(
            artistUuid,
            state.SongNames.Keys.Concat(state.Aliases.Keys).Concat(placed).Distinct().ToList(),
            "placed"
        );
        await store.FinishAsync();
        await tx.CommitAsync(cancellationToken);

        return new CatalogueMatchResult(store.TracksTouched, evidence);
    }
}
