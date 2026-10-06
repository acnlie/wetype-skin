#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
task_arch="$(uname -m)"
task_checks="$project_dir/build/macos-check"
mkdir -p "$task_checks" "$project_dir/build/macos-module-cache"
xcrun swiftc -swift-version 5 -num-threads 1 \
  -target "$task_arch-apple-macosx13.0" \
  -module-cache-path "$project_dir/build/macos-module-cache" \
  "$project_dir/macos/src/SkinTheme.swift" "$project_dir/macos/tests/ThemeChecks.swift" \
  -o "$task_checks/theme-checks"
"$task_checks/theme-checks"
task_ui=""
if [[ "$#" -gt 0 ]]; then task_ui="$1"; fi
if [[ "$task_ui" == "--lifecycle" ]]; then
  task_app="${2:?Pass the installed application path}"
  xcrun swiftc -swift-version 5 -num-threads 1 \
    -target "$task_arch-apple-macosx13.0" \
    -module-cache-path "$project_dir/build/macos-module-cache" \
    "$project_dir/macos/src/SkinTheme.swift" "$project_dir/macos/src/MacEditor.swift" \
    "$project_dir/macos/src/InstanceLock.swift" "$project_dir/macos/src/PetRuntime.swift" \
    "$project_dir/macos/src/InputWindowTracker.swift" \
    "$project_dir/macos/tests/LifecycleChecks.swift" -o "$task_checks/lifecycle-checks"
  "$task_checks/lifecycle-checks" "$task_app"
fi
if [[ "$task_ui" == "--ui" ]]; then
  task_app="$project_dir/build/macos-$task_arch/WeType Skin Studio.app"
  if [[ ! -d "$task_app" ]]; then bash "$project_dir/macos/build.sh"; fi
  xcrun swiftc -swift-version 5 -num-threads 1 \
    -target "$task_arch-apple-macosx13.0" \
    -module-cache-path "$project_dir/build/macos-module-cache" \
    "$project_dir/macos/src/SkinTheme.swift" "$project_dir/macos/src/MacEditor.swift" \
    "$project_dir/macos/src/InstanceLock.swift" \
    "$project_dir/macos/src/PetRuntime.swift" \
    "$project_dir/macos/tests/EditorChecks.swift" -o "$task_checks/editor-checks"
  "$task_checks/editor-checks" "$task_app/Contents/Resources" "$task_checks"
fi
