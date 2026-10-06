#!/bin/bash
set -euo pipefail
task_clone="$HOME/Library/Input Methods/WeTypeSkinStudioInputMethod.app"
pkill -f "$task_clone/Contents/MacOS/WeType" 2>/dev/null || true
rm -f "$HOME/Library/Application Support/WeTypeSkinStudio/native/clone.pid"
echo "Stopped clone."
