using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Relisten.Data;

namespace Relisten.Kb.Catalogue;

public sealed record CatalogueMatchResult(int TracksWritten, int EvidenceWritten);

/// The deterministic pass (no LLM): resolves an artist's unresolved tracks and setlist songs
/// from the alias table as it stands. It ends every LLM catalogue run, and is cheap enough to
/// run after imports to place new uploads of known songs.
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

        var state = await CatalogueState.LoadAsync(connection, tx, artistUuid);
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
        var tracks = TrackMatcher.Match(state);
        await store.WriteTrackSongsAsync(tracks, KbWriteMode.FillOnly);
        await store.FinishAsync();
        await tx.CommitAsync(cancellationToken);

        return new CatalogueMatchResult(
            tracks.Select(t => t.TrackUuid).Distinct().Count(),
            evidence
        );
    }
}
