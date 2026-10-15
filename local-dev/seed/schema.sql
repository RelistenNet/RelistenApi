--
-- PostgreSQL database dump
--


-- Dumped from database version 17.10 (Ubuntu 17.10-1.pgdg22.04+1)
-- Dumped by pg_dump version 18.1 (Homebrew)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: artists; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.artists (
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


--
-- Name: shows; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.shows (
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


--
-- Name: source_tracks; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.source_tracks (
    id bigint NOT NULL,
    source_id bigint,
    source_set_id bigint,
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
    is_orphaned boolean DEFAULT false NOT NULL,
    CONSTRAINT url_check CHECK (((mp3_url IS NOT NULL) OR (flac_url IS NOT NULL)))
);


--
-- Name: COLUMN source_tracks.duration; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.source_tracks.duration IS 'Duration in seconds';


--
-- Name: sources; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.sources (
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


--
-- Name: artists_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.artists_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: artists_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.artists_id_seq OWNED BY public.artists.id;


--
-- Name: artists_upstream_sources; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.artists_upstream_sources (
    upstream_source_id integer NOT NULL,
    artist_id integer NOT NULL,
    upstream_identifier text
);


--
-- Name: eras; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.eras (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    "order" smallint DEFAULT (0)::smallint,
    name text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


--
-- Name: eras_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.eras_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: eras_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.eras_id_seq OWNED BY public.eras.id;


--
-- Name: features; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.features (
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


--
-- Name: featuresets_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.featuresets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: featuresets_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.featuresets_id_seq OWNED BY public.features.id;


--
-- Name: links; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.links (
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


--
-- Name: links_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.links_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: links_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.links_id_seq OWNED BY public.links.id;


--
-- Name: setlist_shows; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.setlist_shows (
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


--
-- Name: setlist_show_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.setlist_show_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: setlist_show_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.setlist_show_id_seq OWNED BY public.setlist_shows.id;


--
-- Name: setlist_songs; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.setlist_songs (
    id bigint NOT NULL,
    artist_id integer NOT NULL,
    name text NOT NULL,
    slug text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    upstream_identifier text NOT NULL,
    uuid uuid NOT NULL
);


--
-- Name: setlist_song_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.setlist_song_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: setlist_song_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.setlist_song_id_seq OWNED BY public.setlist_songs.id;


--
-- Name: setlist_songs_plays; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.setlist_songs_plays (
    played_setlist_song_id bigint NOT NULL,
    played_setlist_show_id bigint NOT NULL
);


--
-- Name: show_source_information; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.show_source_information (
    show_id bigint,
    max_updated_at timestamp with time zone,
    source_count bigint,
    artist_id integer,
    max_avg_rating_weighted real,
    has_soundboard_source boolean,
    has_flac boolean,
    max_created_at timestamp with time zone NOT NULL
);


--
-- Name: shows_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.shows_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: shows_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.shows_id_seq OWNED BY public.shows.id;


--
-- Name: source_review_counts; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.source_review_counts (
    source_id bigint,
    source_review_max_updated_at timestamp with time zone,
    source_review_count bigint
);


--
-- Name: source_reviews; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.source_reviews (
    id bigint NOT NULL,
    source_id bigint NOT NULL,
    rating smallint,
    title text,
    review text DEFAULT ''::text NOT NULL,
    author text,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    uuid uuid NOT NULL
);


--
-- Name: COLUMN source_reviews.rating; Type: COMMENT; Schema: public; Owner: -
--

COMMENT ON COLUMN public.source_reviews.rating IS 'Scale of 1 to 10';


--
-- Name: source_review_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.source_review_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_review_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.source_review_id_seq OWNED BY public.source_reviews.id;


--
-- Name: source_sets; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.source_sets (
    id bigint NOT NULL,
    source_id bigint NOT NULL,
    index integer DEFAULT 0 NOT NULL,
    is_encore boolean DEFAULT false NOT NULL,
    name text NOT NULL,
    created_at timestamp with time zone DEFAULT timezone('utc'::text, now()) NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    uuid uuid NOT NULL
);


--
-- Name: source_sets_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.source_sets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_sets_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.source_sets_id_seq OWNED BY public.source_sets.id;


--
-- Name: source_tracks_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.source_tracks_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: source_tracks_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.source_tracks_id_seq OWNED BY public.source_tracks.id;


--
-- Name: sources_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: sources_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.sources_id_seq OWNED BY public.sources.id;


--
-- Name: tours; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.tours (
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


--
-- Name: tours_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.tours_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: tours_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.tours_id_seq OWNED BY public.tours.id;


--
-- Name: upstream_sources; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.upstream_sources (
    id integer NOT NULL,
    name text NOT NULL,
    url text,
    description text,
    credit_line text
);


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.upstream_sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.upstream_sources_id_seq OWNED BY public.upstream_sources.id;


--
-- Name: venue_show_counts; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.venue_show_counts (
    id bigint,
    shows_at_venue bigint
);


--
-- Name: venues; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.venues (
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


--
-- Name: venues_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.venues_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: venues_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.venues_id_seq OWNED BY public.venues.id;


--
-- Name: versioninfo; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.versioninfo (
    id integer NOT NULL,
    version bigint NOT NULL,
    appliedon timestamp with time zone,
    description text NOT NULL
);


--
-- Name: versioninfo_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.versioninfo_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: versioninfo_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.versioninfo_id_seq OWNED BY public.versioninfo.id;


--
-- Name: years; Type: TABLE; Schema: public; Owner: -
--

CREATE TABLE public.years (
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


--
-- Name: years_id_seq; Type: SEQUENCE; Schema: public; Owner: -
--

CREATE SEQUENCE public.years_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


--
-- Name: years_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: -
--

ALTER SEQUENCE public.years_id_seq OWNED BY public.years.id;


--
-- Name: artists id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists ALTER COLUMN id SET DEFAULT nextval('public.artists_id_seq'::regclass);


--
-- Name: eras id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.eras ALTER COLUMN id SET DEFAULT nextval('public.eras_id_seq'::regclass);


--
-- Name: features id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.features ALTER COLUMN id SET DEFAULT nextval('public.featuresets_id_seq'::regclass);


--
-- Name: links id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.links ALTER COLUMN id SET DEFAULT nextval('public.links_id_seq'::regclass);


--
-- Name: setlist_shows id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows ALTER COLUMN id SET DEFAULT nextval('public.setlist_show_id_seq'::regclass);


--
-- Name: setlist_songs id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs ALTER COLUMN id SET DEFAULT nextval('public.setlist_song_id_seq'::regclass);


--
-- Name: shows id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows ALTER COLUMN id SET DEFAULT nextval('public.shows_id_seq'::regclass);


--
-- Name: source_reviews id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_reviews ALTER COLUMN id SET DEFAULT nextval('public.source_review_id_seq'::regclass);


--
-- Name: source_sets id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_sets ALTER COLUMN id SET DEFAULT nextval('public.source_sets_id_seq'::regclass);


--
-- Name: source_tracks id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_tracks ALTER COLUMN id SET DEFAULT nextval('public.source_tracks_id_seq'::regclass);


--
-- Name: sources id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sources ALTER COLUMN id SET DEFAULT nextval('public.sources_id_seq'::regclass);


--
-- Name: tours id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tours ALTER COLUMN id SET DEFAULT nextval('public.tours_id_seq'::regclass);


--
-- Name: upstream_sources id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.upstream_sources ALTER COLUMN id SET DEFAULT nextval('public.upstream_sources_id_seq'::regclass);


--
-- Name: venues id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.venues ALTER COLUMN id SET DEFAULT nextval('public.venues_id_seq'::regclass);


--
-- Name: versioninfo id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.versioninfo ALTER COLUMN id SET DEFAULT nextval('public.versioninfo_id_seq'::regclass);


--
-- Name: years id; Type: DEFAULT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.years ALTER COLUMN id SET DEFAULT nextval('public.years_id_seq'::regclass);


--
-- Name: artists artists_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists
    ADD CONSTRAINT artists_pkey PRIMARY KEY (id);


--
-- Name: artists artists_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists
    ADD CONSTRAINT artists_uuid_key UNIQUE (uuid);


--
-- Name: eras eras_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.eras
    ADD CONSTRAINT eras_pkey PRIMARY KEY (id);


--
-- Name: features featuresets_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.features
    ADD CONSTRAINT featuresets_pkey PRIMARY KEY (id);


--
-- Name: links links_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.links
    ADD CONSTRAINT links_pkey PRIMARY KEY (id);


--
-- Name: links links_source_id_upstream_source_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.links
    ADD CONSTRAINT links_source_id_upstream_source_id_key UNIQUE (source_id, upstream_source_id);


--
-- Name: setlist_shows setlist_show_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_show_pkey PRIMARY KEY (id);


--
-- Name: setlist_shows setlist_show_upstream_identifier_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_show_upstream_identifier_key UNIQUE (artist_id, upstream_identifier);


--
-- Name: setlist_shows setlist_shows_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_shows_uuid_key UNIQUE (uuid);


--
-- Name: setlist_songs setlist_song_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs
    ADD CONSTRAINT setlist_song_pkey PRIMARY KEY (id);


--
-- Name: setlist_songs_plays setlist_songs_plays_song_id_show_id_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs_plays
    ADD CONSTRAINT setlist_songs_plays_song_id_show_id_key UNIQUE (played_setlist_song_id, played_setlist_show_id);


--
-- Name: setlist_songs setlist_songs_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs
    ADD CONSTRAINT setlist_songs_uuid_key UNIQUE (uuid);


--
-- Name: shows shows_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_pkey PRIMARY KEY (id);


--
-- Name: shows shows_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_uuid_key UNIQUE (uuid);


--
-- Name: source_reviews source_review_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_reviews
    ADD CONSTRAINT source_review_pkey PRIMARY KEY (id);


--
-- Name: source_reviews source_reviews_uuid; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_reviews
    ADD CONSTRAINT source_reviews_uuid UNIQUE (uuid);


--
-- Name: source_sets source_sets_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_sets
    ADD CONSTRAINT source_sets_pkey PRIMARY KEY (id);


--
-- Name: source_sets source_sets_source_id_index_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_sets
    ADD CONSTRAINT source_sets_source_id_index_key UNIQUE (source_id, index);


--
-- Name: source_sets source_sets_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_sets
    ADD CONSTRAINT source_sets_uuid_key UNIQUE (uuid);


--
-- Name: source_tracks source_tracks_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_tracks
    ADD CONSTRAINT source_tracks_pkey PRIMARY KEY (id);


--
-- Name: source_tracks source_tracks_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_tracks
    ADD CONSTRAINT source_tracks_uuid_key UNIQUE (uuid);


--
-- Name: sources sources_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sources
    ADD CONSTRAINT sources_pkey PRIMARY KEY (id);


--
-- Name: sources sources_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sources
    ADD CONSTRAINT sources_uuid_key UNIQUE (uuid);


--
-- Name: tours tours_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tours
    ADD CONSTRAINT tours_pkey PRIMARY KEY (id);


--
-- Name: tours tours_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tours
    ADD CONSTRAINT tours_uuid_key UNIQUE (uuid);


--
-- Name: upstream_sources upstream_sources_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.upstream_sources
    ADD CONSTRAINT upstream_sources_pkey PRIMARY KEY (id);


--
-- Name: venues venues_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_pkey PRIMARY KEY (id);


--
-- Name: venues venues_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_uuid_key UNIQUE (uuid);


--
-- Name: versioninfo versioninfo_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.versioninfo
    ADD CONSTRAINT versioninfo_pkey PRIMARY KEY (id);


--
-- Name: years years_pkey; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.years
    ADD CONSTRAINT years_pkey PRIMARY KEY (id);


--
-- Name: years years_uuid_key; Type: CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.years
    ADD CONSTRAINT years_uuid_key UNIQUE (uuid);


--
-- Name: artists_upstream_sources_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX artists_upstream_sources_key ON public.artists_upstream_sources USING btree (upstream_source_id, artist_id);


--
-- Name: idx_features_artist; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_features_artist ON public.features USING btree (artist_id);


--
-- Name: idx_setlist_shows_artist_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_setlist_shows_artist_id ON public.setlist_shows USING btree (artist_id);


--
-- Name: idx_show_source_information_artist_source_count; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_show_source_information_artist_source_count ON public.show_source_information USING btree (artist_id) INCLUDE (source_count);


--
-- Name: idx_show_source_information_max_created_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_show_source_information_max_created_at ON public.show_source_information USING btree (max_created_at DESC, show_id);


--
-- Name: idx_show_source_information_max_updated_at; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_show_source_information_max_updated_at ON public.show_source_information USING btree (max_updated_at DESC, show_id);


--
-- Name: idx_shows_artist_id_venue_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_shows_artist_id_venue_id ON public.shows USING btree (artist_id, venue_id);


--
-- Name: idx_shows_artist_id_year_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_shows_artist_id_year_id ON public.shows USING btree (artist_id, year_id);


--
-- Name: idx_shows_month_day; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_shows_month_day ON public.shows USING btree (EXTRACT(month FROM date), EXTRACT(day FROM date));


--
-- Name: idx_source_reviews_source_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_source_reviews_source_id ON public.source_reviews USING btree (source_id);


--
-- Name: idx_source_sets_source_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_source_sets_source_id ON public.source_sets USING btree (source_id);


--
-- Name: idx_source_show_id_covering; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_source_show_id_covering ON public.sources USING btree (show_id, avg_rating_weighted, flac_type, is_soundboard, updated_at);


--
-- Name: idx_source_tracks_source_set_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_source_tracks_source_set_id ON public.source_tracks USING btree (source_set_id);


--
-- Name: idx_source_venue_id; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_source_venue_id ON public.sources USING btree (venue_id, show_id);


--
-- Name: idx_sources_flac_upstream; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX idx_sources_flac_upstream ON public.sources USING btree (upstream_identifier, flac_type);


--
-- Name: idx_years_year; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX idx_years_year ON public.years USING btree (artist_id, year);


--
-- Name: setlist_songs_artist_id_slug; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX setlist_songs_artist_id_slug ON public.setlist_songs USING btree (artist_id, slug);


--
-- Name: setlist_songs_artist_id_upstream_identifier; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX setlist_songs_artist_id_upstream_identifier ON public.setlist_songs USING btree (artist_id, upstream_identifier);


--
-- Name: setlist_songs_plays_played_setlist_show_id_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX setlist_songs_plays_played_setlist_show_id_idx ON public.setlist_songs_plays USING btree (played_setlist_show_id);


--
-- Name: show_source_information_show_id_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX show_source_information_show_id_key ON public.show_source_information USING btree (show_id);


--
-- Name: shows_artist_id_display_date_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX shows_artist_id_display_date_key ON public.shows USING btree (artist_id, display_date);


--
-- Name: source_review_counts_source_id_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX source_review_counts_source_id_key ON public.source_review_counts USING btree (source_id);


--
-- Name: source_tracks_artist_id_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX source_tracks_artist_id_idx ON public.source_tracks USING btree (artist_id);


--
-- Name: source_tracks_source_id_idx; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX source_tracks_source_id_idx ON public.source_tracks USING btree (source_id);


--
-- Name: source_uniq_artist_upstream; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX source_uniq_artist_upstream ON public.sources USING btree (artist_id, upstream_identifier);


--
-- Name: tour_artist_id_tour_upstream_identifier; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX tour_artist_id_tour_upstream_identifier ON public.tours USING btree (artist_id, upstream_identifier);


--
-- Name: unique_artist_id_mp3_url; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX unique_artist_id_mp3_url ON public.source_tracks USING btree (artist_id, mp3_url);


--
-- Name: venue_show_counts_id_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX venue_show_counts_id_key ON public.venue_show_counts USING btree (id);


--
-- Name: venues_artist; Type: INDEX; Schema: public; Owner: -
--

CREATE INDEX venues_artist ON public.venues USING btree (artist_id);


--
-- Name: venues_upstream_identifier_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX venues_upstream_identifier_key ON public.venues USING btree (artist_id, upstream_identifier) WHERE (artist_id IS NOT NULL);


--
-- Name: venues_upstream_identifier_null_artist_key; Type: INDEX; Schema: public; Owner: -
--

CREATE UNIQUE INDEX venues_upstream_identifier_null_artist_key ON public.venues USING btree (upstream_identifier) WHERE (artist_id IS NULL);


--
-- Name: artists_upstream_sources artists_upstream_sources_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists_upstream_sources
    ADD CONSTRAINT artists_upstream_sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON DELETE CASCADE;


--
-- Name: artists_upstream_sources artists_upstream_sources_upstream_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.artists_upstream_sources
    ADD CONSTRAINT artists_upstream_sources_upstream_source_id_fkey FOREIGN KEY (upstream_source_id) REFERENCES public.upstream_sources(id) ON DELETE RESTRICT;


--
-- Name: eras eras_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.eras
    ADD CONSTRAINT eras_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: features featuresets_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.features
    ADD CONSTRAINT featuresets_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: links links_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.links
    ADD CONSTRAINT links_source_id_fkey FOREIGN KEY (source_id) REFERENCES public.sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: links links_upstream_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.links
    ADD CONSTRAINT links_upstream_source_id_fkey FOREIGN KEY (upstream_source_id) REFERENCES public.upstream_sources(id);


--
-- Name: setlist_shows setlist_show_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_show_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_shows setlist_show_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_show_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON UPDATE CASCADE;


--
-- Name: setlist_shows setlist_shows_era_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES public.eras(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: setlist_shows setlist_shows_tour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_shows
    ADD CONSTRAINT setlist_shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES public.tours(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: setlist_songs setlist_song_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs
    ADD CONSTRAINT setlist_song_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_songs_plays setlist_song_plays_played_setlist_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_show_id_fkey FOREIGN KEY (played_setlist_show_id) REFERENCES public.setlist_shows(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


--
-- Name: setlist_songs_plays setlist_song_plays_played_setlist_song_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_song_id_fkey FOREIGN KEY (played_setlist_song_id) REFERENCES public.setlist_songs(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: shows shows_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: shows shows_era_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES public.eras(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows shows_tour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES public.tours(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows shows_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows shows_year_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.shows
    ADD CONSTRAINT shows_year_id_fkey FOREIGN KEY (year_id) REFERENCES public.years(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: source_reviews source_review_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_reviews
    ADD CONSTRAINT source_review_source_id_fkey FOREIGN KEY (source_id) REFERENCES public.sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_sets source_sets_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_sets
    ADD CONSTRAINT source_sets_source_id_fkey FOREIGN KEY (source_id) REFERENCES public.sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_tracks source_tracks_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_tracks
    ADD CONSTRAINT source_tracks_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON DELETE CASCADE;


--
-- Name: source_tracks source_tracks_set_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_tracks
    ADD CONSTRAINT source_tracks_set_id_fkey FOREIGN KEY (source_set_id) REFERENCES public.source_sets(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: source_tracks source_tracks_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.source_tracks
    ADD CONSTRAINT source_tracks_source_id_fkey FOREIGN KEY (source_id) REFERENCES public.sources(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: sources sources_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sources
    ADD CONSTRAINT sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: sources sources_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sources
    ADD CONSTRAINT sources_show_id_fkey FOREIGN KEY (show_id) REFERENCES public.shows(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: sources sources_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.sources
    ADD CONSTRAINT sources_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES public.venues(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: tours tours_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.tours
    ADD CONSTRAINT tours_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: venues venues_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.venues
    ADD CONSTRAINT venues_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: years years_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: -
--

ALTER TABLE ONLY public.years
    ADD CONSTRAINT years_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES public.artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--


-- Local playback starts empty. Build the same Timescale query interfaces used
-- by the API without copying listeners, playback history, or production jobs.
SET search_path = public;
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE TABLE source_track_plays (
    id serial NOT NULL,
    created_at timestamptz DEFAULT timezone('utc', now()) NOT NULL,
    source_track_uuid uuid,
    user_uuid uuid,
    app_type smallint NOT NULL,
    UNIQUE (id, created_at)
);
SELECT create_hypertable('source_track_plays', 'created_at', chunk_time_interval => interval '1 day');

CREATE MATERIALIZED VIEW source_track_plays_hourly WITH (timescaledb.continuous) AS
SELECT time_bucket('1 hour', p.created_at) AS play_hour,
       p.source_track_uuid, a.uuid AS artist_uuid, s.uuid AS source_uuid, sh.uuid AS show_uuid,
       count(*) AS plays, sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON t.uuid = p.source_track_uuid
JOIN artists a ON a.id = t.artist_id
JOIN sources s ON s.id = t.source_id
JOIN shows sh ON sh.id = s.show_id
GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

CREATE MATERIALIZED VIEW source_track_plays_daily WITH (timescaledb.continuous) AS
SELECT time_bucket('1 day', p.created_at) AS play_day,
       p.source_track_uuid, a.uuid AS artist_uuid, s.uuid AS source_uuid, sh.uuid AS show_uuid,
       count(*) AS plays, sum(coalesce(t.duration, 0)) AS total_track_seconds
FROM source_track_plays p
JOIN source_tracks t ON t.uuid = p.source_track_uuid
JOIN artists a ON a.id = t.artist_id
JOIN sources s ON s.id = t.source_id
JOIN shows sh ON sh.id = s.show_id
GROUP BY 1, 2, 3, 4, 5
WITH NO DATA;

CREATE INDEX ON source_track_plays_hourly (play_hour DESC, artist_uuid);
CREATE INDEX ON source_track_plays_hourly (play_hour DESC, show_uuid);
CREATE INDEX ON source_track_plays_daily (play_day DESC, artist_uuid);
CREATE INDEX ON source_track_plays_daily (play_day DESC, show_uuid);

-- These are fresh local policies, with no production role or job state.
SELECT add_continuous_aggregate_policy('source_track_plays_hourly',
    start_offset => interval '6 hours', end_offset => interval '1 hour', schedule_interval => interval '1 hour');
SELECT add_continuous_aggregate_policy('source_track_plays_daily',
    start_offset => interval '3 days', end_offset => interval '1 day', schedule_interval => interval '1 day');
