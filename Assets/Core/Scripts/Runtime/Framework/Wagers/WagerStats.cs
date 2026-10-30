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
    /// tracker starts from zero when it spawns. Score stats are read live from PlayerScore instead, and
    /// pickups from PickupManager.
    /// </summary>
    public static class WagerStats
    {
        /// <summary>One slot per WagerStat (the score and pickup slots go unused - those are read live).</summary>
        public const int StatCount = (int)WagerStat.PoleRotations + 1;

        /// <summary>The RPC carries two stats per int (16 bits each - up to 65535, i.e. 6553.5 seconds in tenths).</summary>
        public const int MaxStats = WagerStatsSync.PackedInts * 2;

        // Doesn't compile once there are more stats than the RPC carries: raise PackedInts and add parameters.
        private const uint StatsFitInRpc = MaxStats - StatCount;

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
        /// <summary>Ints of packed stats the RPC carries (two WagerStats per int, in WagerStat order).</summary>
        public const int PackedInts = 12;

        /// <summary>Sends this player's running totals (and new trigger actions) to everyone.</summary>
        public static void Send(NetworkRunner runner, int sequence, int[] v, int triggerMask)
        {
            if (runner == null || !runner.IsRunning || !runner.LocalPlayer.IsRealPlayer) return;

            // Applied locally too, in case the RPC isn't echoed back to its sender (repeats are dropped).
            WagerStats.Apply(runner.LocalPlayer.PlayerId, sequence, v, triggerMask);
            StatsRpc(runner, sequence, Pack(v, 0), Pack(v, 1), Pack(v, 2), Pack(v, 3), Pack(v, 4), Pack(v, 5),
                Pack(v, 6), Pack(v, 7), Pack(v, 8), Pack(v, 9), Pack(v, 10), Pack(v, 11), triggerMask);
        }

        [Rpc]
        public static void StatsRpc(NetworkRunner runner, int sequence, int p0, int p1, int p2, int p3, int p4, int p5,
            int p6, int p7, int p8, int p9, int p10, int p11, int triggerMask, RpcInfo info = default)
        {
            PlayerRef source = info.Source.IsRealPlayer ? info.Source : runner.LocalPlayer;
            if (!source.IsRealPlayer) return;

            var values = new int[WagerStats.StatCount];
            int[] packed = { p0, p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11 };
            for (int i = 0; i < values.Length && i / 2 < packed.Length; i++)
            {
                values[i] = (i & 1) == 0 ? packed[i / 2] & 0xFFFF : (packed[i / 2] >> 16) & 0xFFFF;
            }
            WagerStats.Apply(source.PlayerId, sequence, values, triggerMask);
        }

        /// <summary>Stats 2i and 2i + 1 as one int (low and high 16 bits, each clamped to 0-65535).</summary>
        private static int Pack(int[] v, int i)
        {
            int Get(int index) => index < v.Length ? Mathf.Clamp(v[index], 0, 0xFFFF) : 0;
            return Get(2 * i) | (Get(2 * i + 1) << 16);
        }
    }
}
