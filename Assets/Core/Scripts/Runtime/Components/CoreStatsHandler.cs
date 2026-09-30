using UnityEngine;
using Fusion;
using System.Collections.Generic;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Manages a character's stats (e.g., Health, Stamina) based on a <see cref="StatsConfig"/> asset.
    /// It handles stat initialization, modification, consumption, and regeneration.
    /// Stats are synchronized over the network in a Fusion networked array written by the owning client
    /// (State Authority); every client raises the stat events when it sees a value change.
    /// It also determines the "alive" state of the character based on a designated primary stat (e.g., Health).
    /// </summary>
    public class CoreStatsHandler : CoreNetworkBehaviour
    {
        #region Fields & Properties

        /// <summary>How many stats one character can have (size of the networked array).</summary>
        public const int MaxStats = 8;

        [Header("Configuration")]
        [Tooltip("The ScriptableObject defining the stats for this character.")]
        [SerializeField] private StatsConfig statsConfig;

        [Header("Broadcasting on")]
        [Tooltip("GameEvent raised when any stat's value changes (only for stats with broadcastNetworkedEvents enabled).")]
        [SerializeField] private StatChangeEvent onStatChangedEvent;
        [Tooltip("GameEvent raised when any stat reaches zero (only for stats with broadcastNetworkedEvents enabled).")]
        [SerializeField] private StatDepletedEvent onStatDepletedEvent;

        /// <summary>
        /// Gets a value indicating whether the character is currently alive.
        /// The character is considered alive if their primary stat (e.g., Health) is greater than zero.
        /// </summary>
        public bool IsAlive { get; private set; } = true;

        // Networked stat values - everyone reads, only the owner writes.
        [Networked, Capacity(MaxStats)] private NetworkArray<RuntimeStat> NetStats => default;
        [Networked] private int NetStatCount { get; set; }

        // The values this client last saw, to raise change events when they change (see DetectChanges).
        private readonly RuntimeStat[] m_LastSeen = new RuntimeStat[MaxStats];
        private int m_LastSeenCount;

        // Tracks the last time each stat was consumed to enforce regeneration delays
        private readonly Dictionary<int, float> m_LastStatUseTime = new Dictionary<int, float>();

        // Caches stat definitions by hash for fast lookup without config access
        private readonly Dictionary<int, StatDefinition> m_StatDefinitions = new Dictionary<int, StatDefinition>();

        private int StatCount => IsSpawned ? Mathf.Clamp(NetStatCount, 0, MaxStats) : 0;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            if (statsConfig == null)
            {
                Debug.LogError($"[CoreStatsHandler] StatsConfig not assigned", this);
                enabled = false;
                return;
            }

            // Populate the definitions dictionary from the config for fast lookups
            foreach (var def in statsConfig.stats)
            {
                m_StatDefinitions[Animator.StringToHash(def.statName)] = def;
            }
        }

        public override void OnNetworkSpawn()
        {
            if (statsConfig == null) return;

            if (IsOwner && NetStatCount == 0)
            {
                // The owner is responsible for initializing the stats for their character
                // This ensures that stats are set only once and then synchronized to other clients
                if (statsConfig.stats == null || statsConfig.stats.Count == 0)
                {
                    Debug.LogWarning($"[CoreStatsHandler] No stats defined in StatsConfig on {gameObject.name}", this);
                }
                else
                {
                    if (statsConfig.stats.Count > MaxStats)
                    {
                        Debug.LogWarning($"[CoreStatsHandler] StatsConfig has {statsConfig.stats.Count} stats; only the first {MaxStats} are networked.", this);
                    }

                    int count = Mathf.Min(statsConfig.stats.Count, MaxStats);
                    for (int i = 0; i < count; i++)
                    {
                        var def = statsConfig.stats[i];
                        NetStats.Set(i, new RuntimeStat
                        {
                            StatHash = Animator.StringToHash(def.statName),
                            CurrentValue = def.startingValue
                        });
                    }
                    NetStatCount = count;
                }
            }

            // Broadcast the initial state of all stats for late-joining clients or initialization
            m_LastSeenCount = StatCount;
            for (int i = 0; i < m_LastSeenCount; i++)
            {
                m_LastSeen[i] = NetStats[i];
                BroadcastStatChange(m_LastSeen[i], raiseDepleted: m_LastSeen[i].CurrentValue <= 0);
            }

            // Set the initial alive state
            UpdateAliveState();
        }

        public override void Render()
        {
            DetectChanges();
        }

        private void Update()
        {
            // Regeneration logic is only handled by the owner
            if (!IsOwner || !IsAlive) return;

            HandleRegeneration();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Modifies a stat by a given amount. Only the owner can call this.
        /// </summary>
        /// <param name="statHash">The hash of the stat to modify (use StatKeys).</param>
        /// <param name="amount">The amount to add or subtract.</param>
        /// <param name="sourcePlayerId">Who caused this change.</param>
        /// <param name="sourceType">What type of modification this is.</param>
        public void ModifyStat(int statHash, float amount, ulong sourcePlayerId = 0, ModificationSource sourceType = ModificationSource.Direct)
        {
            if (!IsOwner) return;

            int statIndex = FindStatIndex(statHash);

            if (statIndex != -1)
            {
                ModifyStat(statIndex, amount, true, sourcePlayerId, sourceType);
            }
            else
            {
                Debug.LogWarning($"[CoreStatsHandler] ModifyStat failed: Stat with hash {statHash} not found on {gameObject.name}", this);
            }
        }

        /// <summary>
        /// Attempts to consume a certain amount from a stat.
        /// </summary>
        /// <param name="statHash">The hash of the stat to consume (use StatKeys).</param>
        /// <param name="amount">The amount to consume.</param>
        /// <param name="sourcePlayerId">Who caused this consumption.</param>
        /// <returns>True if the stat had enough value to consume, false otherwise.</returns>
        public bool TryConsumeStat(int statHash, float amount, ulong sourcePlayerId = 0)
        {
            if (!IsOwner) return false;

            int statIndex = FindStatIndex(statHash);

            if (statIndex == -1)
            {
                Debug.LogWarning($"[CoreStatsHandler] TryConsumeStat failed: Stat with hash {statHash} not found on {gameObject.name}", this);
                return false;
            }

            if (NetStats[statIndex].CurrentValue >= amount)
            {
                ModifyStat(statIndex, -amount, true, sourcePlayerId, ModificationSource.Consumption);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Attempts to consume a certain amount from a stat.
        /// </summary>
        /// <param name="statName">The name of the stat to consume.</param>
        /// <param name="amount">The amount to consume.</param>
        /// <param name="sourcePlayerId">Who caused this consumption.</param>
        /// <returns>True if the stat had enough value to consume, false otherwise.</returns>
        [System.Obsolete("Use TryConsumeStat(int statHash, ...) instead with StatKeys to avoid string hashing overhead")]
        public bool TryConsumeStat(string statName, float amount, ulong sourcePlayerId = 0)
        {
            return TryConsumeStat(Animator.StringToHash(statName), amount, sourcePlayerId);
        }

        /// <summary>
        /// Gets an enumerable collection of all stats with their current and max values.
        /// </summary>
        /// <returns>An IEnumerable of tuples containing the stat name, current value, and max value.</returns>
        public IEnumerable<(string name, float current, float max)> GetAllStats()
        {
            int count = StatCount;
            for (int i = 0; i < count; i++)
            {
                RuntimeStat runtimeStat = NetStats[i];
                if (m_StatDefinitions.TryGetValue(runtimeStat.StatHash, out var def))
                {
                    yield return (def.statName, runtimeStat.CurrentValue, def.maxValue);
                }
            }
        }

        /// <summary>
        /// Gets the current value of a specific stat. Before the object has spawned this is the stat's
        /// starting value.
        /// </summary>
        /// <param name="statHash">The hash of the stat (use StatKeys).</param>
        /// <returns>The current value, or 0 if the stat is not found.</returns>
        public float GetCurrentValue(int statHash)
        {
            if (!IsSpawned)
            {
                return m_StatDefinitions.TryGetValue(statHash, out var definition) ? definition.startingValue : 0f;
            }

            int index = FindStatIndex(statHash);
            if (index != -1)
            {
                return NetStats[index].CurrentValue;
            }

            Debug.LogWarning($"[CoreStatsHandler] GetCurrentValue: Stat with hash {statHash} not found on {gameObject.name}, returning 0", this);
            return 0;
        }

        /// <summary>
        /// Gets the maximum value of a specific stat.
        /// </summary>
        /// <param name="statHash">The hash of the stat (use StatKeys).</param>
        /// <returns>The maximum value, or 0 if the stat is not found.</returns>
        public float GetMaxValue(int statHash)
        {
            if (m_StatDefinitions.TryGetValue(statHash, out var def))
            {
                return def.maxValue;
            }
            return 0f;
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Compares the networked stats with the values this client last saw and raises the stat events for
        /// every stat that changed. Runs every frame (Render) on every client, and right after the owner
        /// changes a value, so the owner's listeners react in the same frame (like NGO's OnListChanged).
        /// "Depleted" is raised when a stat drops to zero from above zero (not again for further changes
        /// while it's still at zero).
        /// </summary>
        private void DetectChanges()
        {
            if (!IsSpawned) return;

            int count = StatCount;
            bool anyChanged = false;
            for (int i = 0; i < count; i++)
            {
                RuntimeStat current = NetStats[i];
                bool isNew = i >= m_LastSeenCount;
                if (!isNew && current.Equals(m_LastSeen[i])) continue;

                bool wasAboveZero = isNew || m_LastSeen[i].CurrentValue > 0;
                m_LastSeen[i] = current;
                anyChanged = true;

                UpdateAliveState();
                BroadcastStatChange(current, raiseDepleted: wasAboveZero && current.CurrentValue <= 0);
            }

            if (count != m_LastSeenCount)
            {
                m_LastSeenCount = count;
                anyChanged = true;
            }

            if (anyChanged) UpdateAliveState();
        }

        /// <summary>
        /// Handles the regeneration of stats over time, respecting regeneration delays.
        /// </summary>
        private void HandleRegeneration()
        {
            int count = StatCount;
            for (int i = 0; i < count; i++)
            {
                var stat = NetStats[i];
                if (m_StatDefinitions.TryGetValue(stat.StatHash, out var def))
                {
                    if (def.regenRate > 0 && stat.CurrentValue < def.maxValue)
                    {
                        // Only regenerate if the stat has never been used or enough time has passed since last consumption
                        if (!m_LastStatUseTime.ContainsKey(stat.StatHash) ||
                            Time.time - m_LastStatUseTime[stat.StatHash] > def.regenDelay)
                        {
                            // Don't record use time for regeneration to avoid resetting the delay
                            ModifyStat(i, def.regenRate * Time.deltaTime, false, 0, ModificationSource.Regeneration);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Updates the character's alive state based on the primary stat.
        /// Runs on all clients whenever stats change.
        /// </summary>
        private void UpdateAliveState()
        {
            var primaryStatValue = GetCurrentValue(statsConfig.PrimaryStatHash);
            IsAlive = primaryStatValue > 0;
        }

        /// <summary>
        /// Raises the appropriate events with a payload containing the updated stat information.
        /// This runs on all clients when a stat changes, not just the owner.
        /// Use the OwnerClientId in the payload to identify which player the stat change belongs to.
        /// For local player updates, listeners can filter using IsOwner.
        /// </summary>
        /// <param name="stat">The stat that has changed (including who caused it and how).</param>
        /// <param name="raiseDepleted">Whether to also raise the depleted event (if the stat's flags ask for it).</param>
        private void BroadcastStatChange(RuntimeStat stat, bool raiseDepleted)
        {
            if (m_StatDefinitions.TryGetValue(stat.StatHash, out var def))
            {
                if (def.eventFlags.HasFlag(StatEventFlags.OnChanged))
                {
                    var statPayload = new StatChangePayload
                    {
                        targetPlayerId = OwnerClientId,
                        sourcePlayerId = stat.SourcePlayerId,
                        sourceType = stat.SourceType,
                        statName = def.statName,
                        statID = stat.StatHash,
                        currentValue = stat.CurrentValue,
                        maxValue = def.maxValue
                    };
                    onStatChangedEvent?.Raise(statPayload);
                }

                if (raiseDepleted && def.eventFlags.HasFlag(StatEventFlags.OnDepleted))
                {
                    var depletedPayload = new StatDepletedPayload
                    {
                        playerId = OwnerClientId,
                        statName = def.statName,
                        statID = stat.StatHash
                    };
                    onStatDepletedEvent?.Raise(depletedPayload);
                }
            }
            else
            {
                Debug.LogWarning($"[CoreStatsHandler] BroadcastStatChange: No definition found for stat hash {stat.StatHash} on {gameObject.name}", this);
            }
        }

        /// <summary>
        /// Internal method to modify a stat by its index in the networked array.
        /// Clamps the value to the stat's min and max values and triggers network synchronization.
        /// </summary>
        /// <param name="index">The index of the stat in the networked array.</param>
        /// <param name="amount">The amount to add or subtract.</param>
        /// <param name="recordUseTime">Whether to record the use time for regeneration delay tracking.</param>
        /// <param name="sourcePlayerId">The player who caused this change.</param>
        /// <param name="sourceType">The type of modification.</param>
        private void ModifyStat(int index, float amount, bool recordUseTime, ulong sourcePlayerId = 0, ModificationSource sourceType = ModificationSource.Unknown)
        {
            if (index < 0 || index >= StatCount)
            {
                Debug.LogError($"[CoreStatsHandler] ModifyStat: Index {index} out of bounds (count: {StatCount}) on {gameObject.name}", this);
                return;
            }

            var stat = NetStats[index];
            if (m_StatDefinitions.TryGetValue(stat.StatHash, out var def))
            {
                stat.CurrentValue = Mathf.Clamp(stat.CurrentValue + amount, def.minValue, def.maxValue);
                stat.SourcePlayerId = sourcePlayerId;
                stat.SourceType = sourceType;

                // Writing the networked array replicates the change to all clients
                NetStats.Set(index, stat);

                // Record use time only for consumption to enforce regeneration delays
                if (recordUseTime && amount < 0)
                {
                    m_LastStatUseTime[stat.StatHash] = Time.time;
                }

                DetectChanges();
            }
            else
            {
                Debug.LogWarning($"[CoreStatsHandler] ModifyStat: No definition found for stat hash {stat.StatHash} on {gameObject.name}", this);
            }
        }

        /// <summary>
        /// Finds the index of a stat in the networked array using its hash.
        /// </summary>
        /// <param name="statHash">The hash of the stat to find.</param>
        /// <returns>The index of the stat, or -1 if not found.</returns>
        private int FindStatIndex(int statHash)
        {
            int count = StatCount;
            for (int i = 0; i < count; i++)
            {
                if (NetStats[i].StatHash == statHash)
                {
                    return i;
                }
            }
            return -1;
        }

        #endregion
    }
}
