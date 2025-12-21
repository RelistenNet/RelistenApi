using System;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
    public class SourceReview : BaseRelistenModel
    {
        [Required] public int source_id { get; set; }

        public int? rating { get; set; }

        public string? title { get; set; }

        [Required] public string review { get; set; } = null!;

        public string? author { get; set; }

        [Required] public Guid uuid { get; set; }
    }
}
