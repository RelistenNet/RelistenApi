# Preview auth reload proof

## Purpose and context

The local proof passes on OpenIddict 7.6.0: one running User Service can add two exact preview origins, authenticate both, and retire one while canonical login and the other preview continue. The change is prepared for review on `codex/private-preview-auth-reload`. The complete User Service test suite passed **195/195 with zero skips**. Nothing has been pushed or deployed.

Private previews will share the production account backend. Their browser origins must change without restarting the single User Service. This experiment uses disposable local PostgreSQL databases and synthetic development identities. It exercises the normal application pipeline and OAuth code exchange through ASP.NET TestServer, with an in-process backchannel replacing network transport.

## What changes

`WebOriginRegistry` owns the fixed origins and an immutable, validated preview snapshot. `PreviewOriginReload.ApplyAsync(revision, origins)` is the local entry point. It requires increasing revisions, permits an identical retry, caps the active set at four, and accepts only exact `https://pr-<positive-integer>.preview.relisten.net` origins.

A reload executes these steps:

1. Validate the candidate; serialize reload with browser-session creation.
2. Keep registration IDs for continuously active origins; assign a new ID to each newly added origin.
3. Construct candidate OpenIddict client options without removing the working options.
4. In one database transaction, reconcile only managed preview callbacks on the existing web client and revoke sessions for retired origins. Include persisted callbacks when identifying retirement after restart.
5. Atomically publish the prepared client options, then publish the active origin snapshot.

Fixed canonical/local registrations, provider registrations, secrets, grants, keys, and callback paths retain their existing configuration. Startup still rejects unexpected baseline client drift. `AccountsApplication` exposes the existing production bootstrap so tests use the actual middleware and controllers.

## Why this approach

OpenIddict reads client registrations through its monitored options. Candidates are built through the standard options factory, and a small implementation of its options-cache interface publishes a complete replacement under a lock. This avoids mutating a registration list while requests use it and preserves working auth if candidate construction keeps failing.

Each preview registration has an incarnation suffix. Removing and re-adding the same hostname must not revive an unfinished login. A supported handler rejects validated state for a retired incarnation before OpenIddict's missing-registration lookup. Session creation checks that same authenticated registration ID under the retirement lock, covering a callback already authenticated before removal. Continuously active preview registrations retain their IDs so their in-flight logins survive unrelated changes.

## Verification

Run a local PostgreSQL instance using the repository's existing development setup, then run:

```sh
dotnet test RelistenUserServiceTests/RelistenUserServiceTests.csproj \
  --filter 'FullyQualifiedName~TestPreviewOriginReload|FullyQualifiedName~TestPreviewOriginPolicy' \
  --logger 'console;verbosity=normal'
```

The database fixture creates and drops uniquely named databases. Check that **zero tests are skipped**: the fixture can skip when local PostgreSQL is unavailable. Configuration sources are cleared in the proof, external auth calls are blocked, and no real OAuth credentials participate.

The protocol cases verify canonical and two preview logins; canonical and unrelated preview callbacks spanning reload; shared favorite writes; host-bound session replay rejection; cross-preview CSRF rejection; session renewal; switch-account and logout; malformed/stale snapshot rejection; persistent options-construction failure; database rollback and application-cache eviction; retirement after process restart; and both pending and already-authenticated callbacks crossing removal/re-add.

Four fault experiments disabled one fix at a time. The corresponding regressions failed when persisted retirement origins, registration incarnations, preservation of working options, or the registration check inside session creation were removed. Restoring the fixes made the suite pass. Two-pass independent review identified the recovery and concurrency cases now covered by those tests.

## Operational considerations

This is a **single-process proof**, matching the current one-replica User Service. The retirement lock and options snapshots are process-local. Scaling requires a design for coordinating activation and session creation across replicas; this change does not establish that guarantee.

The optional projected-file loader is disabled until `Accounts__PreviewOriginsFile` names a file. It reopens that path every two seconds, following atomic ConfigMap projection replacement. Missing, malformed, stale, oversized, or dependency-failing snapshots retain the last active configuration and are retried. A valid snapshot with a newer revision and `"origins":[]` deliberately retires every active preview origin; a missing file or invalid snapshot preserves the last active set. It logs only state transitions and active revisions. The source accepts at most 16 KiB, schema version 1, required exact property names, and no duplicate/unknown properties. Mount the containing directory without `subPath`.

```json
{"version":1,"revision":42,"origins":["https://pr-138.preview.relisten.net"]}
```

`PreviewOriginFileLoader.Status` supplies the applied revision and source state in-process. There is no HTTP diagnostic or remote mutation endpoint yet. Private revision acknowledgment remains part of gateway integration. The hosted-loader test verifies atomic replacement, invalid input, recovery, file removal, and continued canonical/preview auth. An active preview cookie remains usable after a restart if the same origin is restored; origins omitted from the new snapshot are revoked using persisted callback state. Pending login completion across process restart was not tested; fresh registration IDs are assigned after restart, so callers should restart unfinished preview logins.

Real Google/Apple login, projected ConfigMap propagation, private gateway header handling, Tailscale access denial, and TLS reload still require integration/pilot verification. The local proof does not establish public-network isolation. Test restore also reports advisories on existing cryptography package versions; dependency remediation is separate from this experiment.

## Review guidance

Review the order of publication and retirement, the transaction/cache rollback, and the incarnation check at session creation. The bounded projected-file loader is also prepared locally. The next step is private gateway/resource preparation, including restricted revision acknowledgment. Production rollout and any push remain subject to Alec's sign-off.
