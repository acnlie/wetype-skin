#!/bin/bash
set -euo pipefail
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
task_arch="$(uname -m)"
if [[ "${1:-}" != "--skip-build" ]]; then
  bash "$project_dir/macos/build.sh"
fi
task_output="$project_dir/build/macos-$task_arch"
task_app="$task_output/WeType Skin Studio.app"
codesign --verify --deep --strict "$task_app"
test -x "$task_app/Contents/Helpers/WeType Skin Pet.app/Contents/MacOS/WeTypeSkinPet"
test -f "$task_app/Contents/Resources/native/run-clone.sh"
test -f "$task_app/Contents/Resources/native/stop-clone.sh"
/usr/bin/python3 - "$task_app/Contents/Resources/frontend/pet" <<'PY'
import json,sys
from pathlib import Path
root=Path(sys.argv[1])
for clip in json.loads((root/'clips.json').read_text())['clips']:
    name=clip['file']
    if Path(name).name != name or not (root/name).is_file():
        raise SystemExit('Missing pet animation: '+name)
PY
task_staging="$(mktemp -d "$task_output/.package.XXXXXX")"
trap 'rm -rf "$task_staging"' EXIT
cp -R "$task_app" "$task_staging/WeType Skin Studio.app"
cp "$project_dir/macos/README.md" "$task_staging/README.md"
cp "$project_dir/LICENSE" "$task_staging/LICENSE"
ln -s /Applications "$task_staging/Applications"
hdiutil create -volname "WeType Skin Studio" -srcfolder "$task_staging" \
  -ov -format UDZO "$task_output/WeTypeSkinStudio-macos-$task_arch.dmg"
hdiutil verify "$task_output/WeTypeSkinStudio-macos-$task_arch.dmg"
echo "Packaged: $task_output/WeTypeSkinStudio-macos-$task_arch.dmg"
