#!/usr/bin/env bash
# Both provider modes use the same local service, database and issuer.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
case "${1:-dummy}" in
  dummy) auth_profile=RelistenUserService ;;
  google) auth_profile=RelistenUserService.LocalGoogle ;;
  *) echo "Usage: local-dev/accounts.sh [dummy|google]" >&2; exit 2 ;;
esac

exec dotnet run --project "$repo_root/RelistenUserService" --launch-profile "$auth_profile"
