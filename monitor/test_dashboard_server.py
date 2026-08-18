"""
FILE PURPOSE

These unit tests pin the dashboard's consequential-event definition and normalized observability
contract. Routine catch-up traffic and reset-local milestones must stay out of the public ledger,
while records, permanent progression, irreversible safety failures, and resets remain visible.
Missing telemetry must remain unknown rather than turning into a false zero-second ETA, challenge
admission, reset schedule, or deployment verification claim.
"""

import tempfile
import unittest
from pathlib import Path

from monitor.dashboard_server import (
    build_observability,
    event_importance,
    is_key_event,
    recent_action_errors,
)


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
            }
        )
        self.assertEqual(view["rebirth"]["action"], "no-reset-challenge")
        self.assertIsNone(view["rebirth"]["targetRunAgeSeconds"])
        self.assertIsNone(view["rebirth"]["resetEtaSeconds"])
        self.assertEqual(view["challenge"]["status"], "active")

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
        view = build_observability(
            {
                "buildId": "build-a",
                "producerPid": 123,
                "producerSessionId": "session-a",
                "diskArtifactSha256": "disk",
                "activeImageHashAvailable": False,
                "activeMatchesDisk": "unknown-until-reinjection-build-id-verification",
                "automationTransactionComplete": False,
                "automationTransactionError": "inventory rollback mismatch",
            }
        )
        self.assertTrue(view["identity"]["verifiedEnvelope"])
        self.assertFalse(view["identity"]["activeImageHashAvailable"])
        self.assertEqual(view["identity"]["activeMatchesDisk"], "unknown-until-reinjection-build-id-verification")
        self.assertEqual(view["transaction"]["status"], "error")
        self.assertEqual(view["transaction"]["error"], "inventory rollback mismatch")


class ActionErrorTests(unittest.TestCase):
    def test_repeated_failures_are_deduplicated_with_occurrence_count(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "actions.log"
            path.write_text(
                "12:00:00.000 [REJECTED] (10s) Loadout admission failed\n"
                "12:00:01.000 [ALLOC] (11s) Routine sweep\n"
                "12:00:02.000 [REJECTED] (12s) Loadout admission failed\n"
                "12:00:03.000 [ERROR] (13s) Controller exception\n",
                encoding="utf-8",
            )
            errors = recent_action_errors(path)
        self.assertEqual(len(errors), 2)
        self.assertEqual(errors[0]["category"], "ERROR")
        self.assertEqual(errors[1]["count"], 2)
        self.assertEqual(errors[1]["clock"], "12:00:02.000")
        self.assertEqual(errors[1]["severity"], "critical")


if __name__ == "__main__":
    unittest.main()
