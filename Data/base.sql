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
-- Name: plpgsql; Type: EXTENSION; Schema: -; Owner: 
--

CREATE EXTENSION IF NOT EXISTS plpgsql WITH SCHEMA pg_catalog;


--
-- Name: EXTENSION plpgsql; Type: COMMENT; Schema: -; Owner: 
--

COMMENT ON EXTENSION plpgsql IS 'PL/pgSQL procedural language';


SET search_path = public, pg_catalog;

SET default_tablespace = '';

SET default_with_oids = false;

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
    featured integer DEFAULT 0 NOT NULL
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
    track_names boolean DEFAULT false NOT NULL
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
    updated_at timestamp with time zone NOT NULL
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
    avg_rating_weighted real DEFAULT '0'::real NOT NULL,
    avg_duration real DEFAULT '0'::real NOT NULL,
    display_date text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
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
    review text NOT NULL,
    author text NOT NULL,
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
-- Name: source_tracks; Type: TABLE; Schema: public; Owner: alecgorge
--

CREATE TABLE source_tracks (
    id integer NOT NULL,
    source_id integer NOT NULL,
    source_set_id integer NOT NULL,
    track_position integer NOT NULL,
    duration integer NOT NULL,
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
    avg_rating real NOT NULL,
    num_reviews integer NOT NULL,
    upstream_identifier text NOT NULL,
    has_jamcharts boolean NOT NULL,
    description text NOT NULL,
    taper_notes text NOT NULL,
    source text NOT NULL,
    taper text NOT NULL,
    transferrer text NOT NULL,
    lineage text NOT NULL,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
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
    name text,
    created_at timestamp with time zone DEFAULT (now())::timestamp(0) without time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
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
    updated_at timestamp with time zone NOT NULL
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
    recording_count integer DEFAULT 0,
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


--
-- Data for Name: artists; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY artists (id, upstream_identifier, data_source, musicbrainz_id, created_at, updated_at, name, featured) FROM stdin;
1	GratefulDead	archive.org + setlist.fm	6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6	2016-06-23 16:58:18-04	2016-06-23 16:59:44-04	Grateful Dead	0
\.


--
-- Name: artists_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('artists_id_seq', 1, true);


--
-- Data for Name: eras; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY eras (id, artist_id, "order", name, created_at, updated_at) FROM stdin;
\.


--
-- Name: eras_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('eras_id_seq', 1, false);


--
-- Data for Name: features; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY features (id, descriptions, eras, multiple_sources, reviews, ratings, tours, taper_notes, source_information, sets, per_show_venues, per_source_venues, venue_coords, songs, years, track_md5s, review_titles, jam_charts, setlist_data_incomplete, artist_id, track_names) FROM stdin;
\.


--
-- Name: featuresets_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('featuresets_id_seq', 1, false);


--
-- Name: setlist_show_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('setlist_show_id_seq', 1, false);


--
-- Data for Name: setlist_shows; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY setlist_shows (id, artist_id, venue_id, upstream_identifier, date, created_at, updated_at) FROM stdin;
\.


--
-- Name: setlist_song_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('setlist_song_id_seq', 1, false);


--
-- Data for Name: setlist_songs; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY setlist_songs (id, artist_id, name, slug, created_at, updated_at, upstream_identifier) FROM stdin;
\.


--
-- Data for Name: setlist_songs_plays; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY setlist_songs_plays (played_setlist_song_id, played_setlist_show_id) FROM stdin;
\.


--
-- Data for Name: shows; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY shows (id, artist_id, tour_id, year_id, era_id, date, avg_rating_weighted, avg_duration, display_date, created_at, updated_at) FROM stdin;
\.


--
-- Name: shows_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('shows_id_seq', 1, false);


--
-- Name: source_review_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('source_review_id_seq', 1, false);


--
-- Data for Name: source_reviews; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY source_reviews (id, source_id, rating, title, review, author, created_at, updated_at) FROM stdin;
\.


--
-- Data for Name: source_sets; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY source_sets (id, source_id, index, is_encore, name, created_at, updated_at) FROM stdin;
\.


--
-- Name: source_sets_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('source_sets_id_seq', 1, false);


--
-- Data for Name: source_tracks; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY source_tracks (id, source_id, source_set_id, track_position, duration, title, slug, mp3_url, md5, created_at, updated_at) FROM stdin;
\.


--
-- Name: source_tracks_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('source_tracks_id_seq', 1, false);


--
-- Data for Name: sources; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY sources (id, show_id, is_soundboard, is_remaster, avg_rating, num_reviews, upstream_identifier, has_jamcharts, description, taper_notes, source, taper, transferrer, lineage, created_at, updated_at) FROM stdin;
\.


--
-- Name: sources_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('sources_id_seq', 1, false);


--
-- Data for Name: tours; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY tours (id, artist_id, start_date, end_date, name, created_at, updated_at) FROM stdin;
\.


--
-- Name: tours_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('tours_id_seq', 1, false);


--
-- Data for Name: venues; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY venues (id, artist_id, latitude, longitude, name, location, upstream_identifier, created_at, updated_at) FROM stdin;
\.


--
-- Name: venues_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('venues_id_seq', 1, false);


--
-- Data for Name: years; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY years (id, show_count, recording_count, duration, avg_duration, avg_rating, artist_id, year, created_at, updated_at) FROM stdin;
\.


--
-- Name: years_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('years_id_seq', 1, false);


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


--
-- Name: shows_artist_id_display_date_key; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX shows_artist_id_display_date_key ON shows USING btree (artist_id, display_date);


--
-- Name: venues_upstream_identifier_key; Type: INDEX; Schema: public; Owner: alecgorge
--

CREATE UNIQUE INDEX venues_upstream_identifier_key ON venues USING btree (artist_id, upstream_identifier);


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
-- Name: sources_show_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: alecgorge
--

ALTER TABLE ONLY sources
    ADD CONSTRAINT sources_show_id_fkey FOREIGN KEY (show_id) REFERENCES shows(id) ON UPDATE CASCADE ON DELETE SET NULL;


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

