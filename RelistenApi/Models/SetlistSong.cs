using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Relisten.Api.Models.Api;

namespace Relisten.Api.Models
{
    public class SetlistSong : BaseRelistenModel, IHasPersistentIdentifier
    {
        [V2JsonOnly] [Required] public int artist_id { get; set; }
        [Required] public Guid artist_uuid { get; set; }
        [Required] public string name { get; set; }
        [Required] public string slug { get; set; }
        [Required] public string upstream_identifier { get; set; }

        [Required]
        public string sortName
        {
            get
            {
                if (name.StartsWith("The ", StringComparison.CurrentCultureIgnoreCase))
                {
                    return name.Substring(4) + ", The";
                }

                return name;
            }
        }

        [Required] public Guid uuid { get; set; }
    }

    public class SetlistSongWithPlayCount : SetlistSong
    {
        [Required] public int shows_played_at { get; set; }
    }

    public class SetlistSongWithShows : SetlistSong
    {
        [Required] public IList<Show> shows { get; set; }
    }

    public class SetlistShowSongJoin
    {
        [Required] public int played_setlist_song_id { get; set; }

        [Required] public int played_setlist_show_id { get; set; }
    }
}
