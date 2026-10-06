#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/../.." && pwd)"
task_out="$project_dir/build/mac-native"
mkdir -p "$task_out"
xcrun clang -fobjc-arc -dynamiclib -O2 -mmacosx-version-min=13.0 \
  "$project_dir/macos/native/WeTypeSkinHook.m" \
  -o "$task_out/libWeTypeSkinHook.dylib" \
  -framework AppKit -framework QuartzCore -framework ImageIO
codesign --force --sign - "$task_out/libWeTypeSkinHook.dylib"
cp "$project_dir/macos/native/clone.entitlements" "$task_out/"
xcrun swiftc -swift-version 5 -O -num-threads 1 "$project_dir/macos/native/PrepareInputMethod.swift" -o "$task_out/WeTypeSkinPrepare"
"$task_out/WeTypeSkinPrepare"
task_clone="$HOME/Library/Input Methods/WeTypeSkinStudioInputMethod.app"
codesign --verify --deep --strict "$task_clone"
echo "Clone ready: $task_clone"
echo "Launch with: bash macos/native/run-clone.sh"
