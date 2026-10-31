#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo_root/RelistenUserService"
export ASPNETCORE_ENVIRONMENT=Development
export ACCOUNTS_DATABASE_URL='Host=127.0.0.1;Port=15432;Database=relisten_c2_review;Username=relisten;Password=local_dev_password'
export Logging__LogLevel__Default=Warning
exec dotnet run --no-launch-profile --urls https://127.0.0.1:5443
