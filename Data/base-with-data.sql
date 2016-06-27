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
    updated_at timestamp with time zone NOT NULL,
    tour_id integer
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

COPY artists (id, upstream_identifier, data_source, musicbrainz_id, created_at, updated_at, name, featured, slug) FROM stdin;
1	GratefulDead	archive.org + setlist.fm	6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6	2016-06-23 16:58:18-04	2016-06-23 16:59:44-04	Grateful Dead	0	grateful-dead
2	DeadAndCompany	archive.org + setlist.fm	94f8947c-2d9c-4519-bcf9-6d11a24ad006	2016-06-27 15:15:33-04	2016-06-27 15:15:33-04	Dead & Company	0	dead-and-company
\.


--
-- Name: artists_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('artists_id_seq', 2, true);


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
1	t	f	t	t	t	t	t	t	f	t	f	t	t	t	t	t	f	f	1	t
2	t	f	t	t	t	t	t	t	f	t	f	t	t	t	t	t	f	f	2	t
\.


--
-- Name: featuresets_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('featuresets_id_seq', 1, false);


--
-- Name: setlist_show_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('setlist_show_id_seq', 35, true);


--
-- Data for Name: setlist_shows; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY setlist_shows (id, artist_id, venue_id, upstream_identifier, date, created_at, updated_at, tour_id) FROM stdin;
1	2	1	43ffbbb7	2016-06-26	2016-06-27 18:47:55-04	2016-06-27 09:30:40-04	1
2	2	1	6bfe423a	2016-06-25	2016-06-27 18:47:57-04	2016-06-27 10:01:55-04	1
3	2	2	1bfe4dcc	2016-06-23	2016-06-27 18:47:58-04	2016-06-24 01:07:29-04	1
4	2	2	13ffad5d	2016-06-22	2016-06-27 18:48:00-04	2016-06-26 20:38:11-04	1
5	2	3	5bfe5fac	2016-06-21	2016-06-27 18:48:00-04	2016-06-22 13:34:39-04	1
6	2	4	4bfe636e	2016-06-20	2016-06-27 18:48:01-04	2016-06-25 23:54:56-04	1
7	2	5	3bfe74cc	2016-06-17	2016-06-27 18:48:02-04	2016-06-21 12:34:53-04	1
8	2	6	bfe7966	2016-06-16	2016-06-27 18:48:03-04	2016-06-18 09:36:58-04	1
9	2	7	2bfe10ba	2016-06-12	2016-06-27 18:48:04-04	2016-06-13 22:33:50-04	1
10	2	8	1bfe1d48	2016-06-10	2016-06-27 18:48:05-04	2016-06-12 17:30:52-04	1
11	2	9	33fe88fd	2016-05-23	2016-06-27 18:48:06-04	2016-06-20 20:24:36-04	1
12	2	10	63f16207	2016-05-10	2016-06-27 18:48:06-04	2016-05-12 16:52:52-04	2
13	2	11	1bf36d4c	2016-02-18	2016-06-27 18:48:07-04	2016-02-24 12:38:42-05	2
14	2	12	7bf27e94	2015-12-31	2016-06-27 18:48:07-04	2016-03-19 04:44:50-04	3
15	2	12	63f2028f	2015-12-30	2016-06-27 18:48:08-04	2016-02-26 12:56:18-05	3
16	2	13	23f20457	2015-12-28	2016-06-27 18:48:09-04	2016-06-12 10:59:19-04	3
17	2	13	43f20b8f	2015-12-27	2016-06-27 18:48:10-04	2015-12-28 08:56:28-05	3
18	2	14	73f2b28d	2015-11-28	2016-06-27 18:48:10-04	2016-02-26 12:56:18-05	3
19	2	14	2bf2b4ce	2015-11-27	2016-06-27 18:48:11-04	2015-11-28 13:27:07-05	3
20	2	15	53f543ed	2015-11-25	2016-06-27 18:48:12-04	2015-11-26 09:34:03-05	3
21	2	15	6bf54a8a	2015-11-24	2016-06-27 18:48:12-04	2015-11-30 13:19:17-05	3
22	2	16	4bf55b2e	2015-11-21	2016-06-27 18:48:13-04	2015-11-25 13:35:33-05	3
23	2	17	13f55d39	2015-11-20	2016-06-27 18:48:14-04	2015-11-25 13:36:37-05	3
24	2	18	7bf56e5c	2015-11-18	2016-06-27 18:48:14-04	2016-01-27 20:13:01-05	3
25	2	19	23f570f7	2015-11-17	2016-06-27 18:48:15-04	2015-11-23 18:40:15-05	3
26	2	20	3f505a7	2015-11-14	2016-06-27 18:48:16-04	2016-06-12 10:55:12-04	3
27	2	21	2bf50c3a	2015-11-13	2016-06-27 18:48:17-04	2015-11-23 18:53:09-05	3
28	2	22	1bf5199c	2015-11-11	2016-06-27 18:48:17-04	2016-06-18 08:29:17-04	3
29	2	23	43f523b7	2015-11-10	2016-06-27 18:48:18-04	2016-06-21 13:16:07-04	3
30	2	24	13f52d49	2015-11-07	2016-06-27 18:48:19-04	2015-11-15 19:58:31-05	3
31	2	25	3f53913	2015-11-06	2016-06-27 18:48:19-04	2015-11-15 19:58:19-05	3
32	2	26	5bf5c348	2015-11-05	2016-06-27 18:48:20-04	2015-11-15 19:58:07-05	3
33	2	24	2bf5d806	2015-11-01	2016-06-27 18:48:21-04	2015-11-15 19:57:56-05	3
34	2	24	5bf5df5c	2015-10-31	2016-06-27 18:48:21-04	2016-06-12 11:01:13-04	3
35	2	27	53f5df5d	2015-10-29	2016-06-27 18:48:22-04	2015-11-15 19:57:22-05	3
\.


