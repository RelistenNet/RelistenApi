
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Relisten.Vendor.SetlistFm
{

    public class Artist
    {
        [JsonProperty("disambiguation")]
        public string disambiguation { get; set; }
        [JsonProperty("mbid")]
        public string mbid { get; set; }
        [JsonProperty("name")]
        public string name { get; set; }
        [JsonProperty("sortName")]
        public string sortName { get; set; }
        [JsonProperty("tmid")]
        public string tmid { get; set; }
        public string url { get; set; }
    }

    public class Coords
    {
        [JsonProperty("lat")]
        public string latitude { get; set; }
        [JsonProperty("long")]
        public string longitude { get; set; }
    }

    public class Country
    {
        [JsonProperty("code")]
        public string code { get; set; }
        [JsonProperty("name")]
        public string name { get; set; }
    }

    public class City
    {
        [JsonProperty("id")]
        public string id { get; set; }
        [JsonProperty("name")]
        public string name { get; set; }
        [JsonProperty("state")]
        public string state { get; set; }
        [JsonProperty("stateCode")]
        public string stateCode { get; set; }
        public Coords coords { get; set; }
        public Country country { get; set; }
    }

    public class Venue
    {
        [JsonProperty("id")]
        public string id { get; set; }
        [JsonProperty("name")]
        public string name { get; set; }
        public City city { get; set; }
        public string url { get; set; }
    }

    public class Set
    {
        [JsonProperty("name")]
        public string name { get; set; }
        public object song { get; set; }
        [JsonProperty("encore")]
        public string encore { get; set; }
    }

    public class Sets
    {
        public IList<Set> set { get; set; }
    }

    public class Setlist
    {
        [JsonProperty("eventDate")]
        public string eventDate { get; set; }
        [JsonProperty("id")]
        public string id { get; set; }
        [JsonProperty("lastUpdated")]
        public string lastUpdated { get; set; }
        [JsonProperty("tour")]
        public string tour { get; set; }
        [JsonProperty("versionId")]
        public string versionId { get; set; }
        public Artist artist { get; set; }
        public Venue venue { get; set; }
        public Sets sets { get; set; }
        public string url { get; set; }
        [JsonProperty("lastFmEventId")]
        public string lastFmEventId { get; set; }
    }

    public class Setlists
    {
        [JsonProperty("itemsPerPage")]
        public string itemsPerPage { get; set; }
        [JsonProperty("page")]
        public string page { get; set; }
        [JsonProperty("total")]
        public string total { get; set; }
        public IList<Setlist> setlist { get; set; }
    }

    public class SetlistsRootObject
    {
        public Setlists setlists { get; set; }
    }

}