using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Relisten.Identifiers;

namespace Relisten.Search;

// This file bridges the gap until norm and relisten-admin can store and edit
// reviewed artist names and song aliases, including their search scope.
internal static class SearchReviewedNicknames
{
    private const string ImportedSongSource = "setlist_songs";
    private const string TrackTitleSource = "track titles";

    private static readonly Lazy<Batch> File = new(Load);
    private static readonly Lazy<ArtistNickname[]> OrderedArtists = new(() =>
        File.Value.ArtistNicknames.OrderByDescending(name => name.Nickname.Length).ToArray()
    );
    private static readonly Lazy<FrozenSet<string>> LeadingNames = new(() =>
        Artists
            .Where(name => name.LeadingOnly)
            .Select(name => name.Nickname)
            .ToFrozenSet(StringComparer.Ordinal)
    );
    private static readonly Lazy<ILookup<string, string>> ArtistNames = new(() =>
        File
            .Value.ArtistNicknames.Where(name => name.MatchAsName)
            .ToLookup(name => name.ArtistSlug, name => name.Nickname, StringComparer.Ordinal)
    );

    private static readonly Lazy<SongNickname[]> ExpandedSongNicknames = new(() =>
        File.Value.SongNicknames.Where(song => !song.MatchWithOtherTerms).ToArray()
    );
    private static readonly Lazy<(string Artist, string Alias, string Title)[]> CompoundAliases =
        new(() =>
            File
                .Value.SongNicknames.Where(song => song.MatchWithOtherTerms)
                .Select(song =>
                    (song.ArtistSlug, SearchText.Fold(song.Nickname), SearchText.Fold(song.Song))
                )
                .ToArray()
        );
    private static readonly Lazy<FrozenSet<string>> SongNames = new(() =>
        Songs.Select(song => SearchText.Fold(song.Song)).ToFrozenSet(StringComparer.Ordinal)
    );
    private static readonly Lazy<FrozenSet<string>> LookupNames = new(() =>
        SongTitles
            .Concat(Songs.Select(song => SearchText.Fold(song.Nickname)))
            .ToFrozenSet(StringComparer.Ordinal)
    );

    public static IReadOnlyList<ArtistNickname> Artists => OrderedArtists.Value;
    public static IReadOnlySet<string> LeadingOnlyNames => LeadingNames.Value;
    public static IReadOnlySet<string> SongTitles => SongNames.Value;
    public static IReadOnlySet<string> SongLookupNames => LookupNames.Value;
    private static IReadOnlyList<SongNickname> Songs => ExpandedSongNicknames.Value;

    // These aliases can replace a term in a longer venue or date query.
    // Whole-title nicknames instead carry across artists and have their own readings.
    public static IReadOnlyList<(string Artist, string Alias, string Title)> CompoundSongAliases =>
        CompoundAliases.Value;

    public static IEnumerable<string> ArtistNameAliases(string slug) => ArtistNames.Value[slug];

    public static string IndexedArtistText(string slug) =>
        string.Join(' ', ArtistNameAliases(slug));

    public static long[] SetlistSongIds =>
        Songs
            .Where(song => song.SongSource == ImportedSongSource)
            .Select(song => SetlistId(song.SongId))
            .Distinct()
            .ToArray();

    public static ILookup<string, SongTarget> SongTargets(
        IReadOnlyDictionary<(long Id, string ArtistSlug), Guid> setlistSongs,
        IEnumerable<CatalogSong> catalog
    ) => ExpandSongTargets(ApprovedSongTargets(setlistSongs), catalog);

