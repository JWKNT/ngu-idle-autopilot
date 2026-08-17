/*
FILE PURPOSE

GameNames is the single display-name boundary for bot logs and monitor telemetry. Whenever NGU
Idle exposes a serialized/runtime name, this class reads that native value instead of maintaining
a second, drift-prone bot vocabulary. The two Basic Training arrays are the only static tables:
their exact ordering is derived from the native training arrays and their next-unlock switches.
The nextAttackName/nextDefenseName widgets name the move earned by completing the current slot;
they do not name the current slot. Keeping current-training and next-unlock tables separate avoids
the one-place rotation where Ultimate Attack was reported as Idle Attack (and Ultimate Buff as
Block). Fallbacks are deliberately generic and retain an index/ID.
*/
namespace NGUInjector.Managers
{
    internal static class GameNames
    {
        private static readonly string[] AttackTrainingNames =
        {
            "Idle Attack", "Regular Attack", "Strong Attack", "Parry", "Piercing Attack", "Ultimate Attack"
        };

        private static readonly string[] DefenseTrainingNames =
        {
            "Block", "Defensive Buff", "Heal", "Offensive Buff", "Charge", "Ultimate Buff"
        };

        private static readonly string[] AttackUnlockNames =
        {
            "Regular Attack", "Strong Attack", "Parry", "Piercing Attack", "Ultimate Attack"
        };

        private static readonly string[] DefenseUnlockNames =
        {
            "Defensive Buff", "Heal", "Offensive Buff", "Charge", "Ultimate Buff"
        };

        internal static string AttackTraining(int index)
        {
            return index >= 0 && index < AttackTrainingNames.Length
                ? AttackTrainingNames[index] : "Attack Training " + (index + 1);
        }

        internal static string AttackTraining(Character c, int index)
        {
            return AttackTraining(index);
        }

        internal static string DefenseTraining(int index)
        {
            return index >= 0 && index < DefenseTrainingNames.Length
                ? DefenseTrainingNames[index] : "Defense Training " + (index + 1);
        }

        internal static string DefenseTraining(Character c, int index)
        {
            return DefenseTraining(index);
        }

        internal static string AttackUnlock(int trainingIndex)
        {
            return trainingIndex >= 0 && trainingIndex < AttackUnlockNames.Length
                ? AttackUnlockNames[trainingIndex] : "Attack ability " + (trainingIndex + 1);
        }

        internal static string DefenseUnlock(int trainingIndex)
        {
            return trainingIndex >= 0 && trainingIndex < DefenseUnlockNames.Length
                ? DefenseUnlockNames[trainingIndex] : "Defense ability " + (trainingIndex + 1);
        }

        internal static string Item(Character c, int id)
        {
            try
            {
                if (c != null && c.itemInfo != null && c.itemInfo.itemName != null
                    && id >= 0 && id < c.itemInfo.itemName.Length)
                    return Clean(c.itemInfo.itemName[id], "Item " + id);
            }
            catch { }
            return "Item " + id;
        }

        internal static string Zone(Character c, int zone)
        {
            if (zone < 0) return "Safe Zone";
            if (zone == 1000) return "THE ITOPOD";
            try
            {
                if (c != null && c.adventureController != null)
                    return Clean(c.adventureController.zoneName(zone), "Adventure Zone " + zone);
            }
            catch { }
            return "Adventure Zone " + zone;
        }

        internal static string Titan(Character c, int titanIndex)
        {
            if (titanIndex >= 0 && titanIndex < ZoneHelpers.TitanZones.Length)
                return Zone(c, ZoneHelpers.TitanZones[titanIndex]) + " (Titan " + (titanIndex + 1) + ")";
            return "Titan " + (titanIndex + 1);
        }

        internal static string Augment(Character c, int index, bool upgrade)
        {
            try
            {
                if (c != null && c.augmentsController != null && c.augmentsController.augments != null
                    && index >= 0 && index < c.augmentsController.augments.Length)
                {
                    AugmentController controller = null;
                    foreach (var candidate in c.augmentsController.augments)
                    {
                        if (candidate != null && candidate.id == index)
                        {
                            controller = candidate;
                            break;
                        }
                    }
                    if (controller == null && index < c.augmentsController.augments.Length)
                        controller = c.augmentsController.augments[index];
                    if (controller != null)
                        return Clean(upgrade ? controller.upgradeName : controller.augName,
                            (upgrade ? "Augment Upgrade " : "Augment ") + (index + 1));
                }
            }
            catch { }
            return (upgrade ? "Augment Upgrade " : "Augment ") + (index + 1);
        }

        internal static string Beard(Character c, int id)
        {
            try
            {
                var names = c.allBeards.beard.beardNames;
                if (names != null && id >= 0 && id < names.Length)
                    return Clean(names[id], "Beard " + (id + 1));
            }
            catch { }
            return "Beard " + (id + 1);
        }

        internal static string Digger(Character c, int id)
        {
            try
            {
                var names = c.allDiggers.diggerName;
                if (names != null && id >= 0 && id < names.Count)
                    return Clean(names[id], "Gold Digger " + (id + 1));
            }
            catch { }
            return "Gold Digger " + (id + 1);
        }

        internal static string Fruit(Character c, int id)
        {
            try
            {
                var names = c.yggdrasilController.fruitName;
                if (names != null && id >= 0 && id < names.Count)
                    return Clean(names[id], "Yggdrasil Fruit " + (id + 1));
            }
            catch { }
            return "Yggdrasil Fruit " + (id + 1);
        }

        internal static string Wish(Character c, int id)
        {
            try
            {
                var properties = c.wishesController.properties;
                if (properties != null && id >= 0 && id < properties.Count)
                    return Clean(properties[id].wishName, "Wish " + (id + 1));
            }
            catch { }
            return "Wish " + (id + 1);
        }

        private static string Clean(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length == 0 ? fallback : clean;
        }
    }
}
