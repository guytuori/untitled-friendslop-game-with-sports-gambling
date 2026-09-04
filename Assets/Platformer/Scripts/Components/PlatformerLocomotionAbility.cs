using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A comprehensive platformer movement ability that handles both walking and jumping
    /// with improved feel through timing buffers, variable jump height, and responsive controls.
    /// </summary>
    public class PlatformerLocomotionAbility : MonoBehaviour, IMovementAbility
    {
        #region Enums

        /// <summary>
        /// Defines the current phase of a jump action.
        /// </summary>
        private enum JumpPhase
        {
            /// <summary>
            /// No jump in progress.
            /// </summary>
            None,
            /// <summary>
            /// Player is moving upward during jump.
            /// </summary>
            Rising,
            /// <summary>
            /// Player is falling after reaching jump apex.
            /// </summary>
            Falling,
            /// <summary>
            /// Player just landed on the ground.
            /// </summary>
            Landing
        }

        #endregion

        #region Fields & Properties

        private static readonly int k_TurnAngle = Animator.StringToHash("TurnAngle");
        private static readonly int k_TurnInPlace = Animator.StringToHash("TurnInPlace");

        public int Priority => 10;
        public float StaminaCost => staminaCost;
        public float JumpStaminaCost => jumpStaminaCost;

        [Header("Stamina")]
        [Tooltip("Stamina cost for general movement.")]
        [SerializeField] private float staminaCost = 5f;
        [Tooltip("Stamina cost for performing a jump.")]
        [SerializeField] private float jumpStaminaCost = 10f;

        [Header("Movement Feel")]
        [Tooltip("Curve defining how quickly the player accelerates to target speed.")]
        [SerializeField] private AnimationCurve accelerationCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        [Tooltip("Curve defining how quickly the player decelerates to a stop.")]
        [SerializeField] private AnimationCurve decelerationCurve = AnimationCurve.EaseInOut(0, 1, 1, 0);
        [Tooltip("Acceleration rate when on the ground.")]
        [SerializeField] private float groundAcceleration = 15f;
        [Tooltip("Deceleration rate when on the ground.")]
        [SerializeField] private float groundDeceleration = 25f;
        [Tooltip("Acceleration rate when in the air.")]
        [SerializeField] private float airAcceleration = 8f;
        [Tooltip("Deceleration rate when in the air.")]
        [SerializeField] private float airDeceleration = 5f;

        [Header("Jump Feel")]
        [Tooltip("Grace period after leaving ground where jump is still allowed (coyote time).")]
        [SerializeField] private float coyoteTime = 0.15f;
        [Tooltip("Time window to buffer jump input before landing.")]
        [SerializeField] private float jumpBufferTime = 0.12f;
        [Tooltip("Maximum duration player can hold jump button for full jump height.")]
        [SerializeField] private float jumpHoldTime = 0.3f;
        [Tooltip("Horizontal movement multiplier applied during jump ascent.")]
        [SerializeField] private float jumpHorizontalBoost = 1.05f;
        [Tooltip("Velocity multiplier for short hops when jump is released early.")]
        [SerializeField] private float shortHopMultiplier = 0.5f;
        [Tooltip("Time in seconds after landing before the player can jump again.")]
        [SerializeField] private float landingCooldown = 0.5f;

        [Header("Advanced Settings")]
        [Tooltip("Minimum speed difference threshold to snap to target speed.")]
        [SerializeField] private float speedOffset = 0.1f;
        [Tooltip("Speed multiplier applied when changing direction for snappier controls.")]
        [SerializeField] private float directionChangeBoost = 1.2f;

        [Header("Ice / Low-Grip Turning")]
        [Tooltip("On low-grip ground (e.g. ice), direction changes use groundAcceleration/groundDeceleration scaled by GroundGrip, same as speed changes - but grip can go as low as 0.03 (near-frictionless), which would make a full reversal take absurdly long. This is the floor GroundGrip is clamped to specifically for that direction-change rate, so even the iciest surface still lets a turn complete in a few seconds instead of ~15+. Raise it for a shorter, snappier slide; lower it for a longer one. Does not affect how slippery the surface feels for speeding up/slowing down in a straight line (that still uses the 0.08 floor).")]
        [SerializeField] private float minIceTurnGrip = 0.2f;

        [Header("Turn In Place Settings")]
        [Tooltip("Maximum duration (in seconds) for a move input to be considered a 'tap' for turn-in-place.")]
        [SerializeField] private float tapThreshold = 0.2f;

        [Header("Movement Effects")]
        [Tooltip("Effect to spawn when the player starts sprinting.")]
        [SerializeField] private GameObject sprintStartEffect;
        [Tooltip("Effect to spawn when the player stops sprinting.")]
        [SerializeField] private GameObject sprintStopEffect;
        [Tooltip("Effect to spawn when the player starts jumping.")]
        [SerializeField] private GameObject jumpStartEffect;
        [Tooltip("Effect to spawn when the player stops jumping (lands).")]
        [SerializeField] private GameObject jumpStopEffect;

        private CoreMovement m_Motor;
        private float m_CurrentSpeed;
        private Vector3 m_CurrentVelocity;
        private Vector2 m_LastMoveInput;
        private float m_TimeLeftGround;
        private float m_JumpBufferTimer;
        private float m_JumpCooldown;
        private float m_JumpHoldTimer;
        private bool m_JumpHeld;
        private bool m_IsRisingFromJump;
        private JumpPhase m_JumpPhase = JumpPhase.None;
        private bool m_WasSprinting;
        private bool m_WasJumping;
        private bool m_WasGroundedLastFrame;
        private float m_MoveInputStartTime;
        private bool m_IsMoveInputActive;
        private Vector2 m_LastMoveDirection;
        private bool m_TurnInPlaceTriggered;
        private readonly int m_AnimIDSpeed = Animator.StringToHash("Speed");
        private Animator m_Animator;
        private CoreCameraController m_CameraController;
        private CoreMovement m_CoreMovement;

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes the ability with the specified <see cref="CoreMovement"/> motor.
        /// </summary>
        /// <param name="motor">The motor that owns this ability.</param>
        public void Initialize(CoreMovement motor)
        {
            m_Motor = motor;
            m_Animator = GetComponentInChildren<Animator>();
            m_CameraController = m_Motor.GetComponent<CoreCameraController>();
            m_CoreMovement = GetComponentInParent<CoreMovement>();

            m_JumpBufferTimer = 0f;
            m_JumpCooldown = 0f;
            m_JumpHoldTimer = 0f;
            m_WasSprinting = false;
            m_WasJumping = false;
            m_WasGroundedLastFrame = m_Motor.IsGrounded;
            m_IsMoveInputActive = false;
            m_TurnInPlaceTriggered = false;
        }

        /// <summary>
        /// Processes movement and jump logic each frame, returning movement modifiers.
        /// </summary>
        /// <returns>A <see cref="MovementModifier"/> containing velocity and gravity modifications.</returns>
        public MovementModifier Process()
        {
            var modifier = new MovementModifier();

            m_TurnInPlaceTriggered = false;

            UpdateTimingContext();
            HandleTurnInPlaceDetection();
            CheckMovementStateChanges();
            HandleHorizontalMovement(ref modifier);
            HandleJumping(ref modifier);
            UpdateAnimationContext();

            return modifier;
        }

        /// <summary>
        /// Attempts to activate the ability.
        /// </summary>
        /// <returns>Always returns false as this is a continuous ability.</returns>
        public bool TryActivate() => false;

        #endregion

        #region Private Methods

        /// <summary>
        /// Checks for state changes in sprinting and jumping to trigger visual effects.
        /// </summary>
        private void CheckMovementStateChanges()
        {
            bool isCurrentlySprinting = m_Motor.IsSprinting && m_Motor.IsGrounded && m_Motor.InputMagnitude > 0.1f;
            if (isCurrentlySprinting != m_WasSprinting)
            {
                if (isCurrentlySprinting)
                {
                    SpawnMovementEffect(sprintStartEffect, "SprintStart");
                }
                else
                {
                    SpawnMovementEffect(sprintStopEffect, "SprintStop");
                }
                m_WasSprinting = isCurrentlySprinting;
            }

            bool isCurrentlyJumping = m_JumpPhase == JumpPhase.Rising;
            if (isCurrentlyJumping != m_WasJumping)
            {
                if (isCurrentlyJumping)
                {
                    SpawnMovementEffect(jumpStartEffect, "JumpStart");
                }
                m_WasJumping = isCurrentlyJumping;
            }

            if (m_WasJumping && m_Motor.IsGrounded && m_JumpPhase == JumpPhase.Landing)
            {
                SpawnMovementEffect(jumpStopEffect, "JumpStop");
                m_WasJumping = false;
            }
        }

        /// <summary>
        /// Spawns a visual effect at the player's position.
        /// </summary>
        /// <param name="effectPrefab">The effect prefab to spawn.</param>
        /// <param name="effectName">Name for the spawned effect instance.</param>
        private void SpawnMovementEffect(GameObject effectPrefab, string effectName)
        {
            if (effectPrefab == null) return;

            Vector3 effectPosition = transform.position;
            CoreDirector.CreatePrefabEffect(effectPrefab)
                .WithPosition(effectPosition)
                .WithRotation(Quaternion.LookRotation(m_CoreMovement.LastMoveDirection, Vector3.up))
                .WithName(effectName)
                .WithDuration(0.5f)
                .Create();
        }

        /// <summary>
        /// Updates timing-related state for jump buffers, coyote time, and cooldowns.
        /// </summary>
        private void UpdateTimingContext()
        {
            bool isCurrentlyGrounded = m_Motor.IsGrounded;

            if (m_Motor.IsGrounded)
            {
                m_TimeLeftGround = 0f;
                if (m_JumpPhase == JumpPhase.Falling)
                {
                    m_JumpPhase = JumpPhase.Landing;
                }
                else if (m_JumpPhase == JumpPhase.Landing)
                {
                    m_JumpPhase = JumpPhase.None;
                }
            }
            else
            {
                m_TimeLeftGround += Time.deltaTime;
                if (m_JumpPhase == JumpPhase.None && m_Motor.VerticalVelocity > 2f && m_WasGroundedLastFrame && !isCurrentlyGrounded)
                {
                    m_JumpPhase = JumpPhase.Rising;
                    m_IsRisingFromJump = true;
                    m_JumpHeld = false;
                }
                else if (m_JumpPhase == JumpPhase.None)
                {
                    m_JumpPhase = JumpPhase.Falling;
                }
            }

            if (m_Motor.JumpRequested && (m_Motor.IsGrounded || m_TimeLeftGround <= coyoteTime))
            {
                m_JumpBufferTimer = jumpBufferTime;
            }
            m_JumpBufferTimer = Mathf.Max(0, m_JumpBufferTimer - Time.deltaTime);

            if (m_Motor.VariableActionReleasedThisFrame && m_IsRisingFromJump)
            {
                m_JumpHeld = false;
            }

            m_JumpCooldown = Mathf.Max(0, m_JumpCooldown - Time.deltaTime);

            if (m_JumpHeld)
            {
                m_JumpHoldTimer += Time.deltaTime;
            }

            m_WasGroundedLastFrame = isCurrentlyGrounded;
        }

        /// <summary>
        /// Detects quick tap inputs for triggering turn-in-place animations.</summary>
        private void HandleTurnInPlaceDetection()
        {
            bool hasInput = m_Motor.MoveInput.magnitude > 0.1f;

            if (hasInput && !m_IsMoveInputActive)
            {
                m_IsMoveInputActive = true;
                m_MoveInputStartTime = Time.time;
                m_LastMoveDirection = m_Motor.MoveInput;
            }
            else if (!hasInput && m_IsMoveInputActive)
            {
                m_IsMoveInputActive = false;
                float inputDuration = Time.time - m_MoveInputStartTime;

                if (inputDuration <= tapThreshold && m_Motor.IsGrounded)
                {
                    m_TurnInPlaceTriggered = true;
                    TriggerTurnInPlace(m_LastMoveDirection);
                }
            }
        }

        /// <summary>
        /// Triggers the turn-in-place animation with the calculated angle.
        /// </summary>
        /// <param name="direction">The input direction for the turn.</param>
        private void TriggerTurnInPlace(Vector2 direction)
        {
            if (m_Animator == null || m_CameraController == null) return;
            if (!m_Motor.IsGrounded) return;

            if (direction.magnitude < 0.1f) return;
            direction.Normalize();

            float cameraYaw = m_CameraController.CurrentHorizontalLookAngle * Mathf.Deg2Rad;
            Vector3 cameraForward = new Vector3(Mathf.Sin(cameraYaw), 0, Mathf.Cos(cameraYaw));
            Vector3 cameraRight = new Vector3(Mathf.Cos(cameraYaw), 0, -Mathf.Sin(cameraYaw));

            Vector3 targetDirection = (cameraForward * direction.y) + (cameraRight * direction.x);
            targetDirection.Normalize();

            Vector3 playerForward = m_Motor.RotationTransform.forward;
            float angle = Vector3.SignedAngle(playerForward, targetDirection, Vector3.up);

            m_Animator.SetFloat(k_TurnAngle, angle);
            m_Animator.SetTrigger(k_TurnInPlace);
        }

        /// <summary>
        /// Handles horizontal movement with acceleration, deceleration, and direction changes.
        /// </summary>
        /// <param name="modifier">The movement modifier to update with horizontal velocity.</param>
        private void HandleHorizontalMovement(ref MovementModifier modifier)
        {
            if (m_TurnInPlaceTriggered)
            {
                modifier.ArealVelocity = Vector3.zero;
                m_CurrentVelocity = Vector3.zero;
                m_Motor.FacingDirectionOverride = null;
                return;
            }

            float targetSpeed = CalculateTargetSpeed();
            bool isChangingDirection = Vector2.Dot(m_Motor.MoveInput, m_LastMoveInput) < -0.1f;
            bool hasInput = m_Motor.MoveInput.magnitude > 0.1f;

            if (hasInput)
            {
                if (isChangingDirection && m_Motor.IsGrounded)
                {
                    targetSpeed *= directionChangeBoost;
                }

                m_CurrentSpeed = CalculateSpeedTransition(m_CurrentSpeed, targetSpeed, true);
            }
            else
            {
                m_CurrentSpeed = CalculateSpeedTransition(m_CurrentSpeed, 0f, false);
            }

            m_CurrentSpeed = Mathf.Round(m_CurrentSpeed * 1000f) / 1000f;

            Vector3 inputDirection = new Vector3(m_Motor.MoveInput.x, 0.0f, m_Motor.MoveInput.y).normalized;
            Vector3 targetDirection = TransformInputDirection(inputDirection);

            // Let facing/animation respond to input immediately, independent of how fast the actual
            // translation below eases toward its target. This is what makes turning around on ice
            // look like the character spinning to face the new direction (and playing the running
            // animation, via m_CurrentSpeed above) while the body is still sliding the old way,
            // instead of the whole body slowly carving a turn.
            m_Motor.FacingDirectionOverride = hasInput ? targetDirection : (Vector3?)null;

            bool hasFullGrip = !m_Motor.IsGrounded || m_Motor.GroundGrip >= 0.999f;
            if (hasFullGrip)
            {
                // Normal traction (or airborne): direction snaps immediately, exactly like before -
                // only the scalar speed (m_CurrentSpeed, eased above) ramps up/down.
                m_CurrentVelocity = targetDirection * m_CurrentSpeed;
            }
            else
            {
                // Low-grip ground (e.g. ice): ease the whole velocity vector - direction included -
                // toward the target using the same grip-scaled rate/curve as the scalar speed
                // transition above. Direction changes are therefore just as sluggish as speed changes
                // on ice, so momentum from the old direction carries over while the new direction is
                // fought for, even though FacingDirectionOverride has already snapped the character's
                // visual facing to the new input.
                Vector3 targetVelocity = hasInput ? targetDirection * targetSpeed : Vector3.zero;
                m_CurrentVelocity = CalculateVelocityTransition(m_CurrentVelocity, targetVelocity, hasInput);
            }

            modifier.ArealVelocity = m_CurrentVelocity;

            if (m_JumpPhase == JumpPhase.Rising && hasInput)
            {
                modifier.ArealVelocity *= jumpHorizontalBoost;
            }

            m_LastMoveInput = m_Motor.MoveInput;
        }

        /// <summary>
        /// Calculates the target movement speed based on input magnitude and sprint state.
        /// </summary>
        /// <returns>The target speed value.</returns>
        private float CalculateTargetSpeed()
        {
            if (m_Motor.MoveInput.magnitude < 0.1f) return 0f;

            bool isSprinting = m_Motor.IsSprinting && m_Motor.IsGrounded;
            float baseSpeed = isSprinting ? m_Motor.sprintSpeed : m_Motor.moveSpeed;

            return baseSpeed * m_Motor.InputMagnitude;
        }

        /// <summary>
        /// Calculates smooth speed transition using acceleration/deceleration curves.
        /// </summary>
        /// <param name="current">Current speed value.</param>
        /// <param name="target">Target speed value.</param>
        /// <param name="accelerating">Whether the player is accelerating or decelerating.</param>
        /// <returns>The interpolated speed value.</returns>
        private float CalculateSpeedTransition(float current, float target, bool accelerating)
        {
            if (Mathf.Abs(current - target) < speedOffset) return target;

            float rate = CalculateGroundedRate(accelerating);
            AnimationCurve curve = accelerating ? accelerationCurve : decelerationCurve;
            float t = Time.deltaTime * rate;

            return Mathf.Lerp(current, target, curve.Evaluate(Mathf.Clamp01(t)));
        }

        /// <summary>
        /// Vector3 counterpart to <see cref="CalculateSpeedTransition"/> - eases an entire velocity
        /// vector (direction and magnitude together) toward a target. Used on low-grip ground so that
        /// changing direction is just as sluggish as changing speed, letting old momentum carry the
        /// character through a turn.
        ///
        /// Deliberately NOT curve-based like <see cref="CalculateSpeedTransition"/>: this method feeds
        /// a per-frame rate (already scaled down toward ~1 by low grip) into accelerationCurve /
        /// decelerationCurve, and those curves are tuned for the *unscaled*, full-grip rate (18-26 in
        /// this project). At grip-scaled rates as low as ~1-2, curve.Evaluate() of the resulting tiny
        /// per-frame t sits in the curve's near-flat regions and returns a per-frame Lerp fraction on
        /// the order of 0.0005 - which compounds to something that looks completely frozen for the
        /// first several seconds (and would take on the order of a minute to actually complete), not a
        /// visible slide. A constant-rate MoveTowards sidesteps that entirely, and is also a closer
        /// match to how real kinetic friction works (a constant decelerating force, not an exponential
        /// decay) - the character visibly, steadily arcs into the new direction from the first frame,
        /// finishing the turn in a predictable number of seconds set by groundAcceleration /
        /// groundDeceleration and GroundGrip.
        /// </summary>
        /// <param name="current">Current velocity.</param>
        /// <param name="target">Target velocity.</param>
        /// <param name="accelerating">Whether this transition should use the acceleration or deceleration rate.</param>
        /// <returns>The velocity vector, moved a fixed distance toward the target this frame.</returns>
        private Vector3 CalculateVelocityTransition(Vector3 current, Vector3 target, bool accelerating)
        {
            // Uses its own, higher grip floor (minIceTurnGrip) than the scalar/animation path below -
            // GroundGrip on ice can be as low as 0.03, and a turn rate scaled down that far would take
            // upwards of 15 seconds to complete a full reversal. This keeps the slide dramatic without
            // making it feel broken or eternal.
            float rate = CalculateGroundedRate(accelerating, minIceTurnGrip);
            return Vector3.MoveTowards(current, target, rate * Time.deltaTime);
        }

        /// <summary>
        /// Computes the grounded/air acceleration or deceleration rate for this frame, scaling the
        /// ground rate down on low-grip surfaces (e.g. ice) for a sliding feel. Air rates are untouched
        /// since they're not affected by the ground.
        /// </summary>
        /// <param name="accelerating">Whether the player is accelerating or decelerating.</param>
        /// <param name="minGrip">
        /// Floor GroundGrip is clamped to before scaling the rate, so a near-frictionless surface still
        /// lets the character eventually gain/lose speed (or complete a turn) rather than freezing in
        /// place or taking unreasonably long. Defaults to 0.08 for the scalar/animation-speed path;
        /// <see cref="CalculateVelocityTransition"/> passes its own, higher <see cref="minIceTurnGrip"/>.
        /// </param>
        /// <returns>The rate to use for this frame's transition.</returns>
        private float CalculateGroundedRate(bool accelerating, float minGrip = 0.08f)
        {
            bool isGrounded = m_Motor.IsGrounded;
            float groundGrip = isGrounded ? Mathf.Clamp(m_Motor.GroundGrip, minGrip, 1f) : 1f;

            return accelerating ?
                (isGrounded ? groundAcceleration * groundGrip : airAcceleration) :
                (isGrounded ? groundDeceleration * groundGrip : airDeceleration);
        }

        /// <summary>
        /// Transforms input direction based on the movement direction mode.
        /// </summary>
        /// <param name="inputDirection">The raw input direction vector.</param>
        /// <returns>The transformed direction in world space.</returns>
        private Vector3 TransformInputDirection(Vector3 inputDirection)
        {
            switch (m_Motor.directionMode)
            {
                case CoreMovement.MovementDirectionMode.CharacterRelative:
                    return m_Motor.transform.rotation * inputDirection;
                case CoreMovement.MovementDirectionMode.CameraRelative:
                    return Quaternion.Euler(0.0f, m_Motor.TargetRotationY, 0.0f) * inputDirection;
                case CoreMovement.MovementDirectionMode.World:
                default:
                    return inputDirection;
            }
        }

        /// <summary>
        /// Handles jump input, buffering, coyote time, and variable jump height.
        /// </summary>
        /// <param name="modifier">The movement modifier to update with jump velocity.</param>
        private void HandleJumping(ref MovementModifier modifier)
        {
            bool canJump = (m_Motor.IsGrounded || m_TimeLeftGround <= coyoteTime)
                           && m_Motor.TimeSinceLanded >= landingCooldown;
            bool wantsToJump = m_JumpBufferTimer > 0f;

            if (wantsToJump && canJump && m_JumpCooldown <= 0f)
            {
                PerformJump(ref modifier);
            }

            if (!m_JumpHeld && m_IsRisingFromJump && m_Motor.VerticalVelocity > 0 && m_JumpHoldTimer < jumpHoldTime)
            {
                m_Motor.SetVerticalVelocity(m_Motor.VerticalVelocity * shortHopMultiplier);
                m_IsRisingFromJump = false;
            }

            if (m_Motor.VerticalVelocity <= 0 && m_JumpPhase == JumpPhase.Rising)
            {
                m_JumpPhase = JumpPhase.Falling;
                m_IsRisingFromJump = false;
            }
        }

        /// <summary>
        /// Executes a jump by setting vertical velocity and managing jump state.
        /// </summary>
        /// <param name="modifier">The movement modifier to update with gravity override.</param>
        private void PerformJump(ref MovementModifier modifier)
        {
            float jumpVelocity = m_Motor.gravity < 0f
                ? Mathf.Sqrt(m_Motor.jumpHeight * -2f * m_Motor.gravity)
                : 0f;
            m_Motor.SetVerticalVelocity(jumpVelocity);

            m_JumpBufferTimer = 0f;
            m_JumpCooldown = 0.1f;
            m_JumpHoldTimer = 0f;
            m_JumpHeld = true;
            m_IsRisingFromJump = true;
            m_JumpPhase = JumpPhase.Rising;

            modifier.OverrideGravity = true;

            if (m_Motor.MoveInput.magnitude > 0.1f)
            {
                modifier.ArealVelocity *= jumpHorizontalBoost;
            }
        }

        /// <summary>
        /// Updates animation parameters based on current movement state.
        /// </summary>
        private void UpdateAnimationContext()
        {
            if (m_Animator == null) return;
            m_Animator.SetFloat(m_AnimIDSpeed, m_CurrentSpeed);
        }

        #endregion
    }
}
