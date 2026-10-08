using System;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Tracks one player's score - the currency this game's wagering and gambling mechanics will spend and win.
    /// Every connected player has one of these on their player object; CoreHUD reads every connected player's copy
    /// to build the scoreboard.
    ///
    /// Networking (Photon Fusion, Shared mode): the score lives on the player's own avatar, so the player's
    /// own client (its State Authority) is the one that writes it. Anyone else - normally the master
    /// client's ChallengeManager, which decides payouts and penalties - changes it through
    /// <see cref="AddScore"/> / <see cref="SetScore"/>, which forward the change to the owner as an RPC.
    /// Every client can read everyone's current score, which is exactly what the scoreboard needs.
    ///
    /// The starting score is the session's Starting Points rule from the Host Game screen (falling back to
    /// <see cref="gameRulesConfig"/> when the scene is played directly for testing), plus - from round 2 on -
    /// the share of last round's final score this player keeps (MatchProgress.LocalCarryOverPoints).
    /// </summary>
    public class PlayerScore : CoreNetworkBehaviour, IPlayerRequiredComponent
    {
        #region Fields & Properties

        private const int DefaultStartingScore = 1000;

        [Header("Configuration")]
        [Tooltip("Starting score when the scene is played directly (no Host Game rules). Falls back to a hardcoded default if left unassigned.")]
        [SerializeField] private GameRulesConfig gameRulesConfig;

        [Networked] private int NetScore { get; set; }

        private int m_LastScore;

        /// <summary>
        /// The player's current score. Everyone can read it (for the scoreboard); 0 until spawned.
        /// </summary>
        public int Score => IsSpawned ? NetScore : 0;

        /// <summary>Raised on every client whenever this player's score changes (previous value, new value).</summary>
        public event Action<int, int> ScoreChanged;

        #endregion

        #region Network Lifecycle

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                NetScore = GetStartingScore();
            }
            m_LastScore = NetScore;
        }

        public override void Render()
        {
            DetectChanges();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Sets this player's score. Intended for the wagering/gambling system (ChallengeManager, on the
        /// master client). Applied by the player's own client - directly if that's this client, otherwise
        /// through an RPC.
        /// </summary>
        public void SetScore(int newScore)
        {
            if (!IsSpawned) return;

            if (IsOwner)
            {
                NetScore = newScore;
                DetectChanges();
            }
            else
            {
                SetScoreRpc(newScore);
            }
        }

        /// <summary>
        /// Adds (or subtracts, with a negative amount) to this player's score. Intended for the
        /// wagering/gambling system and end-of-round bonus points (ChallengeManager, on the master client).
        /// Applied by the player's own client - directly if that's this client, otherwise through an RPC.
        /// </summary>
        public void AddScore(int amount)
        {
            if (!IsSpawned || amount == 0) return;

            if (IsOwner)
            {
                NetScore += amount;
                DetectChanges();
            }
            else
            {
                AddScoreRpc(amount);
            }
        }

        #endregion

        #region RPCs

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void SetScoreRpc(int newScore)
        {
            NetScore = newScore;
            DetectChanges();
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void AddScoreRpc(int amount)
        {
            NetScore += amount;
            DetectChanges();
        }

        #endregion

        #region Private Methods

        private int GetStartingScore()
        {
            if (PracticeMode.IsActive) return 0; // practice: the score only counts pickups

            int startingPoints = MatchRules.Get(gameRulesConfig).StartingPoints;
            return startingPoints + MatchProgress.LocalCarryOverPoints;
        }

        private void DetectChanges()
        {
            if (!IsSpawned) return;

            int current = NetScore;
            if (current == m_LastScore) return;

            int previous = m_LastScore;
            m_LastScore = current;
            ScoreChanged?.Invoke(previous, current);
        }

        #endregion
    }
}
