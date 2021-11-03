using System;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class SourceTrack : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int source_id { get; set; }
        [Required] public Guid source_uuid { get; set; }

        [V2JsonOnly] [Required] public int source_set_id { get; set; }
        [Required] public Guid source_set_uuid { get; set; }

        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }


        [Required] public int track_position { get; set; }

        public int? duration { get; set; }

        [Required] public string title { get; set; }

        [Required] public string slug { get; set; }

        public string mp3_url { get; set; }
        public string mp3_md5 { get; set; }

        public string flac_url { get; set; }
        public string flac_md5 { get; set; }

        [Required] public Guid uuid { get; set; }
    }

    public class PlayedSourceTrack
    {
        [Required] public SlimSourceWithShowVenueAndArtist source { get; set; }

        [Required] public SourceTrack track { get; set; }
    }

    public class LivePlayedTrack
    {
        [Required] public DateTime played_at { get; set; }

        [Required] public int track_id { get; set; }

        [Required] public Guid uuid { get; set; } = Guid.Empty;

        [Required] public string app_type { get; set; }

        public PlayedSourceTrack track { get; set; } = null;
    }
}
