"""
FILE PURPOSE

These unit tests pin the dashboard's consequential-event definition and normalized observability
contract. Routine catch-up traffic and reset-local milestones must stay out of the public ledger,
while records, permanent progression, irreversible safety failures, and resets remain visible.
The denser Adventure journal must include kills, deaths, route changes, and useful loot while
filtering ordinary ability-button spam.
Missing telemetry must remain unknown rather than turning into a false zero-second ETA. Challenge
rebirth legality must come only from its explicit telemetry, and the browser shell must keep the
current route beside the ranked priorities while showing the selected Boss as the headline value.
The tests never contact the live bot, mutate telemetry, or turn a missing field into an admission,
reset schedule, or deployment-verification claim.
"""

import tempfile
import unittest
from pathlib import Path

from monitor.dashboard_server import (
    build_observability,
    compact_event_message,
    event_importance,
    is_key_event,
    recent_key_events,
    recent_adventure_log,
    recent_action_errors,
    session_bound_lines,
    session_tail_lines,
)


def deployment_for(state: dict) -> dict:
    return {
        "schemaVersion": 2,
        "producerPid": state["producerPid"],
        "producerSessionId": state["producerSessionId"],
        "activeBuildId": state["buildId"],
        "diskArtifactSha256": state["diskArtifactSha256"],
        "gameAssemblySha256": state["gameAssemblySha256"],
        "gameEpochFingerprint": state.get("gameEpochFingerprint", "deployment-start"),
    }


class KeyEventPolicyTests(unittest.TestCase):
    def test_routine_catchup_and_training_are_not_key_events(self) -> None:
        self.assertFalse(is_key_event("BOSS", "Native controller victory over Boss 42"))
        self.assertFalse(is_key_event("MILESTONE", "Basic Training reached level 10000"))
        self.assertFalse(is_key_event("YGG", "Harvested Fruit of Gold"))

    def test_permanent_records_and_unlocks_are_key_events(self) -> None:
        self.assertTrue(is_key_event("BOSS", "Defeated record Fight Boss 58"))
        self.assertTrue(is_key_event("COLLECTION", "Forest set complete"))
        self.assertTrue(is_key_event("PERK", "Adventure perk milestone reached"))
        self.assertTrue(is_key_event("PROGRESSION", "ITOPOD unlocked"))

    def test_legacy_rebirth_snapshot_is_compacted_for_display(self) -> None:
        message = (
            "Completed normal rebirth [confirmed transaction] "
            "pre{numA=2.351185531977945E+37,numD=2.351185531977945E+37,"
            "previewA=1.2044625712533734E+37,previewD=1.2044625712533734E+37,"
            "AP=225,bloodNumber=1,difficulty=normal,boss=57,record=59/1/1,"
            "rebirths=51,time=5102.1868372349418,challenge=none:None;flags=;counts=} "
            "post{numA=1.2044625712533734E+37,numD=1.2044625712533734E+37,"
            "previewA=1.2044625712533734E+37,previewD=1.2044625712533734E+37,"
            "AP=228,bloodNumber=1,difficulty=normal,boss=0,record=59/1/1,"
            "rebirths=52,time=0,challenge=none:None;flags=;counts=}"
        )
        self.assertEqual(
            compact_event_message("REBIRTH", message),
            "Normal rebirth confirmed after 1h 25m — Number 2.351e+37 → 1.204e+37 "
            "(-48.8%); AP +3; Boss 57 → 0; rebirth #52; no challenge",
        )

    def test_only_irreversible_rejections_reach_the_ledger(self) -> None:
        self.assertFalse(is_key_event("REJECTED", "Allocation target was unavailable"))
        self.assertTrue(is_key_event("REJECTED", "Rebirth verification produced no delta"))
        self.assertEqual(event_importance("REJECTED", "Rebirth failed"), "critical")
        self.assertEqual(event_importance("PURCHASE", "Bought permanent Daycare"), "major")


