--
-- PostgreSQL database dump
--

-- Dumped from database version 9.4.10
-- Dumped by pg_dump version 9.5.3

SET statement_timeout = 0;
SET lock_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SET check_function_bodies = false;
SET client_min_messages = warning;

--
-- Name: plpgsql; Type: EXTENSION; Schema: -; Owner: -
--

CREATE EXTENSION IF NOT EXISTS plpgsql WITH SCHEMA pg_catalog;


--
-- Name: EXTENSION plpgsql; Type: COMMENT; Schema: -; Owner: -
--

COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';


SET search_path = public, pg_catalog;

SET default_tablespace = '';

SET default_with_oids = false;

--
-- Name: artists; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE artists (
    id integer NOT NULL,
    musicbrainz_id text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    name text NOT NULL,
    featured integer DEFAULT 0 NOT NULL,
    slug text NOT NULL
);


--
-- Name: artists_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE artists_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: artists_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE artists_id_seq OWNED BY artists.id;


--
-- Name: artists_upstream_sources; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE artists_upstream_sources (
    upstream_source_id integer NOT NULL,
    artist_id integer NOT NULL,
    upstream_identifier text
);


--
-- Name: eras; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE eras (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    "order" smallint DEFAULT (0)::smallint,
    name text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


--
-- Name: eras_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE eras_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: eras_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE eras_id_seq OWNED BY eras.id;


--
-- Name: features; Type: TABLE; Schema: public; Owner: -
--

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
    track_durations boolean DEFAULT true NOT NULL
);


--
-- Name: featuresets_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE featuresets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: featuresets_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE featuresets_id_seq OWNED BY features.id;


--
-- Name: setlist_shows; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE setlist_shows (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    venue_id integer NOT NULL,
    upstream_identifier text NOT NULL,
    date date NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    tour_id integer,
    era_id integer
);


--
-- Name: setlist_show_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE setlist_show_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: setlist_show_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE setlist_show_id_seq OWNED BY setlist_shows.id;


--
-- Name: setlist_songs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE setlist_songs (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    name text NOT NULL,
    slug text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    upstream_identifier text NOT NULL
);


--
-- Name: setlist_song_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE setlist_song_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: setlist_song_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE setlist_song_id_seq OWNED BY setlist_songs.id;


--
-- Name: setlist_songs_plays; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE setlist_songs_plays (
    played_setlist_song_id integer NOT NULL,
    played_setlist_show_id integer NOT NULL
);


--
-- Name: shows; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE shows (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    tour_id integer,
    year_id integer,
    era_id integer,
    date date NOT NULL,
    avg_rating real DEFAULT (0)::real,
    avg_duration real DEFAULT (0)::real,
    display_date text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    venue_id integer
);


--
-- Name: shows_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE shows_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: shows_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE shows_id_seq OWNED BY shows.id;


