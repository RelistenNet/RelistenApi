#!/usr/bin/env bash

set -Eeuo pipefail

cluster=(kubectl --context relisten3-k3s --namespace default --request-timeout=150s)

wait_for_replica_replay() {
  local primary release_lsn replicas replica replayed deadline
  primary="$("${cluster[@]}" get cluster relisten-db-target -o jsonpath='{.status.currentPrimary}')"
  release_lsn="$("${cluster[@]}" exec "$primary" -c postgres -- \
    psql -X -w -U postgres -d app -At -v ON_ERROR_STOP=1 -c 'SELECT pg_current_wal_lsn()')"
  replicas="$("${cluster[@]}" get pods \
    -l cnpg.io/cluster=relisten-db-target,cnpg.io/instanceRole=replica \
    --field-selector=status.phase=Running -o name)"
  if [ -z "$replicas" ]; then
    echo "No read replicas found; cannot confirm schema replay." >&2
    return 1
  fi

  # Otherwise the RO pool can prepare against the old schema immediately after reset.
  for replica in $replicas; do
    echo "Waiting for $replica to replay release WAL position $release_lsn"
    deadline=$((SECONDS + 120))
    while true; do
      replayed="$("${cluster[@]}" exec "$replica" -c postgres -- \
        timeout 10s psql -X -w -U postgres -d app -At -v ON_ERROR_STOP=1 \
        -c "SELECT pg_is_in_recovery() AND pg_last_wal_replay_lsn() >= '$release_lsn'::pg_lsn")"
      if [ "$replayed" = t ]; then
        break
      fi
      if (( SECONDS >= deadline )); then
        echo "Timed out waiting for schema replay on $replica" >&2
        return 1
      fi
      sleep 1
    done
  done
}

reconnect_pooler() {
  local pooler="$1" pods pod
  pods="$("${cluster[@]}" get pods -l "cnpg.io/poolerName=$pooler" \
    --field-selector=status.phase=Running -o name)"
  if [ -z "$pods" ]; then
    echo "No running pods found for $pooler; statement caches were not reset." >&2
    return 1
  fi

  for pod in $pods; do
    echo "Rebuilding prepared statement cache on $pod"
    # RECONNECT preserves client sockets and lets active transactions finish.
    # Separate commands use PgBouncer's simple-query admin protocol; WAIT_CLOSE
    # confirms all marked server connections have drained before release success.
    "${cluster[@]}" exec "$pod" -c pgbouncer -- \
      timeout 120s psql -X -w -d pgbouncer -v ON_ERROR_STOP=1 \
      -c 'RECONNECT app' -c 'WAIT_CLOSE app'
  done
}

# Call after API readiness has confirmed startup migrations finished.
wait_for_replica_replay
reconnect_pooler relisten-db-pgbouncer-rw
reconnect_pooler relisten-db-pgbouncer-ro
