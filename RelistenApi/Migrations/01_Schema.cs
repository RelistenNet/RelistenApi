using SimpleMigrations;

namespace Migrations
{
    [Migration(1, "Create initial schema")]
    public class CreateUsers : Migration
    {
        protected override void Up()
        {
            // generated with:
            // docker exec -i 1a9d276c0d02 sh -c "pg_dump -U relisten --no-owner -n public -s relisten_db" > schema.sql
            Execute(@"
CREATE TABLE artists (
    id integer NOT NULL,
    musicbrainz_id text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    name text NOT NULL,
    featured integer DEFAULT 0 NOT NULL,
    slug text NOT NULL,
    sort_name text,
    uuid uuid NOT NULL
);

CREATE SEQUENCE artists_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE artists_id_seq OWNED BY artists.id;

CREATE TABLE artists_upstream_sources (
    upstream_source_id integer NOT NULL,
    artist_id integer NOT NULL,
    upstream_identifier text
);

CREATE TABLE eras (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    ""order"" smallint DEFAULT (0)::smallint,
    name text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE eras_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE eras_id_seq OWNED BY eras.id;

CREATE TABLE features (
    id integer NOT NULL,
    descriptions boolean DEFAULT false NOT NULL,
    eras boolean DEFAULT false NOT NULL,
    multiple_sources boolean DEFAULT false NOT NULL,
    reviews boolean DEFAULT false NOT NULL,
    ratings boolean DEFAULT false NOT NULL,
    tours boolean DEFAULT false NOT NULL,
    taper_notes boolean DEFAULT false NOT NULL,
    source_information boolean DEFAULT false NOT NULL,
    sets boolean DEFAULT false NOT NULL,
    per_show_venues boolean DEFAULT false NOT NULL,
    per_source_venues boolean DEFAULT false NOT NULL,
    venue_coords boolean DEFAULT false NOT NULL,
    songs boolean DEFAULT false NOT NULL,
    years boolean DEFAULT true NOT NULL,
    track_md5s boolean DEFAULT false NOT NULL,
    review_titles boolean DEFAULT false NOT NULL,
    jam_charts boolean DEFAULT false NOT NULL,
    setlist_data_incomplete boolean DEFAULT false NOT NULL,
    artist_id integer NOT NULL,
    track_names boolean DEFAULT false NOT NULL,
    venue_past_names boolean DEFAULT false NOT NULL,
    reviews_have_ratings boolean DEFAULT false NOT NULL,
    track_durations boolean DEFAULT true NOT NULL,
    can_have_flac boolean DEFAULT false NOT NULL
);

CREATE SEQUENCE featuresets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE featuresets_id_seq OWNED BY features.id;

CREATE TABLE links (
    id bigint NOT NULL,
    source_id integer NOT NULL,
    upstream_source_id integer NOT NULL,
    for_reviews boolean NOT NULL,
    for_ratings boolean NOT NULL,
    for_source boolean NOT NULL,
    url text NOT NULL,
    label text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL
);

CREATE SEQUENCE links_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE links_id_seq OWNED BY links.id;

CREATE TABLE setlist_shows (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    venue_id bigint NOT NULL,
    upstream_identifier text NOT NULL,
    date date NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    tour_id bigint,
    era_id bigint,
    uuid uuid NOT NULL
);

CREATE SEQUENCE setlist_show_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE setlist_show_id_seq OWNED BY setlist_shows.id;

CREATE TABLE setlist_songs (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    name text NOT NULL,
    slug text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    upstream_identifier text NOT NULL,
    uuid uuid NOT NULL
);

CREATE SEQUENCE setlist_song_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE setlist_song_id_seq OWNED BY setlist_songs.id;

CREATE TABLE setlist_songs_plays (
    played_setlist_song_id bigint NOT NULL,
    played_setlist_show_id bigint NOT NULL
);

CREATE TABLE sources (
    id bigint NOT NULL,
    show_id bigint,
    is_soundboard boolean NOT NULL,
    is_remaster boolean NOT NULL,
    avg_rating real DEFAULT (0)::real NOT NULL,
    num_reviews integer DEFAULT 0 NOT NULL,
    upstream_identifier text NOT NULL,
    has_jamcharts boolean NOT NULL,
    description text DEFAULT ''''''::text,
    taper_notes text DEFAULT ''''''::text,
    source text DEFAULT ''''''::text,
    taper text DEFAULT ''''''::text,
    transferrer text DEFAULT ''''''::text,
    lineage text DEFAULT ''''''::text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    artist_id integer NOT NULL,
    avg_rating_weighted real DEFAULT (0)::real,
    duration real DEFAULT (0)::real,
    venue_id bigint,
    display_date text NOT NULL,
    num_ratings integer,
    flac_type integer DEFAULT 0 NOT NULL,
    uuid uuid NOT NULL
);

CREATE MATERIALIZED VIEW show_source_information AS
 SELECT src.show_id,
    max(src.updated_at) AS max_updated_at,
    count(*) AS source_count,
    src.artist_id,
    max(src.avg_rating_weighted) AS max_avg_rating_weighted,
    bool_or(src.is_soundboard) AS has_soundboard_source,
    bool_or(
        CASE
            WHEN ((src.flac_type = 1) OR (src.flac_type = 2)) THEN true
            ELSE false
        END) AS has_flac
   FROM sources src
  GROUP BY src.show_id, src.artist_id
  WITH NO DATA;

CREATE TABLE shows (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    tour_id bigint,
    year_id bigint,
    era_id bigint,
    date date NOT NULL,
    avg_rating real DEFAULT (0)::real,
    avg_duration real DEFAULT (0)::real,
    display_date text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    venue_id integer,
    uuid uuid NOT NULL
);

CREATE SEQUENCE shows_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE shows_id_seq OWNED BY shows.id;

CREATE TABLE source_reviews (
    id bigint NOT NULL,
    source_id bigint NOT NULL,
    rating smallint,
    title text,
    review text DEFAULT ''::text NOT NULL,
    author text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL
);

COMMENT ON COLUMN source_reviews.rating IS 'Scale of 1 to 10';

CREATE MATERIALIZED VIEW source_review_counts AS
 SELECT r.source_id,
    max(r.updated_at) AS source_review_max_updated_at,
    count(r.id) AS source_review_count
   FROM source_reviews r
  GROUP BY r.source_id
  WITH NO DATA;

CREATE SEQUENCE source_review_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE source_review_id_seq OWNED BY source_reviews.id;

CREATE TABLE source_sets (
    id bigint NOT NULL,
    source_id bigint NOT NULL,
    index integer DEFAULT 0 NOT NULL,
    is_encore boolean DEFAULT false NOT NULL,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE source_sets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE source_sets_id_seq OWNED BY source_sets.id;

CREATE TABLE source_tracks_plays (
    id bigint NOT NULL,
    source_track_id bigint NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    user_id bigint NOT NULL
);

CREATE SEQUENCE source_track_play_history_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE source_track_play_history_id_seq OWNED BY source_tracks_plays.id;

CREATE TABLE source_tracks (
    id bigint NOT NULL,
    source_id bigint NOT NULL,
    source_set_id bigint NOT NULL,
    track_position integer NOT NULL,
    duration integer,
    title text NOT NULL,
    slug text NOT NULL,
    mp3_url text,
    mp3_md5 text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    flac_md5 text,
    flac_url text,
    artist_id integer,
    uuid uuid NOT NULL,
    CONSTRAINT url_check CHECK (((mp3_url IS NOT NULL) OR (flac_url IS NOT NULL)))
);

COMMENT ON COLUMN source_tracks.duration IS 'Duration in seconds';

CREATE SEQUENCE source_tracks_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE source_tracks_id_seq OWNED BY source_tracks.id;

CREATE SEQUENCE sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE sources_id_seq OWNED BY sources.id;

CREATE TABLE tours (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    start_date date,
    end_date date,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    slug text NOT NULL,
    upstream_identifier text NOT NULL,
    uuid uuid NOT NULL
);

CREATE SEQUENCE tours_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE tours_id_seq OWNED BY tours.id;

CREATE TABLE upstream_sources (
    id integer NOT NULL,
    name text NOT NULL,
    url text,
    description text,
    credit_line text
);

CREATE SEQUENCE upstream_sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE upstream_sources_id_seq OWNED BY upstream_sources.id;

CREATE TABLE venues (
    id bigint NOT NULL,
    artist_id integer,
    latitude double precision,
    longitude double precision,
    name text,
    location text,
    upstream_identifier text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    slug text,
    past_names text,
    uuid uuid NOT NULL
);

CREATE MATERIALIZED VIEW venue_show_counts AS
 SELECT v.id,
        CASE
            WHEN (count(DISTINCT src.show_id) = 0) THEN count(s.id)
            ELSE count(DISTINCT src.show_id)
        END AS shows_at_venue
   FROM ((shows s
     JOIN venues v ON ((v.id = s.venue_id)))
     LEFT JOIN sources src ON ((src.venue_id = v.id)))
  GROUP BY v.id
  WITH NO DATA;

CREATE SEQUENCE venues_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE venues_id_seq OWNED BY venues.id;

CREATE TABLE years (
    id bigint NOT NULL,
    show_count integer DEFAULT 0,
    source_count integer DEFAULT 0,
    duration integer DEFAULT 0,
    avg_duration real DEFAULT (0)::real,
    avg_rating real DEFAULT (0)::real,
    artist_id integer NOT NULL,
    year text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    uuid uuid NOT NULL
);

CREATE SEQUENCE years_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE years_id_seq OWNED BY years.id;

ALTER TABLE ONLY artists ALTER COLUMN id SET DEFAULT nextval('artists_id_seq'::regclass);

ALTER TABLE ONLY eras ALTER COLUMN id SET DEFAULT nextval('eras_id_seq'::regclass);

ALTER TABLE ONLY features ALTER COLUMN id SET DEFAULT nextval('featuresets_id_seq'::regclass);

ALTER TABLE ONLY links ALTER COLUMN id SET DEFAULT nextval('links_id_seq'::regclass);

ALTER TABLE ONLY setlist_shows ALTER COLUMN id SET DEFAULT nextval('setlist_show_id_seq'::regclass);

ALTER TABLE ONLY setlist_songs ALTER COLUMN id SET DEFAULT nextval('setlist_song_id_seq'::regclass);

ALTER TABLE ONLY shows ALTER COLUMN id SET DEFAULT nextval('shows_id_seq'::regclass);

ALTER TABLE ONLY source_reviews ALTER COLUMN id SET DEFAULT nextval('source_review_id_seq'::regclass);

ALTER TABLE ONLY source_sets ALTER COLUMN id SET DEFAULT nextval('source_sets_id_seq'::regclass);

ALTER TABLE ONLY source_tracks ALTER COLUMN id SET DEFAULT nextval('source_tracks_id_seq'::regclass);

ALTER TABLE ONLY source_tracks_plays ALTER COLUMN id SET DEFAULT nextval('source_track_play_history_id_seq'::regclass);

ALTER TABLE ONLY sources ALTER COLUMN id SET DEFAULT nextval('sources_id_seq'::regclass);

ALTER TABLE ONLY tours ALTER COLUMN id SET DEFAULT nextval('tours_id_seq'::regclass);

ALTER TABLE ONLY upstream_sources ALTER COLUMN id SET DEFAULT nextval('upstream_sources_id_seq'::regclass);

ALTER TABLE ONLY venues ALTER COLUMN id SET DEFAULT nextval('venues_id_seq'::regclass);

ALTER TABLE ONLY years ALTER COLUMN id SET DEFAULT nextval('years_id_seq'::regclass);

ALTER TABLE ONLY artists
    ADD CONSTRAINT artists_pkey PRIMARY KEY (id);

ALTER TABLE ONLY artists
    ADD CONSTRAINT artists_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY eras
    ADD CONSTRAINT eras_pkey PRIMARY KEY (id);

ALTER TABLE ONLY features
    ADD CONSTRAINT featuresets_pkey PRIMARY KEY (id);

ALTER TABLE ONLY links
    ADD CONSTRAINT links_pkey PRIMARY KEY (id);

ALTER TABLE ONLY links
    ADD CONSTRAINT links_source_id_upstream_source_id_key UNIQUE (source_id, upstream_source_id);

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_pkey PRIMARY KEY (id);

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_upstream_identifier_key UNIQUE (upstream_identifier);

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_song_pkey PRIMARY KEY (id);

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_songs_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_pkey PRIMARY KEY (id);

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_review_pkey PRIMARY KEY (id);

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_pkey PRIMARY KEY (id);

ALTER TABLE ONLY source_tracks_plays
    ADD CONSTRAINT source_track_play_history_pkey PRIMARY KEY (id);

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_pkey PRIMARY KEY (id);

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_source_id_slug_key UNIQUE (source_id, slug);

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_pkey PRIMARY KEY (id);

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_pkey PRIMARY KEY (id);

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY upstream_sources
    ADD CONSTRAINT upstream_sources_pkey PRIMARY KEY (id);

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_pkey PRIMARY KEY (id);

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_uuid_key UNIQUE (uuid);

ALTER TABLE ONLY years
    ADD CONSTRAINT years_pkey PRIMARY KEY (id);

ALTER TABLE ONLY years
    ADD CONSTRAINT years_uuid_key UNIQUE (uuid);

CREATE UNIQUE INDEX artists_upstream_sources_key ON artists_upstream_sources USING btree (upstream_source_id, artist_id);

CREATE INDEX idx_features_artist ON features USING btree (artist_id);

CREATE INDEX idx_shows_artist_id_display_date ON shows USING btree (artist_id, display_date);

CREATE INDEX idx_source_reviews_source_id ON source_reviews USING btree (source_id);

CREATE INDEX idx_source_sets_source_id ON source_sets USING btree (source_id);

CREATE INDEX idx_source_show_id_covering ON sources USING btree (show_id, avg_rating_weighted, flac_type, is_soundboard, updated_at);

CREATE INDEX idx_source_tracks_source_set_id ON source_tracks USING btree (source_set_id);

CREATE INDEX idx_source_venue_id ON sources USING btree (venue_id, show_id);

CREATE INDEX idx_sources_flac_upstream ON sources USING btree (upstream_identifier, flac_type);

CREATE UNIQUE INDEX idx_years_year ON years USING btree (artist_id, year);

CREATE UNIQUE INDEX setlist_songs_artist_id_slug ON setlist_songs USING btree (artist_id, slug);

CREATE UNIQUE INDEX setlist_songs_artist_id_upstream_identifier ON setlist_songs USING btree (artist_id, upstream_identifier);

CREATE INDEX setlist_songs_plays_played_setlist_show_id_idx ON setlist_songs_plays USING btree (played_setlist_show_id);

CREATE UNIQUE INDEX shows_artist_id_display_date_key ON shows USING btree (artist_id, display_date);

CREATE UNIQUE INDEX source_uniq_artist_upstream ON sources USING btree (artist_id, upstream_identifier);

CREATE UNIQUE INDEX tour_artist_id_tour_upstream_identifier ON tours USING btree (artist_id, upstream_identifier);

CREATE UNIQUE INDEX unique_artist_id_mp3_url ON source_tracks USING btree (artist_id, mp3_url);

CREATE INDEX venues_artist ON venues USING btree (artist_id);

CREATE UNIQUE INDEX venues_upstream_identifier_key ON venues USING btree (artist_id, upstream_identifier) WHERE (artist_id IS NOT NULL);

CREATE UNIQUE INDEX venues_upstream_identifier_null_artist_key ON venues USING btree (upstream_identifier) WHERE (artist_id IS NULL);

ALTER TABLE ONLY artists_upstream_sources
    ADD CONSTRAINT artists_upstream_sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON DELETE CASCADE;

ALTER TABLE ONLY artists_upstream_sources
    ADD CONSTRAINT artists_upstream_sources_upstream_source_id_fkey FOREIGN KEY (upstream_source_id) REFERENCES upstream_sources(id) ON DELETE RESTRICT;

ALTER TABLE ONLY eras
    ADD CONSTRAINT eras_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY features
    ADD CONSTRAINT featuresets_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY links
    ADD CONSTRAINT links_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY links
    ADD CONSTRAINT links_upstream_source_id_fkey FOREIGN KEY (upstream_source_id) REFERENCES upstream_sources(id);

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE;

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES eras(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES tours(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_song_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_show_id_fkey FOREIGN KEY (played_setlist_show_id) REFERENCES setlist_shows(id) ON UPDATE RESTRICT ON DELETE RESTRICT;

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_song_id_fkey FOREIGN KEY (played_setlist_song_id) REFERENCES setlist_songs(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES eras(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES tours(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_year_id_fkey FOREIGN KEY (year_id) REFERENCES years(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_review_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON DELETE CASCADE;

ALTER TABLE ONLY source_tracks_plays
    ADD CONSTRAINT source_tracks_plays_source_track_id_fkey FOREIGN KEY (source_track_id) REFERENCES source_tracks(id);

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_set_id_fkey FOREIGN KEY (source_set_id) REFERENCES source_sets(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_show_id_fkey FOREIGN KEY (show_id) REFERENCES shows(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE ON DELETE SET NULL;

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;

ALTER TABLE ONLY years
    ADD CONSTRAINT years_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;            
            ");
        }

        protected override void Down()
        {
            Execute(@"
DO $$ DECLARE
    r RECORD;
BEGIN
    FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = current_schema()) LOOP
        EXECUTE 'DROP TABLE IF EXISTS ' || quote_ident(r.tablename) || ' CASCADE';
    END LOOP;
END $$;            
            ");
        }
    }
}