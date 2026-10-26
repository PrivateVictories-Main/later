#!/bin/zsh
# Fails if the signed app has any network entitlement, or isn't sandboxed. Usage: scripts/check-entitlements.sh [app]
set -euo pipefail
cd "${0:A:h}/.."
app=${1:-build/Later.app}
entitlements=$(codesign -d --entitlements :- "$app" 2>/dev/null)
if print -r -- "$entitlements" | grep -q 'com.apple.security.network'; then
  echo "error: $app has a network entitlement" >&2
  exit 1
fi
print -r -- "$entitlements" | grep -q 'com.apple.security.app-sandbox' || { echo "error: $app is not sandboxed" >&2; exit 1; }
echo "ok: $app is sandboxed and has no network entitlement"
