#!/usr/bin/env python3
"""
FILE PURPOSE

Purpose: serve the public dashboard shell as a local NGU Idle client and expose the
bot's confirmed telemetry to that shell through a small read-only HTTP API.

Mechanism: a loopback-only ThreadingHTTPServer serves the repository's docs/ tree
and builds /api/state from the matching runtime/deployment.json + decision.json epoch
and an explicitly session-bound tail of runtime/logs/actions.log.  A derived
observability view normalizes optional rebirth, challenge, difficulty, terminal,
capacity, scheduler, transaction, and producer-identity fields without changing their
meaning; it never invents an ETA when the producer supplied no finite estimate.  The optional
daemon mode detaches from the launcher and records its own PID for exact lifecycle
cleanup.  The public jehlp.net copy may request the same endpoint; explicit CORS and
Private Network Access headers permit that hand-off when the browser allows it.

Inputs and outputs: reads generated telemetry and action logs, returns JSON, and
serves static HTML/CSS/JavaScript.  It never imports the injector, calls a game
controller, writes the save, or accepts commands.  Malformed or stale telemetry is
reported honestly instead of reusing a prior frame.  Recent action failures are
deduplicated for display while retaining occurrence counts and exact log messages.

Invariants and safety: bind only to 127.0.0.1; allow only GET/HEAD/OPTIONS; expose no
directory outside the configured docs root; do not return arbitrary files from the
runtime directory; and never turn an intended action into a confirmed event.

Extension points and non-goals: new presentation fields belong in docs/assets/app.js
and new telemetry belongs in the bot's decision schema.  This bridge may add derived
read-only summaries, but it is deliberately not a remote-control API or persistence
layer.
"""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import sys
from datetime import datetime, timezone
from functools import partial
from http import HTTPStatus
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlsplit


EVENT_LINE = re.compile(
    r"^(?P<clock>\d{2}:\d{2}:\d{2}(?:\.\d+)?)\s+"
    r"\[(?P<category>[^\]]+)\]\s+"
    r"(?:\((?P<run>[^)]*)\)\s+)?(?P<message>.*)$"
)
SESSION_MARKER = re.compile(
    r"^=== SESSION (?P<observed>\S+) id (?P<session>\S+) "
    r"build (?P<build>\S+) pid (?P<pid>\d+) ===$"
)
EVIDENCE_SUFFIX = re.compile(r"\s+\[confirmed(?:[^\]]*)\]\s*$", re.IGNORECASE)
ALWAYS_INTERESTING = {"TITAN", "DISCOVERY", "REBIRTH", "CHALLENGE", "MACGUFFIN", "DEATH"}
IRREVERSIBLE_TERMS = {
    "rebirth",
    "challenge",
    "consume",
    "trash",
    "blood",
    "loadout",
    "rollback",
    "purchase",
}
ACTION_ERROR_CATEGORIES = {"ERROR", "EXCEPTION", "FAILED", "REJECTED", "SAFETY"}
LEGACY_CHALLENGE_ADMISSION = re.compile(
    r"(?P<name>[A-Z0-9]+-\d+):\s*target Boss (?P<boss>\d+),\s*"
    r"p90 (?P<eta>\d+)s,\s*recovery (?P<recovery>\d+)s,\s*"
    r"(?P<evidence>.*?)(?=\s+\|\s+|$)"
)
CHALLENGE_ADMISSION = re.compile(
    r"(?P<name>[A-Z0-9]+-\d+)\s*\[[^\]]*?"
    r"(?:Boss (?P<boss>\d+)|levels (?P<level>\d+)/\d+),\s*"
    r"p90 (?P<eta>[\d.]+)(?P<eta_unit>[smh]),\s*"
    r"recovery (?P<recovery>[\d.]+)(?P<recovery_unit>[smh])\]:\s*"
    r"(?P<evidence>.*?)(?=\s+\|\s+|$)"
)
_SESSION_BOUNDARIES: dict[tuple[str, str], tuple[int, int, int, int, bytes]] = {}


def iso_age_seconds(value: Any) -> float | None:
    """Return a truthful telemetry age, accepting the producer's ISO-8601 form."""
    if not isinstance(value, str) or not value.strip():
        return None
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return max(0.0, (datetime.now(timezone.utc) - parsed).total_seconds())
    except ValueError:
        return None


def finite_number(value: Any) -> float | None:
    """Normalize JSON numbers while rejecting booleans, NaN, and infinities."""
    if value is None or isinstance(value, bool):
        return None
    try:
        result = float(value)
    except (TypeError, ValueError):
        return None
    if result != result or result in {float("inf"), float("-inf")}:
        return None
    return result


def optional_seconds(value: Any) -> int | None:
    """Return only finite, non-negative ETA values; -1 means unavailable upstream."""
    result = finite_number(value)
    return None if result is None or result < 0 else int(round(result))


def optional_count(value: Any) -> int | None:
    """Return a non-negative integer count without turning a missing value into zero."""
    result = finite_number(value)
    return None if result is None or result < 0 else int(result)


def optional_nonnegative_number(value: Any) -> float | None:
    """Preserve an emitted finite statistic exactly while rejecting legacy -1 sentinels."""
    result = finite_number(value)
    return None if result is None or result < 0 else result


def first_text(state: dict[str, Any], *keys: str) -> str:
    for key in keys:
        value = state.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return ""