    internal static ILookup<string, SongTarget> ExpandSongTargets(
        IEnumerable<(string Nickname, SongTarget Target)> approved,
        IEnumerable<CatalogSong> catalog
    )
    {
        var songs = catalog.ToArray();
        var approvals = approved.ToLookup(row => row.Nickname, row => row.Target);
        var songsByTitle = songs.ToLookup(song => song.Target.Title, song => song.Target);
        var claims = SongClaims(songs);
        var expanded = new List<(string Nickname, SongTarget Target)>();
        foreach (var group in approvals)
        {
            expanded.AddRange(group.Select(song => (group.Key, song)));
            var contextualTitles = group
                .GroupBy(song => song.Title)
                .Where(title => title.All(song => song.RequiresArtistContext))
                .Select(title => title.Key)
                .ToHashSet(StringComparer.Ordinal);
            var proposals = group
                .SelectMany(song => songsByTitle[song.Title])
                .Distinct()
                .Select(song =>
                    song with
                    {
                        RequiresArtistContext = contextualTitles.Contains(song.Title),
                    }
                )
                .GroupBy(song => song.Artist);
            foreach (var artist in proposals)
            {
                var localTitles = group
                    .Where(song => song.Artist == artist.Key)
                    .Select(song => song.Title)
                    .Concat(claims[(artist.Key, group.Key)])
                    .Distinct()
                    .ToArray();
                var eligible = artist
                    .Where(song => localTitles.All(title => title == song.Title))
                    .ToArray();
                if (eligible.Select(song => song.Title).Distinct().Take(2).Count() == 1)
                {
                    expanded.AddRange(eligible.Select(song => (group.Key, song)));
                }
            }
        }
        return expanded
            .Distinct()
            .ToLookup(row => row.Nickname, row => row.Target, StringComparer.Ordinal);
    }

    // A song entity can establish a competing identity. A loose track
    // titled "NFA" may be the very same Not Fade Away performance.
    private static ILookup<(string Artist, string Name), string> SongClaims(
        IEnumerable<CatalogSong> songs
    ) =>
        songs
            .Where(song => song.Target.EntityId != null)
            .SelectMany(song =>
                song.Aliases.Prepend(song.Target.Title)
                    .Select(alias => (song.Target.Artist, Alias: alias, song.Target.Title))
            )
            .Concat(CompoundSongAliases)
            .ToLookup(claim => (claim.Artist, claim.Alias), claim => claim.Title);

    private static IEnumerable<(string Nickname, SongTarget Target)> ApprovedSongTargets(
        IReadOnlyDictionary<(long Id, string ArtistSlug), Guid> setlistSongs
    ) =>
        Songs.Select(song =>
        {
            var imported = song.SongSource == ImportedSongSource;
            var entityId = song.SongSource switch
            {
                ImportedSongSource
                    when setlistSongs.TryGetValue(
                        (SetlistId(song.SongId), song.ArtistSlug),
                        out var uuid
                    ) => UuidTypeId.Format("imported_song", uuid),
                ImportedSongSource or TrackTitleSource => null,
                _ => song.SongId,
            };
            return (
                Nickname: SearchText.Fold(song.Nickname),
                Target: new SongTarget(
                    song.ArtistSlug,
                    SearchText.Fold(song.Song),
                    entityId,
                    MatchTrackTitle: imported || song.SongSource == TrackTitleSource,
                    RequiresArtistContext: song.Scope == "artist_context_only"
                )
            );
        });

    private static long SetlistId(string key) =>
        long.Parse(
            key.AsSpan("setlist:".Length),
            System.Globalization.CultureInfo.InvariantCulture
        );

    private static Batch Load()
    {
        using var resource =
            typeof(SearchReviewedNicknames).Assembly.GetManifestResourceStream(
                "Relisten.Search.ReviewedNicknames.json"
            )
            ?? throw new InvalidDataException("The reviewed search nicknames resource is missing.");
        var batch =
            JsonSerializer.Deserialize<Batch>(
                resource,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }
            ) ?? throw new InvalidDataException("The reviewed search nicknames resource is empty.");

        return batch with
        {
            ArtistNicknames = batch
                .ArtistNicknames.Select(row =>
                    row with
                    {
                        Nickname = SearchText.Fold(row.Nickname),
                    }
                )
                .ToArray(),
        };
    }

    private sealed record Batch(ArtistNickname[] ArtistNicknames, SongNickname[] SongNicknames);

    internal sealed record ArtistNickname(
        string ArtistSlug,
        string Nickname,
        string Scope,
        bool MatchAsName
    )
    {
        public bool LeadingOnly => Scope == "leading_only";
    }

    internal sealed record SongNickname(
        string ArtistSlug,
        string SongId,
        string SongSource,
        string Song,
        string Nickname,
        string? Scope,
        bool MatchWithOtherTerms
    );

    internal sealed record SongTarget(
        string Artist,
        string Title,
        string? EntityId,
        bool MatchTrackTitle,
        bool RequiresArtistContext = false
    );

    internal sealed record CatalogSong(SongTarget Target, string[] Aliases);
}

internal sealed record ReviewedSongReading(
    SearchReviewedNicknames.SongTarget[] Targets,
    IReadOnlySet<string> Venues,
    bool Contextual,
    bool ReplacesYear
);
