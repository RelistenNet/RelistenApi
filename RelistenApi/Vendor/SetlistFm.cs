
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.SetlistFm
{

    public class Artist
    {
        [JsonProperty("@disambiguation")]
        public string disambiguation { get; set; }
        [JsonProperty("@mbid")]
        public string mbid { get; set; }
        [JsonProperty("@name")]
        public string name { get; set; }
        [JsonProperty("@sortName")]
        public string sortName { get; set; }
        [JsonProperty("@tmid")]
        public string tmid { get; set; }
        public string url { get; set; }
    }

    public class Coords
    {
        [JsonProperty("@lat")]
        public double latitude { get; set; }
        [JsonProperty("@long")]
        public double longitude { get; set; }
    }

    public class Country
    {
        [JsonProperty("@code")]
        public string code { get; set; }
        [JsonProperty("@name")]
        public string name { get; set; }
    }

    public class City
    {
        [JsonProperty("@id")]
        public string id { get; set; }
        [JsonProperty("@name")]
        public string name { get; set; }
        [JsonProperty("@state")]
        public string state { get; set; }
        [JsonProperty("@stateCode")]
        public string stateCode { get; set; }
        public Coords coords { get; set; }
        public Country country { get; set; }
    }

    public class Venue
    {
        [JsonProperty("@id")]
        public string id { get; set; }
        [JsonProperty("@name")]
        public string name { get; set; }
        public City city { get; set; }
        public string url { get; set; }

        public string _iguanaUpstreamId {
            get {
                return "setlistfm:" + id;
            }
        }
    }

    public class Song
    {
        [JsonProperty("@name")]
        public string name { get; set; }
    }

    public class Set
    {
        [JsonProperty("@name")]
        public string name { get; set; }
        public IList<Song> song { get; set; }
        [JsonProperty("@encore")]
        public string encore { get; set; }
    }

    class TolerantListConverter<T> : Newtonsoft.Json.Converters.CustomCreationConverter<IList<T>> where T: new()
    {
        public override IList<T> Create(Type objectType)
        {
            return new List<T>();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            // Load JObject from stream 
            if (reader.TokenType == JsonToken.StartArray)
            {
                var l = new List<T>();
                serializer.Populate(reader, l);
                return l;
            }
            else if(reader.TokenType == JsonToken.StartObject)
            {
                var l = new List<T>();

                T obj = new T();
                serializer.Populate(reader, obj);

                l.Add(obj);

                return l;
            }
            else if (reader.TokenType == JsonToken.String)
            {
                return new List<T>();
            }

            JObject jObject = JObject.Load(reader);

            // Create target object based on JObject 
            var target = (T)Activator.CreateInstance(typeof(T));

            // Populate the object properties 
            serializer.Populate(jObject.CreateReader(), target);

            var list = new List<T>();
            list.Add(target);
            return list;
        }
    }

    class TolerantSetsConverter : Newtonsoft.Json.Converters.CustomCreationConverter<Sets>
    {
        public override Sets Create(Type objectType)
        {
            return new Sets() { set = new List<Set>() };
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var s = Create(objectType);

            // Load JObject from stream 
            if(reader.TokenType == JsonToken.String) {
                return s;
            }

            serializer.Populate(reader, s);

            return s;
        }
    }

    public class Sets
    {
        public IList<Set> set { get; set; }
    }

    public class Setlist
    {
        [JsonProperty("@eventDate")]
        public string eventDate { get; set; }
        [JsonProperty("@id")]
        public string id { get; set; }
        [JsonProperty("@lastUpdated")]
        public string lastUpdated { get; set; }
        [JsonProperty("@tour")]
        public string tour { get; set; }
        [JsonProperty("@versionId")]
        public string versionId { get; set; }
        public Artist artist { get; set; }
        public Venue venue { get; set; }
        public Sets sets { get; set; }
        public string url { get; set; }
        [JsonProperty("@lastFmEventId")]
        public string lastFmEventId { get; set; }
    }

    public class Setlists
    {
        [JsonProperty("@itemsPerPage")]
        public int itemsPerPage { get; set; }
        [JsonProperty("@page")]
        public int page { get; set; }
        [JsonProperty("@total")]
        public int total { get; set; }
        public IList<Setlist> setlist { get; set; }
    }

    public class SetlistsRootObject
    {
        public Setlists setlists { get; set; }
    }

}