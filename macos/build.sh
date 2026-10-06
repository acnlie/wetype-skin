#!/bin/bash
set -euo pipefail

project_dir="$(cd "$(dirname "$0")/.." && pwd)"
task_arch="$(uname -m)"
case "$task_arch" in
  arm64|x86_64) ;;
  *) echo "Unsupported Mac architecture: $task_arch" >&2; exit 1 ;;
esac
if [[ "$(uname -s)" != Darwin ]]; then
  echo "Run this script on macOS with Xcode Command Line Tools and Swift 5.9+." >&2
  exit 1
fi

task_build_dir="$project_dir/build/macos-$task_arch"
task_app="$task_build_dir/WeType Skin Studio.app"
mkdir -p "$task_build_dir"
task_staging="$(mktemp -d "$task_build_dir/.stage.XXXXXX")"
trap 'rm -rf "$task_staging"' EXIT
task_bundle="$task_staging/WeType Skin Studio.app"
mkdir -p "$task_bundle/Contents/MacOS" "$task_bundle/Contents/Resources/frontend/pet"
mkdir -p "$task_bundle/Contents/Resources/native"
cp "$project_dir/macos/Info.plist" "$task_bundle/Contents/Info.plist"
for file in index.html studio.css studio.js lucide.min.js; do
  cp "$project_dir/frontend/$file" "$task_bundle/Contents/Resources/frontend/$file"
done
cp "$project_dir/assets/pet/"*.png "$task_bundle/Contents/Resources/frontend/pet/"
for file in clips.json ATTRIBUTION.md LICENSE.txt; do
  cp "$project_dir/assets/pet/$file" "$task_bundle/Contents/Resources/frontend/pet/$file"
done
cp "$project_dir/LICENSE" "$task_bundle/Contents/Resources/LICENSE"

xcrun clang -fobjc-arc -dynamiclib -O2 -mmacosx-version-min=13.0 \
  "$project_dir/macos/native/WeTypeSkinHook.m" \
  -o "$task_bundle/Contents/Resources/native/libWeTypeSkinHook.dylib" \
  -framework AppKit -framework QuartzCore -framework ImageIO
mkdir -p "$project_dir/build/macos-module-cache"
xcrun swiftc -swift-version 5 -O -num-threads 1 \
  -target "$task_arch-apple-macosx13.0" \
  -module-cache-path "$project_dir/build/macos-module-cache" \
  "$project_dir/macos/native/PrepareInputMethod.swift" \
  -o "$task_bundle/Contents/Resources/native/WeTypeSkinPrepare"
codesign --force --sign - "$task_bundle/Contents/Resources/native/WeTypeSkinPrepare"
cp "$project_dir/macos/native/clone.entitlements" "$task_bundle/Contents/Resources/native/"
cp "$project_dir/macos/native/run-clone.sh" "$task_bundle/Contents/Resources/native/"
cp "$project_dir/macos/native/stop-clone.sh" "$task_bundle/Contents/Resources/native/"
chmod +x "$task_bundle/Contents/Resources/native/"*.sh

# One host architecture, one compiler process, no package restore or web server.
# Keep the compiler's cache inside build so its disk use can be inspected.
mkdir -p "$project_dir/build/macos-module-cache"
xcrun swiftc -swift-version 5 -O -num-threads 1 \
  -target "$task_arch-apple-macosx13.0" \
  -module-cache-path "$project_dir/build/macos-module-cache" \
  "$project_dir/macos/src/SkinTheme.swift" "$project_dir/macos/src/MacEditor.swift" \
  "$project_dir/macos/src/MacPet.swift" \
  "$project_dir/macos/src/InstanceLock.swift" \
  "$project_dir/macos/src/PetRuntime.swift" \
  "$project_dir/macos/src/InputWindowTracker.swift" \
  "$project_dir/macos/src/AppMain.swift" \
  -o "$task_bundle/Contents/MacOS/WeTypeSkinStudio"
task_helper="$task_bundle/Contents/Helpers/WeType Skin Pet.app"
mkdir -p "$task_helper/Contents/MacOS"
cp "$project_dir/macos/PetInfo.plist" "$task_helper/Contents/Info.plist"
# Both entry points share the compiler output; the helper's bundle ID
# selects its role. Animation assets live in the parent bundle.
cp "$task_bundle/Contents/MacOS/WeTypeSkinStudio" "$task_helper/Contents/MacOS/WeTypeSkinPet"
plutil -lint "$task_helper/Contents/Info.plist"
codesign --force --sign - "$task_helper"
plutil -lint "$task_bundle/Contents/Info.plist"
# Local ad-hoc signing is not Developer ID signing or notarization.
codesign --force --sign - "$task_bundle"
codesign --verify --deep --strict "$task_bundle"
if [[ -e "$task_app" ]]; then
  # The detached desktop pet uses the same executable with --pet. Do not
  # mistake it for the editor and block an otherwise safe rebuild.
  if pgrep -f "^$task_app/Contents/MacOS/WeTypeSkinStudio$" >/dev/null; then
    echo "Close $task_app before rebuilding." >&2
    exit 1
  fi
  rm -rf "$task_app"
fi
mv "$task_bundle" "$task_app"
echo "Built: $task_app"
