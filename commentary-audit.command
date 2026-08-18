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
files+=(test-mechanics.command test-rebirth-policy.command tests/test_deployment_lifecycle.command)
while IFS= read -r file; do
  files+=("$file")
done < <(rg --files tests | rg '\.cs$')
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

# Task 29's compatibility delegate may remain as a source-level migration seam, but it must not
# dispatch a raw Action. All executable automation in the one-second sweep is a typed child intent.
if rg -q 'private static void RunAutomationStep\(' source/Main.cs; then
  run_step_body=$(sed -n '/private static void RunAutomationStep(/,/^        }/p' source/Main.cs)
  if print -r -- "$run_step_body" | rg -q 'action\(\);'; then
    print -u2 "Raw Action dispatch remains in Main.RunAutomationStep"
    exit 1
  fi
  if ! print -r -- "$run_step_body" | rg -q 'typed child intent is required'; then
    print -u2 "Main.RunAutomationStep does not publish the typed-intent hold"
    exit 1
  fi
fi
# The compatibility helper is restricted to explicit user key commands (F8/F3/F7). Automated
# callbacks use MutationCoordinator child intents or visible typed-intent holds.
if rg -n 'TryRunMutation\(' source/Main.cs \
    | rg -v 'private static bool TryRunMutation|manual (loadout|quick loadout|digger|quick diggers|quicksave|quickload)'; then
  print -u2 "Automated raw TryRunMutation callsite remains"
  exit 1
fi
if ! rg -q 'GlobalSchedulerCanExecute \{ get \{ return false; \} \}' source/Autopilot/AutopilotPlan.cs; then
  print -u2 "Global scheduler shadow hard-false invariant is missing"
  exit 1
fi
if ! rg -q 'AllowEndSequence = false' source/Autopilot/AutopilotConfig.cs; then
  print -u2 "END execution no longer defaults fail-closed"
  exit 1
fi
print "Commentary audit passed for ${#files} maintained executable source files."
