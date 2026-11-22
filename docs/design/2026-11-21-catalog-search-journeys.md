# Relisten catalog search and Add Tracks

The journeys and exact-file checks below define what search should do for a listener. The [implementation plan](../plans/active/2026-11-22-catalog-search.md) records the Typesense design chosen after replaying the eval. The Postgres design and build order in this document are the original proposal.

Listeners often come to Relisten with a clue. A friend says “Japan Twist.” Someone remembers seeing the Dead at Barton Hall. A newer Billy Strings fan remembers “Creek” and Red Rocks. Search should get them from that clue to a show they can explore or a specific recording they can hear or add. Finding the right show and adding the wrong recording is a failed search.

Use the same catalog search in **Search** and **Add Tracks**. When search starts from a playlist, carry that destination through the artist, venue, show, and recording screens. Tapping a show opens its setlist. A result with a playable preview offers **+** for the labeled file and a way to see other matches. Going back restores the query and the listener's place in the results. The old substring search returned zero hits for `Japan Twist` and `Japan Ghost`, even though Relisten has the recordings.

## What the listener is trying to do

### Find the performance a friend mentioned

“You have to hear the Japan Twist” is a useful recommendation even if the listener knows neither the city nor the date. They type `Japan Twist`. Show the matching Phish performances by date and place, with the exact track beneath each show. Fukuoka and Tokyo both qualify. Give the listener enough context to make the choice and allow them to hear it before committing to it.

A listener who picks Fukuoka may want the entire passage. The June 14, 2000 recording has `Twist`, a separate `Jam`, and then `Walk Away`. Adding the file labeled `Twist` is one action. Opening Set 2 and keeping `Twist` and `Jam` together is another. Both should be possible from the same search. A soundcheck titled `(Check) Twist Jam` is a weaker match for the broad query and a fair answer when the listener specifies its date.

### Find a show by the place you remember

A Deadhead types `Barton Hall` to see what happened there. They could be looking for Cornell ’77, a show they attended in 1980, or a different band that played the same room. Lead with the venue and show a few dated concerts immediately. From the venue, let them see more. From a show, let them see the setlist and recordings. Cornell ’77 can be prominent in broad search. `Barton Hall 1980` should take the listener to the show they asked for.

### Find a song when all you remember is one word

A Billy Strings listener hears a friend play “Creek” and remembers the video was at Red Rocks. `billy red rocks creek` should give them playable matches from 2022 and 2023, with artist, date, venue, and title in view. Adding `2022` should narrow the results. Someone who types just `Meet Me at the Creek` may want the song page and versions to explore. Give them the song as an entry point before showing a long list of recordings.

Fans also bring the spelling they saw on a tape, in a text, or in an uploader's filename. `Termoil and Tinfoil` is an actual variant in Relisten. A good result gives the canonical Billy Strings title and still leads to the recording that had the variant.

### Pick the version you actually want

“That long Lawn Boy from the Baker’s Dozen” is how a Phish listener might remember it. They type `lawn boy baker's dozen`, or perhaps `quan boy baker's dozen` if they misheard the title. If 2017 is the only show detail they remember, they type `lawn boy 2017`. The run name should lead them to the Garden show. The year should help them compare the Garden show with the July 16 Chicago performance. Show date, venue, set, and duration together so the listener can recognize the version from the result.

The Garden recording has a Set 1 file of about 30 minutes and an encore file of about 88 seconds. One show card can offer both choices. If the listener taps **+** on the encore, their playlist should get that recording's `track_` ID. July 25 is how the test identifies the show. The listener can find either file without knowing it. [Phish’s Baker’s Dozen announcement](https://phish.com/news/announcing-the-bakers-dozen-13-nights-at-madison-square-garden/) · [July 25 setlist](https://phish.com/tours/dates/tue-2017-7-25-madison-square-garden/)

