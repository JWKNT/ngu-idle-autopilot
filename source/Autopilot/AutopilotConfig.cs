using System;
using System.IO;
using UnityEngine;

namespace NGUInjector.Autopilot
{
    [Serializable]
    internal class AutopilotConfig
    {
        public bool Enabled = true;
        public string Mode = "dry-run";
        public string Goal = "progression";
        public bool AutoEnterGame = true;

        public bool ManageAllocations = true;
        public bool ManageBosses = true;
        public bool ManageAdventure = true;
        public bool ManageInventory = true;
        public bool ManageDiggers = true;
        public bool ManageYggdrasil = true;
        public bool ManageQuests = true;
        public bool ManageWishes = true;
        public bool ManageCards = true;
        public bool ManageCooking = true;
        public bool ManageMoneyPit = true;
        public bool ManageDailySpin = true;
        public bool ManageBloodMagic = true;
        public bool ManageBeards = true;

        public bool AllowExpSpending = false;
        public bool AllowApSpending = false;
        public bool AllowRebirths = false;
        public bool AllowChallenges = false;
        public bool AllowCardYeeting = false;
        public bool AllowPerkSpending = false;
        public bool AllowQuirkSpending = false;

        public long ExpReserve = 0;
        public long ApReserve = 0;
        public long PPReserve = 0;
        public long QPReserve = 0;
        public double MoneyPitReserve = 100000.0;
        public int DecisionIntervalSeconds = 1;

        public static AutopilotConfig LoadOrCreate(string path)
        {
            if (File.Exists(path))
            {
                var loaded = JsonUtility.FromJson<AutopilotConfig>(File.ReadAllText(path));
                if (loaded != null)
                    return loaded;
            }

            var config = new AutopilotConfig();
            config.Save(path);
            return config;
        }

        public void Save(string path)
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }

        public bool IsDryRun
        {
            get { return !string.Equals(Mode, "assist", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(Mode, "full", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsAssist
        {
            get { return string.Equals(Mode, "assist", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsFull
        {
            get { return string.Equals(Mode, "full", StringComparison.OrdinalIgnoreCase); }
        }
    }
}