def first_bool(state: dict[str, Any], *keys: str) -> bool | None:
    for key in keys:
        value = state.get(key)
        if isinstance(value, bool):
            return value
    return None


def first_seconds(state: dict[str, Any], *keys: str) -> int | None:
    for key in keys:
        value = optional_seconds(state.get(key))
        if value is not None:
            return value
    return None


def ratio(explicit: Any, preview: Any, current: Any) -> float | None:
    """Prefer the producer's ratio and otherwise derive the exact visible preview ratio."""
    chosen = finite_number(explicit)
    if chosen is not None and chosen >= 0:
        return chosen
    preview_number = finite_number(preview)
    current_number = finite_number(current)
    if preview_number is None or current_number is None or current_number <= 0:
        return None
    return preview_number / current_number


def duration_token_seconds(value: str, unit: str) -> int:
    multiplier = {"s": 1.0, "m": 60.0, "h": 3600.0}[unit]
    return int(round(float(value) * multiplier))


def parse_challenge_admission(summary: str) -> dict[str, Any] | None:
    """Accept both legacy second-valued and current human-duration admission summaries."""
    match = CHALLENGE_ADMISSION.search(summary)
    if match:
        return {
            "name": match.group("name"),
            "boss": int(match.group("boss")) if match.group("boss") else None,
            "level": int(match.group("level")) if match.group("level") else None,
            "eta": duration_token_seconds(match.group("eta"), match.group("eta_unit")),
            "recovery": duration_token_seconds(
                match.group("recovery"), match.group("recovery_unit")
            ),
            "evidence": match.group("evidence"),
        }
    match = LEGACY_CHALLENGE_ADMISSION.search(summary)
    if not match:
        return None
    return {
        "name": match.group("name"),
        "boss": int(match.group("boss")),
        "level": None,
        "eta": int(match.group("eta")),
        "recovery": int(match.group("recovery")),
        "evidence": match.group("evidence"),
    }


def _same_text(left: Any, right: Any, *, casefold: bool = False) -> bool:
    if not isinstance(left, str) or not left.strip() or not isinstance(right, str) or not right.strip():
        return False
    return left.casefold() == right.casefold() if casefold else left == right


def deployment_matches_decision(
    state: dict[str, Any], deployment: dict[str, Any] | None
) -> bool:
    """Require the same process, session, build, and artifact hashes on both frames."""
    if not isinstance(deployment, dict):
        return False
    deployment_schema = optional_count(deployment.get("schemaVersion"))
    producer_pid = optional_count(state.get("producerPid"))
    deployment_pid = optional_count(deployment.get("producerPid"))
    return bool(
        deployment_schema is not None
        and deployment_schema >= 2
        and producer_pid is not None
        and producer_pid > 0
        and producer_pid == deployment_pid
        and _same_text(state.get("producerSessionId"), deployment.get("producerSessionId"))
        and _same_text(state.get("buildId"), deployment.get("activeBuildId"), casefold=True)
        and _same_text(
            state.get("diskArtifactSha256"), deployment.get("diskArtifactSha256"), casefold=True
        )
        and _same_text(
            state.get("gameAssemblySha256"), deployment.get("gameAssemblySha256"), casefold=True
        )
    )


def normalized_scheduler(state: dict[str, Any]) -> dict[str, Any]:
    source = state.get("globalScheduler")
    scheduler = source if isinstance(source, dict) else {}
    provenance = first_text(scheduler, "provenance")
    sample_count = optional_count(scheduler.get("sampleCount"))
    confidence = finite_number(scheduler.get("confidence"))
    if not provenance or provenance.casefold() == "unknown":
        provenance = ""
        confidence = None
        sample_count = None
    if confidence is not None and not 0 <= confidence <= 1:
        confidence = None
    return {
        "status": first_text(scheduler, "status") or "Unavailable",
        "authority": first_text(scheduler, "authority") or None,
        "canExecute": scheduler.get("canExecute") if isinstance(scheduler.get("canExecute"), bool) else None,
        "snapshotHash": first_text(scheduler, "snapshotHash") or None,
        "modelHash": first_text(scheduler, "modelHash") or None,
        "objectiveHash": first_text(scheduler, "objectiveHash") or None,
        "action": first_text(scheduler, "action") or None,
        "actionId": first_text(scheduler, "actionId") or None,
        "nextEvent": first_text(scheduler, "nextEvent") or None,
        "eventId": first_text(scheduler, "eventId") or None,
        "meanSeconds": optional_nonnegative_number(scheduler.get("meanSeconds")),
        "p50Seconds": optional_nonnegative_number(scheduler.get("p50Seconds")),
        "p90Seconds": optional_nonnegative_number(scheduler.get("p90Seconds")),
        "lowerBoundSeconds": optional_nonnegative_number(scheduler.get("lowerBoundSeconds")),
        "upperBoundSeconds": optional_nonnegative_number(scheduler.get("upperBoundSeconds")),
        "gapSeconds": optional_nonnegative_number(scheduler.get("gapSeconds")),
        "regretSeconds": optional_nonnegative_number(scheduler.get("regretSeconds")),
        "runnerUp": first_text(scheduler, "runnerUp") or None,
        "blocker": first_text(scheduler, "blocker") or None,
        "blockerDetail": first_text(scheduler, "blockerDetail") or None,
        "rolloutFallback": scheduler.get("rolloutFallback")
        if isinstance(scheduler.get("rolloutFallback"), bool)
        else None,
        "expandedNodes": optional_count(scheduler.get("expandedNodes")),
        "generatedTransitions": optional_count(scheduler.get("generatedTransitions")),
        "dominancePruned": optional_count(scheduler.get("dominancePruned")),
        "observationStatus": first_text(scheduler, "observationStatus") or None,
        "observedSeconds": optional_nonnegative_number(scheduler.get("observedSeconds")),
        "timingResidualSeconds": finite_number(scheduler.get("timingResidualSeconds")),
        "deltaResidual": finite_number(scheduler.get("deltaResidual")),
        "provenance": provenance or None,
        "sampleCount": sample_count,
        "confidence": confidence,
    }


