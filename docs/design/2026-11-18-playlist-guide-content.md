# Playlist descriptions, text blocks, and timestamp links

A playlist can double as a listening guide. A segment description introduces a group of tracks. A track entry carries its own description when it moves. A text-only entry can sit between tracks and move independently. Typing `2:00` in a track description links to that moment in that entry. Sharing, following, and copying are specified in [Playlist sharing](2026-11-18-playlist-sharing.md). This doc covers how guide content behaves there.

## API and storage

- Add nullable `description` text to `playlist_segments` and `playlist_entries`. Allow `track_id` to be null for a text-only entry. Replace the track check constraint with one that requires either a valid `track_id` or a nonblank description, and keeps the existing format check for non-null track IDs. Text and tracks share the same entry IDs and rank order within a segment. Keep a segment if it still has a description or any entries.
- Make `TrackId` nullable in the entity and contracts, and update every caller. The compiler's nullable warnings list them. Catalog hydration on mobile and the web skips entries with no track.
- Add descriptions to create requests, insert operations, snapshots, and changes. Use `segments[].entries[]` for both track and text entries. Add segment and entry description updates to the existing edit batch, using `OptionalText` like playlist metadata does. Omitting `description` leaves it alone. Passing `null` clears a segment or track description. Passing `null` or blank text for a text-only entry is rejected; delete the entry to remove its prose. Description edits advance the playlist revision. `PlaylistHasher` must include descriptions and null track IDs, so a retry with different text never matches an earlier receipt. It adds them only when present, so track-only requests keep their existing hashes.
- A description edit replaces the whole field, and the last writer wins. If two devices edit the same description offline, one version is lost. That's acceptable before launch.
- Store Markdown as source text and render it on the client. Count text blocks toward entry quotas, keep displayed track counts separate, and bound description lengths, the number of prose-only segments, and the total size of a playlist's descriptions, so every guide fits in one create request.

## Mobile editing and playback

- Extend the local model and sync for the new snapshot and change payloads. Let users edit descriptions and add, move, or delete text blocks. Queue construction uses track entries and keeps the existing segment shuffle behavior.
- A track's menu offers **Add Note**, which edits that track's description and moves with it. In edit mode, one **Add Text** control in each gap picks the rest of the storage: a segment's description at the top of a group, a text block between tracks, or a prose-only segment between groups. Authors never choose a storage type. The position only decides what moves with what.
- Render paragraphs, bold, italics, lists, block quotes, HTTPS links, and `##` / `###` headings from a [CommonMark subset](https://spec.commonmark.org/0.31.2/). Treat the playlist name as the page title. Render everything else, including raw HTML and non-HTTPS links, as plain text. The public web page uses the same subset and sanitizing.

### Timestamp links

A timestamp is `m:ss` or `h:mm:ss`. Seconds are always two digits. A link fragment is `#t=<timestamp>`, optionally preceded by `entry=<entry_id>&`.

- **In a track description**, auto-link a bare timestamp only if it falls within that track's duration. `Try 2:00 and then 6:42` gives two links to the same entry when both times are in range. Authors can also label a link, as in `[Bass enters](#t=6:42)`, or point at another entry with `#entry=…&t=…`.
- **In a segment description or text block**, bare timestamps stay as text. **Insert timestamp** picks a track and writes a link like `[Hear the transition](#entry=entry_2&t=6:42)`. The entry ID keeps the target stable when the playlist is reordered or contains the same recording twice. A `#t=` link with no entry renders as its label text here.

Tapping a link starts or seeks the target entry at the chosen time. If the target is missing or unplayable, show the link label as text. Deleting an entry leaves links to it in place, and they render as text.

## Public guides

- **Public snapshots** return the guide as written, including every description and text block, with no catalog filtering. Readers' apps mark unavailable tracks as they do in any playlist. A link to an unavailable entry renders as its label text.
- **Copies** keep the descriptions and text blocks in the public snapshot. For each mapped `playlist_entry_` TypeID, replace the exact `#entry=<old-id>&t=` prefix with `#entry=<new-id>&t=` in the raw description text. This also rewrites literal examples that use the exact prefix. Bare IDs and timestamps stay intact. A copy includes every entry, so every link has a new ID to point at.
- **Moderation.** Public descriptions are user-generated content, so the length limits above apply, and the sharing milestone's reporting and takedown must ship before descriptions are public. A takedown affects the source playlist. Existing copies keep their contents and publication state.

Playback timeline markers can come later through an annotations array on track entries.

## Rollout checks

Playlists are pre-launch. Revise the existing contract in place and update the API and mobile together.

Verify that a segment description survives moving out its last track, a ranked text block stays put, and snapshots and deltas round-trip the result. Verify that reusing an operation ID with different description text is treated as a mismatched retry and doesn't return the first receipt. On mobile, put two timestamp links to different occurrences of the same recording in a single text block. Reorder the entries and tap both links to verify the intended occurrence and time. Delete one target and confirm its link renders as text. Confirm that segment shuffle keeps tracks together and playback skips text blocks.

For public guides, mark a track that has a description and a link pointing at it as unavailable in the catalog. Confirm that the public snapshot still returns its description, and that the reader's app shows the track as unavailable and renders the link as text. Copy a guide with a timestamp link, a literal example of the same `#entry=<old-id>&t=` prefix, and the same ID on its own in prose. Check that the link and example get the new ID and the bare ID stays intact.