A Deadhead who says “the Charlie Miller Cornell tape” is already choosing among transfers of the same concert. They may care about a specific audience recording and know the etree identifier. At minimum, they should reach that show, open **Other recordings**, and recognize their tape from its credits and source ID. Searching by transfer credit or source ID directly is worth evaluating too. [Moore, Berger, and Miller recording on the Internet Archive](https://archive.org/details/gd1977-05-08.ecm33p.moore.berger.miller.117026.flac16)

### Add something you just listened to

A playlist curator opens **Add Tracks** thinking about the show that was playing earlier. They see recently played shows first. They open one, add three tracks, and keep the sheet open. Their next option is another track from a show already in the playlist. If the curator searches for a known segue such as `scarlet > fire`, they can later add both files in order as one undoable edit. The files have to come from the same source and actually run together.

## Add Tracks on the phone

- **Before typing.** Show recent listening grouped by show, tracks from the shows in this playlist, and downloaded tracks. A later suggestion such as **More versions of songs in this playlist** can use one cached catalog request. Show it when useful results arrive. It should not delay the local list.
- **While typing.** Show local matches immediately. Debounce the catalog query, cancel the previous request, and discard late responses. Cache by normalized query and explicit filters for backspacing. Keep the tapped or focused row stable while results settle. Start with the full catalog, boost the playlist's artists, and give the listener an explicit artist filter. If they go offline, keep downloaded tracks addable and make the scope of the local results clear.
- **When choosing.** A venue or artist opens browse, and a show opens its setlist within the search flow. A performance result offers a clearly labeled playable preview and **+**. Show the set and duration when a song occurs twice on one tape. **Other recordings** exposes alternate sources. Keep the destination, query, and scroll position through drill-ins and return.
- **After adding.** Keep the sheet open, confirm in place, and offer **Undo**. Use the existing duplicate prompt for an identical file. Different files with the same title are different choices. Resolve availability before applying a catalog result. A withdrawn file prompts the listener to pick another without changing the playlist. Use the existing local playlist edit and sync path. [Track resolution](../../RelistenApi/Controllers/V4/CatalogResolveController.cs)

## What the current system supports

Live `GET /api/v4/search` with default options returned venues and sources for `Barton Hall`, with zero show hits. `Japan Twist` and `Japan Ghost` returned zero hits across all categories. `Billy Strings` returned the artist. These were November 21, 2026 spot checks. A follow-up Phish-scoped `tahoe tweezer` also returned zero hits. The earlier playlist proposal classified its first 48 cases as 11 pass, eight partial, and 29 fail from SQL and catalog checks. The merged fixture needs a complete measured baseline.

The existing endpoint performs literal substring search across result types. The magnifier inside a playlist filters its current contents. The Catalog API already has show and source detail, an artist-scoped performances drill-in, and an indexed artist-scoped review search. [Current search controller](../../RelistenApi/Controllers/V4/CatalogSearchController.cs) · [Performance and review endpoints](../../RelistenApi/Controllers/V4/CatalogPerformanceController.cs) · [Show and source reads](../../RelistenApi/Controllers/V4/CatalogReadController.cs)

## Retrieval in Postgres

Build one PostgreSQL `search_documents` projection for the Catalog API. Use typed documents for artist, venue, show, song, tour, recording, and a song or raw title at one show. Index title and aliases separately from context such as artist, venue, city, country, year, and run. Keep the verified source and file identities needed to render a preview. A show-level match can expand into multiple files even when those files are on the same tape. Search both canonical and imported song names, and index playable raw titles for artists with sparse song metadata. `String Cheese Restless Wind` is a global query that should work through the raw title path.

1. **Normalize and recognize reliable constraints.** Fold case, accents, and punctuation for text matching. Parse explicit dates, years, holidays, and selected filters before removing date delimiters or `>`. Preserve numeric titles like `46 Days` and exact source IDs such as `117026`. Use the reader's locale or offer a choice for a genuinely ambiguous date. Artist and country context can match indexed fields even when there is no confident parse.
2. **Retrieve candidates.** Use GIN full-text and a prefix on the last token for multiword lookup, B-tree for explicit constraints, and a bounded `pg_trgm` fallback on names and titles for typos. `Japan Twist` matches the country and song on the same document. Require some title evidence to promote a performance, so a venue name alone does not match every track there. Preserve explicit filters when trying a typo. A broader suggestion should say which clue it relaxed.
3. **Rank and return a bounded mixed page.** Exact artist, venue, or song names can lead. When the query combines a song with show context, show cards can lead with their matching track previews. Rank within the matched scope, including when `Tokyo Twist` has a less-played match than Fukuoka. Use live reads for a bounded show or source drill-in. Reuse the existing `/performances` filters for song, years, venue, tour, soundboard, duration, and verified segue relationships where they fit. Use the projection or a scoped catalog read for exact dates and source IDs. Add a popular sort when the ranking data is available before pagination.
4. **Publish a rebuildable generation.** The ingestion worker builds and validates a new generation from committed catalog data, then atomically publishes it. A request sees one published build. Keep the last successful one available if a rebuild fails. Measure rebuild time, WAL, and standby lag before choosing a refresh cadence. If the entity lookup plus live performance join meets the same relevance and latency targets at lower measured operating cost, use that path.

The first endpoint can be `GET /api/v4/search/typeahead`, shared by both entry points. Return a stable typed ID and display fields for each hit. A performance preview also carries its actual source and track IDs. The Catalog resolver handles playability at selection time. The search document makes the candidate cheap to find.

Relisten has all 13 Baker’s Dozen shows under the broader `Summer Tour 2017` label. Add the documented run name to those 13 show IDs and their search documents. Then `lawn boy baker's dozen` can find the Garden performance, and `lawn boy 2017` can also find Chicago. [Phish’s announcement with the run dates](https://phish.com/news/announcing-the-bakers-dozen-13-nights-at-madison-square-garden/)

### Rank matching versions with track plays

Use anonymous track starts to order comparably relevant performances. On the 365 complete UTC days ending November 20, 2026, the existing rollups recorded **741 starts for the Fukuoka `Twist` and 1 for the Tokyo `Twist`**. The 30-day counts were **43 and 1**. For `Japan Twist`, that is a reason to lead with Fukuoka while keeping Tokyo visible. For `Tokyo Twist`, the location takes precedence. Starts measure listening activity. They include tracks reached in normal show playback.

- **Rank the show card by the matching song.** Sum starts across the distinct playable files for that song and show. Deduplicate the file UUID before summing, since one file can match more than one part of the song. Start with 365-day counts and a modest logarithmic boost within the same relevance tier. A low count gives a small boost. An eligible file with zero or missing plays gets no boost and can still appear on the strength of its match.
- **Choose the preview with the file's own count.** Honor explicit source filters and the existing recording preference first. Among equally preferred sources or files, use the file's play count. In the same snapshot, the long Garden `Lawn Boy` has 700 starts and the encore has 55, so the long one can lead. Both remain selectable.
- **Calculate outside the search request.** Reuse the daily track rollup and the monthly rollup for complete months in a yearly window. Store the score and window end with the search build. The existing performance reader already joins plays by file UUID after choosing its page. Search needs the score available before choosing the page. [Popularity rollups](../popularity/README.md#calculation-sources) · [Existing performance play reader](../../RelistenApi/Services/Popularity/PlayRollupReader.cs)

On a pinned build, replay the same queries with and without the boost. The base PH02 case requires both performances ahead of the soundcheck. Its replay with the pinned play counts also requires Fukuoka ahead of Tokyo. PH19 must still lead with Tokyo. Try a file with no plays that is the only exact match, and repeated mappings of one file that could inflate the aggregate. Check that the boost improves broad version searches without degrading known-item searches.

### Find the recording through its notes or reviews

Index recording documents in the same `search_documents` table. Exact archive IDs, taper names, and transfer credits can identify a particular tape. Search descriptions, taper notes, and lineage as recording text, with a short excerpt explaining the hit. For `jerry moore cornell dolby b`, etree source `117026` is a good result. The taper credit identifies Moore, the show is at Cornell, and the source notes discuss Dolby B. Tapping should open that recording with its show still accessible. [Existing source search fields](../../RelistenApi/Services/Catalog/CatalogCollectionReader.cs) · [Moore recording and notes](https://archive.org/details/gd1977-05-08.ecm33p.moore.berger.miller.117026.flac16)

Keep review matches in a bounded **Notes & Reviews** result group. Start with the existing indexed review search when the artist is selected or known. Group excerpts by their owning recording and show its date. A review can describe an embedded passage, use the same words as a nickname in an ordinary sentence, or talk about a different concert. In a live Grateful Dead review query for `Beautiful Jam` scoped to 1971, an April 28 review came first because it used the phrase descriptively. Several February 18 reviews use it as the name of a passage. The February 18 Charlie Miller source has those reviews but has no separately titled `Beautiful Jam` file. Another February 18 source does. Show the review and recording as evidence. Offer direct **+** only when there is an independently matched playable file on that source, or after the listener explicitly picks a file. [Artist review search](../../RelistenApi/Controllers/V4/CatalogPerformanceController.cs) · [Review matching and excerpts](../../RelistenApi/Services/Catalog/CatalogReviewSearch.cs) · [February 18 recording with the named file](https://archive.org/details/gd1971-02-18.sbd.cantor-crouch-diebert-gmb.85478.sbeok.flac16)

### Ranking experiments

Try days with plays and play counts relative to other tracks on the same show separately after the track-start baseline. Days with plays reduces the influence of looping within one day. Show-relative plays can suggest listeners sought out a track, and can also reflect set order or tape availability. A fixed median for a song preserves the order of that feature across versions of the same song. Use the eval to see whether the feature improves cross-song ranking before relying on it.

Use review phrases to suggest aliases for editorial confirmation. A phrase can describe a different show. For example, 42 Phish reviews mention `Tahoe Tweezer` and only two are attached to July 31, 2013. Keep review evidence tied to its owning source. If play reporting is extended, record the playback trigger separately from where the tap occurred, so a deliberate search choice and a track reached by auto-advance can be evaluated separately. Current play starts remain useful with their documented limitations.

## One eval for the API and phone

[Search eval fixture](../../RelistenApiTests/Fixtures/search_eval.json) is the single list of queries and expected outcomes. It has **127 query cases** from the two proposals, **nine phone journeys**, and **nine exact recording fixtures**. It retains the catalog IDs such as PH02 and gives the playlist proposal's rows PT IDs. GD04 also covers PT15, the same `5/8/77` query with the Grateful Dead explicitly selected. PT31 includes PT53 as a replay with the reviewed Tahoe alias enabled. Keep other similar cases when spelling, scope, or intent changes what should happen.

The fixture separates actual input from expected dates, artist filters, ranked result targets, and subsequent actions. A target can accept several result kinds for one task. When a case lists several targets, each must be reachable as specified. A browse case can also list acceptable landing targets that lead to the required result. Required and excluded recording references identify exact file checks. Bind artist slugs, dates, song names, source IDs, and file UUIDs to a pinned catalog, and report a fixture gap if the necessary records cannot be seeded. Catalog checks establish the presence of the targets. The proposed endpoint and phone replay establish whether the search finds them.

| Case | Input | What the listener needs to get |
| --- | --- | --- |
| GD02 | `barton hall` | Barton Hall leads to shows there, including 1980. |
| PH02 | `japan twist` | Fukuoka and Tokyo in the first five with playable matches, ahead of the Tokyo soundcheck. |
| PH14 | `lawn boy baker's dozen` | The Garden show comes first and opens to both the long set file and short encore. |
| PH18 | `lawn boy 2017` | The Garden and Chicago versions are available to compare. |
| PT13 | `tw` with Phish selected | Phish's `Tweezer` in the first three. |
| PT31 | `tahoe tweezer` with Phish selected | The preview is the 36:16 Set 2 `Tweezer`. The 3:05 Set 1 file is `Lawn Boy` and cannot satisfy the query. [LivePhish setlist](https://www.livephish.com/LP-765.html) |
| PT47 | Empty Add Tracks box | A recent local show is available to open and add from. |
| X08 | `grateful dead scarlet japan 5/8/77` | A Cornell file cannot satisfy the Japan constraint. |
| R13 | `beautiful jam charlie miller` | Evidence for the Miller source stays attached to that source. Another source's standalone file remains available under its own recording. |

### Score the listener's result

- **Rank checks** use the case's maximum position in the ordered response, independent of phone viewport. A show with a matching track preview counts as one result. A bare show can satisfy a show target. A performance target needs the matching file or the stated expansion behavior.
- **Browse checks** also exercise the next action. The specified venue, show, or song must be reachable within its rank window, and its next step must get the listener to the requested content without a new query.
- **Guardrails** cover explicit constraints, source and file identity, and superseded responses. A single violation is a release blocker. Score the extended cases separately until their feature is introduced. Score the empty state and exact playlist edits on the phone.
- **Report counts** by surface, fan cohort, and task. For a pilot, require every starter first-result and guardrail case to pass, plus at least 90% of the remaining starter rank and browse cases. The phone journeys must pass too. Freeze popularity for CI or accept orders that are equivalent for the case.

Send each query as written. Dates in expected results identify shows for scoring. Run shorthand fixtures with the fixture's US locale. An `artist_filter` represents a listener selecting the artist chip. Some PT fixtures preserve the explicitly scoped API calls from the earlier baseline. PHONE06 exercises the default playlist-artist boost with the full catalog still available. Ask a few fans to complete the tasks without handing them a query. Keep some of what they actually type out of tuning and use it as a separate check.

### Follow the result through to the exact file

Run the phone checks against a real search response. Compare the selected file UUID to the queue ID or the v4 `track_` ID written to the playlist. The fixture keeps the source, set and position beside each checked UUID.

| Journey | Pass condition |
| --- | --- |
| PHONE01 · Main Search, PH02 | Open Fukuoka, see the neighboring `Twist` and `Jam` files in Set 2, and return to the query at the same place. |
| PHONE02 · Add Tracks, PH02 | Add Tokyo's `Twist` directly, open Fukuoka, then add its `Jam`. Verify both IDs in order in the original playlist. |
| PHONE03 · Add Tracks, PH14 | Find the Garden through the run name. Add the short encore `Lawn Boy` then the long Set 1 file. Verify both IDs in order and the duplicate prompt when re-adding the same file. |
| PHONE04 · Add Tracks, GD06 | Choose the Moore/Berger/Miller transfer under **Other recordings** and verify that exact `Scarlet` file. |
| PHONE05 · Main Search, GD02 | Open Barton Hall, reach the 1980 Dead show, and play a file that belongs to it. |
| PHONE06 · Add Tracks, BS04 | Start in a Phish-only playlist with no artist filter. Open the venue from `red rocks` and find Billy’s May 13, 2022 show. Back up and type `red rocks creek`. A Billy match is visible before naming the artist. Refine to `billy red rocks creek`, add the exact 2022 file, and keep the query and destination on return. |
| PHONE07 · Add Tracks, offline | Find and add the downloaded `Creek` file. Reconnection restores remote results and keeps the destination. |
| PHONE08 · Add Tracks, stale result | Withdraw Tokyo's `Twist` after search in the test catalog. Add explains the unavailability and makes no playlist change. |
| PHONE09 · Add Tracks, PT47 | Open recent Fukuoka with the query empty. Add `Twist` then `Jam` without closing the sheet. Verify both IDs in order, then undo `Jam` and verify `Twist` remains. |

## Implementation order

| Step | Build and verify |
| --- | --- |
| 1 · Eval harness and baseline | Wire the fixture into an NUnit replay against a disposable seeded catalog. Run the same supported query cases against the existing endpoint for a dated baseline. Capture missing capabilities, target reachability, and timeouts separately. In parallel, build the local Add Tracks sections, starting with recents and downloads. |
| 2 · Search documents and basic lookup | Publish the Postgres projection and integrate exact name, prefix, venue, date, and raw-title lookup. Explain remaining failures by missing aliases, incomplete parsing, retrieval, or ranking. Check real browse and Add eligibility on a small slice before widening it. |
| 3 · Mixed endpoint and Add Tracks pilot | Add the high-confidence parser, typeahead, performance previews, and precomputed play boost. Seed the reviewed aliases required by the starter set, including Baker’s Dozen on the 13 shows. Keep the mobile flow behind a flag. Replay the core query and exact-file phone checks. Gate scope and file correctness independently of overall relevance. |
| 4 · Wider aliases, sequences, and richer ranking | Expand reviewed nicknames, source metadata, notes and review evidence, and safe sequence actions in the order the eval shows they are needed. Add a suite only when a source proves file order or the combined file. Compare ranking signals independently on a pinned snapshot. |

Start with **150 ms API p95** and **500 ms p95 from the last keystroke to usable results on a phone**. Measure under concurrent typing, catalog imports, and ordinary browse requests. Separate index build time and replication impact from request latency. Keep a production smoke replay distinct from deterministic CI. Revise stage-by-stage relevance targets after measuring the full baseline. Consider a dedicated search engine if indexed Postgres falls short on measured relevance or latency and the same eval shows that the added service solves it at an acceptable operating cost.