def build_observability(
    state: dict[str, Any], deployment: dict[str, Any] | None = None
) -> dict[str, Any]:
    """Build a stable read-only decision view from optional producer telemetry.

    Missing data stays ``None``.  In particular, an execution hold has no countdown,
    and an unadmitted challenge has no ETA.  This is presentation normalization only:
    admission and mutation authority remain wholly owned by the injector.
    """
    target = finite_number(state.get("rebirthSeconds"))
    elapsed = finite_number(state.get("rebirthElapsed"))
    execution_hold = state.get("rebirthExecutionHold") is True
    execution_enabled = first_bool(state, "rebirthExecutionEnabled")
    no_reset_challenge = target is not None and target < 0
    reset_eta = None
    if not execution_hold and not no_reset_challenge and target is not None and elapsed is not None:
        reset_eta = max(0, int(round(target - elapsed)))

    safety_reason = first_text(state, "rebirthSafetyBlockReason")
    selection_reason = first_text(state, "rebirthReason", "rebirthObjective")
    eta_reason = first_text(state, "rebirthEtaReason")
    if no_reset_challenge:
        action = "no-reset-challenge"
        action_label = "NO RESET — active challenge forbids rebirth"
    elif execution_hold:
        action = "hold"
        action_label = "HOLD — no executable reset is scheduled"
    elif execution_enabled is False:
        action = "disabled"
        action_label = "DISABLED — rebirth execution is off"
    elif reset_eta == 0:
        action = "reset-due"
        action_label = "RESET DUE — waiting for the verified native boundary"
    elif reset_eta is not None:
        action = "reset-at-checkpoint"
        action_label = "RESET at the selected checkpoint"
    else:
        action = "unknown"
        action_label = "Rebirth decision telemetry is incomplete"

    current_attack = finite_number(state.get("rebirthCurrentAttackMultiplier"))
    current_defense = finite_number(state.get("rebirthCurrentDefenseMultiplier"))
    preview_attack = finite_number(state.get("rebirthNextAttackMultiplierPreview"))
    preview_defense = finite_number(state.get("rebirthNextDefenseMultiplierPreview"))
    attack_ratio = ratio(state.get("rebirthProjectedAttackMultiplier"), preview_attack, current_attack)
    defense_ratio = ratio(state.get("rebirthProjectedDefenseMultiplier"), preview_defense, current_defense)
    visible_ratios = [value for value in (attack_ratio, defense_ratio) if value is not None]
    selected_ratio = finite_number(state.get("rebirthMinimumNumberRatio"))
    if selected_ratio is not None and selected_ratio < 0:
        selected_ratio = None

    challenge_summary = first_text(state, "challengeEvidenceSummary", "challengeAdmissionReason")
    parsed = parse_challenge_admission(challenge_summary)
    challenge_active = first_bool(state, "challengeActive", "inChallenge")
    if challenge_active is None:
        challenge_active = "active challenge" in first_text(state, "stage").lower()
    challenge_name = first_text(
        state, "nextChallengeName", "challengeName", "challengeType", "challengeRecommendation"
    )
    if not challenge_name and parsed:
        challenge_name = parsed["name"]
    admitted = first_bool(state, "nextChallengeAdmitted", "challengeAdmitted")
    if admitted is None:
        admitted = parsed is not None
    challenge_eta = first_seconds(
        state, "nextChallengeEtaSeconds", "challengeEtaSeconds", "challengePessimisticClearSeconds"
    )
    recovery_eta = first_seconds(state, "challengeRecoveryEtaSeconds")
    target_boss = finite_number(state.get("challengeTargetBoss"))
    target_level = finite_number(state.get("challengeTargetLevel"))
    if target_boss is not None and target_boss < 0:
        target_boss = None
    if target_level is not None and target_level < 0:
        target_level = None
    if parsed:
        if challenge_eta is None:
            challenge_eta = parsed["eta"]
        if recovery_eta is None:
            recovery_eta = parsed["recovery"]
        if target_boss is None and parsed["boss"] is not None:
            target_boss = float(parsed["boss"])
        if target_level is None and parsed["level"] is not None:
            target_level = float(parsed["level"])
    if challenge_active:
        challenge_status = "active"
        challenge_label = challenge_name or "Active challenge (type not emitted)"
    elif admitted:
        challenge_status = "admitted"
        challenge_label = challenge_name or "Next admitted challenge"
    else:
        challenge_status = "none-admitted"
        challenge_label = "No challenge admitted"

    transaction_error = first_text(state, "automationTransactionError")
    transaction_complete = first_bool(state, "automationTransactionComplete")
    build_id = first_text(state, "buildId")
    session_id = first_text(state, "producerSessionId")
    producer_pid = finite_number(state.get("producerPid"))
    decision_epoch = first_text(state, "gameEpochFingerprint")
    deployment_epoch = first_text(deployment or {}, "gameEpochFingerprint")
    deployment_match = deployment_matches_decision(state, deployment)

    root_source = state.get("mutationRoot")
    root = root_source if isinstance(root_source, dict) else {}
    root_id = optional_count(root.get("id"))
    if root_id == 0:
        root_id = None
    root_state = first_text(root, "state")
    root_epoch = first_text(root, "epochFingerprint")
    committed_steps = optional_count(root.get("committedSteps"))
    pending_steps = optional_count(root.get("pendingSteps"))
    rejected_steps = optional_count(root.get("rejectedSteps"))
    quarantined_steps = optional_count(root.get("quarantinedSteps"))
    root_epoch_matches = (
        root_epoch == decision_epoch if root_epoch and decision_epoch else None
    )
    lowered_root_state = root_state.casefold()
    if (quarantined_steps or 0) > 0 or "quarant" in lowered_root_state or root_epoch_matches is False:
        transaction_status = "Quarantined"
    elif transaction_error:
        transaction_status = "Error"
    elif (pending_steps or 0) > 0 or lowered_root_state in {"open", "pending"}:
        transaction_status = "Pending"
    elif root_id is None or lowered_root_state in {"", "held", "not-planned", "not planned"}:
        transaction_status = "Held"
    elif transaction_complete:
        transaction_status = "Committed"
    else:
        transaction_status = "Pending"

    if not build_id or not session_id or producer_pid is None or producer_pid <= 0 or not decision_epoch:
        join_status = "Pending"
    elif not deployment_match or root_epoch_matches is False:
        join_status = "Quarantined"
    else:
        join_status = "Bound"

    staged_source = state.get("stagedAuthority")
    staged = staged_source if isinstance(staged_source, dict) else {}
    staged_authority = {
        key: "Enabled" if value is True else "Held" if value is False else "Unavailable"
        for key, value in (
            ("verifiedReversible", staged.get("verifiedReversible")),
            ("permanentPurchases", staged.get("permanentPurchases")),
            ("moneyPit", staged.get("moneyPit")),
            ("challenges", staged.get("challenges")),
            ("difficulty", staged.get("difficulty")),
            ("titan1Through12", staged.get("titan1Through12")),
            ("titan13Through14", staged.get("titan13Through14")),
            ("move69", staged.get("move69")),
            ("endSequence", staged.get("endSequence")),
        )
    }

    inventory_total = optional_count(state.get("inventoryTotalSlots"))
    inventory_used = optional_count(state.get("inventoryUsedSlots"))
    inventory_free = optional_count(state.get("inventoryFreeSlots"))
    capacity_reserve = optional_count(state.get("collectionRequiredFreeReserve"))
    capacity_margin = (
        inventory_free - capacity_reserve
        if inventory_free is not None and capacity_reserve is not None
        else None
    )
    capacity_status = "Unavailable" if inventory_total is None or inventory_free is None else (
        "Held" if capacity_margin is not None and capacity_margin < 0 else "Available"
    )

    scheduler = normalized_scheduler(state)
    difficulty_value = optional_count(state.get("difficulty"))
    difficulty_names = {0: "Normal", 1: "Evil", 2: "Sadistic"}
    difficulty_target = first_text(
        state, "difficultyTarget", "nextDifficulty", "difficultyTransitionTarget"
    )
    difficulty_authority = staged_authority["difficulty"]
    difficulty_status = "Pending" if difficulty_authority == "Enabled" and difficulty_target else (
        "Held" if difficulty_authority == "Held" else "Unavailable"
    )
    end_ready = first_bool(state, "endgameReadyToTrigger")
    end_authorized = first_bool(state, "endgameExecutionAuthorized")
    end_status = "Pending" if end_ready and end_authorized else (
        "Held" if end_authorized is False or end_ready is False else "Unavailable"
    )

    return {
        "rebirth": {
            "action": action,
            "actionLabel": action_label,
            "reason": safety_reason or selection_reason or "No decision reason was emitted.",
            "noResetHold": execution_hold or no_reset_challenge or execution_enabled is False,
            "targetRunAgeSeconds": optional_seconds(target),
            "currentRunAgeSeconds": optional_seconds(elapsed),
            "resetEtaSeconds": reset_eta,
            "nextPositiveEtaSeconds": first_seconds(state, "rebirthNextPositiveEtaSeconds"),
            "nextEvaluationEtaSeconds": first_seconds(state, "rebirthNextEvaluationEtaSeconds"),
            "etaReason": eta_reason or None,
            "resetRecoveryEtaSeconds": first_seconds(state, "rebirthRecoveryResetRouteEtaSeconds"),
            "continueRecoveryEtaSeconds": first_seconds(state, "rebirthRecoveryContinueRouteEtaSeconds"),
            "selectedCycleRecoveryEtaSeconds": first_seconds(
                state, "rebirthOptimizerRecordRecoveryEtaSeconds"
            ),
            "recoveryRemainingBosses": optional_seconds(state.get("rebirthRecoveryRemainingBosses")),
            "recoveryReason": first_text(
                state, "rebirthRecoveryReason", "rebirthOptimizerRecoveryReason"
            ),
            "model": first_text(state, "rebirthOptimizerModel") or None,
            "provenance": first_text(state, "rebirthEtaProvenance") or None,
            "confidence": finite_number(state.get("rebirthEtaConfidence")),
            "currentAttack": current_attack,
            "currentDefense": current_defense,
            "previewAttack": preview_attack,
            "previewDefense": preview_defense,
            "previewAttackRatio": attack_ratio,
            "previewDefenseRatio": defense_ratio,
            "previewWorstRatio": min(visible_ratios) if visible_ratios else None,
            "selectedCheckpointWorstRatio": selected_ratio,
        },
        "challenge": {
            "status": challenge_status,
            "label": challenge_label,
            "admitted": admitted,
            "active": challenge_active,
            "entryEtaSeconds": reset_eta if admitted and not challenge_active else 0 if challenge_active else None,
            "clearEtaSeconds": challenge_eta,
            "recoveryEtaSeconds": recovery_eta,
            "targetBoss": int(target_boss) if target_boss is not None else None,
            "targetLevel": int(target_level) if target_level is not None else None,
            "reason": challenge_summary or "The producer emitted no challenge-admission evidence.",
            "provenance": first_text(state, "challengeEtaProvenance") or None,
            "confidence": finite_number(state.get("challengeEtaConfidence")),
        },
        "difficulty": {
            "status": difficulty_status,
            "current": difficulty_names.get(difficulty_value) if difficulty_value is not None else None,
            "target": difficulty_target or None,
            "etaSeconds": first_seconds(
                state, "difficultyEtaSeconds", "difficultyTransitionEtaSeconds"
            ),
            "blocker": first_text(
                state, "difficultyBlocker", "difficultyTransitionReason"
            ) or None,
            "provenance": first_text(state, "difficultyEtaProvenance") or None,
            "confidence": finite_number(state.get("difficultyEtaConfidence")),
        },
        "end": {
            "status": end_status,
            "objective": first_text(state, "endgameObjective") or None,
            "missing": first_text(state, "endgameMissingSummary") or None,
            "titan12VersionTarget": optional_count(state.get("endgameTitan12VersionTarget")),
            "ready": end_ready,
            "authorized": end_authorized,
            "meanSeconds": scheduler["meanSeconds"],
            "p50Seconds": scheduler["p50Seconds"],
            "p90Seconds": scheduler["p90Seconds"],
            "lowerBoundSeconds": scheduler["lowerBoundSeconds"],
            "provenance": scheduler["provenance"],
            "confidence": scheduler["confidence"],
        },
        "identity": {
            "verifiedEnvelope": bool(
                deployment_match and decision_epoch and root_epoch_matches is not False
            ),
            "decisionEnvelopeComplete": bool(
                build_id and session_id and producer_pid and producer_pid > 0 and decision_epoch
            ),
            "joinStatus": join_status,
            "deploymentDecisionMatch": deployment_match,
            "rootEpochMatchesDecision": root_epoch_matches,
            "buildId": build_id or None,
            "producerPid": int(producer_pid) if producer_pid is not None and producer_pid > 0 else None,
            "producerSessionId": session_id or None,
            "diskArtifactSha256": first_text(state, "diskArtifactSha256") or None,
            "gameAssemblySha256": first_text(state, "gameAssemblySha256") or None,
            "activeLocationSha256AtObservation": first_text(
                state, "activeLocationSha256AtObservation"
            ) or None,
            "activeImageHashAvailable": state.get("activeImageHashAvailable") is True,
            "activeMatchesDisk": first_text(state, "activeMatchesDisk") or "unknown",
            "decisionEpochFingerprint": decision_epoch or None,
            "deploymentEpochFingerprint": deployment_epoch or None,
            "rootEpochFingerprint": root_epoch or None,
        },
        "transaction": {
            "status": transaction_status,
            "complete": transaction_complete is True,
            "error": transaction_error or None,
            "rootId": root_id,
            "rootState": root_state or "Unavailable",
            "rootEpochFingerprint": root_epoch or None,
            "committedSteps": committed_steps,
            "pendingSteps": pending_steps,
            "rejectedSteps": rejected_steps,
            "quarantinedSteps": quarantined_steps,
        },
        "authority": {
            "stage": first_text(state, "authorityStage") or "Unavailable",
            "routes": staged_authority,
        },
        "capacity": {
            "status": capacity_status,
            "totalSlots": inventory_total,
            "usedSlots": inventory_used,
            "freeSlots": inventory_free,
            "requiredReserve": capacity_reserve,
            "projectedNewSlots": optional_count(state.get("collectionProjectedNewSlots")),
            "marginSlots": capacity_margin,
            "pressure": first_text(state, "inventoryPressure") or None,
            "provenance": "LiveCounters" if inventory_total is not None and inventory_free is not None else None,
            "confidence": 1.0 if inventory_total is not None and inventory_free is not None else None,
            "exactDeliveryProof": first_bool(state, "capacityProofExact"),
        },
        "scheduler": scheduler,
    }


