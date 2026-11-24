using System.Text.RegularExpressions;

namespace Relisten.Search;

public enum MatchTier
{
    Partial,
    Prefix,
    Exact,
}

/// <summary>Coverage and correction cost reported for a Typesense hit.</summary>
public sealed record SearchTextMatch(int TokensMatched, int NumTokensDropped, int TypoPrefixScore);

public sealed record MatchEvidence(MatchTier Tier, string[] Fields, double Relevance)
{
    public static MatchEvidence Read(
        string words,
        params (string Field, string? Text, double Weight)[] fields
    )
    {
        var query = Fold(words);
        if (query.Length == 0)
        {
            return new(MatchTier.Partial, [], 0);
        }

        var tokens = query.Split(' ');
        var tier = MatchTier.Partial;
        var best = 0d;
        var matching = new List<string>();
        foreach (var (field, text, weight) in fields)
        {
            var candidate = Fold(text);
            if (candidate.Length == 0)
            {
                continue;
            }

            var terms = candidate.Split(' ');
            var coverage = tokens.Count(word => terms.Contains(word, StringComparer.Ordinal));
            var completesPrefix =
                candidate.StartsWith(query, StringComparison.Ordinal)
                && candidate != query
                && tokens.Take(tokens.Length - 1).All(terms.Contains);
            if (coverage == 0 && !completesPrefix)
            {
                continue;
            }

            matching.Add(field);
            var current =
                weight >= .75 && query == candidate ? MatchTier.Exact
                : weight >= .75 && completesPrefix ? MatchTier.Prefix
                : MatchTier.Partial;
            tier = current > tier ? current : tier;
            best = Math.Max(
                best,
                weight * Math.Min(1d, (coverage + (completesPrefix ? .5 : 0)) / tokens.Length)
            );
        }

        // Another matching field corroborates the best match without adding
        // the evidence twice for rows with copied metadata.
        var relevance = Math.Min(1d, best + .03 * Math.Max(0, matching.Count - 1));
        return new(tier, matching.ToArray(), relevance);
    }

    public MatchEvidence WithTypoCoverage(SearchTextMatch? match, double weight = 1)
    {
        if (
            Tier != MatchTier.Partial
            || match is not { TokensMatched: > 0, TypoPrefixScore: >= 2 and < 255 }
        )
        {
            return this;
        }

        // Typesense charges two per edit and one for completing the last word.
        // Give corrected words partial credit without promoting the match tier.
        var edits = match.TypoPrefixScore / 2;
        var coverage = (double)match.TokensMatched / (match.TokensMatched + match.NumTokensDropped);
        var corrected = weight * .6 * coverage / (1 + .25 * (edits - 1));
        return corrected <= Relevance
            ? this
            : this with
            {
                Relevance = corrected,
                Fields = [.. Fields, "typo"],
            };
    }

