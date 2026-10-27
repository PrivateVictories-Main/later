#!/bin/zsh
# Builds, bundles and signs build/Later.app for Apple Silicon. Usage: scripts/build-app.sh [debug|release]
# An optional, gitignored scripts/local.env can set LATER_SIGN_IDENTITY (default: ad-hoc, "-") and
# LATER_EXTRA_HELIUM_IDS (space-separated bundle IDs of other Helium builds to watch).
set -euo pipefail
cd "${0:A:h}/.."
[[ -f scripts/local.env ]] && source scripts/local.env
config=${1:-release}
identity=${LATER_SIGN_IDENTITY:--}
swift build -c "$config" --arch arm64 --product Later
bin="$(swift build -c "$config" --arch arm64 --show-bin-path)/Later"
app=build/Later.app
rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin" "$app/Contents/MacOS/Later"
cp Resources/Info.plist "$app/Contents/Info.plist"
if [[ $config == debug ]]; then
  # Its own identity, so a development build never shares the installed app's container, settings or list.
  for cmd in "Set :CFBundleIdentifier com.ryan.later.dev" "Set :CFBundleName 'Later Dev'" "Set :CFBundleDisplayName 'Later Dev'"; do
    /usr/libexec/PlistBuddy -c "$cmd" "$app/Contents/Info.plist"
  done
fi
# The background agent's launchd plist; macOS finds it here. A debug build gets its own label, so it never touches the installed app's agent.
mkdir -p "$app/Contents/Library/LaunchAgents"
if [[ $config == debug ]]; then
  agentplist="$app/Contents/Library/LaunchAgents/com.ryan.later.dev.agent.plist"
  cp Resources/com.ryan.later.agent.plist "$agentplist"
  /usr/libexec/PlistBuddy -c "Set :Label com.ryan.later.dev.agent" -c "Set :AssociatedBundleIdentifiers:0 com.ryan.later.dev" "$agentplist"
else
  cp Resources/com.ryan.later.agent.plist "$app/Contents/Library/LaunchAgents/"
fi
# The Liquid Glass icon: actool turns the layered .icon into Assets.car (what macOS 26 draws) plus an .icns fallback.
xcrun actool Resources/AppIcon.icon --compile "$app/Contents/Resources" --output-format human-readable-text \
  --output-partial-info-plist /dev/null --app-icon AppIcon --target-device mac --platform macosx \
  --minimum-deployment-target 26.0 > /dev/null
[[ -f "$app/Contents/Resources/Assets.car" ]] || { echo "actool produced no icon" >&2; exit 1; }
# Extra Helium builds go into the built Info.plist (the app reads them at launch) and into a temporary copy of the entitlements.
entitlements=Resources/Later.entitlements
extra=(${=LATER_EXTRA_HELIUM_IDS:-})
if (( $#extra )); then
  entitlements=$(mktemp "${TMPDIR:-/tmp}/later-entitlements.XXXXXX")
  trap 'rm -f "$entitlements"' EXIT
  cp Resources/Later.entitlements "$entitlements"
  /usr/libexec/PlistBuddy -c "Add :LaterExtraHeliumBundleIDs array" "$app/Contents/Info.plist"
  n=0
  for id in $extra; do
    /usr/libexec/PlistBuddy -c "Add :LaterExtraHeliumBundleIDs: string $id" "$app/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Add :com.apple.security.temporary-exception.apple-events:$((++n)) string $id" "$entitlements"
  done
fi
codesign --force --options runtime --timestamp=none --entitlements "$entitlements" --sign "$identity" "$app"
codesign --verify --strict "$app"
echo "$PWD/$app"