def is_key_event(category: str, message: str) -> bool:
    """Keep only permanent, record-setting, or safety-consequential transitions."""
    category = category.upper()
    lowered = message.lower()
    if category in ALWAYS_INTERESTING:
        return True
    if category == "BOSS":
        # Ordinary catch-up kills create EXP but are repeated run traffic. The
        # dashboard already projects their value; only a new record is a key event.
        return "record fight boss" in lowered or "new boss record" in lowered
    if category == "PURCHASE":
        return any(term in lowered for term in ("exp", "ap", "arbitrary point", "permanent"))
    if category == "COLLECTION":
        return "maxxed" in lowered or "set complete" in lowered
    if category == "PROGRESSION":
        return any(term in lowered for term in ("completed", "unlocked", "consumed progression", "new record"))
    if category == "QUEST":
        return "completed" in lowered or "turned in" in lowered
    if category == "YGG":
        return any(term in lowered for term in ("permanent", "tier cap", "max tier"))
    if category == "LOOT":
        return any(term in lowered for term in ("ultra rare", "legendary", "macguffin", "heart"))
    if category in {"PERK", "QUIRK", "WISH", "HACK", "NGU", "BEARD", "DIGGER"}:
        return any(term in lowered for term in ("unlocked", "milestone", "maxxed", "permanent"))
    if category in {"BLOOD", "SPELL"}:
        return any(term in lowered for term in ("iron pill", "number", "macguffin", "permanent"))
    if category in {"MILESTONE", "REWARD"}:
        return any(term in lowered for term in ("unlocked", "permanent", "record", "set complete", "maxxed"))
    if category == "REJECTED":
        return any(term in lowered for term in IRREVERSIBLE_TERMS)
    return False