class ObservabilityTests(unittest.TestCase):
    def test_selected_reset_exposes_countdown_recovery_and_distinct_number_ratios(self) -> None:
        view = build_observability(
            {
                "rebirthSeconds": 3600,
                "rebirthElapsed": 600,
                "rebirthExecutionHold": False,
                "rebirthExecutionEnabled": True,
                "rebirthReason": "best persistent-cycle score",
                "rebirthCurrentAttackMultiplier": 1e12,
                "rebirthCurrentDefenseMultiplier": 2e12,
                "rebirthNextAttackMultiplierPreview": 2e10,
                "rebirthNextDefenseMultiplierPreview": 2e10,
                "rebirthProjectedAttackMultiplier": 0.02,
                "rebirthProjectedDefenseMultiplier": 0.01,
                "rebirthMinimumNumberRatio": 0.25,
                "rebirthRecoveryResetRouteEtaSeconds": 1200,
                "rebirthRecoveryContinueRouteEtaSeconds": 4800,
                "rebirthOptimizerRecordRecoveryEtaSeconds": 3600,
                "rebirthRecoveryRemainingBosses": 4,
            }
        )
        rebirth = view["rebirth"]
        self.assertEqual(rebirth["action"], "reset-at-checkpoint")
        self.assertEqual(rebirth["resetEtaSeconds"], 3000)
        self.assertEqual(rebirth["resetRecoveryEtaSeconds"], 1200)
        self.assertEqual(rebirth["continueRecoveryEtaSeconds"], 4800)
        self.assertEqual(rebirth["selectedCycleRecoveryEtaSeconds"], 3600)
        self.assertEqual(rebirth["previewAttackRatio"], 0.02)
        self.assertEqual(rebirth["previewDefenseRatio"], 0.01)
        self.assertEqual(rebirth["previewWorstRatio"], 0.01)
        self.assertEqual(rebirth["selectedCheckpointWorstRatio"], 0.25)

    def test_hold_and_missing_fields_never_become_false_zero_etas(self) -> None:
        view = build_observability(
            {
                "rebirthExecutionHold": True,
                "rebirthReason": "waiting for a legal event boundary",
                "rebirthNextPositiveEtaSeconds": 840,
                "rebirthNextEvaluationEtaSeconds": 12,
                "rebirthEtaReason": "next finite event candidate",
                "challengeEvidenceSummary": "no same-type evidence yet",
                "challengeTargetBoss": -1,
                "challengeTargetLevel": -1,
            }
        )
        self.assertEqual(view["rebirth"]["action"], "hold")
        self.assertTrue(view["rebirth"]["noResetHold"])
        self.assertIsNone(view["rebirth"]["resetEtaSeconds"])
        self.assertEqual(view["rebirth"]["nextPositiveEtaSeconds"], 840)
        self.assertEqual(view["rebirth"]["nextEvaluationEtaSeconds"], 12)
        self.assertEqual(view["rebirth"]["etaReason"], "next finite event candidate")
        self.assertIsNone(view["rebirth"]["previewWorstRatio"])
        self.assertEqual(view["challenge"]["status"], "none-admitted")
        self.assertIsNone(view["challenge"]["entryEtaSeconds"])
        self.assertIsNone(view["challenge"]["clearEtaSeconds"])
        self.assertIsNone(view["challenge"]["targetBoss"])
        self.assertIsNone(view["challenge"]["targetLevel"])
        self.assertFalse(view["identity"]["verifiedEnvelope"])

    def test_no_rebirth_challenge_is_an_explicit_no_reset_policy(self) -> None:
        view = build_observability(
            {
                "stage": "Normal / active challenge",
                "rebirthSeconds": -1,
                "rebirthElapsed": 125,
                "rebirthReason": "No Rebirth challenge forbids resets",
                "challengeAllowsRebirth": False,
                "challengeRulesSummary": "Boss kills do not permit a rebirth in this challenge.",
                "challengeRebirthPolicy": "Rebirth disabled until challenge completion",
            }
        )
        self.assertEqual(view["rebirth"]["action"], "no-reset-challenge")
        self.assertIsNone(view["rebirth"]["targetRunAgeSeconds"])
        self.assertIsNone(view["rebirth"]["resetEtaSeconds"])
        self.assertEqual(view["challenge"]["status"], "active")
        self.assertFalse(view["challenge"]["allowsRebirth"])
        self.assertEqual(
            view["challenge"]["rebirthPolicy"],
            "Rebirth disabled until challenge completion",
        )
        self.assertIn("Boss kills", view["challenge"]["rulesSummary"])

    def test_negative_rebirth_target_alone_never_invents_a_challenge_rule(self) -> None:
        missing_policy = build_observability(
            {
                "stage": "Normal / active challenge",
                "rebirthSeconds": -1,
                "rebirthElapsed": 125,
                "rebirthExecutionEnabled": True,
            }
        )
        explicit_allowed = build_observability(
            {
                "stage": "Normal / active challenge",
                "rebirthSeconds": -1,
                "rebirthElapsed": 125,
                "rebirthExecutionEnabled": True,
                "challengeAllowsRebirth": True,
                "challengeRebirthPolicy": "Ordinary rebirth is allowed",
            }
        )
        self.assertEqual(missing_policy["rebirth"]["action"], "unknown")
        self.assertFalse(missing_policy["rebirth"]["noResetHold"])
        self.assertIsNone(missing_policy["challenge"]["allowsRebirth"])
        self.assertEqual(explicit_allowed["rebirth"]["action"], "unknown")
        self.assertFalse(explicit_allowed["rebirth"]["noResetHold"])
        self.assertTrue(explicit_allowed["challenge"]["allowsRebirth"])

    def test_future_no_rebirth_candidate_does_not_freeze_an_ordinary_run(self) -> None:
        view = build_observability(
            {
                "challengeActive": False,
                "challengeAllowsRebirth": False,
                "challengeRebirthPolicy": "A future No Rebirth candidate forbids rebirth",
                "rebirthSeconds": 600,
                "rebirthElapsed": 100,
                "rebirthExecutionEnabled": True,
            }
        )
        self.assertEqual(view["rebirth"]["action"], "reset-at-checkpoint")
        self.assertFalse(view["rebirth"]["noResetHold"])
        self.assertEqual(view["rebirth"]["resetEtaSeconds"], 500)
        self.assertEqual(view["challenge"]["status"], "none-admitted")

    def test_challenge_admission_summary_yields_clear_and_entry_etas(self) -> None:
        view = build_observability(
            {
                "rebirthSeconds": 7200,
                "rebirthElapsed": 1200,
                "rebirthExecutionEnabled": True,
                "challengeEvidenceSummary": (
                    "BASIC-2: target Boss 58, p90 900s, recovery 3600s, "
                    "native same-challenge best-time sample"
                ),
            }
        )
        challenge = view["challenge"]
        self.assertEqual(challenge["status"], "admitted")
        self.assertEqual(challenge["label"], "BASIC-2")
        self.assertEqual(challenge["entryEtaSeconds"], 6000)
        self.assertEqual(challenge["clearEtaSeconds"], 900)
        self.assertEqual(challenge["recoveryEtaSeconds"], 3600)
        self.assertEqual(challenge["targetBoss"], 58)

    def test_current_challenge_summary_parses_human_durations_and_level_target(self) -> None:
        view = build_observability(
            {
                "rebirthSeconds": 600,
                "rebirthElapsed": 100,
                "challengeEvidenceSummary": (
                    "LSC-1 [0/5 -> 1, levels 25/25, p90 18.0h, recovery 1.0h]: "
                    "first-clear conservative envelope"
                ),
            }
        )
        challenge = view["challenge"]
        self.assertEqual(challenge["label"], "LSC-1")
        self.assertEqual(challenge["clearEtaSeconds"], 64800)
        self.assertEqual(challenge["recoveryEtaSeconds"], 3600)
        self.assertEqual(challenge["targetLevel"], 25)
        self.assertIsNone(challenge["targetBoss"])

    def test_identity_and_transaction_error_are_reported_without_hash_inference(self) -> None:
        state = {
                "buildId": "build-a",
                "producerPid": 123,
                "producerSessionId": "session-a",
                "diskArtifactSha256": "disk",
                "gameAssemblySha256": "game",
                "gameEpochFingerprint": "epoch-7",
                "activeImageHashAvailable": False,
                "activeMatchesDisk": "unknown-until-reinjection-build-id-verification",
                "automationTransactionComplete": False,
                "automationTransactionError": "inventory rollback mismatch",
                "mutationRoot": {"id": 8, "state": "Aborted", "epochFingerprint": "epoch-7"},
        }
        view = build_observability(state, deployment_for(state))
        self.assertTrue(view["identity"]["verifiedEnvelope"])
        self.assertEqual(view["identity"]["joinStatus"], "Bound")
        self.assertFalse(view["identity"]["activeImageHashAvailable"])
        self.assertEqual(view["identity"]["activeMatchesDisk"], "unknown-until-reinjection-build-id-verification")
        self.assertEqual(view["transaction"]["status"], "Error")
        self.assertEqual(view["transaction"]["error"], "inventory rollback mismatch")

    def test_root_states_keep_held_pending_and_quarantined_distinct(self) -> None:
        held = build_observability(
            {"mutationRoot": {"id": 0, "state": "not-planned", "epochFingerprint": "e"},
             "gameEpochFingerprint": "e"}
        )
        pending = build_observability(
            {"mutationRoot": {"id": 9, "state": "Open", "epochFingerprint": "e",
                              "pendingSteps": 2}, "gameEpochFingerprint": "e"}
        )
        quarantined = build_observability(
            {"mutationRoot": {"id": 10, "state": "Quarantined", "epochFingerprint": "old",
                              "quarantinedSteps": 1}, "gameEpochFingerprint": "new"}
        )
        self.assertEqual(held["transaction"]["status"], "Held")
        self.assertIsNone(held["transaction"]["rootId"])
        self.assertEqual(pending["transaction"]["status"], "Pending")
        self.assertEqual(pending["transaction"]["pendingSteps"], 2)
        self.assertEqual(quarantined["transaction"]["status"], "Quarantined")
        self.assertFalse(quarantined["identity"]["rootEpochMatchesDecision"])

    def test_scheduler_preserves_exact_statistics_and_removes_unknown_sentinels(self) -> None:
        exact = build_observability({"globalScheduler": {
            "status": "Planned", "authority": "ShadowOnly", "canExecute": False,
            "snapshotHash": "save", "modelHash": "model", "objectiveHash": "objective",
            "action": "Wait", "actionId": "wait-1", "nextEvent": "TitanDue",
            "eventId": "titan-6", "meanSeconds": 90.25, "p50Seconds": 80,
            "p90Seconds": 140, "lowerBoundSeconds": 60, "upperBoundSeconds": 200,
            "gapSeconds": 30, "regretSeconds": 12, "provenance": "Empirical",
            "sampleCount": 44, "confidence": 0.875, "rolloutFallback": True,
        }})["scheduler"]
        self.assertEqual(exact["meanSeconds"], 90.25)
        self.assertEqual(exact["p50Seconds"], 80)
        self.assertEqual(exact["p90Seconds"], 140)
        self.assertEqual(exact["lowerBoundSeconds"], 60)
        self.assertEqual(exact["gapSeconds"], 30)
        self.assertEqual(exact["regretSeconds"], 12)
        self.assertEqual(exact["provenance"], "Empirical")
        self.assertEqual(exact["confidence"], 0.875)
        self.assertTrue(exact["rolloutFallback"])

        unavailable = build_observability({"globalScheduler": {
            "status": "Blocked", "meanSeconds": -1, "p50Seconds": None,
            "p90Seconds": -1, "lowerBoundSeconds": None, "gapSeconds": -1,
            "regretSeconds": -1, "provenance": "Unknown", "sampleCount": 0,
            "confidence": 0,
        }})["scheduler"]
        for key in ("meanSeconds", "p50Seconds", "p90Seconds", "lowerBoundSeconds",
                    "gapSeconds", "regretSeconds", "provenance", "sampleCount", "confidence"):
            self.assertIsNone(unavailable[key], key)

    def test_capacity_difficulty_and_end_are_explicitly_unavailable_or_held(self) -> None:
        missing = build_observability({})
        self.assertEqual(missing["capacity"]["status"], "Unavailable")
        self.assertEqual(missing["difficulty"]["status"], "Unavailable")
        self.assertEqual(missing["end"]["status"], "Unavailable")
        self.assertIsNone(missing["end"]["p90Seconds"])

        held = build_observability({
            "inventoryTotalSlots": 100, "inventoryUsedSlots": 99,
            "inventoryFreeSlots": 1, "collectionRequiredFreeReserve": 3,
            "difficulty": 1, "stagedAuthority": {"difficulty": False, "endSequence": False},
            "endgameReadyToTrigger": True, "endgameExecutionAuthorized": False,
        })
        self.assertEqual(held["capacity"]["status"], "Held")
        self.assertEqual(held["capacity"]["marginSlots"], -2)
        self.assertEqual(held["difficulty"]["current"], "Evil")
        self.assertEqual(held["difficulty"]["status"], "Held")
        self.assertEqual(held["end"]["status"], "Held")

    def test_loaded_assembly_binding_health_is_explicit_and_fail_closed(self) -> None:
        complete = build_observability({
            "nativeBindingKnownBuild": True,
            "nativeBindingsComplete": True,
            "nativeBindingDescriptorCount": 112,
            "nativeBindingBoundCount": 112,
            "nativeBindingFailureCount": 0,
            "nativeBindingFailureSummary": "",
        })["bindings"]
        self.assertEqual(complete["status"], "Complete")
        self.assertTrue(complete["knownBuild"])
        self.assertEqual(complete["boundCount"], complete["descriptorCount"])
        self.assertEqual(complete["failureCount"], 0)
        self.assertEqual(complete["provenance"], "LoadedAssemblyMetadata")

        broken = build_observability({
            "nativeBindingKnownBuild": True,
            "nativeBindingsComplete": False,
            "nativeBindingDescriptorCount": 112,
            "nativeBindingBoundCount": 111,
            "nativeBindingFailureCount": 1,
            "nativeBindingFailureSummary": "purchase.example: token mismatch",
        })["bindings"]
        self.assertEqual(broken["status"], "Quarantined")
        self.assertEqual(broken["failureCount"], 1)
        self.assertIn("token mismatch", broken["failureSummary"])

    def test_deployment_mismatch_quarantines_the_join(self) -> None:
        state = {
            "buildId": "build-a", "producerPid": 123, "producerSessionId": "session-a",
            "diskArtifactSha256": "disk", "gameAssemblySha256": "game",
            "gameEpochFingerprint": "epoch-a",
            "mutationRoot": {"id": 4, "state": "Committed", "epochFingerprint": "epoch-a"},
        }
        deployment = deployment_for(state)
        deployment["producerSessionId"] = "stale-session"
        view = build_observability(state, deployment)
        self.assertFalse(view["identity"]["verifiedEnvelope"])
        self.assertFalse(view["identity"]["deploymentDecisionMatch"])
        self.assertEqual(view["identity"]["joinStatus"], "Quarantined")


