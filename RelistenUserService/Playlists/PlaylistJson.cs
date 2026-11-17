using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelistenUserService.Playlists;

/// <summary>The versioned playlist JSON format, also used for durable revision envelopes.</summary>
public static class PlaylistJson
{
    public static JsonSerializerOptions Options { get; } =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            // PostgreSQL jsonb does not preserve the discriminator's position in an object.
            AllowOutOfOrderMetadataProperties = true,
        };
}