def event_importance(category: str, message: str) -> str:
    """Attach presentation priority without changing the underlying evidence."""
    category = category.upper()
    lowered = message.lower()
    if category in {"REBIRTH", "CHALLENGE", "DEATH", "REJECTED"}:
        return "critical"
    if category in {"TITAN", "DISCOVERY", "MACGUFFIN", "PURCHASE"} or any(
        term in lowered for term in ("unlocked", "new record", "set complete", "permanent")
    ):
        return "major"
    return "milestone"


def compact_event_message(category: str, message: str) -> str:
    """Turn legacy raw rebirth snapshots into a readable event without losing the source log."""
    if category.upper() != "REBIRTH" or "pre{" not in message or "} post{" not in message:
        return message
    match = re.search(r"pre\{(?P<before>.*?)\}\s*post\{(?P<after>.*?)\}", message)
    if not match:
        return message

    def field(snapshot: str, name: str) -> str | None:
        found = re.search(rf"(?:^|,){re.escape(name)}=([^,}}]+)", snapshot)
        return found.group(1).strip() if found else None

    before = match.group("before")
    after = match.group("after")
    try:
        old_number = float(field(before, "numA") or "nan")
        new_number = float(field(after, "numA") or "nan")
        old_ap = float(field(before, "AP") or "nan")
        new_ap = float(field(after, "AP") or "nan")
        run_seconds = max(0, int(float(field(before, "time") or "0")))
        old_boss = int(field(before, "boss") or "0")
        new_boss = int(field(after, "boss") or "0")
        rebirths = int(field(after, "rebirths") or "0")
        challenge = (field(after, "challenge") or "none").split(";", 1)[0]
    except (TypeError, ValueError, OverflowError):
        return message
    if not all(math.isfinite(value) for value in (old_number, new_number, old_ap, new_ap)):
        return message

    days, remainder = divmod(run_seconds, 86400)
    hours, remainder = divmod(remainder, 3600)
    minutes, seconds = divmod(remainder, 60)
    if days:
        duration = f"{days}d {hours}h {minutes}m"
    elif hours:
        duration = f"{hours}h {minutes}m"
    elif minutes:
        duration = f"{minutes}m {seconds}s"
    else:
        duration = f"{seconds}s"
    percent = (new_number / old_number - 1.0) * 100.0 if old_number > 0 else 0.0
    ap_delta = new_ap - old_ap
    prefix = "Normal rebirth confirmed" if message.startswith("Completed normal rebirth") else "Rebirth confirmed"
    challenge_label = "no challenge" if challenge.lower().startswith("none") else f"challenge {challenge}"
    return (
        f"{prefix} after {duration} — Number {old_number:.3e} → {new_number:.3e} "
        f"({percent:+.1f}%); AP {ap_delta:+.0f}; Boss {old_boss} → {new_boss}; "
        f"rebirth #{rebirths}; {challenge_label}"
    )