--
-- Name: source_reviews; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE source_reviews (
    id integer NOT NULL,
    source_id integer NOT NULL,
    rating smallint,
    title text,
    review text DEFAULT ''::text NOT NULL,
    author text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


--
-- Name: COLUMN source_reviews.rating; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN source_reviews.rating IS 'Scale of 1 to 10';


--
-- Name: source_review_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE source_review_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_review_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE source_review_id_seq OWNED BY source_reviews.id;


--
-- Name: source_sets; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE source_sets (
    id integer NOT NULL,
    source_id integer NOT NULL,
    index integer DEFAULT 0 NOT NULL,
    is_encore boolean DEFAULT false NOT NULL,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


--
-- Name: source_sets_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE source_sets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_sets_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE source_sets_id_seq OWNED BY source_sets.id;


--
-- Name: source_tracks_plays; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE source_tracks_plays (
    id integer NOT NULL,
    source_track_id integer NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    user_id integer NOT NULL
);


--
-- Name: source_track_play_history_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE source_track_play_history_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_track_play_history_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE source_track_play_history_id_seq OWNED BY source_tracks_plays.id;


--
-- Name: source_tracks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE source_tracks (
    id integer NOT NULL,
    source_id integer NOT NULL,
    source_set_id integer NOT NULL,
    track_position integer NOT NULL,
    duration integer,
    title text NOT NULL,
    slug text NOT NULL,
    mp3_url text NOT NULL,
    md5 text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


--
-- Name: COLUMN source_tracks.duration; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN source_tracks.duration IS 'Duration in seconds';


--
-- Name: source_tracks_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE source_tracks_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_tracks_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE source_tracks_id_seq OWNED BY source_tracks.id;


--
-- Name: sources; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE sources (
    id integer NOT NULL,
    show_id integer,
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
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    artist_id integer NOT NULL,
    avg_rating_weighted real DEFAULT (0)::real,
    duration real DEFAULT (0)::real,
    venue_id integer,
    display_date text NOT NULL,
    num_ratings integer
);


--
-- Name: sources_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: sources_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE sources_id_seq OWNED BY sources.id;


--
-- Name: tours; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE tours (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    start_date date,
    end_date date,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    slug text NOT NULL,
    upstream_identifier text NOT NULL
);


--
-- Name: tours_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE tours_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: tours_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE tours_id_seq OWNED BY tours.id;


--
-- Name: upstream_sources; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE upstream_sources (
    id integer NOT NULL,
    name text NOT NULL,
    url text,
    description text,
    credit_line text
);


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE upstream_sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE upstream_sources_id_seq OWNED BY upstream_sources.id;


--
-- Name: venues; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE venues (
    id integer NOT NULL,
    artist_id integer,
    latitude double precision,
    longitude double precision,
    name text,
    location text,
    upstream_identifier text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    slug text,
    past_names text
);


--
-- Name: venues_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE venues_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: venues_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE venues_id_seq OWNED BY venues.id;


--
-- Name: years; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE years (
    id integer NOT NULL,
    show_count integer DEFAULT 0,
    source_count integer DEFAULT 0,
    duration integer DEFAULT 0,
    avg_duration real DEFAULT (0)::real,
    avg_rating real DEFAULT (0)::real,
    artist_id integer NOT NULL,
    year text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


--
-- Name: years_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE years_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: years_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE years_id_seq OWNED BY years.id;


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY artists ALTER COLUMN id SET DEFAULT nextval('artists_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY eras ALTER COLUMN id SET DEFAULT nextval('eras_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY features ALTER COLUMN id SET DEFAULT nextval('featuresets_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows ALTER COLUMN id SET DEFAULT nextval('setlist_show_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_songs ALTER COLUMN id SET DEFAULT nextval('setlist_song_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows ALTER COLUMN id SET DEFAULT nextval('shows_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_reviews ALTER COLUMN id SET DEFAULT nextval('source_review_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_sets ALTER COLUMN id SET DEFAULT nextval('source_sets_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks ALTER COLUMN id SET DEFAULT nextval('source_tracks_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks_plays ALTER COLUMN id SET DEFAULT nextval('source_track_play_history_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY sources ALTER COLUMN id SET DEFAULT nextval('sources_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY tours ALTER COLUMN id SET DEFAULT nextval('tours_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY upstream_sources ALTER COLUMN id SET DEFAULT nextval('upstream_sources_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY venues ALTER COLUMN id SET DEFAULT nextval('venues_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY years ALTER COLUMN id SET DEFAULT nextval('years_id_seq'::regclass);


--
-- Name: artists_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY artists
    ADD CONSTRAINT artists_pkey PRIMARY KEY (id);


--
-- Name: eras_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY eras
    ADD CONSTRAINT eras_pkey PRIMARY KEY (id);


--
-- Name: featuresets_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY features
    ADD CONSTRAINT featuresets_pkey PRIMARY KEY (id);


--
-- Name: setlist_show_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_pkey PRIMARY KEY (id);


--
-- Name: setlist_show_upstream_identifier_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_upstream_identifier_key UNIQUE (upstream_identifier);


--
-- Name: setlist_song_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_song_pkey PRIMARY KEY (id);


--
-- Name: shows_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_pkey PRIMARY KEY (id);


--
-- Name: source_review_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_review_pkey PRIMARY KEY (id);


--
-- Name: source_sets_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_pkey PRIMARY KEY (id);


--
-- Name: source_track_play_history_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks_plays
    ADD CONSTRAINT source_track_play_history_pkey PRIMARY KEY (id);


--
-- Name: source_tracks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_pkey PRIMARY KEY (id);


--
-- Name: sources_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_pkey PRIMARY KEY (id);


--
-- Name: sources_upstream_identifier_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_upstream_identifier_key UNIQUE (upstream_identifier);


--
-- Name: tours_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_pkey PRIMARY KEY (id);


--
-- Name: upstream_sources_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY upstream_sources
    ADD CONSTRAINT upstream_sources_pkey PRIMARY KEY (id);


--
-- Name: venues_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_pkey PRIMARY KEY (id);


--
-- Name: years_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY years
    ADD CONSTRAINT years_pkey PRIMARY KEY (id);


--
-- Name: artists_upstream_sources_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX artists_upstream_sources_key ON artists_upstream_sources USING btree (upstream_source_id, artist_id);


--
-- Name: setlist_songs_artist_id_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX setlist_songs_artist_id_slug ON setlist_songs USING btree (artist_id, slug);


--
-- Name: shows_artist_id_display_date_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX shows_artist_id_display_date_key ON shows USING btree (artist_id, display_date);


--
-- Name: tour_artist_id_tour_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX tour_artist_id_tour_slug ON tours USING btree (artist_id, slug);


--
-- Name: venues_artist; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX venues_artist ON venues USING btree (artist_id);


--
-- Name: venues_upstream_identifier_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX venues_upstream_identifier_key ON venues USING btree (artist_id, upstream_identifier) WHERE (artist_id IS NOT NULL);


--
-- Name: venues_upstream_identifier_null_artist_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX venues_upstream_identifier_null_artist_key ON venues USING btree (upstream_identifier) WHERE (artist_id IS NULL);


--
-- Name: years_year; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX years_year ON years USING btree (artist_id, year);


--
-- Name: artists_upstream_sources_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY artists_upstream_sources
    ADD CONSTRAINT artists_upstream_sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON DELETE CASCADE;


--
-- Name: artists_upstream_sources_upstream_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY artists_upstream_sources
    ADD CONSTRAINT artists_upstream_sources_upstream_source_id_fkey FOREIGN KEY (upstream_source_id) REFERENCES upstream_sources(id) ON DELETE RESTRICT;


--
-- Name: eras_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY eras
    ADD CONSTRAINT eras_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: featuresets_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY features
    ADD CONSTRAINT featuresets_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_show_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_show_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE;


--
-- Name: setlist_shows_era_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES eras(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: setlist_shows_tour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES tours(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: setlist_song_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_song_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_song_plays_played_setlist_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_show_id_fkey FOREIGN KEY (played_setlist_show_id) REFERENCES setlist_shows(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


--
-- Name: setlist_song_plays_played_setlist_song_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_song_id_fkey FOREIGN KEY (played_setlist_song_id) REFERENCES setlist_songs(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: shows_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: shows_era_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES eras(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows_tour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES tours(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows_year_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_year_id_fkey FOREIGN KEY (year_id) REFERENCES years(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: source_review_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_review_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_sets_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_tracks_plays_source_track_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks_plays
    ADD CONSTRAINT source_tracks_plays_source_track_id_fkey FOREIGN KEY (source_track_id) REFERENCES source_tracks(id);


--
-- Name: source_tracks_set_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_set_id_fkey FOREIGN KEY (source_set_id) REFERENCES source_sets(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_tracks_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: sources_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: sources_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_show_id_fkey FOREIGN KEY (show_id) REFERENCES shows(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: sources_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: tours_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: venues_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: years_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY years
    ADD CONSTRAINT years_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--