--
-- Name: setlist_song_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('setlist_song_id_seq', 106, true);


--
-- Data for Name: setlist_songs; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY setlist_songs (id, artist_id, name, slug, created_at, updated_at, upstream_identifier) FROM stdin;
1	2	St. Stephen	st-stephen	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	St. Stephen
2	2	The Music Never Stopped	the-music-never-stopped	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	The Music Never Stopped
3	2	Bertha	bertha	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Bertha
4	2	Black-Throated Wind	black-throated-wind	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Black-Throated Wind
5	2	Peggy-O	peggy-o	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Peggy-O
6	2	Box of Rain	box-of-rain	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Box of Rain
7	2	Going Down the Road Feelin' Bad	going-down-the-road-feelin-bad	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Going Down the Road Feelin' Bad
8	2	Truckin'	truckin	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Truckin'
9	2	He's Gone	hes-gone	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	He's Gone
10	2	Help on the Way	help-on-the-way	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Help on the Way
11	2	Slipknot!	slipknot	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Slipknot!
12	2	Franklin's Tower	franklins-tower	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Franklin's Tower
13	2	Drums	drums	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Drums
14	2	Space	space	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Space
15	2	Days Between	days-between	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Days Between
16	2	China Cat Sunflower	china-cat-sunflower	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	China Cat Sunflower
17	2	I Know You Rider	i-know-you-rider	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	I Know You Rider
18	2	Samson and Delilah	samson-and-delilah	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	Samson and Delilah
19	2	Shakedown Street	shakedown-street	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Shakedown Street
20	2	Jack Straw	jack-straw	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Jack Straw
21	2	Althea	althea	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Althea
22	2	Loose Lucy	loose-lucy	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Loose Lucy
23	2	Ramble On Rose	ramble-on-rose	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Ramble On Rose
24	2	Sugaree	sugaree	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Sugaree
25	2	Passenger	passenger	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Passenger
26	2	Casey Jones	casey-jones	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Casey Jones
27	2	Dark Star	dark-star	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Dark Star
28	2	Friend of the Devil	friend-of-the-devil	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Friend of the Devil
29	2	Scarlet Begonias	scarlet-begonias	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Scarlet Begonias
30	2	Fire on the Mountain	fire-on-the-mountain	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Fire on the Mountain
31	2	The Other One	the-other-one	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	The Other One
32	2	Wharf Rat	wharf-rat	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Wharf Rat
33	2	Throwing Stones	throwing-stones	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Throwing Stones
34	2	Ripple	ripple	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	Ripple
35	2	One More Saturday Night	one-more-saturday-night	2016-06-27 18:47:57-04	2016-06-27 22:47:57.042646-04	One More Saturday Night
36	2	Cold Rain and Snow	cold-rain-and-snow	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Cold Rain and Snow
37	2	New Speedway Boogie	new-speedway-boogie	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	New Speedway Boogie
38	2	El Paso	el-paso	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	El Paso
39	2	They Love Each Other	they-love-each-other	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	They Love Each Other
40	2	Candyman	candyman	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Candyman
41	2	Bird Song	bird-song	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Bird Song
42	2	Don't Ease Me In	dont-ease-me-in	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Don't Ease Me In
43	2	Lost Sailor	lost-sailor	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Lost Sailor
44	2	Saint of Circumstance	saint-of-circumstance	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Saint of Circumstance
45	2	Viola Lee Blues	viola-lee-blues	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Viola Lee Blues
46	2	Terrapin Station	terrapin-station	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Terrapin Station
47	2	Dear Prudence	dear-prudence	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Dear Prudence
48	2	Sugar Magnolia	sugar-magnolia	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Sugar Magnolia
49	2	Black Muddy River	black-muddy-river	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04	Black Muddy River
50	2	Space... Lotsa space...	space-lotsa-space	2016-06-27 18:48:00-04	2016-06-27 22:47:59.503461-04	Space... Lotsa space...
51	2	Feel Like a Stranger	feel-like-a-stranger	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Feel Like a Stranger
52	2	Here Comes Sunshine	here-comes-sunshine	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Here Comes Sunshine
53	2	Brown Eyed Women	brown-eyed-women	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Brown Eyed Women
54	2	Loser	loser	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Loser
55	2	Little Red Rooster	little-red-rooster	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Little Red Rooster
56	2	Cassidy	cassidy	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Cassidy
57	2	Deal	deal	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Deal
58	2	Iko Iko	iko-iko	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Iko Iko
59	2	Estimated Prophet	estimated-prophet	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Estimated Prophet
60	2	Uncle John's Band	uncle-johns-band	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Uncle John's Band
61	2	Good Lovin'	good-lovin	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Good Lovin'
62	2	Brokedown Palace	brokedown-palace	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Brokedown Palace
63	2	Johnny B. Goode	johnny-b-goode	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04	Johnny B. Goode
64	2	West L.A. Fadeaway	west-la-fadeaway	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	West L.A. Fadeaway
65	2	Row Jimmy	row-jimmy	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Row Jimmy
66	2	Crazy Fingers	crazy-fingers	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Crazy Fingers
67	2	I Need a Miracle	i-need-a-miracle	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	I Need a Miracle
68	2	Big Railroad Blues	big-railroad-blues	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Big Railroad Blues
69	2	Playing in the Band	playing-in-the-band	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Playing in the Band
70	2	The Wheel	the-wheel	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	The Wheel
71	2	Eyes of the World	eyes-of-the-world	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Eyes of the World
72	2	Standing on the Moon	standing-on-the-moon	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Standing on the Moon
73	2	Let It Grow	let-it-grow	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Let It Grow
74	2	All Along the Watchtower	all-along-the-watchtower	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	All Along the Watchtower
75	2	Morning Dew	morning-dew	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Morning Dew
76	2	Not Fade Away	not-fade-away	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04	Not Fade Away
77	2	Minglewood Blues	minglewood-blues	2016-06-27 18:48:02-04	2016-06-27 22:48:02.201856-04	Minglewood Blues
78	2	Cumberland Blues	cumberland-blues	2016-06-27 18:48:02-04	2016-06-27 22:48:02.201856-04	Cumberland Blues
79	2	A Hard Rain's A-Gonna Fall	a-hard-rains-a-gonna-fall	2016-06-27 18:48:02-04	2016-06-27 22:48:02.201856-04	A Hard Rain's A-Gonna Fall
80	2	Looks Like Rain	looks-like-rain	2016-06-27 18:48:02-04	2016-06-27 22:48:02.201856-04	Looks Like Rain
81	2	Jam	jam	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	Jam
82	2	Hell in a Bucket	hell-in-a-bucket	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	Hell in a Bucket
83	2	Me and My Uncle	me-and-my-uncle	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	Me and My Uncle
84	2	Big River	big-river	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	Big River
85	2	Mississippi Half-Step Uptown Toodeloo	mississippi-half-step-uptown-toodeloo	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	Mississippi Half-Step Uptown Toodeloo
86	2	Stella Blue	stella-blue	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	Stella Blue
87	2	U.S. Blues	us-blues	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04	U.S. Blues
88	2	Smokestack Lightning	smokestack-lightning	2016-06-27 18:48:04-04	2016-06-27 22:48:03.925122-04	Smokestack Lightning
89	2	Tennessee Jed	tennessee-jed	2016-06-27 18:48:04-04	2016-06-27 22:48:03.925122-04	Tennessee Jed
90	2	Touch of Grey	touch-of-grey	2016-06-27 18:48:04-04	2016-06-27 22:48:03.925122-04	Touch of Grey
91	2	Liberty	liberty	2016-06-27 18:48:05-04	2016-06-27 22:48:04.844858-04	Liberty
92	2	The Promised Land	the-promised-land	2016-06-27 18:48:05-04	2016-06-27 22:48:04.844858-04	The Promised Land
93	2	Black Peter	black-peter	2016-06-27 18:48:05-04	2016-06-27 22:48:04.844858-04	Black Peter
94	2	Turn On Your Love Light	turn-on-your-love-light	2016-06-27 18:48:05-04	2016-06-27 22:48:04.844858-04	Turn On Your Love Light
95	2	Queen Jane Approximately	queen-jane-approximately	2016-06-27 18:48:06-04	2016-06-27 22:48:05.576191-04	Queen Jane Approximately
96	2	In the Midnight Hour	in-the-midnight-hour	2016-06-27 18:48:07-04	2016-06-27 22:48:07.048921-04	In the Midnight Hour
97	2	Sunshine Daydream	sunshine-daydream	2016-06-27 18:48:07-04	2016-06-27 22:48:07.048921-04	Sunshine Daydream
98	2	Shakey Ground	shakey-ground	2016-06-27 18:48:09-04	2016-06-27 22:48:08.702177-04	Shakey Ground
99	2	Cryptical Envelopment	cryptical-envelopment	2016-06-27 18:48:10-04	2016-06-27 22:48:10.347392-04	Cryptical Envelopment
100	2	Big Boss Man	big-boss-man	2016-06-27 18:48:13-04	2016-06-27 22:48:13.112-04	Big Boss Man
101	2	Mexicali Blues	mexicali-blues	2016-06-27 18:48:13-04	2016-06-27 22:48:13.112-04	Mexicali Blues
102	2	Ship of Fools	ship-of-fools	2016-06-27 18:48:16-04	2016-06-27 22:48:15.851062-04	Ship of Fools
103	2	Wang Dang Doodle	wang-dang-doodle	2016-06-27 18:48:17-04	2016-06-27 22:48:17.347333-04	Wang Dang Doodle
104	2	China Doll	china-doll	2016-06-27 18:48:17-04	2016-06-27 22:48:17.347333-04	China Doll
105	2	Get Out of My Life, Woman	get-out-of-my-life-woman	2016-06-27 18:48:18-04	2016-06-27 22:48:18.016261-04	Get Out of My Life, Woman
106	2	Werewolves of London	werewolves-of-london	2016-06-27 18:48:21-04	2016-06-27 22:48:21.457308-04	Werewolves of London
\.


