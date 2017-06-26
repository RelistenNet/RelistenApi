--
-- PostgreSQL database dump
--

-- Dumped from database version 9.6.2
-- Dumped by pg_dump version 9.6.2

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SET check_function_bodies = false;
SET client_min_messages = warning;
SET row_security = off;

SET search_path = public, pg_catalog;

--
-- Data for Name: artists; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO artists VALUES (1, 'b4681cdc-4002-4521-8458-ac812f1b6d28', '2017-02-11 18:59:40.256192+01', '2017-05-19 09:29:11.908867+02', 'Lotus', 1, 'lotus');
INSERT INTO artists VALUES (5, '3797a6d0-7700-44bf-96fb-f44386bc9ab2', '2017-02-12 22:14:53.403868+01', '2017-05-19 09:29:11.908867+02', 'Widespread Panic', 1, 'wsp');
INSERT INTO artists VALUES (4, 'e01646f2-2a04-450d-8bf2-0d993082e058', '2017-02-12 22:11:14.708367+01', '2017-05-19 09:36:25.831377+02', 'Phish', 1, 'phish');
INSERT INTO artists VALUES (11, '863af5a7-805f-42b2-b436-62917b2b7b3e', '2017-05-23 13:10:41.760514+02', '2017-05-29 14:41:14.883452+02', 'Scott Metzger', 0, 'scott-metzger');
INSERT INTO artists VALUES (8, '2de4e97f-550b-4083-8fc4-83f7fd0385be', '2017-05-18 10:18:14.800757+02', '2017-05-29 14:41:21.366685+02', 'Marco Benevento', 0, 'marco');
INSERT INTO artists VALUES (12, '1002a330-c6eb-4a8e-bafd-bb8e612c3a57', '2017-05-29 14:28:33.703709+02', '2017-05-29 14:41:29.228711+02', 'Garage A Trois', 0, 'garage-a-trois');
INSERT INTO artists VALUES (13, 'f573a3b2-27a7-46e7-a7eb-7a6c9bdd7b38', '2017-05-29 14:29:32.968672+02', '2017-05-29 14:41:34.101554+02', 'G-Nome Project', 0, 'g-nome-project');
INSERT INTO artists VALUES (15, '581ba4df-7d45-412a-8e6b-3690666d06ec', '2017-05-29 14:49:07.438355+02', '2017-05-29 14:49:07.438355+02', 'Everyone Orchestra', 0, 'everyone-orchestra');
INSERT INTO artists VALUES (7, '84a69823-3d4f-4ede-b43f-17f85513181a', '2017-02-12 22:14:53.403868+01', '2017-05-30 12:23:51.157331+02', 'Joe Russo''s Almost Dead', 1, 'jrad');
INSERT INTO artists VALUES (19, 'c8a63580-9e6b-4852-bf93-c09760035e76', '2017-05-29 14:57:50.753997+02', '2017-05-30 12:24:18.259922+02', 'Bob Weir', 0, 'bob-weir');
INSERT INTO artists VALUES (14, '39e07389-bbc0-4629-9ceb-dbd0d13b85fe', '2017-05-29 14:41:00.269167+02', '2017-05-30 12:24:36.042721+02', 'Furthur', 0, 'furthur');
INSERT INTO artists VALUES (16, 'c4237bd8-b11a-4f22-b0b7-29beca7daab9', '2017-05-29 14:50:57.141476+02', '2017-05-30 12:24:46.381439+02', 'Dispatch', 0, 'dispatch');
INSERT INTO artists VALUES (17, '4e43632a-afef-4b54-a822-26311110d5c5', '2017-05-29 14:53:28.745154+02', '2017-05-30 12:24:52.022762+02', 'Disco Biscuits', 0, 'disco-biscuits');
INSERT INTO artists VALUES (18, 'e477d9c0-1f35-40f7-ad1a-b915d2523b84', '2017-05-29 14:56:27.937642+02', '2017-05-30 12:24:58.110568+02', 'Dark Star Orchestra', 0, 'dark-star-orchestra');
INSERT INTO artists VALUES (2, '94f8947c-2d9c-4519-bcf9-6d11a24ad006', '2017-02-12 22:01:12.707771+01', '2017-05-30 12:25:09.824445+02', 'Dead and Company', 1, 'dead-and-co');
INSERT INTO artists VALUES (9, '6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6', '2017-05-19 11:02:11.702744+02', '2017-05-30 12:25:13.654625+02', 'Grateful Dead', 1, 'grateful-dead');
INSERT INTO artists VALUES (20, '84a69823-3d4f-4ede-b43f-17f85513181a', '2017-06-13 18:08:52.888261+02', '2017-06-13 18:10:01.403456+02', 'Joe Russo Presents: Hooteroll? + Plus', 0, 'joe-russo-presents-hooteroll-plus');


--
-- Name: artists_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('artists_id_seq', 20, true);


--
-- Data for Name: upstream_sources; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO upstream_sources VALUES (1, 'archive.org', 'https://archive.org', 'Internet Archive', 'blah blah blah');
INSERT INTO upstream_sources VALUES (2, 'phantasytour.com', 'https://phantasytour.com', 'Phantasy Tour', 'blah blah bloop');
INSERT INTO upstream_sources VALUES (3, 'phish.in', 'https://phish.in', 'Phish.in', 'beep boop');
INSERT INTO upstream_sources VALUES (4, 'phish.net', 'https://phish.net', 'Phish.net', 'blop blorp');
INSERT INTO upstream_sources VALUES (5, 'panicstream.com', 'https://panicstream.com', 'Panic Stream', 'asdf');
INSERT INTO upstream_sources VALUES (6, 'setlist.fm', 'https://setlist.fm', 'Setlist.FM', 'doop boop');
INSERT INTO upstream_sources VALUES (7, 'jerrygarcia.com', 'https://jerrygarcia.com', ' ', ' ');


