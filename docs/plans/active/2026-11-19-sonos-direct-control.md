# Play a Relisten queue on Sonos from mobile

Build and exercise the production server code locally first. A signed-in listener should be able to shuffle a playlist by recording, move playback to a room at the current track and position, close Relisten, and hear the rest of the queue in order. On reopening the app they should see what the room is actually playing. Deliver mobile first. Design and test the same server API for a future web controller. Keep browsing the existing Relisten catalog in the Sonos app anonymous.

The Office prototype played a real segment-shuffled playlist using the published Relisten service. It used legacy Sonos IDs assembled by the harness. The first implementation milestone is a UUID-backed recording that the real User Service can hand off and the speaker can resolve.

## Catalog IDs for app-started queues

The app sends existing `track_` IDs. Store those IDs with the queue occurrences. Resolve catalog availability and metadata in the User Service before taking over the room. Wrap the resolved V4 track ID in a new anonymous SMAPI object ID, proposed as `TrackU:{trackId}` (for example `TrackU:track_4rc5e1za2bvsrnnr0edzzj8rfs`), under the configured Sonos service ID. The existing `GET /api/v4/source-tracks/{trackId}` returns the stream URLs and catalog references. The live endpoint returned the Cornell “Minglewood Blues” recording with its MP3 URL. Use the existing V4 artist catalog to map the returned artist ID to a slug for the adapter’s audio-proxy rewrites. Use the UUID resolver for `getMediaURI` and the track metadata handlers, with fixtures for resolution, unavailable tracks and legacy IDs. The queue item ID identifies one occurrence. The SMAPI object ID identifies the recording and can repeat.

Qualify the UUID form on Alec’s Office before building mobile. Register a separate test SMAPI service for the updated adapter, point that adapter at the V4 catalog, and add the test service to the test household. Configure the local User Service to emit that test service ID. If a test registration is unavailable, qualify the adapter and catalog change through the approved Sonos preview process before sending UUID-form items to a speaker. Compare it with the proven legacy form for the same catalog track. Review the existing stale-source fix for legacy favorites and queues as separate compatibility work. The UUID lookup depends on stable catalog UUIDs and should follow the catalog’s existing availability and redirect rules.

