using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class SourceTrack : BaseRelistenModel
    {
        [Required]
        public int source_id { get; set; }

        [Required]
        public int source_set_id { get; set; }


        [Required]
        public int track_position { get; set; }

        public int? duration { get; set; }

        [Required]
        public string title { get; set; }

        [Required]
        public string slug { get; set; }

        [Required]
        public string mp3_url { get; set; }

        public string md5 { get; set; }
    }
}