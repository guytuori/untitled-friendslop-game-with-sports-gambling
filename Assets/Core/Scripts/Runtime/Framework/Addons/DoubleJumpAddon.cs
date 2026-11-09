using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// An addon that grants the player the ability to perform a double jump.
    /// Listens for jump input and applies vertical velocity if the player is airborne and hasn't already double jumped.
    /// </summary>
    public class DoubleJumpAddon : CoreNetworkBehaviour, IPlayerAddon
    {
        #region Fields & Properties

        [Header("Settings")]
        [Tooltip("The height of the double jump.")]
        [SerializeField] private float doubleJumpHeight = 2.0f;
        [Tooltip("Optional stamina cost for the double jump.")]
        [SerializeField] private float staminaCost = 2f;
        [Tooltip("How many double jumps the player gets before touching the ground again. -1 = unlimited. (Items like double jump boots can raise it at runtime through MaxDoubleJumps.)")]
        [SerializeField] private int maxDoubleJumps = -1;

        /// <summary>Double jumps allowed per time in the air; -1 = unlimited.</summary>
        public int MaxDoubleJumps
        {
            get => maxDoubleJumps;
            set => maxDoubleJumps = value;
        }

        [Header("Input Events")]
        [Tooltip("Event raised when the jump button is pressed.")]
        [SerializeField] private GameEvent onJumpPressed;

        private CorePlayerManager m_PlayerManager;
        private CoreMovement m_CoreMovement;
        private CoreStatsHandler m_CoreStats;

        private int m_doubleJumpedCount;

        /// <summary>
        /// Raised on the owner's machine for every air jump: the player's avatar (CoreMovement's GameObject) and
        /// which air jump it was (1 = the first). ItemSounds plays the double / triple / quadruple jump sound.
        /// </summary>
        public static event System.Action<GameObject, int> AirJumped;
        private CharacterController m_Body;
        private bool m_HasDoubleJumped;
        private bool m_IsActive = true;

        // The last frame a movement ability (rail, wall climb, pole) was running. A Jump press that ends one of
        // those (jumping off a rail, wall or pole) arrives in the same or the next frame, after the ability has
        // let go - without this it would also fire a double jump that overwrites the jump-off's own launch.
        private int m_LastAbilityFrame = -10;

        #endregion

        #region IPlayerAddon Implementation

        public void Initialize(CorePlayerManager playerManager)
        {
            staminaCost = 2f;
            m_PlayerManager = playerManager;
            m_CoreMovement = playerManager.CoreMovement;
            m_CoreStats = playerManager.CoreStats;
        }

        public void OnPlayerSpawn()
        {
            if (IsOwner || true)
            {
                RegisterEventListeners();
            }
        }

        public void OnPlayerDespawn()
        {
            if (IsOwner)
            {
                UnregisterEventListeners();
            }
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;

            if (newState == PlayerLifeState.Respawned || newState == PlayerLifeState.InitialSpawn)
            {
                m_HasDoubleJumped = false;
            }
        }

        #endregion

        #region Private Methods

        private void Update()
        {
            if (m_CoreMovement == null) return;
            if (m_CoreMovement.MovementAbilityActive()) m_LastAbilityFrame = Time.frameCount;

            // Air jumps come back on a real landing (2026-11-01).
            if (m_doubleJumpedCount > 0 && IsOwner && HasReallyLanded())
            {
                ResetAirJumps();
            }
        }

        /// <summary>
        /// On the ground for real: CoreMovement's ground check, not going up, and the body actually resting on
        /// something. The ground check alone isn't enough - its sphere reaches 0.42 m below the feet and can brush a
        /// ledge, slope or wall edge in mid-air (the jetpack hit the same thing), and resetting then gave the jump
        /// boots endless air jumps.
        /// </summary>
        private bool HasReallyLanded()
        {
            if (!m_CoreMovement.IsGrounded || m_CoreMovement.VerticalVelocity > 0.1f) return false;
            if (m_Body == null) m_Body = m_CoreMovement.GetComponent<CharacterController>();
            return m_Body == null || !m_Body.enabled || m_Body.isGrounded;
        }

        private void ResetAirJumps()
        {
            if (m_doubleJumpedCount > 0 && maxDoubleJumps > 0)
            {
                Debug.Log($"[Items] Landed: air jumps back ({maxDoubleJumps}).", this);
            }
            m_HasDoubleJumped = false;
            m_doubleJumpedCount = 0;
        }

        private void RegisterEventListeners()
        {
            if (onJumpPressed != null)
            {
                onJumpPressed.RegisterListener(HandleJump);
            }

            if (m_CoreMovement != null)
            {
                m_CoreMovement.OnGroundedStateChanged += HandleGroundedStateChanged;
            }
        }

        private void UnregisterEventListeners()
        {
            if (onJumpPressed != null)
            {
                onJumpPressed.UnregisterListener(HandleJump);
            }

            if (m_CoreMovement != null)
            {
                m_CoreMovement.OnGroundedStateChanged -= HandleGroundedStateChanged;
            }
        }

        private void HandleJump()
        {
            if (!m_IsActive || !IsOwner) return;

            if (m_CoreMovement == null) return;

            // About to land (CoreMovement.LandingJumpWindow) or just off an edge (CoreMovement.CoyoteTime): this
            // press is a full jump off the ground, not an air jump - and like a landing, it gives the air jumps
            // back (2026-11-08).
            if (!m_CoreMovement.IsGrounded && m_CoreMovement.CanJumpFromGround)
            {
                ResetAirJumps();
                return;
            }

            // Out of double jumps until the player lands (maxDoubleJumps -1 = unlimited).
            if (maxDoubleJumps >= 0 && m_doubleJumpedCount >= maxDoubleJumps)
            {
                if (maxDoubleJumps > 0 && !m_CoreMovement.IsGrounded) Debug.Log($"[Items] No air jumps left ({m_doubleJumpedCount} of {maxDoubleJumps} used).", this);
                return;
            }

            // This press is jumping off a rail, wall or pole, not a double jump.
            if (Time.frameCount - m_LastAbilityFrame <= 1) return;

            // Only allow double jump if airborne and haven't jumped yet
            //&& !m_HasDoubleJumped
            if (!m_CoreMovement.IsGrounded && m_CoreMovement.MovementAbilityActive() == false)
            {
                AttemptDoubleJump();
            }
        }

        private void AttemptDoubleJump()
        {
            // Check stamina if needed
            if (staminaCost > 0f && m_CoreStats != null)
            {
                if (!m_CoreStats.TryConsumeStat(StatKeys.Stamina, staminaCost * m_doubleJumpedCount, OwnerClientId))
                {
                    return;
                }
            }

            PerformDoubleJump();
        }

        private void PerformDoubleJump()
        {
            m_HasDoubleJumped = true;
            m_doubleJumpedCount++;
            if (maxDoubleJumps > 0) Debug.Log($"[Items] Air jump {m_doubleJumpedCount} of {maxDoubleJumps}.", this);
            // Calculate jump velocity: v = sqrt(2 * g * h)
            // Note: Gravity is negative, so we multiply by -2
            float jumpVelocity = Mathf.Sqrt(doubleJumpHeight * -2f * m_CoreMovement.gravity);

            // Apply velocity directly
            m_CoreMovement.ApplyExternalForce(Vector3.zero,ForceMode.Impulse);
            m_CoreMovement.SetVerticalVelocity(jumpVelocity);

            AirJumped?.Invoke(m_CoreMovement.gameObject, m_doubleJumpedCount); // before the Jump report (ItemSounds tells them apart)
            PlayerActions.Report(m_CoreMovement.gameObject, PlayerAction.Jump); // counted for wagers (LocalActionTracker)
            // Reset any downward force/gravity accumulation if needed, though SetVerticalVelocity handles the main part
        }

        private void HandleGroundedStateChanged(bool isGrounded)
        {
            // Only a real landing gives the air jumps back - Update checks that every frame (this event can fire
            // from the ground check brushing a ledge in mid-air).
            if (isGrounded && m_CoreMovement != null && HasReallyLanded())
            {
                ResetAirJumps();
            }
        }

        #endregion
    }
}
