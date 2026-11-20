using System.Text.RegularExpressions;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserService.Playlists;

internal static partial class PlaylistCopy
{
    internal static (
        CreatePlaylistRequest Request,
        IReadOnlyDictionary<PlaylistEntryId, int> Durations
    ) CreateRequest(
        CopyPublicPlaylistRequest request,
        Playlist source,
        IReadOnlyList<PlaylistSegmentSnapshot> structure,
        IReadOnlyDictionary<PlaylistEntryId, int> sourceDurations
    )
    {
        var entryIds = structure
            .SelectMany(segment => segment.Entries)
            .ToDictionary(
                entry => entry.EntryId.Value,
                _ => PlaylistEntryId.New(),
                StringComparer.Ordinal
            );
        var segments = structure
            .Select(segment => new PlaylistSegmentInput(
                PlaylistSegmentId.New(),
                segment
                    .Entries.Select(entry => new PlaylistEntryInput(
                        entryIds[entry.EntryId.Value],
                        entry.TrackId,
                        Rewrite(entry.Description, entryIds)
                    ))
                    .ToArray(),
                Rewrite(segment.Description, entryIds)
            ))
            .ToArray();

        return (
            new(
                request.ContractVersion,
                request.CommandId,
                PlaylistId.New(),
                new(source.Name, Rewrite(source.Description, entryIds)),
                segments
            ),
            sourceDurations.ToDictionary(pair => entryIds[pair.Key.Value], pair => pair.Value)
        );
    }

    // Match literal prefixes anywhere in authored text. Existing links to entries
    // outside this snapshot and bare ID mentions keep their original text.
    [GeneratedRegex("#entry=(playlist_entry_[0-7][0-9a-hjkmnp-tv-z]{25})&t=")]
    private static partial Regex TimestampPrefix();

    private static string? Rewrite(
        string? text,
        IReadOnlyDictionary<string, PlaylistEntryId> entryIds
    ) =>
        text is null
            ? null
            : TimestampPrefix()
                .Replace(
                    text,
                    match =>
                        entryIds.TryGetValue(match.Groups[1].Value, out var id)
                            ? $"#entry={id}&t="
                            : match.Value
                );
}
