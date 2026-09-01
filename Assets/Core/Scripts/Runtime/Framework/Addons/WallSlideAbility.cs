using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Super Mario Sunshine-style wall sliding and wall jumping.
    ///
    /// While airborne and holding the movement stick into a nearby vertical wall, the character
    /// clings to it and eases down to a slow slide instead of falling freely. Pressing Jump while
    /// sliding launches the character up and away from the wall - straight into another wall-jump
    /// if there's a matching wall on the other side, which is what the parallel-wall test corridor
    /// (Friendslop > Build Wall Jump Test Area) is built to exercise.
    ///
    /// This implements both interfaces the Core framework uses to discover behaviour:
    /// - IMovementAbility: added to CoreMovement's per-frame ability list (like WalkAbility /
    ///   JumpAbility / PlatformerLocomotionAbility), so it can damp the fall while sliding.
    /// - IPlayerAddon: added to CorePlayerManager's addon list, giving it OnPlayerSpawn /
    ///   OnPlayerDespawn / OnLifeStateChanged lifecycle hooks for safely registering its input
    ///   listener and resetting state on respawn - the same pattern DoubleJumpAddon uses.
    ///
    /// The jump-off is wired directly to the raw "jump pressed" GameEvent (see onJumpPressed
    /// below) rather than going through CorePlayerManager.HandleJump, because that handler only
    /// calls CoreMovement.PerformJump() while grounded. DoubleJumpAddon does the same thing for
    /// the same reason - both need to react to a jump press while airborne.
    ///
    /// Setup: add this component to a player prefab (CorePlayer or PlatformerPlayer both work -
    /// it only depends on CoreMovement/CharacterController, not on which locomotion ability is
    /// used) next to the existing movement abilities, then drag the same GameEvent asset used by
    /// CoreInputHandler's "On Jump Pressed" field into this component's "On Jump Pressed" field.
    /// Friendslop > Add Wall Jump To Player does this automatically for both player prefabs.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class WallSlideAbility : NetworkBehaviour, IMovementAbility, IPlayerAddon
    {
        #region IMovementAbility

        /// <summary>
        /// Processed after WalkAbility (0) and JumpAbility (10). Processing order doesn't actually
        /// change the outcome here - CoreMovement sums every ability's ArealVelocity and ORs their
        /// OverrideGravity flags rather than letting higher-priority abilities replace lower ones -
        /// but a higher number keeps this grouped with the other "can override gravity" abilities.
        /// </summary>
        public int Priority => 15;

        /// <summary>
        /// Continuous sliding costs nothing; the discrete wall jump has its own optional cost below.
        /// </summary>
        public float StaminaCost => 0f;

        #endregion

        [Header("Wall Detection")]
        [Tooltip("Layers that count as a slideable wall. Defaults to Everything; the surface-angle check below already filters out floors, ceilings and shallow ramps, so most projects can leave this alone.")]
        [SerializeField] private LayerMask wallLayers = ~0;
        [Tooltip("How far out from the character's capsule to check for a wall.")]
        [SerializeField] private float wallCheckDistance = 0.6f;
        [Tooltip("Radius of the check - a small sphere cast rather than a thin raycast, so an imperfectly aimed approach still catches the wall.")]
        [SerializeField] private float wallCheckRadius = 0.15f;
        [Tooltip("How far a surface's normal can lean away from perfectly horizontal (90 degrees from world up) and still count as a wall rather than a floor/ceiling/ramp.")]
        [SerializeField, Range(1f, 45f)] private float maxWallSurfaceAngle = 20f;
        [Tooltip("How directly the player has to be pressing toward the wall to grab it. 1 = dead-on, 0 = any glancing contact counts.")]
        [SerializeField, Range(0f, 1f)] private float minPressInto = 0.4f;
        [Tooltip("Minimum time airborne before a wall can be grabbed, so stepping off a ledge next to a wall doesn't instantly stick.")]
        [SerializeField] private float minAirTimeBeforeSlide = 0.1f;

        [Header("Sliding")]
        [Tooltip("Downward speed the character eases toward while clinging to a wall.")]
        [SerializeField] private float slideSpeed = 2.0f;
        [Tooltip("How quickly vertical speed eases toward -slideSpeed once a slide starts. Higher = grabs on harder.")]
        [SerializeField] private float slideDamping = 12f;

        [Header("Wall Jump")]
        [Tooltip("Jump height off the wall, using the same v = sqrt(-2 * g * h) formula as a normal jump.")]
        [SerializeField] private float wallJumpHeight = 2.2f;
        [Tooltip("Outward push away from the wall, applied as an instantaneous force (decays via CoreMovement's forceDecayRate, same as any other external force).")]
        [SerializeField] private float wallJumpPushForce = 7f;
        [Tooltip("Grace window after losing wall contact where a jump press still counts as a wall jump - coyote time for walls.")]
        [SerializeField] private float wallJumpBufferTime = 0.15f;
        [Tooltip("How long after a wall jump before the character can grab a wall again. Stops it immediately re-sticking to the wall it just launched off of.")]
        [SerializeField] private float regrabCooldown = 0.25f;
        [Tooltip("Optional stamina cost per wall jump. 0 = free.")]
        [SerializeField] private float wallJumpStaminaCost = 0f;

        [Header("Input Event")]
        [Tooltip("Same GameEvent CoreInputHandler raises on jump press (drag the same asset assigned there). Wired directly here - see the class summary for why.")]
        [SerializeField] private GameEvent onJumpPressed;

        /// <summary>
        /// True while actively clinging to and sliding down a wall (including the short post-contact
        /// buffer window). Exposed for animation/VFX/camera code that wants to react to wall-sliding.
        /// </summary>
        public bool IsWallSliding { get; private set; }

        /// <summary>
        /// The outward-facing normal of the wall currently being slid, valid while <see cref="IsWallSliding"/> is true.
        /// </summary>
        public Vector3 WallNormal { get; private set; }

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private CoreStatsHandler m_CoreStats;
        private bool m_IsActive = true;
        private bool m_ListenersRegistered;
        private float m_TimeSinceGrounded;
        private float m_TimeSinceOffWall;
        private float m_RegrabTimer;

        #region IMovementAbility

        public void Initialize(CoreMovement movementController)
        {
            m_Motor = movementController;
            m_Controller = m_Motor.GetComponent<CharacterController>();
        }

        public MovementModifier Process()
        {
            var modifier = new MovementModifier();

            if (!m_IsActive || m_Motor == null || m_Controller == null)
            {
                return modifier;
            }

            if (m_RegrabTimer > 0f)
            {
                m_RegrabTimer -= Time.deltaTime;
            }

            if (m_Motor.IsGrounded)
            {
                IsWallSliding = false;
                m_TimeSinceGrounded = 0f;
                return modifier;
            }

            m_TimeSinceGrounded += Time.deltaTime;

            bool canGrab = m_RegrabTimer <= 0f && m_TimeSinceGrounded >= minAirTimeBeforeSlide;
            // Pre-initialized (rather than declared inline via `out`) because the compiler can't
            // prove wallNormal is definitely assigned below just from `wallFound` being true - that
            // requires connecting canGrab's short-circuit to a *separate* `if` statement, which is
            // more than definite-assignment analysis tracks across statement boundaries.
            Vector3 wallNormal = Vector3.zero;
            bool wallFound = canGrab && TryFindWall(out wallNormal);

            if (wallFound)
            {
                WallNormal = wallNormal;
                IsWallSliding = true;
                m_TimeSinceOffWall = 0f;

                float target = -slideSpeed;
                float eased = Mathf.Lerp(m_Motor.VerticalVelocity, target, Time.deltaTime * slideDamping);
                // Don't let the slide speed *up* a fall that's already slower than the slide speed
                // (e.g. right at the apex of a jump against the wall).
                if (m_Motor.VerticalVelocity > target)
                {
                    eased = Mathf.Max(eased, target);
                }

                m_Motor.SetVerticalVelocity(eased);
                modifier.OverrideGravity = true;
            }
            else if (IsWallSliding)
            {
                m_TimeSinceOffWall += Time.deltaTime;
                if (m_TimeSinceOffWall > wallJumpBufferTime)
                {
                    IsWallSliding = false;
                }
            }

            return modifier;
        }

        /// <summary>
        /// The wall jump is a reactive response to the raw jump-pressed event (see <see cref="HandleJumpPressed"/>),
        /// not a discrete ability meant to be triggered externally.
        /// </summary>
        public bool TryActivate() => false;

        #endregion

        #region IPlayerAddon

        public void Initialize(CorePlayerManager playerManager)
        {
            m_CoreStats = playerManager.CoreStats;
        }

        public void OnPlayerSpawn()
        {
            if (IsOwner)
            {
                TryRegisterListener();
            }
        }

        public void OnPlayerDespawn()
        {
            UnregisterListener();
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;

            if (!m_IsActive)
            {
                IsWallSliding = false;
            }

            if (newState == PlayerLifeState.Respawned || newState == PlayerLifeState.InitialSpawn)
            {
                IsWallSliding = false;
                m_RegrabTimer = 0f;
                m_TimeSinceOffWall = wallJumpBufferTime + 1f;
            }
        }

        #endregion

        #region Private Methods

        private void TryRegisterListener()
        {
            if (m_ListenersRegistered || onJumpPressed == null) return;

            onJumpPressed.RegisterListener(HandleJumpPressed);
            m_ListenersRegistered = true;
        }

        private void UnregisterListener()
        {
            if (!m_ListenersRegistered || onJumpPressed == null) return;

            onJumpPressed.UnregisterListener(HandleJumpPressed);
            m_ListenersRegistered = false;
        }

        private void HandleJumpPressed()
        {
            if (!m_IsActive || !IsOwner || m_Motor == null) return;
            if (m_Motor.IsGrounded) return; // grounded jumps go through JumpAbility / CorePlayerManager instead
            if (!IsWallSliding) return;

            if (wallJumpStaminaCost > 0f && m_CoreStats != null)
            {
                if (!m_CoreStats.TryConsumeStat(StatKeys.Stamina, wallJumpStaminaCost, OwnerClientId))
                {
                    return;
                }
            }

            PerformWallJump();
        }

        private void PerformWallJump()
        {
            float jumpVelocity = Mathf.Sqrt(wallJumpHeight * -2f * m_Motor.gravity);
            m_Motor.SetVerticalVelocity(jumpVelocity);
            m_Motor.ApplyExternalForce(WallNormal * wallJumpPushForce, ForceMode.Impulse);

            IsWallSliding = false;
            m_RegrabTimer = regrabCooldown;
        }

        /// <summary>
        /// Casts toward the wall the player is currently pressing into (in world space) and returns
        /// true if a wall-angled surface is within range.
        /// </summary>
        private bool TryFindWall(out Vector3 wallNormal)
        {
            wallNormal = Vector3.zero;

            Vector3 inputDir = GetWorldInputDirection();
            if (inputDir.sqrMagnitude < 0.01f) return false;

            Vector3 capsuleCenter = m_Controller.transform.TransformPoint(m_Controller.center);
            // Start just outside our own capsule so the cast doesn't immediately self-hit.
            Vector3 castOrigin = capsuleCenter + inputDir * (m_Controller.radius + 0.05f);

            if (!Physics.SphereCast(castOrigin, wallCheckRadius, inputDir, out RaycastHit hit, wallCheckDistance, wallLayers, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            float angleFromHorizontalPlane = Vector3.Angle(hit.normal, Vector3.up);
            if (Mathf.Abs(angleFromHorizontalPlane - 90f) > maxWallSurfaceAngle)
            {
                return false; // too shallow - this is a floor, ceiling or ramp, not a wall
            }

            float pressAmount = Vector3.Dot(inputDir, -hit.normal);
            if (pressAmount < minPressInto)
            {
                return false; // brushing past the wall, not pressing into it
            }

            wallNormal = hit.normal;
            return true;
        }

        /// <summary>
        /// Mirrors the direction calculation in WalkAbility / PlatformerLocomotionAbility so the wall
        /// check looks the same way the player is actually trying to move.
        /// </summary>
        private Vector3 GetWorldInputDirection()
        {
            Vector2 input = m_Motor.MoveInput;
            if (input == Vector2.zero) return Vector3.zero;

            Vector3 inputDirection = new Vector3(input.x, 0f, input.y).normalized;

            switch (m_Motor.directionMode)
            {
                case CoreMovement.MovementDirectionMode.CharacterRelative:
                    return m_Motor.transform.rotation * inputDirection;
                case CoreMovement.MovementDirectionMode.CameraRelative:
                    return Quaternion.Euler(0f, m_Motor.TargetRotationY, 0f) * inputDirection;
                case CoreMovement.MovementDirectionMode.World:
                default:
                    return inputDirection;
            }
        }

        #endregion

        #region Debug

        private void OnDrawGizmosSelected()
        {
            if (m_Controller == null) return;

            Gizmos.color = IsWallSliding ? Color.cyan : new Color(1f, 1f, 1f, 0.3f);
            Vector3 capsuleCenter = m_Controller.transform.TransformPoint(m_Controller.center);
            Gizmos.DrawWireSphere(capsuleCenter, m_Controller.radius + wallCheckDistance);

            if (IsWallSliding)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawRay(capsuleCenter, WallNormal * 1.5f);
            }
        }

        #endregion
    }
}
