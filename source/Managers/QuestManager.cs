using static NGUInjector.Main;

/*
FILE PURPOSE

QuestManager starts, routes, consumes drops for, and turns in Beast Quests using native state.
It distinguishes idle/active and major/minor reward policy while preserving quest-item MAXX goals.
Adventure combat policy may defer to an active quest; this manager does not choose general zones.
*/
namespace NGUInjector.Managers
{
    internal class QuestManager
    {
        private readonly Character _character;

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
                    // Butter only doubles QP, not AP. Preserve the last finite charge
                    // unless a bank cap is imminent; this keeps an option for a later
                    // higher-multiplier major while preventing bank overflow.
                    var bankNearCap = _character.beastQuest.curBankedQuests
                                      >= _character.beastQuestController.maxBankedQuests() - 1;
                    useButter = _character.arbitrary.beastButterCount > 1 || bankNearCap;
                }
                if (useButter)
                {
                    var before = _character.arbitrary.beastButterCount;
                    _character.beastQuestController.tryUseButter();
                    var confirmed = _character.beastQuest.usedButter
                                    && _character.arbitrary.beastButterCount < before;
                    Main.LogAction(confirmed ? "QUEST" : "REJECTED",
                        confirmed
                            ? "Applied Butter to " + (_character.beastQuest.reducedRewards ? "minor" : "major")
                              + " quest [confirmed stock " + before + " -> "
                              + _character.arbitrary.beastButterCount + "]"
                            : "Butter request produced no verified quest/stock transition");
                }
            }

            if (_character.beastQuestController.readyToHandIn())
            {
                Log("Turning in quest");
                _character.beastQuestController.completeQuest();
            }
        }

        internal int IsQuesting()
        {
            var autopilotQuests = Main.Autopilot != null
                                  && Main.Autopilot.CanExecuteSafe
                                  && Main.Autopilot.Config.ManageQuests;
            if (!Settings.AutoQuest && !autopilotQuests)
                return -1;

            if (!_character.beastQuest.inQuest)
                return -1;

            var questZone = _character.beastQuestController.curQuestZone();

            if (!CombatManager.IsZoneUnlocked(questZone))
                return -1;

            if (_character.beastQuest.reducedRewards)
            {
                if (Settings.ManualMinors)
                {
                    return questZone;
                }

                return -1;
            }

            return _character.beastQuestController.curQuestZone();
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
            //First logic: not in a quest
            if (!_character.beastQuest.inQuest)
            {
                //If we're allowing major quests and we have a quest available
                if (allowMajor && _character.beastQuest.curBankedQuests > 0)
                {
                    _character.settings.useMajorQuests = true;
                    SetIdleMode(false);
                    _character.beastQuestController.startQuest();
                }
                else
                {
                    _character.settings.useMajorQuests = false;
                    SetIdleMode(!Settings.ManualMinors);
                    _character.beastQuestController.startQuest();
                }

                return;
            }

            //Second logic, we're in a quest
            if (_character.beastQuest.reducedRewards)
            {
                var bankNearCap = _character.beastQuest.curBankedQuests
                                  >= _character.beastQuestController.maxBankedQuests() - 1;
                if (allowMajor && (Settings.AbandonMinors || autopilotQuests && bankNearCap)
                    && _character.beastQuest.curBankedQuests > 0)
                {
                    var progress = (_character.beastQuest.curDrops / (float) _character.beastQuest.targetDrops) * 100;
                    var abandonThreshold = autopilotQuests && bankNearCap
                        ? System.Math.Max(25, Settings.MinorAbandonThreshold)
                        : Settings.MinorAbandonThreshold;
                    if (progress <= abandonThreshold)
                    {
                        //If all this is true get rid of this minor quest and pick up a new one.
                        _character.settings.useMajorQuests = true;
                        _character.beastQuestController.skipQuest();
                        SetIdleMode(false);
                        _character.beastQuestController.startQuest();
                        //Combat logic will pick up from here
                        return;
                    }
                }
                else
                {
                    _character.settings.useMajorQuests = false;
                }

                SetIdleMode(!Settings.ManualMinors);
            }
            else
            {
                SetIdleMode(false);
            }
        }
    }
}
