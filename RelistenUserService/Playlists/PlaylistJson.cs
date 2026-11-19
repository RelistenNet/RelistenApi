using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Relisten.Accounts.Contracts.Playlists;

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

    // During a rolling deploy, the old server still reads these durable envelopes
    // with strict deserialization. Track-only writes keep the old shape.
    internal static JsonSerializerOptions StoredOptions { get; } =
        new(Options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { OmitRedundantGuideFields },
            },
        };

    private static void OmitRedundantGuideFields(JsonTypeInfo type)
    {
        if (type.Type == typeof(PlaylistSummary))
        {
            type.Properties.Single(property => property.Name == "track_count").ShouldSerialize = (
                value,
                _
            ) => value is PlaylistSummary summary && summary.TrackCount != summary.EntryCount;
        }
        else if (
            type.Type == typeof(PlaylistSegmentUpserted)
            || type.Type == typeof(PlaylistEntryUpserted)
        )
        {
            type.Properties.Single(property => property.Name == "description").ShouldSerialize = (
                _,
                description
            ) => description is not null;
        }
    }
}