class DashboardMarkupTests(unittest.TestCase):
    def test_current_route_immediately_follows_priorities_without_redundant_next_section(self) -> None:
        html = Path("docs/index.html").read_text(encoding="utf-8")
        priorities = html.index('id="priorities"')
        now = html.index('id="now"')
        resources = html.index('id="resources"')
        self.assertLess(priorities, now)
        self.assertLess(now, resources)
        self.assertIn("What the bot is doing", html)
        self.assertNotIn("What can happen next", html)
        self.assertNotIn('id="execution"', html)
        self.assertNotIn('id="technical-diagnostics"', html)
        self.assertIn("Recent errors and blocked actions", html)
        self.assertNotIn("<details open", html)

    def test_rebirth_model_is_collapsed_but_policy_and_challenge_rules_are_visible(self) -> None:
        html = Path("docs/index.html").read_text(encoding="utf-8")
        marker = '<details class="rebirth-details">'
        self.assertIn(marker, html)
        start = html.index(marker)
        end = html.index("</details>", start)
        disclosure = html[start:end]
        self.assertIn("Why this rebirth timing?", disclosure)
        self.assertIn('id="rebirth-current"', disclosure)
        self.assertIn('id="rebirth-candidates"', disclosure)
        self.assertLess(html.index('id="rebirth-policy"'), start)
        self.assertLess(html.index('id="rebirth-next-action"'), start)
        self.assertLess(html.index('id="rebirth-reset-eta"'), start)
        self.assertGreater(html.index('id="challenge-rebirth-policy"'), end)
        self.assertGreater(html.index('id="challenge-rules"'), end)

    def test_browser_fallback_uses_explicit_challenge_policy_and_selected_boss(self) -> None:
        source = Path("docs/assets/app.js").read_text(encoding="utf-8")
        self.assertIn('challengeActive && challengeAllowsRebirth === false', source)
        self.assertNotIn('target !== null && target < 0 ? "no-reset-challenge"', source)
        self.assertIn('const selectedBoss = optionalNumber(s.bossSelectedId);', source)
        self.assertIn('setText("metric-boss", selectedBoss === null', source)
        self.assertIn('setText("metric-boss-record", highestBoss === null', source)
        self.assertNotIn('const boss = number(s.bossRecordTargetId || s.nextBoss);', source)

    def test_player_facing_overview_has_priorities_allocations_growth_and_journal(self) -> None:
        html = Path("docs/index.html").read_text(encoding="utf-8")
        source = Path("docs/assets/app.js").read_text(encoding="utf-8")
        for element_id in (
            "metric-boss-record",
            "priority-list",
            "energy-allocation-list",
            "magic-allocation-list",
            "growth-augment",
            "growth-wandoos",
            "growth-tm",
            "adventure-log-list",
            "inventory-glance-list",
            "gear-glance-summary",
            "gear-glance-list",
        ):
            self.assertEqual(html.count(f'id="{element_id}"'), 1, element_id)
        self.assertIn("function renderPriorities", source)
        self.assertIn("function renderAllocationList", source)
        self.assertIn("function renderGrowth", source)
        self.assertIn("function renderActivity", source)
        self.assertIn("envelope.adventureLog", source)
        self.assertIn("Array.isArray(s.equippedGear)", source)
        self.assertNotIn("function renderExecution", source)


