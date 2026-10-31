using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Relisten.Accounts.Contracts.Library;
using RelistenUserService.Library;

namespace RelistenUserServiceTests;

[TestFixture]
public sealed class TestTypedAccountContracts
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    [TestCase("artist", "artist_3qmp7zjbg1rpebhtyfy43096vj")]
    [TestCase("show", "show_0gmtpk4g0pf5yfayb76m0j0k7g")]
    [TestCase("track", "track_0gmtpk4g0pf5yfayb76m0j0k7g")]
    [TestCase("venue", "venue_fp4h900f")]
    [TestCase("song", "song_62mhvjyd")]
    [TestCase("imported_venue", "imported_venue_78331pxdqjdg434rrgm1d2pnrj")]
    public void Canonical_target_survives_the_JSON_and_validation_boundary(string kind, string id)
    {
        var original = new FavoriteMutationBatchRequest(
            1,
            [new(MutationId.New(), new(kind, id), "favorite", FavoriteId.New())]
        );
        var parsed = JsonSerializer.Deserialize<FavoriteMutationBatchRequest>(
            JsonSerializer.Serialize(original, Json),
            Json
        );
        FavoriteMutationRequestValidator
            .TryValidate(parsed, out var commands, out _)
            .Should()
            .BeTrue();
        commands.Single().CatalogType.Should().Be(kind);
        commands.Single().CatalogId.Should().Be(id);
        commands.Single().FavoriteId.Should().Be(original.Mutations![0].FavoriteId);
    }

    [TestCase("venue", "song_62mhvjyd")]
    [TestCase("song", "777f627f-442e-9a6f-a366-ac734827dcc5")]
    [TestCase("source_track", "track_0gmtpk4g0pf5yfayb76m0j0k7g")]
    [TestCase("imported_venue", "venue_fp4h900f")]
    public void Wrong_target_kind_or_format_is_rejected(string kind, string id)
    {
        var request = new FavoriteMutationBatchRequest(
            1,
            [new(MutationId.New(), new(kind, id), "favorite", FavoriteId.New())]
        );
        FavoriteMutationRequestValidator.TryValidate(request, out _, out _).Should().BeFalse();
    }

    [TestCase("019a0000-0000-7000-8000-000000000001")]
    [TestCase("session_01k8000000e008000000000001")]
    public void A_user_ID_cannot_be_read_as_another_kind_or_a_raw_UUID(string value)
    {
        var read = () => JsonSerializer.Deserialize<UserId>(JsonSerializer.Serialize(value), Json);
        read.Should().Throw<JsonException>();
    }
}
