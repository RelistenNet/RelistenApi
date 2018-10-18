using SimpleMigrations;

namespace Migrations
{
    [Migration(2, "Baseline Artist Data")]
    public class BaselineData : Migration
    {
        protected override void Up()
        {
            // generated with:
            // docker exec -i 1a9d276c0d02 sh -c "pg_dump -U relisten --no-owner -t artists -t artists_upstream_sources -t features -t upstream_sources  -a relisten_db" > data.sql
            Execute(@"
--
-- Data for Name: artists; Type: TABLE DATA; Schema: public; Owner: -
--

COPY artists (id, musicbrainz_id, created_at, updated_at, name, featured, slug, sort_name, uuid) FROM stdin;
216		2018-07-20 18:10:24.312975+00	2018-07-20 18:10:24.312975+00	The Higgs	0	the-higgs	Higgs	46d147d9-7b12-8125-80c3-89127ff14686
217	218b1df5-0aa5-402f-a3a8-eed2b751baea	2018-07-21 18:57:54.544781+00	2018-07-22 07:06:48.751682+00	Circles Around The Sun	0	circles-around-the-sun	Circles Around Sun	ab171e81-b06c-101d-a65e-28f6c535d21d
218		2018-07-22 18:07:05.485406+00	2018-07-22 18:07:05.485406+00	Karina Rykman	0	karina-rykman	Karina Rykman	92dc1b18-bf35-95a4-027a-23129b890063
219		2018-07-22 18:14:32.631005+00	2018-07-22 18:15:04.431583+00	Charlie Hunter	0	charlie-hunter	Charlie Hunter	d8ed91f1-1e39-5190-a46f-31a6b70a0325
202	640db492-34c4-47df-be14-96e2cd4b9fe4	2017-08-30 13:05:00.502345+00	2017-08-30 13:05:00.502345+00	Billy Strings	0	billy-strings	Billy Strings	52328c75-6fc7-20ae-f53f-3817ff3310d5
221		2018-08-07 15:57:50.365341+00	2018-08-07 15:57:50.365341+00	North Mississippi Allstars	0	north-mississippi-allstars	North Mississippi Allstars	3b53fb37-e43a-7339-53e1-a047a6a6bde5
222		2018-08-22 18:26:35.70686+00	2018-08-22 18:26:35.70686+00	Eric Krasno	0	eric-krasno	Eric Krasno	d1ceefe9-a06d-8af9-1bd1-bf0f023ed5ef
203	2a994741-42ca-4251-8400-7b6fe09ecf3d	2017-08-30 13:05:31.920951+00	2017-08-30 13:05:31.920951+00	Kitchen Dwellers	0	kitchen-dwellers	Kitchen Dwellers	415fb2ca-9301-e8ce-d48b-495c64da18f1
223		2018-09-13 01:13:28.790464+00	2018-09-13 01:13:28.790464+00	Chris Harford	0	chris-harford	Chris Harford	25f16d4d-9d51-1c25-55ce-fd10649243cc
224	ffc35dde-fc40-4009-bb41-a9aedfd2a27c	2018-10-12 05:41:05.510213+00	2018-10-12 05:41:05.510213+00	Animal Liberation Orchestra	0	alo	Animal Liberation Orchestra	a11b8647-0f4a-d371-ed9c-537182648239
229	a1a053b3-cad1-43c2-8234-29367a9a42a3	2018-10-12 05:45:05.519165+00	2018-10-12 05:45:05.519165+00	Jackie Greene	0	jackie-greene	Jackie Greene	d10fcabe-485c-65d9-854d-4e69dd906ac9
204	ec8e3cea-69f0-4ff3-b42c-74937d336334	2017-08-30 13:05:53.295569+00	2017-08-30 13:05:53.295569+00	Pigeons Playing Ping Pong	0	pigeons-playing-ping-pong	Pigeons Playing Ping Pong	e265ba0f-90d8-d150-b0a2-0c65518593d3
237		2018-10-12 05:56:58.78142+00	2018-10-12 05:56:58.78142+00	Grampas Grass	0	grampas-grass	Grampas Grass	bea4f27a-b121-221a-5cb1-96b83b2e3df2
205	8d418822-4d8b-4c44-947e-d897eb3be8c4	2017-10-04 15:23:37.785438+00	2017-10-04 15:23:37.785438+00	Moon Taxi	0	moon-taxi	Moon Taxi	c5a4da93-1740-2aec-4e10-e0ddada02748
206	8165c0ff-1d9c-4045-aaef-9561dee1ba21	2017-12-23 20:26:33.396492+00	2017-12-23 20:27:10.352128+00	Nathan Moore	0	nathan-moore	Nathan Moore	f13bdeea-c86d-a1e7-54b6-82689325f265
207	326aeeff-ef3f-4409-b166-916d23bc1169	2017-12-23 20:27:45.275384+00	2017-12-23 20:27:45.275384+00	Surprise Me Mr. Davis	0	surprise-me-mr-davis	Surprise Me Mr. Davis	3ec0d937-cb64-b9d5-278a-c3582e502580
208	7daad2f5-3398-4954-9c43-e88d060b291b	2018-01-07 17:24:43.130931+00	2018-01-07 17:37:18.228019+00	Trevor Garrod	0	trevor-garrod	Trevor Garrod	df4934ac-1b10-de2d-f392-df6efb2e2447
209	22ec9a8c-d82a-4ac4-928f-ea5f4c1c27ea	2018-01-07 23:31:04.578906+00	2018-01-07 23:31:04.578906+00	Formula 5	0	formula-5	Formula 5	51c5af72-6576-bf4c-fa2e-8ee9ca2c5d73
210	799388a4-f668-4f96-bbc1-6b00e0972f6b	2018-01-13 20:37:38.435924+00	2018-01-13 20:37:38.435924+00	Perpetual Groove	0	perpetual-groove	Perpetual Groove	1bf2c5d9-9773-7da0-d7b7-e876f4cd3c55
211	a0fa7565-82ff-4744-b932-633b7a4fe249	2018-03-23 11:32:07.242408+00	2018-03-23 11:32:07.242408+00	Ghost Light	0	ghost-light	Ghost Light	b4007cfe-3144-0131-84c6-9cddbf10e67a
201	16aae9fa-d8f7-4e2c-a907-44aee1ad67fc	2017-07-12 12:15:01.348308+00	2018-04-03 06:59:56.655251+00	O.A.R.	0	oar	O.A.R.	839ba0d8-a9b8-bcc5-7646-41186b1803b1
163	3826a6e0-9ea5-4007-941c-25b9dd943981	2017-07-12 09:10:36.240117+00	2018-04-03 07:00:05.957771+00	Umphrey's McGee	1	umphreys	Umphrey's McGee	42d715b0-25c1-e268-8a9d-d1df3b507436
212	f6f33c55-8156-482b-a071-fc58824e9551	2018-05-13 16:16:57.100396+00	2018-05-13 16:16:57.100396+00	ekoostik hookah	0	ekoostik-hookah	ekoostik hookah	0dedb77f-33e8-2fb0-ed05-094f6c5e173e
213		2018-06-11 18:40:01.821259+00	2018-06-11 18:40:01.821259+00	Guitarmageddon	0	guitarmageddon	Guitarmageddon	70605cd9-281f-34f3-7e13-88678286009c
214	4fe8f273-df8c-4fea-b0a2-30022452730c	2018-07-15 17:06:33.885325+00	2018-07-15 17:06:33.885325+00	Dave Harrington	0	dave-harrington	Dave Harrington	f682d483-2b9f-e73d-8198-d6b643d728cd
115	3b572f79-2eac-476a-b41e-cf85d6bddbd0	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The New Mastersounds	0	new-mastersounds	New Mastersounds	357ba449-e983-3bac-8839-54e54b11fe0c
116	3ccaedfa-780e-4b0c-bbe8-915a8e5e1ab9	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Zebu	0	zebu	Zebu	7887bf1c-3a60-bcb3-2f5d-aedf6781f6a1
117	b3a49700-729b-4d5d-87ad-2bb6ae6322c2	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Zero	0	zero	Zero	3ac2708c-52e2-83e8-7134-1f3307532453
220		2018-07-22 18:14:52.511631+00	2018-07-22 18:14:52.511631+00	Stanton Moore	0	stanton-moore	Stanton Moore	0ba95bf2-173d-58d7-39d4-f35e95a9219e
225	59bc9caa-5700-4c77-9ab7-f089aa7a357f	2018-10-12 05:41:55.348087+00	2018-10-12 05:41:55.348087+00	Dawes	0	dawes	Dawes	850cb973-6c10-ec6f-cc1d-ec7a361e215a
230	ac53c83c-c969-477f-9564-769e1ed80519	2018-10-12 05:45:42.500302+00	2018-10-12 05:45:42.500302+00	Josh Ritter	0	josh-ritter	Josh Ritter	68b28670-b21d-300d-f45d-28ecbe00068c
234	d64c7ecc-88a1-42b1-962d-a51e3bc061bc	2018-10-12 05:53:23.275051+00	2018-10-12 05:53:23.275051+00	Rumpke Mountain Boys	0	rumpke-mountain-boys	Rumpke Mountain Boys	f8290300-bbe9-0aa2-8d2b-cdd81b0eb8d9
235	26645c31-17d5-46a2-ba4c-8740e0aaaa84	2018-10-12 05:54:19.83107+00	2018-10-12 05:54:19.83107+00	Cubensis	0	cubensis	Cubensis	4f5c89ac-603c-87ff-adc5-d55c14c5717b
238	a4c13824-64dd-49bb-bada-2f29acbc33e2	2018-10-12 05:58:14.766391+00	2018-10-12 05:58:14.766391+00	The Other Ones	0	the-other-ones	Other Ones	93748aed-eea5-b4e7-14d5-1a7551718c87
118	692af82d-1056-4c1f-8666-0c6f6fa7779b	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Brothers Past	0	brothers-past	Brothers Past	121ab16a-aebb-d0d5-53b4-14d6c795ffc2
119	809c8576-cbcb-4dcc-b19c-2c3277c5ac5a	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Percy Hill	0	percy-hill	Percy Hill	8bd3a60b-1c6b-fb6c-84e2-3f0331e1c8b6
120	734d083f-eb2b-4c01-a739-cb8f5a361dab	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Jerry Joseph and the Jackmormons	0	jerry-joseph	Jerry Joseph and the Jackmormons	ea37428a-4a83-cb51-cee3-b54eef9b491b
121	76e1bc53-ec85-4266-8b8b-9fde2fee92da	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Steve Kimock Band	0	steve-kimock-band	Steve Kimock Band	be58d3a9-307a-2bd9-591e-22943dd6802e
122	5ac47e00-6cf1-4d35-b544-a28bb1b79b89	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Club d'Elf	0	club-d-elf	Club d'Elf	fa14219c-1c68-24de-1a27-d5fd6d0904ab
123	39c0c0ab-528f-4484-b25b-3a553b96f83c	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Assembly of Dust	0	assembly-of-dust	Assembly of Dust	d32de3cc-9557-a267-656e-ca53816a5df8
124	e84ddc5c-06e3-48fc-a8f1-fa0cf430d6b5	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Cabinet	0	cabinet	Cabinet	75963cb2-d308-895a-beaf-d1f42a87ddcd
125	a916ce0a-0ab7-4b2f-b169-2166b746e31e	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Donna the Buffalo	0	donna-the-buffalo	Donna the Buffalo	7ff85ff1-ba62-3893-6bff-fdaf9af00b8a
126	8e86eeb3-c214-422d-9e00-d6bbb099f510	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Raq	0	raq	Raq	05efb411-d681-a4eb-6f9f-4ac58acf9362
127	75f27492-3018-4b1e-aa04-60c31059a5c5	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Max Creek	0	max-creek	Max Creek	641203c8-bc68-c562-14cf-e24696824b44
128	9860c682-823e-41b0-82cd-f994b13b0e95	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Tea Leaf Green	0	tea-leaf-green	Tea Leaf Green	93d0c6ac-8ca6-d38b-35db-ca20de369301
129	b07354af-e6d0-4cf2-af83-a1679606f34a	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	KVHW	0	kvhw	KVHW	8a5e5c4f-1522-cb9c-331b-be76b2469f69
130	4aa86473-e786-4c6a-8e4d-01e0028522ec	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The New Deal	0	the-new-deal	New Deal	7c13f3a1-8f04-279d-dfce-20652a63920c
131	e1a889b7-5e45-447f-baa0-b9c6cbc0d39f	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Bela Fleck and the Flecktones	0	bela-fleck	Bela Fleck and the Flecktones	d98ca9c9-373d-4e46-c30d-e4694a6ec89f
132	43e088bd-2520-4a84-bb8e-827c86d2cf84	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Infamous Stringdusters	0	stringdusters	Infamous Stringdusters	272fe100-2535-309f-c222-2d8b4a87cd2c
133	c02617f9-2408-4434-8588-d06a123b73c0	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Fruition	0	fruition	Fruition	90bd276a-e36e-4893-c0fc-c21d4bc13ffa
134	199596a3-a1af-49f8-8795-259eff8461fb	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Greensky Bluegrass	0	greensky-bluegrass	Greensky Bluegrass	bc0b2d5f-a167-00ac-cd5e-b07c4d558f6d
226	7e5a0c3f-339a-4e5d-aed0-1946112b4a66	2018-10-12 05:42:40.096796+00	2018-10-12 05:42:40.096796+00	Del McCoury Band	0	del-mccoury-band	Del McCoury Band	0dd5ee15-6667-cd2a-097c-1ce52336b00c
231	15f8b799-4c43-4f39-8ceb-9cdce7051b51	2018-10-12 05:47:54.535983+00	2018-10-12 05:47:54.535983+00	Marcus King Band	0	marcus-king-band	Marcus King Band	3851e0ba-0537-3ceb-395f-fc93410d6ef0
2	94f8947c-2d9c-4519-bcf9-6d11a24ad006	2017-02-12 21:01:12.707771+00	2018-04-03 06:59:26.975918+00	Dead and Company	0	dead-and-co	Dead and Company	75f6d676-f424-1b2b-8055-a3b6de42db04
1	b4681cdc-4002-4521-8458-ac812f1b6d28	2017-02-11 17:59:40.256192+00	2018-04-03 06:59:39.054047+00	Lotus	0	lotus	Lotus	7ae9efe1-e23b-714c-68fc-f7c3748b1f5d
215		2018-07-15 17:06:58.498491+00	2018-07-15 17:06:58.498491+00	Boyfriends	0	boyfriends	Boyfriends	ae37219c-d118-87f1-0619-643bb9f57621
5	3797a6d0-7700-44bf-96fb-f44386bc9ab2	2017-02-12 21:14:53.403868+00	2017-05-19 07:29:11.908867+00	Widespread Panic	1	wsp	Widespread Panic	ce1dd669-3f3b-0186-ab59-e8300e92db7f
11	863af5a7-805f-42b2-b436-62917b2b7b3e	2017-05-23 11:10:41.760514+00	2017-05-29 12:41:14.883452+00	Scott Metzger	0	scott-metzger	Scott Metzger	33687075-ccd8-0053-5db5-c86586c0db5e
8	2de4e97f-550b-4083-8fc4-83f7fd0385be	2017-05-18 08:18:14.800757+00	2017-05-29 12:41:21.366685+00	Marco Benevento	0	marco	Marco Benevento	8389db00-bf5c-1e96-e500-00ff584b7ee3
12	1002a330-c6eb-4a8e-bafd-bb8e612c3a57	2017-05-29 12:28:33.703709+00	2017-05-29 12:41:29.228711+00	Garage A Trois	0	garage-a-trois	Garage A Trois	e1b91c94-5e53-06a1-47b0-b2fe8a9fd714
13	f573a3b2-27a7-46e7-a7eb-7a6c9bdd7b38	2017-05-29 12:29:32.968672+00	2017-05-29 12:41:34.101554+00	G-Nome Project	0	g-nome-project	G-Nome Project	a12fe7e5-ebda-4e3a-2029-17ae43d4de2c
15	581ba4df-7d45-412a-8e6b-3690666d06ec	2017-05-29 12:49:07.438355+00	2017-05-29 12:49:07.438355+00	Everyone Orchestra	0	everyone-orchestra	Everyone Orchestra	792515f5-c7e9-a598-cfc4-5610142030a5
7	84a69823-3d4f-4ede-b43f-17f85513181a	2017-02-12 21:14:53.403868+00	2017-05-30 10:23:51.157331+00	Joe Russo's Almost Dead	1	jrad	Joe Russo's Almost Dead	f46c0236-ac71-2173-c6b0-afa4e2badbef
20	84a69823-3d4f-4ede-b43f-17f85513181a	2017-06-13 16:08:52.888261+00	2017-06-13 16:10:01.403456+00	Joe Russo Presents: Hooteroll? + Plus	0	joe-russo-presents-hooteroll-plus	Joe Russo Presents: Hooteroll? + Plus	15af8149-a173-0ff4-54f5-bfdd1073fe94
9	6faa7ca7-0d99-4a5e-bfa6-1fd5037520c6	2017-05-19 09:02:11.702744+00	2017-06-26 13:43:50.14246+00	Grateful Dead	1	grateful-dead	Grateful Dead	77a58ff9-2e01-c59c-b8eb-cff106049b72
19	c8a63580-9e6b-4852-bf93-c09760035e76	2017-05-29 12:57:50.753997+00	2017-06-26 13:44:26.290822+00	Bob Weir	0	bob-weir	Bob Weir	49c025d3-85bf-4309-0255-adc5a816b8da
18	e477d9c0-1f35-40f7-ad1a-b915d2523b84	2017-05-29 12:56:27.937642+00	2017-06-26 13:44:30.568154+00	Dark Star Orchestra	0	dark-star-orchestra	Dark Star Orchestra	be53d0ee-4abd-0f02-eabe-e0c40103160e
17	4e43632a-afef-4b54-a822-26311110d5c5	2017-05-29 12:53:28.745154+00	2017-06-26 13:44:34.52177+00	Disco Biscuits	0	disco-biscuits	Disco Biscuits	4a8dbebe-80be-f077-1755-60f7cd4b897f
16	c4237bd8-b11a-4f22-b0b7-29beca7daab9	2017-05-29 12:50:57.141476+00	2017-06-26 13:44:37.801214+00	Dispatch	0	dispatch	Dispatch	d4a8ed4d-9296-9656-b7dc-5e9f3975e0d2
14	39e07389-bbc0-4629-9ceb-dbd0d13b85fe	2017-05-29 12:41:00.269167+00	2017-06-26 13:44:45.966478+00	Furthur	0	furthur	Furthur	04735701-ff23-9ec4-93fa-f503246310ec
4	e01646f2-2a04-450d-8bf2-0d993082e058	2017-02-12 21:11:14.708367+00	2017-07-03 13:39:47.500192+00	Phish	1	phish	Phish	ca53d281-0041-ae33-a050-c87702d93b0c
111	637b7f25-0b6c-464f-80e2-3b0ae7216cb9	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	EGi	0	egi	EGi	a94fa90f-2dac-c084-3943-cc738785b28e
112		2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	John Kadlecik	0	john-kadlecik	John Kadlecik	6faf7071-5c9a-1c9a-dde0-5a2576a5d5bb
135	492a80cc-792b-4491-80f9-695ef1233656	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	God Street Wine	0	god-street-wine	God Street Wine	57253875-288e-ff8a-b621-05bbc29ab244
136	507f2e33-8503-45df-9c93-e255821c520a	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Heavy Pets	0	the-heavy-pets	Heavy Pets	f26d10dc-beb9-1e72-7ce7-809228b0f79c
137	5df34416-d6dd-4692-b92d-86f81d724b9d	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Aqueous	0	aqueous	Aqueous	07ae6827-471f-19a6-d4f5-1626914985dc
138	b2e2abfa-fb1e-4be0-b500-56c4584f41cd	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Railroad Earth	0	railroad-earth	Railroad Earth	b349ebee-aad5-6c22-6563-f17ee22518df
139	d0e17673-a49c-4131-a731-329a1a3994d2	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	American Babies	0	american-babies	American Babies	bf2f29db-8681-de2f-5001-3804fd860b87
140	cf9655a8-ce1e-414e-9843-de1c4a60830d	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Jauntee	0	jauntee	Jauntee	f13fc92b-9377-6366-cade-89a99e5c8038
141	f17f5164-ef7a-4ad2-97a3-3400162fbb7b	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Steve Kimock	0	steve-kimock	Steve Kimock	c5d1b96f-b86e-dcf1-625f-06e022de2961
142	5cf454bc-3be0-47ba-9d0b-1e53da631a4e	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Twiddle	0	twiddle	Twiddle	8e3915fa-dcf3-4924-3072-eea459563b3e
143	04fc77df-698e-4060-8ae5-766fb0cba9ce	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Motet	0	the-motet	Motet	748b97d7-0613-5126-8a15-bc68f8896a17
144	a1cf1713-f05d-4a43-8e0f-0feef3f38ff3	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Strangefolk	0	strangefolk	Strangefolk	5d5eaf8c-343f-a178-1b22-f3ea63234a48
145	536a68e9-049b-4d9f-a0d1-2630c92acc4b	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Keller Williams	0	keller-williams	Keller Williams	3ab245b3-3be0-4f05-a7c9-247dfa986899
146	ffb7c323-5113-4bb0-a5f7-5b657eec4083	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Phil Lesh and Friends	0	phil-lesh	Phil Lesh and Friends	06b06e15-a5b0-65d1-e182-b85781724c64
147	564acb69-9a29-481e-8e6a-3557211d2f74	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Aquarium Rescue Unit	0	aru	Aquarium Rescue Unit	a0957e97-5da5-5816-0659-a61d8259e567
113	fbed8f26-b8ef-4cc0-832e-9f32a1588432	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Kung Fu	0	kung-fu	Kung Fu	3532d76b-9033-8793-bfca-f262ad7850b2
114		2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Shafty	0	shafty	Shafty	ae07cd02-55cc-cff9-c9ab-d7344fb39a29
199	0c751690-c784-4a4f-b1e4-c1de27d47581	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Animal Collective	0	animal-collective	Animal Collective	2f78c0aa-1b48-effc-7d16-8fbf2a145dbb
200	233fc3f3-6de2-465c-985e-e721dbabbace	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Fugazi	0	fugazi	Fugazi	28dc0b55-a2c8-481e-7542-45f7e9e53038
236		2018-10-12 05:56:01.727365+00	2018-10-12 05:56:01.727365+00	GrooveSession	0	groovesession	GrooveSession	14fafd2c-147f-3836-8437-1f2736a4b4cf
239	1b4a9142-d67b-469f-b992-1469d2e6bf25	2018-10-12 05:59:06.726155+00	2018-10-12 05:59:06.726155+00	Papadosio	0	papadosio	Papadosio	45dfe187-bdc8-7c4b-3bf4-0c2aea8e5652
227	448913ad-9a62-4278-abbe-07b35cae6c73	2018-10-12 05:43:16.612904+00	2018-10-12 05:43:16.612904+00	Greyboy Allstars	0	greyboy-allstars	Greyboy Allstars	9f456947-71b7-f9fa-44f0-f0875b0fae44
232		2018-10-12 05:49:13.614112+00	2018-10-12 05:49:13.614112+00	Dub Apocalypse	0	dub-apocalypse	Dub Apocalypse	c8a9f654-d5f3-0b91-f5c1-0456bf519172
148	9abcbe69-fa83-4d34-8e34-ffc8c9876685	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Dead	0	the-dead	Dead	f7db8c26-cbec-b135-8575-171b1f6795c0
149	d73c9a5d-5d7d-47ec-b15a-a924a1a271c4	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	John Popper	0	john-popper	John Popper	0e4070b3-545a-3e9f-79c7-9fb3268367a3
150	3020be1d-3c41-4e42-911b-ca5e96489300	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Leftover Salmon	0	leftover-salmon	Leftover Salmon	feadbbf7-a021-1566-41d9-fe08d0e8a2aa
151	cff95140-6d57-498a-8834-10eb72865b29	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	String Cheese Incident	0	sci	String Cheese Incident	a42d2b4d-5b39-629c-2826-c4d7afc46a17
152	ee4dd09a-864b-406d-baa1-9932e36c6de5	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Werks	0	the-werks	Werks	ea01733f-3e1a-5ae8-9077-15ee86a8ac76
153	76f7803b-735e-4c21-aa1a-dc5896818434	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Big Wu	0	the-big-wu	Big Wu	ade7b451-f4c3-4548-d979-4c1b95ed4d66
154	ac07f9b0-b4cf-43c7-b6bf-ed82f07105b9	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Breakfast	0	the-breakfast	Breakfast	73650d5d-f660-56ad-acfc-cb4732067357
155	e88313e2-22f6-4f6d-9656-6d2ad20ea415	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Lettuce	0	lettuce	Lettuce	3259ab8b-ad08-98ec-e795-13dd2b0d4b0e
156	76fda896-7c2a-4e5e-a45b-d40acfb2080c	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Yonder Mountain String Band	0	yonder	Yonder Mountain String Band	789b7c4f-26d9-6aa5-a935-50204e5ec023
157	8d07ac81-0b49-4ec3-9402-2b8b479649a2	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Sound Tribe Sector 9	0	sts9	Sound Tribe Sector 9	6a660dae-12ca-3202-ec99-9a2a688ecce0
158	a4ad4581-721e-4123-aa3e-15b36490cf0f	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Spafford	0	spafford	Spafford	11244359-0aa6-0dce-05ad-0805e7969bde
159	4bd3fb40-1c6f-4056-a0ee-8427685586fc	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Radiators	0	radiators	Radiators	46c7d495-b2c3-601c-84b8-499c6fb7fd45
160	e68abd50-e5ec-4b5c-87dd-0fd0437cafd1	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Bernie Worrell Orchestra	0	bernie-worrell	Bernie Worrell Orchestra	c873da1b-6cff-06a8-d52e-e88d23c078af
161	5fab339d-5dd4-42b0-8d70-496a4493ed59	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	moe.	0	moe	moe.	4ff36bf3-e959-3617-45d4-07fc7a9d6278
162	6f2adbd6-7685-47a9-932e-b1e450a561a3	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	TAUK	0	tauk	TAUK	d3048777-b6e1-580c-cfd5-14c5b87b80ad
164	c80f38a6-9980-485d-997c-5c1a9cbd0d64	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Ryan Adams	0	ryan-adams	Ryan Adams	2e38a274-2d4d-787f-bfcf-73fb2c79cc4a
165	fa480a57-80ee-44eb-b138-9a5fae8af5cb	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Jazz Mandolin Project	0	jazz-mandolin-project	Jazz Mandolin Project	d754cf93-d8f9-6616-9ca7-38832c49e09e
166	e33e1ccf-a3b9-4449-a66a-0091e8f55a60	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Tedeschi Trucks Band	0	tedeschi-trucks	Tedeschi Trucks Band	4f222dcb-6fe7-6ab3-5571-0faab0be18c4
167	73c5a9bc-3e0d-45e6-a981-ba67435e1f58	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Ratdog	0	ratdog	Ratdog	4eda3cf1-0364-9ddc-c486-2cb902e01bee
168	a4ba3c8d-468c-4b99-b732-91b3e737d19d	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Holly Bowling	0	holly-bowling	Holly Bowling	73dfe1df-337d-b5e2-2384-84cdc9e5799b
169	1f8c1417-ddf7-41f0-9f54-2d5b847c6a80	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Dopapod	0	dopapod	Dopapod	c58ce0bc-b581-f2b6-60d1-172302410e97
170	ba0d6274-db14-4ef5-b28d-657ebde1a396	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Smashing Pumpkins	0	smashing-pumpkins	Smashing Pumpkins	503e528f-20e4-cbb9-cac9-bdfbd7a25634
171	bba78c6c-21b9-42fd-88e8-d47a2ee1764b	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Benevento Russo Duo	0	duo	Benevento Russo Duo	13c0e5be-6bf2-5766-9cf6-813c28d8fbaa
172	e8993e9d-9313-4447-ad23-791459a3790d	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Matisyahu	0	matisyahu	Matisyahu	d88e1f6a-bd76-3259-3869-7a950aae37d5
173	8eae1e0a-1696-4532-9e3c-0a072217ef4c	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Drive-By Truckers	0	drive-by-truckers	Drive-By Truckers	be72d897-0088-8698-6aa3-eda658196a9d
228	2c64df55-32ed-4505-b74a-5ba178be7ee7	2018-10-12 05:44:19.094489+00	2018-10-12 05:44:19.094489+00	Hot Buttered Rum	0	hot-buttered-rum	Hot Buttered Rum	a4b8788c-0bbb-2b5b-e3b1-72484d3723fb
233	731f3ac1-3af9-473b-9aa5-72fa6b193f6d	2018-10-12 05:52:30.970278+00	2018-10-12 05:52:30.970278+00	Garaj Mahal	0	garaj-mahal	Garaj Mahal	3491edd1-5abb-8ebe-a32b-f783f57a45f3
174	9501a1d0-04a2-47ee-828d-9d016c315bb7	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Grace Potter	0	grace-potter	Grace Potter	120668b4-7004-14e1-3cbd-04b3cccf6b8f
175	c0eee88b-47f2-4cd2-ac48-a045e902a432	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Ween	0	ween	Ween	51346493-51c1-a85e-814a-45330c2ddbb4
176	6b28ecf0-94e6-48bb-aa2a-5ede325b675b	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Blues Traveler	0	blues-traveler	Blues Traveler	f2175b7e-776a-ec5a-a69b-b3780ebf573e
177	6cbe1e63-5895-4168-ac7e-f0d2836ba0c1	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Guster	0	guster	Guster	ddec6d9f-38a1-51a4-f8b0-b9b886bf0270
178	9b106beb-12b5-4525-8025-42e295a2b90a	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Little Feat	0	little-feat	Little Feat	d6b5b54d-6748-8939-143b-c34bc47ff919
179	ea5883b7-68ce-48b3-b115-61746ea53b8c	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	My Morning Jacket	0	mmj	My Morning Jacket	921b420f-b2ff-3b98-39ea-31c99ce22155
180	3648db01-b29d-4ab9-835c-83f6a5068fe4	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Godspeed You Black Emperor!	0	godspeed-you-black-emperor	Godspeed You Black Emperor!	57efa37f-548b-d78b-e9fb-21a8065e298d
181	7d0e8067-10b9-4069-95dc-1110a0fbb877	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Vulfpeck	0	vulfpeck	Vulfpeck	46fa30b8-8427-4284-c041-71866d777dc7
182	ff6e677f-91dd-4986-a174-8db0474b1799	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Jack Johnson	0	jack-johnson	Jack Johnson	f856bb43-f50c-490b-342b-fedd8fa15837
183	03ad1736-b7c9-412a-b442-82536d63a5c4	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Elliott Smith	0	elliott-smith	Elliott Smith	432223b4-ce16-f51a-d38e-deb7e4401e85
184	dbf7c761-e332-467b-b4d9-aafe06bbcf8f	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	G. Love and Special Sauce	0	g-love	G. Love and Special Sauce	e1269df5-3786-4f14-d31f-1213b74d8f9f
185	cf9655a8-ce1e-414e-9843-de1c4a60830d	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The HippoCampus	0	the-hippocampus	HippoCampus	dc375d8b-5dbf-f5c1-7c3d-362b2cd98f85
186	081b133e-ce74-42ba-92c1-c18234acb532	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Andrew Bird	0	andrew-bird	Andrew Bird	dcdde54f-dcf8-5ad3-358b-594d9c4e539f
187	0f11d99e-88ba-48d2-b652-4ebe04c52d11	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Akron/Family	0	akron-family	Akron/Family	732d968c-a550-d459-3034-3759d2cefc69
188	d700b3f5-45af-4d02-95ed-57d301bda93e	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Mogwai	0	mogwai	Mogwai	8a50a0e4-9ea5-ddae-4831-341fade9c5e4
189	87b9b3b8-ab93-426c-a200-4012d667a626	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The War on Drugs	0	war-on-drugs	War on Drugs	1e5d9f3b-9163-a8cf-6fd9-5ef529dda753
190	970fb29f-e288-403e-a388-d2a7889bfa47	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Warren Zevon	0	warren-zevon	Warren Zevon	76735c77-90ca-dac8-bc44-92c7e6d69698
191	84eac621-1c5a-49a1-9500-555099c6e184	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Spoon	0	spoon	Spoon	0048a309-7914-6680-6710-1fc478ec82dc
192	148ddea2-6839-4354-8e2c-5dfadf136b7f	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Tenacious D	0	tenacious-d	Tenacious D	82e6c7ab-38a2-d9b9-0c74-7de7bf9cedf3
193	d4a9be59-13e5-481b-8c68-833c5c1fd458	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Matt Pond PA	0	matt-pond-pa	Matt Pond PA	b1e8a8b4-184e-cdd1-c439-fa305228dc4c
194	ff9deaae-da4f-42b7-a19e-36fedd3fc706	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Ben Kweller	0	ben-kweller	Ben Kweller	a45348ac-3d0f-fae8-3a82-26d81015f948
195	42e49814-6058-431c-80e2-a3aab424060d	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	White Denim	0	white-denim	White Denim	3244bdd2-b3a8-9666-7a65-5be4b8ca0012
196	97b1142f-c71e-4971-8736-4a8ceaf6b4c3	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Decemberists	0	the-decemberists	Decemberists	39b726ed-590e-5290-490a-f5ce6f670ed4
197	31095622-5a1e-4f22-8ad1-b08eb6255f37	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	Ted Leo and the Pharmacists	0	ted-leo	Ted Leo and the Pharmacists	1806b92a-7eca-92dd-0b68-5d60bb8e0e80
198	b7834ebd-64ae-46c3-a930-2d3a52ee743a	2017-07-12 09:10:36.240117+00	2017-07-12 09:10:36.240117+00	The Walkmen	0	the-walkmen	Walkmen	e63e6e0a-e7e3-ff7e-dba7-2e87173a08b2
\.


--
-- Name: artists_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('artists_id_seq', 239, true);


--
-- Data for Name: upstream_sources; Type: TABLE DATA; Schema: public; Owner: -
--

COPY upstream_sources (id, name, url, description, credit_line) FROM stdin;
2	phantasytour.com	https://phantasytour.com	Phantasy Tour	blah blah bloop
5	panicstream.com	https://panicstream.com	Panic Stream	asdf
7	jerrygarcia.com	https://jerrygarcia.com	 jerrygarcia.com has detailed setlist information for all the bands that he was a part. Relisten uses this information to match venues and songs to recordings.	 
6	setlist.fm	https://setlist.fm	Source: concert setlists on setlist.fm. setlist.fm is a free wiki-like service to collect and share setlists. Relisten uses this information to match venues and songs to recordings.	doop boop
1	archive.org	https://archive.org	The Live Music Archive, part of the Internet Archive, is a collection of over 170,000 concert recordings in lossless audio formats. The songs are also downloadable or playable in lossy formats such as Ogg Vorbis or MP3. Archive.org is the one hosting the music you are listening to. Reviews and ratings also come from archive.org.	blah blah blah
4	phish.net	https://phish.net	Phish.net is a non-commercial project run by Phish fans and for Phish fans under the auspices of the all-volunteer, non-profit Mockingbird Foundation.\n\nThis project serves to compile, preserve, and protect encyclopedic information about Phish and their music.\n\nRelisten uses information from phish.net to show Show ratings and reviews.	blop blorp
3	phish.in	https://phish.in	Phish.in provides a JSON API for programmatic access to content. It is a fan run site to host Phish audience recordings alongside detailed setlist information in compliance with Phish's official taping policy.\n\nRelisten uses this information for everything about Phish aside from reviews and ratings.	beep boop
\.


--
-- Data for Name: artists_upstream_sources; Type: TABLE DATA; Schema: public; Owner: -
--

COPY artists_upstream_sources (upstream_source_id, artist_id, upstream_identifier) FROM stdin;
5	5	\N
6	5	\N
1	11	ScottMetzger
1	8	MarcoBenevento
1	12	GarageATrois
1	13	G-NomeProject
1	15	EveryoneOrchestra
1	7	JoeRussosAlmostDead
1	20	JoeRussoPresentsHooterollPlus
1	9	GratefulDead
7	9	grateful_dead
1	19	BobWeir
6	19	
1	18	DarkStarOrchestra
6	18	
1	17	DiscoBiscuits
2	17	4
1	16	Dispatch
6	16	
1	14	Furthur
6	14	
3	4	
4	4	
1	111	EGimusic
1	112	JohnKadlecik
1	113	KungFuband
1	114	Shafty
1	115	NewMastersounds
1	116	ZebuBand
1	117	Zero
1	118	BrothersPast
1	119	PercyHill
1	120	JerryJosephandtheJackmormons
1	121	SteveKimockBand
1	122	ClubdElf
1	123	AssemblyofDust
1	124	Cabinet
1	125	DonnatheBuffalo
1	126	Raq
1	127	MaxCreek
1	128	TeaLeafGreen
1	129	KVHW
1	130	TheNewDeal
1	131	BelaFleckandtheFlecktones
1	132	InfamousStringdusters
1	133	Fruition
1	134	GreenskyBluegrass
1	135	GodStreetWine
1	136	HeavyPets
1	137	Aqueous
1	138	RailroadEarth
1	139	AmericanBabies
1	140	TheJauntee
1	141	SteveKimock
1	142	Twiddle
1	143	TheMotet
1	144	Strangefolk
1	145	KellerWilliams
1	146	PhilLeshandFriends
1	147	AquariumRescueUnit
1	148	TheDead
1	149	JohnPopper
1	150	LeftoverSalmon
1	151	StringCheeseIncident
1	152	thewerks
1	153	TheBigWu
1	154	TheBreakfast
1	155	Lettuce
1	156	YonderMountainStringBand
1	157	SoundTribeSector9
1	158	Spafford
1	159	Radiators
1	160	BernieWorrellOrchestra
1	161	moe
1	162	TAUKband
1	164	RyanAdams
6	164	
1	165	JazzMandolinProject
6	165	
1	166	TedeschiTrucksBand
6	166	
1	167	Ratdog
6	167	
1	168	HollyBowling
6	168	
1	169	Dopapod
6	169	
1	170	SmashingPumpkins
6	170	
1	171	BeneventoRusso
6	171	
1	172	Matisyahu
6	172	
1	173	Drive-ByTruckers
6	173	
1	174	GracePotterandtheNocturnals
6	174	
1	175	Ween
6	175	
1	176	BluesTraveler
6	176	
1	177	Guster
6	177	
1	178	LittleFeat
6	178	
1	179	MyMorningJacket
6	179	
1	180	GodspeedYouBlackEmperor
6	180	
1	181	Vulfpeck
6	181	
1	182	JackJohnson
6	182	
1	183	ElliottSmith
6	183	
1	184	G.LoveandSpecialSauce
6	184	
1	185	TheHippoCampusBand
6	185	
1	186	AndrewBird
6	186	
1	187	AkronFamily
6	187	
1	188	Mogwai
6	188	
1	189	TheWarOnDrugsMusic
6	189	
1	190	WarrenZevon
6	190	
1	191	Spoon
6	191	
1	192	TenaciousD
6	192	
1	193	MattPondPA
6	193	
1	194	BenKweller
6	194	
1	195	WhiteDenim
6	195	
1	196	TheDecemberists
6	196	
1	197	TedLeoandthePharmacists
6	197	
1	198	TheWalkmen
6	198	
1	199	AnimalCollective
6	199	
1	200	Fugazi
6	200	
1	202	BillyStrings
1	203	KitchenDwellers
1	204	PigeonsPlayingPingPong
1	205	MoonTaxi
6	205	
1	206	NathanMoore
1	207	SurpriseMeMrDavis
1	208	TrevorGarrod
1	209	Formula5
1	210	PerpetualGroove
1	211	GhostLightBand
1	2	DeadAndCompany
6	2	
2	1	12
1	1	Lotus
1	201	OfARevolution
6	201	
1	163	UmphreysMcGee
6	163	
1	212	EkoostikHookah
1	213	Guitarmageddon
1	214	DaveHarrington
1	215	BoyfriendsMusic
1	216	TheHiggs
1	217	CirclesAroundTheSun
1	218	KarinaRykman
1	220	StantonMoore
1	219	CharlieHunter
1	221	NorthMississippiAllstars
1	222	EricKrasno
1	223	ChrisHarford
1	224	AnimalLiberationOrchestra
1	225	Dawes
1	226	DelMcCouryBand
1	227	GreyboyAllstars
1	228	HotButteredRum
1	229	JackieGreene
1	230	JoshRitter
1	231	MarcusKingBand
6	231	
1	232	DubApocalypse
1	233	GarajMahal
1	234	RumpkeMountainBoys
1	235	Cubensis
1	236	GrooveSession
1	237	GrampasGrass
1	238	TheOtherOnes
6	238	
1	239	Papadosio
6	239	
\.


--
-- Data for Name: features; Type: TABLE DATA; Schema: public; Owner: -
--

COPY features (id, descriptions, eras, multiple_sources, reviews, ratings, tours, taper_notes, source_information, sets, per_show_venues, per_source_venues, venue_coords, songs, years, track_md5s, review_titles, jam_charts, setlist_data_incomplete, artist_id, track_names, venue_past_names, reviews_have_ratings, track_durations, can_have_flac) FROM stdin;
8	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	11	t	f	t	t	t
5	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	8	t	f	t	t	t
9	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	12	t	f	t	t	t
10	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	13	t	f	t	t	t
12	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	15	t	f	t	t	t
7	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	7	t	f	t	t	t
17	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	20	t	f	t	t	t
6	t	f	t	t	t	t	t	t	f	t	t	f	t	t	t	t	f	f	9	t	f	t	t	t
16	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	19	t	f	t	t	t
15	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	18	t	f	t	t	t
14	t	f	t	t	t	t	t	t	f	t	t	f	t	t	t	t	f	f	17	t	f	t	t	t
13	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	16	t	f	t	t	t
11	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	14	t	f	t	t	t
4	t	t	f	t	t	t	t	f	t	t	t	t	t	t	f	f	t	f	4	t	t	f	t	f
108	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	111	t	f	t	t	t
109	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	112	t	f	t	t	t
110	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	113	t	f	t	t	t
111	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	114	t	f	t	t	t
112	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	115	t	f	t	t	t
113	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	116	t	f	t	t	t
114	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	117	t	f	t	t	t
115	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	118	t	f	t	t	t
116	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	119	t	f	t	t	t
117	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	120	t	f	t	t	t
118	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	121	t	f	t	t	t
119	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	122	t	f	t	t	t
120	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	123	t	f	t	t	t
121	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	124	t	f	t	t	t
122	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	125	t	f	t	t	t
123	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	126	t	f	t	t	t
124	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	127	t	f	t	t	t
125	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	128	t	f	t	t	t
126	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	129	t	f	t	t	t
127	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	130	t	f	t	t	t
128	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	131	t	f	t	t	t
129	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	132	t	f	t	t	t
130	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	133	t	f	t	t	t
131	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	134	t	f	t	t	t
132	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	135	t	f	t	t	t
133	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	136	t	f	t	t	t
134	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	137	t	f	t	t	t
135	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	138	t	f	t	t	t
136	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	139	t	f	t	t	t
137	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	140	t	f	t	t	t
138	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	141	t	f	t	t	t
139	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	142	t	f	t	t	t
140	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	143	t	f	t	t	t
141	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	144	t	f	t	t	t
142	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	145	t	f	t	t	t
143	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	146	t	f	t	t	t
144	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	147	t	f	t	t	t
145	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	148	t	f	t	t	t
146	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	149	t	f	t	t	t
147	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	150	t	f	t	t	t
148	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	151	t	f	t	t	t
149	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	152	t	f	t	t	t
150	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	153	t	f	t	t	t
151	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	154	t	f	t	t	t
152	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	155	t	f	t	t	t
153	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	156	t	f	t	t	t
154	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	157	t	f	t	t	t
155	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	158	t	f	t	t	t
156	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	159	t	f	t	t	t
157	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	160	t	f	t	t	t
158	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	161	t	f	t	t	t
159	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	162	t	f	t	t	t
161	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	164	t	f	t	t	t
162	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	165	t	f	t	t	t
163	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	166	t	f	t	t	t
164	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	167	t	f	t	t	t
165	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	168	t	f	t	t	t
166	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	169	t	f	t	t	t
167	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	170	t	f	t	t	t
168	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	171	t	f	t	t	t
169	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	172	t	f	t	t	t
170	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	173	t	f	t	t	t
171	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	174	t	f	t	t	t
172	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	175	t	f	t	t	t
173	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	176	t	f	t	t	t
174	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	177	t	f	t	t	t
175	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	178	t	f	t	t	t
176	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	179	t	f	t	t	t
177	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	180	t	f	t	t	t
178	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	181	t	f	t	t	t
179	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	182	t	f	t	t	t
180	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	183	t	f	t	t	t
181	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	184	t	f	t	t	t
182	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	185	t	f	t	t	t
183	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	186	t	f	t	t	t
184	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	187	t	f	t	t	t
185	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	188	t	f	t	t	t
186	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	189	t	f	t	t	t
187	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	190	t	f	t	t	t
188	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	191	t	f	t	t	t
189	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	192	t	f	t	t	t
190	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	193	t	f	t	t	t
191	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	194	t	f	t	t	t
192	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	195	t	f	t	t	t
193	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	196	t	f	t	t	t
194	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	197	t	f	t	t	t
195	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	198	t	f	t	t	t
196	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	199	t	f	t	t	t
197	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	200	t	f	t	t	t
199	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	202	t	f	t	t	t
200	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	203	t	f	t	t	t
201	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	204	t	f	t	t	t
202	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	205	t	f	t	t	t
203	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	206	t	f	t	t	t
204	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	207	t	f	t	t	t
205	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	208	t	f	t	t	t
206	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	209	t	f	t	t	t
207	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	210	t	f	t	t	t
208	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	211	t	f	t	t	t
2	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	2	t	f	t	t	t
1	t	f	t	t	t	t	t	t	f	t	t	f	t	t	t	t	f	f	1	t	f	t	t	t
198	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	201	t	f	t	t	t
160	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	163	t	f	t	t	t
209	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	212	t	f	t	t	t
210	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	213	t	f	t	t	t
211	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	214	t	f	t	t	t
212	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	215	t	f	t	t	t
213	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	216	t	f	t	t	t
214	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	217	t	f	t	t	t
215	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	218	t	f	t	t	t
217	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	220	t	f	t	t	t
216	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	219	t	f	t	t	t
218	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	221	t	f	t	t	t
219	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	222	t	f	t	t	t
3	f	f	f	f	f	t	f	f	f	t	f	t	t	t	f	f	f	f	5	t	f	f	t	f
220	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	223	t	f	t	t	t
221	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	224	t	f	t	t	t
222	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	225	t	f	t	t	t
223	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	226	t	f	t	t	t
224	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	227	t	f	t	t	t
225	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	228	t	f	t	t	t
226	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	229	t	f	t	t	t
227	t	f	t	t	t	f	t	t	f	f	t	f	f	t	t	t	f	f	230	t	f	t	t	t
228	t	f	t	t	t	t	t	t	f	t	t	t	t	t	t	t	f	f	231	t	f	t	t	t
229	t	f	t	t	t	t	t	t	f	f	t	t	f	t	t	t	f	f	232	t	f	t	t	t
230	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	233	t	f	t	t	t
231	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	234	t	f	t	t	t
232	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	235	t	f	t	t	t
233	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	236	t	f	t	t	t
234	t	f	t	t	t	f	t	t	f	f	t	f	t	t	t	t	f	f	237	t	f	t	t	t
235	t	f	t	t	t	t	t	t	f	t	t	f	t	t	t	t	f	f	238	t	f	t	t	t
236	t	f	t	t	t	t	t	t	f	t	t	f	t	t	t	t	f	f	239	t	f	t	t	t
\.


--
-- Name: featuresets_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('featuresets_id_seq', 236, true);


--
-- Name: upstream_sources_id_seq; Type: SEQUENCE SET; Schema: public; Owner: -
--

SELECT pg_catalog.setval('upstream_sources_id_seq', 7, true);
           ");
        }

        protected override void Down()
        {
            Execute(@"
TRUNCATE features, artists_upstream_sources, artists, upstream_sources RESTART IDENTITY;          
            ");
        }
    }
}