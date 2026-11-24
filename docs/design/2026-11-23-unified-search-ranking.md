# One ranked list for search

**Proposal for review.** An exact playlist from the listener’s library leads Explore when the app can open it. An owned private playlist on a fresh device appears after library sync. For an anonymous `dark star` search, the Grateful Dead song and an exact performance should lead a stranger’s playlist with that title. Popularity breaks close matches across kinds. Add Tracks keeps an exact unplayable result first and offers the best playable match directly below it.

Today the local merge compares a playlist’s position in the playlist results with a catalog hit’s position in the catalog results. It also checks for an exact catalog title only on the first row. The replacement scores each candidate’s match to the request and renders that order in both mobile screens. Keep the show metadata and the optional place-plus-song search in separate commits so either can land independently.

## Who produces and consumes the order

- **Catalog API `/api/v4/search`.** Return a ranked page of kind-specific rows. Attach existing v4 resources from the search index in a single batch. A performance carries a show and song identity when known, and always carries the exact preview track. The source setlist provides the other files. The authorized candidate exchange carries the score evidence used by the User Service.
- **User Service `/v1/search`.** Fetch catalog and playlist candidates in parallel. Read live playlist cards with existing visibility and revision checks. An exact owned or followed title uses the current library lookup, so it can appear before indexing. Use current follow membership for affinity and current visible IDs before scoring. Exact name uses the live title. Supporting fields from Typesense contribute match evidence only after the hit passes its revision check. One shared C# scoring function sets the mixed order. The Catalog API can use the same function to bound candidates and order scoped pages. Typesense’s native score and hit position stay in retrieval.
- **Mobile.** Render the API order through the existing row renderers and routes. Explore shows playlists inline. Add Tracks requests catalog only and keeps the existing resolve-before-add and undo flow. Keep downloaded results for catalog outages and keep any live playlist results when the catalog fails. Use the existing public-code route or the local library route when its row is synced. An owned private playlist pending library sync is still hidden in the first release, as it is today. Its API rank becomes visible when the local row arrives.

The app-facing response uses the same fields for All and single scopes. The server emits a typed row union, and kind-specific fields continue to carry the existing IDs and playable previews. Scope chips stay independent of interpretation chips.

```ts
type SearchResult = (SearchCatalogRow | SearchPlaylistCard) & {
  key: string
  position: number // one-based within the response
  request_id: string | null // impression that supplied this row
}
type Reading = {
  id: string
  chips: { key: 'artist' | 'place'; value: string; label: string }[]
}
type SearchResponse = {
  q: string
  request_id: string | null
  scope: SearchScope
  results: SearchResult[]
  availability: { catalog: SearchAvailability; playlists: SearchAvailability }
  counts: Partial<Record<SearchScope, number>>
  readings: { applied: Reading | null; alternatives: Reading[]; chips: SearchInterpretationChip[] }
  next_page: number | null
  more_scopes: ('shows' | 'sources')[]
}
```

Put the shared C# candidate DTOs and scorer in a search contracts project referenced by both services. Publish the public search envelope with System.Text.Json. Build embedded catalog resources with the existing v4 serializer. Keep rank features on the authorized catalog-to-user-service exchange, which uses the existing catalog service credential.

`counts` keeps the current catalog counts and Reviews chip eligibility. A playlist count, if returned, covers authorized rows only. All returns up to 24 rows and a null `next_page`. After sorting, keep up to three performances per song ID and three recordings per show ID. Song and show identity rows do not count toward these limits. Group unmapped performances by artist and folded title. Keep the relative order of the surviving rows. When the cap hides versions, `more_scopes` links to Shows or Sources. Those scopes retain every matching version, paged. Scoped paging follows the selected scope’s upstream continuation. An upstream page with more hits can have a continuation even if authorization or deduplication emptied this page. The app appends by stable row key, stops at null, and drops in-flight pages when the query, scope, or reading changes. All typeahead skips recordings and reviews, and their selected scopes search them. `find` reads the typed merged rows and projects them into its tool result.

## How two results compare

