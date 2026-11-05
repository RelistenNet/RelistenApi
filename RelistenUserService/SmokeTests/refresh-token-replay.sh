#!/usr/bin/env bash

set -Eeuo pipefail

readonly auth_url="${RELISTEN_USER_SERVICE_AUTH_URL:-https://localhost:5443}"
readonly accounts_url="${RELISTEN_USER_SERVICE_ACCOUNTS_URL:-https://accounts.relisten.localhost:5443}"
smoke_tmp_dir="$(mktemp -d)"
trap 'rm -r "$smoke_tmp_dir"' EXIT

assert_equal() {
  if [[ "$1" != "$2" ]]; then
    echo "Expected '$2', received '$1'." >&2
    exit 1
  fi
}

sign_in() {
  "$(dirname "$0")/native-sign-in.sh" "$1"
}

logout_tokens="$(sign_in logout-smoke)"
logout_access="$(printf '%s' "$logout_tokens" | jq -r '.access_token')"
logout_refresh="$(printf '%s' "$logout_tokens" | jq -r '.refresh_token')"

invalid_uuid_status="$(curl --silent --show-error \
  --request PATCH "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --header 'Content-Type: application/json' \
  --data '{"contract_version":1,"command_id":"not-a-uuid","expected_username_version":1,"username":"listener"}' \
  --output "$smoke_tmp_dir/invalid-uuid.json" \
  --write-out '%{http_code}')"
assert_equal "$invalid_uuid_status" "422"
assert_equal "$(jq -r '.code' "$smoke_tmp_dir/invalid-uuid.json")" "invalid_command_id"

invalid_contract_status="$(curl --silent --show-error \
  --request PATCH "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --header 'Content-Type: application/json' \
  --data '{"contract_version":"one","command_id":"command_01kxvcgz3pet0atbs4he3qydc1","expected_username_version":1,"username":"listener"}' \
  --output "$smoke_tmp_dir/invalid-contract.json" \
  --write-out '%{http_code}')"
assert_equal "$invalid_contract_status" "422"
assert_equal "$(jq -r '.code' "$smoke_tmp_dir/invalid-contract.json")" "invalid_contract_version"

unmapped_status="$(curl --silent --show-error \
  --request PATCH "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --header 'Content-Type: application/json' \
  --data '{"contract_version":1,"command_id":"command_01kxvcgz3pet0atbs4he3qydc1","expected_username_version":1,"username":"listener","extra":true}' \
  --output "$smoke_tmp_dir/unmapped.json" \
  --write-out '%{http_code}')"
assert_equal "$unmapped_status" "400"
assert_equal "$(jq -r '.code' "$smoke_tmp_dir/unmapped.json")" "invalid_request"

profile="$(curl --fail --silent --show-error \
  "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access")"
username="$(printf '%s' "$profile" | jq -r '.username')"
username_version="$(printf '%s' "$profile" | jq -r '.username_version')"
command_uuid="$(python3 - <<'PYID'
import uuid
value = uuid.uuid4().int
value = (value & ~(15 << 76)) | (7 << 76)
alphabet = "0123456789abcdefghjkmnpqrstvwxyz"
print("command_" + "".join(alphabet[(value >> (5*i)) & 31] for i in reversed(range(26))))
PYID
)"
username_command="$(jq --null-input --compact-output \
  --arg command_uuid "$command_uuid" \
  --arg username "$username" \
  --argjson username_version "$username_version" \
  '{contract_version:1,command_id:$command_uuid,expected_username_version:$username_version,username:$username}')"

username_result="$(curl --fail --silent --show-error \
  --request PATCH "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --header 'Content-Type: application/json' \
  --data "$username_command")"
username_replay="$(curl --fail --silent --show-error \
  --request PATCH "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --header 'Content-Type: application/json' \
  --data "$username_command")"
assert_equal "$(printf '%s' "$username_replay" | jq --sort-keys --compact-output)" \
  "$(printf '%s' "$username_result" | jq --sort-keys --compact-output)"

changed_replay="$(printf '%s' "$username_command" | jq --compact-output '.username=" bad "')"
changed_replay_status="$(curl --silent --show-error \
  --request PATCH "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --header 'Content-Type: application/json' \
  --data "$changed_replay" \
  --output "$smoke_tmp_dir/changed-replay.json" \
  --write-out '%{http_code}')"
