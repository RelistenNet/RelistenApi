using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class SourceTrack : BaseRelistenModel, IHasPersistentIdentifier
    {
        [Required]
        public int source_id { get; set; }

        [Required]
        public int source_set_id { get; set; }

        [Required]
        public int artist_id { get; set; }


        [Required]
        public int track_position { get; set; }

        public int? duration { get; set; }

        [Required]
        public string title { get; set; }

        [Required]
        public string slug { get; set; }

        public string mp3_url { get; set; }
        public string mp3_md5 { get; set; }

        public string flac_url { get; set; }
        public string flac_md5 { get; set; }

        [Required]
        public Guid uuid { get; set; }
    }

    public class PlayedSourceTrack
    {
        [Required]
        public SlimSourceWithShowAndArtist source { get; set; }

        [Required]
        public SourceTrack track { get; set; }
    }

    public class LivePlayedTrack
    {
        [Required]
        public DateTime played_at { get; set; }

        [Required]
        public int track_id { get; set; }

        [Required]
        public Guid uuid { get; set; } = Guid.Empty;

        [Required]
        public string app_type { get; set; }

        public PlayedSourceTrack track { get; set; } = null;
    }
}