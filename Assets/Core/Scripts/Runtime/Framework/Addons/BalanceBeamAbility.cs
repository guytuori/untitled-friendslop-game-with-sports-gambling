using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Balance-beam traversal: snaps the player onto a <see cref="BalanceBeam"/> when they land (or
    /// walk) on one, locks movement to the beam's length, and drives a small over-the-head balance
    /// meter that randomly drifts (faster movement = more drift) and has to be corrected with the
    /// left/right stick or it tips the player off.
    ///
    /// Movement while on a beam:
    /// - Forward/back stick moves along the beam - walk speed while Sprint is held, a slower
    ///   dedicated "careful" speed otherwise. This intentionally does NOT use CoreMovement's normal
    ///   ground speeds directly for the slow case, since a beam should feel more deliberate than
    ///   regular walking.
    /// - Left/right stick doesn't move the player sideways at all - it only pushes the balance meter,
    ///   same as the passive random drift does.
    /// - Jump has a longer wind-up (jumpSquatDuration) and a reduced height (beamJumpHeightMultiplier
    ///   of the player's normal jump height) with zero carried horizontal momentum, specifically so
    ///   bunny-hopping across a beam is worse than just walking it, not a shortcut past the balance
    ///   mechanic.
    ///
    /// How this avoids fighting the rest of the movement stack: WalkAbility's own contribution is
    /// zeroed out every frame while on a beam (by feeding it Vector2.zero input before it runs - see
    /// Priority below), and this ability supplies 100% of the horizontal/vertical movement itself
    /// instead. Priority 6 is deliberately between JumpAbility/PlatformerLocomotionAbility (10) and
    /// WalkAbility (0): high enough to react to - and override - a same-frame jump *after* those
    /// abilities have already applied it (so the squat/reduced-height behavior can cleanly replace a
    /// normal jump), but still low enough to run before WalkAbility so its input can be zeroed before
    /// WalkAbility reads it. No FinalMoveCalculationOverride/RotationOverride conflicts with something
    /// like PlatformerMovingPlatformAbility are expected in practice (you can't be on a moving
    /// platform and a beam at once), but exiting the beam always restores RotationOverride to null
    /// either way.
    ///
    /// Like WallSlideAbility, this implements both IMovementAbility (added to CoreMovement's per-frame
    /// ability list) and IPlayerAddon (added to CorePlayerManager's addon list, for OnLifeStateChanged
    /// cleanup). Unlike WallSlideAbility it doesn't need any GameEvent wired up - it reads
    /// CoreMovement.JumpRequested directly, which already gets set correctly for a grounded jump (and
    /// standing on a beam counts as grounded) via the existing CorePlayerManager -> PerformJump path.
    ///
    /// The balance meter is a small world-space UI bar built entirely in code in Awake (no prefab
    /// dependency) and positioned above the player's head, billboarded to the camera like
    /// NamePlateAddon. Two NetworkVariables mirror the on-beam state and balance value so remote
    /// clients can see it too, since the movement logic itself only ever runs on the owner (mirroring
    /// how CorePlayerState syncs name/life-state).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class BalanceBeamAbility : NetworkBehaviour, IMovementAbility, IPlayerAddon
    {
        #region IMovementAbility

        public int Priority => 6;
        public float StaminaCost => 0f;

        #endregion

        [Header("Careful vs. Sprint Speed")]
        [Tooltip("Speed while moving along the beam without Sprint held. Deliberately its own value rather than a fraction of moveSpeed, since a beam should feel more careful than regular walking regardless of how the ground speeds are tuned.")]
        [SerializeField] private float beamCarefulSpeed = 1.5f;
        // Speed while Sprint IS held is CoreMovement.moveSpeed - i.e. normal walk speed, not sprint
        // speed. A beam caps you at a walk even while sprinting, so there's no separate field for it.

        [Header("Balance")]
        [Tooltip("How fast the balance meter drifts (per second, at max beam speed) toward whichever side the current random gust is pushing.")]
        [SerializeField] private float tippingStrength = 0.5f;
        [Tooltip("How often a new random gust direction/strength is rolled.")]
        [SerializeField] private float minGustInterval = 0.4f;
        [SerializeField] private float maxGustInterval = 0.9f;
        [Tooltip("How fast the left/right stick moves the balance meter (per second). Positive stick input pushes the meter the same direction, so correcting a rightward tip means holding left.")]
        [SerializeField] private float balanceControlRate = 1.4f;

        [Header("Jump Off")]
        [Tooltip("How much longer than a normal jump the wind-up takes before actually leaving the beam.")]
        [SerializeField] private float jumpSquatDuration = 0.45f;
        [Tooltip("Multiplier applied to the player's normal jumpHeight for a beam jump-off.")]
        [SerializeField, Range(0.1f, 1f)] private float beamJumpHeightMultiplier = 0.5f;

        [Header("Falling Off")]
        [Tooltip("Sideways stumble impulse applied when the balance meter maxes out.")]
        [SerializeField] private float fallOffPushForce = 3f;
        [Tooltip("Small upward hop applied on top of the sideways stumble, so it reads as losing footing rather than just sliding off.")]
        [SerializeField] private float fallOffHop = 2f;
        [Tooltip("How long after falling off (balance maxed out) or jumping off before the beam can snap the player back on. Without this, snapping is precise enough that a fall or a jump lands you right back where you started and you're immediately back on the beam - this is what actually stops that, more than the reduced jump height/momentum alone. Doesn't apply when you simply walk off the far end onto solid ground or the next beam, since that's a successful crossing, not a failure.")]
        [SerializeField] private float reEntryCooldown = 0.75f;

        [Header("Balance Bar UI")]
        [SerializeField] private float barWorldWidth = 1.2f;
        [SerializeField] private float barWorldHeight = 0.14f;
        [SerializeField] private float indicatorWorldWidth = 0.06f;
        [SerializeField] private float headHeightOffset = 2.2f;
        [SerializeField] private Color barBackgroundColor = new Color(0f, 0f, 0f, 0.55f);
        [SerializeField] private Color indicatorSafeColor = Color.white;
        [SerializeField] private Color indicatorDangerColor = new Color(1f, 0.25f, 0.2f);

        /// <summary>True while actively on a beam (owner-authoritative; see IsOnBeamNetworked for the synced version other clients should read).</summary>
        public bool IsOnBeam { get; private set; }

        /// <summary>Networked, read-anywhere version of <see cref="IsOnBeam"/> - what the UI actually displays from.</summary>
        public bool IsOnBeamNetworked => m_NetIsOnBeam.Value;

        /// <summary>Networked, read-anywhere balance value, -1 (fully left) to 1 (fully right).</summary>
        public float BalanceNetworked => m_NetBalance.Value;

        private readonly NetworkVariable<bool> m_NetIsOnBeam = new NetworkVariable<bool>(
            false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        private readonly NetworkVariable<float> m_NetBalance = new NetworkVariable<float>(
            0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private BalanceBeam m_CurrentBeam;
        private bool m_IsActive = true;

        private float m_Balance;
        private float m_GustDirection;
        private float m_GustTimer;

        private bool m_IsSquatting;
        private float m_SquatTimer;

        private float m_ReEntryCooldownTimer;

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

            if (!IsOnBeam)
            {
                if (m_ReEntryCooldownTimer > 0f)
                {
                    m_ReEntryCooldownTimer -= Time.deltaTime;
                }
                else if (m_Motor.IsGrounded && TryDetectBeamBelow(out BalanceBeam beam))
                {
                    EnterBeam(beam);
                }
                return modifier;
            }

            // From here on, this ability owns 100% of this frame's movement for the player - zero out
            // WalkAbility's contribution *before* it runs (see the Priority discussion in the class
            // summary) so the two don't fight.
            Vector2 rawInput = m_Motor.MoveInput;
            m_Motor.SetMoveInput(Vector2.zero);

            if (m_IsSquatting)
            {
                ProcessJumpSquat(ref modifier);
            }
            else if (m_Motor.JumpRequested)
            {
                BeginJumpSquat(ref modifier);
            }
            else
            {
                ProcessBeamMovement(rawInput, ref modifier);
            }

            SyncNetworkState();
            return modifier;
        }

        public bool TryActivate() => false;

        #endregion

        #region IPlayerAddon

        public void Initialize(CorePlayerManager playerManager)
        {
            // No additional references needed today - kept for symmetry with the other addons and in
            // case a future stamina cost for beam jumps wants CoreStats, the way DoubleJumpAddon does.
        }

        public void OnPlayerSpawn()
        {
        }

        public void OnPlayerDespawn()
        {
            if (IsOwner)
            {
                ExitBeam();
            }
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;

            if (!m_IsActive && IsOwner)
            {
                ExitBeam();
            }
        }

        #endregion

        #region Beam Movement

        private void ProcessBeamMovement(Vector2 rawInput, ref MovementModifier modifier)
        {
            float speed = m_Motor.IsSprinting ? m_Motor.moveSpeed : beamCarefulSpeed;
            float alongAmount = Mathf.Clamp(rawInput.y, -1f, 1f);
            modifier.ArealVelocity = m_CurrentBeam.Forward * (alongAmount * speed);

            UpdateBalance(rawInput.x, Mathf.Abs(alongAmount) * speed);

            if (Mathf.Abs(m_Balance) >= 1f)
            {
                FallOff();
                return;
            }

            SnapToBeam();
        }

        private void UpdateBalance(float stickX, float currentSpeed)
        {
            m_GustTimer -= Time.deltaTime;
            if (m_GustTimer <= 0f)
            {
                m_GustDirection = Random.Range(-1f, 1f);
                m_GustTimer = Random.Range(minGustInterval, maxGustInterval);
            }

            float speedFactor = Mathf.Clamp01(currentSpeed / Mathf.Max(0.01f, m_Motor.moveSpeed));
            m_Balance += m_GustDirection * tippingStrength * speedFactor * Time.deltaTime;
            m_Balance += stickX * balanceControlRate * Time.deltaTime;
            m_Balance = Mathf.Clamp(m_Balance, -1f, 1f);
        }

        /// <summary>
        /// Corrects the player's position back onto the beam's centerline (offset sideways by the
        /// current balance) and top surface every frame, leaving the along-beam (Z) component alone
        /// so the CharacterController.Move() driven by this frame's ArealVelocity still handles actual
        /// forward/back progress and end-of-beam collision normally.
        /// </summary>
        private void SnapToBeam()
        {
            Vector3 snapped = m_CurrentBeam.GetSnapPosition(m_Motor.transform.position, m_Balance, out _, out bool withinBeam);

            if (!withinBeam)
            {
                ExitBeam();
                return;
            }

            m_Motor.SetPosition(snapped, teleport: false);
        }

        private void EnterBeam(BalanceBeam beam)
        {
            m_CurrentBeam = beam;
            IsOnBeam = true;
            m_Balance = 0f;
            m_GustTimer = 0f;
            m_IsSquatting = false;
            m_SquatTimer = 0f;

            Vector3 snapped = beam.GetSnapPosition(m_Motor.transform.position, m_Balance, out _, out _);
            m_Motor.SetPosition(snapped, teleport: false);
            m_Motor.SetVerticalVelocity(0f);

            m_Motor.RotationOverride = () => m_CurrentBeam != null
                ? Quaternion.LookRotation(m_CurrentBeam.Forward, Vector3.up)
                : m_Motor.transform.rotation;

            SyncNetworkState();
        }

        /// <summary>
        /// Leaves the beam. <paramref name="applyReentryCooldown"/> should be true for a *failure*
        /// exit (falling off, jumping off) so the player can't immediately snap back on - and false
        /// for successfully walking off either end, so crossing onto solid ground or the next beam
        /// isn't penalized the same way a mistake is.
        /// </summary>
        private void ExitBeam(bool applyReentryCooldown = false)
        {
            if (!IsOnBeam) return;

            IsOnBeam = false;
            m_CurrentBeam = null;
            m_IsSquatting = false;

            if (applyReentryCooldown)
            {
                m_ReEntryCooldownTimer = reEntryCooldown;
            }

            if (m_Motor != null)
            {
                m_Motor.RotationOverride = null;
            }

            SyncNetworkState();
        }

        private void FallOff()
        {
            Vector3 outward = m_CurrentBeam.Right * Mathf.Sign(m_Balance == 0f ? 1f : m_Balance);
            m_Motor.ApplyExternalForce(outward * fallOffPushForce, ForceMode.Impulse);

            // The hop has to go through SetVerticalVelocity, not ApplyExternalForce - CoreMovement's
            // ProcessAbilities() always does `movement.y = m_VerticalVelocity` *after* summing in any
            // external force, so an external force's own Y component is silently thrown away every
            // frame. Feeding it through ApplyExternalForce (as this used to) meant the "hop" never
            // actually happened: the character barely left grounded state, so the very next frame's
            // TryDetectBeamBelow immediately found the same beam underneath and snapped right back on.
            m_Motor.SetVerticalVelocity(fallOffHop);

            ExitBeam(applyReentryCooldown: true);
        }

        #endregion

        #region Jump Off

        private void BeginJumpSquat(ref MovementModifier modifier)
        {
            m_IsSquatting = true;
            m_SquatTimer = jumpSquatDuration;

            // A normal grounded jump was just requested and CorePlayerManager's own gate is satisfied
            // (standing on a beam counts as grounded), so JumpAbility / PlatformerLocomotionAbility -
            // both higher priority, so they already ran this frame - will have applied a full-height
            // jump velocity already. Cancel it; the squat below replaces it.
            m_Motor.SetVerticalVelocity(0f);
            modifier.OverrideGravity = true;
            modifier.ArealVelocity = Vector3.zero;

            SnapToBeam();
        }

        private void ProcessJumpSquat(ref MovementModifier modifier)
        {
            // Hold completely still and grounded-feeling for the duration of the squat.
            m_Motor.SetVerticalVelocity(0f);
            modifier.OverrideGravity = true;
            modifier.ArealVelocity = Vector3.zero;
            SnapToBeam();

            m_SquatTimer -= Time.deltaTime;
            if (m_SquatTimer > 0f) return;

            float jumpVelocity = Mathf.Sqrt(m_Motor.jumpHeight * beamJumpHeightMultiplier * -2f * m_Motor.gravity);
            m_Motor.SetVerticalVelocity(jumpVelocity);
            modifier.OverrideGravity = true;
            // No carried horizontal momentum - a beam jump-off is a near-vertical hop by design.
            // Combined with the re-entry cooldown below, landing back on the same spot doesn't
            // immediately re-snap: for reEntryCooldown seconds you're just a normal grounded character
            // standing on a ~0.3m wide surface with no centerline lock, which is precarious enough on
            // its own that repeatedly tapping Jump to "bunny-hop" across is worse than walking it, not
            // a shortcut past the balance mechanic.
            ExitBeam(applyReentryCooldown: true);
        }

        #endregion

        #region Detection

        private bool TryDetectBeamBelow(out BalanceBeam beam)
        {
            beam = null;
            float checkDistance = (m_Controller.height * 0.5f) - m_Controller.radius + 0.15f;

            if (Physics.SphereCast(m_Controller.center + m_Motor.transform.position, m_Controller.radius, Vector3.down,
                    out RaycastHit hit, checkDistance, m_Motor.groundLayers, QueryTriggerInteraction.Ignore))
            {
                return hit.collider.TryGetComponent(out beam);
            }

            return false;
        }

        #endregion

        #region Networking

        private void SyncNetworkState()
        {
            if (!IsOwner) return;

            if (m_NetIsOnBeam.Value != IsOnBeam) m_NetIsOnBeam.Value = IsOnBeam;
            m_NetBalance.Value = m_Balance;
        }

        #endregion

        #region Balance Bar UI

        private void BuildBalanceUI()
        {
            var canvasGO = new GameObject("BalanceBarCanvas");
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

            bool visible = IsOnBeamNetworked;
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

            // Billboard toward the camera, same correction NamePlateAddon uses.
            m_BalanceCanvas.transform.LookAt(m_MainCamera.transform);
            m_BalanceCanvas.transform.Rotate(0, 180, 0);
        }

        #endregion
    }
}
