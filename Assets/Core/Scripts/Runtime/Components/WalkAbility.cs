using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// An <see cref="IMovementAbility"/> that handles the character's fundamental ground-based locomotion,
    /// including walking and sprinting. It processes player input to determine the target speed and direction
    /// of movement relative to the character, camera, or world. This ability has the lowest priority,
    /// as it forms the base layer of movement that other abilities can modify or override.
    /// </summary>
    public class WalkAbility : MonoBehaviour, IMovementAbility
    {
        #region Fields & Properties

        private CoreMovement m_Motor;
        private float m_CurrentSpeed;
        private Vector3 m_CurrentVelocity;
        private bool m_WasSprinting;

        // Momentum carried off a grind rail (GrindRail2Ability, 2026-11-05) - see CarryMomentum.
        private float m_CarriedSpeed;
        private float m_CarryStartTime;
        private float m_ReducedControlUntil;
        private float m_ReducedControlScale = 1f;

        // A dash panel's boost (DashPanel, 2026-11-07) - see Dash.
        private float m_DashSpeed;
        private float m_DashUntil;
        private float m_DashJumpControlSeconds;
        private float m_DashJumpControlScale = 1f;
        private bool m_WasGrounded;

        /// <summary>
        /// Gets the priority of the Walk ability. It is set to 0, the lowest priority, because it represents
        /// the most basic form of movement. All other abilities (like jumping or dashing) will be processed
        /// after this one, allowing them to modify or build upon the base walking velocity.
        /// </summary>
        public int Priority => 0;

        /// <summary>
        /// Gets the stamina cost per second while sprinting.
        /// </summary>
        public float StaminaCost => 3f;

        [Header("Air Control")]
        [Tooltip("How much influence input has in the air. 0 = no control, 1 = full control.")]
        [Range(0f, 1f)]
        [SerializeField] private float airControl = 1;

        [Tooltip("How fast the character can turn in the air.")]
        [SerializeField] private float airRotationSpeed = 5f;

        [Tooltip("If true, the character keeps their momentum when jumping, especially from a sprint.")]
        [SerializeField] private bool conserveMomentum = true;

        [Tooltip("Only steer in the air (Air Control / Air Rotation Speed) while holding Grab. Off (2026-11-01, the user's call): air control is just how jumps work, no button needed. On: Dan's original behaviour - without Grab, jumps keep their take-off direction and speed.")]
        [SerializeField] private bool airSteeringNeedsGrab = false;

        [Header("Ice / Low-Grip Turning")]
        [Tooltip("On low-grip ground (e.g. ice), direction changes ease in at speedChangeRate scaled by GroundGrip instead of snapping instantly, so momentum from the old direction carries through a turn. GroundGrip can be as low as 0.03 (near-frictionless), which would make a full reversal take absurdly long - this is the floor GroundGrip is clamped to specifically for that direction-change rate, so even the iciest surface still lets a turn complete in a few seconds. Raise it for a shorter, snappier slide; lower it for a longer one. Does not affect normal-grip ground, which still turns instantly as before.")]
        [SerializeField] private float minIceTurnGrip = 0.2f;

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes the ability by caching a reference to the CoreMovement controller.
        /// </summary>
        /// <param name="motor">The CoreMovement instance that will manage this ability.</param>
        public void Initialize(CoreMovement motor)
        {
            m_Motor = motor;
        }

        /// <summary>
        /// Processes the walking and sprinting logic for the current frame.
        /// This method calculates the appropriate movement speed based on input and sprint state,
        /// smoothly interpolates to that speed, and determines the final movement direction.
        /// </summary>
        /// <returns>A <see cref="MovementModifier"/> containing the calculated horizontal velocity for this frame.</returns>
        public MovementModifier Process()
        {
            var modifier = new MovementModifier();
            if (m_Motor.MovementAbilityActive()) { m_CarriedSpeed = 0f; m_DashUntil = 0f; return modifier; }
            // Gliding (GlideAbility) moves the player itself; it hands its speed back with CarryMomentum when it ends.
            if (m_Motor.IsGliding) { m_CarriedSpeed = 0f; m_DashUntil = 0f; return modifier; }

            // Dash panel boost: leaving the ground during it keeps the speed in the air (CarryMomentum) - a jump
            // with the limited air control, running off an edge with the normal one.
            bool dashing = Time.time < m_DashUntil;
            if (dashing && !m_Motor.IsGrounded && m_WasGrounded)
            {
                bool jumped = m_Motor.VerticalVelocity > 0.1f;
                m_DashUntil = 0f;
                dashing = false;
                CarryMomentum(m_CurrentVelocity, jumped ? m_DashJumpControlSeconds : 0f, jumped ? m_DashJumpControlScale : 1f);
            }
            m_WasGrounded = m_Motor.IsGrounded;
            if (m_Motor.WalkLockedByExternalForce()) { return modifier; }
            // Determine if we should be using sprint speed
            // If we are grounded, we check the current sprint state
            // If we are in the air and conserving momentum, we use the state from when we left the ground
            bool useSprintSpeed = m_Motor.IsGrounded ? m_Motor.IsSprinting : (conserveMomentum && m_WasSprinting);
            
            // Determine the target speed based on whether the player is sprinting
            float targetSpeed = (useSprintSpeed ? m_Motor.sprintSpeed : m_Motor.moveSpeed) * m_Motor.RunSpeedMultiplier; // Rocket Shoes

            // If grounded, update our sprint state memory
            if (m_Motor.IsGrounded)
            {
                m_WasSprinting = m_Motor.IsSprinting;
            }

            if (m_Motor.MoveInput == Vector2.zero)
            {
                targetSpeed = 0.0f;
            }

            // Smoothly interpolate the current speed towards the target speed
            float speedOffset = 0.1f;
            
            // Adjust acceleration/deceleration based on grounded state
            float acceleration = m_Motor.speedChangeRate;
            if (!m_Motor.IsGrounded )
            {
                if (UserInputInControl())
                {
                    // Reduce control authority in air based on airControl setting
                    acceleration *= airControl * AirControlScale;
                }
                else { acceleration = 0; }
                
            }
            else
            {
                // Low-grip ground (e.g. ice) scales speed changes down for a sliding feel. Clamped so a
                // zero-friction surface still lets speed change eventually rather than freezing in place.
                acceleration *= Mathf.Clamp(m_Motor.GroundGrip, 0.08f, 1f);
            }

            if (m_CurrentSpeed < targetSpeed - speedOffset || m_CurrentSpeed > targetSpeed + speedOffset)
            {
                // Lerp the speed, scaling by InputMagnitude to handle analog stick input correctly
                m_CurrentSpeed = Mathf.Lerp(m_CurrentSpeed, targetSpeed * m_Motor.InputMagnitude,
                    Time.deltaTime * acceleration);

                // Round to three decimal places
                m_CurrentSpeed = Mathf.Round(m_CurrentSpeed * 1000f) / 1000f;
            }
            else
            {
                // Snap to target speed only if we have input or are grounded
                // This allows momentum to carry us a bit if we release input in air
                if (m_Motor.MoveInput != Vector2.zero || m_Motor.IsGrounded)
                {
                    m_CurrentSpeed = targetSpeed;
                }
            }

            // Carrying a grind rail's speed (CarryMomentum): it stays until landing (or grabbing something) -
            // the stick only steers. A ground check brushing the rail right as the player leaves doesn't count.
            if (m_CarriedSpeed > 0f)
            {
                bool landed = m_Motor.IsGrounded && m_Motor.VerticalVelocity <= 0.1f && Time.time - m_CarryStartTime > 0.1f;
                if (landed) m_CarriedSpeed = 0f;
                else m_CurrentSpeed = m_CarriedSpeed;
            }

            // Dash panel boost on the ground: at least the dash speed, stick or no stick.
            if (dashing && m_Motor.IsGrounded) m_CurrentSpeed = Mathf.Max(m_CurrentSpeed, m_DashSpeed);

            // Convert 2D input to 3D direction
            Vector3 inputDirection = new Vector3(m_Motor.MoveInput.x, 0.0f, m_Motor.MoveInput.y).normalized;
            Vector3 targetMoveDirection = transform.forward; // Default to forward if no input
            Vector3 targetFacingDirection = transform.forward; // Default to forward if no input
            if (m_CurrentVelocity.magnitude > 0.1f)
            {
                targetMoveDirection = m_CurrentVelocity;
                targetFacingDirection = m_CurrentVelocity;
            }
            // Calculate the final movement direction
            if (inputDirection.sqrMagnitude > 0 )
            {
                switch (m_Motor.directionMode)
                {
                    case CoreMovement.MovementDirectionMode.CharacterRelative:
                        if (UserInputInControl())
                        { targetMoveDirection = m_Motor.transform.rotation * inputDirection; }
                        targetFacingDirection = m_Motor.transform.rotation * inputDirection;
                        break;
                    case CoreMovement.MovementDirectionMode.CameraRelative:
                        if (UserInputInControl())
                        { targetMoveDirection = Quaternion.Euler(0.0f, m_Motor.TargetRotationY, 0.0f) * inputDirection; }
                        targetFacingDirection = Quaternion.Euler(0.0f, m_Motor.TargetRotationY, 0.0f) * inputDirection;
                        break;
                    case CoreMovement.MovementDirectionMode.World:
                    default:
                        if (UserInputInControl())
                        { targetMoveDirection = inputDirection; }
                        targetFacingDirection = inputDirection;
                        break;
                }
                
                // Store last valid input direction for situations where we might want to continue in that direction
                // (Though m_CurrentVelocity helps with that too)
            }
            else if (m_CurrentVelocity.sqrMagnitude > 0.1f)
            {
                 // If no input, direction is our current velocity direction
                 targetMoveDirection = m_CurrentVelocity.normalized;
            }

            // Calculate target velocity
            Vector3 targetVelocity = targetMoveDirection * m_CurrentSpeed;

            // Let facing/animation respond to input immediately regardless of how fast the actual
            // translation below eases toward its target. This is what makes turning around on ice
            // look like the character spinning to face the new direction while the body is still
            // sliding the old way, instead of the whole body slowly carving a turn.
            m_Motor.FacingDirectionOverride = inputDirection.sqrMagnitude > 0 ? targetFacingDirection : (Vector3?)null;

            // Apply smoothing to the velocity vector itself for better air control feel
            // If grounded, we track perfectly (handled by m_CurrentSpeed lerp above efficiently enough, but let's be consistent)
            // Actually, separating speed and direction logic as before is fine, but for air control we want to steer the vector.

            if (m_Motor.IsGrounded)
            {
                bool hasFullGrip = m_Motor.GroundGrip >= 0.999f;
                if (hasFullGrip && dashing)
                {
                    // Dash panel boost: the stick steers it, but doesn't snap it round.
                    Vector3 steered = Vector3.Lerp(m_CurrentVelocity, targetMoveDirection.normalized * m_CurrentSpeed, Time.deltaTime * airRotationSpeed);
                    steered.y = 0f;
                    m_CurrentVelocity = (steered.sqrMagnitude > 0.0001f ? steered.normalized : targetMoveDirection.normalized) * m_CurrentSpeed;
                }
                else if (hasFullGrip)
                {
                    // Normal traction: instant direction changes are usually preferred for
                    // responsiveness. The speed lerp above handles the acceleration.
                    m_CurrentVelocity = targetMoveDirection.normalized * m_CurrentSpeed;
                }
                else
                {
                    // Low-grip ground (e.g. ice): ease the whole velocity vector - direction included -
                    // toward the target at a constant rate (not the speed lerp's curve/rate, which is
                    // tuned for full grip and would be imperceptibly slow scaled this far down). This is
                    // what lets the character keep sliding in the old direction after facing has already
                    // snapped to the new input via FacingDirectionOverride above.
                    float turnRate = m_Motor.speedChangeRate * Mathf.Clamp(m_Motor.GroundGrip, minIceTurnGrip, 1f);
                    m_CurrentVelocity = Vector3.MoveTowards(m_CurrentVelocity, targetMoveDirection.normalized * m_CurrentSpeed, turnRate * Time.deltaTime);
                }
            }
            else
            {
                // In air, we interpolate velocity vector to allow steering
                if (m_Motor.MoveInput != Vector2.zero)
                {
                     // Steer towards target direction
                     // We use airRotationSpeed to interpolate the direction
                     m_CurrentVelocity = Vector3.Lerp(m_CurrentVelocity, targetMoveDirection.normalized * m_CurrentSpeed, Time.deltaTime * airRotationSpeed * AirControlScale);
                }
                else
                {
                    // No input in air - preserve momentum but let drag/gravity do its thing (handled by CoreMovement via speed updates here)
                    // We just update the speed part of the velocity
                    if (m_CurrentVelocity.sqrMagnitude > 0)
                    {
                        m_CurrentVelocity = m_CurrentVelocity.normalized * m_CurrentSpeed;
                    }
                }
            }

            // Apply the calculated speed to the movement direction to get the final velocity
            modifier.ArealVelocity = m_CurrentVelocity;

            return modifier;
        }
        /// <summary>
        /// Leaving a grind rail (GrindRail2Ability): the player flies on at <paramref name="horizontalVelocity"/>
        /// - that speed is kept in the air (the stick steers, at the normal air control) until they land or grab
        /// something. <paramref name="reducedControlSeconds"/> / <paramref name="controlScale"/>: air control is
        /// scaled down for that long first (jumping off a rail: half for 0.2 s).
        /// </summary>
        public void CarryMomentum(Vector3 horizontalVelocity, float reducedControlSeconds = 0f, float controlScale = 1f)
        {
            horizontalVelocity.y = 0f;
            m_CurrentVelocity = horizontalVelocity;
            m_CurrentSpeed = horizontalVelocity.magnitude;
            m_CarriedSpeed = m_CurrentSpeed;
            m_CarryStartTime = Time.time;
            m_ReducedControlUntil = Time.time + Mathf.Max(0f, reducedControlSeconds);
            m_ReducedControlScale = Mathf.Clamp01(controlScale);
        }

        /// <summary>Stops dead: no running speed, no air velocity, no carried rail speed (CoreMovement.ResetMovementForces - respawn, teleport, Retry).</summary>
        public void ResetMomentum()
        {
            m_CurrentVelocity = Vector3.zero;
            m_CurrentSpeed = 0f;
            m_CarriedSpeed = 0f;
            m_ReducedControlUntil = 0f;
            m_DashUntil = 0f;
        }

        /// <summary>
        /// A dash panel (DashPanel): running along <paramref name="direction"/> at <paramref name="speed"/>, held on the
        /// ground for <paramref name="seconds"/> even with the stick let go (the stick steers, smoothly). Leaving the
        /// ground during it carries the speed into the air (CarryMomentum); a jump gets
        /// <paramref name="jumpControlScale"/> air control for <paramref name="jumpControlSeconds"/>.
        /// </summary>
        public void Dash(Vector3 direction, float speed, float seconds, float jumpControlSeconds, float jumpControlScale)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) return;
            m_CurrentVelocity = direction.normalized * speed;
            m_CurrentSpeed = speed;
            m_DashSpeed = speed;
            m_DashUntil = Time.time + Mathf.Max(0f, seconds);
            m_DashJumpControlSeconds = jumpControlSeconds;
            m_DashJumpControlScale = jumpControlScale;
            m_CarriedSpeed = 0f;
            m_WasGrounded = true;
        }

        /// <summary>Air control right now: normally 1, less just after jumping off a rail (CarryMomentum).</summary>
        private float AirControlScale => Time.time < m_ReducedControlUntil ? m_ReducedControlScale : 1f;

        [SerializeField] private float loseControlThreshold; // the magnitude of velocity where the user input is overriden by the force
        public bool UserInputInControl()
        {
            //allow external effects to override the user input
            //and for the user to jump and rotate without being forced to alter the direction of the jump

            if (!m_Motor.IsGrounded)
            {
                // Air steering is on by default (2026-11-01); airSteeringNeedsGrab brings back hold-Grab-to-steer.
                if (!airSteeringNeedsGrab || m_Motor.IsGrabHeld)
                {
                    return true;
                }
                else { return false; }

            }

            if (loseControlThreshold != 0 && m_CurrentVelocity.magnitude > loseControlThreshold)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Attempts to activate the ability. Walking is a continuous state that processes every frame,
        /// not a discrete, activatable ability like jumping or dashing.
        /// </summary>
        /// <returns>Always returns false as this ability cannot be manually activated.</returns>
        public bool TryActivate() => false;

        #endregion
    }
}
