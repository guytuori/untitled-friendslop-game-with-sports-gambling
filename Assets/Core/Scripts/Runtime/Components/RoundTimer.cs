using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A single, shared, server-authoritative round countdown - not per-player. Lives on one GameObject placed
    /// directly in the gameplay scene (see GameRulesSetup, which places it), rather than a prefab spawned at
    /// runtime, since there's always exactly one of these for the whole session.
    ///
    /// Counts down from GameRulesConfig.roundDurationSeconds to 0 and then simply stops - reaching 0 has no
    /// gameplay effect today. The remaining time is reserved for a future "finish faster, earn a bigger bonus"
    /// scoring rule once rounds have an actual end condition.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class RoundTimer : NetworkBehaviour
    {
        #region Fields & Properties

        private const float DefaultRoundDurationSeconds = 120f;

        [Header("Configuration")]
        [Tooltip("Where the round duration comes from. Falls back to a hardcoded default if left unassigned.")]
        [SerializeField] private GameRulesConfig gameRulesConfig;

        /// <summary>
        /// The single RoundTimer for the current scene, if one has been placed and has awoken. CoreHUD reads this
        /// to display the countdown.
        /// </summary>
        public static RoundTimer Instance { get; private set; }

        /// <summary>
        /// Seconds left in the round. Everyone can read it; only the server counts it down.
        /// </summary>
        public NetworkVariable<float> TimeRemaining = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            TimeRemaining.Value = gameRulesConfig != null ? gameRulesConfig.roundDurationSeconds : DefaultRoundDurationSeconds;
        }

        private void Update()
        {
            if (!IsSpawned || !IsServer) return;
            if (TimeRemaining.Value <= 0f) return;

            TimeRemaining.Value = Mathf.Max(0f, TimeRemaining.Value - Time.deltaTime);
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        #endregion
    }
}
