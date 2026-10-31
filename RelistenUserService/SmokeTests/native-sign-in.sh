#!/usr/bin/env bash
# Real Development persona -> authorization code + PKCE -> native tokens.
# Emits credentials on stdout for a caller to keep private; never run under shell tracing.
set -Eeuo pipefail
readonly auth_url="${RELISTEN_USER_SERVICE_AUTH_URL:-https://auth.relisten.localhost:5443}"
readonly verifier="abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~abc"
readonly challenge="dYSqskoTcWrpu8GYY0XpWlzOc0c5rd9YO3uAgh_zmV4"
smoke_tmp_dir="$(mktemp -d)"
trap 'rm -r "$smoke_tmp_dir"' EXIT
sign_in() {
  local run_name="$1"
  local cookie_jar="$smoke_tmp_dir/$run_name.cookies"
  local sign_in_page="$smoke_tmp_dir/$run_name.html"
  local sign_in_url return_url authorize_return callback_url code

  sign_in_url="$(curl --silent --show-error --get "$auth_url/connect/authorize" \
    --data-urlencode 'client_id=relisten-mobile-ios-dev' \
    --data-urlencode 'redirect_uri=net.relisten.mobile:/oauth2redirect/ios' \
    --data-urlencode 'response_type=code' \
    --data-urlencode 'scope=openid profile offline_access user.read library.read library.write account.manage' \
    --data-urlencode "code_challenge=$challenge" \
    --data-urlencode 'code_challenge_method=S256' \
    --data-urlencode 'provider=google' \
    --data-urlencode "state=$run_name" \
    --write-out '%{redirect_url}' \
    --output /dev/null)"

  curl --silent --show-error \
    --cookie-jar "$cookie_jar" \
    "$sign_in_url" \
    --output "$sign_in_page"

  return_url="$(sed -n \
    's/.*name="return_url" value="\([^"]*\)".*/\1/p' \
    "$sign_in_page" | sed 's/&amp;/\&/g')"

  authorize_return="$(curl --silent --show-error \
    --cookie "$cookie_jar" \
    --cookie-jar "$cookie_jar" \
    --request POST "$auth_url/development/sign-in" \
    --header "Origin: $auth_url" \
    --data-urlencode "return_url=$return_url" \
    --data-urlencode "persona=${2:-google-alice}" \
    --write-out '%{redirect_url}' \
    --output /dev/null)"
  callback_url="$(curl --silent --show-error \
    --cookie "$cookie_jar" \
    "$authorize_return" \
    --write-out '%{redirect_url}' \
    --output /dev/null)"
  code="$(printf '%s' "$callback_url" | sed -n 's/.*[?&]code=\([^&]*\).*/\1/p')"

  curl --fail --silent --show-error \
    --request POST "$auth_url/connect/token" \
    --data-urlencode 'grant_type=authorization_code' \
    --data-urlencode 'client_id=relisten-mobile-ios-dev' \
    --data-urlencode 'redirect_uri=net.relisten.mobile:/oauth2redirect/ios' \
    --data-urlencode "code=$code" \
    --data-urlencode "code_verifier=$verifier"
}

sign_in "${1:-native-review}" "${2:-google-alice}"
