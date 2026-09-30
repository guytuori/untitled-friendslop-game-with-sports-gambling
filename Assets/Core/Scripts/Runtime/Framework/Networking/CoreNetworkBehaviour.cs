using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Base class for every networked gameplay component (Photon Fusion 2, Shared mode).
    ///
    /// The gameplay code was written for Netcode for GameObjects' owner-authoritative model (Distributed
    /// Authority), which maps almost one-to-one onto Fusion's Shared mode: each player's own client has
    /// State Authority over their player object (moves it, writes its [Networked] state), and scene
    /// objects such as RoundTimer / ChallengeManager are owned by the Shared-mode master client, which
    /// takes over the role the NGO "server/session owner" had. This base class keeps the familiar NGO
    /// names so the gameplay scripts read the same as before:
    ///
    ///   IsOwner / HasAuthority - this client has State Authority over the object (own player, or the
    ///                            master client for scene objects). Safe to call before spawn (false).
    ///   IsSpawned              - Fusion has spawned the object and its [Networked] state is readable.
    ///   OwnerClientId          - the owning player's id as a ulong (PlayerRef.PlayerId), the id type
    ///                            every GameEvent payload in the project uses.
    ///   OnNetworkSpawn/Despawn - called from Fusion's Spawned()/Despawned().
    ///
    /// Reading a [Networked] property before the object is spawned throws, so public accessors on
    /// derived classes check IsSpawned first.
    /// </summary>
    public abstract class CoreNetworkBehaviour : NetworkBehaviour
    {
        private bool m_IsSpawned;
        private bool m_IsDespawning;
        private bool m_LastKnownOwner;
        private ulong m_LastKnownOwnerId;

        /// <summary>True between Fusion's Spawned() and Despawned() - [Networked] state is only readable in that window.</summary>
        public bool IsSpawned => m_IsSpawned;

        /// <summary>This client has State Authority over the object. Stays readable (last known value) during OnNetworkDespawn.</summary>
        public bool IsOwner
        {
            get
            {
                if (m_IsSpawned && Object != null && Object.IsValid)
                {
                    m_LastKnownOwner = Object.HasStateAuthority;
                    return m_LastKnownOwner;
                }
                return m_IsDespawning && m_LastKnownOwner;
            }
        }

        /// <summary>Same as <see cref="IsOwner"/> (NGO Distributed Authority's name for it).</summary>
        public bool HasAuthority => IsOwner;

        /// <summary>The id of the player with State Authority over this object (0 if none / not spawned).</summary>
        public ulong OwnerClientId
        {
            get
            {
                if (m_IsSpawned && Object != null && Object.IsValid)
                {
                    m_LastKnownOwnerId = NetworkPlayers.ToClientId(Object.StateAuthority);
                }
                return m_LastKnownOwnerId;
            }
        }

        public sealed override void Spawned()
        {
            m_IsSpawned = true;
            m_IsDespawning = false;
            m_LastKnownOwner = Object.HasStateAuthority;
            m_LastKnownOwnerId = NetworkPlayers.ToClientId(Object.StateAuthority);
            OnNetworkSpawn();
        }

        public sealed override void Despawned(NetworkRunner runner, bool hasState)
        {
            m_IsDespawning = true;
            try
            {
                OnNetworkDespawn();
            }
            finally
            {
                m_IsSpawned = false;
                m_IsDespawning = false;
            }
        }

        /// <summary>Called once Fusion has spawned this object on this client ([Networked] state is readable from here on).</summary>
        public virtual void OnNetworkSpawn() { }

        /// <summary>Called when the object is despawned on this client (also when leaving the session or the scene unloads).</summary>
        public virtual void OnNetworkDespawn() { }
    }
}
