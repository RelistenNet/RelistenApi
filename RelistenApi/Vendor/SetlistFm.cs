
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Relisten.Vendor.SetlistFm
{

    public class Artist
    {
        public string mbid { get; set; }
        public int tmid { get; set; }
        public string name { get; set; }
        public string sortName { get; set; }
        public string disambiguation { get; set; }
        public string url { get; set; }
    }

    public class Coords
    {
        public double lat { get; set; }
        public double @long { get; set; }
    }

    public class Country
    {
        public string code { get; set; }
        public string name { get; set; }
    }

    public class City
    {
        public string id { get; set; }
        public string name { get; set; }
        public string state { get; set; }
        public string stateCode { get; set; }
        public Coords coords { get; set; }
        public Country country { get; set; }
    }

    public class Venue
    {
        public string id { get; set; }
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
        public string name { get; set; }
//        public string info { get; set; }
//        public Cover cover { get; set; }
//        public With with { get; set; }
    }

    public class Set
    {
        public string name { get; set; }
        public List<Song> song { get; set; }
        public int? encore { get; set; }
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
        public string id { get; set; }
        public string versionId { get; set; }
        public string eventDate { get; set; }
        public DateTime lastUpdated { get; set; }
        public Artist artist { get; set; }
        public Venue venue { get; set; }
        public Tour tour { get; set; }
        public Sets sets { get; set; }
        public string info { get; set; }
        public string url { get; set; }
        public int? lastFmEventId { get; set; }
    }
    
    public class Tour
    {
        public string name { get; set; }
    }

    public class SetlistsRootObject
    {
        public string type { get; set; }
        public int itemsPerPage { get; set; }
        public int page { get; set; }
        public int total { get; set; }
        public List<Setlist> setlist { get; set; }
    }

}