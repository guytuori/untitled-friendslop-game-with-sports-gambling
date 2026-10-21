using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>One player's line on the Round Results screen.</summary>
    [Serializable]
    public struct RoundResultEntry
    {
        public int PlayerId;
        public string Name;
        public int Score;
    }

    /// <summary>
    /// What this client remembers between the scenes of one match (gameplay -> Round Results -> gameplay ...
    /// or -> Final Score). Plain statics, not networked: every client records the same things at the same
    /// moment (when RoundTimer reports the round has ended, before anyone leaves the gameplay scene), so
    /// they all agree without sending anything extra.
    ///
    /// - <see cref="RoundNumber"/>: the round being played (1-based). The master client copies it into
    ///   RoundTimer when a round starts, so the round's target score is the same for everyone.
    /// - <see cref="LocalCarryOverPoints"/>: this player's share of their last final score that they keep
    ///   (CarryOverPercent, 5% by default). PlayerScore adds it to Starting Points at the next spawn.
    /// - <see cref="LastResults"/> etc.: the finished round, for the Round Results screen.
    /// - Who's ready on the Round Results screen (<see cref="MatchFlowSync.ReadyRpc"/>).
    ///
    /// Reset when a session starts or ends, and when the host starts a match from the lobby.
    /// </summary>
    public static class MatchProgress
    {
        private static readonly List<RoundResultEntry> s_LastResults = new List<RoundResultEntry>();
        private static readonly HashSet<int> s_Ready = new HashSet<int>();

        /// <summary>The round being played (or about to be), starting at 1.</summary>
        public static int RoundNumber { get; private set; } = 1;

        /// <summary>Points this player keeps from the last round, added to Starting Points when they next spawn.</summary>
        public static int LocalCarryOverPoints { get; private set; }

        /// <summary>True once a round has ended in this match (LastXxx below are valid).</summary>
        public static bool HasResults { get; private set; }

        public static int LastRoundNumber { get; private set; }
        public static bool LastPassed { get; private set; }
        public static int LastTeamTotal { get; private set; }
        public static int LastTeamTarget { get; private set; }

        /// <summary>The team target of the next round, for the same number of players.</summary>
        public static int NextTeamTarget { get; private set; }

        /// <summary>Every player's score at the end of the last round, highest first.</summary>
        public static IReadOnlyList<RoundResultEntry> LastResults => s_LastResults;

        /// <summary>Raised when someone's ready state changes on the Round Results screen.</summary>
        public static event Action ReadyChanged;

        /// <summary>This player's own final score in the last round - their budget in the between-rounds item shop.</summary>
        public static int LastLocalFinalScore { get; private set; }

        /// <summary>The item this player bought in the between-rounds shop, waiting to be put in their slot (None if none).</summary>
        public static ItemId LocalShopItem { get; private set; }

        /// <summary>What it cost: taken off the points this player starts the next round with.</summary>
        public static int LocalShopSpend { get; private set; }

        /// <summary>Between-rounds shop: this player buys <paramref name="item"/> (None = No Item) for <paramref name="cost"/>.</summary>
        public static void SetShopPurchase(ItemId item, int cost)
        {
            LocalShopItem = item;
            LocalShopSpend = item == ItemId.None ? 0 : Math.Max(0, cost);
        }

        /// <summary>The bought item, once (LocalItemSlot puts it in the slot when the round starts).</summary>
        public static ItemId TakeShopItem()
        {
            ItemId item = LocalShopItem;
            LocalShopItem = ItemId.None;
            return item;
        }

        /// <summary>The shop spend, once (PlayerScore takes it off the starting points).</summary>
        public static int TakeShopSpend()
        {
            int spend = LocalShopSpend;
            LocalShopSpend = 0;
            return spend;
        }

        /// <summary>
        /// How this player placed in the last round, 1 = first and 0 = last (players tied on score share the
        /// better place; 0.5 if they were the only one). Used to pick which shop items they're offered.
        /// </summary>
        public static float LocalStanding(int localPlayerId)
        {
            int n = s_LastResults.Count;
            if (n <= 1) return 0.5f;

            int localScore = int.MinValue;
            bool found = false;
            foreach (RoundResultEntry entry in s_LastResults)
            {
                if (entry.PlayerId != localPlayerId) continue;
                localScore = entry.Score;
                found = true;
                break;
            }
            if (!found) return 0.5f;

            int better = 0;
            foreach (RoundResultEntry entry in s_LastResults)
            {
                if (entry.Score > localScore) better++;
            }
            return 1f - better / (float)(n - 1);
        }

        public static void Reset()
        {
            RoundNumber = 1;
            LocalCarryOverPoints = 0;
            LastLocalFinalScore = 0;
            LocalShopItem = ItemId.None;
            LocalShopSpend = 0;
            HasResults = false;
            LastRoundNumber = 0;
            LastPassed = false;
            LastTeamTotal = 0;
            LastTeamTarget = 0;
            NextTeamTarget = 0;
            s_LastResults.Clear();
            ClearReady();
        }

        /// <summary>Round N's target per player: Average Target Score x Target Score Growth^(N-1), rounded.</summary>
        public static int PerPlayerTarget(HostGameRulesData rules, int roundNumber)
        {
            double growth = Math.Max(0.01, rules.TargetScoreGrowth);
            double target = rules.AverageTargetScore * Math.Pow(growth, Math.Max(0, roundNumber - 1));
            return (int)Math.Round(target, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Called on every client by RoundTimer when the round's result is in. Ignored if this round was
        /// already recorded. A cleared round moves <see cref="RoundNumber"/> on and works out this player's
        /// carry-over; a failed one ends the match (no carry-over).
        /// </summary>
        public static void RecordRoundEnd(int roundNumber, bool passed, int teamTotal, int teamTarget, int nextTeamTarget,
            List<RoundResultEntry> results, int localFinalScore, int carryOverPercent)
        {
            if (HasResults && LastRoundNumber == roundNumber) return;

            HasResults = true;
            LastRoundNumber = roundNumber;
            LastPassed = passed;
            LastTeamTotal = teamTotal;
            LastTeamTarget = teamTarget;
            NextTeamTarget = nextTeamTarget;
            LastLocalFinalScore = localFinalScore;
            LocalShopItem = ItemId.None; // a fresh shop for every round
            LocalShopSpend = 0;

            s_LastResults.Clear();
            if (results != null) s_LastResults.AddRange(results);
            s_LastResults.Sort((a, b) => b.Score.CompareTo(a.Score));

            if (passed)
            {
                RoundNumber = roundNumber + 1;
                LocalCarryOverPoints = Mathf.Max(0, Mathf.RoundToInt(localFinalScore * carryOverPercent / 100f));
            }
            else
            {
                LocalCarryOverPoints = 0;
            }

            ClearReady();
        }

        // ----- Round Results ready-up -----

        public static bool IsReady(int playerId) => s_Ready.Contains(playerId);

        public static int ReadyCount => s_Ready.Count;

        internal static void MarkReady(int playerId)
        {
            if (s_Ready.Add(playerId)) ReadyChanged?.Invoke();
        }

        private static void ClearReady()
        {
            if (s_Ready.Count == 0) return;
            s_Ready.Clear();
            ReadyChanged?.Invoke();
        }
    }

    /// <summary>
    /// Holds the static RPCs for the between-scenes flow (static RPCs need a SimulationBehaviour to live on,
    /// but no instance or NetworkObject - same trick as PlayerProfileSync). Never added to anything.
    /// </summary>
    public class MatchFlowSync : SimulationBehaviour
    {
        /// <summary>"I'm ready for the next round" - sent to everyone (including the sender) from the Round Results screen.</summary>
        [Rpc]
        public static void ReadyRpc(NetworkRunner runner, RpcInfo info = default)
        {
            PlayerRef source = info.Source.IsRealPlayer ? info.Source : runner.LocalPlayer;
            if (!source.IsRealPlayer) return;
            MatchProgress.MarkReady(source.PlayerId);
        }
    }
}