1. **Match tier comes first.** Exact requires a full normalized title or an identity resolved by the catalog. A compound exact match must account for the query through verified fields, as in a known place and exact song on the same performance. A title prefix has all earlier tokens matched and an unfinished last token. Typos, review text, taper notes, and tracks mentioned in playlist contents score as partial. Compute the tier for every candidate. Library title priority uses the current trimmed, Unicode-normalized title with case folded.
2. **Apply the mode and date order.** Add Tracks leads with a playable row within the top match tier if one exists. For an artist browse with no song words, a performance by that artist can satisfy the artist reading. If the exact tier has no playable candidate, lead with the exact row and its existing drill-in, then show the best playable match. Keep newest-first within matching dated results when the query calls for that order.
3. **Use bounded signals inside those rules.** Start the replay with `score = 100 × tier + 20 × exact_priority + 9 × relevance + 4 × popularity + 2 × confidence + 4 × affinity`. Tier is 0 for partial, 1 for prefix, and 2 for exact. Exact priority is 4 for an owned title, 3 for a followed title, 2 for a direct catalog identity or verified compound match, 1 for a plain exact playable performance, and 0 otherwise. Apply the highest qualifying priority. Each soft signal ranges from 0 to 1. Their combined 19 points fit between adjacent priorities, and the maximum of 99 fits between tiers. The same identity priority lets a well-played Phish performance compete with Holly Bowling’s exact song title for `tahoe tweezer`. Break equal scores within a kind by its raw count, then by stable identity. When two readings produce the same item, keep its stronger evidence. Deduplicate show rows using the existing grouping rule.

Use matched tokens and fields to score relevance within a tier. Coverage of a title contributes more than coverage of aliases or supporting text. Confidence records how well the result verifies an interpretation. Affinity uses authenticated playlist membership and the app's explicit `boost_artist_ids`.

The full catalog build calculates the distribution for each kind in the projection and attaches percentiles on the initial import. Performance calibration reads one batch of show/song totals at a time before projecting display documents. Ties share midpoint rank. Performances, recordings, shows, and songs use recent starts where available. Artists and venues use catalog size. The separate playlist build uses follower counts. Zero or unavailable signals contribute zero. Incremental updates preserve an existing document's percentile and give new documents zero until the next full build.

## How a listener corrects an inferred reading

Use the original text and request filters as the shared inputs. Generate the literal reading and at most two catalog-supported alternatives from the current artist and place vocabulary. Batch the literal request and the strongest additional reading into Typesense `multi_search` using the existing additional-search budget. Retain candidates from each. An inferred artist or place can restrict its own additional sub-search. The literal search still runs with the original words, and the inferred reading adds confidence only to candidates that verify its fields. Keep all requested words in play, so `phish 1977` still yields no matching performance.

At vocabulary load, derive initials from artist names, skipping articles and conjunctions such as “the” and “and.” Keep unique two-to-six-character initials that do not collide with whole catalog names or titles, or common English words. A leading initialism adds an optional artist-scoped search for the remaining terms. The literal query still runs and the listener can select either reading.

Return the reading used by the highest-ranked catalog result. Offer another reading if vocabulary lookup verifies an identity for it, even if it did not get its own search. For `capitol theatre`, a venue-name lookup can verify both Port Chester and Passaic. Selecting Passaic should keep a Passaic show and exclude a Port Chester show even when both have the same venue label.

A tap sends `reading_id` with the original query. Resolve it against the readings generated for that query, then apply its artist ID or place-to-venue IDs as explicit filters. Use vocabulary location keys for cities. The app keeps the selected chip active even when its results are empty. The literal option searches the original words with the explicit request filters. Clearing the selection restores the default search across readings, and editing the text clears the selection.

Selected readings, explicit request filters, and fully specified typed dates constrain all catalog searches. When there is an explicit artist filter, playlist candidates must satisfy the existing indexed artist filter. The unindexed exact-library lookup runs for unconstrained title queries. A selected place or exact date leaves playlists out in the first release because playlist documents lack those fields. Keep the existing named runs, marked segues, and known-item retrieval. Other kinds of ambiguous syntax can use the same contract later.

## Before, proposed after, and how to check it

Baseline scores are from the prior local mixed replay on the restored catalog, in both submitted search and typeahead. The last column gives acceptance targets for the local implementation. The validation report records where the replay differs. For the new `dark star` cases, the baseline runs the current `MixedSearchRanking` on a real local catalog response with seeded playlist candidates. Each result reproduced in both modes.

| Case | Observed before | Required after |
| --- | --- | --- |
| Existing 209 catalog cases | Public 155/160, holdout 27/30, famous 8/8, Denver/Radio City/Tahoe 5/5, SF 6/6 | Same verdict per case in both modes and the same exact-file checks |
| `dark star`, stranger’s exact title | Dead song #1, playlist #2, first performance #16 | Dead song leads. An exact playable performance and then the playlist both appear in All |
| `dark star`, second playlist hit is `Dark Star Sketches` | Prefix playlist #3, ahead of JRAD’s exact song | Exact JRAD song precedes the prefix playlist when both are returned |
| `dark star`, owned exact title | Own playlist #1 in the signed-in ranker probe | Own playlist #1 in the signed-in API even before indexing, and #1 on mobile once synced. A private card stays hidden before sync and from anonymous callers |
| Equal-strength catalog and playlist prefixes | Separate ranks supply no cross-kind comparison | Higher percentile leads when their other evidence is equal. Swap the percentiles and the order flips |
| `phish sf light` and `capitol theatre` | Phish’s SF Light leads. The current search offers no selectable venue reading | Same SF Light lead. The Passaic choice keeps only Passaic shows, and clearing it brings back the mixed venue results |

