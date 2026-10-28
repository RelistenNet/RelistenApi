using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace Relisten.Normalization.Songs;

/// One labelled track of the rollout gate: what its title names, part by part, as a person
/// read it without seeing the catalogue. Each part is the answers that count as right: song
/// names, and NotASong when the part may be stage talk, tuning or an unnamed jam.
public sealed record GoldTrack(
    string Artist,
    Guid TrackUuid,
    string Title,
    IReadOnlyList<IReadOnlyList<string>> Parts
)
{
    public const string NotASong = "-";
}

/// One norm.track_songs row of a labelled track, its song followed to the survivor of merges.
public sealed record GatePart(Guid? Song, bool Resolved);

/// - Right: every labelled part is placed and nothing is placed wrong.
/// - Wrong: a part is linked to a song the label doesn't name (a placeholder linked counts).
/// - Hidden: a song the label names is written as not a song.
/// - Partial: some parts linked right, the rest left open.
/// - Open: nothing placed.
public enum GateVerdict
{
    Right,
    Wrong,
    Hidden,
    Partial,
    Open,
}

public sealed record GateTrack(GoldTrack Gold, GateVerdict Verdict, string Got);

/// The song rollout gate (string-theory docs/catalogue.md §Rollout: title→song precision on
/// the artist's evaluation sample). Unlike setlist agreement it is assignment precision: a
/// link is right only when the labelled track names that song.
/// - Precision: links to the song the label names, of all links written.
/// - Coverage: labelled songs linked right, of all labelled songs.
/// NotInCatalogue lists labelled names no song or alias carries: a song the catalogue lacks,
/// or one it names differently (then the label gets the catalogue's spelling as an
/// alternative; names are a convention, identity is what is measured).
public sealed record GateResult(
    string Artist,
    IReadOnlyList<GateTrack> Tracks,
    int Links,
    int LinksRight,
    int LabelledSongs,
    IReadOnlyList<string> NotInCatalogue
)
{
    public double Precision => Links == 0 ? double.NaN : (double)LinksRight / Links;

    public double Coverage => LabelledSongs == 0 ? double.NaN : (double)LinksRight / LabelledSongs;

    public int Count(GateVerdict verdict) => Tracks.Count(t => t.Verdict == verdict);

    public override string ToString() =>
        $"{Artist}: precision {Precision:P1} ({LinksRight}/{Links} links), coverage {Coverage:P1} "
        + $"of {LabelledSongs} labelled songs; {Tracks.Count} tracks: "
        + string.Join(
            ", ",
            Enum.GetValues<GateVerdict>()
                .Select(v => $"{Count(v)} {v.ToString().ToLowerInvariant()}")
        );
}

public static class SongGate
{
    /// The label file: tab-separated with a header naming at least artist, track_uuid, title and
    /// songs. Songs are parts joined by " > ", alternatives inside a part by " | ", "-" for not
    /// a song. A track with a part labelled "?" (the title names nothing readable) is left out.
    public static IReadOnlyList<GoldTrack> Read(string path) => Parse(File.ReadLines(path));

    public static IReadOnlyList<GoldTrack> Parse(IEnumerable<string> lines)
    {
        using var rows = lines.GetEnumerator();
        if (!rows.MoveNext())
        {
            return [];
        }

        var header = rows
            .Current.Split('\t')
            .Select((h, i) => (h, i))
            .ToDictionary(c => c.h, c => c.i);
        var gold = new List<GoldTrack>();
        while (rows.MoveNext())
        {
            var f = rows.Current.Split('\t');
            if (f.Length < header.Count || string.IsNullOrWhiteSpace(f[header["track_uuid"]]))
            {
                continue;
            }

            var parts = f[header["songs"]]
                .Split(" > ")
                .Select(p => (IReadOnlyList<string>)p.Split(" | ").Select(a => a.Trim()).ToList())
                .ToList();
            if (parts.Any(p => p.Contains("?") || p.All(string.IsNullOrEmpty)))
            {
                continue;
            }

            gold.Add(
                new GoldTrack(
                    f[header["artist"]],
                    Guid.Parse(f[header["track_uuid"]]),
                    f[header["title"]],
                    parts
                )
            );
        }

        return gold;
    }

    /// Compared as normalised titles are, with a leading "the" ignored ("The Wheel").
    public static string Key(string name)
    {
        var key = TitleNormalizer.Normalize(name);
        return key.StartsWith("the ") ? key[4..] : key;
    }

