#!/bin/bash
set -euo pipefail
script_dir="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HOME/Library/Application Support/WeTypeSkinStudio/native"
task_clone="$HOME/Library/Input Methods/WeTypeSkinStudioInputMethod.app"
"$script_dir/WeTypeSkinPrepare"
/usr/bin/open -g "$task_clone"
echo "Launched skin input method through Launch Services."
