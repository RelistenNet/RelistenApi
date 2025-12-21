using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.SetlistFm
{
    public class Artist
    {
        public string mbid { get; set; } = null!;
        public int tmid { get; set; }
        public string name { get; set; } = null!;
        public string sortName { get; set; } = null!;
        public string disambiguation { get; set; } = null!;
        public string url { get; set; } = null!;
    }

    public class Coords
    {
        public double lat { get; set; }
        public double @long { get; set; }
    }

    public class Country
    {
        public string code { get; set; } = null!;
        public string name { get; set; } = null!;
    }

    public class City
    {
        public string id { get; set; } = null!;
        public string name { get; set; } = null!;
        public string state { get; set; } = null!;
        public string stateCode { get; set; } = null!;
        public Coords coords { get; set; } = null!;
        public Country country { get; set; } = null!;
    }

    public class Venue
    {
        public string id { get; set; } = null!;
        public string name { get; set; } = null!;
        public City city { get; set; } = null!;
        public string url { get; set; } = null!;

        public string _iguanaUpstreamId => "setlistfm:" + id;
    }

    public class Song
    {
        public string name { get; set; } = null!;
//        public string info { get; set; }
//        public Cover cover { get; set; }
//        public With with { get; set; }
    }

    public class Set
    {
        public string name { get; set; } = null!;
        public List<Song> song { get; set; } = null!;
        public int? encore { get; set; }
    }

    internal class TolerantListConverter<T> : CustomCreationConverter<IList<T>> where T : new()
    {
        public override IList<T> Create(Type objectType)
        {
            return new List<T>();
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
            JsonSerializer serializer)
        {
            // Load JObject from stream 
            if (reader.TokenType == JsonToken.StartArray)
            {
                var l = new List<T>();
                serializer.Populate(reader, l);
                return l;
            }

            if (reader.TokenType == JsonToken.StartObject)
            {
                var l = new List<T>();

                var obj = new T();
                serializer.Populate(reader, obj);

                l.Add(obj);

                return l;
            }

            if (reader.TokenType == JsonToken.String)
            {
                return new List<T>();
            }

            var jObject = JObject.Load(reader);

            // Create target object based on JObject 
            var target = (T)Activator.CreateInstance(typeof(T))!;

            // Populate the object properties 
            serializer.Populate(jObject.CreateReader(), target);

            var list = new List<T>();
            list.Add(target);
            return list;
        }
    }

    internal class TolerantSetsConverter : CustomCreationConverter<Sets>
    {
        public override Sets Create(Type objectType)
        {
            return new Sets {set = new List<Set>()};
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue,
            JsonSerializer serializer)
        {
            var s = Create(objectType);

            // Load JObject from stream 
            if (reader.TokenType == JsonToken.String)
            {
                return s;
            }

            serializer.Populate(reader, s);

            return s;
        }
    }

    public class Sets
    {
        public IList<Set> set { get; set; } = null!;
    }

    public class Setlist
    {
        public string id { get; set; } = null!;
        public string versionId { get; set; } = null!;
        public string eventDate { get; set; } = null!;
        public DateTime lastUpdated { get; set; }
        public Artist artist { get; set; } = null!;
        public Venue venue { get; set; } = null!;
        public Tour tour { get; set; } = null!;
        public Sets sets { get; set; } = null!;
        public string info { get; set; } = null!;
        public string url { get; set; } = null!;
        public int? lastFmEventId { get; set; }
    }

    public class Tour
    {
        public string name { get; set; } = null!;
    }

    public class SetlistsRootObject
    {
        public string type { get; set; } = null!;
        public int itemsPerPage { get; set; }
        public int page { get; set; }
        public int total { get; set; }
        public List<Setlist> setlist { get; set; } = null!;
    }
}