--
-- Data for Name: artists_upstream_sources; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO artists_upstream_sources VALUES (2, 1, '12');
INSERT INTO artists_upstream_sources VALUES (1, 1, 'Lotus');
INSERT INTO artists_upstream_sources VALUES (5, 5, NULL);
INSERT INTO artists_upstream_sources VALUES (6, 5, NULL);
INSERT INTO artists_upstream_sources VALUES (3, 4, '');
INSERT INTO artists_upstream_sources VALUES (4, 4, '');
INSERT INTO artists_upstream_sources VALUES (1, 11, 'ScottMetzger');
INSERT INTO artists_upstream_sources VALUES (1, 8, 'MarcoBenevento');
INSERT INTO artists_upstream_sources VALUES (1, 12, 'GarageATrois');
INSERT INTO artists_upstream_sources VALUES (1, 13, 'G-NomeProject');
INSERT INTO artists_upstream_sources VALUES (1, 15, 'EveryoneOrchestra');
INSERT INTO artists_upstream_sources VALUES (1, 7, 'JoeRussosAlmostDead');
INSERT INTO artists_upstream_sources VALUES (1, 19, 'BobWeir');
INSERT INTO artists_upstream_sources VALUES (6, 19, '');
INSERT INTO artists_upstream_sources VALUES (1, 14, 'Furthur');
INSERT INTO artists_upstream_sources VALUES (6, 14, '');
INSERT INTO artists_upstream_sources VALUES (1, 16, 'Dispatch');
INSERT INTO artists_upstream_sources VALUES (6, 16, '');
INSERT INTO artists_upstream_sources VALUES (1, 17, 'DiscoBiscuits');
INSERT INTO artists_upstream_sources VALUES (2, 17, '4');
INSERT INTO artists_upstream_sources VALUES (1, 18, 'DarkStarOrchestra');
INSERT INTO artists_upstream_sources VALUES (6, 18, '');
INSERT INTO artists_upstream_sources VALUES (1, 2, 'DeadAndCompany');
INSERT INTO artists_upstream_sources VALUES (6, 2, '');
INSERT INTO artists_upstream_sources VALUES (1, 9, 'GratefulDead');
INSERT INTO artists_upstream_sources VALUES (7, 9, 'grateful_dead');
INSERT INTO artists_upstream_sources VALUES (1, 20, 'JoeRussoPresentsHooterollPlus');


--
-- Data for Name: features; Type: TABLE DATA; Schema: public; Owner: -
--

INSERT INTO features VALUES (3, false, false, false, false, false, true, false, false, false, true, false, true, true, true, false, false, false, false, 5, true, false, false, false, false);
INSERT INTO features VALUES (1, true, false, true, true, true, true, true, true, false, true, false, false, true, true, true, true, false, false, 1, true, false, true, true, true);
INSERT INTO features VALUES (4, true, true, false, true, true, true, true, true, true, true, true, true, true, true, false, false, true, false, 4, true, true, false, true, false);
INSERT INTO features VALUES (8, true, false, true, true, true, false, true, true, false, false, true, false, true, true, true, true, false, false, 11, true, false, true, true, true);
INSERT INTO features VALUES (5, true, false, true, true, true, false, true, true, false, false, true, false, true, true, true, true, false, false, 8, true, false, true, true, true);
INSERT INTO features VALUES (9, true, false, true, true, true, false, true, true, false, false, true, false, true, true, true, true, false, false, 12, true, false, true, true, true);
INSERT INTO features VALUES (10, true, false, true, true, true, false, true, true, false, false, true, false, true, true, true, true, false, false, 13, true, false, true, true, true);
INSERT INTO features VALUES (12, true, false, true, true, true, false, true, true, false, false, true, false, true, true, true, true, false, false, 15, true, false, true, true, true);
INSERT INTO features VALUES (7, true, false, true, true, true, false, true, true, false, false, true, false, false, true, true, true, false, false, 7, true, false, true, true, true);
INSERT INTO features VALUES (16, true, false, true, true, true, true, true, true, false, true, false, true, true, true, true, true, false, false, 19, true, false, true, true, true);
INSERT INTO features VALUES (11, true, false, true, true, true, true, true, true, false, true, false, true, true, true, true, true, false, false, 14, true, false, true, true, true);
INSERT INTO features VALUES (13, true, false, true, true, true, true, true, true, false, true, false, true, true, true, true, true, false, false, 16, true, false, true, true, true);
INSERT INTO features VALUES (14, true, false, true, true, true, true, true, true, false, true, false, false, true, true, true, true, false, false, 17, true, false, true, true, true);
INSERT INTO features VALUES (15, true, false, true, true, true, true, true, true, false, true, false, true, true, true, true, true, false, false, 18, true, false, true, true, true);
INSERT INTO features VALUES (2, true, false, true, true, true, true, true, true, false, true, false, true, true, true, true, true, false, false, 2, true, false, true, true, true);
INSERT INTO features VALUES (6, true, false, true, true, true, true, true, true, false, true, false, false, true, true, true, true, false, false, 9, true, false, true, true, true);
INSERT INTO features VALUES (17, true, false, true, true, true, false, true, true, false, false, true, false, false, true, true, true, false, false, 20, true, false, true, true, true);


--
-- Name: featuresets_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('featuresets_id_seq', 17, true);


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('upstream_sources_id_seq', 7, true);


--
-- PostgreSQL database dump complete
--

