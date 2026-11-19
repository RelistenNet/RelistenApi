# Server roadmap for accounts, playlists and MCP

Status: coordinating document, 2026-11-18. It sets the order of the server work
below and records what each piece waits on. Each linked plan owns its own steps,
validation and decisions. When this document disagrees with one of them about
order, this document wins. When it disagrees about anything else, the linked plan
wins.

## Order of work

| # | Work | Plan | Waits on |
|---|---|---|---|
| 1 | Private playlists: qualify for internal TestFlight | [v4 playlists](2026-11-06-v4-playlists.md) §1, mobile milestones 1–2 | Android Auto list on mobile |
| 2 | Account deletion, with its `relisten-flux` network change | [Account deletion](2026-11-18-account-deletion.md) | Nothing |
| 3 | MCP PR 1: monthly play rollup and `1y` popularity | [Relisten MCP](2026-11-18-relisten-mcp.md) | Nothing. The daily backfill reached 2018-10-19 |
| 4 | Guide content: descriptions, text blocks, timestamp links | [v4 playlists](2026-11-06-v4-playlists.md) §2, mobile milestone 3 | #1. API and mobile ship together |
| 5 | Cloud play history | [Cloud play history](2026-11-18-cloud-play-history.md) | #2 before any external users |
| 6 | MCP PR 2 and 3: endpoint, OAuth, ChatGPT client, deployment | [Relisten MCP](2026-11-18-relisten-mcp.md) | #3, #4. Account tools stay switched off until #2 |
| 7 | Sharing: publish, follow, copy, report, takedown | [v4 playlists](2026-11-06-v4-playlists.md) §3, mobile milestone 4 | #2, #4 |
| 8 | Collaboration | [v4 playlists](2026-11-06-v4-playlists.md) §4 | #7 |

#2, #3 and #4 can run in parallel. Nothing that lets external users create an
account, or use one, launches before #2. That includes external release of
private playlists and MCP account tools.

The mobile plan is
[Local-first playlists](../../../../relisten-mobile/docs/plans/active/2026-11-16-local-first-playlists.md).
The deletion and history plans carry their own mobile sections. The relisten.net
web work is planned separately in `relisten-web`.

## Design documents

- [Playlist guide content](../../design/2026-11-18-playlist-guide-content.md):
  behavior for #4, and for content in public playlists.
- [Playlist sharing](../../design/2026-11-18-playlist-sharing.md): behavior for #7.
- [Private playlist contracts](2026-11-16-v4-playlist-contracts.md): the wire
  contract. #4 and #7 revise it in place.

## Rules that cross plans

- **Deletion purge coverage.** Every table that stores user data cascades from
  `identity.users` or adds a step to the deletion purge. The deletion schema
  test enforces this. Tables added later include plays (#5), MCP consent grants
  (#6), and follows and reports (#7).
- **No per-follower writes.** An owner's edit never writes anything for its
  followers. Followed playlists ride the library sync as heads computed at read
  time, at most 500 per account. Collaboration uses the same approach for member
  playlists, and writes other members' library changes only when membership
  changes.
- **Ownership and library membership are separate on mobile.** `ownerUserId` is
  the owner. `libraryUserId` and `access` decide sync, visibility and editing.
- **Destroy local data only on a definite success.** Mobile clears an account's
  data on deletion's `202`, never on a bare `401`. Rows bound to an account are
  sent only to that account.
- **Public responses are cached for at most 60 seconds at the edge, and browsers
  revalidate on every read.** Takedown, unpublish and owner deletion need no purge.
- **Guide content changes don't change old hashes.** New hash lines appear only
  when a description is present, so receipts from before rollout still replay.
- **One account settings screen** holds Delete account (#2) and connected MCP
  clients (#6).
- **The User Service never reads catalog tables.** Public reads and copies
  return authored structure, and clients check availability through the
  resolver. Play uploads accept any well-formed track ID. The one catalog write
  is the anonymous `source_track_plays` row.
- **Signed-in plays count once in anonymous popularity.** They reach
  `source_track_plays` only through the User Service (#5), stamped with the
  upload time. Imported plays never do. Signed-out `/live/play` retries can still
  double-count, and that's accepted.
- **Typed IDs, stored as `text`.** IDs the account domain creates, such as
  `user_`, `playlist_`, `playlist_entry_` and `play_`, must wrap a UUIDv7. A
  catalog ID stored in account data, such as `track_`, keeps the catalog's
  existing UUID of any version. Validate it as a well-formed catalog ID, never
  with `AccountIdValidation`.
- **MCP write calls aren't idempotent.** A retried call can create a duplicate
  playlist, which the listener archives.

## Open questions

- The MCP plan's "To verify before building" list (#3, #6).
- How long a heavy listener's deletion purge takes once plays are compressed
  (#2, #5).

## Deferred on purpose

Database roles that enforce the user/catalog boundary, server-side history
clearing, re-authentication before deletion, an object-storage tombstone for
backup restores, and the web page in this repository.