    /// Scores labelled tracks against what was written for them. songKeys gives each surviving
    /// song its name and every spelling that reaches it (aliases, merged songs);
    /// songNames its display name.
    public static GateResult Score(
        string artist,
        IReadOnlyList<GoldTrack> gold,
        IReadOnlyDictionary<Guid, IReadOnlyList<GatePart>> written,
        IReadOnlyDictionary<Guid, IReadOnlySet<string>> songKeys,
        IReadOnlyDictionary<Guid, string> songNames
    )
    {
        var known = songKeys.Values.SelectMany(k => k).ToHashSet();
        var (links, right, labelled) = (0, 0, 0);
        var missing = new SortedSet<string>();
        var tracks = new List<GateTrack>();
        foreach (var track in gold)
        {
            var parts = track
                .Parts.Select(p =>
                    (
                        Songs: p.Where(a => a != GoldTrack.NotASong).Select(Key).ToHashSet(),
                        MayBeNone: p.Contains(GoldTrack.NotASong)
                    )
                )
                .ToList();
            foreach (var p in track.Parts.Where(p => !p.Contains(GoldTrack.NotASong)))
            {
                labelled++;
                if (!p.Any(a => known.Contains(Key(a))))
                {
                    missing.Add(string.Join(" | ", p));
                }
            }

            var rows = written.GetValueOrDefault(track.TrackUuid) ?? [];
            var used = new bool[parts.Count];
            var (wrong, hidden, placed) = (false, false, false);
            // Links first, each to the first unplaced part that names its song, so a link isn't
            // spent on a part a not-a-song row could take.
            foreach (var row in rows.Where(r => r.Resolved && r.Song != null))
            {
                links++;
                var keys = songKeys.GetValueOrDefault(row.Song!.Value) ?? new HashSet<string>();
                var at = Enumerable
                    .Range(0, parts.Count)
                    .FirstOrDefault(i => !used[i] && parts[i].Songs.Overlaps(keys), -1);
                if (at < 0)
                {
                    wrong = true;
                    continue;
                }

                used[at] = true;
                placed = true;
                if (!parts[at].MayBeNone)
                {
                    right++;
                }
            }

            foreach (var row in rows.Where(r => r.Resolved && r.Song == null))
            {
                var at = Enumerable
                    .Range(0, parts.Count)
                    .FirstOrDefault(i => !used[i] && parts[i].MayBeNone, -1);
                if (at >= 0)
                {
                    used[at] = true;
                    placed = true;
                }
                else if (Enumerable.Range(0, parts.Count).Any(i => !used[i]))
                {
                    // A song the title names, written as stage talk: the listener never sees it.
                    hidden = true;
                }
            }

            var verdict =
                wrong ? GateVerdict.Wrong
                : hidden ? GateVerdict.Hidden
                : used.All(u => u) ? GateVerdict.Right
                : placed ? GateVerdict.Partial
                : GateVerdict.Open;
            var got =
                rows.Count == 0
                    ? "nothing"
                    : string.Join(
                        " > ",
                        rows.Select(r =>
                            !r.Resolved ? "open"
                            : r.Song == null ? GoldTrack.NotASong
                            : songNames.GetValueOrDefault(r.Song.Value, r.Song.Value.ToString())
                        )
                    );
            tracks.Add(new GateTrack(track, verdict, got));
        }

        return new GateResult(artist, tracks, links, right, labelled, missing.ToList());
    }

    /// The gate for one artist on a catalogue database (the lab, or production read-only):
    /// only reads.
    public static async Task<GateResult> ScoreAsync(
        NpgsqlConnection connection,
        string artistSlug,
        IReadOnlyList<GoldTrack> gold
    )
    {
        gold = gold.Where(g => g.Artist == artistSlug).ToList();
        var artist = await connection.QuerySingleAsync<Guid>(
            "SELECT uuid FROM artists WHERE slug = @artistSlug",
            new { artistSlug }
        );
        var songs = (
            await connection.QueryAsync<(Guid Uuid, string Name, Guid? MergedInto)>(
                "SELECT uuid, name, merged_into FROM norm.songs WHERE artist_uuid = @artist",
                new { artist }
            )
        ).ToDictionary(s => s.Uuid);
        var aliases = await connection.QueryAsync<(Guid Song, string Alias)>(
            "SELECT song_uuid, alias FROM norm.song_aliases WHERE artist_uuid = @artist",
            new { artist }
        );

        // A merged song is read as its survivor, as readers do.
        Guid Survivor(Guid song)
        {
            for (var depth = 0; depth < 20 && songs[song].MergedInto is { } next; depth++)
            {
                song = next;
            }

            return song;
        }

        var keys = new Dictionary<Guid, HashSet<string>>();
        void Add(Guid song, string name)
        {
            var survivor = Survivor(song);
            if (!keys.TryGetValue(survivor, out var set))
            {
                keys[survivor] = set = [];
            }

            set.Add(Key(name));
        }

        foreach (var s in songs.Values)
        {
            Add(s.Uuid, s.Name);
        }

        foreach (var a in aliases.Where(a => songs.ContainsKey(a.Song)))
        {
            Add(a.Song, a.Alias);
        }

        var rows = await connection.QueryAsync<(Guid Track, Guid? Song, bool Resolved)>(
            """
            SELECT track_uuid, song_uuid, resolved FROM norm.track_songs
            WHERE track_uuid = ANY(@tracks) ORDER BY track_uuid, part
            """,
            new { tracks = gold.Select(g => g.TrackUuid).ToArray() }
        );
        var written = rows.GroupBy(r => r.Track)
            .ToDictionary(
                g => g.Key,
                g =>
                    (IReadOnlyList<GatePart>)
                        g.Select(r => new GatePart(
                                r.Song is { } s && songs.ContainsKey(s) ? Survivor(s) : r.Song,
                                r.Resolved
                            ))
                            .ToList()
            );

        return Score(
            artistSlug,
            gold,
            written,
            keys.ToDictionary(k => k.Key, k => (IReadOnlySet<string>)k.Value),
            songs.Values.ToDictionary(s => s.Uuid, s => s.Name)
        );
    }
}
