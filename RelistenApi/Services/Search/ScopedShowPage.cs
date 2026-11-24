using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

/// <summary>A page across the sequence, show, and playable-file streams.</summary>
internal static class ScopedShowPage
{
    internal const int Size = 24;
    internal const int LastPage = 100;
    internal const int GroupCountLimit = Size * LastPage;

    internal readonly record struct Slice(int Offset, int Count);

    internal sealed record Window(Slice Passages, Slice[] Catalog, int Total);

    internal static SearchRequest Request(SearchRequest request, int offset, int count) =>
        request with
        {
            Page = null,
            Offset = offset,
            Limit = count,
            PerPage = count,
            // One file can match several song documents. Group before pagination
            // so the same playable file cannot occupy slots on different pages.
            GroupBy =
                request.Collection == SearchIndexSchema.Performances && !IsShowBrowse(request)
                    ? "preview_track_id"
                    : request.GroupBy,
            GroupLimit =
                request.Collection == SearchIndexSchema.Performances ? 1 : request.GroupLimit,
            GroupMaxCandidates =
                request.Collection == SearchIndexSchema.Performances ? GroupCountLimit : null,
        };

    private static bool IsShowBrowse(SearchRequest request) =>
        request is { Q: "*", GroupBy: "show_id", GroupLimit: 1 };

    internal static Window Select(int page, int passageCount, IReadOnlyList<int> catalogCounts)
    {
        var start = (page - 1) * Size;
        var end = start + Size;
        var total = passageCount + catalogCounts.Sum();
        var passages = new Slice(
            Math.Min(start, passageCount),
            Math.Max(0, Math.Min(passageCount, end) - start)
        );

        // Marked sequences lead. Alternate the two catalog streams, then
        // finish the surviving stream if one runs out. Each stream contributes
        // one contiguous slice, regardless of how many rows the other has.
        var offsets = new int[catalogCounts.Count];
        var lengths = new int[catalogCounts.Count];
        var positions = new int[catalogCounts.Count];
        for (var index = passageCount; index < Math.Min(total, end); )
        {
            for (var stream = 0; stream < positions.Length && index < end; stream++)
            {
                if (positions[stream] == catalogCounts[stream])
                {
                    continue;
                }

                positions[stream]++;
                if (index < start)
                {
                    offsets[stream]++;
                }
                else
                {
                    lengths[stream]++;
                }
                index++;
            }
        }

        var catalog = offsets
            .Zip(lengths)
            .Select(value => new Slice(value.First, value.Second))
            .ToArray();
        return new(passages, catalog, total);
    }
}
