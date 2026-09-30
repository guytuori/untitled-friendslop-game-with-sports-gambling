using System;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A single, shared round countdown - not per-player. Lives on one GameObject placed directly in the
    /// gameplay scene (see GameRulesSetup, which places it), rather than a prefab spawned at runtime, since
    /// there's always exactly one of these for the whole session.
    ///
    /// Networking (Photon Fusion, Shared mode): a scene object owned by the session's master client
    /// ("Is Master Client Object" on its NetworkObject), which counts it down; everyone else just reads it.
    /// If the master client leaves, the next one takes over and carries on counting.
    ///
    /// Counts down from the session's Round Time rule (Host Game screen; GameRulesConfig.roundDurationSeconds
    /// when the scene is played directly) to 0 and then simply stops - reaching 0 has no gameplay effect
    /// today. The remaining time is reserved for a future "finish faster, earn a bigger bonus" scoring rule
    /// once rounds have an actual end condition.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class RoundTimer : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        private const float DefaultRoundDurationSeconds = 120f;

        [Header("Configuration")]
        [Tooltip("Round duration when the scene is played directly (no Host Game rules). Falls back to a hardcoded default if left unassigned.")]
        [SerializeField] private GameRulesConfig gameRulesConfig;

        /// <summary>
        /// The single RoundTimer for the current scene, if one has been placed and has awoken. CoreHUD reads this
        /// to display the countdown.
        /// </summary>
        public static RoundTimer Instance { get; private set; }

        [Networked] private float NetTimeRemaining { get; set; }

        private float m_LastTimeRemaining;

        /// <summary>
        /// Seconds left in the round. Everyone can read it; only the master client counts it down. 0 until spawned.
        /// </summary>
        public float TimeRemaining => IsSpawned ? NetTimeRemaining : 0f;

        /// <summary>Raised on every client whenever the remaining time changes (previous value, new value).</summary>
        public event Action<float, float> TimeRemainingChanged;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner)
            {
                NetTimeRemaining = GetRoundDuration();
            }
            m_LastTimeRemaining = NetTimeRemaining;
            TimeRemainingChanged?.Invoke(m_LastTimeRemaining, m_LastTimeRemaining);
        }

        public override void FixedUpdateNetwork()
        {
            if (!IsOwner) return;
            if (NetTimeRemaining <= 0f) return;

            NetTimeRemaining = Mathf.Max(0f, NetTimeRemaining - Runner.DeltaTime);
        }

        public override void Render()
        {
            if (!IsSpawned) return;

            float current = NetTimeRemaining;
            if (Mathf.Approximately(current, m_LastTimeRemaining)) return;

            float previous = m_LastTimeRemaining;
            m_LastTimeRemaining = current;
            TimeRemainingChanged?.Invoke(previous, current);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        #endregion

        #region Private Methods

        private float GetRoundDuration()
        {
            if (FusionSessionService.HasInstance && FusionSessionService.Instance.InGame && !FusionSessionService.Instance.IsDevSession)
            {
                return FusionSessionService.Instance.SessionRules.RoundTimeSeconds;
            }
            return gameRulesConfig != null ? gameRulesConfig.roundDurationSeconds : DefaultRoundDurationSeconds;
        }

        #endregion
    }
}
