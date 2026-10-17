#!/usr/bin/env bash
# Query the production replica only; retain the exact query and raw evidence locally.
set -euo pipefail

audit_output=${1:?Pass a new output JSONL filename.}
audit_sql_snapshot="${audit_output}.sql"
audit_manifest="${audit_output}.query.json"
for audit_target in "$audit_output" "$audit_sql_snapshot" "$audit_manifest"; do
    if [[ -e "$audit_target" ]]; then
        printf 'Choose a new evidence filename; existing evidence is preserved.\n' >&2
        exit 1
    fi
done

audit_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
mkdir -p -- "$(dirname -- "$audit_output")"
audit_partial=$(mktemp "${audit_output}.partial.XXXXXX")
audit_sql_partial=$(mktemp "${audit_output}.sql.partial.XXXXXX")
trap 'printf "Census failed; preserve partial output %s and query snapshot %s.\n" "$audit_partial" "$audit_sql_snapshot" >&2' ERR

# Copy before starting the query. A later checkout edit must not change its evidence hash.
cp -- "$audit_directory/sql/catalog-census.sql" "$audit_sql_partial"
ln -- "$audit_sql_partial" "$audit_sql_snapshot"
rm -- "$audit_sql_partial"
python3 - "$audit_sql_snapshot" "$audit_manifest" "$audit_output" <<'PY'
import datetime, hashlib, json, pathlib, sys
sql, manifest, output = map(pathlib.Path, sys.argv[1:])
with manifest.open("x", encoding="utf-8") as stream:
    json.dump({"started_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
               "sql_snapshot": str(sql.resolve()), "sql_sha256": hashlib.sha256(sql.read_bytes()).hexdigest(),
               "output": str(output.resolve()), "endpoint": "relisten-db-ro.tail09dbf.ts.net:5432",
               "database": "app", "read_only": True}, stream, indent=2)
    stream.write("\n")
PY

# Keep acquisition separate so a failed secret lookup stops before psql can run.
audit_db_password=$(kubectl --context relisten3-k3s -n default get secret relisten-db-app -o jsonpath='{.data.password}' | base64 --decode)
PGPASSWORD="$audit_db_password" \
PGSSLMODE=require PGCONNECT_TIMEOUT=15 \
PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=180000' \
psql -X -q -v ON_ERROR_STOP=1 \
    -h relisten-db-ro.tail09dbf.ts.net -p 5432 -U app -d app -At \
    -f "$audit_sql_snapshot" > "$audit_partial"
unset audit_db_password

python3 - "$audit_partial" <<'PY'
import json, pathlib, sys
lines = [line for line in pathlib.Path(sys.argv[1]).read_bytes().splitlines() if line.strip()]
if not lines or json.loads(lines[-1]).get("kind") != "snapshot_complete":
    raise ValueError("The query did not emit its completion marker.")
PY
# Hard-link creation is atomic and refuses an output created by another process.
ln -- "$audit_partial" "$audit_output"
rm -- "$audit_partial"
printf 'Saved completed read-only census: %s\nSQL snapshot: %s\n' "$audit_output" "$audit_sql_snapshot"
