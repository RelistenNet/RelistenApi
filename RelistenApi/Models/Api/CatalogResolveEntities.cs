using System;

namespace Relisten.Api.Models.Api
{
    public sealed class ResolvedArtist
    {
        public Guid uuid { get; init; }
        public string name { get; init; } = string.Empty;
        public string slug { get; init; } = string.Empty;
    }

    public sealed class ResolvedShow
    {
        public Guid uuid { get; init; }
        public Guid artist_uuid { get; init; }
        public Guid? year_uuid { get; init; }
        public Guid? venue_uuid { get; init; }
        public Guid? tour_uuid { get; init; }
        public DateTime date { get; init; }
        public string display_date { get; init; } = string.Empty;
    }

    public sealed class ResolvedSource
    {
        public Guid uuid { get; init; }
        public Guid artist_uuid { get; init; }
        public Guid? show_uuid { get; init; }
        public Guid? venue_uuid { get; init; }
        public string display_date { get; init; } = string.Empty;
        public bool is_soundboard { get; init; }
        public bool is_remaster { get; init; }
    }

    public sealed class ResolvedSourceTrack
    {
        public Guid uuid { get; init; }
        public Guid source_uuid { get; init; }
        public Guid source_set_uuid { get; init; }
        public Guid artist_uuid { get; init; }
        public Guid? show_uuid { get; init; }
        public int track_position { get; init; }
        public int? duration { get; init; }
        public string title { get; init; } = string.Empty;
        public string? mp3_url { get; init; }
        public string? flac_url { get; init; }
    }

    public sealed class ResolvedSong
    {
        public Guid uuid { get; init; }
        public Guid artist_uuid { get; init; }
        public string name { get; init; } = string.Empty;
        public string slug { get; init; } = string.Empty;
    }

    public sealed class ResolvedTour
    {
        public Guid uuid { get; init; }
        public Guid artist_uuid { get; init; }
        public string name { get; init; } = string.Empty;
        public string slug { get; init; } = string.Empty;
        public DateTime? start_date { get; init; }
        public DateTime? end_date { get; init; }
    }

    public sealed class ResolvedVenue
    {
        public Guid uuid { get; init; }
        public Guid? artist_uuid { get; init; }
        public string name { get; init; } = string.Empty;
        public string location { get; init; } = string.Empty;
        public string slug { get; init; } = string.Empty;
    }
}
