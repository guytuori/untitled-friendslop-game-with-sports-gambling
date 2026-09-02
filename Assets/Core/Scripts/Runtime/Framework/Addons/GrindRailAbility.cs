using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Grind-rail traversal: snaps the player onto the nearest <see cref="GrindRail"/> within range -
    /// whether they're grounded or mid-air - and carries them along its path, automatically speeding
    /// up (faster while Sprint is held, faster downhill, slower uphill) up to a capped top speed,
    /// while a balance meter has to be fought against both ambient wobble and the outward pull of
    /// whatever turn the rail is currently making. Jumping off carries your current speed with you
    /// and lets it decay gradually in the air, rather than the flat "just detach" a balance beam does
    /// - grinding is meant to be chained rail-to-rail via jumps, not just crossed in a straight line.
    ///
    /// Key differences from BalanceBeamAbility (worth reading first - this mirrors its overall shape):
    /// - Forward/back stick does nothing while grinding; speed is fully automatic. Left/right stick
    ///   still only manages the balance meter, same as a beam.
    /// - Jump is a real jump here (see JumpOffRail), not a flat detach - it carries your rail speed
    ///   into the air as decaying momentum, specifically so jumping between rails is the intended way
    ///   to play rather than something to be discouraged (contrast with the balance beam's long
    ///   re-entry cooldown, which exists specifically to punish jumping/bunny-hopping).
    /// - Rail detection runs every frame the player isn't already on a rail, regardless of grounded
    ///   state or any cooldown, by proximity to the path (see GrindRail.ActiveRails) rather than a
    ///   downward SphereCast - so you can snap onto a rail mid-jump, including a different rail than
    ///   the one you just left. The only guard is a brief same-rail exclusion right after leaving one,
    ///   just to stop an immediate twitchy re-snap to the exact spot you just detached from.
    /// - Because the path isn't a single straight axis, this ability doesn't feed CoreMovement a
    ///   forward ArealVelocity and let CharacterController.Move carry the player along it (the way
    ///   BalanceBeamAbility does) - it advances an explicit arc-length value each frame and places the
    ///   player directly via CoreMovement.SetPosition, which handles curves without drifting off the
    ///   path the way an accumulated straight-line Move would.
    /// - A rail has no collider (see GrindRail), so CoreMovement.IsGrounded reads false the whole time
    ///   you're grinding - which breaks the two things BalanceBeamAbility got "for free" by actually
    ///   resting on a physical beam collider: (a) CorePlayerManager.HandleJump() only calls
    ///   CoreMovement.PerformJump() while grounded, so JumpRequested never becomes true here - jumping
    ///   off a rail instead has to be wired directly to the raw "jump pressed" GameEvent (see
    ///   onJumpPressed below), exactly the way WallSlideAbility and DoubleJumpAddon react to a jump
    ///   press while airborne; and (b) with nothing physically holding the character up, gravity would
    ///   otherwise integrate into vertical velocity every single frame while grinding even though
    ///   SnapToRail is placing the player exactly on the path - so ProcessGrinding explicitly zeroes
    ///   vertical velocity and sets OverrideGravity every frame it runs, rather than only once on
    ///   entry. Skipping either of these is what makes grinding intermittently yank the player off the
    ///   rail and immediately re-snap them.
    ///
    /// Same overall architecture otherwise: implements both IMovementAbility (CoreMovement's per-frame
    /// ability list) and IPlayerAddon (CorePlayerManager's addon list, for life-state cleanup and
    /// registering the jump-pressed listener), uses Priority 6 for the same reason BalanceBeamAbility
    /// does (before WalkAbility at 0, so it can zero WalkAbility's input before WalkAbility reads it),
    /// and drives its own copy of the world-space balance bar UI (see BalanceBeamAbility for why it's
    /// built in code rather than from a prefab).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class GrindRailAbility : NetworkBehaviour, IMovementAbility, IPlayerAddon
    {
        #region IMovementAbility

        public int Priority => 6;
        public float StaminaCost => 0f;

        #endregion

        [Header("Speed")]
        [Tooltip("Speed (in multiples of CoreMovement.moveSpeed) you accelerate toward while grinding on flat ground.")]
        [SerializeField] private float maxSpeedMultiplier = 2.5f;
        [Tooltip("Baseline acceleration along the rail, in m/s^2.")]
        [SerializeField] private float railAcceleration = 4f;
        [Tooltip("Extra acceleration added on top of the baseline while Sprint is held.")]
        [SerializeField] private float sprintAccelerationBonus = 4f;
        [Tooltip("Speed you never drop below while grinding, even riding straight uphill - as a multiple of moveSpeed.")]
        [SerializeField] private float minSpeedMultiplier = 0.5f;
        [Tooltip("Starting speed when snapping onto a rail from a standstill/walk (not carrying jump momentum), as a multiple of moveSpeed.")]
        [SerializeField] private float entrySpeedMultiplier = 0.6f;
        [Tooltip("How strongly slope affects speed - going downhill adds this much (scaled by how steep), uphill subtracts it.")]
        [SerializeField] private float slopeSpeedInfluence = 6f;

        [Header("Balance")]
        [Tooltip("Ambient random wobble strength (per second, at max speed) - present even on a flat, straight stretch of rail.")]
        [SerializeField] private float ambientTippingStrength = 1.2f;
        [Tooltip("How often the ambient wobble's direction is re-rolled.")]
        [SerializeField] private float minGustInterval = 0.2f;
        [SerializeField] private float maxGustInterval = 0.45f;
        [Tooltip("How hard a turn pushes the balance meter toward its outside (per degree of turn sharpness, per second, at max speed). Countering it means leaning INTO the turn - holding the stick the same direction the rail is bending.")]
        [SerializeField] private float turnLeanStrength = 0.05f;
        [Tooltip("How fast the left/right stick moves the balance meter (per second).")]
        [SerializeField] private float balanceControlRate = 2.5f;

        [Header("Falling Off")]
        [SerializeField] private float fallOffPushForce = 3f;
        [SerializeField] private float fallOffHop = 2f;
        [Tooltip("How long after leaving a rail before that SAME rail can snap you back on. Deliberately short - unlike a balance beam, snapping onto a rail (including the one you just left, once this expires, or any other rail immediately) is the whole point of grinding.")]
        [SerializeField] private float sameRailReentryGuard = 0.2f;
        [Tooltip("How close (meters) the player needs to be to a rail's path to snap onto it.")]
        [SerializeField] private float snapDetectionRadius = 0.9f;

        [Header("Jump Off")]
        [Tooltip("Multiplier applied to the player's normal jumpHeight when jumping off a rail.")]
        [SerializeField, Range(0.3f, 2f)] private float jumpOffHeightMultiplier = 1f;
        [Tooltip("How long after jumping (or grinding off the end of a rail) before carried momentum starts decaying - a short plateau so a rail-to-rail jump reads as 'keeping your speed', not an instant slowdown.")]
        [SerializeField] private float momentumDecayDelay = 0.35f;
        [Tooltip("How fast carried momentum decays once the delay above has passed - higher decays faster.")]
        [SerializeField] private float airborneMomentumDecayPerSecond = 1.2f;

        [Header("Input Event")]
        [Tooltip("Same GameEvent CoreInputHandler raises on jump press (drag the same asset assigned there, or use Friendslop > Add Grind Rail To Player which copies it automatically). Wired directly here rather than read through CoreMovement.JumpRequested, because a rail has no collider - CoreMovement.IsGrounded reads false the whole time you're grinding, and CorePlayerManager's own jump handling only fires while grounded. Same reason WallSlideAbility does this.")]
        [SerializeField] private GameEvent onJumpPressed;

        [Header("Balance Bar UI")]
        [SerializeField] private float barWorldWidth = 1.2f;
        [SerializeField] private float barWorldHeight = 0.14f;
        [SerializeField] private float indicatorWorldWidth = 0.06f;
        [SerializeField] private float headHeightOffset = 2.2f;
        [SerializeField] private Color barBackgroundColor = new Color(0f, 0f, 0f, 0.55f);
        [SerializeField] private Color indicatorSafeColor = Color.white;
        [SerializeField] private Color indicatorDangerColor = new Color(1f, 0.25f, 0.2f);

        public bool IsOnRail { get; private set; }
        public bool IsOnRailNetworked => m_NetIsOnRail.Value;
        public float BalanceNetworked => m_NetBalance.Value;

        private readonly NetworkVariable<bool> m_NetIsOnRail = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> m_NetBalance = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private bool m_IsActive = true;
        private bool m_ListenersRegistered;

        private GrindRail m_CurrentRail;
        private float m_ArcLength;
        private float m_TravelSign = 1f;
        private float m_Speed;

        private float m_Balance;
        private float m_GustDirection;
        private float m_GustTimer;

        private GrindRail m_LastRail;
        private float m_LastRailGuardTimer;

        private Vector3 m_AirborneMomentum;
        private float m_MomentumAirTime;

        private Canvas m_BalanceCanvas;
        private RectTransform m_IndicatorRect;
        private Image m_IndicatorImage;
        private Camera m_MainCamera;

        #region Unity Lifecycle

        private void Awake()
        {
            BuildBalanceUI();
        }

        private void LateUpdate()
        {
            UpdateBalanceUI();
        }

        #endregion

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

            if (m_LastRailGuardTimer > 0f)
            {
                m_LastRailGuardTimer -= Time.deltaTime;
            }

            if (!IsOnRail)
            {
                if (m_Motor.IsGrounded)
                {
                    // Landed safely without snapping to anything - don't keep sliding on carried
                    // rail speed once you're back on solid, non-rail ground.
                    m_AirborneMomentum = Vector3.zero;
                    m_MomentumAirTime = 0f;
                }
                else
                {
                    ProcessAirborneMomentum(ref modifier);
                }

                if (TryDetectRail(out GrindRail rail, out float arc, out float travelSign))
                {
                    EnterRail(rail, arc, travelSign);
                }

                return modifier;
            }

            // From here on this ability owns 100% of this frame's movement - zero out WalkAbility's
            // contribution before it runs (see the Priority discussion in the class summary). Jumping
            // off is handled separately via HandleJumpPressed (the raw jump-pressed event), not here -
            // see the class summary for why CoreMovement.JumpRequested doesn't work for a rail.
            Vector2 rawInput = m_Motor.MoveInput;
            m_Motor.SetMoveInput(Vector2.zero);

            ProcessGrinding(rawInput, ref modifier);

            SyncNetworkState();
            return modifier;
        }

        public bool TryActivate() => false;

        #endregion

        #region IPlayerAddon

        public void Initialize(CorePlayerManager playerManager)
        {
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

            if (IsOwner)
            {
                m_AirborneMomentum = Vector3.zero;
                ExitRail();
            }
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;

            if (!m_IsActive && IsOwner)
            {
                m_AirborneMomentum = Vector3.zero;
                ExitRail();
            }
        }

        #endregion

        #region Grinding

        private void ProcessGrinding(Vector2 rawInput, ref MovementModifier modifier)
        {
            // A rail has no collider, so CoreMovement.IsGrounded is false the whole time we're on one -
            // without this, gravity integrates into vertical velocity every frame regardless of
            // SnapToRail placing the player exactly on the path, and the accumulated fall gets applied
            // on top immediately after, yanking the player off the rail. Zeroing it and overriding
            // gravity every frame (not just once on entry) is what actually keeps that from happening.
            m_Motor.SetVerticalVelocity(0f);
            modifier.OverrideGravity = true;

            m_CurrentRail.Evaluate(m_ArcLength, out _, out Vector3 tangent, out _, out float turnSharpness);
            Vector3 travelTangent = m_TravelSign >= 0f ? tangent : -tangent;

            float maxSpeed = maxSpeedMultiplier * m_Motor.moveSpeed;
            float minSpeed = minSpeedMultiplier * m_Motor.moveSpeed;
            float accel = railAcceleration + (m_Motor.IsSprinting ? sprintAccelerationBonus : 0f);

            // Downhill (tangent pointing down along the direction you're actually travelling) speeds
            // you up, uphill slows you down - travelTangent.y is negative heading downhill.
            float slopeAccel = -travelTangent.y * slopeSpeedInfluence;

            m_Speed = Mathf.Clamp(m_Speed + (accel + slopeAccel) * Time.deltaTime, minSpeed, maxSpeed);

            float speedFactor = Mathf.Clamp01(m_Speed / Mathf.Max(0.01f, maxSpeed));
            UpdateBalance(rawInput.x, speedFactor, turnSharpness);

            if (Mathf.Abs(m_Balance) >= 1f)
            {
                FallOff();
                return;
            }

            float rawArc = m_ArcLength + m_TravelSign * m_Speed * Time.deltaTime;
            if (!m_CurrentRail.IsArcWithinRail(rawArc))
            {
                // Ran off the end under your own speed - carry that speed into the air as momentum
                // rather than just stopping dead, so a well-timed jump off the tail of a rail still
                // reaches the next one.
                m_ArcLength = Mathf.Clamp(rawArc, 0f, m_CurrentRail.TotalLength);
                m_AirborneMomentum = travelTangent * m_Speed;
                m_MomentumAirTime = 0f;
                ExitRail();
                return;
            }

            m_ArcLength = rawArc;
            SnapToRail();
        }

        private void UpdateBalance(float stickX, float speedFactor, float turnSharpness)
        {
            m_GustTimer -= Time.deltaTime;
            if (m_GustTimer <= 0f)
            {
                m_GustDirection = Random.Range(-1f, 1f);
                m_GustTimer = Random.Range(minGustInterval, maxGustInterval);
            }

            // Two things try to tip you: a constant ambient wobble (present even on a flat, straight
            // stretch) and the turn itself - the sharper the current bend, the harder it pushes you
            // toward its outside, so countering it means actively leaning INTO the turn (holding the
            // stick toward the direction the rail is bending) rather than away from it.
            float ambient = m_GustDirection * ambientTippingStrength;
            float turnPush = -turnSharpness * turnLeanStrength;

            m_Balance += (ambient + turnPush) * speedFactor * Time.deltaTime;
            m_Balance += stickX * balanceControlRate * Time.deltaTime;
            m_Balance = Mathf.Clamp(m_Balance, -1f, 1f);
        }

        /// <summary>Places the player at the current arc length, offset up by the rail's radius and sideways by the current balance lean.</summary>
        private void SnapToRail()
        {
            m_CurrentRail.Evaluate(m_ArcLength, out Vector3 point, out _, out Vector3 right, out _);
            Vector3 lateral = right * (Mathf.Clamp(m_Balance, -1f, 1f) * m_CurrentRail.Radius * 0.6f);
            Vector3 ridePos = point + Vector3.up * m_CurrentRail.Radius + lateral;
            m_Motor.SetPosition(ridePos, teleport: false);
        }

        private void EnterRail(GrindRail rail, float arc, float travelSign)
        {
            m_CurrentRail = rail;
            IsOnRail = true;
            m_ArcLength = arc;
            m_TravelSign = travelSign;
            m_Balance = 0f;
            m_GustTimer = 0f;

            float carriedSpeed = m_AirborneMomentum.magnitude;
            m_Speed = Mathf.Max(entrySpeedMultiplier * m_Motor.moveSpeed, carriedSpeed);
            m_AirborneMomentum = Vector3.zero;
            m_MomentumAirTime = 0f;

            SnapToRail();
            m_Motor.SetVerticalVelocity(0f);

            m_Motor.RotationOverride = () => m_CurrentRail != null ? CurrentTangentRotation() : m_Motor.transform.rotation;

            SyncNetworkState();
        }

        private Quaternion CurrentTangentRotation()
        {
            m_CurrentRail.Evaluate(m_ArcLength, out _, out Vector3 tangent, out _, out _);
            Vector3 travelTangent = m_TravelSign >= 0f ? tangent : -tangent;
            return Quaternion.LookRotation(travelTangent, Vector3.up);
        }

        /// <summary>Leaves the current rail. Always applies the (short) same-rail re-entry guard - see the class summary for why that's brief here, unlike BalanceBeamAbility's long one.</summary>
        private void ExitRail()
        {
            if (!IsOnRail) return;

            m_LastRail = m_CurrentRail;
            m_LastRailGuardTimer = sameRailReentryGuard;

            IsOnRail = false;
            m_CurrentRail = null;

            if (m_Motor != null)
            {
                m_Motor.RotationOverride = null;
            }

            SyncNetworkState();
        }

        private void FallOff()
        {
            m_CurrentRail.Evaluate(m_ArcLength, out _, out _, out Vector3 right, out _);
            Vector3 outward = right * Mathf.Sign(m_Balance == 0f ? 1f : m_Balance);
            m_Motor.ApplyExternalForce(outward * fallOffPushForce, ForceMode.Impulse);
            m_Motor.SetVerticalVelocity(fallOffHop);

            // A stumble, not a clean exit - you don't get to keep your grind speed.
            m_AirborneMomentum = Vector3.zero;
            m_MomentumAirTime = 0f;

            ExitRail();
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

        /// <summary>
        /// Reacts to the raw jump-pressed event rather than CoreMovement.JumpRequested - see the class
        /// summary for why (no collider under a rail means IsGrounded is false, so
        /// CorePlayerManager.HandleJump never sets JumpRequested while grinding). Mirrors
        /// WallSlideAbility.HandleJumpPressed: a normal grounded jump goes through JumpAbility/
        /// CorePlayerManager instead, so this only ever needs to care about jumping off a rail.
        /// </summary>
        private void HandleJumpPressed()
        {
            if (!m_IsActive || !IsOwner || m_Motor == null) return;
            if (!IsOnRail) return;

            JumpOffRail();
        }

        /// <summary>
        /// A real jump, carrying current rail speed with it - see the class summary for why this is
        /// deliberately different from BalanceBeamAbility's flat detach. Sets vertical velocity and
        /// carried momentum directly (like WallSlideAbility.PerformWallJump) rather than through a
        /// MovementModifier - there isn't one live here, since this runs from the raw input event, not
        /// from inside Process(). The very next Process() call will see IsOnRail false and pick up
        /// m_AirborneMomentum through ProcessAirborneMomentum instead.
        /// </summary>
        private void JumpOffRail()
        {
            m_CurrentRail.Evaluate(m_ArcLength, out _, out Vector3 tangent, out _, out _);
            Vector3 travelTangent = m_TravelSign >= 0f ? tangent : -tangent;

            float jumpVelocity = Mathf.Sqrt(Mathf.Max(0.01f, jumpOffHeightMultiplier * m_Motor.jumpHeight) * -2f * m_Motor.gravity);
            m_Motor.SetVerticalVelocity(jumpVelocity);

            m_AirborneMomentum = travelTangent * m_Speed;
            m_MomentumAirTime = 0f;

            ExitRail();
        }

        private void ProcessAirborneMomentum(ref MovementModifier modifier)
        {
            if (m_AirborneMomentum.sqrMagnitude < 0.01f)
            {
                m_AirborneMomentum = Vector3.zero;
                m_MomentumAirTime = 0f;
                return;
            }

            modifier.ArealVelocity = m_AirborneMomentum;

            m_MomentumAirTime += Time.deltaTime;
            if (m_MomentumAirTime <= momentumDecayDelay) return;

            float decay = Mathf.Clamp01(airborneMomentumDecayPerSecond * Time.deltaTime);
            m_AirborneMomentum = Vector3.Lerp(m_AirborneMomentum, Vector3.zero, decay);
        }

        #endregion

        #region Detection

        /// <summary>
        /// Scans every active rail for the closest playable point within snapDetectionRadius,
        /// regardless of grounded state. A rail this ability just left is skipped while its own
        /// sameRailReentryGuard timer is still running, but every other rail (and that same rail once
        /// the guard expires) is fair game immediately - see the class summary.
        /// </summary>
        private bool TryDetectRail(out GrindRail foundRail, out float arc, out float travelSign)
        {
            foundRail = null;
            arc = 0f;
            travelSign = 1f;

            float bestDist = snapDetectionRadius;
            Vector3 playerPos = m_Motor.transform.position;

            IReadOnlyList<GrindRail> rails = GrindRail.ActiveRails;
            for (int i = 0; i < rails.Count; i++)
            {
                GrindRail rail = rails[i];
                if (rail == null) continue;
                if (rail == m_LastRail && m_LastRailGuardTimer > 0f) continue;

                float candidateArc = rail.ProjectToArcLength(playerPos, out bool withinRail);
                if (!withinRail) continue;

                rail.Evaluate(candidateArc, out Vector3 point, out Vector3 tangent, out _, out _);
                Vector3 riderPos = point + Vector3.up * rail.Radius;
                float dist = Vector3.Distance(playerPos, riderPos);
                if (dist >= bestDist) continue;

                bestDist = dist;
                foundRail = rail;
                arc = candidateArc;

                Vector3 approachDir = m_AirborneMomentum.sqrMagnitude > 0.01f ? m_AirborneMomentum.normalized : m_Motor.transform.forward;
                travelSign = Vector3.Dot(approachDir, tangent) < 0f ? -1f : 1f;
            }

            return foundRail != null;
        }

        #endregion

        #region Networking

        private void SyncNetworkState()
        {
            if (!IsOwner) return;

            if (m_NetIsOnRail.Value != IsOnRail) m_NetIsOnRail.Value = IsOnRail;
            m_NetBalance.Value = m_Balance;
        }

        #endregion

        #region Balance Bar UI

        private void BuildBalanceUI()
        {
            var canvasGO = new GameObject("GrindBalanceBarCanvas");
            canvasGO.transform.SetParent(transform, false);
            canvasGO.transform.localPosition = Vector3.up * headHeightOffset;
            canvasGO.transform.localScale = Vector3.one;

            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta = new Vector2(barWorldWidth, barWorldHeight);

            CreateBarImage(canvasRect, "Background", new Vector2(barWorldWidth, barWorldHeight), barBackgroundColor);

            m_IndicatorImage = CreateBarImage(canvasRect, "Indicator", new Vector2(indicatorWorldWidth, barWorldHeight * 1.3f), indicatorSafeColor);
            m_IndicatorRect = m_IndicatorImage.rectTransform;

            m_BalanceCanvas = canvas;
            canvasGO.SetActive(false);
        }

        private Image CreateBarImage(RectTransform parent, string name, Vector2 size, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.color = color;

            var rt = image.rectTransform;
            rt.sizeDelta = size;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;

            return image;
        }

        private void UpdateBalanceUI()
        {
            if (m_BalanceCanvas == null) return;

            bool visible = IsOnRailNetworked;
            if (m_BalanceCanvas.gameObject.activeSelf != visible)
            {
                m_BalanceCanvas.gameObject.SetActive(visible);
            }

            if (!visible) return;

            float balance = BalanceNetworked;

            if (m_IndicatorRect != null)
            {
                float travel = (barWorldWidth - indicatorWorldWidth) * 0.5f;
                m_IndicatorRect.anchoredPosition = new Vector2(balance * travel, 0f);
            }

            if (m_IndicatorImage != null)
            {
                m_IndicatorImage.color = Color.Lerp(indicatorSafeColor, indicatorDangerColor, Mathf.Abs(balance));
            }

            if (m_MainCamera == null) m_MainCamera = Camera.main;
            if (m_MainCamera == null) return;

            m_BalanceCanvas.transform.LookAt(m_MainCamera.transform);
            m_BalanceCanvas.transform.Rotate(0, 180, 0);
        }

        #endregion
    }
}
