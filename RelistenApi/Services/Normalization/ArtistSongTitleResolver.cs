using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Relisten.Normalization;

/// <summary>
/// An existing artist-song identity and its supported names and qualified composition keys.
/// EntityKey must be stable and unique within the candidate population. Empty WorkKeys means
/// that composition identity is unknown; it does not contradict another member's known work.
/// </summary>
public sealed record ArtistSongTitleCandidate(
    string EntityKey,
    Guid ArtistUuid,
    IReadOnlyList<string> Titles,
    IReadOnlyList<string> WorkKeys
);

public enum ArtistSongTitleResolutionKind
{
    NoMatch,
    SharedTitle,
    ConflictingWorks,
}

/// <summary>
/// A title group retains every member key. Representative is null when no title matches or
/// qualified work identities disagree. A representative supplies correspondence policy only;
/// it does not replace stored identities, establish a performance, or authorize publication.
/// </summary>
public sealed record ArtistSongTitleResolution(
    ArtistSongTitleResolutionKind Kind,
    ArtistSongTitleCandidate? Representative,
    IReadOnlyList<ArtistSongTitleCandidate> Members
);

/// <summary>
/// Applies the default that one artist's equal normalized song titles describe one artist-song,
/// unless known composition identities disagree. Callers must apply reviewed correspondences
/// and exclusions before using this result. This policy neither joins artists nor infers that
/// their equal titles identify a shared composition.
/// </summary>
public sealed class ArtistSongTitleResolver
{
    public const string Version = "artist-song-title-v2";
    private readonly Dictionary<(Guid Artist, string Title), ArtistSongTitleResolution> byTitle;

    /// <summary>
    /// Snapshots the complete population and indexes connected supported titles within each
    /// artist. Observe all identities before construction. Later aliases require a new index;
    /// otherwise the same identity could acquire different representatives for different titles.
    /// </summary>
    public ArtistSongTitleResolver(IEnumerable<ArtistSongTitleCandidate> candidates)
    {
        var snapshot = candidates
            .Select(candidate =>
                candidate with
                {
                    Titles = Array.AsReadOnly(candidate.Titles.ToArray()),
                    WorkKeys = Array.AsReadOnly(candidate.WorkKeys.ToArray()),
                }
            )
            .ToArray();
        var titleMembers = IndexTitleMemberships(snapshot);
        byTitle = IndexComponents(snapshot, titleMembers);
    }

    private static Dictionary<(Guid Artist, string Title), List<int>> IndexTitleMemberships(
        IReadOnlyList<ArtistSongTitleCandidate> snapshot
    )
    {
        var titleMembers = new Dictionary<(Guid Artist, string Title), List<int>>();
        for (var index = 0; index < snapshot.Count; index++)
        {
            foreach (var title in snapshot[index].Titles.Select(NormalizeTitle).Distinct())
            {
                if (title.Length == 0)
                {
                    continue;
                }

                var key = (snapshot[index].ArtistUuid, title);
                if (!titleMembers.TryGetValue(key, out var members))
                {
                    members = [];
                    titleMembers.Add(key, members);
                }

                members.Add(index);
            }
        }

        return titleMembers;
    }

    private static Dictionary<
        (Guid Artist, string Title),
        ArtistSongTitleResolution
    > IndexComponents(
        IReadOnlyList<ArtistSongTitleCandidate> snapshot,
        IReadOnlyDictionary<(Guid Artist, string Title), List<int>> titleMembers
    )
    {
        var components = new Dictionary<(Guid Artist, string Title), ArtistSongTitleResolution>();
        foreach (var title in titleMembers.Keys)
        {
            if (components.ContainsKey(title))
            {
                continue;
            }

            var pending = new Queue<(Guid Artist, string Title)>();
            var titles = new HashSet<(Guid Artist, string Title)>();
            var component = new HashSet<int>();
            pending.Enqueue(title);
            while (pending.TryDequeue(out var current))
            {
                if (!titles.Add(current))
                {
                    continue;
                }

                foreach (var index in titleMembers[current])
                {
                    if (!component.Add(index))
                    {
                        continue;
                    }

                    foreach (var alias in snapshot[index].Titles.Select(NormalizeTitle))
                    {
                        if (alias.Length > 0)
                        {
                            pending.Enqueue((current.Artist, alias));
                        }
                    }
                }
            }

            var resolution = ResolveComponent(component.Select(index => snapshot[index]));
            foreach (var alias in titles)
            {
                components.Add(alias, resolution);
            }
        }

        return components;
    }

    /// <summary>
    /// Returns the complete supported-title component and its ordinally smallest stable member
    /// key. Unequal nonempty work sets block the entire component, including aliases connected
    /// through a member without work evidence. The result is a direct correspondence, not a
    /// redirect chain, so every name for one identity reaches the same representative.
    /// </summary>
    public ArtistSongTitleResolution Resolve(Guid artistUuid, string title) =>
        byTitle.TryGetValue((artistUuid, NormalizeTitle(title)), out var resolution)
            ? resolution
            : new(ArtistSongTitleResolutionKind.NoMatch, null, []);

    private static ArtistSongTitleResolution ResolveComponent(
        IEnumerable<ArtistSongTitleCandidate> component
    )
    {
        var members = Array.AsReadOnly(
            component.OrderBy(candidate => candidate.EntityKey, StringComparer.Ordinal).ToArray()
        );
        HashSet<string>? knownWorks = null;
        foreach (var member in members)
        {
            if (member.WorkKeys.Count == 0)
            {
                continue;
            }

            if (knownWorks == null)
            {
                knownWorks = member.WorkKeys.ToHashSet(StringComparer.Ordinal);
            }
            else if (!knownWorks.SetEquals(member.WorkKeys))
            {
                return new(ArtistSongTitleResolutionKind.ConflictingWorks, null, members);
            }
        }

        return new(ArtistSongTitleResolutionKind.SharedTitle, members[0], members);
    }

    /// <summary>
    /// Lowercases canonical Unicode letters and digits and separates punctuation with spaces.
    /// Accents and word boundaries remain significant: Café differs from Cafe and Love Light
    /// differs from Lovelight. This is an identity default, not fuzzy candidate retrieval.
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var result = new StringBuilder();
        var separator = false;
        foreach (var rune in title.Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (
                Rune.IsLetterOrDigit(rune)
                || Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark
            )
            {
                if (separator && result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(Rune.ToLowerInvariant(rune).ToString());
                separator = false;
            }
            else
            {
                separator = true;
            }
        }

        return result.ToString();
    }
}
