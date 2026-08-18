using System;
using static NGUInjector.Main;

/*
FILE PURPOSE

QuestManager starts, routes, skips, and turns in Beast Quests using native controllers. Every
start/skip/complete request is treated as a transaction: it snapshots quest, bank, QP, AP, and
Butter state, invokes one native action, then emits QUEST only after exact postconditions are
visible. Missing transitions emit REJECTED and are never followed by a dependent mutation.
InventoryManager separately protects/offers the exact active quest item; Adventure combat policy
may defer to the verified active quest zone.
*/
namespace NGUInjector.Managers
{
    internal class QuestManager
    {
        private readonly Character _character;

        private sealed class QuestSnapshot
        {
            internal bool InQuest;
            internal int QuestId;
            internal int Banked;
            internal bool Reduced;
            internal long QuirkPoints;
            internal long ArbitraryPoints;
            internal int Butter;
        }

        public QuestManager()
        {
            _character = Main.Character;
        }

        internal void CheckQuestTurnin()
        {
            var autopilotQuests = Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                                  && Main.Autopilot.Config.ManageQuests;
            if (_character.beastQuest.inQuest && _character.beastQuest.targetDrops > 0
                && _character.beastQuest.curDrops >= _character.beastQuest.targetDrops - 2
                && !_character.beastQuest.usedButter && _character.arbitrary.beastButterCount > 0)
            {
                var useButter = _character.beastQuest.reducedRewards
                    ? Settings.UseButterMinor
                    : Settings.UseButterMajor;
                if (!_character.beastQuest.reducedRewards && autopilotQuests)
                {
                    // Butter doubles QP but not AP. Preserve the final charge unless the
                    // major-bank cap makes losing another quest more expensive than the option.
                    var bankNearCap = _character.beastQuest.curBankedQuests
                                      >= _character.beastQuestController.maxBankedQuests() - 1;
                    useButter = _character.arbitrary.beastButterCount > 1 || bankNearCap;
                }
                if (useButter)
                {
                    var before = Snapshot();
                    _character.beastQuestController.tryUseButter();
                    var confirmed = _character.beastQuest.inQuest
                                    && _character.beastQuest.questID == before.QuestId
                                    && _character.beastQuest.usedButter
                                    && _character.arbitrary.beastButterCount == before.Butter - 1;
                    Main.LogAction(confirmed ? "QUEST" : "REJECTED",
                        confirmed
                            ? "Applied Butter to " + (before.Reduced ? "minor" : "major")
                              + " quest [confirmed by quest identity and exact stock debit]"
                            : "Butter request produced no exact quest/stock transition");
                }
            }

            if (!_character.beastQuestController.readyToHandIn()) return;
            var turnin = Snapshot();
            _character.beastQuestController.completeQuest();
            var cleared = !_character.beastQuest.inQuest && _character.beastQuest.questID == 0;
            var rewarded = _character.beastQuest.quirkPoints > turnin.QuirkPoints
                           || _character.arbitrary.curArbitraryPoints > turnin.ArbitraryPoints;
            Main.LogAction(cleared && rewarded ? "QUEST" : "REJECTED",
                cleared && rewarded
                    ? "Turned in " + (turnin.Reduced ? "minor" : "major") + " quest item "
                      + turnin.QuestId + " [confirmed by cleared identity and QP/AP credit]"
                    : "Quest turn-in request lacked a verified clear plus QP/AP reward transition");
        }

        internal int IsQuesting()
        {
            var autopilotQuests = Main.Autopilot != null
                                  && Main.Autopilot.CanExecuteSafe
                                  && Main.Autopilot.Config.ManageQuests;
            if (!Settings.AutoQuest && !autopilotQuests || !_character.beastQuest.inQuest)
                return -1;
            var questZone = _character.beastQuestController.curQuestZone();
            if (!CombatManager.IsZoneUnlocked(questZone)) return -1;
            if (_character.beastQuest.reducedRewards && !Settings.ManualMinors) return -1;
            return questZone;
        }

