using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Collections;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The single, server-authoritative brain for every <see cref="ChallengeZone"/> in the map, plus the
    /// flat death penalty that applies regardless of any challenge. Lives on one GameObject placed
    /// directly in the gameplay scene (see ChallengeSystemSetup), the same way RoundTimer does.
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
    /// <see cref="ActiveBettorClientIds"/> to draw the betting overlay, and calls <see cref="PlaceBetRpc"/>
    /// when a player picks an option. <see cref="ChallengePauseGate"/> (one per player) reads
    /// <see cref="IsPaused"/> to freeze/unfreeze that player's own movement input.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class ChallengeManager : NetworkBehaviour
    {
        #region Fields & Properties

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

        /// <summary>True while every player should be frozen in place (the betting window). Everyone reads it; only the server sets it.</summary>
        public NetworkVariable<bool> IsPaused = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>True while a betting window is actively counting down.</summary>
        public NetworkVariable<bool> IsBettingWindowActive = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Seconds left in the current betting window (0 when none is active).</summary>
        public NetworkVariable<float> BettingTimeRemaining = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>The name of the challenge currently up for betting (only meaningful while a window is active).</summary>
        public NetworkVariable<FixedString64Bytes> ActiveChallengeName = new NetworkVariable<FixedString64Bytes>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>The client ID of the player who just claimed the active challenge (only meaningful while a window is active).</summary>
        public NetworkVariable<ulong> ActiveChallengerClientId = new NetworkVariable<ulong>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>Client IDs of everyone who has placed a real wager (not a decline) during the current window.</summary>
        public NetworkList<ulong> ActiveBettorClientIds = new NetworkList<ulong>(
            default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server-only bookkeeping - never networked directly, only reflected through the NetworkVariables above.
        private ChallengeZone m_BettingWindowZone;
        private readonly Dictionary<ulong, ChallengeZone> m_RunningAttemptsByOwner = new Dictionary<ulong, ChallengeZone>();

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer && onStatDepletedEvent != null)
            {
                onStatDepletedEvent.RegisterListener(HandleAnyStatDepleted);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && onStatDepletedEvent != null)
            {
                onStatDepletedEvent.UnregisterListener(HandleAnyStatDepleted);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || !IsBettingWindowActive.Value) return;

            float remaining = BettingTimeRemaining.Value - Time.deltaTime;
            if (remaining <= 0f)
            {
                BettingTimeRemaining.Value = 0f;
                CloseBettingWindow();
            }
            else
            {
                BettingTimeRemaining.Value = remaining;
            }
        }

        #endregion

        #region Death Penalty

        /// <summary>
        /// Fires once, server-side, for every player's every stat depletion (see CoreStatsHandler -
        /// BroadcastStatChange runs on every machine including the server, so this needs no extra RPC
        /// plumbing to be reliably server-authoritative). Applies the flat penalty and, if that player is
        /// currently mid-attempt on some challenge, counts the death against their death par too.
        /// </summary>
        private void HandleAnyStatDepleted(StatDepletedPayload payload)
        {
            if (!IsServer) return;
            if (payload.statID != StatKeys.Health) return;

            GetPlayerScore(payload.playerId)?.ServerAddScore(-deathPenalty);

            if (m_RunningAttemptsByOwner.TryGetValue(payload.playerId, out var zone))
            {
                zone.RecordDeath();
            }
        }

        #endregion

        #region Challenge Ownership & Betting Window

        /// <summary>
        /// Called by a ChallengeZone's RequestEnterRpc (itself server-only) whenever any player enters that
        /// specific challenge's start or finish volume. This is the single decision point for both halves
        /// of a challenge's lifecycle.
        /// </summary>
        public void HandleZoneEntry(ChallengeZone zone, ChallengeZoneKind kind, ulong clientId)
        {
            if (!IsServer) return;

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
            if (IsBettingWindowActive.Value) return; // only one betting window globally at a time
            if (zone.HasOwner) return; // first come, first served
            if (m_RunningAttemptsByOwner.ContainsKey(challengerId)) return; // already mid-attempt elsewhere

            zone.Claim(challengerId);

            m_BettingWindowZone = zone;
            ActiveChallengeName.Value = zone.Definition != null ? zone.Definition.challengeName : zone.name;
            ActiveChallengerClientId.Value = challengerId;
            BettingTimeRemaining.Value = bettingWindowSeconds;
            ActiveBettorClientIds.Clear();
            IsBettingWindowActive.Value = true;
            IsPaused.Value = true;
        }

        private void CloseBettingWindow()
        {
            IsBettingWindowActive.Value = false;
            IsPaused.Value = false;

            if (m_BettingWindowZone != null)
            {
                m_BettingWindowZone.BeginAttempt();
                m_RunningAttemptsByOwner[m_BettingWindowZone.OwnerId] = m_BettingWindowZone;
            }

            m_BettingWindowZone = null;
        }

        /// <summary>
        /// Called by CoreHUD (via an Rpc from the betting player's own client) when a non-challenger picks
        /// one of the three betting options. Declining is free; Success/Failure immediately costs the
        /// challenge's base points, win or lose.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void PlaceBetRpc(ChallengeBetChoice choice, RpcParams rpcParams = default)
        {
            ulong bettorId = rpcParams.Receive.SenderClientId;

            if (!IsBettingWindowActive.Value || m_BettingWindowZone == null) return;
            if (bettorId == ActiveChallengerClientId.Value) return; // can't bet on yourself

            var bettorScore = GetPlayerScore(bettorId);
            if (bettorScore == null) return;

            bool wagered = m_BettingWindowZone.TryRecordBet(bettorId, choice, bettorScore.Score.Value);
            if (!wagered) return;

            int cost = m_BettingWindowZone.Definition != null ? m_BettingWindowZone.Definition.basePoints : 0;
            bettorScore.ServerAddScore(-cost);
            if (!ActiveBettorClientIds.Contains(bettorId))
            {
                ActiveBettorClientIds.Add(bettorId);
            }
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
            GetPlayerScore(ownerId)?.ServerAddScore(reward);

            ChallengeBetChoice winningChoice = (underDeathPar && underTimePar) ? ChallengeBetChoice.Success : ChallengeBetChoice.Failure;
            int payout = definition != null ? definition.basePoints * betPayoutMultiplier : 0;
            foreach (var bet in bets)
            {
                if (bet.Value != winningChoice) continue;
                GetPlayerScore(bet.Key)?.ServerAddScore(payout);
            }

            m_RunningAttemptsByOwner.Remove(ownerId);
            zone.ResetToIdle();
        }

        #endregion

        #region Helpers

        private PlayerScore GetPlayerScore(ulong clientId)
        {
            if (NetworkManager.Singleton == null || NetworkManager.Singleton.SpawnManager == null) return null;

            var playerObject = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
            if (playerObject != null && playerObject.TryGetComponent(out PlayerScore score))
            {
                return score;
            }
            return null;
        }

        #endregion
    }
}