--
-- Data for Name: setlist_songs_plays; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY setlist_songs_plays (played_setlist_song_id, played_setlist_show_id) FROM stdin;
1	1
2	1
3	1
4	1
5	1
6	1
7	1
8	1
9	1
10	1
11	1
12	1
13	1
14	1
15	1
16	1
17	1
18	1
13	2
14	2
19	2
20	2
21	2
22	2
23	2
24	2
25	2
26	2
27	2
28	2
29	2
30	2
31	2
32	2
33	2
34	2
35	2
13	3
14	3
36	3
37	3
38	3
39	3
40	3
41	3
42	3
43	3
44	3
45	3
46	3
47	3
48	3
49	3
50	4
13	5
14	5
15	5
16	5
17	5
51	5
52	5
53	5
54	5
55	5
56	5
57	5
58	5
59	5
60	5
61	5
62	5
63	5
3	6
13	6
14	6
19	6
34	6
64	6
65	6
66	6
67	6
68	6
69	6
70	6
71	6
72	6
73	6
74	6
75	6
76	6
1	7
4	7
7	7
9	7
13	7
14	7
21	7
23	7
27	7
37	7
43	7
44	7
49	7
77	7
78	7
79	7
80	7
6	8
13	8
14	8
16	8
17	8
20	8
24	8
45	8
48	8
53	8
60	8
81	8
82	8
83	8
84	8
85	8
86	8
87	8
3	9
8	9
10	9
11	9
12	9
13	9
14	9
19	9
25	9
26	9
29	9
30	9
31	9
32	9
41	9
46	9
69	9
81	9
88	9
89	9
90	9
2	10
13	10
14	10
28	10
36	10
39	10
51	10
56	10
57	10
59	10
71	10
91	10
92	10
93	10
94	10
1	11
15	11
16	11
17	11
22	11
24	11
27	11
31	11
36	11
53	11
59	11
62	11
68	11
82	11
95	11
3	12
21	12
29	12
30	12
76	12
19	13
53	13
1	14
13	14
14	14
22	14
23	14
24	14
27	14
29	14
30	14
39	14
46	14
47	14
48	14
51	14
60	14
62	14
82	14
90	14
96	14
97	14
3	15
13	15
14	15
16	15
17	15
19	15
21	15
28	15
31	15
32	15
41	15
45	15
52	15
54	15
64	15
69	15
77	15
94	15
2	16
10	16
11	16
12	16
13	16
14	16
20	16
37	16
40	16
43	16
44	16
62	16
70	16
76	16
80	16
83	16
85	16
89	16
98	16
4	17
7	17
8	17
9	17
13	17
14	17
18	17
26	17
34	17
36	17
53	17
56	17
57	17
59	17
71	17
72	17
87	17
93	17
10	18
11	18
12	18
13	18
14	18
22	18
23	18
24	18
29	18
30	18
31	18
35	18
56	18
57	18
62	18
64	18
75	18
89	18
94	18
99	18
1	19
4	19
13	19
14	19
19	19
20	19
21	19
26	19
27	19
32	19
34	19
48	19
60	19
65	19
69	19
73	19
2	20
5	20
8	20
9	20
13	20
14	20
16	20
17	20
41	20
46	20
51	20
53	20
55	20
71	20
76	20
82	20
86	20
3	21
10	21
11	21
12	21
13	21
14	21
27	21
36	21
37	21
40	21
43	21
44	21
59	21
61	21
83	21
88	21
90	21
93	21
7	22
13	22
14	22
20	22
23	22
26	22
28	22
29	22
30	22
35	22
47	22
60	22
65	22
78	22
82	22
99	22
100	22
101	22
2	23
4	23
13	23
14	23
19	23
24	23
34	23
39	23
46	23
48	23
53	23
58	23
70	23
71	23
73	23
77	23
80	23
8	24
13	24
14	24
16	24
17	24
21	24
22	24
37	24
38	24
45	24
57	24
59	24
63	24
64	24
69	24
81	24
86	24
89	24
1	25
3	25
10	25
11	25
12	25
13	25
14	25
36	25
42	25
51	25
54	25
56	25
60	25
62	25
67	25
75	25
76	25
92	25
9	26
13	26
14	26
19	26
20	26
28	26
29	26
30	26
31	26
32	26
35	26
43	26
44	26
66	26
82	26
83	26
102	26
13	27
14	27
16	27
17	27
41	27
53	27
55	27
61	27
69	27
70	27
71	27
72	27
78	27
85	27
89	27
90	27
93	27
3	28
8	28
13	28
14	28
21	28
26	28
27	28
37	28
38	28
45	28
48	28
52	28
77	28
87	28
92	28
103	28
104	28
2	29
5	29
7	29
13	29
14	29
23	29
24	29
34	29
46	29
47	29
56	29
57	29
59	29
60	29
65	29
84	29
105	29
1	30
9	30
10	30
11	30
12	30
13	30
14	30
19	30
20	30
31	30
36	30
39	30
62	30
67	30
76	30
86	30
89	30
3	31
8	31
13	31
14	31
29	31
30	31
43	31
44	31
53	31
54	31
64	31
70	31
71	31
80	31
90	31
96	31
100	31
13	32
14	32
16	32
17	32
22	32
26	32
40	32
41	32
48	32
51	32
52	32
58	32
69	32
72	32
78	32
85	32
87	32
3	33
7	33
13	33
14	33
18	33
19	33
24	33
27	33
28	33
29	33
30	33
32	33
34	33
60	33
66	33
69	33
77	33
8	34
13	34
14	34
16	34
17	34
20	34
21	34
23	34
35	34
37	34
46	34
53	34
56	34
57	34
59	34
71	34
75	34
103	34
106	34
2	35
9	35
10	35
11	35
12	35
13	35
14	35
31	35
36	35
41	35
43	35
44	35
51	35
69	35
76	35
86	35
89	35
90	35
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

