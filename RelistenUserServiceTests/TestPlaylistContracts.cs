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

    [TestCase("{\"description\":null,\"extra\":1}")]
    [TestCase("{\"name\":\"A\",\"name\":\"B\"}")]
    public void Unknown_and_duplicate_payload_fields_are_rejected(string payload)
    {
        var read = () => Read(payload);
        read.Should().Throw<JsonException>();
    }

    [Test]
    public void Missing_base_revision_and_wrong_account_ID_prefix_are_rejected()
    {
        var read = () =>
            JsonSerializer.Deserialize<PlaylistOperation>(
                $$$"""{"operation_id":"{{{PlaylistOperationId.New()}}}","operation_type":"update_metadata","payload":{}}""",
                PlaylistJson.Options
            );
        read.Should().Throw<JsonException>();
        var wrong = () =>
            JsonSerializer.Deserialize<PlaylistId>(
                JsonSerializer.Serialize(CommandId.New().Value),
                PlaylistJson.Options
            );
        wrong.Should().Throw<JsonException>();
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
