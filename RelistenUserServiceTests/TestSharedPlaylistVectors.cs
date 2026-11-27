using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Playlists;
using Relisten.Identifiers;
using RelistenUserService.Playlists;
using RelistenUserService.Playlists.Entities;

namespace RelistenUserServiceTests;

/// <summary>
/// Runs the vectors relisten-web also runs (src/domain/sharedVectors.test.ts), so its TypeID
/// encoder and optimistic applyOp cannot drift from UuidTypeId and PlaylistEditor unnoticed.
/// </summary>
[TestFixture]
public sealed class TestSharedPlaylistVectors
{
    private static JsonDocument Fixture(string name) =>
        JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures/playlists", name)
            )
        );

    private static IEnumerable<TestCaseData> TypeIdVectors()
    {
        using var fixture = Fixture("typeid-vectors.json");
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var prefix = vector.GetProperty("prefix").GetString()!;
            var uuid = vector.GetProperty("uuid").GetString()!;
            var typeId = vector.GetProperty("typeid").GetString()!;
            yield return new TestCaseData(prefix, uuid, typeId).SetName(
                $"Shared TypeID vector: {typeId}"
            );
        }
    }

    [TestCaseSource(nameof(TypeIdVectors))]
    public void TypeIds_match_the_shared_encoding(string prefix, string uuid, string typeId)
    {
        var guid = Guid.Parse(uuid);
        UuidTypeId.Format(prefix, guid).Should().Be(typeId);
        UuidTypeId.TryParse(typeId, prefix, out var parsed).Should().BeTrue();
        parsed.Should().Be(guid);

        bool? typed = prefix switch
        {
            "playlist" => PlaylistId.TryParse(typeId, out _),
            "playlist_segment" => PlaylistSegmentId.TryParse(typeId, out _),
            "playlist_entry" => PlaylistEntryId.TryParse(typeId, out _),
            "playlist_operation" => PlaylistOperationId.TryParse(typeId, out _),
            "command" => CommandId.TryParse(typeId, out _),
            _ => null,
        };
        typed?.Should().BeTrue($"{typeId} is a canonical {prefix}_ ID");
    }

    private static IEnumerable<TestCaseData> OperationVectors()
    {
        using var fixture = Fixture("playlist-op-vectors.json");
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            yield return new TestCaseData(vector.GetRawText()).SetName(
                $"Shared operation vector: {vector.GetProperty("name").GetString()}"
            );
        }
    }

    [TestCaseSource(nameof(OperationVectors))]
    public void Operations_match_the_shared_outcome(string json)
    {
        var vector = JsonNode.Parse(json)!.AsObject();
        var editor = BuildEditor(vector["before"]!.AsObject());

        var revision = editor.Playlist.Revision;
        var operations = vector["operations"]!
            .AsArray()
            .Select(operation =>
                new JsonObject
                {
                    ["operation_id"] = PlaylistOperationId.New().Value,
                    ["base_revision"] = revision,
                    ["operation_type"] = operation!["operation_type"]!.GetValue<string>(),
                    ["payload"] = operation["payload"]!.DeepClone(),
                }.Deserialize<PlaylistOperation>(PlaylistJson.Options)!
            )
            .ToArray();
        var request = PlaylistValidation.Normalize(new PlaylistBatchRequest(1, operations));

        var statuses = request.Operations.Select(operation => editor.Apply(operation).Status);
        statuses
            .Should()
            .Equal(vector["statuses"]!.AsArray().Select(status => status!.GetValue<string>()));

        Live(editor)
            .Should()
            .BeEquivalentTo(
                Expected(vector["after"]!.AsObject()),
                options => options.WithStrictOrdering()
            );
    }

    private static PlaylistEditor BuildEditor(JsonObject before)
    {
        var playlist = new Playlist
        {
            Id = PlaylistId.New(),
            OwnerId = UserId.New(),
            Name = before["name"]!.GetValue<string>(),
            Description = Text(before, "description"),
            Revision = 1,
        };
        var segments = new List<PlaylistSegment>();
        var entries = new List<PlaylistEntry>();
        var segmentNodes = before["segments"]!.AsArray();
        var segmentRanks = PlaylistRanks.Between(null, null, segmentNodes.Count);
        for (var s = 0; s < segmentNodes.Count; s++)
        {
            var segmentNode = segmentNodes[s]!.AsObject();
            var segmentId = PlaylistSegmentId.Parse(segmentNode["id"]!.GetValue<string>());
            segments.Add(
                new()
                {
                    Id = segmentId,
                    PlaylistId = playlist.Id,
                    Description = Text(segmentNode, "description"),
                    Rank = segmentRanks[s],
                }
            );
            var entryNodes = segmentNode["entries"]!.AsArray();
            var entryRanks = PlaylistRanks.Between(null, null, entryNodes.Count);
            for (var e = 0; e < entryNodes.Count; e++)
            {
                var entryNode = entryNodes[e]!.AsObject();
                var entry = new PlaylistEntry
                {
                    Id = PlaylistEntryId.Parse(entryNode["id"]!.GetValue<string>()),
                    PlaylistId = playlist.Id,
                    SegmentId = segmentId,
                    TrackId = Text(entryNode, "track_id"),
                    Description = Text(entryNode, "description"),
                    Rank = entryRanks[e],
                };
                entries.Add(entry);
            }
        }
        playlist.EntryCount = entries.Count;
        playlist.TrackCount = entries.Count(entry => entry.TrackId is not null);

        // The write service seeds this with created IDs that already exist; every row here does.
        var existing = segments
            .Select(row => row.Id.Value)
            .Concat(entries.Select(row => row.Id.Value))
            .ToHashSet(StringComparer.Ordinal);
        return new(playlist, segments, entries, existing);
    }

    private static LivePlaylist Live(PlaylistEditor editor) =>
        new(
            editor.Playlist.Name,
            editor.Playlist.Description,
            editor
                .Segments.Values.Where(row => !row.Deleted)
                .OrderBy(row => row.Rank, StringComparer.Ordinal)
                .Select(segment => new LiveSegment(
                    segment.Id.Value,
                    segment.Description,
                    editor
                        .Entries.Values.Where(row => !row.Deleted && row.SegmentId == segment.Id)
                        .OrderBy(row => row.Rank, StringComparer.Ordinal)
                        .Select(entry => new LiveEntry(
                            entry.Id.Value,
                            entry.TrackId,
                            entry.Description
                        ))
                        .ToArray()
                ))
                .ToArray()
        );

    private static LivePlaylist Expected(JsonObject after) =>
        new(
            after["name"]!.GetValue<string>(),
            Text(after, "description"),
            after["segments"]!
                .AsArray()
                .Select(segment => new LiveSegment(
                    segment!["id"]!.GetValue<string>(),
                    Text(segment.AsObject(), "description"),
                    segment["entries"]!
                        .AsArray()
                        .Select(entry => new LiveEntry(
                            entry!["id"]!.GetValue<string>(),
                            Text(entry.AsObject(), "track_id"),
                            Text(entry.AsObject(), "description")
                        ))
                        .ToArray()
                ))
                .ToArray()
        );

    // Absent and null mean the same thing in the fixture.
    private static string? Text(JsonObject node, string property) =>
        node[property]?.GetValue<string>();

    private sealed record LivePlaylist(
        string Name,
        string? Description,
        IReadOnlyList<LiveSegment> Segments
    );

    private sealed record LiveSegment(
        string Id,
        string? Description,
        IReadOnlyList<LiveEntry> Entries
    );

    private sealed record LiveEntry(string Id, string? TrackId, string? Description);
}