        private void SetIdleMode(bool idle)
        {
            _character.beastQuest.idleMode = idle;
            _character.beastQuestController.updateButtons();
            _character.beastQuestController.updateButtonText();
        }

        internal void ManageQuests()
        {
            var autopilotQuests = Main.Autopilot != null && Main.Autopilot.CanExecuteSafe
                                  && Main.Autopilot.Config.ManageQuests;
            var allowMajor = Settings.AllowMajorQuests || autopilotQuests;
            if (!_character.beastQuest.inQuest)
            {
                var major = allowMajor && _character.beastQuest.curBankedQuests > 0;
                StartQuestVerified(major, !major && !Settings.ManualMinors);
                return;
            }

            if (!_character.beastQuest.reducedRewards)
            {
                SetIdleMode(false);
                return;
            }

            var bankNearCap = _character.beastQuest.curBankedQuests
                              >= _character.beastQuestController.maxBankedQuests() - 1;
            if (allowMajor && (Settings.AbandonMinors || autopilotQuests && bankNearCap)
                && _character.beastQuest.curBankedQuests > 0)
            {
                var progress = _character.beastQuest.targetDrops <= 0 ? 100f
                    : _character.beastQuest.curDrops / (float)_character.beastQuest.targetDrops * 100f;
                var abandonThreshold = autopilotQuests && bankNearCap
                    ? Math.Max(25, Settings.MinorAbandonThreshold)
                    : Settings.MinorAbandonThreshold;
                if (progress <= abandonThreshold)
                {
                    var before = Snapshot();
                    _character.beastQuestController.skipQuest();
                    var skipped = !_character.beastQuest.inQuest && _character.beastQuest.questID == 0
                                  && _character.beastQuest.curBankedQuests == before.Banked;
                    Main.LogAction(skipped ? "QUEST" : "REJECTED", skipped
                        ? "Skipped minor quest item " + before.QuestId
                          + " [confirmed clear; bank unchanged]"
                        : "Minor-quest skip request produced no exact clear transition; replacement was not started");
                    if (skipped) StartQuestVerified(true, false);
                    return;
                }
            }

            _character.settings.useMajorQuests = false;
            SetIdleMode(!Settings.ManualMinors);
        }

        private bool StartQuestVerified(bool requestMajor, bool idle)
        {
            var before = Snapshot();
            _character.settings.useMajorQuests = requestMajor;
            SetIdleMode(idle);
            _character.beastQuestController.startQuest();
            var started = !before.InQuest && _character.beastQuest.inQuest
                          && _character.beastQuest.questID >= 278
                          && _character.beastQuest.questID <= 287
                          && _character.beastQuest.targetDrops > 0
                          && _character.beastQuest.curDrops == 0;
            var exactKind = requestMajor
                ? !_character.beastQuest.reducedRewards
                  && _character.beastQuest.curBankedQuests == before.Banked - 1
                : _character.beastQuest.reducedRewards
                  && _character.beastQuest.curBankedQuests == before.Banked;
            var confirmed = started && exactKind;
            Main.LogAction(confirmed ? "QUEST" : "REJECTED", confirmed
                ? "Started " + (requestMajor ? "major" : "minor") + " quest item "
                  + _character.beastQuest.questID + " for " + _character.beastQuest.targetDrops
                  + " drops [confirmed identity, type, progress, and bank delta]"
                : "Quest start request produced no exact " + (requestMajor ? "major" : "minor")
                  + " state transition");
            return confirmed;
        }

        private QuestSnapshot Snapshot()
        {
            return new QuestSnapshot
            {
                InQuest = _character.beastQuest.inQuest,
                QuestId = _character.beastQuest.questID,
                Banked = _character.beastQuest.curBankedQuests,
                Reduced = _character.beastQuest.reducedRewards,
                QuirkPoints = _character.beastQuest.quirkPoints,
                ArbitraryPoints = _character.arbitrary.curArbitraryPoints,
                Butter = _character.arbitrary.beastButterCount
            };
        }
    }
}
