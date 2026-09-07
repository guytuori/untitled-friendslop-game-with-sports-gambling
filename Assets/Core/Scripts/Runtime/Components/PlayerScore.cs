using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Tracks one player's score - the currency this game's wagering and gambling mechanics will spend and win.
    /// Every connected player has one of these on their player object; CoreHUD reads every connected player's copy
    /// to build the scoreboard.
    ///
    /// Unlike most per-player state in this project (CorePlayerState's name/life-state, CoreStatsHandler's
    /// health/stamina), Score is deliberately Server-write rather than Owner-write. Health and stamina only ever
    /// need to be trusted by their own owner - a client cheating its own stamina doesn't affect anyone else. Score
    /// is different: it's a currency that gets wagered and won *between* players, so an Owner-write score would
    /// let a compromised client simply grant itself infinite winnings. Only the server may change it; every client
    /// (Everyone read permission) can still see everyone's current score, which is exactly what the scoreboard needs.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PlayerScore : NetworkBehaviour
    {
        #region Fields & Properties

        private const int DefaultStartingScore = 1000;

        [Header("Configuration")]
        [Tooltip("Where the starting score comes from. Falls back to a hardcoded default if left unassigned.")]
        [SerializeField] private GameRulesConfig gameRulesConfig;

        /// <summary>
        /// The player's current score. Everyone can read it (for the scoreboard); only the server can write it.
        /// </summary>
        public NetworkVariable<int> Score = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        #endregion

        #region Unity & Network Lifecycle

        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            Score.Value = gameRulesConfig != null ? gameRulesConfig.startingScore : DefaultStartingScore;
        }

        #endregion

        #region Server API

        /// <summary>
        /// Server-only: sets this player's score directly. Intended for the future wagering/gambling system -
        /// never call this from client code.
        /// </summary>
        public void ServerSetScore(int newScore)
        {
            if (!IsServer)
            {
                Debug.LogError("PlayerScore.ServerSetScore was called on a non-server instance. Ignoring.", this);
                return;
            }

            Score.Value = newScore;
        }

        /// <summary>
        /// Server-only: adds (or subtracts, with a negative amount) to this player's score. Intended for the
        /// future wagering/gambling system and end-of-round bonus points - never call this from client code.
        /// </summary>
        public void ServerAddScore(int amount)
        {
            if (!IsServer)
            {
                Debug.LogError("PlayerScore.ServerAddScore was called on a non-server instance. Ignoring.", this);
                return;
            }

            Score.Value += amount;
        }

        #endregion
    }
}
