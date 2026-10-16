using System;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models;

/// <summary>Known ingestion providers. Serialized values match the existing upstream source names.</summary>
[Newtonsoft.Json.JsonConverter(typeof(UpstreamProviderNewtonsoftConverter))]
[JsonConverter(typeof(UpstreamProviderJsonConverter))]
public enum UpstreamProvider
{
    [EnumMember(Value = "archive.org")]
    [JsonStringEnumMemberName("archive.org")]
    ArchiveOrg,

    [EnumMember(Value = "phish.in")]
    [JsonStringEnumMemberName("phish.in")]
    Phishin,

    [EnumMember(Value = "phish.net")]
    [JsonStringEnumMemberName("phish.net")]
    PhishNet,

    [EnumMember(Value = "setlist.fm")]
    [JsonStringEnumMemberName("setlist.fm")]
    SetlistFm,

    [EnumMember(Value = "phantasytour.com")]
    [JsonStringEnumMemberName("phantasytour.com")]
    PhantasyTour,

    [EnumMember(Value = "jerrygarcia.com")]
    [JsonStringEnumMemberName("jerrygarcia.com")]
    JerryGarcia,

    [EnumMember(Value = "local")]
    [JsonStringEnumMemberName("local")]
    Local,

    [EnumMember(Value = "panicstream.com")]
    [JsonStringEnumMemberName("panicstream.com")]
    PanicStream,
}

/// <summary>Converts provider identifiers at database, command-line, and persisted-key boundaries.</summary>
public static class UpstreamProviders
{
    /// <summary>Returns the unchanged source name used by SQL, snapshots, and receipts.</summary>
    public static string ToValue(this UpstreamProvider provider) =>
        provider switch
        {
            UpstreamProvider.ArchiveOrg => "archive.org",
            UpstreamProvider.Phishin => "phish.in",
            UpstreamProvider.PhishNet => "phish.net",
            UpstreamProvider.SetlistFm => "setlist.fm",
            UpstreamProvider.PhantasyTour => "phantasytour.com",
            UpstreamProvider.JerryGarcia => "jerrygarcia.com",
            UpstreamProvider.Local => "local",
            UpstreamProvider.PanicStream => "panicstream.com",
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unknown provider."
            ),
        };

    /// <summary>Accepts the exact stored name; unknown names and numeric enum values are rejected.</summary>
    public static UpstreamProvider Parse(string value)
    {
        if (TryParse(value, out var provider))
        {
            return provider;
        }

        throw new ArgumentException($"Unknown upstream provider '{value}'.", nameof(value));
    }

    /// <summary>Recognizes known source names without rejecting unknown catalog rows.</summary>
    public static bool TryParse(string value, out UpstreamProvider provider)
    {
        foreach (var candidate in Enum.GetValues<UpstreamProvider>())
        {
            if (candidate.ToValue() == value)
            {
                provider = candidate;
                return true;
            }
        }

        provider = default;
        return false;
    }
}

/// <summary>Keeps API JSON string-valued and rejects numeric provider inputs.</summary>
public sealed class UpstreamProviderNewtonsoftConverter : StringEnumConverter
{
    public UpstreamProviderNewtonsoftConverter() => AllowIntegerValues = false;
}

/// <summary>Keeps Temporal payloads string-valued and rejects numeric provider inputs.</summary>
public sealed class UpstreamProviderJsonConverter : JsonStringEnumConverter<UpstreamProvider>
{
    public UpstreamProviderJsonConverter()
        : base(allowIntegerValues: false) { }
}
