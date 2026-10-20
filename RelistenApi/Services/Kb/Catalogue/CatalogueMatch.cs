using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Data;

namespace Relisten.Kb.Catalogue;

public sealed record CatalogueMatchResult(int TracksWritten, int EvidenceWritten);

/// The deterministic pass (no LLM): rebuilds every derived track row of an artist from its
/// songs and aliases as they stand, places tracks that now resolve, and maps setlist songs.
/// So a person's fix to a song or an alias reaches every track on the next match. It ends
/// every LLM catalogue run, and is cheap enough to run after imports and edits.
public sealed class CatalogueMatch(DbService db)
{
    public async Task<CatalogueMatchResult> RunAsync(
        Guid artistUuid,
        CancellationToken cancellationToken
    )
    {
        await using var connection = db.CreateConnection(longTimeout: true, readOnly: false);
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "SELECT pg_advisory_xact_lock(hashtext('kb-catalogue'), hashtext(@artist))",
            new { artist = artistUuid.ToString() },
            tx
        );

        var state = await CatalogueState.LoadAsync(connection, tx, artistUuid, allTracks: true);
        var store = await KbStore.BeginAsync(
            connection,
            tx,
            AuthorTypes.Algorithm,
            TrackMatcher.Author
        );
        var evidence = await store.WriteEvidenceAsync(
            TrackMatcher.Evidence(state),
            KbWriteMode.FillOnly
        );
        var match = TrackMatcher.Match(state);
        await store.ClearTracksAsync(match.Cleared);
        // Upsert: derived rows follow the songs and aliases; unchanged rows aren't written.
        await store.WriteTrackSongsAsync(match.Records);
        await store.FinishAsync();
        await tx.CommitAsync(cancellationToken);

        return new CatalogueMatchResult(store.TracksTouched, evidence);
    }
}
