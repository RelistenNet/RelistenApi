using System;
using System.Threading.Tasks;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
	public class SearchService : RelistenDataServiceBase
	{
		public SearchService(DbService db) : base(db)
		{

		}

		public async Task<SearchResults> Search(string searchTerm, int? artistId = null)
		{
			return await db.WithConnection(async con =>
			{
				var parms = new { searchTerm, artistId };

				return new SearchResults
				{
					Artists = await con.QueryAsync<SlimArtist>($@"
						SELECT
							*
						FROM
							artists
						WHERE
							name ILIKE '%' || @searchTerm || '%'
							{(artistId.HasValue ? "AND id = @artistId" : "")}
						LIMIT 20;
					", parms),

					Shows = await con.QueryAsync<ShowWithSlimArtist, SlimArtist, ShowWithSlimArtist>($@"
						SELECT
							s.*, a.*
						FROM
							shows s
							JOIN artists a ON s.artist_id = a.id
						WHERE
							s.display_date ILIKE '%' || @searchTerm || '%'
							{(artistId.HasValue ? "AND s.artist_id = @artistId" : "")}
						LIMIT 20;
					", (s, a) => { s.slim_artist = a; return s; }, parms),

					Source = await con.QueryAsync<SourceWithSlimArtist, SlimArtist, SourceWithSlimArtist>($@"
						SELECT
							s.*, a.*
						FROM
							sources s
							JOIN artists a ON s.artist_id = a.id
						WHERE
							(s.upstream_identifier ILIKE '%' || @searchTerm || '%'
							OR s.description ILIKE '%' || @searchTerm || '%'
							OR s.taper_notes ILIKE '%' || @searchTerm || '%'
							OR s.source ILIKE '%' || @searchTerm || '%'
							OR s.taper ILIKE '%' || @searchTerm || '%'
							OR s.transferrer ILIKE '%' || @searchTerm || '%'
							OR s.lineage ILIKE '%' || @searchTerm || '%')
							{(artistId.HasValue ? "AND s.artist_id = @artistId" : "")}
						LIMIT 20;
					", (s, a) => { s.slim_artist = a; return s; }, parms),

					Tours = await con.QueryAsync<TourWithSlimArtist, SlimArtist, TourWithSlimArtist>($@"
						SELECT
							t.*, a.*
						FROM
							tours t
							JOIN artists a ON t.artist_id = a.id
						WHERE
							t.name ILIKE '%' || @searchTerm || '%'
							{(artistId.HasValue ? "AND t.artist_id = @artistId" : "")}
					    LIMIT 20;
					", (t, a) => { t.slim_artist = a; return t; }, parms),

					Venues = await con.QueryAsync<VenueWithSlimArtist, SlimArtist, VenueWithSlimArtist>($@"
						SELECT
							v.*, a.*
						FROM
							venues v
							JOIN artists a ON v.artist_id = a.id
						WHERE
							(v.name ILIKE '%' || @searchTerm || '%'
							OR v.location ILIKE '%' || @searchTerm || '%'
					        OR v.past_names ILIKE '%' || @searchTerm || '%')
							{(artistId.HasValue ? "AND v.artist_id = @artistId" : "")}
						LIMIT 20;
					", (v, a) => { v.slim_artist = a; return v; }, parms)
				};
			});
		}
	}
}
