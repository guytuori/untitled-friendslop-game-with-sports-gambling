using System;
using System.Collections.Generic;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The single, authoritative brain for every <see cref="ChallengeZone"/> in the map, plus the
    /// flat death penalty that applies regardless of any challenge. Lives on one GameObject placed
    /// directly in the gameplay scene (see ChallengeSystemSetup), the same way RoundTimer does.
    ///
    /// Networking (Photon Fusion, Shared mode): a scene object owned by the session's master client
    /// ("Is Master Client Object" on its NetworkObject). The master client makes every decision below;
    /// other clients report their own zone entries and bets to it through RPCs
    /// (<see cref="RequestZoneEntry"/>, <see cref="PlaceBet"/>) and read the networked betting-window state.
    /// ChallengeZones themselves aren't networked objects - they're identified by a stable index (see
    /// <see cref="GetZoneIndex"/>). If the master client leaves mid-challenge, the next master takes over
    /// the networked state (pause, betting window), but the departed master's in-progress bookkeeping
    /// (who owns which challenge, bets placed) is lost.
    ///
    /// Responsibilities, each easy to re-tune without touching the others:
    /// - Death penalty: -<see cref="deathPenalty"/> points any time any player's Health depletes, challenge
    ///   or no challenge (see the death-penalty region).
    /// - Ownership: the first player to enter a challenge's start volume owns it (see HandleZoneEntry).
    /// - The betting window: taking ownership pauses every player for <see cref="bettingWindowSeconds"/>
    ///   while everyone else is offered a bet (see the betting-window region). Only one betting window can
    ///   run at a time, project-wide - a second claim attempt while one is already running is just ignored.
    /// - Payout: reaching the finish awards the base points plus either par bonus that was beaten, and
    ///   resolves every placed bet at <see cref="betPayoutMultiplier"/>x for a correct prediction (see
    ///   ResolveAttempt).
    ///
    /// CoreHUD reads <see cref="IsBettingWindowActive"/>, <see cref="BettingTimeRemaining"/>,
    /// <see cref="ActiveChallengeName"/>, <see cref="ActiveChallengerClientId"/> and
    /// <see cref="ActiveBettorClientIds"/> (and their change events) to draw the betting overlay, and calls
    /// <see cref="PlaceBet"/> when a player picks an option. <see cref="ChallengePauseGate"/> (one per
    /// player) reads <see cref="IsPaused"/> to freeze/unfreeze that player's own movement input.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class ChallengeManager : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        private const int MaxBettors = 32;

        [Header("Death Penalty")]
        [Tooltip("Points lost any time any player's Health stat depletes to zero, whether or not they're mid-challenge.")]
        [SerializeField] private int deathPenalty = 50;
        [Tooltip("The shared GameEvent CoreStatsHandler raises when a stat depletes - assign the project's OnStatDepleted asset.")]
        [SerializeField] private StatDepletedEvent onStatDepletedEvent;

        [Header("Betting Window")]
        [Tooltip("How long, in seconds, the game pauses for everyone after a challenge is claimed.")]
        [SerializeField] private float bettingWindowSeconds = 5f;
        [Tooltip("Multiplier applied to a challenge's base points for a correct bet (paid in full, on top of the wager already lost).")]
        [SerializeField] private int betPayoutMultiplier = 3;

        /// <summary>The single ChallengeManager for the current scene. Set in Awake, like RoundTimer/CoreDirector.</summary>
        public static ChallengeManager Instance { get; private set; }

        /// <summary>How long a betting window lasts, for CoreHUD to size its countdown bar correctly.</summary>
        public float BettingWindowSeconds => bettingWindowSeconds;

        // Networked state - everyone reads, only the master client writes.
        [Networked] private NetworkBool NetIsPaused { get; set; }
        [Networked] private NetworkBool NetIsBettingWindowActive { get; set; }
        [Networked] private float NetBettingTimeRemaining { get; set; }
        [Networked] private NetworkString<_64> NetActiveChallengeName { get; set; }
        [Networked] private int NetActiveChallengerId { get; set; }
        [Networked, Capacity(MaxBettors)] private NetworkArray<int> NetBettorIds => default;
        [Networked] private int NetBettorCount { get; set; }

        /// <summary>True while every player should be frozen in place (the betting window).</summary>
        public bool IsPaused => IsSpawned && NetIsPaused;

        /// <summary>True while a betting window is actively counting down.</summary>
        public bool IsBettingWindowActive => IsSpawned && NetIsBettingWindowActive;

        /// <summary>Seconds left in the current betting window (0 when none is active).</summary>
        public float BettingTimeRemaining => IsSpawned ? NetBettingTimeRemaining : 0f;

        /// <summary>The name of the challenge currently up for betting (only meaningful while a window is active).</summary>
        public string ActiveChallengeName => IsSpawned ? NetActiveChallengeName.ToString() : string.Empty;

        /// <summary>The player id of the player who just claimed the active challenge (only meaningful while a window is active).</summary>
        public ulong ActiveChallengerClientId => IsSpawned ? (ulong)Mathf.Max(0, NetActiveChallengerId) : 0;

        /// <summary>Player ids of everyone who has placed a real wager (not a decline) during the current window.</summary>
        public IEnumerable<ulong> ActiveBettorClientIds
        {
            get
            {
                if (!IsSpawned) yield break;
                int count = Mathf.Clamp(NetBettorCount, 0, MaxBettors);
                for (int i = 0; i < count; i++)
                {
                    yield return (ulong)NetBettorIds[i];
                }
            }
        }

        /// <summary>Raised on every client when <see cref="IsPaused"/> changes (previous, new).</summary>
        public event Action<bool, bool> PausedChanged;

        /// <summary>Raised on every client when <see cref="IsBettingWindowActive"/> changes (previous, new).</summary>
        public event Action<bool, bool> BettingWindowActiveChanged;

        /// <summary>Raised on every client when <see cref="BettingTimeRemaining"/> changes (previous, new).</summary>
        public event Action<float, float> BettingTimeRemainingChanged;

        /// <summary>Raised on every client when the list of bettors changes.</summary>
        public event Action ActiveBettorsChanged;

        // Last values seen by this client - for the change events above.
        private bool m_LastIsPaused;
        private bool m_LastIsBettingWindowActive;
        private float m_LastBettingTimeRemaining;
        private int m_LastBettorsKey;

        // Master-client-only bookkeeping - never networked directly, only reflected through the networked state above.
        private ChallengeZone m_BettingWindowZone;
        private readonly Dictionary<ulong, ChallengeZone> m_RunningAttemptsByOwner = new Dictionary<ulong, ChallengeZone>();

        // Every ChallengeZone in this scene, in a stable order all clients agree on (see GetZoneIndex).
        private List<ChallengeZone> m_Zones;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            // Every client listens; only the current master client acts on it (see HandleAnyStatDepleted),
            // so a client that becomes master later is already listening.
            if (onStatDepletedEvent != null)
            {
                onStatDepletedEvent.RegisterListener(HandleAnyStatDepleted);
            }

            m_LastIsPaused = NetIsPaused;
            m_LastIsBettingWindowActive = NetIsBettingWindowActive;
            m_LastBettingTimeRemaining = NetBettingTimeRemaining;
            m_LastBettorsKey = BuildBettorsKey();
        }

        public override void OnNetworkDespawn()
        {
            if (onStatDepletedEvent != null)
            {
                onStatDepletedEvent.UnregisterListener(HandleAnyStatDepleted);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void FixedUpdateNetwork()
        {
            if (!IsOwner || !NetIsBettingWindowActive) return;

            float remaining = NetBettingTimeRemaining - Runner.DeltaTime;
            if (remaining <= 0f)
            {
                NetBettingTimeRemaining = 0f;
                CloseBettingWindow();
            }
            else
            {
                NetBettingTimeRemaining = remaining;
            }
        }

        public override void Render()
        {
            DetectChanges();
        }

        #endregion

        #region Death Penalty

        /// <summary>
        /// Fires for every player's every stat depletion, on every client (see CoreStatsHandler - the stat
        /// events are raised wherever the change is seen). Only the master client applies the flat penalty
        /// and, if that player is currently mid-attempt on some challenge, counts the death against their
        /// death par too.
        /// </summary>
        private void HandleAnyStatDepleted(StatDepletedPayload payload)
        {
            if (!IsOwner) return;
            if (payload.statID != StatKeys.Health) return;

            GetPlayerScore(payload.playerId)?.AddScore(-deathPenalty);

            if (m_RunningAttemptsByOwner.TryGetValue(payload.playerId, out var zone))
            {
                zone.RecordDeath();
            }
        }

        #endregion

        #region Challenge Ownership & Betting Window

        /// <summary>
        /// Called by a ChallengeZone (via its ChallengeZoneTrigger) on the entering player's own machine
        /// when they walk (or jump) into that challenge's start or finish volume. Forwarded to the master
        /// client, which decides what happens - claiming the challenge, or resolving it, are both global
        /// decisions (only one betting window can run at a time), not the zone's alone.
        /// </summary>
        public void RequestZoneEntry(ChallengeZone zone, ChallengeZoneKind kind)
        {
            if (!IsSpawned || zone == null) return;

            int index = GetZoneIndex(zone);
            if (index < 0)
            {
                Debug.LogWarning($"[ChallengeManager] '{zone.name}' isn't one of this scene's challenge zones.", zone);
                return;
            }

            RequestZoneEntryRpc(index, kind);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RequestZoneEntryRpc(int zoneIndex, ChallengeZoneKind kind, RpcInfo info = default)
        {
            ChallengeZone zone = GetZone(zoneIndex);
            if (zone == null) return;

            HandleZoneEntry(zone, kind, SenderId(info));
        }

        /// <summary>
        /// The single decision point (master client only) for both halves of a challenge's lifecycle.
        /// </summary>
        public void HandleZoneEntry(ChallengeZone zone, ChallengeZoneKind kind, ulong clientId)
        {
            if (!IsOwner) return;

            if (kind == ChallengeZoneKind.Start)
            {
                TryClaimChallenge(zone, clientId);
            }
            else
            {
                if (zone.IsOwnedAndInProgressBy(clientId))
                {
                    ResolveAttempt(zone);
                }
            }
        }

        private void TryClaimChallenge(ChallengeZone zone, ulong challengerId)
        {
            if (NetIsBettingWindowActive) return; // only one betting window globally at a time
            if (zone.HasOwner) return; // first come, first served
            if (zone.HasCompletedThisRound) return; // once and only once per round - see ChallengeZone.HasCompletedThisRound
            if (m_RunningAttemptsByOwner.ContainsKey(challengerId)) return; // already mid-attempt elsewhere

            zone.Claim(challengerId);

            m_BettingWindowZone = zone;
            NetActiveChallengeName = zone.Definition != null ? zone.Definition.challengeName : zone.name;
            NetActiveChallengerId = (int)challengerId;
            NetBettingTimeRemaining = bettingWindowSeconds;
            NetBettorCount = 0;
            NetIsBettingWindowActive = true;
            NetIsPaused = true;
            DetectChanges();
        }

        private void CloseBettingWindow()
        {
            NetIsBettingWindowActive = false;
            NetIsPaused = false;

            if (m_BettingWindowZone != null)
            {
                m_BettingWindowZone.BeginAttempt();
                m_RunningAttemptsByOwner[m_BettingWindowZone.OwnerId] = m_BettingWindowZone;
            }

            m_BettingWindowZone = null;
            DetectChanges();
        }

        /// <summary>
        /// Called by CoreHUD when the local (non-challenger) player picks one of the three betting options.
        /// Sent to the master client, which records it. Declining is free; Success/Failure immediately
        /// costs the challenge's base points, win or lose.
        /// </summary>
        public void PlaceBet(ChallengeBetChoice choice)
        {
            if (!IsSpawned) return;
            PlaceBetRpc(choice);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void PlaceBetRpc(ChallengeBetChoice choice, RpcInfo info = default)
        {
            ulong bettorId = SenderId(info);

            if (!NetIsBettingWindowActive || m_BettingWindowZone == null) return;
            if (bettorId == ActiveChallengerClientId) return; // can't bet on yourself

            var bettorScore = GetPlayerScore(bettorId);
            if (bettorScore == null) return;

            bool wagered = m_BettingWindowZone.TryRecordBet(bettorId, choice, bettorScore.Score);
            if (!wagered) return;

            int cost = m_BettingWindowZone.Definition != null ? m_BettingWindowZone.Definition.basePoints : 0;
            bettorScore.AddScore(-cost);
            AddBettor(bettorId);
            DetectChanges();
        }

        #endregion

        #region Resolution

        private void ResolveAttempt(ChallengeZone zone)
        {
            ulong ownerId = zone.OwnerId;
            ChallengeDefinition definition = zone.Definition;
            var bets = new Dictionary<ulong, ChallengeBetChoice>(zone.Bets);

            (bool underDeathPar, bool underTimePar) = zone.CompleteAttempt();

            int reward = definition != null ? definition.basePoints : 0;
            if (definition != null)
            {
                if (underDeathPar) reward += definition.parDeathBonus;
                if (underTimePar) reward += definition.parTimeBonus;
            }
            GetPlayerScore(ownerId)?.AddScore(reward);

            ChallengeBetChoice winningChoice = (underDeathPar && underTimePar) ? ChallengeBetChoice.Success : ChallengeBetChoice.Failure;
            int payout = definition != null ? definition.basePoints * betPayoutMultiplier : 0;
            foreach (var bet in bets)
            {
                if (bet.Value != winningChoice) continue;
                GetPlayerScore(bet.Key)?.AddScore(payout);
            }

            m_RunningAttemptsByOwner.Remove(ownerId);
            zone.ResetToIdle();
        }

        #endregion

        #region Zones

        /// <summary>
        /// This zone's index in the scene's ChallengeZones, sorted by hierarchy path so every client
        /// computes the same index for the same zone (the scene is identical on every client).
        /// </summary>
        /// <summary>
        /// Forgets the cached zone list so it's rebuilt on next use. MatchLayout calls this on every client
        /// once it has placed the match's challenges (their zones don't exist when the scene first loads).
        /// </summary>
        public void RefreshZones()
        {
            m_Zones = null;
        }

        public int GetZoneIndex(ChallengeZone zone)
        {
            EnsureZones();
            return m_Zones.IndexOf(zone);
        }

        private ChallengeZone GetZone(int index)
        {
            EnsureZones();
            return index >= 0 && index < m_Zones.Count ? m_Zones[index] : null;
        }

        private void EnsureZones()
        {
            if (m_Zones != null) return;

            m_Zones = new List<ChallengeZone>();
            foreach (ChallengeZone zone in FindObjectsByType<ChallengeZone>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (zone.gameObject.scene == gameObject.scene) m_Zones.Add(zone);
            }

            m_Zones.Sort((a, b) => string.CompareOrdinal(GetHierarchyKey(a.transform), GetHierarchyKey(b.transform)));
        }

        /// <summary>Root-to-leaf sibling indices plus names, e.g. "0003:Map/0012:challenge_16_01" - unique and identical on every client.</summary>
        private static string GetHierarchyKey(Transform transform)
        {
            var parts = new List<string>();
            for (Transform t = transform; t != null; t = t.parent)
            {
                parts.Add($"{t.GetSiblingIndex():D4}:{t.name}");
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        #endregion

        #region Helpers

        private static PlayerScore GetPlayerScore(ulong clientId)
        {
            return NetworkPlayers.TryGetComponent(clientId, out PlayerScore score) ? score : null;
        }

        private ulong SenderId(RpcInfo info)
        {
            // An RPC the master client sends to itself runs locally - the sender is then this client.
            ulong id = NetworkPlayers.ToClientId(info.Source);
            return id != 0 ? id : NetworkPlayers.ToClientId(Runner.LocalPlayer);
        }

        private void AddBettor(ulong bettorId)
        {
            int count = Mathf.Clamp(NetBettorCount, 0, MaxBettors);
            for (int i = 0; i < count; i++)
            {
                if ((ulong)NetBettorIds[i] == bettorId) return;
            }
            if (count >= MaxBettors) return;

            NetBettorIds.Set(count, (int)bettorId);
            NetBettorCount = count + 1;
        }

        /// <summary>A cheap fingerprint of the challenger + bettor list, to notice when it changes without allocating.</summary>
        private int BuildBettorsKey()
        {
            if (!IsSpawned) return 0;
            unchecked
            {
                int count = Mathf.Clamp(NetBettorCount, 0, MaxBettors);
                int key = NetActiveChallengerId * 397 + count;
                for (int i = 0; i < count; i++)
                {
                    key = key * 31 + NetBettorIds[i];
                }
                return key;
            }
        }

        /// <summary>Raises the change events for any networked value that changed since this client last looked.</summary>
        private void DetectChanges()
        {
            if (!IsSpawned) return;

            bool isPaused = NetIsPaused;
            if (isPaused != m_LastIsPaused)
            {
                bool previous = m_LastIsPaused;
                m_LastIsPaused = isPaused;
                PausedChanged?.Invoke(previous, isPaused);
            }

            bool isActive = NetIsBettingWindowActive;
            if (isActive != m_LastIsBettingWindowActive)
            {
                bool previous = m_LastIsBettingWindowActive;
                m_LastIsBettingWindowActive = isActive;
                BettingWindowActiveChanged?.Invoke(previous, isActive);
            }

            float timeRemaining = NetBettingTimeRemaining;
            if (!Mathf.Approximately(timeRemaining, m_LastBettingTimeRemaining))
            {
                float previous = m_LastBettingTimeRemaining;
                m_LastBettingTimeRemaining = timeRemaining;
                BettingTimeRemainingChanged?.Invoke(previous, timeRemaining);
            }

            int bettorsKey = BuildBettorsKey();
            if (bettorsKey != m_LastBettorsKey)
            {
                m_LastBettorsKey = bettorsKey;
                ActiveBettorsChanged?.Invoke();
            }
        }

        #endregion
    }
}
