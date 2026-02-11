using System;
using System.Security.Cryptography;
using System.Text;

namespace Relisten.Services.Search.Models
{
    public class HybridSearchRequest
    {
        public string Query { get; set; } = "";
        public int? ArtistId { get; set; }
        public short? Year { get; set; }
        public bool? Soundboard { get; set; }
        public string? RecordingType { get; set; }
        public string Sort { get; set; } = "relevance"; // relevance, date, rating
        public int Limit { get; set; } = 20;
        public int Offset { get; set; } = 0;

        public string CacheKey()
        {
            var raw = $"{Query}|{ArtistId}|{Year}|{Soundboard}|{RecordingType}|{Sort}|{Limit}|{Offset}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash)[..16];
        }
    }
}
