using System;
using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal sealed record SearchTabCounts(
    Dictionary<string, int> Exact,
    Dictionary<string, int> LowerBounds
)
{
    internal static SearchTabCounts Empty() => new(new(), new());

    internal void Add(SearchScope scope, int count, bool exact)
    {
        (exact ? Exact : LowerBounds)[scope.ToString().ToLowerInvariant()] = count;
    }

    /// <summary>
    /// Matching filters can reuse an ungrouped document total or a bounded
    /// count of one row per group. A kind facet from All can supply a lower
    /// bound for match-all or literal text. Prefixes and typos can expand
    /// differently after narrowing to one kind.
    /// </summary>
    internal static (int Count, bool Exact) Read(
        SearchRequest scoped,
        IEnumerable<(SearchRequest Request, SearchReply Reply)> observed
    )
    {
        var best = (Count: 0, Exact: false);
        foreach (var (request, reply) in observed)
        {
            if (reply.Rejected || Lookup(request) != Lookup(scoped))
            {
                continue;
            }

            if (request.FilterBy == scoped.FilterBy)
            {
                if (
                    scoped.GroupBy == null
                    && (request.GroupBy == null || reply.FoundDocuments.HasValue)
                )
                {
                    return (
                        request.GroupBy == null ? reply.Found : reply.FoundDocuments!.Value,
                        true
                    );
                }
                if (
                    request.GroupBy == scoped.GroupBy
                    && scoped.GroupLimit == 1
                    && ExactTotal(request, reply)
                )
                {
                    return (reply.Found, true);
                }

                if (scoped.GroupBy == request.GroupBy)
                {
                    best.Count = Math.Max(best.Count, MinimumTotal(request, reply));
                }
                continue;
            }

            if (
                scoped.Collection == SearchIndexSchema.Entities
                && reply.KindCounts is { } kinds
                && (
                    scoped.Q == "*"
                    || (scoped is { Prefix: false, NumTypos: 0, DropTokensThreshold: 0 })
                )
                && BaseEntityFilters(scoped.FilterBy) == BaseEntityFilters(request.FilterBy)
            )
            {
                var kindFilter = scoped
                    .FilterBy?.Split(" && ")
                    .FirstOrDefault(filter =>
                        filter.StartsWith("kind:=", StringComparison.Ordinal)
                    );
                if (kindFilter != null)
                {
                    var count = kindFilter["kind:=".Length..]
                        .Trim('[', ']')
                        .Split(',')
                        .Sum(kind => kinds.GetValueOrDefault(kind));
                    best.Count = Math.Max(best.Count, count);
                }
            }
        }
        return best;
    }

    // Typesense approximates group totals unless the request asks it to count
    // up to a bound. Returned groups alone still establish a safe minimum.
    internal static bool ExactTotal(SearchRequest request, SearchReply reply) =>
        !reply.Rejected
        && (
            request.GroupBy == null
            || (request.GroupMaxCandidates is int limit && reply.Found < limit)
        );

    internal static int MinimumTotal(SearchRequest request, SearchReply reply)
    {
        if (ExactTotal(request, reply))
        {
            return reply.Found;
        }

        return request.GroupBy switch
        {
            "show_id" => reply.Documents.Select(row => row.ShowId).Distinct().Count(),
            "preview_track_id" => reply
                .Documents.Select(row => row.Preview?.TrackId)
                .Distinct()
                .Count(),
            _ => 0,
        };
    }

    private static string BaseEntityFilters(string? filter) =>
        string.Join(
            " && ",
            (filter ?? "")
                .Split(" && ")
                .Where(part => !part.StartsWith("kind:", StringComparison.Ordinal))
        );

    // Compare the retrieval semantics. Page size, ranking order and projected
    // fields only affect the returned window, not which match is counted.
    private static SearchRequest Lookup(SearchRequest request) =>
        request with
        {
            FilterBy = null,
            Optional = false,
            GroupBy = null,
            GroupLimit = null,
            GroupMaxCandidates = null,
            Page = null,
            Offset = null,
            Limit = null,
            PerPage = 0,
            FacetBy = null,
            SortBy = "",
            IncludeFields = "",
            HighlightFields = "",
        };
}