class ActionErrorTests(unittest.TestCase):
    def test_adventure_journal_keeps_outcomes_and_filters_ability_spam(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "actions.log"
            path.write_text(
                "=== SESSION 2026-08-18T02:00:00Z id current build b pid 11 ===\n"
                "13:00:00.000 [COMBAT] (20s) Used Strong Attack for 900 damage\n"
                "13:00:01.000 [COMBAT] (21s) Ghost killed in 3.0s\n"
                "13:00:02.000 [ZONE] (22s) Safe Zone -> THE ITOPOD\n"
                "13:00:03.000 [INVENTORY] (23s) Applied boosts to Cheese Chestplate\n"
                "13:00:04.000 [COMBAT] (24s) Enemy defeated the player after 4.0s\n",
                encoding="utf-8",
            )
            entries = recent_adventure_log(path, session_id="current")
        self.assertEqual(len(entries), 4)
        self.assertEqual(entries[0]["tone"], "danger")
        self.assertEqual(entries[1]["tone"], "loot")
        self.assertEqual(entries[2]["tone"], "route")
        self.assertEqual(entries[3]["tone"], "victory")
        self.assertFalse(any("Strong Attack" in entry["message"] for entry in entries))

    def test_native_monitor_normalizes_windows_crlf_before_session_admission(self) -> None:
        source = (Path(__file__).parent / "ActionMonitor.swift").read_text(encoding="utf-8")
        self.assertIn('rawLine.hasSuffix("\\r")', source)
        self.assertIn('String(decoding: data, as: UTF8.self)', source)

        text = (
            "=== SESSION 2026-08-18T02:00:00Z id current build b pid 11 ===\r\n"
            "13:00:00.000 [ALLOC] (20s) current allocation\r\n"
        )
        lines, status = session_bound_lines(text, "current")
        self.assertEqual(status, "Bound")
        self.assertEqual(lines, ["13:00:00.000 [ALLOC] (20s) current allocation"])

    def test_tail_selects_only_the_exact_session_boundary(self) -> None:
        text = (
            "=== SESSION 2026-08-18T01:00:00Z id old build a pid 10 ===\n"
            "12:00:00.000 [REBIRTH] (10s) stale reset\n"
            "=== SESSION 2026-08-18T02:00:00Z id current build b pid 11 ===\n"
            "13:00:00.000 [REBIRTH] (20s) current reset\n"
            "13:00:01.000 [ERROR] (21s) current failure\n"
            "=== SESSION 2026-08-18T03:00:00Z id later build c pid 12 ===\n"
            "14:00:00.000 [REBIRTH] (30s) later reset\n"
        )
        lines, status = session_bound_lines(text, "current")
        self.assertEqual(status, "Bound")
        self.assertEqual(len(lines), 2)
        self.assertIn("current reset", lines[0])
        self.assertFalse(any("stale" in line or "later" in line for line in lines))
        self.assertEqual(session_bound_lines(text, "missing"), ([], "Unbound"))
        self.assertEqual(session_bound_lines(text, None), ([], "MissingSession"))

    def test_stale_session_events_and_errors_are_excluded(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "actions.log"
            path.write_text(
                "=== SESSION 2026-08-18T01:00:00Z id old build a pid 10 ===\n"
                "12:00:00.000 [REBIRTH] (10s) stale reset\n"
                "12:00:01.000 [ERROR] (11s) stale exception\n"
                "=== SESSION 2026-08-18T02:00:00Z id current build b pid 11 ===\n"
                "13:00:00.000 [REBIRTH] (20s) current reset\n",
                encoding="utf-8",
            )
            events = recent_key_events(path, session_id="current")
            errors = recent_action_errors(path, session_id="current")
        self.assertEqual([event["message"] for event in events], ["current reset"])
        self.assertEqual(errors, [])

    def test_bounded_tail_remains_bound_when_marker_is_before_window(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "actions.log"
            path.write_text(
                "=== SESSION 2026-08-18T02:00:00Z id current build b pid 11 ===\n"
                + "".join(
                    f"13:00:{index:02d}.000 [ALLOC] ({index}s) filler-{index:02d}\n"
                    for index in range(30)
                )
                + "13:01:00.000 [REBIRTH] (60s) current reset\n",
                encoding="utf-8",
            )
            lines, status = session_tail_lines(path, "current", limit=90)
        self.assertEqual(status, "Bound")
        self.assertTrue(any("current reset" in line for line in lines))
        self.assertFalse(any("filler-00" in line for line in lines))

    def test_repeated_failures_are_deduplicated_with_occurrence_count(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "actions.log"
            path.write_text(
                "=== SESSION 2026-08-18T02:00:00Z id current build b pid 11 ===\n"
                "12:00:00.000 [REJECTED] (10s) Loadout admission failed\n"
                "12:00:01.000 [ALLOC] (11s) Routine sweep\n"
                "12:00:02.000 [REJECTED] (12s) Loadout admission failed\n"
                "12:00:03.000 [ERROR] (13s) Controller exception\n",
                encoding="utf-8",
            )
            errors = recent_action_errors(path, session_id="current")
        self.assertEqual(len(errors), 2)
        self.assertEqual(errors[0]["category"], "ERROR")
        self.assertEqual(errors[1]["count"], 2)
        self.assertEqual(errors[1]["clock"], "12:00:02.000")
        self.assertEqual(errors[1]["severity"], "critical")


if __name__ == "__main__":
    unittest.main()
