using System.Collections.Generic;
using System.Linq;

namespace Relisten.Search;

internal static class SearchIndexSchema
{
    public const string Entities = "entities";
    public const string Performances = "performances";
    public const string Recordings = "recordings";
    public const string Reviews = "reviews";

    public static readonly IReadOnlyList<string> Collections =
    [
        Entities,
        Performances,
        Recordings,
        Reviews,
    ];

    private static readonly Field[] Common =
    [
        new("name"),
        new("aliases"),
        new("artist"),
        new("place"),
        new("run"),
        new("kind", Facet: true),
        new("artist_slug", Facet: true),
        new("artist_slugs", "string[]", Facet: true),
        new("country", Facet: true),
        new("show_id", Facet: true),
        new("venue_id", Facet: true),
        new("date", "int32"),
        new("day_key", "int32"),
        new("popularity", "float"),
        new("label", Index: false),
        new("artist_label", Index: false),
        new("venue_label", Index: false),
        new("city_label", Index: false),
        new("tour_label", Index: false),
    ];

    private static readonly IReadOnlyDictionary<string, Field[]> Specific = new Dictionary<
        string,
        Field[]
    >
    {
        [Entities] = [new("song_id", Facet: true)],
        [Performances] =
        [
            new("song_id", Facet: true),
            new("soundboard", "bool", Facet: true),
            new("soundcheck", "bool", Facet: true),
            new("longest", "float"),
            new("track_plays", "int64"),
            new("review_mentions", "int32"),
            new("files", "object[]", Index: false),
            new("preview", "object", Index: false),
        ],
        [Recordings] =
        [
            new("credits"),
            new("notes"),
            new("source_upstream", Index: false),
            new("soundboard", "bool", Facet: true),
            new("matrix", "bool", Facet: true),
            new("track_plays", "int64"),
        ],
        [Reviews] =
        [
            new("review_text"),
            new("source_id", Facet: true),
            new("source_upstream", Index: false),
        ],
    };

    public static object Create(string kind, string name) =>
        new
        {
            name,
            enable_nested_fields = true,
            fields = Common.Concat(Specific[kind]),
        };

    private sealed record Field(
        string Name,
        string Type = "string",
        bool Facet = false,
        bool Index = true,
        bool Optional = true
    );
}
