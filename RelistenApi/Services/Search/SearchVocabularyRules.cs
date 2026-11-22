using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Relisten.Search;

// Listener shorthand from the frozen search evaluation. Catalog names and aliases come from Postgres.
internal static class SearchVocabularyRules
{
    public static readonly FrozenDictionary<string, string> LeadingArtistAliases = new Dictionary<
        string,
        string
    >
    {
        ["grateful dead"] = "grateful-dead",
        ["gd"] = "grateful-dead",
        ["dead"] = "grateful-dead",
        ["billy strings"] = "billy-strings",
        ["billy"] = "billy-strings",
        ["bmfs"] = "billy-strings",
        ["phish"] = "phish",
        ["ween"] = "ween",
        ["goose"] = "goose",
        ["string cheese incident"] = "sci",
        ["string cheese"] = "sci",
        ["sci"] = "sci",
        ["dead and company"] = "dead-and-co",
    }.ToFrozenDictionary();

    public static readonly FrozenDictionary<string, string> ArtistAliases = LeadingArtistAliases
        .Concat(
            new Dictionary<string, string> { ["dead and co"] = "dead-and-co", ["jrad"] = "jrad" }
        )
        .ToFrozenDictionary();

    public static readonly ImmutableArray<(
        string Artist,
        string Alias,
        string Title
    )> SongAliases =
    [
        ("grateful-dead", "pitb", "playing in the band"),
        ("grateful-dead", "dew", "morning dew"),
        ("phish", "halleys", "halleys comet"),
    ];

    public static readonly ImmutableArray<(
        string Artist,
        string Place,
        string Alias
    )> PlaceAliases = [("grateful-dead", "barton hall", "cornell")];

    public static readonly ImmutableArray<(
        string Artist,
        string Terms,
        int Date,
        string? Song,
        bool RequiresArtist
    )> Routes =
    [
        ("grateful-dead", "cornell", 19770508, null, true),
        ("grateful-dead", "sunshine daydream", 19720827, null, false),
        ("phish", "went gin", 19970817, "bathtub gin", false),
    ];

    public static readonly ImmutableArray<(
        string Artist,
        string Name,
        int StartDate,
        int EndDate
    )> Runs = [("phish", "bakers dozen", 20170721, 20170806)];

    // A folded passage phrase also covers its punctuation variants, such as "scarlet > fire".
    public static readonly ImmutableArray<PassageRule> Passages =
    [
        new("grateful-dead", ["scarlet fire"], ["scarlet begonias", "fire on the mountain"]),
        new(
            "grateful-dead",
            ["help slip frank", "help slip franklin"],
            ["help on the way", "slipknot", "franklins tower"]
        ),
        new("grateful-dead", ["china rider"], ["china cat sunflower", "i know you rider"]),
        new("phish", ["mikes groove"], ["mikes song", "i am hydrogen", "weekapaug groove"]),
        new("phish", ["tweezabella"], ["tweezer", "izabella"], 19971206),
    ];

    internal sealed record PassageRule(
        string Artist,
        ImmutableArray<string> Phrases,
        ImmutableArray<string> SongTitles,
        int? Date = null
    );
}
