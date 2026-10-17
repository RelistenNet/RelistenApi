using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;
using Dapper;
using Relisten.Api.Models;

namespace Relisten.Data
{
    public class EntityOneToManyMapper<TP, TC, TPk>
        where TPk : notnull
    {
        private readonly IDictionary<TPk, TP> _lookup = new Dictionary<TPk, TP>();

        public Action<TP, TC> AddChildAction { get; set; } = null!;

        public Func<TP, TPk> ParentKey { get; set; } = null!;

        public virtual TP Map(TP parent, TC child)
        {
            var found = true;
            var primaryKey = ParentKey(parent);

            if (!_lookup.TryGetValue(primaryKey, out var entity))
            {
                _lookup.Add(primaryKey, parent);
                entity = parent;
                found = false;
            }

            AddChildAction(entity, child);

            return !found ? entity : default!;
        }
    }

    public class SourceService : RelistenDataServiceBase
    {
        public SourceService(DbService db, SourceSetService sourceSetService)
            : base(db)
        {
            _sourceSetService = sourceSetService;
        }

        private SourceSetService _sourceSetService { get; }

        /// <summary>Reads the current recording inside an import transaction, avoiding replica lag.</summary>
        public Task<Source?> ForUpstreamIdentifierFromPrimary(Artist artist, string identifier) =>
            db.WithWriteConnection(connection =>
                connection.QuerySingleOrDefaultAsync<Source>(
                    "SELECT * FROM sources WHERE artist_id=@artistId AND upstream_identifier=@identifier",
                    new { artistId = artist.id, identifier }
                )
            );

