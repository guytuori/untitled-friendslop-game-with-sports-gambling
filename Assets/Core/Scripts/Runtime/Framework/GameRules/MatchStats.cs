using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Every round's stats, for the Game Results screen (<see cref="GameResults"/>, 2026-11-09): one snapshot per
    /// round, of every player's score and every <see cref="WagerStat"/> (seconds stats in tenths, as WagerStats
    /// keeps them). Taken on every client by RoundTimer when the round has ended - the same moment
    /// MatchProgress records the results, before anyone leaves the gameplay scene - so everyone has the same
    /// table without sending anything extra.
    ///
    /// Kept in memory, not in files: a 16-player round is about 1.5 KB, so even a 100-round match is ~150 KB.
    /// Cleared with MatchProgress (session start / end, Start Match).
    /// </summary>
    public static class MatchStats
    {
        /// <summary>One player's line in one round.</summary>
        public class PlayerLine
        {
            public int PlayerId;
            public string Name;
            public int Score;

            /// <summary>Indexed by WagerStat; seconds stats are in tenths of a second.</summary>
            public readonly int[] Values = new int[WagerStats.StatCount];
        }

        /// <summary>One finished round.</summary>
        public class Round
        {
            public int RoundNumber;
            public string MapId;
            public bool Passed;
            public int TeamTotal;
            public int TeamTarget;

            // What the map had - map-specific columns only show for rounds where they meant something.
            public bool HasPickups;
            public bool HasItems;
            public bool HasDinosaurs;

            public readonly List<PlayerLine> Players = new List<PlayerLine>();
        }

        private static readonly List<Round> s_Rounds = new List<Round>();

        /// <summary>The rounds played so far in this match, in order.</summary>
        public static IReadOnlyList<Round> Rounds => s_Rounds;

        public static void Clear() => s_Rounds.Clear();

        /// <summary>Records the round that just ended (a second call for the same round number replaces the first).</summary>
        public static void Capture(int roundNumber, bool passed, int teamTotal, int teamTarget)
        {
            var round = new Round
            {
                RoundNumber = roundNumber,
                MapId = MusicTracks.MapIdOf(MatchLayout.Current),
                Passed = passed,
                TeamTotal = teamTotal,
                TeamTarget = teamTarget,
                HasPickups = PickupManager.Instance != null && PickupManager.Instance.TotalCount > 0,
                HasItems = ItemManager.ItemsEnabled && ItemManager.Instance != null && ItemManager.Instance.BoxCount > 0,
                HasDinosaurs = Dinosaurs.All.Count > 0,
            };

            foreach (ulong id in NetworkPlayers.Ids)
            {
                if (!NetworkPlayers.TryGetComponent(id, out PlayerScore score)) continue;
                int playerId = (int)id;
                var line = new PlayerLine
                {
                    PlayerId = playerId,
                    Name = NetworkPlayers.GetPlayerLabel(id),
                    Score = score.Score,
                };
                for (int i = 0; i < WagerStats.StatCount; i++) line.Values[i] = WagerStats.Get(playerId, (WagerStat)i);
                round.Players.Add(line);
            }

            s_Rounds.RemoveAll(r => r.RoundNumber == roundNumber);
            s_Rounds.Add(round);
            s_Rounds.Sort((a, b) => a.RoundNumber.CompareTo(b.RoundNumber));
            Debug.Log($"[MatchStats] Round {roundNumber}: stats of {round.Players.Count} player(s) recorded.");
        }
    }
}
