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
    /// the share of last round's final score this player keeps (MatchProgress.LocalCarryOverPoints), minus
    /// the price of anything they bought in the between-rounds item shop (MatchProgress.LocalShopSpend).
    ///
    /// LIVES (added 2026-10-25): this also holds the player's lives and the 1-Up's death shield, networked so
    /// everyone can see them. Lives come from the Lives rule (-1 = unlimited; always unlimited in practice) and
    /// reset every round. The owner handles its own deaths (<see cref="HandleLocalDeath"/>, called by
    /// GameManager): the shield (if up) is used up instead of the death penalty, a life is lost if lives are
    /// limited, and running out means sitting out the rest of the round (RoundTimer.ReportLocalPlayerOut).
    /// </summary>
    public class PlayerScore : CoreNetworkBehaviour, IPlayerRequiredComponent
    {
        #region Fields & Properties

        private const int DefaultStartingScore = 1000;

        [Header("Configuration")]
        [Tooltip("Starting score when the scene is played directly (no Host Game rules). Falls back to a hardcoded default if left unassigned.")]
        [SerializeField] private GameRulesConfig gameRulesConfig;

        [Networked] private int NetScore { get; set; }
        [Networked] private int NetLives { get; set; }
        [Networked] private NetworkBool NetDeathShield { get; set; }

        private int m_LastScore;

        /// <summary>
        /// The player's current score. Everyone can read it (for the scoreboard); 0 until spawned.
        /// </summary>
        public int Score => IsSpawned ? NetScore : 0;

        /// <summary>Raised on every client whenever this player's score changes (previous value, new value).</summary>
        public event Action<int, int> ScoreChanged;

        private int m_LastLives = int.MinValue;
        private bool m_LastShield;

        /// <summary>Lives left (-1 = unlimited).</summary>
        public int Lives => IsSpawned ? NetLives : -1;

        public bool UnlimitedLives => Lives < 0;

        /// <summary>The 1-Up's shield (unlimited lives only): the next death costs no points.</summary>
        public bool HasDeathShield => IsSpawned && NetDeathShield;

        /// <summary>Raised on every client when this player's lives or shield change (old lives, new lives).</summary>
        public event Action<int, int> LivesChanged;

        #endregion

        #region Network Lifecycle

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                NetScore = GetStartingScore();
                NetLives = PracticeMode.IsActive ? -1 : MatchRules.Get(gameRulesConfig).Lives; // every round starts fresh
                if (NetLives == 0) NetLives = -1;
                NetDeathShield = false;
            }
            m_LastScore = NetScore;
            m_LastLives = NetLives;
            m_LastShield = NetDeathShield;
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

        /// <summary>
        /// 1-Up (owner only): a life if lives are limited, otherwise the death shield (the next death costs no
        /// points). Returns true for a life, false for the shield.
        /// </summary>
        public bool GrantOneUp()
        {
            if (!IsSpawned || !IsOwner) return false;
            if (NetLives >= 0)
            {
                NetLives = NetLives + 1;
                DetectChanges();
                return true;
            }
            NetDeathShield = true;
            DetectChanges();
            return false;
        }

        /// <summary>
        /// This player just died (owner only - GameManager calls it): uses up the death shield instead of the
        /// death penalty, otherwise takes the Death Penalty rule off the score (All = every point; none in
        /// practice), and loses a life if lives are limited. Returns true if that was the last life.
        /// </summary>
        public bool HandleLocalDeath()
        {
            if (!IsSpawned || !IsOwner) return false;

            if (NetDeathShield)
            {
                NetDeathShield = false;
                Debug.Log("[Lives] The 1-Up shield took the hit - no points lost for that death.", this);
            }
            else if (!PracticeMode.IsActive)
            {
                int penalty = MatchRules.Get(gameRulesConfig).DeathPenalty;
                if (penalty < 0) penalty = Mathf.Max(0, NetScore); // All
                if (penalty > 0) NetScore -= penalty;
            }

            bool outOfLives = false;
            if (NetLives > 0)
            {
                NetLives = NetLives - 1;
                outOfLives = NetLives == 0;
            }
            DetectChanges();
            return outOfLives;
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
            // Minus whatever this player spent in the between-rounds item shop (never below 0).
            return Mathf.Max(0, startingPoints + MatchProgress.LocalCarryOverPoints - MatchProgress.TakeShopSpend());
        }

        private void DetectChanges()
        {
            if (!IsSpawned) return;

            int lives = NetLives;
            bool shield = NetDeathShield;
            if (lives != m_LastLives || shield != m_LastShield)
            {
                int previousLives = m_LastLives;
                m_LastLives = lives;
                m_LastShield = shield;
                LivesChanged?.Invoke(previousLives, lives);
            }

            int current = NetScore;
            if (current == m_LastScore) return;

            int previous = m_LastScore;
            m_LastScore = current;
            ScoreChanged?.Invoke(previous, current);
        }

        #endregion
    }
}
