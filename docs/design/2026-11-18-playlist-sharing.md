# Playlist sharing: share, follow, and copy

An owner can publish a playlist and share a link. Anyone with the link can open it, including signed-out visitors. A signed-in listener can follow the original or make their own copy. What descriptions, text blocks, and timestamp links do in a public playlist is covered in [Playlist guide content](2026-11-18-playlist-guide-content.md#public-guides).

## Share a link

Playlists start private. The owner can publish one and share a stable `relisten.net/playlists/<code>` link. It opens the read-only playlist in Relisten if it's installed, or on the web otherwise.

- The code is random and unguessable, generated on first publish. It is not derived from the playlist ID. Republishing reuses the same code.
- The public endpoint returns the same `404` for missing, private, archived, and taken-down playlists, so a code reveals nothing unless the playlist is public.
- The public snapshot returns the playlist as the owner wrote it, with no catalog filtering. The User Service never reads catalog tables. Mobile and the web page check tracks through the catalog resolver and show unavailable ones as unavailable, as they do for a listener's own playlists.
- The web page renders descriptions with the same Markdown subset and sanitizing as mobile. On the web, a timestamp link plays the target entry in the web player starting at that time.

## Follow the original

A signed-in listener can follow or unfollow from the shared playlist. Save one subscription per account and playlist. A library snapshot, and the last page of each library pull, carry the current revision of every followed playlist. Mobile fetches a public snapshot only when that revision differs from the stored one, or after a server restore. Followers pull edits from the original, so the owner's write does the same work regardless of follower count.

An unpublished, archived, or taken-down playlist, or one whose owner deleted their account, shows as unavailable on the next check. While it is unavailable, followers see only that it is unavailable, not its later title, edits or owner. The subscription stays. If the owner publishes again or unarchives, the same follow and link work again. Unarchiving restores the prior publication state.

## Make a copy

A signed-in listener can make a private, editable copy while the source is public. Build it on the server from one public snapshot. Preserve descriptions, segment boundaries, order, and repeated tracks. Give the copy fresh playlist, segment, and entry IDs and apply the normal creation quotas to every copied entry. The listener keeps the copy through later edits, unpublishing, or a takedown of the source.

The copy includes every entry in the snapshot, including tracks that are currently unavailable. The new owner's app marks those the same way it does in any playlist, and they play again if the catalog restores them. Copying makes no catalog call, so it can't fail because the catalog is slow.

The app keeps the copy command's ID until it knows the outcome. On retry, check for a matching successful receipt before reading the source. Return the same copy even if the source has since gone private or been taken down.

## Reporting and takedown

Descriptions are user-generated content anyone with the link can read. Before descriptions go public, this milestone needs a way to report a public playlist and take it down.

A takedown is a separate state from unpublishing. The web page, public snapshot, follower checks, and new copy requests use the same visibility rule. During a takedown, public reads return `404`, followers see the playlist as unavailable, and new copies are refused. The owner can still view and edit the playlist privately and must wait for the takedown to be lifted before publishing it again. Public responses are cached at the edge for at most 60 seconds, and browsers revalidate on every read, so a takedown reaches every reader within a minute without a purge.

Existing copies keep their contents and publication state. If someone publishes a copy, listeners can report that playlist separately.

## Rollout checks

- Open a link signed out, in the installed app and in the browser. On the web, tap a timestamp link and confirm the web player starts the right entry at the right time.
- Sign in, follow, and confirm an owner edit appears after a refresh. Unpublish and republish to verify that access and the follow recover through the same link.
- Copy a playlist with a track the catalog reports as unavailable. Verify the copy keeps that entry and the new owner's app shows it as unavailable.
- Lose a successful copy response, then take down the source. Verify the retry returns the same private copy. Confirm the public link returns `404`, followers see the source as unavailable, new copies and owner republishing are blocked, and existing copies keep their contents and publication state.
