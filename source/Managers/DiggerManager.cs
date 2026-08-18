/*
FILE PURPOSE

Purpose: DiggerManager owns current Digger-set transactions and one-at-a-time permanent max-level
purchase execution for progression, Titan, Yggdrasil, and Money Pit contexts.

Mechanism: Current-set changes snapshot exact levels/membership, solve under available gross GPS,
apply through native controllers, verify set/levels/slots/drain, and roll back on mismatch. Permanent
upgrades are emitted as GoldSpendBundle records. ResourceHorizonModel and the actor call the same
SelectUpgradeBundle function, and the actor buys at most one confirmed bundle before replanning.

Inputs and outputs: Inputs are live Digger levels/caps/drains, gross GPS, objective weights, locks,
the shared chronological Gold ledger, and an AutopilotPlan. Outputs are selected IDs/levels, exact
bundle action IDs, confirmed mutations, rollback evidence, and action telemetry.

Invariants and safety: Active drain never exceeds available gross GPS. Clearing is always protected
by an exact snapshot and rollback. A max-level purchase credits direct cap value only when that new
level is attainable with current GPS/slot capacity; its global total-level bonus remains valuable.
Ledger and actor cannot choose different IDs, equality at a native affordability gate is accepted,
and no loop spends again from a stale reserve or stale ROI.

Extension points and non-goals: The global scheduler may replace textual objective weights and rank
the typed bundle by seconds-to-terminal. This manager does not price Money Pit rewards, reserve an
optional competing branch, or perform multiple permanent purchases without a fresh plan.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using NGUInjector.Autopilot;

namespace NGUInjector.Managers
{
    internal static class DiggerManager
    {
        private sealed class DiggerState
        {
            internal long[] Levels;
            internal int[] ActiveIds;
        }

        private static DiggerState _savedDiggerState;
        private static DiggerState _tempDiggerState;
        private static int _cheapestDigger;
        private static readonly Dictionary<int, double> ObjectiveWeights = new Dictionary<int, double>();

        internal static LockType CurrentLock { get; set; }
        private static readonly int[] TitanDiggers = { 0, 3, 8, 11 };
        private static readonly int[] YggDiggers = {8, 11};

        internal static bool CanSwap()
        {
            return CurrentLock == LockType.None;
        }

        internal static void TryTitanSwap()
        {
            if (!CanAcquireOrHasLock(LockType.Titan))
                return;

            var ts = ZoneHelpers.TitansSpawningSoon();
            if (CurrentLock == LockType.Titan)
            {
                if (ts.SpawningSoon)
                    return;

                RestoreDiggers();
                ReleaseLock();
                return;
            }

            if (ts.SpawningSoon)
            {
                CurrentLock = LockType.Titan;
                SaveDiggers();
                if (!TryEquipDiggers(TitanDiggers, null))
                {
                    RestoreDiggers();
                    ReleaseLock();
                }
            }
        }

        internal static bool TryYggSwap()
        {
            if (!CanAcquireOrHasLock(LockType.Yggdrasil))
                return false;

            if (CurrentLock == LockType.Yggdrasil)
                return true;

            CurrentLock = LockType.Yggdrasil;
            SaveDiggers();
            if (!TryEquipDiggers(YggDiggers, null))
            {
                RestoreDiggers();
                ReleaseLock();
                return false;
            }
            var expected = YggDiggers.Where(x => x >= 0 && x < Main.Character.diggers.diggers.Count)
                .Where(x => Main.Character.diggers.diggers[x].maxLevel > 0)
                .Take(Main.Character.allDiggers.maxDiggerSlots()).ToArray();
            var verified = expected.All(x => Main.Character.diggers.diggers[x].active)
                           && Main.Character.diggers.activeDiggers.All(expected.Contains);
            if (verified) return true;
            RestoreDiggers();
            ReleaseLock();
            Main.LogAction("REJECTED", "Yggdrasil digger swap did not match the requested active set; restored prior diggers");
            return false;
        }

        internal static void ReleaseLock()
        {
            CurrentLock = LockType.None;
        }

        internal static void SaveDiggers()
        {
            _savedDiggerState = CaptureDiggerState(Main.Character);
        }

        internal static void SaveTempDiggers()
        {
            _tempDiggerState = CaptureDiggerState(Main.Character);
        }

        internal static void RestoreTempDiggers()
        {
            RestoreSnapshot(_tempDiggerState, "temporary Digger state");
        }

        internal static bool EquipDiggers(int[] diggers)
        {
            // Explicit/manual/event lists carry no global objective weights.
            // The autopilot plan uses EquipOptimizedDiggers below.
            return TryEquipDiggers(diggers, null);
        }

        internal static bool EquipOptimizedDiggers(int[] diggers)
        {
            return TryEquipDiggers(diggers, ObjectiveWeights);
        }

        private static bool TryEquipDiggers(int[] diggers, IDictionary<int, double> weights)
        {
            if (Main.Character == null || Main.Character.diggers == null
                || Main.Character.allDiggers == null || diggers == null)
                return false;
            Main.Log($"Equipping Diggers: {string.Join(",", diggers.Select(x => x.ToString()).ToArray())}");
            var sorted = diggers.Where(x => x >= 0 && x < Main.Character.diggers.diggers.Count)
                .Distinct()
                .Where(x => Main.Character.diggers.diggers[x].maxLevel > 0)
                .Take(Main.Character.allDiggers.maxDiggerSlots())
                .ToArray();
            var budget = AvailableDiggerGps(Main.Character);
            var levels = AllocateDiggerLevels(Main.Character, sorted, weights, budget, 10000);
            var applied = ApplyDiggerTransaction(Main.Character, sorted, levels);
            UpdateCheapestDigger();
            return applied;
        }

        /*
        ATOMIC DIGGER APPLY

        Clearing first is unavoidable in the native UI, so a complete snapshot is captured before
        the destructive half of the transaction. Direct target levels are the integer solution
        already checked against native drain formulas. Activation uses the native controller; exact
        active membership, levels, slot count, and total drain are postconditions. Rollback restores
        both list and per-track flags/levels even if a controller exception interrupted the apply.
        */
        private static bool ApplyDiggerTransaction(Character c, int[] selected,
            IDictionary<int, long> targetLevels)
        {
            var before = CaptureDiggerState(c);
            try
            {
                c.allDiggers.clearAllActiveDiggers();
                for (var i = 0; i < c.diggers.diggers.Count; i++)
                    c.diggers.diggers[i].curLevel = targetLevels.ContainsKey(i)
                        ? targetLevels[i] : 0L;
                foreach (var id in selected)
                    if (targetLevels.ContainsKey(id) && targetLevels[id] > 0)
                        c.allDiggers.activateDigger(id);

                var expected = selected.Where(id => targetLevels.ContainsKey(id)
                    && targetLevels[id] > 0).ToArray();
                var exactSet = c.diggers.activeDiggers.Count == expected.Length
                               && expected.All(id => c.diggers.diggers[id].active)
                               && c.diggers.activeDiggers.All(expected.Contains);
                var exactLevels = Enumerable.Range(0, c.diggers.diggers.Count)
                    .All(id => c.diggers.diggers[id].curLevel
                               == (targetLevels.ContainsKey(id) ? targetLevels[id] : 0L));
                var withinSlots = expected.Length <= c.allDiggers.maxDiggerSlots();
                var gross = Math.Max(0.0, c.grossGoldPerSecond());
                var withinDrain = c.totalGPSDrain()
                                  <= gross + Math.Max(1e-9, gross * 1e-12);
                if (exactSet && exactLevels && withinSlots && withinDrain)
                {
                    Main.LogAction("DIGGER", "Applied joint Digger set "
                        + string.Join(",", expected.Select(id => id + "@" + targetLevels[id]).ToArray())
                        + " [confirmed active set, levels, slots, and native GPS drain]");
                    c.allDiggers.refreshMenu();
                    return true;
                }
            }
            catch (Exception e)
            {
                Main.Log("Digger transaction exception: " + e.Message);
            }

            var restored = RestoreSnapshot(before, "pre-transaction Digger state");
            Main.LogAction("REJECTED", "Joint Digger apply failed exact postconditions; rollback "
                + (restored ? "was confirmed" : "could not be fully confirmed"));
            return false;
        }

        private static DiggerState CaptureDiggerState(Character c)
        {
            return new DiggerState
            {
                Levels = c.diggers.diggers.Select(x => x.curLevel).ToArray(),
                ActiveIds = c.diggers.activeDiggers.ToArray()
            };
        }

        private static bool RestoreSnapshot(DiggerState snapshot, string label)
        {
            var c = Main.Character;
            if (snapshot == null || c == null || c.diggers == null || c.allDiggers == null
                || snapshot.Levels == null || snapshot.Levels.Length != c.diggers.diggers.Count)
                return false;
            try
            {
                c.allDiggers.clearAllActiveDiggers();
                for (var i = 0; i < snapshot.Levels.Length; i++)
                {
                    c.diggers.diggers[i].curLevel = snapshot.Levels[i];
                    c.diggers.diggers[i].active = false;
                }
                c.diggers.activeDiggers.Clear();
                foreach (var id in snapshot.ActiveIds)
                {
                    if (id < 0 || id >= c.diggers.diggers.Count) continue;
                    c.diggers.diggers[id].active = true;
                    c.diggers.activeDiggers.Add(id);
                }
                var exact = snapshot.ActiveIds.SequenceEqual(c.diggers.activeDiggers)
                            && Enumerable.Range(0, snapshot.Levels.Length)
                                .All(i => c.diggers.diggers[i].curLevel == snapshot.Levels[i]
                                          && c.diggers.diggers[i].active == snapshot.ActiveIds.Contains(i));
                c.allDiggers.refreshMenu();
                if (!exact) Main.LogAction("REJECTED", "Rollback mismatch for " + label);
                return exact;
            }
            catch (Exception e)
            {
                Main.LogAction("REJECTED", "Rollback exception for " + label + ": " + e.Message);
                return false;
            }
        }

        /*
        OBJECTIVE-AWARE DIGGER SELECTION

        A slot is a scarce multiplicative resource and Gold drain is shared. Rank every unlocked
        digger by its native attainable log multiplier at an equal-share feasibility point, scaled
        by the active plan's bottleneck. The final equip pass then divides the Gold budget by those
        same shadow weights instead of giving a low-value digger the same drain as the bottleneck.
        */
        internal static int[] OptimizeForPlan(AutopilotPlan plan)
        {
            var c = Main.Character;
            if (c == null || plan == null || c.diggers == null || c.allDiggers == null)
                return plan == null ? new int[0] : plan.Diggers;
            var slots = Math.Max(0, Math.Min(c.allDiggers.maxDiggerSlots(), c.diggers.diggers.Count));
            if (slots == 0) return new int[0];
            ObjectiveWeights.Clear();
            for (var id = 0; id < c.diggers.diggers.Count; id++)
                ObjectiveWeights[id] = DiggerObjectiveWeight(c, plan, id);

            var candidates = Enumerable.Range(0, c.diggers.diggers.Count)
                .Where(id => c.diggers.diggers[id].maxLevel > 0 && ObjectiveWeights[id] > 0.0)
                .Take(30).ToArray();
            var gross = AvailableDiggerGps(c);
            var bestScore = double.NegativeInfinity;
            var best = new int[0];
            var masks = 1 << candidates.Length;
            for (var mask = 1; mask < masks; mask++)
            {
                if (BitCount(mask) > slots) continue;
                var selected = candidates.Where((id, bit) => (mask & 1 << bit) != 0).ToArray();
                var levels = AllocateDiggerLevels(c, selected, ObjectiveWeights, gross, 256);
                var score = selected.Sum(id =>
                {
                    return ObjectiveWeights[id] * Math.Log(Math.Max(1.0,
                        ProjectedDiggerBonus(c, id, levels[id])));
                });
                if (score <= bestScore) continue;
                bestScore = score;
                best = selected;
            }
            return best;
        }

        private static Dictionary<int, long> AllocateDiggerLevels(Character c, int[] selected,
            IDictionary<int, double> weights, double gross, int maxRedistributionSteps)
        {
            var result = new Dictionary<int, long>();
            if (selected == null || selected.Length == 0) return result;
            var weightTotal = selected.Sum(id => weights != null && weights.ContainsKey(id)
                ? Math.Max(0.1, weights[id]) : 1.0);
            foreach (var id in selected)
            {
                var weight = weights != null && weights.ContainsKey(id)
                    ? Math.Max(0.1, weights[id]) : 1.0;
                result[id] = c.allDiggers.consumesGPS[id]
                    ? AffordableLevel(c, id, gross * weight / Math.Max(1e-9, weightTotal))
                    : c.diggers.diggers[id].maxLevel;
            }

            // A capped/cheap digger often uses only a fraction of its nominal share.
            // Reinvest the exact remaining native drain one integer level at a time,
            // choosing the largest weighted logarithmic gain per marginal GPS.
            for (var step = 0; step < maxRedistributionSteps; step++)
            {
                var used = selected.Sum(id => DiggerDrain(c, id, result[id]));
                var remaining = Math.Max(0.0, gross - used);
                var best = -1;
                var bestRoi = double.NegativeInfinity;
                foreach (var id in selected)
                {
                    var level = result[id];
                    if (level >= c.diggers.diggers[id].maxLevel) continue;
                    var increment = DiggerDrain(c, id, level + 1) - DiggerDrain(c, id, level);
                    if (increment > remaining + Math.Max(1e-9, gross * 1e-12)) continue;
                    var weight = weights != null && weights.ContainsKey(id)
                        ? Math.Max(0.1, weights[id]) : 1.0;
                    var gain = weight * Math.Log(ProjectedDiggerBonus(c, id, level + 1)
                                                 / Math.Max(1.0, ProjectedDiggerBonus(c, id, level)));
                    var roi = increment <= 0.0 ? double.PositiveInfinity : gain / increment;
                    if (roi <= bestRoi) continue;
                    bestRoi = roi;
                    best = id;
                }
                if (best < 0) break;
                result[best]++;
            }
            return result;
        }

        private static double DiggerDrain(Character c, int id, long level)
        {
            if (level <= 0 || !c.allDiggers.consumesGPS[id]) return 0.0;
            return DiggerLevelCap(c, id, level);
        }

        private static double AvailableDiggerGps(Character c)
        {
            var gross = Math.Max(0.0, c.grossGoldPerSecond());
            var currentDiggerDrain = Math.Max(0.0, c.allDiggers.totalGPSDrain());
            var otherDrain = Math.Max(0.0, c.totalGPSDrain() - currentDiggerDrain);
            return Math.Max(0.0, gross - otherDrain);
        }

        private static double DiggerLevelCap(Character c, int id, long level)
        {
            if (level <= 0) return 0.0;
            return c.allDiggers.baseGPSDrain[id]
                   * Math.Pow(c.allDiggers.gpsGrowthRate[id], level - 1.0);
        }

        private static int BitCount(int value)
        {
            var count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
            return count;
        }

        private static double DiggerObjectiveWeight(Character c, AutopilotPlan plan, int id)
        {
            var objective = (plan.Stage + " " + plan.Objective).ToLowerInvariant();
            var itopod = c.adventure.zone == 1000 || objective.Contains("itopod");
            switch (id)
            {
                case 0: return itopod ? 0.0 : objective.Contains("collection") ? 8.0 : 3.0;
                case 1: return c.settings.wandoos98On ? (objective.Contains("challenge") ? 7.0 : 2.0) : 0.0;
                // Stat Digger affects Fight Boss Attack/Defense, not Adventure/ITOPOD stats.
                case 2: return itopod ? 0.0
                    : objective.Contains("challenge") || objective.Contains("boss") ? 9.0 : 3.0;
                case 3: return itopod ? 5.0 : objective.Contains("adventure") || objective.Contains("titan") ? 8.0 : 5.0;
                case 4: return c.settings.nguOn ? (objective.Contains("ngu") ? 8.0 : 4.0) : 0.0;
                case 5: return c.settings.nguOn ? (objective.Contains("ngu") ? 7.0 : 3.5) : 0.0;
                case 6: return c.settings.beardsOn ? 3.0 : 0.0;
                case 7: return c.settings.beardsOn ? 2.5 : 0.0;
                case 8: return itopod ? 10.0 : c.adventure.titan4Kills > 0 ? 4.5 : 0.0;
                case 9: return c.inventoryController.daycareSpaces() > 0 ? 1.5 : 0.0;
                case 10: return c.buttons.bloodMagic.interactable ? (objective.Contains("blood") ? 7.0 : 2.0) : 0.0;
                case 11: return objective.Contains("early") ? 7.0 : 3.5;
                default: return 0.0;
            }
        }

        private static long AffordableLevel(Character c, int id, double budget)
        {
            var digger = c.diggers.diggers[id];
            var baseDrain = c.allDiggers.baseGPSDrain[id];
            var growth = c.allDiggers.gpsGrowthRate[id];
            if (budget < baseDrain || baseDrain <= 0.0) return 0;
            if (growth <= 1.0) return digger.maxLevel;
            return Math.Max(0L, Math.Min(digger.maxLevel,
                (long)Math.Floor(Math.Log(budget / baseDrain) / Math.Log(growth)) + 1L));
        }

        private static double ProjectedDiggerBonus(Character c, int id, long level)
        {
            if (level <= 0) return 1.0;
            var start = c.allDiggers.startingBoost[id];
            var perLevel = c.allDiggers.boostPerLevel[id];
            var baseBonus = id == 2
                ? Math.Max(1.0, 1.0 + start + Math.Pow(level, 3.0) * perLevel)
                : Math.Max(1.0, 1.0 + start + level * perLevel);
            return Math.Max(1.0, baseBonus * c.allDiggers.totalLevelBonus());
        }

        private static bool CanAcquireOrHasLock(LockType requestor)
        {
            if (CurrentLock == requestor)
            {
                return true;
            }

            if (CurrentLock == LockType.None)
            {
                return true;
            }

            return false;
        }

        internal static void RecapDiggers()
        {
            if (CurrentLock != LockType.None)
                return;
            var gross = AvailableDiggerGps(Main.Character);
            var active = Enumerable.Range(0, Main.Character.diggers.diggers.Count)
                .Where(x => Main.Character.diggers.diggers[x].active).ToArray();
            if (active.Length > 0)
            {
                var levels = AllocateDiggerLevels(Main.Character, active, ObjectiveWeights, gross, 10000);
                ApplyDiggerTransaction(Main.Character, active, levels);
            }
            UpgradeCheapestDigger();
            Main.Character.allDiggers.refreshMenu();
        }

        internal static void RestoreDiggers()
        {
            RestoreSnapshot(_savedDiggerState, "saved Digger state");
        }

        internal static void UpdateCheapestDigger()
        {
            if (!Main.Settings.UpgradeDiggers && !Main.AutopilotWants(x => x.ManageDiggers)) return;
            var bundle = SelectUpgradeBundle(Main.Character, double.MaxValue);
            _cheapestDigger = bundle == null ? -1 : bundle.ActorId;
        }

        /*
        PERMANENT UPGRADE EVENT BUNDLE

        Purchase selection and execution must share one immutable action ID. A cap increase has an
        immediate direct term only if the extra level can fit current non-Digger drain, Digger GPS,
        slots, and the new native cap. The total-max-level multiplier applies independently to every
        active valued Digger, so it remains in the bundle even when direct cap use is impossible.
        */
        internal static GoldSpendBundle SelectUpgradeBundle(Character c, double reachableGold)
        {
            if (c == null || c.allDiggers == null || c.diggers == null
                || c.diggers.diggers == null)
                return null;
            var candidates = new List<GoldSpendBundle>();
            for (var id = 0; id < c.diggers.diggers.Count; id++)
            {
                if (c.diggers.diggers[id].maxLevel >= c.allDiggers.hardCapLevel(id)) continue;
                var cost = c.allDiggers.upgradeCost(id);
                if (cost <= 0.0 || double.IsNaN(cost) || double.IsInfinity(cost)) continue;
                double value;
                bool directAttainable;
                DiggerUpgradeValue(c, id, out value, out directAttainable);
                candidates.Add(new GoldSpendBundle
                {
                    Kind = GoldClaimKind.DiggerPermanentUpgrade,
                    ActionId = "digger-max-" + id,
                    ActorId = id,
                    Label = "Digger " + id + " max-level upgrade"
                            + (directAttainable ? " (new cap attainable)"
                                : " (global max-level bonus only at current GPS)"),
                    AtSeconds = 0.0,
                    RequiredLiquidity = cost,
                    Debit = cost,
                    ValueScore = cost <= 0.0 ? double.NegativeInfinity : value / cost
                });
            }
            return GoldEventLedger.SelectBestBundle(candidates, reachableGold);
        }

        private static void DiggerUpgradeValue(Character c, int id, out double value,
            out bool directAttainable)
        {
            var currentTotal = Math.Max(1e-9, c.allDiggers.totalLevelBonus());
            var sum = c.diggers.diggers.Sum(x => x.maxLevel);
            var nextTotal = ProjectedTotalLevelBonus(c, sum + 1);
            var globalWeight = Enumerable.Range(0, c.diggers.diggers.Count)
                .Where(x => c.diggers.diggers[x].active)
                .Sum(x => ObjectiveWeights.ContainsKey(x) ? Math.Max(0.0, ObjectiveWeights[x]) : 1.0);
            var globalValue = globalWeight * Math.Log(nextTotal / currentTotal);
            var weight = ObjectiveWeights.ContainsKey(id) ? Math.Max(0.0, ObjectiveWeights[id]) : 1.0;
            var level = c.diggers.diggers[id].maxLevel;
            var hasSlot = c.diggers.diggers[id].active
                          || c.diggers.activeDiggers.Count < c.allDiggers.maxDiggerSlots();
            var newLevelDrain = DiggerDrain(c, id, level + 1L);
            var availableGps = AvailableDiggerGps(c);
            directAttainable = hasSlot && (!c.allDiggers.consumesGPS[id]
                || newLevelDrain <= availableGps + Math.Max(1e-9, availableGps * 1e-12));
            var start = c.allDiggers.startingBoost[id];
            var per = c.allDiggers.boostPerLevel[id];
            var before = id == 2 ? 1.0 + start + Math.Pow(level, 3.0) * per
                : 1.0 + start + level * per;
            var after = id == 2 ? 1.0 + start + Math.Pow(level + 1.0, 3.0) * per
                : 1.0 + start + (level + 1.0) * per;
            var directValue = directAttainable
                ? weight * Math.Log(Math.Max(1.0, after) / Math.Max(1.0, before)) : 0.0;
            value = globalValue + directValue;
        }

        private static double ProjectedTotalLevelBonus(Character c, long sum)
        {
            var result = sum <= 500
                ? 1.0 + sum * 0.0005
                : 1.25 + Math.Pow(sum - 500.0, 0.7) * 0.0005;
            if (c.challenges.timeMachineChallenge.curCompletions >= 1) result += 0.05;
            if (c.inventory.itemList.partyComplete) result += 0.05;
            return result;
        }

        internal static void UpgradeCheapestDigger()
        {
            if (CurrentLock != LockType.None) return;
            if (!Main.Settings.UpgradeDiggers && !Main.AutopilotWants(x => x.ManageDiggers)) return;
            MutationLease purchaseLease;
            string purchaseHold;
            var purchaseOwner = ExecutionSafety.OwnerFor(MutationClass.Diggers);
            if (!ExecutionSafety.TryAcquire(MutationClass.Diggers, MutationRisk.Irreversible,
                    purchaseOwner, out purchaseLease, out purchaseHold)
                || !purchaseLease.IsCurrent)
            {
                ExecutionSafety.ReportHold("digger-permanent-upgrade",
                    "Digger max-level purchase held: "
                    + (string.IsNullOrEmpty(purchaseHold)
                        ? "execution lease became stale" : purchaseHold));
                return;
            }
            var reserve = Main.Settings.MoneyPitThreshold;
            GoldSpendBundle bundle = null;
            if (Main.AutopilotWants(x => x.ManageDiggers))
            {
                var plan = Main.Autopilot == null ? null : Main.Autopilot.Plan;
                var remaining = plan != null && !plan.RebirthExecutionHold
                    ? (int)Math.Max(0.0, Math.Ceiling(plan.EffectiveAllocationTarget(Main.Character)
                                                     - Main.Character.rebirthTime.totalseconds))
                    : 0;
                var ledger = ResourceHorizonModel.EvaluateGold(Main.Character, remaining);
                reserve = ledger.ProtectedSpendBefore(GoldClaimKind.DiggerPermanentUpgrade);
                bundle = ledger.DiggerBundle;
            }
            if (bundle == null)
                bundle = SelectUpgradeBundle(Main.Character,
                    Math.Max(0.0, Main.Character.realGold - reserve));
            if (bundle == null || bundle.ActorId < 0) return;
            _cheapestDigger = bundle.ActorId;
            var cost = Main.Character.allDiggers.upgradeCost(_cheapestDigger);
            var tolerance = Math.Max(1e-9, Main.Character.realGold * 1e-12);
            if (cost <= 0.0 || Math.Abs(cost - bundle.Debit) > Math.Max(1e-9, cost * 1e-9)
                || Main.Character.realGold + tolerance < bundle.RequiredLiquidity + reserve)
                return;
            var levelBefore = Main.Character.diggers.diggers[_cheapestDigger].maxLevel;
            var goldBefore = Main.Character.realGold;
            Main.Character.allDiggers.upgradeMaxLevel(_cheapestDigger);
            var goldDebit = goldBefore - Main.Character.realGold;
            var confirmed = Main.Character.diggers.diggers[_cheapestDigger].maxLevel
                            == levelBefore + 1L
                            && goldDebit > 0.0
                            && Math.Abs(goldDebit - cost) <= Math.Max(1e-6, cost * 1e-6);
            Main.LogAction(confirmed ? "PURCHASE" : "REJECTED",
                confirmed
                    ? "Executed " + bundle.ActionId + ": upgraded "
                      + GameNames.Digger(Main.Character, _cheapestDigger)
                      + " max level [confirmed exact +1 level and Gold debit; replan required]"
                    : bundle.ActionId + " produced no exact level/Gold transition");
        }

    }
}
