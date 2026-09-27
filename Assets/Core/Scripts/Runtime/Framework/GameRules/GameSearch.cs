using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// What a player searching for a public game wants to see: a set of rule values plus an "All" flag
    /// per rule. A rule whose All flag is set isn't filtered on at all. Used by the Normal / Hard / Very
    /// Hard search presets (see <see cref="GameSearchPresets"/>) and by the Custom search screen
    /// (HostGameController in FindGamesFilter mode).
    /// </summary>
    [Serializable]
    public class GameSearchFilter
    {
        /// <summary>The value each non-All rule must match. IsPublic is unused - only public games are listed.</summary>
        public HostGameRulesData Values = new HostGameRulesData();

        public bool AllMaps = true;
        public bool AllMaxPlayers = true;
        public bool AllRoundTimes = true;
        public bool AllBonusTimes = true;
        public bool AllLives = true;

        /// <summary>
        /// When set (and AllLives isn't), Values.Lives is an exclusive minimum instead of an exact match -
        /// only the Hard preset uses this ("more than 5 lives"). Unlimited lives always counts as more.
        /// Editing the Lives slider on the Custom screen clears it back to an exact match.
        /// </summary>
        public bool LivesMoreThan;

        public bool AllStartingPoints = true;
        public bool AllDeathPenalties = true;
        public bool AllWagerPayouts = true;
        public bool AllItems = true;
        public bool AllPickups = true;

        public bool Matches(HostGameRulesData game)
        {
            if (!AllMaps && game.MapName != Values.MapName) return false;
            if (!AllMaxPlayers && game.MaxPlayers != Values.MaxPlayers) return false;
            if (!AllRoundTimes && game.RoundTimeSeconds != Values.RoundTimeSeconds) return false;
            if (!AllBonusTimes && game.BonusTimeSeconds != Values.BonusTimeSeconds) return false;

            if (!AllLives)
            {
                if (LivesMoreThan)
                {
                    bool unlimited = game.Lives < 0;
                    if (!unlimited && game.Lives <= Values.Lives) return false;
                }
                else if (game.Lives != Values.Lives)
                {
                    return false;
                }
            }

            if (!AllStartingPoints && game.StartingPoints != Values.StartingPoints) return false;
            if (!AllDeathPenalties && game.DeathPenalty != Values.DeathPenalty) return false;
            if (!AllWagerPayouts && !Mathf.Approximately(game.WagerPayout, Values.WagerPayout)) return false;
            if (!AllItems && game.ItemsEnabled != Values.ItemsEnabled) return false;
            if (!AllPickups && game.PickupsEnabled != Values.PickupsEnabled) return false;
            return true;
        }

        public GameSearchFilter Clone()
        {
            var copy = (GameSearchFilter)MemberwiseClone();
            copy.Values = Values.Clone();
            return copy;
        }

        public void CopyFrom(GameSearchFilter other)
        {
            Values.CopyFrom(other.Values);
            AllMaps = other.AllMaps;
            AllMaxPlayers = other.AllMaxPlayers;
            AllRoundTimes = other.AllRoundTimes;
            AllBonusTimes = other.AllBonusTimes;
            AllLives = other.AllLives;
            LivesMoreThan = other.LivesMoreThan;
            AllStartingPoints = other.AllStartingPoints;
            AllDeathPenalties = other.AllDeathPenalties;
            AllWagerPayouts = other.AllWagerPayouts;
            AllItems = other.AllItems;
            AllPickups = other.AllPickups;
        }
    }

    /// <summary>
    /// The Join Game > Public search presets. These are deliberately different from the Host Game
    /// presets (HostGamePresets): they leave map, player count, round/bonus time and payout open.
    /// </summary>
    public static class GameSearchPresets
    {
        public static GameSearchFilter Normal => new GameSearchFilter
        {
            AllLives = false, AllStartingPoints = false, AllDeathPenalties = false, AllItems = false, AllPickups = false,
            Values = new HostGameRulesData { Lives = -1, StartingPoints = 1000, DeathPenalty = 50, ItemsEnabled = true, PickupsEnabled = true }
        };

        public static GameSearchFilter Hard => new GameSearchFilter
        {
            AllLives = false, LivesMoreThan = true, AllStartingPoints = false, AllDeathPenalties = false, AllItems = false, AllPickups = false,
            Values = new HostGameRulesData { Lives = 5, StartingPoints = 500, DeathPenalty = 100, ItemsEnabled = true, PickupsEnabled = true }
        };

        public static GameSearchFilter VeryHard => new GameSearchFilter
        {
            AllLives = false, AllStartingPoints = false, AllDeathPenalties = false, AllItems = false, AllPickups = false,
            Values = new HostGameRulesData { Lives = 1, StartingPoints = 100, DeathPenalty = -1, ItemsEnabled = false, PickupsEnabled = false }
        };
    }

    /// <summary>Search state carried between the Join Game screens for as long as the game is running (not saved to disk).</summary>
    public static class GameSearch
    {
        /// <summary>The filter the Game Browser applies - set right before it's opened.</summary>
        public static GameSearchFilter CurrentFilter { get; set; } = new GameSearchFilter();

        /// <summary>What the Custom search screen edits. Starts with every rule on All (show everything) and remembers changes between visits.</summary>
        public static GameSearchFilter CustomFilter { get; set; } = new GameSearchFilter();

        /// <summary>Scene the Game Browser's Back returns to (the public presets menu or the Custom screen). Empty = the browser's own default.</summary>
        public static string BrowserBackScene { get; set; } = "";
    }

    /// <summary>One hosted game as shown in the Game Browser.</summary>
    public class GameSessionListing
    {
        public string SessionName;
        public int PlayerCount;
        public HostGameRulesData Rules;
    }

    /// <summary>
    /// Where the Game Browser gets its list of hosted games. The real implementation will be a Photon
    /// Fusion 2 session lobby (NetworkRunner.JoinSessionLobby + INetworkRunnerCallbacks.OnSessionListUpdated,
    /// with each host's rules published as session properties). Until hosting exists, the browser uses
    /// <see cref="SampleGameSessionSource"/> or <see cref="EmptyGameSessionSource"/>.
    /// </summary>
    public interface IGameSessionSource
    {
        void RequestSessions(Action<IReadOnlyList<GameSessionListing>> onResult);
    }

    public class EmptyGameSessionSource : IGameSessionSource
    {
        public void RequestSessions(Action<IReadOnlyList<GameSessionListing>> onResult) =>
            onResult(Array.Empty<GameSessionListing>());
    }

    /// <summary>
    /// Fake hosted games for building and testing the Game Browser before real hosting exists. Always the
    /// same list (fixed seed). Roughly 70% of the games use one of the Host Game presets' core rules
    /// unchanged, so the Normal / Hard / Very Hard searches each find some results.
    /// </summary>
    public class SampleGameSessionSource : IGameSessionSource
    {
        private const int SessionCount = 24;

        private static readonly string[] MapNames =
        {
            "Airship", "Dinosaur Zoo", "Ninja Pirate Tower", "Red Rocks", "Sea Base", "Wizard Academy",
            "Map 8", "Map 9", "Last Stop", "Random"
        };

        private static readonly int[] MaxPlayerChoices = { 4, 6, 8, 10, 12, 16 };
        private static readonly int[] RoundTimeChoices = { 60, 90, 120, 150, 180, 240, 300, 360, 420, 480 };
        private static readonly int[] BonusTimeChoices = { 0, 30, 40, 50, 60, 70, 80, 90, 120, 180 };
        private static readonly int[] LivesChoices = { 1, 3, 5, 10, 20, -1 };
        private static readonly int[] StartingPointsChoices = { 0, 100, 500, 1000, 1500, 2000 };
        private static readonly int[] DeathPenaltyChoices = { 0, 25, 50, 100, 200, 500, -1 };
        private static readonly float[] WagerPayoutChoices = { 2.5f, 3f, 3.5f, 4f, 4.5f, 5f };

        private List<GameSessionListing> m_Sessions;

        public void RequestSessions(Action<IReadOnlyList<GameSessionListing>> onResult)
        {
            m_Sessions ??= Generate();
            onResult(m_Sessions);
        }

        private static List<GameSessionListing> Generate()
        {
            var random = new System.Random(1234);
            HostGameRulesData[] presets = { HostGamePresets.Normal, HostGamePresets.Hard, HostGamePresets.VeryHard };
            var sessions = new List<GameSessionListing>(SessionCount);

            for (int i = 0; i < SessionCount; i++)
            {
                HostGameRulesData rules = presets[i % presets.Length].Clone();
                rules.MapName = MapNames[random.Next(MapNames.Length)];
                rules.MaxPlayers = MaxPlayerChoices[random.Next(MaxPlayerChoices.Length)];
                rules.RoundTimeSeconds = RoundTimeChoices[random.Next(RoundTimeChoices.Length)];
                rules.BonusTimeSeconds = BonusTimeChoices[random.Next(BonusTimeChoices.Length)];
                rules.WagerPayout = WagerPayoutChoices[random.Next(WagerPayoutChoices.Length)];

                if (random.NextDouble() > 0.7)
                {
                    rules.Lives = LivesChoices[random.Next(LivesChoices.Length)];
                    rules.StartingPoints = StartingPointsChoices[random.Next(StartingPointsChoices.Length)];
                    rules.DeathPenalty = DeathPenaltyChoices[random.Next(DeathPenaltyChoices.Length)];
                    rules.ItemsEnabled = random.Next(2) == 0;
                    rules.PickupsEnabled = random.Next(2) == 0;
                }

                sessions.Add(new GameSessionListing
                {
                    SessionName = $"Sample Game {i + 1}",
                    PlayerCount = 1 + random.Next(rules.MaxPlayers - 1),
                    Rules = rules
                });
            }

            return sessions;
        }
    }

    /// <summary>Shared display formatting for rule values (Host Game screen, Custom search screen, Game Browser).</summary>
    public static class GameRulesFormat
    {
        public static string Time(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
        public static string Lives(int lives) => lives < 0 ? "Unlimited" : lives.ToString(CultureInfo.InvariantCulture);
        public static string DeathPenalty(int penalty) => penalty < 0 ? "All" : penalty.ToString(CultureInfo.InvariantCulture);
        public static string Payout(float payout) => payout.ToString("0.#", CultureInfo.InvariantCulture);
        public static string OnOff(bool on) => on ? "On" : "Off";
    }
}