    public static string Fold(string? text) =>
        Regex.Replace((text ?? "").Normalize().ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
}

/// <summary>In-process rank inputs shared by catalog documents and playlist cards.</summary>
public interface ISearchRankItem
{
    string Kind { get; }
    string Key { get; }
    string SongId => "";
    string ShowId => "";
    string ArtistSlug => "";
    string Name => "";
    int Date => 0;
    double Longest => 0;
    long TrackPlays => 0;
    bool Playable => false;
}

public enum SearchIdentityPriority
{
    None,
    ArtistCatalog,
    NamedArtist,
    LibraryTitle,
}

public readonly record struct SearchOrder(
    SearchIdentityPriority Identity,
    bool Related,
    double Score,
    double Popularity
) : IComparable<SearchOrder>
{
    public int CompareTo(SearchOrder other)
    {
        var order = other.Related.CompareTo(Related);
        if (order != 0)
        {
            return order;
        }

        order = Identity.CompareTo(other.Identity);
        if (order != 0)
        {
            return order;
        }

        order = Score.CompareTo(other.Score);
        return order != 0 ? order : Popularity.CompareTo(other.Popularity);
    }
}

public sealed record RankedCandidate(
    ISearchRankItem Row,
    MatchEvidence Match,
    int ExactPriority = 0,
    double Popularity = 0,
    double Confidence = 0,
    double Affinity = 0,
    string? ReadingId = null,
    long SameKindCount = 0,
    bool NamedArtist = false,
    bool Related = false
)
{
    public bool Playable => Row.Playable;

    private bool ExactSong => Row.Kind == "song" && Match.Tier == MatchTier.Exact;

    private double Score(bool mixed, bool leadSong)
    {
        var tier = Match.Tier;
        var relevance = Math.Clamp(Match.Relevance, 0, 1);
        var popularity = Math.Clamp(Popularity, 0, 1);
        var priority = tier == MatchTier.Exact ? 20 * Math.Clamp(ExactPriority, 0, 4) : 0;
        var context = 2 * Math.Clamp(Confidence, 0, 1) + 4 * Math.Clamp(Affinity, 0, 1);
        if (!mixed)
        {
            return 100 * (int)tier
                + priority
                + 9 * relevance
                + (tier == MatchTier.Exact ? 24 : 4) * popularity
                + context;
        }

        // Many live versions have no starts. A handful of starts can therefore
        // already put an obscure version near the top of its kind by percentile.
        var kind = Row.Kind;
        var starts = Math.Log10(1d + Math.Max(0, SameKindCount));
        var playbackSupport = kind switch
        {
            "song" when ExactSong && (leadSong || Affinity > 0) => 1,
            // A song adds up starts from all its shows. It takes more evidence
            // to support that identity than to support one well-played version.
            "song" => Math.Min(1, starts / 4.75),
            "show" or "performance" or "recording" or "sequence" => Math.Min(1, starts / 2.5),
            "venue" when tier == MatchTier.Partial => 0,
            _ => 1,
        };
        var identitySupport = kind is "show" or "performance" or "song" or "sequence"
            ? playbackSupport * playbackSupport
            : 1;
        var tierPoints = tier == MatchTier.Exact ? 26 * (1 + identitySupport) : 26 * (int)tier;
        return tierPoints
            + priority * identitySupport
            + 18 * relevance
            + 24 * popularity * playbackSupport * relevance * relevance
            + context;
    }

    private SearchIdentityPriority IdentityOrder =>
        Row.Kind switch
        {
            "playlist" when ExactPriority is OwnedPlaylistTitle or FollowedPlaylistTitle =>
                SearchIdentityPriority.LibraryTitle,
            "artist" when NamedArtist => SearchIdentityPriority.NamedArtist,
            "show" or "song" when NamedArtist => SearchIdentityPriority.ArtistCatalog,
            _ => SearchIdentityPriority.None,
        };

    public const int OwnedPlaylistTitle = 4;
    public const int FollowedPlaylistTitle = 3;

    public SearchOrder MergeOrder(bool leadSong = false) =>
        new(IdentityOrder, Related, Score(mixed: true, leadSong), Popularity);

    public static string? LeadSong(IEnumerable<RankedCandidate> candidates) =>
        candidates
            .Where(candidate => candidate.ExactSong)
            .OrderByDescending(candidate => candidate.SameKindCount)
            .ThenByDescending(candidate => candidate.Popularity)
            .ThenBy(candidate => candidate.Row.Key, StringComparer.Ordinal)
            .FirstOrDefault()
            ?.Row.Key;

    private static double OrderedValue(ISearchRankItem row, bool chronological, string order) =>
        row.Kind switch
        {
            "performance" or "show" or "sequence" => order switch
            {
                "longest" => row.Longest,
                "popular" => row.TrackPlays,
                "last" => row.Date,
                "first" => -row.Date,
                _ => chronological && row.Kind == "show" ? row.Date : 0,
            },
            _ => 0,
        };

    public static RankedCandidate[] Order(
        IEnumerable<RankedCandidate> candidates,
        bool addTracks = false,
        bool chronological = false,
        string order = "regular",
        bool mixed = false
    )
    {
        var pool = candidates.ToArray();
        // Give the best exact song a route to its catalog page. Other artists'
        // song rows compete with individual performances on playback support.
        var leadSong = mixed ? LeadSong(pool) : null;
        // Reviews support the catalog results in All. A strong match in a
        // review's text can still lead other reviews in the Reviews scope.
        IEnumerable<RankedCandidate> sorted = pool.OrderBy(match =>
                mixed && match.Row.Kind == "review"
            )
            .ThenBy(match => match.Related)
            .ThenByDescending(match =>
                mixed && !addTracks ? match.IdentityOrder : SearchIdentityPriority.None
            )
            .ThenByDescending(match =>
                !mixed || chronological || order != "regular" ? match.Match.Tier : MatchTier.Partial
            )
            .ThenByDescending(match => OrderedValue(match.Row, chronological, order))
            .ThenByDescending(match => match.Score(mixed, match.Row.Key == leadSong))
            .ThenByDescending(match => match.Popularity)
            .ThenBy(match => match.Row.Key, StringComparer.Ordinal)
            .DistinctBy(match => match.Row.Key);
        var matches = sorted.ToList();
        var bestPlayable = addTracks
            ? matches.FindIndex(match => match.Playable && !match.Related)
            : -1;
        if (bestPlayable > 0)
        {
            var insert = matches[0].Match.Tier > matches[bestPlayable].Match.Tier ? 1 : 0;
            var playable = matches[bestPlayable];
            matches.RemoveAt(bestPlayable);
            matches.Insert(insert, playable);
        }
        return matches.ToArray();
    }
}