def tail_text(path: Path, limit: int = 1_000_000) -> str:
    """Read only the bounded tail needed for recent events, aligned to a full line."""
    if not path.is_file():
        return ""
    with path.open("rb") as stream:
        stream.seek(0, os.SEEK_END)
        size = stream.tell()
        start = max(0, size - limit)
        stream.seek(start)
        data = stream.read()
    if start:
        _, _, data = data.partition(b"\n")
    return data.decode("utf-8", errors="replace")


def session_bound_lines(text: str, session_id: str | None) -> tuple[list[str], str]:
    """Select exactly one append-only producer session, or fail closed with no lines."""
    if not session_id:
        return [], "MissingSession"
    selected: list[str] = []
    admitted = False
    found = False
    for line in text.splitlines():
        marker = SESSION_MARKER.match(line.strip())
        if marker:
            admitted = marker.group("session") == session_id
            found = found or admitted
            continue
        if admitted:
            selected.append(line)
    return (selected, "Bound") if found else ([], "Unbound")


def session_tail_lines(
    path: Path, session_id: str | None, limit: int = 1_000_000
) -> tuple[list[str], str]:
    """Read a bounded current-session tail even when its marker predates the tail window.

    The first lookup scans the append-only file for the exact marker and caches its byte
    boundary. Later polls validate that marker in place, then read only the bounded tail.
    A subsequent different session marker closes admission, so a decision that lags a new
    producer still cannot display the newer producer's lines.
    """
    if not session_id:
        return [], "MissingSession"
    if not path.is_file():
        return [], "Unbound"
    try:
        stat = path.stat()
        key = (str(path.resolve()), session_id)
        boundary = _SESSION_BOUNDARIES.get(key)
        marker_start = marker_end = -1
        marker_bytes = b""
        with path.open("rb") as stream:
            if boundary and boundary[0] == stat.st_dev and boundary[1] == stat.st_ino:
                _, _, cached_start, cached_end, cached_bytes = boundary
                if stat.st_size >= cached_end:
                    stream.seek(cached_start)
                    if stream.read(cached_end - cached_start) == cached_bytes:
                        marker_start, marker_end, marker_bytes = (
                            cached_start, cached_end, cached_bytes
                        )
            if marker_end < 0:
                stream.seek(0)
                while True:
                    start = stream.tell()
                    raw = stream.readline()
                    if not raw:
                        break
                    marker = SESSION_MARKER.match(raw.decode("utf-8", errors="replace").strip())
                    if marker and marker.group("session") == session_id:
                        marker_start, marker_end, marker_bytes = start, stream.tell(), raw
                if marker_end < 0:
                    return [], "Unbound"
                _SESSION_BOUNDARIES[key] = (
                    stat.st_dev, stat.st_ino, marker_start, marker_end, marker_bytes
                )

            start = max(marker_end, stat.st_size - max(1, limit))
            stream.seek(start)
            if start > marker_end:
                stream.readline()
            admitted = True
            selected: list[str] = []
            for raw in stream:
                line = raw.decode("utf-8", errors="replace").rstrip("\r\n")
                marker = SESSION_MARKER.match(line.strip())
                if marker:
                    admitted = marker.group("session") == session_id
                    continue
                if admitted:
                    selected.append(line)
            return selected, "Bound"
    except OSError:
        return [], "Unbound"


