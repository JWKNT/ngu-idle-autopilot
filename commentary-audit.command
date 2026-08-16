#!/bin/zsh
# FILE PURPOSE
#
# This lightweight repository check enforces the minimum presence of the conceptual
# handoff blocks described in AGENTS.md. It scans maintained C#, Swift, shell, and
# Markdown entrypoints, excluding generated/vendored/runtime artifacts. It cannot
# prove that prose is correct; reviewers must update stale explanations whenever
# behavior changes. A non-zero exit makes missing coverage visible before deploy.

set -euo pipefail
repo_dir=${0:A:h}
cd "$repo_dir"

typeset -a files
files=()
while IFS= read -r file; do
  files+=("$file")
done < <(rg --files source monitor | rg '\.(cs|swift)$' | rg -v 'SettingsForm\.Designer\.cs$|SimpleJson\.cs$')
files+=(run.command stop.command build.command status.command commentary-audit.command)
files+=(source/NGUInjector.csproj monitor/Info.plist AGENTS.md COMMENTING.md)
files+=(README.md README-AUTOPILOT.md ARCHITECTURE.md docs/GUIDE-AUDIT.md)

missing=0
for file in "${files[@]}"; do
  if ! head -n 45 "$file" | rg -q 'FILE PURPOSE'; then
    print -u2 "Missing FILE PURPOSE block: $file"
    missing=1
  fi
done

if (( missing )); then
  exit 1
fi
print "Commentary audit passed for ${#files} maintained executable source files."
