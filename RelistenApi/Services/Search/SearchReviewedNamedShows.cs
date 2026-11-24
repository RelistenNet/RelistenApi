using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text.Json;

namespace Relisten.Search;

internal static class SearchReviewedNamedShows
{
    private static readonly Lazy<Batch> File = new(Load);

    public static IReadOnlyList<Route> Routes => File.Value.Routes;
    public static IReadOnlyList<Run> Runs => File.Value.Runs;
    public static IReadOnlyList<PassageRule> Passages => File.Value.Passages;

    private static Batch Load()
    {
        using var resource =
            typeof(SearchReviewedNamedShows).Assembly.GetManifestResourceStream(
                "Relisten.Search.ReviewedNamedShows.json"
            ) ?? throw new InvalidDataException("The reviewed named shows resource is missing.");
        return JsonSerializer.Deserialize<Batch>(
                resource,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
            ) ?? throw new InvalidDataException("The reviewed named shows resource is empty.");
    }

    private sealed record Batch(Route[] Routes, Run[] Runs, PassageRule[] Passages);

    internal sealed record Route(
        string Artist,
        string Terms,
        int Date,
        string? Song,
        bool RequiresArtist
    );

    internal sealed record Run(string Artist, string Name, int StartDate, int EndDate);

    // A folded passage phrase also covers punctuation variants such as "scarlet > fire".
    internal sealed record PassageRule(
        string Artist,
        ImmutableArray<string> Phrases,
        ImmutableArray<string> SongTitles,
        int? Date = null
    );
}