def recent_key_events(
    path: Path, maximum: int = 30, session_id: str | None = None
) -> list[dict[str, str]]:
    events: list[dict[str, str]] = []
    lines, _ = session_tail_lines(path, session_id)
    for line in reversed(lines):
        match = EVENT_LINE.match(line.strip())
        if not match:
            continue
        category = match.group("category").upper()
        message = EVIDENCE_SUFFIX.sub("", match.group("message")).strip()
        message = compact_event_message(category, message)
        if not is_key_event(category, message):
            continue
        events.append(
            {
                "clock": match.group("clock"),
                "run": match.group("run") or "",
                "category": category,
                "importance": event_importance(category, message),
                "message": message,
            }
        )
        if len(events) >= maximum:
            break
    return events


def recent_action_errors(
    path: Path, maximum: int = 12, session_id: str | None = None
) -> list[dict[str, Any]]:
    """Return distinct recent safety/error messages with bounded-tail occurrence counts.

    A controller can reject the same unsafe attempt on every sweep.  Showing hundreds of
    identical rows obscures the actual failure, so the API preserves the newest occurrence
    and exact message while aggregating duplicates from the bounded log tail.
    """
    ordered: list[dict[str, Any]] = []
    by_message: dict[tuple[str, str], dict[str, Any]] = {}
    lines, _ = session_tail_lines(path, session_id)
    for line in reversed(lines):
        match = EVENT_LINE.match(line.strip())
        if not match:
            continue
        category = match.group("category").upper()
        if category not in ACTION_ERROR_CATEGORIES:
            continue
        message = EVIDENCE_SUFFIX.sub("", match.group("message")).strip()
        key = (category, message)
        if key in by_message:
            by_message[key]["count"] += 1
            continue
        if len(ordered) >= maximum:
            continue
        lowered = message.lower()
        event = {
            "clock": match.group("clock"),
            "run": match.group("run") or "",
            "category": category,
            "severity": "critical" if category in {"ERROR", "EXCEPTION", "FAILED"}
            or any(term in lowered for term in (" failed", "failure", "exception", "mismatch"))
            else "warning",
            "message": message,
            "count": 1,
        }
        by_message[key] = event
        ordered.append(event)
    return ordered


