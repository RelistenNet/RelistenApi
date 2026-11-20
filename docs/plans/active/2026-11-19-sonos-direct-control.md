# Play a Relisten queue on Sonos from mobile

Build and exercise the production server code locally first. A signed-in listener should be able to shuffle a playlist by recording, move playback to a room at the current track and position, close Relisten, and hear the rest of the queue in order. On reopening the app they should see what the room is actually playing. Deliver mobile first. Design and test the same server API for a future web controller. Keep browsing the existing Relisten catalog in the Sonos app anonymous.

The Office prototype played a real segment-shuffled playlist using the published Relisten service. It used legacy Sonos IDs assembled by the harness. The first implementation milestone is a UUID-backed recording that the real User Service can hand off and the speaker can resolve.

## Catalog IDs for app-started queues

The app sends existing `track_` IDs. Store those IDs with the queue occurrences. Resolve catalog availability and metadata in the User Service before taking over the room. Wrap the resolved V4 track ID in a new anonymous SMAPI object ID, proposed as `TrackU:{trackId}` (for example `TrackU:track_4rc5e1za2bvsrnnr0edzzj8rfs`), under the configured Sonos service ID. The existing `GET /api/v4/source-tracks/{trackId}` returns the stream URLs and catalog references. The live endpoint returned the Cornell “Minglewood Blues” recording with its MP3 URL. Use the existing V4 artist catalog to map the returned artist ID to a slug for the adapter’s audio-proxy rewrites. Use the UUID resolver for `getMediaURI` and the track metadata handlers, with fixtures for resolution, unavailable tracks and legacy IDs. The queue item ID identifies one occurrence. The SMAPI object ID identifies the recording and can repeat.

Qualify the UUID form on Alec’s Office before building mobile. Register a separate test SMAPI service for the updated adapter, point that adapter at the V4 catalog, and add the test service to the test household. Configure the local User Service to emit that test service ID. If a test registration is unavailable, qualify the adapter and catalog change through the approved Sonos preview process before sending UUID-form items to a speaker. Compare it with the proven legacy form for the same catalog track. Review the existing stale-source fix for legacy favorites and queues as separate compatibility work. The UUID lookup depends on stable catalog UUIDs and should follow the catalog’s existing availability and redirect rules.

