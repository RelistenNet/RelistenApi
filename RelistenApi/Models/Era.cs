using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class Era : BaseRelistenModel
    {
        [Required]
        public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }

        [Required]
        public int order { get; set; }

        [Required]
        public string name { get; set; }
    }
}