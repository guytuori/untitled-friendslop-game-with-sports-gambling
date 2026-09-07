using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One placed challenge - a start area and a finish line (see <see cref="ChallengeZoneTrigger"/>, which
    /// forwards entries into this component) plus everything about the CURRENT attempt against it: who
    /// owns it, how many times they've died since starting, and any bets placed during its betting window.
    ///
    /// All of the actual rules (only one betting window globally at a time, the countdown, applying score,
    /// paying out bets) live in <see cref="ChallengeManager"/> - this component is just the per-challenge
    /// data and the entry point clients call into. Splitting it this way means adding a brand new challenge
    /// to the map grid is just: place a copy of a challenge map piece, run
    /// "Friendslop > Split Selected Map Collider By Material" (which adds one of these and wires up its
    /// start/finish triggers automatically - see MapColliderSetup), then tweak its ChallengeDefinition
    /// asset. No code changes, no changes to ChallengeManager.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class ChallengeZone : NetworkBehaviour
    {
        #region Fields & Properties

        [Header("Configuration")]
        [Tooltip("Name, points, and pars for this specific challenge. Create one asset per challenge map piece.")]
        [SerializeField] private ChallengeDefinition definition;

        private bool m_HasOwner;
        private ulong m_OwnerId;
        private bool m_AttemptInProgress;
        private float m_AttemptStartTime;
        private int m_DeathsDuringAttempt;
        private readonly Dictionary<ulong, ChallengeBetChoice> m_Bets = new Dictionary<ulong, ChallengeBetChoice>();

        /// <summary>This challenge's parameters (name, points, pars). Never null in normal use.</summary>
        public ChallengeDefinition Definition => definition;

        /// <summary>Whether anyone currently owns (has claimed) this challenge.</summary>
        public bool HasOwner => m_HasOwner;

        /// <summary>The client ID of the current owner. Only meaningful when <see cref="HasOwner"/> is true.</summary>
        public ulong OwnerId => m_OwnerId;

        /// <summary>Whether the owner's timed attempt (post-betting-window) is currently running.</summary>
        public bool AttemptInProgress => m_AttemptInProgress;

        /// <summary>Every bet placed during this attempt's betting window, keyed by bettor client ID.</summary>
        public IReadOnlyDictionary<ulong, ChallengeBetChoice> Bets => m_Bets;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            if (definition == null)
            {
                Debug.LogWarning($"[ChallengeZone] '{gameObject.name}' has no ChallengeDefinition assigned - it will use fallback zeroed values.", this);
            }
        }

        #endregion

        #region Server API (called only from ChallengeManager, which is itself IsServer-gated)

        /// <summary>Marks this challenge as owned by the given player. Does not start the timed attempt yet.</summary>
        public void Claim(ulong challengerId)
        {
            m_HasOwner = true;
            m_OwnerId = challengerId;
            m_AttemptInProgress = false;
            m_DeathsDuringAttempt = 0;
            m_Bets.Clear();
        }

        /// <summary>Called once the betting window closes - starts the owner's timed, death-counted attempt.</summary>
        public void BeginAttempt()
        {
            m_AttemptInProgress = true;
            m_AttemptStartTime = Time.time;
        }

        /// <summary>Whether the given player currently owns this challenge and is mid-attempt on it.</summary>
        public bool IsOwnedAndInProgressBy(ulong clientId)
        {
            return m_HasOwner && m_AttemptInProgress && m_OwnerId == clientId;
        }

        /// <summary>Records one death against the current attempt. No-ops if there's no attempt in progress.</summary>
        public void RecordDeath()
        {
            if (!m_AttemptInProgress) return;
            m_DeathsDuringAttempt++;
        }

        /// <summary>
        /// Records a bet from a non-owner player. Returns true if this was a real wager (Success/Failure)
        /// that should be announced and charged; false for a decline, a repeat bet, or one the bettor
        /// couldn't afford (caller is responsible for actually deducting the wager on a true result).
        /// </summary>
        public bool TryRecordBet(ulong bettorId, ChallengeBetChoice choice, int currentBettorScore)
        {
            if (m_Bets.ContainsKey(bettorId)) return false;

            if (choice == ChallengeBetChoice.Decline)
            {
                m_Bets[bettorId] = choice;
                return false;
            }

            int cost = definition != null ? definition.basePoints : 0;
            if (currentBettorScore < cost) return false;

            m_Bets[bettorId] = choice;
            return true;
        }

        /// <summary>
        /// Ends the attempt and reports whether each par was beaten, based on time elapsed since
        /// <see cref="BeginAttempt"/> and deaths recorded via <see cref="RecordDeath"/>.
        /// </summary>
        public (bool underDeathPar, bool underTimePar) CompleteAttempt()
        {
            float elapsed = Time.time - m_AttemptStartTime;
            int parDeaths = definition != null ? definition.parDeaths : 0;
            float parTime = definition != null ? definition.parTimeSeconds : 0f;

            bool underDeathPar = m_DeathsDuringAttempt < parDeaths;
            bool underTimePar = elapsed < parTime;
            return (underDeathPar, underTimePar);
        }

        /// <summary>Clears ownership and attempt state so the next player can claim this challenge.</summary>
        public void ResetToIdle()
        {
            m_HasOwner = false;
            m_OwnerId = 0;
            m_AttemptInProgress = false;
            m_DeathsDuringAttempt = 0;
            m_Bets.Clear();
        }

        #endregion

        #region Client Entry Point

        /// <summary>
        /// Called by <see cref="ChallengeZoneTrigger"/> on the entering player's own machine when they walk
        /// (or jump) into this challenge's start or finish volume. Routed to the server, which asks
        /// <see cref="ChallengeManager"/> to decide what happens - claiming the challenge, or resolving it,
        /// are both global decisions (only one betting window can run at a time), not this zone's alone.
        /// </summary>
        [Rpc(SendTo.Server)]
        public void RequestEnterRpc(ChallengeZoneKind kind, RpcParams rpcParams = default)
        {
            ulong enteringClientId = rpcParams.Receive.SenderClientId;
            if (ChallengeManager.Instance != null)
            {
                ChallengeManager.Instance.HandleZoneEntry(this, kind, enteringClientId);
            }
        }

        #endregion
    }
}
