using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Relisten.Api.Models.Api.V4;
using Relisten.Identifiers;

namespace Relisten.Search;

using Document = Dictionary<string, object>;

internal sealed partial class CatalogSearchProjection
{
    private async IAsyncEnumerable<Document> PassageTrackDocuments(
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        // Resolve the same marked paths as query evidence. Only files that
        // could appear in a passage need a separate indexed track resource.
        var artists = _artists.Values.ToDictionary(artist => artist.Row.Uuid);
        var entries = _songs
            .Values.Where(song =>
                string.IsNullOrEmpty(song.MergedInto) && _approvedArtists.Contains(song.ArtistUuid)
            )
            .Select(song =>
                (
                    new SearchDocument
                    {
                        Id = song.Id,
                        Kind = "song",
                        Name = SearchText.Fold(song.Name),
                        Label = song.Name,
                        ArtistSlug = artists[song.ArtistUuid].Row.Slug,
                    },
                    Array.Empty<string>()
                )
            );
        var passages = await SearchPassages.Load(
            _rows.Connection,
            _rows.Transaction,
            new SearchVocabulary(entries, []),
            _venues,
            cancellationToken,
            includePopularity: false
        );
        foreach (
            var ids in passages
                .DisplayTrackIds()
                .Select(id => UuidTypeId.Parse(id, "track"))
                .Chunk(TrackDisplayBatchSize)
        )
        {
            var tracks = await _resources.Tracks(ids, _approvedArtists, cancellationToken);
            foreach (var track in tracks.Values)
            {
                yield return new Document
                {
                    ["id"] = UuidTypeId.Format("track", track.uuid),
                    ["kind"] = "passage_track",
                    ["artist_slug"] = artists[track.artist_uuid].Row.Slug,
                    ["popularity"] = 0,
                    ["ranking_signal"] = 0,
                    ["display"] = CatalogSearchResourceProjection.Serialize(
                        V4CatalogProjection.Track(track)
                    ),
                };
            }
        }
    }
}
