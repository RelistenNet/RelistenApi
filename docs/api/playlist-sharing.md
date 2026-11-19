# Playlist sharing API

Sharing is served by the User Service. Playlists start private. A published
playlist has a stable random code for the link `relisten.net/playlists/<code>`.
The web page and mobile integration are separate releases.

## Requests

Use the accounts host and the existing library write policy for mutations.
The public snapshot needs no credentials.

| Method and path | Request and result |
| --- | --- |
| `PUT /v1/playlists/{playlist_id}/publication` | Owner only. Send `{"contract_version": 1, "published": true}` or `false`. The request sets the state and needs no `command_id`; a retry returns the current state with `changed: false`. A publish returns `public_code`, and later publishes reuse it. Publishing a taken-down or archived playlist returns `409 playlist_taken_down` or `409 playlist_archived`. |
| `GET /v1/public-playlists/{public_code}` | Returns the current authored structure, public owner ID and username, revision, and history generation. Missing and unavailable playlists return the same 404. |
| `PUT /v1/public-playlists/{public_code}/follow` | Returns 204 for a new or existing follow while the playlist is public. An account can follow up to 500 playlists; the 501st returns `422 quota_exceeded`. Following your own playlist returns `422 own_playlist`, since it is already in `playlists`. |
| `DELETE /v1/playlists/{playlist_id}/follow` | Returns 204 even if the follow is already gone. Works for unavailable or deleted sources. |
| `POST /v1/public-playlists/{public_code}/copy` | Send `contract_version: 1` and `command_id`. Returns 201 with the private copy's ID and revision. Retries return 200 with the same copy, including after takedown or deletion of the source. |
| `POST /v1/public-playlists/{public_code}/reports` | Send a `reason` of 1–300 characters. Returns 204 once a report is open for this account and playlist. A new report beyond 10 per minute per account returns 429. |

Library changes carry `playlist_followed` and `playlist_unfollowed` with
`followed_playlist_id`. A snapshot and the last page of a pull carry every
`followed_playlists` head, even when the library revision is unchanged.
Unavailable heads carry only `playlist_id` and `available: false`. Treat a
snapshot as the full membership list. Apply changes in cursor order and use
heads to update metadata without changing membership.

On a generation change, refetch the public snapshot even if its revision
matches the stored one. Apply it only when its generation matches the new
library generation. If the edge still serves an old generation, retry after
the cached response expires.

## Moderation

Run these commands with the User Service image and its accounts migration
connection pointed at the writer. They exit without starting the HTTP service.

```sh
dotnet RelistenUserService.dll playlist-moderation list-reports
dotnet RelistenUserService.dll playlist-moderation close-report <report_id>
dotnet RelistenUserService.dll playlist-moderation take-down <playlist_id>
dotnet RelistenUserService.dll playlist-moderation lift-takedown <playlist_id>
```

List returns the 100 oldest open reports. Closing a report allows that listener
to report the playlist again, subject to the same rate limit. Takedown hides
public reads, blocks new follows and copies, and blocks the owner from
publishing. The owner can still edit privately. Lifting a takedown restores
public access if the playlist remains published and unarchived. Close the
reviewed reports separately. Existing copies keep their contents and state.
Takedown and lift send the owner a new summary but leave its `updated_at`
alone, so operator actions do not reorder the owner's list. An owner's publish
or unpublish does update it.

## Rollout and recovery

Sharing is available when this User Service build is deployed. Ship account
deletion before deploying sharing. Apply the accounts migration before running
the moderation commands.

This release is roll-forward only. The previous build reads stored library
summaries and revision envelopes with `UnmappedMemberHandling.Disallow`. Writes
that use no guide or sharing feature keep the old shape, but the first publish,
takedown, text block or description writes a summary or envelope that build
rejects. The guide migration's `Down` also refuses to run. No switch disables
guide or sharing writes, so fix forward instead of rolling back.

The accounts migration adds `playlists.track_count` with a default of 0 and
backfills it. Servers still running the previous build during the rollout
write new or edited playlists without updating it. After the rollout, repair
those rows on the writer:

```sql
UPDATE user_data.playlists AS playlist
SET track_count = live.track_count
FROM (
    SELECT p.id, count(e.id)::integer AS track_count
    FROM user_data.playlists AS p
    LEFT JOIN user_data.playlist_entries AS e
        ON e.playlist_id = p.id AND NOT e.deleted AND e.track_id IS NOT NULL
    GROUP BY p.id
) AS live
WHERE playlist.id = live.id AND playlist.track_count <> live.track_count;
```

The repair changes only the stored count. Library summaries already sent keep
their old `track_count` until the playlist's next summary-writing change.

Cloudflare needs a cache eligibility rule for the public snapshot GET route.
Bypass requests carrying cookies or authorization and honor the origin's
`CDN-Cache-Control: max-age=60`. The browser receives `Cache-Control: no-cache`.
Origin 404s, authenticated responses, and mutations use `private, no-store`.
Verify a cache hit for a public snapshot through Cloudflare, then take down
that playlist. Request the same URL without cache busting. It should return
404 within 60 seconds of the successful takedown.

For an account database restore, stop origin traffic and let public snapshots
expire before reopening it. Rotate the history generation using the existing
[restore procedure](private-playlists.md#after-restoring-the-user-service-database). A copied playlist
and its receipt are restored together. If an older backup predates both, a
retry can create a copy only if the restored source is public.
