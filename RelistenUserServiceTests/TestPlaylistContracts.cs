using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Identifiers;
using Relisten.Accounts.Contracts.Playlists;
using RelistenUserService.Playlists;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestPlaylistContracts
{
    [Test]
    public void Ranks_match_pinned_upstream_adversarial_and_bulk_fixtures()
    {
        using var fixture = JsonDocument.Parse(
            File.ReadAllText(
                Path.Combine(
                    TestContext.CurrentContext.TestDirectory,
                    "Fixtures/playlists/ranks.json"
                )
            )
        );
        foreach (var row in fixture.RootElement.GetProperty("rows").EnumerateArray())
        {
            PlaylistRanks
                .Between(row.GetProperty("lower").GetString(), row.GetProperty("upper").GetString())
                .Should()
                .Be(row.GetProperty("expected").GetString());
        }
        foreach (var row in fixture.RootElement.GetProperty("runs").EnumerateArray())
        {
            PlaylistRanks
                .Between(
                    row.GetProperty("lower").GetString(),
                    row.GetProperty("upper").GetString(),
                    row.GetProperty("count").GetInt32()
                )
                .Should()
                .Equal(
                    row.GetProperty("expected").EnumerateArray().Select(value => value.GetString())
                );
        }
    }

    [Test]
    public void Typed_operations_preserve_patch_presence_and_normalized_hash_identity()
    {
        var id = PlaylistId.New();
        var absent = Read("{}");
        var clear = Read("{\"description\":null}");
        var literal = Read("{\"description\":\"N\"}");
        var multiline = Read("{\"description\":\"N\\nV:\\nA|\"}");
        var hashes = new[] { absent, clear, literal, multiline }
            .Select(operation => Convert.ToHexString(PlaylistHasher.Hash(id, 1, operation)))
            .ToArray();
        hashes.Distinct().Should().HaveCount(4);
        var normalized = Read("{\"name\":\"  Cafe\\u0301  \"}");
        var composed = Read("{\"name\":\"Caf\\u00e9\"}");
        PlaylistHasher.Hash(id, 1, normalized).Should().Equal(PlaylistHasher.Hash(id, 1, composed));
        PlaylistHasher
            .Hash(
                id,
                1,
                composed with
                {
                    BaseRevision = 999,
                    OperationId = PlaylistOperationId.New(),
                }
            )
            .Should()
            .Equal(PlaylistHasher.Hash(id, 1, composed));
        var roundTrip = JsonSerializer.Deserialize<PlaylistOperation>(
            JsonSerializer.Serialize(clear, PlaylistJson.Options),
            PlaylistJson.Options
        )!;
        ((UpdatePlaylistMetadata)roundTrip.Payload)
            .Description.Should()
            .Be(new OptionalText(true, null));
    }

    [Test]
    public void Durable_v1_hashes_preserve_fixed_fields_and_array_order()
    {
        var id = PlaylistId.Parse("playlist_01k8000000e008000000000001");
        var firstSegment = PlaylistSegmentId.Parse("playlist_segment_01k8000000e008000000000002");
        var secondSegment = PlaylistSegmentId.Parse("playlist_segment_01k8000000e008000000000003");
        PlaylistEntryInput first = new(
            PlaylistEntryId.Parse("playlist_entry_01k8000000e008000000000004"),
            "track_01k8000000e008000000000007"
        );
        PlaylistEntryInput second = new(
            PlaylistEntryId.Parse("playlist_entry_01k8000000e008000000000005"),
            "track_01k8000000e008000000000008"
        );
        PlaylistEntryInput third = new(
            PlaylistEntryId.Parse("playlist_entry_01k8000000e008000000000006"),
            "track_01k8000000e008000000000009"
        );
        var command = CommandId.New();
        var create = PlaylistValidation.Normalize(
            new CreatePlaylistRequest(
                1,
                command,
                id,
                new(" Cafe\u0301 ", "line\nA|N|V:"),
                [new(secondSegment, [third]), new(firstSegment, [second, first])]
            )
        );
        var insert = new PlaylistOperation(
            PlaylistOperationId.New(),
            0,
            "insert_entries",
            new InsertPlaylistEntries(secondSegment, [second, first], third.EntryId, first.EntryId)
        );
        // Independently calculated from the frozen UTF-8 line format, not another hasher call.
        Convert
            .ToHexString(PlaylistHasher.Hash(create))
            .Should()
            .Be("EB1F94DCF6D8008FAFF4CFB63FB0E76B3996F61FAD3C82512A6ECF78250F7022");
        Convert
            .ToHexString(PlaylistHasher.Hash(id, 1, insert))
            .Should()
            .Be("EC41BBFC55209D0A849A2AB96738C802FDC7CA0454FC5A81C67E975AFC4C9EC9");
        Convert
            .ToHexString(PlaylistHasher.Hash(id, new ArchivePlaylistRequest(1, command, true)))
            .Should()
            .Be("ED80EBBA8EDBD05DE7D868BAA6F8A48E7766B70A1AA6EB3904CEDF523B103B10");
    }

    private static PlaylistOperation Read(string payload)
    {
        var operation = JsonSerializer.Deserialize<PlaylistOperation>(
            $$$"""{"operation_id":"{{{PlaylistOperationId.New()}}}","base_revision":0,"operation_type":"update_metadata","payload":{{{payload}}}} """,
            PlaylistJson.Options
        )!;
        return PlaylistValidation.Normalize(new PlaylistBatchRequest(1, [operation])).Operations[0];
    }
}
