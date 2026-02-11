using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models
{
    /// <summary>
    /// Classifies what a source track actually is: a song, banter, tuning, etc.
    /// Database stores lowercase values (e.g., "song", "banter", "tuning").
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum TrackType
    {
        Song,
        Banter,
        Tuning,
        Crowd,
        Soundcheck,
        Jam,
        Intro,
        Encore,
        Segue,
        Unknown
    }

    public static class TrackTypeExtensions
    {
        public static string ToDbString(this TrackType value)
        {
            return value switch
            {
                TrackType.Song => "song",
                TrackType.Banter => "banter",
                TrackType.Tuning => "tuning",
                TrackType.Crowd => "crowd",
                TrackType.Soundcheck => "soundcheck",
                TrackType.Jam => "jam",
                TrackType.Intro => "intro",
                TrackType.Encore => "encore",
                TrackType.Segue => "segue",
                _ => "unknown"
            };
        }

        public static TrackType ParseTrackType(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return TrackType.Song;

            return value.ToLowerInvariant() switch
            {
                "song" => TrackType.Song,
                "banter" => TrackType.Banter,
                "tuning" => TrackType.Tuning,
                "crowd" => TrackType.Crowd,
                "soundcheck" => TrackType.Soundcheck,
                "jam" => TrackType.Jam,
                "intro" => TrackType.Intro,
                "encore" => TrackType.Encore,
                "segue" => TrackType.Segue,
                _ => TrackType.Unknown
            };
        }
    }
}
