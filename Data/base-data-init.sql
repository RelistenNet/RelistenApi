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
-- Data for Name: artists; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO artists VALUES (1, 'b4681cdc-4002-4521-8458-ac812f1b6d28', '2017-02-11 12:59:40.256192-05', '2017-02-11 12:59:40.256192-05', 'Lotus', 1, 'lotus');
INSERT INTO artists VALUES (2, '94f8947c-2d9c-4519-bcf9-6d11a24ad006', '2017-02-12 16:01:12.707771-05', '2017-02-12 16:01:12.707771-05', 'Dead and Company', 1, 'dead-and-company');
INSERT INTO artists VALUES (4, 'e01646f2-2a04-450d-8bf2-0d993082e058', '2017-02-12 16:11:14.708367-05', '2017-02-12 16:11:14.708367-05', 'Phish', 1, 'phish');
INSERT INTO artists VALUES (5, '3797a6d0-7700-44bf-96fb-f44386bc9ab2', '2017-02-12 16:14:53.403868-05', '2017-02-12 16:14:53.403868-05', 'Widespread Panic', 1, 'wsp');


--
-- Name: artists_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('artists_id_seq', 5, true);


--
-- Data for Name: upstream_sources; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO upstream_sources VALUES (1, 'archive.org', 'https://archive.org', 'Internet Archive', 'blah blah blah');
INSERT INTO upstream_sources VALUES (2, 'phantasytour.com', 'https://phantasytour.com', 'Phantasy Tour', 'blah blah bloop');
INSERT INTO upstream_sources VALUES (3, 'phish.in', 'https://phish.in', 'Phish.in', 'beep boop');
INSERT INTO upstream_sources VALUES (4, 'phish.net', 'https://phish.net', 'Phish.net', 'blop blorp');
INSERT INTO upstream_sources VALUES (5, 'panicstream.com', 'https://panicstream.com', 'Panic Stream', 'asdf');
INSERT INTO upstream_sources VALUES (6, 'setlist.fm', 'https://setlist.fm', 'Setlist.FM', 'doop boop');


--
-- Data for Name: artists_upstream_sources; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO artists_upstream_sources VALUES (2, 1, '12');
INSERT INTO artists_upstream_sources VALUES (1, 1, 'Lotus');
INSERT INTO artists_upstream_sources VALUES (1, 2, 'DeadAndCompany');
INSERT INTO artists_upstream_sources VALUES (2, 2, '7');
INSERT INTO artists_upstream_sources VALUES (5, 5, NULL);
INSERT INTO artists_upstream_sources VALUES (6, 5, NULL);
INSERT INTO artists_upstream_sources VALUES (3, 4, NULL);
INSERT INTO artists_upstream_sources VALUES (4, 4, NULL);


--
-- Data for Name: features; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO features VALUES (1, true, false, true, true, true, true, true, true, false, true, false, false, true, true, true, true, false, false, 1, true, false, true, true);
INSERT INTO features VALUES (2, true, false, true, true, true, true, true, true, true, true, false, false, true, true, true, true, false, false, 2, true, false, true, true);
INSERT INTO features VALUES (3, false, false, false, false, false, false, false, false, false, true, false, true, true, true, false, false, false, false, 5, true, false, false, false);
INSERT INTO features VALUES (4, true, true, false, true, true, true, true, true, true, true, true, true, true, true, false, false, true, false, 4, false, true, false, true);


--
-- Name: featuresets_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('featuresets_id_seq', 4, true);


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('upstream_sources_id_seq', 6, true);


--
-- PostgreSQL database dump complete
--

