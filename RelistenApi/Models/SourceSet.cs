using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class SourceSet : BaseRelistenModel
    {
        [V2JsonOnly] [Required] public int source_id { get; set; }
        [Required] public Guid source_uuid { get; set; }
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }

        [Required] public Guid uuid { get; set; }

        [Required] public int index { get; set; }

        [Required] public bool is_encore { get; set; }


        [Required] public string name { get; set; }


        [Required] public IList<SourceTrack> tracks { get; set; }
    }
}