        /// <summary>
        /// Locks the intake item while an ordinary Archive import checks ownership.
        /// Cohort assignment owns the item before first publication; retained published
        /// references keep that ownership after approval pauses or upstream metadata changes.
        /// Call inside the catalog transaction before changing any recording rows.
        /// </summary>
        public Task<bool> IsArchiveItemIntakeOwnedForUpdate(string identifier) =>
            db.WithWriteConnection(async connection =>
            {
                await connection.QueryAsync<string>(
                    "SELECT item_identifier FROM normalization.archive_item_intake WHERE item_identifier=@identifier FOR UPDATE",
                    new { identifier }
                );
                return await connection.QuerySingleAsync<bool>(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM normalization.archive_item_intake i
                        WHERE i.item_identifier=@identifier
                        AND (i.published_source_uuid IS NOT NULL OR EXISTS (
                            SELECT 1 FROM normalization.archive_artist_candidates c
                            WHERE c.candidate_id=i.candidate_id AND c.artist_uuid IS NOT NULL
                        ))
                    )
                    """,
                    new { identifier }
                );
            });

        // Use the same ownership predicate for candidate selection and final deletion.
        // Source UUIDs survive administrative changes to the item's current candidate.
        private const string WithoutArchiveIntakeOwnership = """
            NOT EXISTS (
                SELECT 1 FROM normalization.archive_item_intake i
                WHERE (i.item_identifier=s.upstream_identifier OR i.published_source_uuid=s.uuid)
                AND (i.published_source_uuid IS NOT NULL OR EXISTS (
                    SELECT 1 FROM normalization.archive_artist_candidates c
                    WHERE c.candidate_id=i.candidate_id AND c.artist_uuid IS NOT NULL
                ))
            )
            """;

        /// <summary>
        /// Reads ordinary Archive deletion candidates, excluding recordings owned by intake
        /// before applying the five-recording deletion guard or fetching deletion evidence.
        /// </summary>
        public Task<IEnumerable<Source>> ArchiveDeletionCandidatesFromPrimary(Artist artist) =>
            db.WithWriteConnection(connection =>
                connection.QueryAsync<Source>(
                    $"SELECT s.* FROM sources s WHERE s.artist_id=@id AND {WithoutArchiveIntakeOwnership}",
                    new { artist.id }
                )
            );

        /// <summary>
        /// Deletes only fixed Archive decisions that intake still does not own. Recheck at
        /// the mutation boundary because ownership can change after upstream verification.
        /// Other providers retain their existing removal contract.
        /// </summary>
        public async Task<int> RemoveUnownedArchiveSourcesWithUpstreamIdentifiers(
            int artistId,
            IEnumerable<string> upstreamIdentifiers
        )
        {
            var identifiers = upstreamIdentifiers.ToArray();
            using var scope = new TransactionScope(
                TransactionScopeOption.Required,
                new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
                TransactionScopeAsyncFlowOption.Enabled
            );
            var removed = await db.WithWriteConnection(async connection =>
            {
                // Intake publication locks this same item row before touching sources.
                // Recheck ownership after any concurrent publication has committed.
                await connection.QueryAsync<string>(
                    """
                    SELECT i.item_identifier FROM normalization.archive_item_intake i
                    WHERE i.item_identifier=ANY(@identifiers) OR i.published_source_uuid IN (
                        SELECT uuid FROM sources WHERE artist_id=@artistId AND upstream_identifier=ANY(@identifiers)
                    )
                    ORDER BY i.item_identifier FOR UPDATE
                    """,
                    new { identifiers, artistId }
                );
                return await connection.ExecuteAsync(
                    $"""
                    WITH source_ids AS MATERIALIZED (
                        SELECT s.id FROM sources s
                        WHERE s.artist_id=@artistId AND s.upstream_identifier=ANY(@identifiers)
                        AND {WithoutArchiveIntakeOwnership}
                    ), deleted_review_counts AS (
                        DELETE FROM source_review_counts WHERE source_id IN (SELECT id FROM source_ids)
                    )
                    DELETE FROM sources WHERE id IN (SELECT id FROM source_ids)
                    """,
                    new { artistId, identifiers }
                );
            });
            scope.Complete();
            return removed;
        }

        /// <summary>Loads the selected recording from the primary, scoped to its artist.</summary>
        public Task<Source> ForArtistFromPrimary(Artist artist, long sourceId) =>
            db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<Source>(
                    "SELECT * FROM sources WHERE artist_id=@artistId AND id=@sourceId",
                    new { artistId = artist.id, sourceId }
                )
            );

        /// <summary>
        /// Reads and locks one recording for compound updates to its reviews and metadata.
        /// Call inside a transaction; PostgreSQL releases the row lock when it finishes.
        /// </summary>
        public Task<Source> ForArtistAndIdForUpdate(Artist artist, long sourceId) =>
            db.WithWriteConnection(connection =>
                connection.QuerySingleAsync<Source>(
                    "SELECT * FROM sources WHERE artist_id=@artistId AND id=@sourceId FOR UPDATE",
                    new { artistId = artist.id, sourceId }
                )
            );

        public async Task<Source?> ForUpstreamIdentifier(Artist artist, string upstreamId)
        {
            return await db.WithConnection(con =>
                con.QueryFirstOrDefaultAsync<Source>(
                    @"
                SELECT
                    src.*
                FROM
                    sources src
                WHERE
                    src.artist_id = @artistId
                    AND src.upstream_identifier = @upstreamId
            ",
                    new { artistId = artist.id, upstreamId }
                )
            );
        }

        public Task<IEnumerable<SourceFull>> FullSourcesForShow(Artist artist, Show show)
        {
            return FullSourcesGeneric(
                "s.artist_id = @artistId AND s.show_id = @showId",
                new { showId = show.id, artistId = artist.id }
            );
        }

        public async Task<IEnumerable<SourceFull>> FullSourcesGeneric(string where, object param)
        {
            var LinkMapper = new EntityOneToManyMapper<SourceFull, Link, int>
            {
                AddChildAction = (source, link) =>
                {
                    if (source.links == null)
                    {
                        source.links = new List<Link>();
                    }

                    if (link != null)
                    {
                        source.links.Add(link);
                    }
                },
                ParentKey = source => source.id,
            };

            var TrackMapper = new EntityOneToManyMapper<SourceSet, SourceTrack, int>
            {
                AddChildAction = (set, track) =>
                {
                    if (set.tracks == null)
                    {
                        set.tracks = new List<SourceTrack>();
                    }

                    if (track != null)
                    {
                        set.tracks.Add(track);
                    }
                },
                ParentKey = set => set.id,
            };

            return await db.WithConnection(async con =>
            {
                var t_srcsWithReviews = await con.QueryAsync<SourceFull, Link, SourceFull>(
                    $@"
	                SELECT
	                    s.*
						, a.uuid as artist_uuid
						, v.uuid as venue_uuid
                        , sh.uuid as show_uuid
						, COALESCE(review_counts.source_review_count, 0) as review_count
						, l.*
	                FROM
	                    sources s
						JOIN artists a ON a.id = s.artist_id
						LEFT JOIN venues v ON v.id = s.venue_id
						LEFT JOIN shows sh ON sh.id = s.show_id
						LEFT JOIN links l ON l.source_id = s.id

						LEFT JOIN source_review_counts review_counts ON review_counts.source_id = s.id
	                WHERE
	                    {where}
                    ORDER BY
                        s.avg_rating_weighted DESC
	            ",
                    LinkMapper.Map,
                    param
                );

                var t_setsWithTracks = await con.QueryAsync<SourceSet, SourceTrack, SourceSet>(
                    $@"
	                SELECT
	                    sets.*, a.uuid as artist_uuid, s.uuid as source_uuid
                        , t.*, s.uuid as source_uuid, sets.uuid as source_set_uuid, a.uuid as artist_uuid, sh.uuid as show_uuid
	                FROM
	                    source_sets sets
	                    JOIN sources s ON s.id = sets.source_id
						JOIN artists a ON a.id = s.artist_id
	                    JOIN source_tracks t ON t.source_set_id = sets.id
	                    JOIN shows sh ON sh.id = s.show_id
	                WHERE 1=1
	                    AND t.is_orphaned = false
	                    AND {where}
					ORDER BY
						sets.index ASC, t.track_position ASC
	            ",
                    TrackMapper.Map,
                    param
                );

                var setsWithTracks = t_setsWithTracks
                    .Where(s => s != null)
                    .GroupBy(s => s.source_id)
                    .ToDictionary(grp => grp.Key, grp => grp.AsList());

                var sources = t_srcsWithReviews.Where(s => s != null).ToList();

                foreach (var src in sources)
                {
                    src.sets = setsWithTracks[src.id];
                }

                return sources;
            });
        }

        public Task<IEnumerable<SourceReview>> ReviewsForSource(
            int? sourceId,
            Guid? sourceUuid = null
        )
        {
            return db.WithConnection(conn =>
                conn.QueryAsync<SourceReview>(
                    @"
				SELECT
					r.*
				FROM
					source_reviews r
				    JOIN sources s on s.id = r.source_id
				WHERE
					(s.id = @sourceId OR s.uuid = @sourceUuid)
				ORDER BY
					r.updated_at DESC
			",
                    new { sourceId, sourceUuid }
                )
            );
        }

        public Task<
            IEnumerable<SlimSourceWithShowVenueAndArtist>
        > SlimSourceWithShowAndArtistForIds(IList<int> ids)
        {
            return db.WithConnection(con =>
                con.QueryAsync<
                    SlimSourceWithShowVenueAndArtist,
                    SlimArtistWithFeatures,
                    Features,
                    Show,
                    VenueWithShowCount,
                    Venue,
                    SlimSourceWithShowVenueAndArtist
                >(
                    @"
				SELECT
					s.*, a.*, f.*, sh.*, shVenue.*, shVenue_counts.shows_at_venue, sVenue.*
				FROM
					sources s
					JOIN artists a ON s.artist_id = a.id
					JOIN features f ON f.artist_id = a.id
					JOIN shows sh ON sh.id = s.show_id
                    LEFT JOIN venues shVenue ON shVenue.id = sh.venue_id
                    LEFT JOIN venues sVenue ON sVenue.id = sh.venue_id
                    LEFT JOIN venue_show_counts shVenue_counts ON shVenue_counts.id = sh.venue_id
				WHERE
					s.id = ANY(@ids)
			",
                    (s, a, f, sh, shVenue, sVenue) =>
                    {
                        sh.venue = shVenue;
                        s.venue = sVenue;

                        a.features = f;

                        s.artist = a;
                        s.show = sh;

                        return s;
                    },
                    new { ids }
                )
            );
        }

        public async Task<IEnumerable<Source>> AllForArtist(Artist artist)
        {
            return await db.WithConnection(con =>
                con.QueryAsync<Source>(
                    @"
                SELECT
                    s.*
                FROM
                    sources s
                WHERE
                    s.artist_id = @id
            ",
                    new { artist.id }
                )
            );
        }

        /// <summary>
        /// Same as AllForArtist but reads from the primary database to ensure read-after-write consistency.
        /// Use this during imports after content has been deleted to avoid read replica lag.
        /// </summary>
        public async Task<IEnumerable<Source>> AllForArtistFromPrimary(Artist artist)
        {
            return await db.WithWriteConnection(con =>
                con.QueryAsync<Source>(
                    @"
                SELECT
                    s.*
                FROM
                    sources s
                WHERE
                    s.artist_id = @id
            ",
                    new { artist.id }
                )
            );
        }

        /// <summary>
        /// Same as AllSourceReviewInformationForArtist but reads from the primary database.
        /// </summary>
        public async Task<
            IEnumerable<SourceReviewInformation>
        > AllSourceReviewInformationForArtistFromPrimary(Artist artist)
        {
            return await db.WithWriteConnection(con =>
                con.QueryAsync<SourceReviewInformation>(
                    @"
                SELECT
                    r.source_id
                    , s.upstream_identifier
                    , r.source_review_max_updated_at as review_max_updated_at
                    , r.source_review_count as review_count
                FROM
                    sources s
                    JOIN source_review_counts r ON s.id = r.source_id
                WHERE
                    s.artist_id = @id
            ",
                    new { artist.id }
                )
            );
        }

        public async Task<IEnumerable<SourceReviewInformation>> AllSourceReviewInformationForArtist(
            Artist artist
        )
        {
            return await db.WithConnection(con =>
                con.QueryAsync<SourceReviewInformation>(
                    @"
                SELECT
                    r.source_id
                    , s.upstream_identifier
                    , r.source_review_max_updated_at as review_max_updated_at
                    , r.source_review_count as review_count
                FROM
                    sources s
                    JOIN source_review_counts r ON s.id = r.source_id
                WHERE
                    s.artist_id = @id
            ",
                    new { artist.id }
                )
            );
        }

        public async Task<int> RemoveSourcesWithUpstreamIdentifiers(
            int artistId,
            IEnumerable<string> upstreamIdentifiers
        )
        {
            return await db.WithWriteConnection(con =>
                con.ExecuteAsync(
                    @"
                WITH source_ids AS MATERIALIZED (
                    SELECT id
                    FROM sources
                    WHERE upstream_identifier = ANY(@upstreamIdentifiers)
                        AND artist_id = @artistId
                ), deleted_review_counts AS (
                    DELETE FROM source_review_counts
                    WHERE source_id IN (SELECT id FROM source_ids)
                )
                DELETE FROM sources
                WHERE id IN (SELECT id FROM source_ids)
            ",
                    new { artistId, upstreamIdentifiers = upstreamIdentifiers.ToList() }
                )
            );
        }

        public async Task<Source> Save(Source source)
        {
            var p = new
            {
                source.id,
                source.artist_id,
                source.show_id,
                source.is_soundboard,
                source.is_remaster,
                source.avg_rating,
                source.num_reviews,
                source.upstream_identifier,
                source.has_jamcharts,
                source.description,
                source.taper_notes,
                source.source,
                source.taper,
                source.transferrer,
                source.lineage,
                source.updated_at,
                source.display_date,
                source.venue_id,
                source.num_ratings,
                source.flac_type,
            };

            return await db.WithWriteConnection(con =>
                con.QuerySingleAsync<Source>(
                    @"
                INSERT INTO
                    sources
                    (
                        artist_id,
                        show_id,
                        is_soundboard,
                        is_remaster,
                        avg_rating,
                        num_reviews,
                        upstream_identifier,
                        has_jamcharts,
                        description,
                        taper_notes,
                        source,
                        taper,
                        transferrer,
                        lineage,
                        updated_at,
                        display_date,
                        venue_id,
                        num_ratings,
                        flac_type,
                        uuid
                    )
                VALUES
                    (
                        @artist_id,
                        @show_id,
                        @is_soundboard,
                        @is_remaster,
                        @avg_rating,
                        @num_reviews,
                        @upstream_identifier,
                        @has_jamcharts,
                        @description,
                        @taper_notes,
                        @source,
                        @taper,
                        @transferrer,
                        @lineage,
                        @updated_at,
                        @display_date,
                        @venue_id,
                        @num_ratings,
                        @flac_type,
                        md5(@artist_id || '::source::' || @upstream_identifier)::uuid
                    )
                ON CONFLICT ON CONSTRAINT sources_uuid_key
                DO
                    UPDATE SET
                        artist_id = EXCLUDED.artist_id,
                        show_id = EXCLUDED.show_id,
                        is_soundboard = EXCLUDED.is_soundboard,
                        is_remaster = EXCLUDED.is_remaster,
                        avg_rating = EXCLUDED.avg_rating,
                        num_reviews = EXCLUDED.num_reviews,
                        upstream_identifier = EXCLUDED.upstream_identifier,
                        has_jamcharts = EXCLUDED.has_jamcharts,
                        description = EXCLUDED.description,
                        taper_notes = EXCLUDED.taper_notes,
                        source = EXCLUDED.source,
                        taper = EXCLUDED.taper,
                        transferrer = EXCLUDED.transferrer,
                        lineage = EXCLUDED.lineage,
                        updated_at = EXCLUDED.updated_at,
                        display_date = EXCLUDED.display_date,
                        venue_id = EXCLUDED.venue_id,
                        num_ratings = EXCLUDED.num_ratings,
                        flac_type = EXCLUDED.flac_type

                RETURNING *
            ",
                    p
                )
            );
        }
    }
}