In parallel with local development, recover access to Relisten’s content integration and ask Sonos which Control client, application association and review it requires for our published anonymous service to offer catalog-ID Cloud Queue from the Relisten app. Include the tested calls and ask explicitly whether a service account ID or account matching is required in production. If Sonos requires a different arrangement, qualify that arrangement in preview before promising this feature. A requirement to change anonymous browsing reopens the product decision. [Partner roles](https://docs.sonos.com/docs/partner-account-types) · [Service submission](https://docs.sonos.com/docs/submit-your-service)

## 1. Run the real server locally

Use the local catalog and accounts databases from the [local development guide](../../../local-dev/README.md). Implement the app-facing control API and player-facing Cloud Queue endpoints in `RelistenUserService` in the `RelistenApi` repository. It owns Sonos grants, immutable queue snapshots, command receipts and observed playback state. On handoff it passes the player a `queueBaseUrl` routed directly to the User Service. Each authenticated window request reads that snapshot and returns ordered Relisten catalog IDs. In production, route the player and OAuth endpoints under `https://accounts.relisten.net/sonos/` and constrain those paths to the accounts host in `HostBoundaryMiddleware`. The speaker resolves each recording through the anonymous `getMediaURI` in `relisten-sonos`, extended to accept the UUID form. The Office prototype exercised this split using the local Python harness as the queue server.

Give the local User Service a stable development accounts hostname and route it through an HTTPS tunnel. Include that hostname in the ASP.NET host and forwarded-host allowlists and route only the following inbound Sonos endpoints to the local User Service. Keep registration, hostnames, encryption keys, databases and client credentials isolated by environment. Use a physical phone with a development build pointed at the local Relisten account API.

| Address to configure | Caller and purpose |
| --- | --- |
| OAuth Redirect URI, e.g. `https://<dev-host>/sonos/oauth/callback` | The listener’s system browser returns the authorization code to the User Service. Register the exact URI on the dev client. |
| Event Callback URL, e.g. `https://<dev-host>/sonos/events` | Sonos posts signed notifications to the User Service. Route both the registered base and appended event paths directly over HTTPS. Register it on the same key before subscribing. |
| Queue base URL on the dev host, under `/sonos/cloud-queue/{queueId}/` | The User Service validates the player’s credential against the requested snapshot before serving it. Send the URL and a separate per-queue credential in the load command. Audio URLs must also be reachable from the speaker. |

Start with two real remotely playable tracks from the local catalog. Complete OAuth, display household/group names, select a group explicitly, then load both tracks via a minimal local client. Record sanitized responses, player requests, transitions and the tested model, firmware and protocol version. Prove a signed event arrives by pausing from the Sonos app. If queue playback works but events fail, debug callback registration, signature inputs and proxy path handling independently. [Authorize](https://docs.sonos.com/docs/authorize) · [Subscribe](https://docs.sonos.com/docs/subscribe)

## 2. Build the account API

Proposed routes below share `/v1/integrations/sonos`. Require a native bearer token with a reviewed Sonos control policy for the mobile release. Resolve the Relisten account on every request. Use the same account-scoped contracts for future web access. Enabling web control needs a reviewed capability, explicit route and relay allowlists, and browser origin and CSRF tests. The OAuth return is a separate browser endpoint authorized by one-time state. Validate current household/group access server-side. Sharing a household alone never grants access to another Relisten user’s private queue. [Relisten account API](../../api/accounts.md) · [Browser sessions](./2026-08-26-web-session-foundation.md)

| Operation | Request and observable result |
| --- | --- |
| Link | `POST /connect` returns a Sonos consent URL. Bind expiring, single-use state to the account, initiating login session and an allowlisted return destination. The callback exchanges the code server-side and returns the user to mobile or web. `GET /connection` reports linked, unlinked or reconnect required. |
| Choose a destination | `GET /groups` returns authorized households and their current groups, room names and IDs. Group membership can change while the picker is open. Recheck before takeover. |
| Move playback | `POST /handoffs` accepts a client request UUID, group, queue UUID, ordered occurrences with mobile occurrence IDs and track IDs, selected occurrence and position in milliseconds. Return an opaque playback handle, current item mapping, omitted occurrences and operation state. `GET /handoffs/{requestId}` lets either authorized client resolve an interrupted response. |
| Observe playback | `GET /playback` lists this account’s active handles and destinations. `GET /playback/{handle}` returns session state, current occurrence, track metadata, position at observation time, capabilities, volume and a version. Include stale, evicted and ended states so a resumed client can act correctly. |
| Control playback | `POST /playback/{handle}/commands` accepts a command UUID, expected version and desired play/pause, seek, skip target or group volume. Return updated state or a pollable operation. `GET /playback/{handle}/commands/{commandId}` reads the result. |
| Disconnect | `DELETE /connection` removes the account’s Sonos grant, invalidates its playback handles and queue credentials, and cleans up subscriptions. Report an uncertain remote stop when Sonos is unreachable. |

Store grants in `sonos_grants` and single-use state hashes in `sonos_oauth_states`. Protect tokens with a Sonos-specific Data Protection purpose and retain keys needed to read existing grants. Use a credential-safe HTTP client and serialize refresh on demand across replicas per grant. Store rotated tokens atomically. Treat withdrawn consent as reconnect required. Bind handles to the account and record the creating Relisten session for revocation. An authenticated second session for the same account may operate a valid handle. Validate the creating session on every command and subsequent player request. Sign-out blocks further authorized requests for that session. Measure how much audio a speaker can still play from an already fetched window. Disconnect and account deletion invalidate all grants and queue credentials for the account. Use accounts EF migrations and user foreign-key cascades. Persist handles, queues, queue items, operations and subscriptions in the account database. Exercise two sessions of one account and a different account against the same routes, including after sign-out.

## 3. Preserve the queue and recover from interrupted controls

Mobile sends its effective queue order from `queue.orderedTracks` using the existing queue-instance occurrence identifiers. Segment membership has already done its job when mobile computes the order. The server preserves that order and allocates one Sonos item ID per occurrence, including repeats of the same source track. Check catalog availability before taking over the room. Fail an unplayable selected item with `422 current_item_unavailable`. Return the identities of omitted future items. Load the selected item ID at the supplied playhead. A later mobile edit requires an explicit replacement handoff.

Persist the snapshot and handoff intent before calling Sonos. Record dispatch before each external effect. A timeout after session creation or queue load can mean the room already changed. Inspect the group, session, account-specific opaque app context and loaded item before completing or failing the receipt. If still uncertain, return a pollable `202` with the receipt URL. Persist prepared, dispatching, committed and failed states. Keep receipts for the published retry lifetime and preserve unresolved dispatches through reconciliation so cleanup cannot turn a retry into a second takeover. An exact retry reuses the operation. Changed input under the same UUID conflicts. Apply the same rule to controls. Resolve next/previous once into an absolute item ID so a retry cannot skip two songs. Serialize conflicting operations for one handle and reject stale preconditions.

Implement the v2.3 player routes under the queue base. `GET /context` serves container metadata and playback policies. `GET /version` serves context and queue versions. `GET /itemWindow` returns the ordered window, its boundary flags and per-occurrence IDs, with the track’s SMAPI object ID, title, artist, album, art when available, duration and content type. `POST /timePlayed` accepts the required player reports. Store a hash of the queue credential and set `useHttpAuthorizationForMedia=false`. Apply the July plan’s bounded snapshot lifetime, availability overlay, request limits and expiry behavior. A phone going offline leaves playback running. A player report acknowledges the transport event only. Sonos listening history is a separate feature. The existing adapter can also receive SMAPI play reports and forward them to the catalog. During qualification, measure a normal play and a Cloud Queue play separately, and check for duplicate catalog counts. Preserve existing SMAPI reporting semantics until that behavior is understood. [July architecture and queue contracts](../../architecture/2026-07-18-relisten-identity-user-data-and-sonos-architecture.md) · [Cloud Queue playback](https://docs.sonos.com/docs/cloud-queue-play-audio)

Subscribe to playback, metadata and playback-session events for the active destination, plus group volume and topology changes used by the UI. Verify `X-Sonos-Event-Signature` using the registered key and Sonos’s documented inputs. Map the subscription and household to the active resource before updating state. Acknowledge promptly after durable acceptance. Deduplicate repeated deliveries and reconcile with Sonos on gaps, stale observations, restart or subscription loss. Confirm the subscription lifetime and renewal procedure with Sonos before choosing a cadence. Refresh subscriptions after relevant topology changes and remove unused ones. Assign durable expiry and reconciliation work to the existing Temporal worker where needed. Request-time checks enforce revocation and expiry while cleanup is pending. Persist eviction immediately and stop issuing commands to the lost session. A new takeover requires a listener action. For launch, poll the shared state endpoint while visible and fetch fresh state on foreground. Derive display progress from the observed position and time between reads. Measure external pause and takeover latency on a physical phone before deciding whether to add a client push channel. [Subscribe](https://docs.sonos.com/docs/subscribe) · [Playback sessions](https://docs.sonos.com/docs/playback-sessions)

## 4. Mobile experience

Offer **Play on Sonos** in the existing Now Playing action menu. Show the destination in the existing output label. A signed-out listener can sign into Relisten from here. A signed-in listener sees **Connect Sonos** once, consents in the system browser, and returns to the room picker. Canceling any step leaves current playback alone.

Show rooms grouped by household and identify already grouped speakers as one destination. Selecting **Play in Living Room** explicitly confirms where playback will move. Show the current source and warn if the room is playing something else. Pause phone/Cast when dispatching. During an ambiguous response show **Connecting to Living Room…** and reconcile the receipt. Restore the previous local source and position on proven pre-commit failure. On confirmed handoff end the local playback instance and show **Playing on Living Room** with remote play/pause, previous/next, seek and group volume. Use the existing output label in `relisten/player/ui/player_output_route.tsx` and render observed remote state in Now Playing.

Use the existing `PlaybackDriver` and `PlaybackSource` model for remote playback. Read skip targets from the server’s snapshot and occurrence mapping. Keep the active snapshot immutable and require an explicit replacement to send edits. Disable local shuffle and repeat controls while Sonos is active, and leave gapless preload to Sonos. Suppress mobile history qualification for Sonos progress. Review lock-screen and CarPlay dispatch explicitly before wiring them to the new source. Clear the control view through the existing playback account-transition effects. Use the existing system-browser auth session and add the Sonos return route to the mobile app-link configuration.

Closing and reopening Relisten restores the active destination from the server. If another controller takes over, show **Living Room is playing something else** and end remote controls. If state is stale, show **Reconnecting…** and re-fetch before enabling commands. **Play on this device** is an explicit transfer using the last confirmed occurrence and position. Resolve remote pause before starting local audio, or surface the uncertainty and let the listener choose. Keep **Disconnect Sonos** in Account settings. Defer web screens until after mobile qualification.

## 5. Qualify locally, then promote

1. **Account and events.** Link on a physical phone. Verify the native API with two sessions of the same account. Reject a different account, a replayed OAuth callback, a forged event and browser-session access to the bearer-only control API. Verify that a player credential for queue A cannot read queue B or call the account control API. Pause from the Sonos app, interrupt event delivery, then recover the correct state. Revoke the grant and verify reconnect.
2. **Real playlist.** Start inside the second occurrence of a repeated recording at a nonzero offset. Use two multi-track segments and enough items to force a later window. Capture expected mobile order before handoff. Compare speaker item IDs, order and position, then take the phone offline and observe the later window. Repeat with room shuffle already enabled. Request `canShuffle: false` and verify the effective mode. Test audible continuity at a live segue with crossfade disabled. Record order and gapless audio as distinct outcomes.
3. **Recovery.** Interrupt each of create, load and skip after Sonos accepts it. Restart the server and retry the same request. Verify one takeover/skip, honest uncertainty when unprovable, and independent recovery from an unavailable track, an expired queue and another app taking over. Confirm reports create no listening-history rows.
4. **Preview and production.** Once Sonos confirms eligibility, configure the approved Relisten-owned client, permanent HTTPS redirect and event URLs, public queue base routed to the User Service, and encrypted secret storage. Deploy the same code exercised locally. Dev grants belong to the dev client. Link again with production credentials on a permitted test household. Verify queue-window fetches through a rolling deployment. The live User Service currently has one replica, zero surge and one allowed unavailable pod. Qualify a readiness-gated rollout with overlapping replicas and compatible migrations. Re-run playback and eviction with actual recordings and verify anonymous Sonos-app browse/play independently.
5. **Release and recovery.** Ship the mobile UI after preview passes. Send link and refresh failures, handoff outcomes, callback rejections and window failures to the User Service’s structured logs with sanitized correlation IDs. Bound public request sizes and rate-limit callback and player routes. Use a server configuration switch to disable new handoffs while active queues remain available through expiry and disconnect stays available. Keep production changes independently reversible from anonymous SMAPI.

The first deliverable is a local real-track handoff with a signed external pause event. The release decision needs both a successful test under the approved partner configuration and Sonos confirmation of that configuration. Web UI can follow against the published account API.

## Appendix. Prototype evidence

### What the personal integration established

Local evidence came from the scratch harness at `~/.codex/worktrees/sonos-cloud-queue-spike/`. Repeat qualification through the real User Service.

On November 19, 2026, a listener authorized the personal **Relisten Dev** Control client and selected one group. A local server behind an HTTPS tunnel called `createSession` without a service `accountId`, then `loadCloudQueue` with its queue base URL. The player requested context, an item window and four synthetic MP3s by direct HTTPS URL. Polling observed `PLAYING` for B1 → B2 → A1 → A2. The listener heard the audio and saw A2 attributed to Relisten Dev. The order was supplied by the harness. The run used neither the mobile shuffle implementation nor actual Relisten recordings. Event delivery was outside this test.

Sonos explicitly limits Cloud Queue to content integrations in its API reference. Its playback guide also documents direct media URLs as a supported item representation. The first statement concerns who may use the API. The second explains how an eligible integration specifies audio. The test establishes behavior for one key and household. Public production eligibility remains open. [Cloud Queue API reference](https://docs.sonos.com/reference/about-cloud-queue-api) · [Cloud Queue playback](https://docs.sonos.com/docs/cloud-queue-play-audio)

### Reuse the published Relisten catalog

A private playlist can contain public recordings. Relisten authorizes access to the playlist and supplies its effective order through the Cloud Queue. The existing anonymous content integration already gives each recording a Sonos browse ID and implements `getMediaURI` to resolve that ID to the chosen source’s stream. A repeated recording can reuse the same catalog object ID while each queue occurrence gets its own item ID.

On November 19, 2026, the personal Relisten Dev Control client loaded “Minglewood Blues” (Grateful Dead, Cornell, May 8, 1977) twice on the Era 300 in Alec’s Office. The speaker listed Relisten as service `270`. The published anonymous adapter returned the browse ID `Track:grateful-dead:1977:1977-05-08:4152204:5182438`.

| Queue item supplied | Observation on the speaker |
| --- | --- |
| Direct Archive.org MP3 URL | The track URI was the Archive.org URL. Transport reported PLAYING and advanced from 0:00 to 0:01. |
| `track.id` with service `270` and the browse ID, omitting `mediaUrl` and an explicit service account ID | The track URI was a Sonos service URI for the exact recording. The live adapter logged `getMediaURI` for the same browse ID. Transport reported PLAYING and advanced from 0:00 to 0:01. |

The harness used the existing numeric Sonos object ID. Production will use the UUID form described above and needs a new speaker test. This one-household result establishes that the anonymous adapter can resolve a recording for a Relisten-app Cloud Queue. Confirm the approved production arrangement with Sonos and qualify first use on a household that has never added Relisten. [Sonos playback objects](https://docs.sonos.com/docs/playback-objects) · [Account matching](https://docs.sonos.com/docs/account-matching)

### Segment-aware shuffle on a real speaker

The local User Service database contained a saved playlist, **Cornell First Set Guide**, with two authored segments. The local harness ran mobile’s `shuffleKeepingGroups` on its saved entries and segment memberships, selecting a shuffle that moved the second segment first. All four items used service 270 and their catalog object IDs. The Office reported normal Sonos play mode.

| Order | First segment | Second segment |
| --- | --- | --- |
| Authored | Minglewood Blues → Loser | El Paso → They Love Each Other |
| Mobile shuffle and observed Sonos playback | El Paso → They Love Each Other | Minglewood Blues → Loser |

The listener confirmed hearing “El Paso.” After 30 seconds on the first track and at least ten seconds on subsequent tracks, the harness sought near each of the first three endings. Sonos advanced on its own across all three boundaries and reported PLAYING on every expected catalog item. The harness paused during “Loser.” This qualifies the four-item order from the mobile shuffle helper through the catalog-ID Cloud Queue. Mobile UI handoff, longer queues across windows, inherited native shuffle, and audible gaplessness still need their own checks.

## Run the local User Service milestone

Start the normal local account stack. Set `Sonos__Enabled=true`, `Sonos__PublicBaseUrl` to the HTTPS tunnel origin, `Sonos__ServiceId` to the test SMAPI service ID, and provide the development client ID and secret through `Sonos__ClientId` and `Sonos__ClientSecret`. Enable the feature only in the local Development profile. Preserve the tunnel authority, route only `/sonos/*` through it, and forward the callback response headers, including `Location` and `Cache-Control`. Keep the regular local accounts host for the authenticated client calls. Point the User Service and test adapter at the same V4 catalog. For a local catalog, set `Accounts__CatalogBaseUrl=http://localhost:55368/` and the adapter’s corresponding catalog URL.

1. Call `POST /v1/integrations/sonos/connect` with a native account bearer token. Open the returned `authorization_url` and consent. Verify the redirect on the tunnel and check `GET /v1/integrations/sonos/connection`.
2. Call `GET /v1/integrations/sonos/groups` and use the exact Office group ID. Submit `POST /v1/integrations/sonos/handoffs` with `request_id` (a fresh UUID), `group_id`, `track_ids` (the ordered V4 IDs), `start_index` (zero-based), and `position_millis`. The dev endpoint accepts up to 5,000 MP3-playable occurrences. The User Service resolves distinct catalog references in batches of at most 500 and preserves the submitted order and duplicates. The speaker controls its window size independently. Reuse the request ID for a retry of the same handoff.
3. Confirm the Office plays both tracks. Pause from the Sonos app and check `GET /v1/integrations/sonos/handoffs/{request_id}` for `playback_event_state`. The development receipt records the last matching playback event. For an uncertain dispatch, poll the receipt. The server checks whether the room reports an item from that queue. A repeated request ID never resends the takeover. The dispatch reservation expires after 45 seconds so the listener can explicitly try again.

The development test uses a disposable PostgreSQL database and simulated Sonos and Catalog responses. Real speaker qualification needs the updated test SMAPI service and a dev key with both the OAuth redirect and event callback registered.

### Office verification

On November 19, 2026, the local .NET User Service exchanged the Sonos authorization code for the Relisten Dev Control key through the HTTPS tunnel. It discovered Alec’s Office and loaded a two-track Cloud Queue using the published Relisten SMAPI service 270. The Era 300 reported PLAYING for the `TrackU` item for “Minglewood Blues,” starting at the submitted 350-second position, and advanced on its own to the `TrackU` item for “Loser.” After a pause from the Sonos app, the speaker reported paused at 0:31 into “Loser” and the local handoff receipt reported `PLAYBACK_STATE_PAUSED` from the verified Sonos event. The Redirect URI and Event Callback URL on the development key point to the local User Service through the tunnel.

The Office System card showed the track title and a generic image during that run. The queue payload then carried only the title, duration, media type and track ID. New snapshots include the artist name, show date and venue in the album label, and a public show artwork URL. The integration test checks those fields in the player’s item window. Verify their rendering in the Sonos app after a fresh handoff.

### Local playback API for mobile

Use the local User Service on `codex/sonos-direct-control` with its accounts migrations applied. Use the same native Relisten bearer token and accounts origin as handoff. On the development Mac the accounts origin is `https://accounts.relisten.localhost:5443`, with the local development CA trusted. The HTTPS tunnel serves the Sonos callback and queue routes. All paths below are relative to `/v1/integrations/sonos`.

| Route | Result |
| --- | --- |
| `POST /connect?return_to=mobile` | Open the returned `authorization_url` in the system authorization session. Wait for `net.relisten.mobile:/sonos/callback?result=connected\|failed`, then refresh `GET /connection`. Refresh after browser cancellation too. The callback carries no credentials. |
| `GET /connection`, `DELETE /connection` | Read `linked`. Disconnect returns `204` and clears Relisten’s grant, pending links, and queue access. The speaker may finish buffered audio. |
| `GET /groups` | Lists `household_id`, `id`, and `name` for each authorized group. Use the group ID the listener chose. |
| `POST /handoffs` | Submits the ordered catalog track IDs and current position. See the body and receipt below. |
| `GET /handoffs/{request_id}` | Follows a pending handoff. The same selection and request UUID can also be submitted again after a lost response. |
| `GET /playback` | Returns a JSON array of currently confirmed Relisten-owned playback objects. An empty array means there are no confirmed rooms. Use this to restore playback when the app reopens. |
| `GET /playback/{handle}` | Returns `playback_handle`, `group_id`, `group_name`, `status`, and `queue_length`. An active result includes `playback_state`, `position_millis`, `observed_at`, and `current_occurrence` with `item_id`, zero-based `queue_index`, `track_id`, and display metadata. Disable transport during `unconfirmed` and release the destination on `evicted`. |
| `GET /playback/{handle}/volume` | Returns `volume` (0–100), `muted`, and `fixed`. These describe the entire selected group. |
| `POST /playback/{handle}/commands` | Submit a command with a client-generated UUID in `command_id`. A `202` returns a receipt and its URL in `Location`. |
| `GET /playback/{handle}/commands/{commandId}` | Reads the receipt. Use the receipt’s `playback_url` to refresh what the room is doing. |

Submit a handoff with this body:

```json
{
  "request_id": "2c05ca2f-3b84-4fc8-8b39-8fa8cc793730",
  "group_id": "<id returned by GET /groups>",
  "track_ids": ["track_4rc5e1za2bvsrnnr0edzzj8rfs", "track_55z7a4qtqec6d5cssxwz7z8cvb"],
  "start_index": 0,
  "position_millis": 42000
}
```

Send the mobile queue's effective order after segment-aware shuffle, including repeated track IDs. Use the zero-based index of the selected occurrence. The local API accepts 1 to 5,000 streamable occurrences and returns `422` for an unavailable recording or an invalid position. A known-duration track requires a starting position strictly below its duration.

Handoff receipts contain `request_id` (UUID), `status`, `playback_handle` (UUID or null), and `playback_event_state` (string or null). On `202 dispatching`, poll `Location` and keep local playback paused while the outcome is unknown. On `200 loaded`, adopt the returned handle and refresh its playback state. Resume the prior local source automatically only on a definitive refusal before takeover, such as `failed` or a first submission rejected with `422`. Treat `interrupted`, `evicted`, `revoked`, lost responses, and unrecognized success bodies conservatively. Check the receipt or discovery again before letting the listener explicitly choose a room or resume on the phone.

A `409 sonos_handoff_busy` means a request already reserves the room. The busy response proves this submission did not take over. Resume the phone after a busy response only if there is no earlier attempt whose outcome remains uncertain. If there is one, keep the phone paused and check that receipt and discovery. The dispatch reservation lasts 45 seconds. A Sonos call already in flight may finish or time out under the room lock. After the reservation expires, receipt polling can still recognize a load that reached Sonos. An identical POST returns the saved status, and `GET /handoffs/{request_id}` reads the room to reconcile it. A new UUID represents a new listener action. Queue access and receipts expire after 24 hours for newly created queues. Bind every retry and poll to the initiating Relisten account scope and discard its results on account switch.

An active playback response includes `current_occurrence` with `item_id`, `queue_index`, `track_id`, `name`, `duration_millis`, `artist`, `album`, and `image_url`. Duration, artist, album and art can be null. `position_millis` can also be null. Use `observed_at` (ISO 8601) for playhead estimation. Inactive responses may have null `playback_state`, `position_millis`, `observed_at`, `queue_length`, and `current_occurrence`. Treat `unconfirmed` as temporary and disable controls. Release local ownership of the handle on `evicted`, `revoked`, `failed`, `interrupted`, or when a direct read returns `404`.

Command fields by action:

- `play` and `pause` should include `expected_item_id` from the current occurrence. The legacy action-only body still returns a playback URL in `Location`.
- `seek` requires `expected_item_id` and an absolute `position_millis` within the track.
- `next` and `previous` require `expected_item_id`. The server moves one position in the submitted queue and preserves the room’s playing or paused state. Disable either action at its queue boundary.
- `volume` requires an integer `volume` from 0 to 100. `mute` requires boolean `muted`. Send the final slider value and refresh the group's volume after a change. These commands target the group. A switch of sources after the server reads the room can affect the new source, and a volume update can overwrite a simultaneous external mute change.

For example, send this body to `POST /playback/{handle}/commands` for an absolute seek:

```json
{
  "action": "seek",
  "command_id": "49290543-f7ef-4a77-b935-656082697e74",
  "expected_item_id": "f2918294-eb5b-47cf-93c8-c45e34df2fb7",
  "position_millis": 42000
}
```

Use the current occurrence's actual `item_id`. Errors can return a JSON object with a `code` and, for some rejected commands, `command_id`. Other errors can return Problem Details or an empty response. Branch on the HTTP status and the optional `code`.

A receipt contains `command_id`, `status`, `code`, `target_item_id`, and `playback_url`. `accepted` means Sonos acknowledged the request. `confirmed` means the server observed the desired state. `pending` means the effect is unknown, so refresh the visible controls from the room. An ambiguous seek can stay pending if the playhead has already moved. Retry an interrupted request with the same UUID and body. This retrieves or reconciles the receipt without reissuing a recorded command. Reusing the UUID with a different body returns `409 sonos_command_id_conflict`. Refresh the room on `409 sonos_playback_changed` or `sonos_playback_inactive`. Disable seek if position or duration is unavailable, and disable volume if `fixed` is true. Send one change at a time. A newer valid command for the same queue can supersede an unresolved receipt. On `sonos_reconnect_required`, refresh the link and offer Connect Sonos again.

Command receipt `code` and `target_item_id` can be null. A rejected POST can return `409` immediately. A subsequent receipt GET may return `200 rejected` with its reason. `sonos_command_unavailable` covers an out-of-range seek or a queue boundary, `sonos_volume_fixed` means the current group rejected volume changes, and `sonos_command_superseded` means a newer command was prepared before the prior result could be confirmed. Refresh the current state and require a fresh listener action for a new UUID.

The User Service issues a session-scoped `skipToItem` for the observed occurrence and offset, with `playOnCompletion` set to the requested state. This keeps a command on the original session during a takeover. Within that session, a seek or track change between the status read and command can move playback back to the sampled occurrence or offset. Measure that behavior before production.

On November 19, Alec’s Office reported “Loser” paused after the local server restarted. Discovery recovered the same queue and playhead. The API resumed it and paused it again. After a replacement handoff in the same room, the old handle returned `409` and the replacement kept playing until its own handle paused it. The test suite separately simulated Sonos rejecting the original session between the status read and command.

The Office also loaded a 24-occurrence queue made from real catalog IDs. Starting at the 20th occurrence, the local client paused, sought to 0:42, and moved to the 21st occurrence of the same recording. Repeating the Next request with the same command ID left playback on the 21st occurrence. Previous returned to the 20th. After a seek near its end and resume, Sonos advanced to the 21st without another transport command. The room also reported muted at its new volume and returned to its original level and mute state after the test.
