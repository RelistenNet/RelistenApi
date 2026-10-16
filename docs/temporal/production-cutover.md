# Test Temporal in production and switch ingestion

Bring up Temporal first, then test catalog imports during a controlled handoff from
Hangfire. Keep the API serving listeners and keep Hangfire's `default` queue for
popularity cache refreshes. This procedure changes ingestion ownership; committed
catalog writes remain in place if we return to Hangfire.

Leave ingestion paused for the whole canary window. Skipping thin refreshes is
acceptable; schedules stay off until the checks pass. Listener requests and
default cache jobs continue.

The [infrastructure bundle](https://github.com/RelistenNet/relisten-flux/tree/codex/temporal-production/clusters/relisten3-k3s/temporal) lives in `../relisten-flux/clusters/relisten3-k3s/temporal/`. That cluster
uses manually applied manifests. Applying manifests or dispatching the image workflow with `deploy=true` changes
production. The commands below are the operator procedure.

## Prepare the release

Publish both images from the same reviewed commit without restarting production:

```sh
gh workflow run build_and_push_image.yml --repo RelistenNet/RelistenApi \
  --ref COMMIT_OR_BRANCH -f component=api -f deploy=false
gh workflow run build_and_push_image.yml --repo RelistenNet/RelistenApi \
  --ref COMMIT_OR_BRANCH -f component=ingestion-worker -f deploy=false
```

Wait for both builds. Record their image digests and use those digests for the
cutover. With `deploy=false`, the workflow publishes only the commit's `sha-` tag
and skips Tailscale, Kubernetes setup, and deployment restarts. It leaves `latest`
unchanged, so an unrelated production pod restart cannot pick up the canary image.
With `deploy=true` (the default), the workflow also updates `latest` and sets each
deployment to the newly built image digest before waiting for its rollout. This
updates deployments even when the preceding release was pinned to a digest.

Provision Temporal persistence and visibility in separate databases, initialize
its schemas, and create the application namespace with at most seven days of
closed-workflow retention. Keep its frontend and UI private. Do not use Temporal's
temporary test server for production. Configure the ingestion worker's database,
Redis, upstream credentials, and Temporal namespace/address before starting it.
All API clients and the worker must use
`Temporal__PayloadRedisUrl=redis://relisten-temporal-payloads.default:6379`. This
persistent store holds workflow inputs separately from the disposable cache.
See [worker configuration](operations.md#configuration) for application settings.

Keep `relistenapi-ingestion-worker` at zero replicas during provisioning. Start
one replica after the Hangfire drain and API handoff below. Starting a worker
creates no schedules or imports. Do not run `--register-schedules` yet: it creates
missing schedules **active immediately**.

## Check and pause ingestion

Thin refreshes run during the day as well as overnight. Do not assume that
ingestion will stay idle because the last check found empty queues. Check
immediately before the handoff. These commands read queue counts and
schedule metadata without printing job arguments or secrets:

```sh
kubectl --context relisten3-k3s -n default get deploy relistenapi relistenapi-worker

kubectl --context relisten3-k3s -n default exec deployment/relistenapi-redis -- \
  redis-cli --raw EVAL '
    local result = {}
    for _, queue in ipairs({"artist_import", "default"}) do
      table.insert(result, queue .. " queued")
      table.insert(result, redis.call("LLEN", "{hangfire}:queue:" .. queue))
      table.insert(result, queue .. " dequeued")
      table.insert(result, redis.call("LLEN", "{hangfire}:queue:" .. queue .. ":dequeued"))
    end
    table.insert(result, "processing")
    table.insert(result, redis.call("ZCARD", "{hangfire}:processing"))
    table.insert(result, "delayed")
    table.insert(result, redis.call("ZCARD", "{hangfire}:schedule"))
    return result
  ' 0

kubectl --context relisten3-k3s -n default exec deployment/relistenapi-redis -- \
  redis-cli --raw EVAL '
    local result = {}
    for _, name in ipairs(redis.call("ZRANGE", "{hangfire}:recurring-jobs", 0, -1)) do
      table.insert(result, name)
      for _, field in ipairs({"Cron", "TimeZoneId", "NextExecution", "LastExecution"}) do
        table.insert(result, field)
        table.insert(result, redis.call("HGET", "{hangfire}:recurring-job:" .. name, field) or "unset")
      end
    end
    return result
  ' 0
```

Review the Hangfire dashboard as well. A count of delayed jobs includes other
queues; inspect their types before deciding whether ingestion has drained. Record
the running API and worker image IDs for rollback:

```sh
kubectl --context relisten3-k3s -n default get pods \
  -l 'app in (relistenapi,relistenapi-worker)' \
  -o jsonpath='{range .items[*]}{.metadata.name}{" "}{range .status.containerStatuses[*]}{.imageID}{"\n"}{end}{end}'
```

## Hand ingestion to Temporal

1. Stop manual ingestion requests for the window. In Hangfire's recurring-jobs
   dashboard, verify and remove only these saved definitions:
   `ScheduledService.IndexArchiveOrgArtists`,
   `ScheduledService.RefreshAllArtists`, and
   `ScheduledService.RefreshActiveArtists`. Do not clear Redis or delete jobs by a
   broad key pattern. Disabling registration in code does not remove saved jobs.
2. Let queued and running ingestion finish. Recheck the dashboard and queue counts.
   If an import is still writing, wait before starting a Temporal import.
3. Deploy the reviewed API image with `Ingestion__Engine=Temporal` on **both**
   `relistenapi` and `relistenapi-worker`. Wait for all three API replicas and the
   worker rollout to finish. Use the atomic image-and-configuration patch below.
   Keep `ENABLE_HANGFIRE_SERVER=true` on the existing worker: it retains the `default` queue while omitting `artist_import`.
4. Verify that Hangfire no longer has an `artist_import` server and that default
   cache jobs still run. Saved or delayed legacy ingestion methods return without
   importing in Temporal mode. Review leftovers before discarding anything.
5. Pin the reviewed ingestion-worker image and scale it to one replica as described
   in the infrastructure README. Confirm it polls its workflow, database, and seven
   provider queues. Keep schedules absent and manual admission limited to the
   checks below until they pass.

Update each deployment's image and client settings together. Both deployments
use a container with the same name as the deployment. The patch preserves other
environment variables, including the legacy worker's default-queue setting:

```sh
api_image='ghcr.io/relistennet/relistenapi@sha256:REPLACE_WITH_REVIEWED_DIGEST'
for deployment in relistenapi relistenapi-worker; do
  patch="$(jq -n --arg image "$api_image" --arg container "$deployment" '{
  spec: {template: {spec: {containers: [{
    name: $container,
    image: $image,
    env: [
      {name: "Ingestion__Engine", value: "Temporal"},
      {name: "Temporal__Address", value: "relisten-temporal-frontend.default:7233"},
      {name: "Temporal__Namespace", value: "relisten"},
      {name: "Temporal__PayloadRedisUrl", value: "redis://relisten-temporal-payloads.default:6379"}
    ]
  }]}}}
}')"
  kubectl --context relisten3-k3s -n default patch deployment "$deployment" \
    --type=strategic --patch "$patch"
done
for deployment in relistenapi relistenapi-worker; do
  kubectl --context relisten3-k3s -n default rollout status "deployment/$deployment" --timeout=10m
done
```

An old API pod can still admit Hangfire jobs during a rolling update. The manual
admission stop and complete rollout are part of the handoff. Temporal artist IDs
do not coordinate with a running Hangfire import.

## Test observable behavior

Choose an existing Archive recording with tracks in the catalog. Record its
artist slug, show date, upstream identifier, source UUID, and track UUIDs. Save the
current artist and show API responses locally for comparison:

```sh
curl --fail --silent --show-error \
  https://api.relisten.net/api/v3/artists/ARTIST_SLUG > /tmp/temporal-artist-before.json
curl --fail --silent --show-error \
  https://api.relisten.net/api/v2/artists/ARTIST_SLUG/shows/YYYY-MM-DD > /tmp/temporal-show-before.json

kubectl --context relisten3-k3s -n default exec deployment/relistenapi-ingestion-worker -- \
  dotnet RelistenIngestionWorker.dll --import=ARTIST_SLUG --item=UPSTREAM_IDENTIFIER
```

The command prints a workflow ID after admission. Wait for its final result in
Temporal; a successful start does not mean the import succeeded. Check completed,
skipped, and failed item counts. A targeted Archive import processes one recording
and the artist coordinator rebuilds derived catalog data afterward.

Fetch the same public API responses. Check source and track identity, show date,
venue, set/track order, playable URLs, and artist show/year counts. An upstream
metadata correction can be expected; duplicate sources, lost tracks, or unrelated
recording removal are failures. Repeat the same item and verify stable IDs. Run an
explicit rebuild and verify it completes with usable public artist/show responses:

```sh
kubectl --context relisten3-k3s -n default exec deployment/relistenapi-ingestion-worker -- \
  dotnet RelistenIngestionWorker.dll --rebuild-artist=ARTIST_SLUG
```

Next run an import with enough work to observe activity progress. While it is
running, replace the ingestion worker pod with the **same image** using:

```sh
kubectl --context relisten3-k3s -n default rollout restart deployment/relistenapi-ingestion-worker
kubectl --context relisten3-k3s -n default rollout status deployment/relistenapi-ingestion-worker --timeout=5m
```

Verify the execution resumes and completes without duplicate catalog records. A
graceful shutdown may let an activity finish; inspect history to see whether the
run exercised retry or only resumed remaining work. Also verify a
failed or canceled run can be followed by a fresh successful import.

Expand to one reviewed artist per provider. Use `--provider` to avoid accidentally
running every mapping. Archive and Phish.net accept an exact `--year`; Phish.in
interprets it as a lower bound. Setlist.fm, Phantasy Tour, Jerry, and Local have no
item-count filter in the operator command. Review selection size and upstream
behavior before using those artist refreshes as production tests. This is a
staged increase in real imports, not a synthetic load test.

Check upstream 429/5xx responses, activity failures and queue delays, PostgreSQL
load, Redis memory/evictions, and listener API errors throughout. Provider activity
concurrency defaults to ten per worker across artists. Start with one worker; use
its existing provider concurrency settings if upstream or database load requires
less parallel work.

## Enable schedules last

Run `--register-schedules` only after the manual checks pass. It creates discovery
at 06:30 UTC, full refresh at 07:00 UTC, and thin refresh at 00:00, 02:00, 04:00,
12:00, 14:00, 16:00, 18:00, 20:00, and 22:00 UTC. Inspect each schedule's next action.
Repeated registration preserves saved schedules and their paused state.

Watch the first thin refresh before leaving the full refresh unattended. A
workflow with a `Partial` result needs its failed-item details reviewed even though
the execution completed.

## Return to Hangfire if needed

Pause Temporal schedules and manual admission. Request cancellation of running
operations, let activities drain, and stop the ingestion worker. Verify catalog
writes have ended before changing engines. Do not rely on a worker flag alone to
stop an activity already executing.

Select `Ingestion__Engine=Hangfire` on both API deployments. If the API release
itself needs rollback, restore the recorded API/worker image digests as well.
Check obsolete `artist_import` jobs before restarting legacy ingestion. Hangfire
mode restores recurring registration on startup and retains default cache jobs.
Keep committed catalog changes; a later import or rebuild repairs derived data.
Do not resume or reset Temporal executions while Hangfire owns ingestion.

If payload decoding prevents cancellation or recovery, follow
[storage recovery](operations.md#storage-backups-and-recovery) before starting a
replacement import. Fixing forward is appropriate for an isolated importer error;
stop admission when the failure can damage unrelated catalog data or overload the
listener database.