COPY tours (id, artist_id, start_date, end_date, name, created_at, updated_at, slug, upstream_identifier) FROM stdin;
1	2	2016-05-23	2016-06-26	2016 U.S. Tour	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04	2016-us-tour	2016 U.S. Tour
2	2	2016-02-18	2016-05-10	Not Part of a Tour	2016-06-27 18:48:06-04	2016-06-27 22:48:06.312177-04	not-part-of-a-tour	Not Part of a Tour
3	2	2015-10-29	2015-12-31	2015 U.S. Tour	2016-06-27 18:48:07-04	2016-06-27 22:48:07.048921-04	2015-us-tour	2015 U.S. Tour
\.


--
-- Name: tours_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('tours_id_seq', 3, true);


--
-- Data for Name: venues; Type: TABLE DATA; Schema: public; Owner: alecgorge
--

COPY venues (id, artist_id, latitude, longitude, name, location, upstream_identifier, created_at, updated_at) FROM stdin;
1	\N	40.7142691000000028	-74.0059729000000033	Citi Field	New York, New York	setlistfm:5bd6d310	2016-06-27 18:47:55-04	2016-06-27 22:47:55.217052-04
2	\N	38.7228945999999965	-77.5361005000000034	Jiffy Lube Live	Bristow, Virginia	setlistfm:3d66993	2016-06-27 18:47:58-04	2016-06-27 22:47:58.42538-04
3	\N	43.0831300999999982	-73.7845650999999947	Saratoga Performing Arts Center	Saratoga Springs, New York	setlistfm:4bd63b2e	2016-06-27 18:48:00-04	2016-06-27 22:47:59.883551-04
4	\N	39.9259462999999997	-75.1196199000000036	BB&T Pavilion	Camden, New Jersey	setlistfm:63d5f21b	2016-06-27 18:48:01-04	2016-06-27 22:48:00.915492-04
5	\N	40.0455917999999969	-86.0085954999999984	Klipsch Music Center	Noblesville, Indiana	setlistfm:43d73f3b	2016-06-27 18:48:02-04	2016-06-27 22:48:02.201856-04
6	\N	39.1620035999999985	-84.4568862999999936	Riverbend Music Center	Cincinnati, Ohio	setlistfm:2bd4a04a	2016-06-27 18:48:03-04	2016-06-27 22:48:02.975382-04
7	\N	35.4817431999999968	-86.0885992999999985	Great Stage Park	Manchester, Tennessee	setlistfm:2bd6181e	2016-06-27 18:48:04-04	2016-06-27 22:48:03.925122-04
8	\N	35.2270869000000033	-80.8431267999999932	PNC Music Pavilion	Charlotte, North Carolina	setlistfm:43d4e71b	2016-06-27 18:48:05-04	2016-06-27 22:48:04.844858-04
9	\N	37.7749999999999986	-122.418999999999997	The Fillmore	San Francisco, California	setlistfm:23d6d4c7	2016-06-27 18:48:06-04	2016-06-27 22:48:05.576191-04
10	\N	34.0519999999999996	-118.244	Jimmy Kimmel Live	Los Angeles, California	setlistfm:4bd7c3d6	2016-06-27 18:48:06-04	2016-06-27 22:48:06.312177-04
11	\N	34.1388961999999978	-118.353411500000007	Universal Studios	Universal City, California	setlistfm:bd6255e	2016-06-27 18:48:07-04	2016-06-27 22:48:06.806114-04
12	\N	33.9616801000000024	-118.353131099999999	The Forum	Inglewood, California	setlistfm:73d6366d	2016-06-27 18:48:07-04	2016-06-27 22:48:07.048921-04
13	\N	37.7749999999999986	-122.418999999999997	Bill Graham Civic Auditorium	San Francisco, California	setlistfm:13d6354d	2016-06-27 18:48:09-04	2016-06-27 22:48:08.702177-04
14	\N	36.1749705000000006	-115.137223000000006	MGM Grand Garden Arena	Las Vegas, Nevada	setlistfm:5bd6378c	2016-06-27 18:48:10-04	2016-06-27 22:48:10.347392-04
15	\N	39.9205411000000012	-105.086650399999996	1st Bank Center	Broomfield, Colorado	setlistfm:63d7aeff	2016-06-27 18:48:12-04	2016-06-27 22:48:11.773942-04
16	\N	44.9799653999999975	-93.263836100000006	Target Center	Minneapolis, Minnesota	setlistfm:3d62113	2016-06-27 18:48:13-04	2016-06-27 22:48:13.112-04
17	\N	38.6272732999999988	-90.1978888999999953	Scottrade Center	St. Louis, Missouri	setlistfm:5bd61fd0	2016-06-27 18:48:14-04	2016-06-27 22:48:13.808477-04
18	\N	36.1658899000000034	-86.7844431999999983	Bridgestone Arena	Nashville, Tennessee	setlistfm:23d6746f	2016-06-27 18:48:14-04	2016-06-27 22:48:14.416737-04
19	\N	33.7489953999999983	-84.3879823999999985	Philips Arena	Atlanta, Georgia	setlistfm:43d637a3	2016-06-27 18:48:15-04	2016-06-27 22:48:15.149058-04
20	\N	36.072635499999997	-79.7919753999999983	Greensboro Coliseum	Greensboro, North Carolina	setlistfm:4bd6cfb6	2016-06-27 18:48:16-04	2016-06-27 22:48:15.851062-04
21	\N	39.9611755000000031	-82.9987942000000061	Nationwide Arena	Columbus, Ohio	setlistfm:1bd63994	2016-06-27 18:48:17-04	2016-06-27 22:48:16.555201-04
22	\N	42.8864468000000016	-78.8783688999999981	First Niagara Center	Buffalo, New York	setlistfm:3d7e1b7	2016-06-27 18:48:17-04	2016-06-27 22:48:17.347333-04
23	\N	42.2625931999999978	-71.8022933999999964	DCU Center	Worcester, Massachusetts	setlistfm:13d63961	2016-06-27 18:48:18-04	2016-06-27 22:48:18.016261-04
24	\N	40.7142691000000028	-74.0059729000000033	Madison Square Garden	New York, New York	setlistfm:23d63cc7	2016-06-27 18:48:19-04	2016-06-27 22:48:18.62919-04
25	\N	38.8950000000000031	-77.0360000000000014	Verizon Center	Washington, Washington, D.C.	setlistfm:3bd6383c	2016-06-27 18:48:19-04	2016-06-27 22:48:19.343981-04
26	\N	39.9523349999999979	-75.1637889999999942	Wells Fargo Center	Philadelphia, Pennsylvania	setlistfm:3bd65058	2016-06-27 18:48:20-04	2016-06-27 22:48:20.075539-04
27	\N	42.6525792999999993	-73.7562317000000007	Times Union Center	Albany, New York	setlistfm:4bd6cb36	2016-06-27 18:48:22-04	2016-06-27 22:48:22.237711-04
\.


--
-- Name: venues_id_seq; Type: SEQUENCE SET; Schema: public; Owner: alecgorge
--

SELECT pg_catalog.setval('venues_id_seq', 27, true);


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

