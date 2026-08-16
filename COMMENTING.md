<!--
FILE PURPOSE

This document explains why the repository intentionally carries unusually large
conceptual comments. It complements AGENTS.md: AGENTS.md is the imperative contract
for future contributors, while this page gives reviewers the rationale and scope.
-->

# Why this bot uses heavy block comments

The autopilot is both an optimizer and a live-state mutation system. Its important
decisions are distributed across decompiled game formulas, native Unity controller
semantics, horizon models, inventory identity rules, and a separate read-only
monitor. Compact code can work today while concealing an assumption that breaks at
the next boss, difficulty, set, or save reload.

Each maintained file therefore starts with a `FILE PURPOSE` block describing its
role, mechanism, boundaries, invariants, and extension points. Large decision files
also explain high-risk stages close to the relevant logic. The goal is that a future
agent can identify the authoritative policy owner and the required state proof
without reverse-engineering the entire repository.

Good comments answer questions such as:

- Which native controller performs the mutation, and how is success verified?
- Is a value persistent, rebirth-local, transient, or merely projected?
- Which apparent shortcut would lose an item, reward, unlock, or saved loadout?
- Which optimizer supplies this decision, and what is intentionally heuristic?
- Where should a new mechanic be modeled without duplicating authority?

Avoid line-by-line narration. Comments should preserve reasoning and safety
constraints that syntax cannot express. Generated, vendored, binary, runtime, and
legal files remain exempt and are documented by their maintained parent.
