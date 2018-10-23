using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;

namespace Relisten.Api.Models
{
    public class SourceReview : BaseRelistenModel
    {

        [Required]
        public int source_id { get; set; }

        public int? rating { get; set; }

        public string title { get; set; }

        [Required]
        public string review { get; set; }

        public string author { get; set; }

        [Required]
        public Guid uuid { get; set; }
    }
}