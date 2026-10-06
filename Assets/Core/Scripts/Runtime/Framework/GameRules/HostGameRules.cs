using System;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The rule set chosen on the Host Game screen (see HostGameController). Stored as concrete values
    /// rather than slider/dropdown indices, the same convention as GraphicsSettingsData, so whatever
    /// eventually starts the hosted session (the Fusion 2 host flow, not wired up yet) can read these
    /// directly without knowing anything about the screen's option lists.
    ///
    /// Sentinel values: <see cref="Lives"/> == -1 means Unlimited, and <see cref="DeathPenalty"/> == -1
    /// means All (the player loses every point they have).
    ///
    /// Field defaults match the Normal preset (see <see cref="HostGamePresets"/>), so a fresh Host Game
    /// screen starts on Normal. Presets don't touch <see cref="IsPublic"/> - visibility isn't part of any
    /// preset.
    /// </summary>
    [Serializable]
    public class HostGameRulesData
    {
        public bool IsPublic = true;
        public string MapName = "Random";
        public int MaxPlayers = 8;
        public int RoundTimeSeconds = 180;
        public int BonusTimeSeconds = 120;
        public int Lives = -1;
        public int StartingPoints = 1000;
        public int DeathPenalty = 50;
        public float WagerPayout = 3f;
        public bool ItemsEnabled = true;
        public bool PickupsEnabled = true;

        /// <summary>
        /// Average Target Score (Host Game screen, host only - not a search filter): the per-player score the
        /// team must average by the end of round 1. The round's team target is this times the number of
        /// players (times <see cref="TargetScoreGrowth"/> once per round already cleared).
        /// </summary>
        public int AverageTargetScore = 3000;

        /// <summary>Target Score Growth (Host Game screen, host only): the target is multiplied by this after every round the team clears. 1.0 = never gets harder.</summary>
        public float TargetScoreGrowth = 1.05f;

        /// <summary>Points a player earns for each whole second left on the bonus timer when they reach the end point. Not on any screen yet.</summary>
        public int BonusPointsPerSecond = 10;

        /// <summary>Percent of a player's final score they keep (on top of Starting Points) going into the next round. Not on any screen yet.</summary>
        public int CarryOverPercent = 5;

        /// <summary>
        /// The map prefab this game actually plays (its file name in Assets/Core/Prefabs/Maps, e.g. "LastStop"),
        /// worked out from the selected map when the game is hosted - see HostGameController.MapEntry.mapPrefab.
        /// Not part of any preset or search filter; those go by <see cref="MapName"/>, the name shown in menus.
        /// </summary>
        public string MapPrefab = "";

        public HostGameRulesData Clone() => (HostGameRulesData)MemberwiseClone();

        /// <summary>Copies every field from <paramref name="other"/>, including IsPublic.</summary>
        public void CopyFrom(HostGameRulesData other)
        {
            IsPublic = other.IsPublic;
            ApplyPreset(other);
        }

        /// <summary>Copies every preset-controlled field from <paramref name="preset"/>, leaving IsPublic as-is.</summary>
        public void ApplyPreset(HostGameRulesData preset)
        {
            MapName = preset.MapName;
            MaxPlayers = preset.MaxPlayers;
            RoundTimeSeconds = preset.RoundTimeSeconds;
            BonusTimeSeconds = preset.BonusTimeSeconds;
            Lives = preset.Lives;
            StartingPoints = preset.StartingPoints;
            DeathPenalty = preset.DeathPenalty;
            WagerPayout = preset.WagerPayout;
            ItemsEnabled = preset.ItemsEnabled;
            PickupsEnabled = preset.PickupsEnabled;
            AverageTargetScore = preset.AverageTargetScore;
            TargetScoreGrowth = preset.TargetScoreGrowth;
            BonusPointsPerSecond = preset.BonusPointsPerSecond;
            CarryOverPercent = preset.CarryOverPercent;
        }
    }

    /// <summary>The Host Game screen's Normal / Hard / Very Hard presets. Map names must match HostGameController's map display names.</summary>
    public static class HostGamePresets
    {
        public static HostGameRulesData Normal => new HostGameRulesData
        {
            MapName = "Random", MaxPlayers = 8, RoundTimeSeconds = 180, BonusTimeSeconds = 120, Lives = -1,
            StartingPoints = 1000, DeathPenalty = 50, WagerPayout = 3f, ItemsEnabled = true, PickupsEnabled = true,
            AverageTargetScore = 3000, TargetScoreGrowth = 1.05f
        };

        public static HostGameRulesData Hard => new HostGameRulesData
        {
            MapName = "Random", MaxPlayers = 8, RoundTimeSeconds = 150, BonusTimeSeconds = 60, Lives = 10,
            StartingPoints = 500, DeathPenalty = 100, WagerPayout = 3f, ItemsEnabled = true, PickupsEnabled = true,
            AverageTargetScore = 3600, TargetScoreGrowth = 1.1f
        };

        public static HostGameRulesData VeryHard => new HostGameRulesData
        {
            MapName = "Last Stop", MaxPlayers = 4, RoundTimeSeconds = 90, BonusTimeSeconds = 30, Lives = 1,
            StartingPoints = 100, DeathPenalty = -1, WagerPayout = 2.5f, ItemsEnabled = false, PickupsEnabled = false,
            AverageTargetScore = 4200, TargetScoreGrowth = 1.1f
        };
    }

    /// <summary>
    /// Holds the most recently configured Host Game rules for the lifetime of the running game (not
    /// saved to disk), so backing out of the Host Game screen and returning keeps what was picked.
    /// </summary>
    public static class HostGameRules
    {
        public static HostGameRulesData Current { get; set; } = new HostGameRulesData();
    }
}
