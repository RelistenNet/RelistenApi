using System;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Relisten.Api.Models
{
    public enum SourceTrackPlayAppType
    {
        Unknown = 0,
        iOS,
        Web,
        Sonos
    }

    public static class SourceTrackPlayAppTypeHelper
    {
        public static SourceTrackPlayAppType FromString(string str)
        {
            switch (str)
            {
                case "sonos":
                    return SourceTrackPlayAppType.Sonos;

                case "ios":
                    return SourceTrackPlayAppType.iOS;

                case "web":
                    return SourceTrackPlayAppType.Web;

                default:
                    return SourceTrackPlayAppType.Unknown;
            }
        }
    }

    public class SourceTrackPlay
    {
        [Required] public int id { get; set; }

        [Required] public DateTime created_at { get; set; }

        [Required] public Guid source_track_uuid { get; set; }

        [Required] public Guid? user_uuid { get; set; } = null;

        [Required] public SourceTrackPlayAppType app_type { get; set; }

        [Required]
        [JsonConverter(typeof(StringEnumConverter))]
        public SourceTrackPlayAppType app_type_description => app_type;

        public PlayedSourceTrack track { get; set; } = null;
    }
}
