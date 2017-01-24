--
-- PostgreSQL database dump
--

-- Dumped from database version 9.5.3
-- Dumped by pg_dump version 9.5.3

SET statement_timeout = 0;
SET lock_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SET check_function_bodies = false;
SET client_min_messages = warning;
SET row_security = off;

--
-- Name: hangfire; Type: SCHEMA; Schema: -; Owner: alecgorge
--

CREATE SCHEMA hangfire;


ALTER SCHEMA hangfire OWNER TO alecgorge;

--
-- Name: plpgsql; Type: EXTENSION; Schema: -; Owner: 
--

CREATE EXTENSION IF NOT EXISTS plpgsql WITH SCHEMA pg_catalog;


--
-- Name: EXTENSION plpgsql; Type: COMMENT; Schema: -; Owner: 
--

COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';


SET search_path = hangfire, pg_catalog;

SET default_tablespace = '';

SET default_with_oids = false;

--
-- Name: counter; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE counter (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    value smallint NOT NULL,
    expireat timestamp without time zone,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE counter OWNER TO alecgorge;

--
-- Name: counter_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE counter_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE counter_id_seq OWNER TO alecgorge;

--
-- Name: counter_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE counter_id_seq OWNED BY counter.id;


--
-- Name: hash; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE hash (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    field character varying(100) NOT NULL,
    value text,
    expireat timestamp without time zone,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE hash OWNER TO alecgorge;

--
-- Name: hash_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE hash_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE hash_id_seq OWNER TO alecgorge;

--
-- Name: hash_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE hash_id_seq OWNED BY hash.id;


--
-- Name: job; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE job (
    id integer NOT NULL,
    stateid integer,
    statename character varying(20),
    invocationdata text NOT NULL,
    arguments text NOT NULL,
    createdat timestamp without time zone NOT NULL,
    expireat timestamp without time zone,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE job OWNER TO alecgorge;

--
-- Name: job_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE job_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE job_id_seq OWNER TO alecgorge;

--
-- Name: job_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE job_id_seq OWNED BY job.id;


--
-- Name: jobparameter; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE jobparameter (
    id integer NOT NULL,
    jobid integer NOT NULL,
    name character varying(40) NOT NULL,
    value text,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE jobparameter OWNER TO alecgorge;

--
-- Name: jobparameter_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE jobparameter_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE jobparameter_id_seq OWNER TO alecgorge;

--
-- Name: jobparameter_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE jobparameter_id_seq OWNED BY jobparameter.id;


--
-- Name: jobqueue; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE jobqueue (
    id integer NOT NULL,
    jobid integer NOT NULL,
    queue character varying(20) NOT NULL,
    fetchedat timestamp without time zone,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE jobqueue OWNER TO alecgorge;

--
-- Name: jobqueue_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE jobqueue_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE jobqueue_id_seq OWNER TO alecgorge;

--
-- Name: jobqueue_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE jobqueue_id_seq OWNED BY jobqueue.id;


--
-- Name: list; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE list (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    value text,
    expireat timestamp without time zone,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE list OWNER TO alecgorge;

--
-- Name: list_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE list_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE list_id_seq OWNER TO alecgorge;

--
-- Name: list_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE list_id_seq OWNED BY list.id;


--
-- Name: lock; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE lock (
    resource character varying(100) NOT NULL,
    updatecount integer DEFAULT 0 NOT NULL,
    acquired timestamp without time zone
);


ALTER TABLE lock OWNER TO alecgorge;

--
-- Name: schema; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE schema (
    version integer NOT NULL
);


ALTER TABLE schema OWNER TO alecgorge;

--
-- Name: server; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE server (
    id character varying(100) NOT NULL,
    data text,
    lastheartbeat timestamp without time zone NOT NULL,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE server OWNER TO alecgorge;

--
-- Name: set; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE set (
    id integer NOT NULL,
    key character varying(100) NOT NULL,
    score double precision NOT NULL,
    value text NOT NULL,
    expireat timestamp without time zone,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE set OWNER TO alecgorge;

--
-- Name: set_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE set_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE set_id_seq OWNER TO alecgorge;

--
-- Name: set_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE set_id_seq OWNED BY set.id;


--
-- Name: state; Type: TABLE; Schema: hangfire; Owner: alecgorge
--

CREATE TABLE state (
    id integer NOT NULL,
    jobid integer NOT NULL,
    name character varying(20) NOT NULL,
    reason character varying(100),
    createdat timestamp without time zone NOT NULL,
    data text,
    updatecount integer DEFAULT 0 NOT NULL
);


ALTER TABLE state OWNER TO alecgorge;

--
-- Name: state_id_seq; Type: SEQUENCE; Schema: hangfire; Owner: alecgorge
--

CREATE SEQUENCE state_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE state_id_seq OWNER TO alecgorge;

--
-- Name: state_id_seq; Type: SEQUENCE OWNED BY; Schema: hangfire; Owner: alecgorge
--

ALTER SEQUENCE state_id_seq OWNED BY state.id;


SET search_path = public, pg_catalog;

--
-- Name: artists; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE artists (
    id integer NOT NULL,
    upstream_identifier text NOT NULL,
    data_source text NOT NULL,
    musicbrainz_id text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    name text NOT NULL,
    featured integer DEFAULT 0 NOT NULL,
    slug text NOT NULL
);


ALTER TABLE artists OWNER TO alecgorge;

--
-- Name: artists_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE artists_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE artists_id_seq OWNER TO alecgorge;

--
-- Name: artists_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE artists_id_seq OWNED BY artists.id;


--
-- Name: eras; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE eras (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    "order" smallint DEFAULT '0'::smallint,
    name text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


ALTER TABLE eras OWNER TO alecgorge;

--
-- Name: eras_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE eras_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE eras_id_seq OWNER TO alecgorge;

--
-- Name: eras_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE eras_id_seq OWNED BY eras.id;


--
-- Name: features; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE features OWNER TO alecgorge;

--
-- Name: featuresets_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE featuresets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE featuresets_id_seq OWNER TO alecgorge;

--
-- Name: featuresets_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE featuresets_id_seq OWNED BY features.id;


--
-- Name: setlist_shows; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE setlist_shows OWNER TO alecgorge;

--
-- Name: setlist_show_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE setlist_show_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE setlist_show_id_seq OWNER TO alecgorge;

--
-- Name: setlist_show_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE setlist_show_id_seq OWNED BY setlist_shows.id;


--
-- Name: setlist_songs; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE setlist_songs OWNER TO alecgorge;

--
-- Name: setlist_song_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE setlist_song_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE setlist_song_id_seq OWNER TO alecgorge;

--
-- Name: setlist_song_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE setlist_song_id_seq OWNED BY setlist_songs.id;


--
-- Name: setlist_songs_plays; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE setlist_songs_plays (
    played_setlist_song_id integer NOT NULL,
    played_setlist_show_id integer NOT NULL
);


ALTER TABLE setlist_songs_plays OWNER TO alecgorge;

--
-- Name: shows; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE shows (
    id integer NOT NULL,
    artist_id integer NOT NULL,
    tour_id integer,
    year_id integer,
    era_id integer,
    date date NOT NULL,
    avg_rating real DEFAULT '0'::real,
    avg_duration real DEFAULT '0'::real,
    display_date text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    venue_id integer
);


ALTER TABLE shows OWNER TO alecgorge;

--
-- Name: shows_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE shows_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE shows_id_seq OWNER TO alecgorge;

--
-- Name: shows_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE shows_id_seq OWNED BY shows.id;


--
-- Name: source_reviews; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE source_reviews OWNER TO alecgorge;

--
-- Name: COLUMN source_reviews.rating; Type: COMMENT; Schema: public; Owner: alecgorge
--

COMMENT ON COLUMN source_reviews.rating IS 'Scale of 1 to 10';


--
-- Name: source_review_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE source_review_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE source_review_id_seq OWNER TO alecgorge;

--
-- Name: source_review_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE source_review_id_seq OWNED BY source_reviews.id;


--
-- Name: source_sets; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE source_sets OWNER TO alecgorge;

--
-- Name: source_sets_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE source_sets_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE source_sets_id_seq OWNER TO alecgorge;

--
-- Name: source_sets_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE source_sets_id_seq OWNED BY source_sets.id;


--
-- Name: source_tracks_plays; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE source_tracks_plays (
    id integer NOT NULL,
    source_track_id integer NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    user_id integer NOT NULL
);


ALTER TABLE source_tracks_plays OWNER TO alecgorge;

--
-- Name: source_track_play_history_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE source_track_play_history_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE source_track_play_history_id_seq OWNER TO alecgorge;

--
-- Name: source_track_play_history_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE source_track_play_history_id_seq OWNED BY source_tracks_plays.id;


--
-- Name: source_tracks; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE source_tracks OWNER TO alecgorge;

--
-- Name: COLUMN source_tracks.duration; Type: COMMENT; Schema: public; Owner: alecgorge
--

COMMENT ON COLUMN source_tracks.duration IS 'Duration in seconds';


--
-- Name: source_tracks_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE source_tracks_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE source_tracks_id_seq OWNER TO alecgorge;

--
-- Name: source_tracks_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE source_tracks_id_seq OWNED BY source_tracks.id;


--
-- Name: sources; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE sources (
    id integer NOT NULL,
    show_id integer,
    is_soundboard boolean NOT NULL,
    is_remaster boolean NOT NULL,
    avg_rating real DEFAULT '0'::real NOT NULL,
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
    avg_rating_weighted real DEFAULT '0'::real,
    duration real DEFAULT '0'::real,
    venue_id integer,
    display_date text NOT NULL,
    num_ratings integer
);


ALTER TABLE sources OWNER TO alecgorge;

--
-- Name: sources_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE sources_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE sources_id_seq OWNER TO alecgorge;

--
-- Name: sources_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE sources_id_seq OWNED BY sources.id;


--
-- Name: tours; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE tours OWNER TO alecgorge;

--
-- Name: tours_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE tours_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE tours_id_seq OWNER TO alecgorge;

--
-- Name: tours_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE tours_id_seq OWNED BY tours.id;


--
-- Name: venues; Type: TABLE; Schema: public; Owner: alecgorge
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


ALTER TABLE venues OWNER TO alecgorge;

--
-- Name: venues_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE venues_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE venues_id_seq OWNER TO alecgorge;

--
-- Name: venues_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE venues_id_seq OWNED BY venues.id;


--
-- Name: years; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE years (
    id integer NOT NULL,
    show_count integer DEFAULT 0,
    source_count integer DEFAULT 0,
    duration integer DEFAULT 0,
    avg_duration real DEFAULT '0'::real,
    avg_rating real DEFAULT '0'::real,
    artist_id integer NOT NULL,
    year text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);


ALTER TABLE years OWNER TO alecgorge;

--
-- Name: years_id_seq; Type: SEQUENCE; Schema: public; Owner: alecgorge
--

CREATE SEQUENCE years_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE years_id_seq OWNER TO alecgorge;

--
-- Name: years_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: alecgorge
--

ALTER SEQUENCE years_id_seq OWNED BY years.id;


SET search_path = hangfire, pg_catalog;

--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY counter ALTER COLUMN id SET DEFAULT nextval('counter_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY hash ALTER COLUMN id SET DEFAULT nextval('hash_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY job ALTER COLUMN id SET DEFAULT nextval('job_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY jobparameter ALTER COLUMN id SET DEFAULT nextval('jobparameter_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY jobqueue ALTER COLUMN id SET DEFAULT nextval('jobqueue_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY list ALTER COLUMN id SET DEFAULT nextval('list_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY set ALTER COLUMN id SET DEFAULT nextval('set_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY state ALTER COLUMN id SET DEFAULT nextval('state_id_seq'::regclass);


SET search_path = public, pg_catalog;

--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY artists ALTER COLUMN id SET DEFAULT nextval('artists_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY eras ALTER COLUMN id SET DEFAULT nextval('eras_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY features ALTER COLUMN id SET DEFAULT nextval('featuresets_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows ALTER COLUMN id SET DEFAULT nextval('setlist_show_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_songs ALTER COLUMN id SET DEFAULT nextval('setlist_song_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows ALTER COLUMN id SET DEFAULT nextval('shows_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_reviews ALTER COLUMN id SET DEFAULT nextval('source_review_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_sets ALTER COLUMN id SET DEFAULT nextval('source_sets_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks ALTER COLUMN id SET DEFAULT nextval('source_tracks_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks_plays ALTER COLUMN id SET DEFAULT nextval('source_track_play_history_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources ALTER COLUMN id SET DEFAULT nextval('sources_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY tours ALTER COLUMN id SET DEFAULT nextval('tours_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY venues ALTER COLUMN id SET DEFAULT nextval('venues_id_seq'::regclass);


--
-- Name: id; Type: DEFAULT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY years ALTER COLUMN id SET DEFAULT nextval('years_id_seq'::regclass);


SET search_path = hangfire, pg_catalog;

--
-- Name: counter_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY counter
    ADD CONSTRAINT counter_pkey PRIMARY KEY (id);


--
-- Name: hash_key_field_key; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY hash
    ADD CONSTRAINT hash_key_field_key UNIQUE (key, field);


--
-- Name: hash_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY hash
    ADD CONSTRAINT hash_pkey PRIMARY KEY (id);


--
-- Name: job_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY job
    ADD CONSTRAINT job_pkey PRIMARY KEY (id);


--
-- Name: jobparameter_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY jobparameter
    ADD CONSTRAINT jobparameter_pkey PRIMARY KEY (id);


--
-- Name: jobqueue_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY jobqueue
    ADD CONSTRAINT jobqueue_pkey PRIMARY KEY (id);


--
-- Name: list_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY list
    ADD CONSTRAINT list_pkey PRIMARY KEY (id);


--
-- Name: lock_resource_key; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY lock
    ADD CONSTRAINT lock_resource_key UNIQUE (resource);


--
-- Name: schema_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY schema
    ADD CONSTRAINT schema_pkey PRIMARY KEY (version);


--
-- Name: server_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY server
    ADD CONSTRAINT server_pkey PRIMARY KEY (id);


--
-- Name: set_key_value_key; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY set
    ADD CONSTRAINT set_key_value_key UNIQUE (key, value);


--
-- Name: set_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY set
    ADD CONSTRAINT set_pkey PRIMARY KEY (id);


--
-- Name: state_pkey; Type: CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY state
    ADD CONSTRAINT state_pkey PRIMARY KEY (id);


SET search_path = public, pg_catalog;

--
-- Name: artists_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY artists
    ADD CONSTRAINT artists_pkey PRIMARY KEY (id);


--
-- Name: eras_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY eras
    ADD CONSTRAINT eras_pkey PRIMARY KEY (id);


--
-- Name: featuresets_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY features
    ADD CONSTRAINT featuresets_pkey PRIMARY KEY (id);


--
-- Name: setlist_show_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_pkey PRIMARY KEY (id);


--
-- Name: setlist_show_upstream_identifier_key; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_upstream_identifier_key UNIQUE (upstream_identifier);


--
-- Name: setlist_song_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_song_pkey PRIMARY KEY (id);


--
-- Name: shows_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_pkey PRIMARY KEY (id);


--
-- Name: source_review_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_review_pkey PRIMARY KEY (id);


--
-- Name: source_sets_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_pkey PRIMARY KEY (id);


--
-- Name: source_track_play_history_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks_plays
    ADD CONSTRAINT source_track_play_history_pkey PRIMARY KEY (id);


--
-- Name: source_tracks_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_pkey PRIMARY KEY (id);


--
-- Name: sources_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_pkey PRIMARY KEY (id);


--
-- Name: sources_upstream_identifier_key; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_upstream_identifier_key UNIQUE (upstream_identifier);


--
-- Name: tours_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_pkey PRIMARY KEY (id);


--
-- Name: venues_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_pkey PRIMARY KEY (id);


--
-- Name: years_pkey; Type: CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY years
    ADD CONSTRAINT years_pkey PRIMARY KEY (id);


SET search_path = hangfire, pg_catalog;

--
-- Name: ix_hangfire_counter_expireat; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_counter_expireat ON counter USING btree (expireat);


--
-- Name: ix_hangfire_counter_key; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_counter_key ON counter USING btree (key);


--
-- Name: ix_hangfire_job_statename; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_job_statename ON job USING btree (statename);


--
-- Name: ix_hangfire_jobparameter_jobidandname; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_jobparameter_jobidandname ON jobparameter USING btree (jobid, name);


--
-- Name: ix_hangfire_jobqueue_jobidandqueue; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_jobqueue_jobidandqueue ON jobqueue USING btree (jobid, queue);


--
-- Name: ix_hangfire_jobqueue_queueandfetchedat; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_jobqueue_queueandfetchedat ON jobqueue USING btree (queue, fetchedat);


--
-- Name: ix_hangfire_state_jobid; Type: INDEX; Schema: hangfire; Owner: alecgorge
--

CREATE INDEX ix_hangfire_state_jobid ON state USING btree (jobid);


SET search_path = public, pg_catalog;

--
-- Name: setlist_songs_artist_id_slug; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX setlist_songs_artist_id_slug ON setlist_songs USING btree (artist_id, slug);


--
-- Name: shows_artist_id_display_date_key; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX shows_artist_id_display_date_key ON shows USING btree (artist_id, display_date);


--
-- Name: tour_artist_id_tour_slug; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX tour_artist_id_tour_slug ON tours USING btree (artist_id, slug);


--
-- Name: venues_artist; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE INDEX venues_artist ON venues USING btree (artist_id);


--
-- Name: venues_upstream_identifier_key; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX venues_upstream_identifier_key ON venues USING btree (artist_id, upstream_identifier) WHERE (artist_id IS NOT NULL);


--
-- Name: venues_upstream_identifier_null_artist_key; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX venues_upstream_identifier_null_artist_key ON venues USING btree (upstream_identifier) WHERE (artist_id IS NULL);


--
-- Name: years_year; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX years_year ON years USING btree (artist_id, year);


SET search_path = hangfire, pg_catalog;

--
-- Name: jobparameter_jobid_fkey; Type: FK CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY jobparameter
    ADD CONSTRAINT jobparameter_jobid_fkey FOREIGN KEY (jobid) REFERENCES job(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: state_jobid_fkey; Type: FK CONSTRAINT; Schema: hangfire; Owner: alecgorge
--

ALTER TABLE ONLY state
    ADD CONSTRAINT state_jobid_fkey FOREIGN KEY (jobid) REFERENCES job(id) ON UPDATE CASCADE ON DELETE CASCADE;


SET search_path = public, pg_catalog;

--
-- Name: eras_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY eras
    ADD CONSTRAINT eras_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: featuresets_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY features
    ADD CONSTRAINT featuresets_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_show_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_show_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_show_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE;


--
-- Name: setlist_shows_era_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES eras(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: setlist_shows_tour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_shows
    ADD CONSTRAINT setlist_shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES tours(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: setlist_song_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_songs
    ADD CONSTRAINT setlist_song_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: setlist_song_plays_played_setlist_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_show_id_fkey FOREIGN KEY (played_setlist_show_id) REFERENCES setlist_shows(id) ON UPDATE RESTRICT ON DELETE RESTRICT;


--
-- Name: setlist_song_plays_played_setlist_song_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY setlist_songs_plays
    ADD CONSTRAINT setlist_song_plays_played_setlist_song_id_fkey FOREIGN KEY (played_setlist_song_id) REFERENCES setlist_songs(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: shows_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: shows_era_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_era_id_fkey FOREIGN KEY (era_id) REFERENCES eras(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows_tour_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_tour_id_fkey FOREIGN KEY (tour_id) REFERENCES tours(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: shows_year_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY shows
    ADD CONSTRAINT shows_year_id_fkey FOREIGN KEY (year_id) REFERENCES years(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: source_review_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_reviews
    ADD CONSTRAINT source_review_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_sets_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_sets
    ADD CONSTRAINT source_sets_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_tracks_plays_source_track_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks_plays
    ADD CONSTRAINT source_tracks_plays_source_track_id_fkey FOREIGN KEY (source_track_id) REFERENCES source_tracks(id);


--
-- Name: source_tracks_set_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_set_id_fkey FOREIGN KEY (source_set_id) REFERENCES source_sets(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: source_tracks_source_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY source_tracks
    ADD CONSTRAINT source_tracks_source_id_fkey FOREIGN KEY (source_id) REFERENCES sources(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: sources_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: sources_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_show_id_fkey FOREIGN KEY (show_id) REFERENCES shows(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: sources_venue_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_venue_id_fkey FOREIGN KEY (venue_id) REFERENCES venues(id) ON UPDATE CASCADE ON DELETE SET NULL;


--
-- Name: tours_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY tours
    ADD CONSTRAINT tours_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: venues_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY venues
    ADD CONSTRAINT venues_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: years_artist_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY years
    ADD CONSTRAINT years_artist_id_fkey FOREIGN KEY (artist_id) REFERENCES artists(id) ON UPDATE CASCADE ON DELETE CASCADE;


--
-- Name: public; Type: ACL; Schema: -; Owner: alecgorge
--

REVOKE ALL ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA public FROM alecgorge;
GRANT ALL ON SCHEMA public TO alecgorge;
GRANT ALL ON SCHEMA public TO PUBLIC;


--
-- PostgreSQL database dump complete
--