class DashboardHandler(SimpleHTTPRequestHandler):
    """Static-file handler with two fixed JSON endpoints and no mutation verbs."""

    server_version = "NGUDashboard/1"

    def __init__(self, *args: Any, runtime_dir: Path, **kwargs: Any) -> None:
        self.runtime_dir = runtime_dir
        super().__init__(*args, **kwargs)

    def log_message(self, format: str, *args: Any) -> None:
        # Lifecycle scripts own operational logging; routine polling should stay quiet.
        return

    def end_headers(self) -> None:
        origin = self.headers.get("Origin", "")
        if self._allowed_origin(origin):
            self.send_header("Access-Control-Allow-Origin", origin)
            self.send_header("Vary", "Origin")
        self.send_header("Access-Control-Allow-Private-Network", "true")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        super().end_headers()

    @staticmethod
    def _allowed_origin(origin: str) -> bool:
        if origin in {"https://jehlp.net", "https://www.jehlp.net"}:
            return True
        try:
            parsed = urlsplit(origin)
            return parsed.scheme == "http" and parsed.hostname in {"127.0.0.1", "localhost"}
        except ValueError:
            return False

    def do_OPTIONS(self) -> None:  # noqa: N802 - stdlib handler API
        self.send_response(HTTPStatus.NO_CONTENT)
        self.send_header("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", "Content-Type")
        self.send_header("Access-Control-Max-Age", "600")
        self.end_headers()

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler API
        path = urlsplit(self.path).path
        if path == "/api/health":
            self._send_json({"ok": True, "service": "ngu-dashboard", "schemaVersion": 2})
            return
        if path == "/api/state":
            self._send_state()
            return
        super().do_GET()

    def do_POST(self) -> None:  # noqa: N802 - explicit read-only boundary
        self.send_error(HTTPStatus.METHOD_NOT_ALLOWED, "This dashboard is read-only")

    do_PUT = do_POST
    do_PATCH = do_POST
    do_DELETE = do_POST

    def list_directory(self, path: str) -> None:
        self.send_error(HTTPStatus.NOT_FOUND)
        return None

    def _send_state(self) -> None:
        decision_path = self.runtime_dir / "decision.json"
        try:
            with decision_path.open("r", encoding="utf-8") as stream:
                state = json.load(stream)
        except FileNotFoundError:
            self._send_json(
                {"ok": False, "error": "Waiting for the bot's first confirmed snapshot."},
                HTTPStatus.SERVICE_UNAVAILABLE,
            )
            return
        except (OSError, json.JSONDecodeError) as error:
            self._send_json(
                {"ok": False, "error": f"Telemetry is temporarily unreadable: {error.__class__.__name__}."},
                HTTPStatus.SERVICE_UNAVAILABLE,
            )
            return

        deployment: dict[str, Any] | None = None
        try:
            with (self.runtime_dir / "deployment.json").open("r", encoding="utf-8") as stream:
                candidate = json.load(stream)
                if isinstance(candidate, dict):
                    deployment = candidate
        except (FileNotFoundError, OSError, json.JSONDecodeError):
            deployment = None

        observability = build_observability(state, deployment)
        identity = observability["identity"]
        session_id = (
            identity["producerSessionId"]
            if identity["deploymentDecisionMatch"] is True
            and identity["decisionEnvelopeComplete"] is True
            else None
        )
        action_log = self.runtime_dir / "logs" / "actions.log"
        _, tail_status = session_tail_lines(action_log, session_id)
        events = recent_key_events(action_log, session_id=session_id)
        action_errors = recent_action_errors(action_log, session_id=session_id)

        timestamp = (
            state.get("snapshotUtc")
            or state.get("timestampUtc")
            or state.get("generatedAt")
            or state.get("timestamp")
            or state.get("time")
        )
        payload = {
            "ok": True,
            "schemaVersion": 2,
            "servedAt": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "stateAgeSeconds": iso_age_seconds(timestamp),
            "state": state,
            "deployment": deployment,
            "observability": observability,
            "actionTail": {
                "status": tail_status if session_id else identity["joinStatus"],
                "producerSessionId": session_id,
                "eventCount": len(events),
                "errorCount": len(action_errors),
            },
            "events": events,
            "actionErrors": action_errors,
        }
        self._send_json(payload)

    def _send_json(self, payload: dict[str, Any], status: HTTPStatus = HTTPStatus.OK) -> None:
        body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


def parse_args() -> argparse.Namespace:
    repository = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description="Serve the read-only NGU dashboard on loopback.")
    parser.add_argument("--root", type=Path, default=repository / "docs")
    parser.add_argument("--runtime", type=Path, default=repository / "runtime")
    parser.add_argument("--port", type=int, default=47635)
    parser.add_argument("--daemon", action="store_true", help="Detach and continue in the background.")
    parser.add_argument("--pid-file", type=Path, help="Write the detached server's PID here.")
    parser.add_argument("--log", type=Path, help="Append detached stdout and stderr here.")
    return parser.parse_args()


def daemonize(pid_file: Path, log_path: Path) -> None:
    """Double-fork into a new session and publish only the final server PID."""
    first = os.fork()
    if first > 0:
        os._exit(0)
    os.setsid()
    second = os.fork()
    if second > 0:
        os._exit(0)

    os.umask(0o077)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    null_input = os.open(os.devnull, os.O_RDONLY)
    log_output = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    os.dup2(null_input, sys.stdin.fileno())
    os.dup2(log_output, sys.stdout.fileno())
    os.dup2(log_output, sys.stderr.fileno())
    if null_input > 2:
        os.close(null_input)
    if log_output > 2:
        os.close(log_output)
    pid_file.parent.mkdir(parents=True, exist_ok=True)
    pid_file.write_text(f"{os.getpid()}\n", encoding="ascii")


def main() -> None:
    args = parse_args()
    if args.daemon:
        if args.pid_file is None or args.log is None:
            raise SystemExit("--daemon requires --pid-file and --log")
        daemonize(args.pid_file.resolve(), args.log.resolve())
    root = args.root.resolve(strict=True)
    runtime = args.runtime.resolve(strict=True)
    handler = partial(DashboardHandler, directory=str(root), runtime_dir=runtime)
    server = ThreadingHTTPServer(("127.0.0.1", args.port), handler)
    server.daemon_threads = True
    print(f"NGU dashboard: http://127.0.0.1:{args.port}/", flush=True)
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
