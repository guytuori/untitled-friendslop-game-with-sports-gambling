using System;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Defines the lifecycle states a player can be in.
    /// </summary>
    public enum PlayerLifeState : byte
    {
        /// <summary>
        /// The state when the player object first spawns into the scene.
        /// </summary>
        InitialSpawn,

        /// <summary>
        /// The state when the player has been defeated/killed.
        /// </summary>
        Eliminated,

        /// <summary>
        /// The state when the player returns to life after being eliminated.
        /// </summary>
        Respawned
    }

    /// <summary>
    /// Manages networked state for the player, such as their name and lifecycle state.
    /// This component separates state data from the management logic in CorePlayerManager.
    /// The owning client (State Authority) writes the values directly; anyone else asks the owner to
    /// change them through an RPC.
    /// </summary>
    public class CorePlayerState : CoreNetworkBehaviour
    {
        #region Fields & Properties

        [Tooltip("Global ScriptableObject event raised when player state changes.")]
        [SerializeField] private PlayerStateEvent onPlayerStateChangedGlobal;

        // Everyone can read, only the owner (State Authority) writes.
        [Networked] private NetworkString<_64> NetPlayerName { get; set; }
        [Networked] private PlayerLifeState NetLifeState { get; set; }

        // Last values seen by this client - used to raise the change events (see DetectChanges).
        private string m_LastName = string.Empty;
        private PlayerLifeState m_LastLifeState = PlayerLifeState.InitialSpawn;

        // Set before the object finished spawning; applied in OnNetworkSpawn.
        private string m_PendingName;

        /// <summary>
        /// Gets the current player name string.
        /// </summary>
        public string PlayerName => IsSpawned ? NetPlayerName.ToString() : string.Empty;

        /// <summary>
        /// Gets the current Life State.
        /// </summary>
        public PlayerLifeState LifeState => IsSpawned ? NetLifeState : PlayerLifeState.InitialSpawn;

        /// <summary>
        /// Helper to check if the player is currently considered "Active" (Not eliminated).
        /// </summary>
        public bool IsActive => LifeState == PlayerLifeState.InitialSpawn || LifeState == PlayerLifeState.Respawned;

        #endregion

        #region Events

        /// <summary>
        /// Event raised when the networked player name changes.
        /// </summary>
        public event Action<string> OnNameChanged;

        /// <summary>
        /// Event raised when the player's life state changes (InitialSpawn -> Eliminated -> Respawned).
        /// </summary>
        public event Action<PlayerLifeState> OnLifeStateChanged;

        #endregion

        #region Network Lifecycle

        public override void OnNetworkSpawn()
        {
            if (IsOwner && !string.IsNullOrEmpty(m_PendingName))
            {
                NetPlayerName = m_PendingName;
            }
            m_PendingName = null;

            m_LastName = NetPlayerName.ToString();
            m_LastLifeState = NetLifeState;

            // For late-joining clients, the values may already be set
            // Trigger events immediately to ensure subscribers receive the current state
            if (!string.IsNullOrEmpty(m_LastName))
            {
                OnNameChanged?.Invoke(m_LastName);
            }

            // Always broadcast initial life state to ensure all systems are synchronized
            OnLifeStateChanged?.Invoke(m_LastLifeState);
        }

        public override void Render()
        {
            DetectChanges();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets the player name.
        /// If called by the Owner, it sets the value directly.
        /// If called by non-Owner, it sends an RPC to the Owner to set it.
        /// </summary>
        /// <param name="newName">The new name to set for the player.</param>
        public void SetPlayerName(string newName)
        {
            if (string.IsNullOrEmpty(newName)) return;

            if (!IsSpawned)
            {
                m_PendingName = newName;
                return;
            }

            if (IsOwner)
            {
                NetPlayerName = newName;
                DetectChanges();
            }
            else
            {
                // Other clients must request the owner to set the value via RPC
                SetPlayerNameRpc(newName);
            }
        }

        /// <summary>
        /// Sets the player's life state.
        /// If called by the Owner, it sets the value directly.
        /// If called by non-Owner, it sends an RPC to the Owner to set it.
        /// </summary>
        /// <param name="newState">The new state to transition to.</param>
        public void SetLifeState(PlayerLifeState newState)
        {
            if (!IsSpawned) return;

            if (IsOwner)
            {
                NetLifeState = newState;
                DetectChanges();
            }
            else
            {
                // Other clients must request the owner to set the value via RPC
                SetLifeStateRpc(newState);
            }
        }

        #endregion

        #region RPCs

        /// <summary>
        /// RPC sent to the owner to set the player name.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void SetPlayerNameRpc(string newName)
        {
            NetPlayerName = newName;
            DetectChanges();
        }

        /// <summary>
        /// RPC sent to the owner to set the life state.
        /// </summary>
        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void SetLifeStateRpc(PlayerLifeState newState)
        {
            NetLifeState = newState;
            DetectChanges();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Raises OnNameChanged / OnLifeStateChanged for any value that changed since this client last
        /// looked. Runs every frame (Render) on every client, and immediately after the owner writes a new
        /// value, so the owner's own listeners react in the same frame (like NGO's OnValueChanged did).
        /// </summary>
        private void DetectChanges()
        {
            if (!IsSpawned) return;

            string currentName = NetPlayerName.ToString();
            if (currentName != m_LastName)
            {
                m_LastName = currentName;
                OnNameChanged?.Invoke(currentName);
            }

            PlayerLifeState currentState = NetLifeState;
            if (currentState != m_LastLifeState)
            {
                PlayerLifeState oldState = m_LastLifeState;
                m_LastLifeState = currentState;

                // Trigger local C# event for components on this GameObject (e.g., abilities, visual effects)
                OnLifeStateChanged?.Invoke(currentState);

                // Trigger global ScriptableObject event for systems elsewhere in the scene
                // This allows managers, UI, and other decoupled systems to respond to state changes
                if (onPlayerStateChangedGlobal != null)
                {
                    onPlayerStateChangedGlobal.Raise(new PlayerStatePayload { playerId = OwnerClientId, newState = currentState, oldState = oldState });
                }
            }
        }

        #endregion
    }
}
