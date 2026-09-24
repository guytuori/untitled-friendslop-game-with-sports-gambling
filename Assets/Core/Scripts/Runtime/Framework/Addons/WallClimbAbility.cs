using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Grab-gated wall climbing. Replaces the old WallSlideAbility (cling-and-slide-down, wall jump on
    /// any airborne jump press) entirely: that mechanic has been scrapped per design direction, not
    /// extended - WallSlideAbility.cs has since been deleted from the project along with it.
    ///
    /// New behavior: if the player is holding Grab while airborne and pressing into a climbable wall,
    /// they stick to it and can climb - forward/back moves up/down the wall's surface, left/right
    /// shimmies sideways along it, both at half normal walk speed - draining stamina continuously.
    /// Pressing Jump while attached kicks the player off, exactly like the old wall jump. Releasing
    /// Grab just drops the player into freefall, the same as PoleGrabAbility.
    ///
    /// Surface eligibility is opt-in via the <see cref="ClimbableWall"/> marker component (see
    /// IsSurfaceClimbable) - a wall-angled surface only counts if it (or a parent) carries that
    /// component; everything else, no matter its angle, is rejected. MapColliderSetup adds it
    /// automatically to any per-submesh collider built from "brickwall"-textured map geometry (see
    /// MapColliderSetup.ClimbableKeyword); it can also be added by hand to any other collider.
    ///
    /// Implements both interfaces the Core framework uses to discover behaviour, same as
    /// BalanceBeamAbility / GrindRailAbility / PoleGrabAbility:
    /// - IMovementAbility: added to CoreMovement's per-frame ability list, so it can drive the climb
    ///   movement directly and zero out WalkAbility's contribution while attached.
    /// - IPlayerAddon: added to CorePlayerManager's addon list, for the jump-off input listener and
    ///   life-state cleanup.
    ///
    /// The jump-off is wired directly to the raw "jump pressed" GameEvent (see onJumpPressed below)
    /// rather than going through CorePlayerManager.HandleJump, because that handler only calls
    /// CoreMovement.PerformJump() while grounded - climbing leaves the player ungrounded the whole time,
    /// same reason GrindRailAbility/PoleGrabAbility react to the raw event instead.
    ///
    /// Setup: add this component to a player prefab (CorePlayer or PlatformerPlayer both work) next to
    /// the existing movement abilities, then drag the same GameEvent asset used by CoreInputHandler's
    /// "On Jump Pressed" field into this component's "On Jump Pressed" field. Friendslop > Add Wall
    /// Climb To ... Player does this automatically (see WallClimbSetup.cs).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class WallClimbAbility : NetworkBehaviour, IMovementAbility, IPlayerAddon
    {
        #region IMovementAbility

        /// <summary>
        /// Priority 8 - needs to run before WalkAbility (0) so it can zero WalkAbility's input while
        /// climbing (see ProcessClimbMovement), same slot PoleGrabAbility uses. Unlike the old
        /// WallSlideAbility, processing order actually matters here since this ability now supplies
        /// its own horizontal movement instead of only touching vertical velocity.
        /// </summary>
        public int Priority => 8;

        /// <summary>Continuous stamina drain is handled directly in Process() below, not via this.</summary>
        public float StaminaCost => 0f;

        #endregion

        [Header("Wall Detection")]
        [Tooltip("Layers that count as a climbable wall. Defaults to Everything; the surface-angle check below already filters out floors, ceilings and shallow ramps, so most projects can leave this alone.")]
        [SerializeField] private LayerMask wallLayers = ~0;
        [Tooltip("How far out from the character's capsule to check for a wall.")]
        [SerializeField] private float wallCheckDistance = 0.6f;
        [Tooltip("Radius of the check - a small sphere cast rather than a thin raycast, so an imperfectly aimed approach still catches the wall.")]
        [SerializeField] private float wallCheckRadius = 0.15f;
        [Tooltip("How far a surface's normal can lean away from perfectly horizontal (90 degrees from world up) and still count as a wall rather than a floor/ceiling/ramp.")]
        [SerializeField, Range(1f, 45f)] private float maxWallSurfaceAngle = 20f;
        [Tooltip("How directly the player has to be pressing toward the wall to grab it, on the initial attach only. 1 = dead-on, 0 = any glancing contact counts.")]
        [SerializeField, Range(0f, 1f)] private float minPressInto = 0.4f;

        [Header("Climbing")]
        [Tooltip("Vertical climb speed, as a fraction of CoreMovement.moveSpeed.")]
        [SerializeField] private float climbSpeedMultiplier = 0.5f;
        [Tooltip("Sideways shimmy speed along the wall's surface, as a fraction of CoreMovement.moveSpeed.")]
        [SerializeField] private float shimmySpeedMultiplier = 0.5f;
        [Tooltip("Stamina drained per second while stuck to a wall.")]
        [SerializeField] private float staminaCostPerSecond = 10f;
        [Tooltip("Small constant push into the wall (on top of climb/shimmy movement) so the CharacterController doesn't drift off the surface across uneven geometry.")]
        [SerializeField] private float stickForce = 2f;

        [Header("Wall Jump")]
        [Tooltip("Jump height off the wall, using the same v = sqrt(-2 * g * h) formula as a normal jump.")]
        [SerializeField] private float wallJumpHeight = 2.2f;
        [Tooltip("Outward push away from the wall, applied as an instantaneous force (decays via CoreMovement's forceDecayRate, same as any other external force).")]
        [SerializeField] private float wallJumpPushForce = 7f;
        [Tooltip("How long after a wall jump before the character can grab a wall again. Stops it immediately re-sticking to the wall it just launched off of.")]
        [SerializeField] private float regrabCooldown = 0.25f;

        [Header("Input Event")]
        [Tooltip("Same GameEvent CoreInputHandler raises on jump press (drag the same asset assigned there). Wired directly here - see the class summary for why.")]
        [SerializeField] private GameEvent onJumpPressed;

        /// <summary>True while actively stuck to and climbing a wall.</summary>
        public bool IsClimbing { get; private set; }

        /// <summary>The outward-facing normal of the wall currently being climbed, valid while <see cref="IsClimbing"/> is true.</summary>
        public Vector3 WallNormal { get; private set; }

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private CoreStatsHandler m_CoreStats;
        private bool m_IsActive = true;
        private bool m_ListenersRegistered;
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
                IsClimbing = false;
                return modifier;
            }

            if (!IsClimbing)
            {
                // Only stick to a wall while Grab is held - per the design, jumping toward a wall while
                // holding Grab is what grabs it, as opposed to the old WallSlideAbility which clung to
                // any wall you pressed into while airborne.
                if (m_Motor.IsGrabHeld && m_RegrabTimer <= 0f && TryFindClimbableWall(out Vector3 wallNormal))
                {
                    EnterClimb(wallNormal);
                }
                return modifier;
            }

            if (!m_Motor.IsGrabHeld)
            {
                // Letting go simply drops you into freefall.
                ExitClimb();
                return modifier;
            }

            float cost = staminaCostPerSecond * Time.deltaTime;
            if (m_CoreStats != null && !m_CoreStats.TryConsumeStat(StatKeys.Stamina, cost, OwnerClientId))
            {
                // Ran out of stamina mid-climb - lose your grip rather than quietly stopping in place.
                ExitClimb();
                return modifier;
            }

            // Re-check the wall is still there each frame (shimmying along a surface, or the surface
            // stepping/curving) rather than trusting the normal captured on entry.
            if (!TryFindClimbableWall(out Vector3 currentNormal))
            {
                ExitClimb();
                return modifier;
            }
            WallNormal = currentNormal;

            ProcessClimbMovement(ref modifier);
            return modifier;
        }

        /// <summary>
        /// The wall jump is a reactive response to the raw jump-pressed event (see
        /// <see cref="HandleJumpPressed"/>), not a discrete ability meant to be triggered externally.
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
                IsClimbing = false;
            }

            if (newState == PlayerLifeState.Respawned || newState == PlayerLifeState.InitialSpawn)
            {
                IsClimbing = false;
                m_RegrabTimer = 0f;
            }
        }

        #endregion

        #region Climbing

        private void ProcessClimbMovement(ref MovementModifier modifier)
        {
            Vector2 rawInput = m_Motor.MoveInput;
            // From here on this ability owns the horizontal movement while attached - zero out
            // WalkAbility's contribution before it runs (see the Priority discussion above).
            m_Motor.SetMoveInput(Vector2.zero);

            Vector3 up = Vector3.up;
            Vector3 alongWall = Vector3.Cross(WallNormal, up).normalized;

            float vertSpeed = m_Motor.moveSpeed * climbSpeedMultiplier;
            float horizSpeed = m_Motor.moveSpeed * shimmySpeedMultiplier;

            Vector3 velocity = up * (rawInput.y * vertSpeed) + alongWall * (rawInput.x * horizSpeed);
            // A small constant push into the wall on top of the climb/shimmy movement, so the
            // CharacterController.Move() this frame's ArealVelocity drives doesn't drift off the
            // surface across uneven geometry.
            velocity += -WallNormal * stickForce;

            modifier.ArealVelocity = velocity;
            m_Motor.SetVerticalVelocity(0f);
            modifier.OverrideGravity = true;
        }

        private void EnterClimb(Vector3 wallNormal)
        {
            WallNormal = wallNormal;
            IsClimbing = true;
            m_Motor.SetVerticalVelocity(0f);
            //NOTE: I think the user should maintain their orientation when wall grabbing
            //starting the wall movement is when the override should kick it, otherwise it should
            //just feel like a quick spiderman stick
            //m_Motor.RotationOverride = () => Quaternion.LookRotation(-WallNormal, Vector3.up);
            m_Motor.RotationOverride = () => Quaternion.LookRotation(transform.forward, Vector3.up);
        }

        private void ExitClimb()
        {
            if (!IsClimbing) return;

            IsClimbing = false;
            if (m_Motor != null)
            {
                m_Motor.RotationOverride = null;
            }
        }

        #endregion

        #region Jump Off

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
            if (!IsClimbing) return;

            PerformWallJump();
        }
        [SerializeField] private int wallJumpStyle;
        private void PerformWallJump()
        {
            if (wallJumpStyle == 1)
            {
                ControlledWallJump();
                return;
            }
            

            float jumpVelocity = Mathf.Sqrt(wallJumpHeight * -2f * m_Motor.gravity);
            m_Motor.SetVerticalVelocity(jumpVelocity);
            m_Motor.ApplyExternalForce(WallNormal * wallJumpPushForce, ForceMode.Impulse);

            ExitClimb();
            m_RegrabTimer = regrabCooldown;
        }

        private void ControlledWallJump()
        {
            float jumpVelocity = Mathf.Sqrt(wallJumpHeight * -2f * m_Motor.gravity);

            Vector2 rawInput = m_Motor.MoveInput;
            Vector3 jumpDirection = WallNormal;

            if (rawInput.sqrMagnitude > 0.1f)
            {
                jumpDirection = rawInput.normalized;
            }
          //  m_Motor.SetVerticalVelocity(jumpVelocity);
            m_Motor.ApplyExternalForce(jumpDirection * wallJumpPushForce, ForceMode.Impulse);

            ExitClimb();
            m_RegrabTimer = regrabCooldown;
        }


        #endregion

        #region Detection

        /// <summary>
        /// Casts toward the wall the player is currently pressing into (in world space) and returns
        /// true if a wall-angled, climbable surface is within range. While already climbing, input
        /// pointed away from any wall (e.g. shimmying with no forward stick held) falls back to
        /// checking straight into the last known wall normal, so a brief lack of input doesn't
        /// immediately drop the player.
        /// </summary>
        private bool TryFindClimbableWall(out Vector3 wallNormal)
        {
            wallNormal = Vector3.zero;

            Vector3 inputDir = GetWorldInputDirection();
            if (inputDir.sqrMagnitude < 0.01f)
            {
                //if (!IsClimbing) return false;
                inputDir = transform.forward;
               // inputDir = -WallNormal;orld 
            }

            Vector3 capsuleCenter = m_Controller.transform.TransformPoint(m_Controller.center);
            // Cast from the capsule's own center, not a point offset out near its surface. This
            // check needs to succeed precisely when the player is pressed flush against a wall - but
            // the character controller's skin width (this project's is 0.02) leaves less real-world
            // clearance than a fixed surface offset assumes, so an origin placed just past the
            // capsule's radius can end up already embedded in the wall's own collider when snugly
            // pressed against it. Physics.SphereCast silently reports no hit for a collider the sphere
            // already overlaps at the start, so an embedded origin made this fail in exactly the
            // moment it needed to succeed. Starting at the center avoids that: it's always outside any
            // external wall, and while it does start inside the player's own CharacterController
            // (itself a Collider), that same "already overlapping" rule means the cast silently skips
            // it too rather than reporting a self-hit, so the sweep still correctly finds the wall the
            // first time the sphere actually reaches it.
            Vector3 castOrigin = capsuleCenter;
            float castDistance = m_Controller.radius + wallCheckDistance;

            if (!Physics.SphereCast(castOrigin, wallCheckRadius, inputDir, out RaycastHit hit, castDistance, wallLayers, QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            float angleFromHorizontalPlane = Vector3.Angle(hit.normal, Vector3.up);
            if (Mathf.Abs(angleFromHorizontalPlane - 90f) > maxWallSurfaceAngle)
            {
                return false; // too shallow - this is a floor, ceiling or ramp, not a wall
            }

            if (!IsClimbing)
            {
                float pressAmount = Vector3.Dot(inputDir, -hit.normal);
                //if (pressAmount < minPressInto)
                //{
                //    return false; // brushing past the wall, not pressing into it - only checked on initial attach
                //}
            }

            if (!IsSurfaceClimbable(hit))
            {
                return false;
            }

            wallNormal = hit.normal;
            return true;
        }

        /// <summary>
        /// Surface eligibility: only a collider that carries (on itself or a parent) a
        /// <see cref="ClimbableWall"/> marker component counts as climbable. Everything else - including
        /// a Pole's own collider, which never carries this marker - is rejected, keeping wall-climbing
        /// and PoleGrabAbility from fighting over the same object.
        /// </summary>
        private bool IsSurfaceClimbable(RaycastHit hit)
        {
            return hit.collider.GetComponentInParent<ClimbableWall>() != null;
        }

        /// <summary>
        /// Mirrors the direction calculation in WalkAbility so the wall check looks the same way the
        /// player is actually trying to move.
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

            Gizmos.color = IsClimbing ? Color.cyan : new Color(1f, 1f, 1f, 0.3f);
            Vector3 capsuleCenter = m_Controller.transform.TransformPoint(m_Controller.center);
            Gizmos.DrawWireSphere(capsuleCenter, m_Controller.radius + wallCheckDistance);

            if (IsClimbing)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawRay(capsuleCenter, WallNormal * 1.5f);
            }
        }

        #endregion
    }
}