assert_equal "$changed_replay_status" "409"
assert_equal "$(jq -r '.code' "$smoke_tmp_dir/changed-replay.json")" "idempotency_conflict"

logout_status="$(curl --silent --show-error \
  --request POST "$accounts_url/v1/logout" \
  --header "Authorization: Bearer $logout_access" \
  --output /dev/null \
  --write-out '%{http_code}')"
assert_equal "$logout_status" "204"

logout_access_status="$(curl --silent --show-error \
  "$accounts_url/v1/me" \
  --header "Authorization: Bearer $logout_access" \
  --output /dev/null \
  --write-out '%{http_code}')"
assert_equal "$logout_access_status" "401"

logout_refresh_status="$(curl --silent --show-error \
  --request POST "$auth_url/connect/token" \
  --data-urlencode 'grant_type=refresh_token' \
  --data-urlencode 'client_id=relisten-mobile-ios-dev' \
  --data-urlencode "refresh_token=$logout_refresh" \
  --output "$smoke_tmp_dir/logout-refresh.json" \
  --write-out '%{http_code}')"
assert_equal "$logout_refresh_status" "400"
assert_equal "$(jq -r '.error' "$smoke_tmp_dir/logout-refresh.json")" "invalid_grant"

initial="$(sign_in refresh-replay-smoke)"
initial_access="$(printf '%s' "$initial" | jq -r '.access_token')"
initial_refresh="$(printf '%s' "$initial" | jq -r '.refresh_token')"

refresh_once() {
  local slot="$1"
  curl --silent --show-error \
    --request POST "$auth_url/connect/token" \
    --data-urlencode 'grant_type=refresh_token' \
    --data-urlencode 'client_id=relisten-mobile-ios-dev' \
    --data-urlencode "refresh_token=$initial_refresh" \
    --output "$smoke_tmp_dir/refresh-$slot.json" \
    --write-out '%{http_code}' > "$smoke_tmp_dir/refresh-$slot.status"
}

refresh_once 1 &
first_refresh_pid=$!
refresh_once 2 &
second_refresh_pid=$!
wait "$first_refresh_pid"
wait "$second_refresh_pid"

refresh_statuses="$(sort \
  "$smoke_tmp_dir/refresh-1.status" \
  "$smoke_tmp_dir/refresh-2.status" | paste -sd, -)"
if [[ "$refresh_statuses" != "200,400" && "$refresh_statuses" != "400,400" ]]; then
  echo "Expected concurrent refresh statuses 200/400 or 400/400; received $refresh_statuses." >&2
  exit 1
fi

for slot in 1 2; do
  if [[ "$(< "$smoke_tmp_dir/refresh-$slot.status")" == "400" ]]; then
    assert_equal "$(jq -r '.error' "$smoke_tmp_dir/refresh-$slot.json")" "invalid_grant"
  fi
done

access_status="$(curl --silent --show-error \
  "$accounts_url/v1/me" \
  --header "Authorization: Bearer $initial_access" \
  --output /dev/null \
  --write-out '%{http_code}')"
assert_equal "$access_status" "401"

for slot in 1 2; do
  if [[ "$(< "$smoke_tmp_dir/refresh-$slot.status")" == "200" ]]; then
    rotated_access="$(jq -r '.access_token' "$smoke_tmp_dir/refresh-$slot.json")"
    rotated_refresh="$(jq -r '.refresh_token' "$smoke_tmp_dir/refresh-$slot.json")"
    rotated_access_status="$(curl --silent --show-error \
      "$accounts_url/v1/me" \
      --header "Authorization: Bearer $rotated_access" \
      --output /dev/null \
      --write-out '%{http_code}')"
    assert_equal "$rotated_access_status" "401"

    family_status="$(curl --silent --show-error \
      --request POST "$auth_url/connect/token" \
      --data-urlencode 'grant_type=refresh_token' \
      --data-urlencode 'client_id=relisten-mobile-ios-dev' \
      --data-urlencode "refresh_token=$rotated_refresh" \
      --output "$smoke_tmp_dir/family.json" \
      --write-out '%{http_code}')"
    assert_equal "$family_status" "400"
    assert_equal "$(jq -r '.error' "$smoke_tmp_dir/family.json")" "invalid_grant"
  fi
done

echo "Account wire errors, username idempotency, logout, and concurrent refresh replay behaved as expected."
