using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class SourceSet : BaseRelistenModel
    {
        [Required]
        public int source_id { get; set; }


        [Required]
        public int index { get; set; }

        [Required]
        public bool is_encore { get; set; }


        [Required]
        public string name { get; set; }


        [Required]
        public IList<SourceTrack> tracks { get; set; }
    }
}