Use the saved local artifacts under `~/code/relisten/relisten-search-workspace/library-mixed-search-2026-11-23/` and the `unified-search-design-2026-11-23/` baseline probe. Tune on the 160 public cases only. Freeze the scorer before checking the holdout and report verdicts separately. Keep Claude’s sealed set sealed. Record a one-case weight fix as a finding. Compare relevance enabled and disabled across the replay and count changes to #1 and the top five. Include literal title matches that survive mistaken or ambiguous leading `billy` and `dead` readings. Use the same 1,960 typed prefixes and four clients to measure the extra reading and wider candidate fetch. Break out `rain s`, whose performance prefix expansion previously took 313–366 ms. Mobile tests should verify the interleaved order, selection and paging resets, partial availability, and that Add Tracks still adds the preview’s exact file.

Use the row key, position, and request ID for future impression attribution. Paging preserves each row’s original request ID. Logging is outside this change.
## Reviewed place names

Search reads reviewed place names from `RelistenApi/Services/Search/Data/reviewed-place-aliases.json`. The index builder and query vocabulary merge reviewed venue names with existing `norm.venue_names` rows and deduplicate by venue and spelling. The reviewed file supplies city names.

The admin UI needs screens for short venue names, city names, and artist and song nicknames. Restore the audited import and a city-name table when the admin UI is ready. Verify that stored names produce the same readings before retiring the bundle. Song nicknames belong in `norm.song_aliases`. The admin workflow also needs a place to store artist nicknames. The existing `venue_names_check` only allows date ranges on official names. RAC, CCCC, BBT, and BGCC are undated short names until that schema can express dated ones.

## Reviewed names for shows and runs

`RelistenApi/Services/Search/Data/reviewed-named-shows.json` holds the named shows, runs, and marked segues that search recognizes. Port them to norm when the admin UI can edit them.

## Reviewed artist and song nicknames

`RelistenApi/Services/Search/Data/reviewed-nicknames.json` carries the approved review batch. The query vocabulary gives band nicknames the same weight as a named artist. A `leading_only` nickname must start the original query, including when the listener types a date.

An approved song nickname follows the normalized title across the catalog. Search folds case, apostrophes and punctuation in the same way for reviewed names and catalog names. The vocabulary carries the nickname to another artist's song or playable track with the same title. It skips that artist if a different song already claims the nickname as a title, a stored alias, or another approval. A raw track labelled with an abbreviation alone cannot establish that there is a different song.

A named or selected artist narrows the song results. An artist boost favors the songs it supports. With neither signal, the nickname stays a weaker reading and popularity helps rank versions across artists. Nicknames marked `artist_context_only`, such as `ds` and `lope`, expand only with an artist reading, selection, or boost. Search reads eligible titles when it loads query evidence for an index generation. Canonical songs use song IDs and unmapped recordings use the artist and indexed track title. Reviewed song abbreviations also reserve their terms against automatic artist initials.

An incremental artist reindex makes new titles available to literal search. Inheritance from those titles starts when search loads the next full index generation.

The bundle is temporary. Search still reads the existing `norm.song_aliases`. Move the reviewed song aliases into that table and store the reviewed band names and their position rules in an artist-name table once the relisten-admin UI can review and edit them. Keep the title-based expansion and conflict checks when switching to stored approvals. Remove the bundle after verifying the stored names produce the same readings.


## Playback windows

The nightly popularity refresh publishes the 48-hour, 7-day, 30-day and
365-day windows from hourly, daily and monthly play rollups. The song and show
key counts the files matched to that song. A recording counts all starts
credited to that source. For search's raw-title groups, the storage key also
supports a normalized raw title when the catalog has no song match.

Search, song pages and the performances endpoint read the published windows.
Search ranks with its existing per-file play evidence and leaves rank
percentiles inside the services. Run one popularity refresh after migration 26
and before the first search build. See [publishing and recovery](../popularity/README.md).

## Ranking evaluation

`RelistenApiTests/Fixtures/search_eval.json` is a judged search evaluation.
Its queries record what a listener would expect to find and how high that
result should appear. Run the replay against a built index to grade relevance.
The ordinary NUnit suite checks the API and its failure behavior separately.
