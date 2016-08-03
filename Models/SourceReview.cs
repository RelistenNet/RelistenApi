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

        [Required]
        public string title { get; set; }

        [Required]
        public string review { get; set; }

        [Required]
        public string author { get; set; }
    }
}