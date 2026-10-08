using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The hidden per-player tallies wagers are priced and settled on (jumps, seconds on rails, deaths, ...).
    /// Each player's own client counts its actions (LocalActionTracker) and sends its running totals to
    /// everyone a couple of times a second (<see cref="WagerStatsSync.StatsRpc"/>), together with which
    /// trigger actions happened since the last send. Nothing shows these numbers - keeping a mental tally is
    /// part of the game. Every client keeps the table so a new master client can carry on seamlessly.
    ///
    /// Counts are per round: the table is cleared when a round starts (WagerManager), and every avatar's
    /// tracker starts from zero when it spawns. Score stats are read live from PlayerScore instead.
    /// </summary>
    public static class WagerStats
    {
        public const int StatCount = 12; // Jumps..FinalScore (score ones unused here)

        private class Entry
        {
            public readonly int[] Values = new int[StatCount];
            public int LastSequence = -1;
        }

        private static readonly Dictionary<int, Entry> s_Players = new Dictionary<int, Entry>();

        /// <summary>Raised on every client when a report says a player just did trigger actions (player id, trigger bit mask).</summary>
        public static event Action<int, int> TriggersReported;

        public static void Clear() => s_Players.Clear();

        /// <summary>The player's current value for a stat (tenths of a second for seconds stats; live score for score stats).</summary>
        public static int Get(int playerId, WagerStat stat)
        {
            if (WagerCatalog.IsScoreStat(stat))
            {
                return NetworkPlayers.TryGetComponent((ulong)playerId, out PlayerScore score) ? score.Score : 0;
            }

            if (stat == WagerStat.Pickups) return PickupManager.CollectedBy(playerId);

            int index = (int)stat;
            if (index < 0 || index >= StatCount) return 0;
            return s_Players.TryGetValue(playerId, out Entry entry) ? entry.Values[index] : 0;
        }

        public static int TriggerBit(WagerTrigger trigger) => 1 << (int)trigger;

        /// <summary>Applies one report (dropping a repeat of an earlier one - it can arrive twice on the sender).</summary>
        internal static void Apply(int playerId, int sequence, int[] values, int triggerMask)
        {
            if (!s_Players.TryGetValue(playerId, out Entry entry))
            {
                entry = new Entry();
                s_Players[playerId] = entry;
            }

            if (sequence <= entry.LastSequence) return;
            entry.LastSequence = sequence;

            for (int i = 0; i < StatCount && i < values.Length; i++) entry.Values[i] = values[i];

            if (triggerMask != 0) TriggersReported?.Invoke(playerId, triggerMask);
        }

        /// <summary>Forgets a player's reports (e.g. a new avatar starting from zero after a sequence reset).</summary>
        internal static void ResetPlayer(int playerId) => s_Players.Remove(playerId);
    }

    /// <summary>
    /// Holds the static RPC that carries a player's tallies (static RPCs need a SimulationBehaviour to live on,
    /// but no instance or NetworkObject - same trick as PlayerProfileSync). Never added to anything.
    /// </summary>
    public class WagerStatsSync : SimulationBehaviour
    {
        /// <summary>Sends this player's running totals (and new trigger actions) to everyone.</summary>
        public static void Send(NetworkRunner runner, int sequence, int[] v, int triggerMask)
        {
            if (runner == null || !runner.IsRunning || !runner.LocalPlayer.IsRealPlayer) return;

            // Applied locally too, in case the RPC isn't echoed back to its sender (repeats are dropped).
            WagerStats.Apply(runner.LocalPlayer.PlayerId, sequence, v, triggerMask);
            StatsRpc(runner, sequence, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], triggerMask);
        }

        [Rpc]
        public static void StatsRpc(NetworkRunner runner, int sequence, int jumps, int runTenths, int railTenths, int railJumps,
            int wallJumps, int wallTenths, int poleTenths, int poleJumps, int deaths, int iceTenths, int triggerMask, RpcInfo info = default)
        {
            PlayerRef source = info.Source.IsRealPlayer ? info.Source : runner.LocalPlayer;
            if (!source.IsRealPlayer) return;

            var values = new int[WagerStats.StatCount];
            values[(int)WagerStat.Jumps] = jumps;
            values[(int)WagerStat.RunSeconds] = runTenths;
            values[(int)WagerStat.RailSeconds] = railTenths;
            values[(int)WagerStat.RailJumps] = railJumps;
            values[(int)WagerStat.WallJumps] = wallJumps;
            values[(int)WagerStat.WallSeconds] = wallTenths;
            values[(int)WagerStat.PoleSeconds] = poleTenths;
            values[(int)WagerStat.PoleJumps] = poleJumps;
            values[(int)WagerStat.Deaths] = deaths;
            values[(int)WagerStat.IceSeconds] = iceTenths;
            WagerStats.Apply(source.PlayerId, sequence, values, triggerMask);
        }
    }
}