In parallel with local development, recover access to Relisten’s content integration and ask Sonos which Control client, application association and review it requires for our published anonymous service to offer catalog-ID Cloud Queue from the Relisten app. Include the tested calls and ask explicitly whether a service account ID or account matching is required in production. If Sonos requires a different arrangement, qualify that arrangement in preview before promising this feature. A requirement to change anonymous browsing reopens the product decision. [Partner roles](https://docs.sonos.com/docs/partner-account-types) · [Service submission](https://docs.sonos.com/docs/submit-your-service)

## 1. Run the real server locally

Use the local catalog and accounts databases from the [local development guide](../../../local-dev/README.md). Implement the app-facing control API and player-facing Cloud Queue endpoints in `RelistenUserService` in the `RelistenApi` repository. It owns Sonos grants, versioned queue snapshots, command receipts and observed playback state. On handoff it passes the player a `queueBaseUrl` routed directly to the User Service. Each authenticated window request reads that snapshot and returns ordered Relisten catalog IDs. In production, route the player and OAuth endpoints under `https://accounts.relisten.net/sonos/` and constrain those paths to the accounts host in `HostBoundaryMiddleware`. The speaker resolves each recording through the anonymous `getMediaURI` in `relisten-sonos`, extended to accept the UUID form. The Office prototype exercised this split using the local Python harness as the queue server.

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

Mobile sends its effective queue order from `queue.orderedTracks` using the existing queue-instance occurrence identifiers. Segment membership has already done its job when mobile computes the order. The server preserves that order and allocates one Sonos item ID per occurrence, including repeats of the same source track. Check catalog availability before taking over the room. Reject the handoff with `422` if any requested recording is unplayable. Load the selected item ID at the supplied playhead. Mobile can edit upcoming occurrences on the active handle while retaining the current item and the queue before it.

Persist the snapshot and handoff intent before calling Sonos. Record dispatch before each external effect. A timeout after session creation or queue load can mean the room already changed. Inspect the group, session, account-specific opaque app context and loaded item before completing or failing the receipt. If still uncertain, return a pollable `202` with the receipt URL. Persist prepared, dispatching, committed and failed states. Keep receipts for the published retry lifetime and preserve unresolved dispatches through reconciliation so cleanup cannot turn a retry into a second takeover. An exact retry reuses the operation. Changed input under the same UUID conflicts. Apply the same rule to controls. Resolve next/previous once into an absolute item ID so a retry cannot skip two songs. Serialize conflicting operations for one handle and reject stale preconditions.

Implement the v2.3 player routes under the queue base. `GET /context` serves container metadata and playback policies. `GET /version` serves context and queue versions. `GET /itemWindow` returns the ordered window, its boundary flags and per-occurrence IDs, with the track’s SMAPI object ID, title, artist, album, art when available, duration and content type. `POST /timePlayed` accepts the required player reports. Store a hash of the queue credential and set `useHttpAuthorizationForMedia=false`. Apply the July plan’s bounded snapshot lifetime, availability overlay, request limits and expiry behavior. A phone going offline leaves playback running. A player report acknowledges the transport event only. Sonos listening history is a separate feature. The existing adapter can also receive SMAPI play reports and forward them to the catalog. During qualification, measure a normal play and a Cloud Queue play separately, and check for duplicate catalog counts. Preserve existing SMAPI reporting semantics until that behavior is understood. [July architecture and queue contracts](../../architecture/2026-07-18-relisten-identity-user-data-and-sonos-architecture.md) · [Cloud Queue playback](https://docs.sonos.com/docs/cloud-queue-play-audio)

Subscribe to playback, metadata and playback-session events for the active destination, plus group volume and topology changes used by the UI. Verify `X-Sonos-Event-Signature` using the registered key and Sonos’s documented inputs. Map the subscription and household to the active resource before updating state. Acknowledge promptly after durable acceptance. Deduplicate repeated deliveries and reconcile with Sonos on gaps, stale observations, restart or subscription loss. Confirm the subscription lifetime and renewal procedure with Sonos before choosing a cadence. Refresh subscriptions after relevant topology changes and remove unused ones. Assign durable expiry and reconciliation work to the existing Temporal worker where needed. Request-time checks enforce revocation and expiry while cleanup is pending. Persist eviction immediately and stop issuing commands to the lost session. A new takeover requires a listener action. For launch, poll the shared state endpoint while visible and fetch fresh state on foreground. Derive display progress from the observed position and time between reads. Measure external pause and takeover latency on a physical phone before deciding whether to add a client push channel. [Subscribe](https://docs.sonos.com/docs/subscribe) · [Playback sessions](https://docs.sonos.com/docs/playback-sessions)

## 4. Mobile experience

Offer **Play on Sonos** in the existing Now Playing action menu. Show the destination in the existing output label. A signed-out listener can sign into Relisten from here. A signed-in listener sees **Connect Sonos** once, consents in the system browser, and returns to the room picker. Canceling any step leaves current playback alone.

Show rooms grouped by household and identify already grouped speakers as one destination. Selecting **Play in Living Room** explicitly confirms where playback will move. Show the current source and warn if the room is playing something else. Pause phone/Cast when dispatching. During an ambiguous response show **Connecting to Living Room…** and reconcile the receipt. Restore the previous local source and position on proven pre-commit failure. On confirmed handoff end the local playback instance and show **Playing on Living Room** with remote play/pause, previous/next, seek and group volume. Use the existing output label in `relisten/player/ui/player_output_route.tsx` and render observed remote state in Now Playing.

Route Now Playing controls through the selected output. Read Sonos skip targets and queue edits from the server’s occurrence mapping. Disable local shuffle while Sonos is selected, and leave gapless preload to Sonos. Use the room’s reported repeat mode for Sonos playback and keep the phone’s repeat setting separate. Suppress mobile history qualification for Sonos progress. Review lock-screen and CarPlay dispatch explicitly before wiring them to the new source. Clear the control view through the existing playback account-transition effects. Use the existing system-browser auth session and add the Sonos return route to the mobile app-link configuration.

On reopen, check the previously selected Sonos handle and refresh the room list. If another controller takes over, show **Living Room is playing something else** and end remote controls for that handle. If state is stale, show **Reconnecting…** and re-fetch before enabling commands. Follow the [output selection and transfer rules](#output-selection-and-queue-ownership) below. Keep **Disconnect Sonos** in Account settings. Defer web screens until after mobile qualification.

## 5. Qualify locally, then promote

1. **Account and events.** Link on a physical phone. Verify the native API with two sessions of the same account. Reject a different account, a replayed OAuth callback, a forged event and browser-session access to the bearer-only control API. Verify that a player credential for queue A cannot read queue B or call the account control API. Pause from the Sonos app, interrupt event delivery, then recover the correct state. Revoke the grant and verify reconnect.
2. **Real playlist.** Start inside the second occurrence of a repeated recording at a nonzero offset. Use two multi-track segments and enough items to force a later window. Capture expected mobile order before handoff. Compare speaker item IDs, order and position, then take the phone offline and observe the later window. Repeat with room shuffle already enabled. Request `canShuffle: false` and verify the effective mode. Test audible continuity at a live segue with crossfade disabled. Record order and gapless audio as distinct outcomes.
3. **Recovery.** Interrupt each of create, load and skip after Sonos accepts it. Restart the server and retry the same request. Verify one takeover/skip, honest uncertainty when unprovable, and independent recovery from an unavailable track, an expired queue and another app taking over. Confirm reports create no listening-history rows.
4. **Preview and production.** Once Sonos confirms eligibility, configure the approved Relisten-owned client using the settings below. Dev grants belong to the dev client. Link again with production credentials on a permitted test household. Verify queue-window fetches through a readiness-gated rollout with overlapping replicas and compatible migrations. Re-run playback and eviction with actual recordings and verify anonymous Sonos-app browse/play independently.
5. **Release and recovery.** Ship the mobile UI after preview passes. Send link and refresh failures, handoff outcomes, callback rejections and window failures to the User Service’s structured logs with sanitized correlation IDs. Bound public request sizes and rate-limit callback and player routes. Keep production changes independently reversible from anonymous SMAPI.

The User Service offers Sonos automatically when its Control credentials and service ID are configured. In production, provision `Sonos__ClientId` and `Sonos__ClientSecret` from the cluster secret and set `Sonos__ServiceId` to the approved published content service ID. Set all of these together. Partial configuration fails startup. The default public origin is the accounts host. Register `https://accounts.relisten.net/sonos/oauth/callback` and `https://accounts.relisten.net/sonos/events` on the production Control key. The existing accounts ingress serves the Cloud Queue routes on that origin.

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

Start the normal local account stack. Set `Sonos__PublicBaseUrl` to the HTTPS tunnel origin, `Sonos__ServiceId` to the test SMAPI service ID, and provide the development client ID and secret through `Sonos__ClientId` and `Sonos__ClientSecret`. Preserve the tunnel authority, route only `/sonos/*` through it, and forward the callback response headers, including `Location` and `Cache-Control`. Keep the regular local accounts host for the authenticated client calls. Point the User Service and test adapter at the same V4 catalog. For a local catalog, set `Accounts__CatalogBaseUrl=http://localhost:55368/` and the adapter’s corresponding catalog URL.

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
| `GET /groups` | Lists `household_id`, `id`, and `name` for each authorized group. Group rows by `household_id` and show the room names Sonos returns. Use the group ID the listener chose. |
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

Playback reads and discovery include the room’s effective `repeat_mode` (`off`, `all`, or `one`) and the `can_repeat` and `can_repeat_one` capability flags. A null field means the service could not determine the value for this playback handle. Refresh playback to pick up repeat changes made by another controller.

Command fields by action:

- `play` and `pause` should include `expected_item_id` from the current occurrence. The legacy action-only body still returns a playback URL in `Location`.
- `seek` requires `expected_item_id` and an absolute `position_millis` within the track.
- `next` and `previous` require `expected_item_id`. The server moves one position in the submitted queue and preserves the room’s playing or paused state. Disable either action at its queue boundary.
- `skip` jumps to a selected queue row. Send `command_id`, the current `expected_item_id` from playback, `expected_queue_version` from the queue being displayed, and `target_occurrence_id` from the tapped row. A different occurrence starts at zero and preserves the room’s playing or paused state. Selecting the current occurrence leaves its position alone.
- `volume` requires an integer `volume` from 0 to 100. `mute` requires boolean `muted`. Send the final slider value and refresh the group's volume after a change. These commands target the group. A switch of sources after the server reads the room can affect the new source, and a volume update can overwrite a simultaneous external mute change.
- `repeat` takes `command_id` and `repeat_mode` (`off`, `all`, or `one`). Enable `all` or `one` only when its corresponding capability is true. An active room can always request `off`. An unavailable mode returns `409 sonos_command_unavailable`. Poll the receipt and refresh playback to show the effective setting. Sonos applies the setting to the group, with the same source-switch race as volume. [Sonos play modes](https://docs.sonos.com/reference/playback-setplaymodes-groupid)

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

For Play and Pause, the User Service issues a session-scoped `skipToItem` for the observed occurrence and offset, with `playOnCompletion` set to the requested state. This keeps a command on the original session during a takeover. Within that session, a seek or track change between the status read and command can move playback back to the sampled occurrence or offset. Measure that behavior before production.

On November 19, Alec’s Office reported “Loser” paused after the local server restarted. Discovery recovered the same queue and playhead. The API resumed it and paused it again. After a replacement handoff in the same room, the old handle returned `409` and the replacement kept playing until its own handle paused it. The test suite separately simulated Sonos rejecting the original session between the status read and command.

The Office also loaded a 24-occurrence queue made from real catalog IDs. Starting at the 20th occurrence, the local client paused, sought to 0:42, and moved to the 21st occurrence of the same recording. Repeating the Next request with the same command ID left playback on the 21st occurrence. Previous returned to the 20th. After a seek near its end and resume, Sonos advanced to the 21st without another transport command. The room also reported muted at its new volume and returned to its original level and mute state after the test.

### Editable Sonos queue for mobile

Use the same native bearer token and accounts origin as the playback API. The paths below are relative to `/v1/integrations/sonos`. For Sonos output, render the server queue. The phone queue supplies the initial handoff order.

1. Send `POST /handoffs` with `request_id`, `group_id`, `start_index`, `position_millis`, and `occurrences`. Each occurrence contains `occurrence_id` and `track_id`. Use mobile's stable occurrence identifier, including for duplicated recordings. Send either `occurrences` or `track_ids`. Legacy handoffs still accept `track_ids` and the server assigns occurrence IDs.
2. After adopting a playback handle, load `GET /playback/{handle}/queue`. The response has `queue_version` (integer), `queue_length`, `refresh_pending` (boolean), and ordered `occurrences`. Each row has `occurrence_id`, `item_id`, `track_id`, `queue_index`, `name`, and optional display values `duration_millis`, `artist`, `album`, and `image_url`. Use the queue response's `track_id` when building edits. A playback response can report a canonical catalog ID after a redirect.
3. Read `GET /playback/{handle}` for the current item and playhead. `current_occurrence.occurrence_id` joins playback to the queue row. `queue_version` reports the server's version and `sonos_queue_version` reports the version Sonos says it has cached, when available. Refresh the queue when the server version changes or the handle changes.

Submit `PUT /playback/{handle}/queue` for one edit at a time. For example:

```json
{
  "command_id": "fc172a6f-5c47-4c8c-b2bb-9bc59b39f19a",
  "expected_version": 1,
  "expected_item_id": "7c98ac17-3ff4-40a9-81d2-0f8ff9e3261e",
  "occurrences": [
    {"occurrence_id": "playing-entry", "track_id": "track_4rc5e1za2bvsrnnr0edzzj8rfs"},
    {"occurrence_id": "new-up-next-entry", "track_id": "track_55z7a4qtqec6d5cssxwz7z8cvb"}
  ]
}
```

- Send the full order through the end of the queue. Take `expected_item_id` from playback's `current_occurrence.item_id` and wait to edit until there is a confirmed current item. Keep the prefix through the current track unchanged. **Clear Upcoming** submits just that prefix. Use a fresh occurrence ID for an addition. A removed occurrence ID cannot be reused on that handle, and an existing occurrence cannot switch recordings. The queue accepts up to 5,000 rows.
- A successful PUT returns `200` with `command_id`, `queue_version`, and `queue_length` for the saved edit. Retry a lost response with the same command ID and body. An identical replay returns the original receipt, including if a later edit has saved a newer version. Read the queue again for the latest order.
- A `409 sonos_queue_changed` means a newer edit won. A `409 sonos_playback_changed` means the observed track changed before the server checked the edit. Fetch both resources again and let the listener retry their intent. `sonos_queue_current_protected` means the proposed order changes the prefix through the current item. `sonos_queue_history_full` means a new handoff is needed because the current session reached its retained removal limit. Invalid or unavailable occurrences return `422`.
- Use `expected_queue_version` alongside `expected_item_id` on Next and Previous commands. The server also checks the version between preparing and dispatching these commands.
- To jump to a row, submit `skip` to `POST /playback/{handle}/commands` and follow the transport receipt. The receipt’s `target_item_id` is Sonos’s item ID. On `409 sonos_queue_changed` or `sonos_playback_changed`, refresh both resources before offering the jump again. `409 sonos_command_unavailable` means the requested occurrence is absent from that queue. Leave the current row highlight tied to confirmed playback.
- `refresh_pending` means the server still needs to notify Sonos. A delivered notification alone does not confirm that the player loaded the edited window. When Sonos reports an older version or no version, keep the saved order in the tray and treat room synchronization as unconfirmed. Keep every request and retry bound to the initiating account.
- The server checks the playhead before saving. Sonos can still advance before fetching the edited window. Keep removal of the current item out of the UI. Qualify the boundary race on a speaker before promising that all upcoming edits preserve uninterrupted playback.

#### Output selection and queue ownership

**Available in the User Service.** Each Sonos playback handle identifies one Relisten playback session in one Sonos group. Two phones signed in to the same Relisten account can read, control, and edit that handle. Each phone still has its own local queue. For example, if phone B removes an upcoming track from Alec’s Office, phone A sees the shorter order when it refreshes that room. Phone A’s local queue stays as it was. If both submit edits against version 5, the first save wins and the other phone refreshes before retrying the listener’s edit. Resubmitting the stale full order with version 6 could undo the first edit.

The creating phone can close the app or go offline. The handle remains usable while its creating Relisten login session is valid and the handle is live. Signing out on the creating phone invalidates that handle even if the other phone is still signed in. Signing out on the other phone removes that phone’s access. Already buffered audio may continue after revocation. **Disconnect Sonos** affects the account’s Sonos link across devices.

**Mobile work remaining.** Keep the selected output on each device, scoped to the signed-in account. Use `GET /playback` to discover existing room sessions. Today mobile falls back to the first result when it has no handle. Replace that fallback with explicit selection. Restore only this device’s previously selected handle if it is still active and there is no local or Cast playback to interrupt. With no selected handle, show the discovered sessions as room choices.

Selecting **Control room** opens the room’s live queue and sends no playback command. If the phone is already playing locally or on Cast, keep its main Now Playing controls on that output and show Sonos controls in the room view. Offer a separate **Play phone queue on Sonos** action, which loads a new queue and replaces the room’s current session. Leaving the room view lets the room keep playing. Clear the selected handle on account change and discard stale results on account or target change.

#### Moving playback between the phone and Sonos

- **Phone to Sonos.** Use the phone’s effective order, selected occurrence, and offset for the explicit handoff. Pause the current output during dispatch and adopt the returned handle on `loaded`. Keep the phone queue available while the room plays. Resume the prior playing output on definitive refusal. Keep it paused while the outcome is uncertain and reconcile the receipt. A Sonos handoff starts playback in the room.
- **Sonos to phone.** The current **This iPhone** / **This Phone** row is informational. Wire it to an explicit transfer, also available from the room view. Read the chosen handle and its queue, resolve tracks through the catalog, and remember whether the room was playing. If needed, pause using the observed `current_occurrence.item_id`. Read the latest room queue, resolve any newly added tracks, and confirm the same handle is paused with a fresh playback read. Use the snapshot when its `queue_version` matches playback, the observed occurrence is in that queue, and the paused `position_millis` is known. Stop any prior phone playback and replace its queue with this order at the matching occurrence and offset. Start local audio only if the room was playing before the transfer. A paused room transfers to a paused phone. Keep the effective order so a local shuffle setting cannot reorder it. If the pause or resume point is uncertain, keep the previous phone queue and playback intact. Tell the listener the transfer could not finish, including if the room is already paused.
- **After the transfer.** Clear the Sonos selection on this device. The last confirmed paused snapshot is the point from which the phone continues. The room may still appear in `GET /playback`. Later room edits or a resume by another controller apply to the room. If the creating phone signs out afterward, playback that already moved to this phone continues locally.

#### How Chromecast differs

Chromecast already switches Relisten’s playback driver to the Cast receiver. Relisten can adopt an existing receiver queue when it connects. During Cast playback it sends updates from the phone’s ordered queue to the receiver via `loadMedia`. On disconnect it tries to resume on the phone using the last reported Cast occurrence and offset. It uses the phone’s existing queue, so exact resume depends on finding that occurrence there. Cast supports multiple senders connected to one receiver through the SDK. Sonos phones share a Relisten server handle and edit its versioned order. Sonos needs its own return path to bring edits from the room back to the phone. For the first mobile pass, use the existing Cast disconnect or the Sonos transfer to return to the phone before selecting the other remote output. [Google Cast overview](https://developers.google.com/cast/docs/overview) · [Google Cast queueing](https://developers.google.com/cast/docs/android_sender/queueing)

Qualify on two phones. Start Sonos from phone A while phone B is playing locally and verify discovery leaves B’s audio and controls alone. Explicitly select the room on B, race two edits, and check that both devices converge on the saved order. Move playback to the phone after the other device edits the queue. Check the order, exact occurrence of a repeated recording, offset, and play/pause state. Repeat with a failed or unconfirmed pause and verify the phone never begins playing automatically. Sign out each phone separately and check how the remaining controller handles the resulting session state. On Cast, reconnect to a receiver with an existing queue and verify the existing resume behavior on disconnect.
