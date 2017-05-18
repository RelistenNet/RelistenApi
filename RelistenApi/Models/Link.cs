using System;
using System.ComponentModel.DataAnnotations;

namespace Relisten.Api.Models
{
	public class Link : BaseRelistenModel
	{
		[Required]
		public int source_id { get; set; }

		[Required]
		public int upstream_source_id { get; set; }

		[Required]
		public bool for_reviews { get; set; }

		[Required]
		public bool for_ratings { get; set; }

		[Required]
		public bool for_source { get; set; }

		[Required]
		public string url { get; set; }

		[Required]
		public string label { get; set; }
	}
}
