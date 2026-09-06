using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Fireman's-pole traversal: while the player holds Grab within range of a <see cref="Pole"/>,
    /// snaps them onto its surface. From there, forward/back input climbs up/down the pole and
    /// left/right input shimmies clockwise/counterclockwise around it - both at half normal walk
    /// speed - while continuously draining stamina. Pressing Jump kicks the player off (same shape as
    /// a wall jump); releasing Grab just drops them into freefall.
    ///
    /// A pole's CapsuleCollider stays solid the whole time (see Pole's class summary) - this ability
    /// never puts the player inside it, only ever positions them at the collider's own radius plus the
    /// character controller's radius, i.e. right up against its outer surface, the same way gripping a
    /// real pole from outside never puts your body inside it.
    ///
    /// Detection is proximity-based (see TryDetectPole), scanning Pole.ActivePoles the same way
    /// GrindRailAbility scans GrindRail.ActiveRails - not a raycast, since "is Grab held and are we
    /// close enough" is the natural test for reaching out and grabbing a pole, unlike WallClimbAbility's
    /// raycast (better suited to detecting a wall you're jumping toward).
    ///
    /// Same overall architecture as BalanceBeamAbility / GrindRailAbility / WallClimbAbility: implements
    /// both IMovementAbility (CoreMovement's per-frame ability list, so it can drive position directly
    /// and zero out WalkAbility's contribution while attached) and IPlayerAddon (for the jump-off input
    /// listener and life-state cleanup).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PoleGrabAbility : NetworkBehaviour, IMovementAbility, IPlayerAddon
    {
        #region IMovementAbility

        /// <summary>
        /// Priority 8 - the same slot BalanceBeamAbility/GrindRailAbility occupy relative to the rest
        /// of the stack (between JumpAbility's 10 and WalkAbility's 0), high enough to zero
        /// WalkAbility's input before it runs. The jump-off itself doesn't depend on this ordering at
        /// all (see HandleJumpPressed) since a pole leaves the player ungrounded, just like a grind
        /// rail - CoreMovement.JumpRequested never becomes true while attached.
        /// </summary>
        public int Priority => 8;

        /// <summary>Continuous stamina drain is handled directly in Process() below, not via this.</summary>
        public float StaminaCost => 0f;

        #endregion

        [Header("Detection")]
        [Tooltip("How close (world units, from the pole's outer surface) the player needs to be to grab a pole while holding Grab. Falls back to Pole.GrabRange per-pole if you want some poles easier to reach than others - this is the ability-side default search radius.")]
        [SerializeField] private float grabDetectionRange = 1.0f;

        [Header("Movement")]
        [Tooltip("Vertical climb speed, as a fraction of CoreMovement.moveSpeed.")]
        [SerializeField] private float climbSpeedMultiplier = 0.5f;
        [Tooltip("Shimmy speed around the pole's circumference, as a fraction of CoreMovement.moveSpeed.")]
        [SerializeField] private float shimmySpeedMultiplier = 0.5f;

        [Header("Stamina")]
        [Tooltip("Stamina drained per second while attached to a pole.")]
        [SerializeField] private float staminaCostPerSecond = 8f;

        [Header("Jump Off")]
        [Tooltip("Multiplier applied to the player's normal jumpHeight when jumping off a pole.")]
        [SerializeField] private float jumpOffHeightMultiplier = 1f;
        [Tooltip("Outward push away from the pole, applied as an instantaneous force - same shape as a wall jump's push.")]
        [SerializeField] private float jumpOffPushForce = 6f;
        [Tooltip("How long after jumping off before the same pole can be re-grabbed - stops immediately re-sticking to the pole you just launched off of.")]
        [SerializeField] private float regrabCooldown = 0.25f;

        [Header("Input Event")]
        [Tooltip("Same GameEvent CoreInputHandler raises on jump press (drag the same asset assigned there). Wired directly here because a pole leaves the player ungrounded, so CoreMovement.JumpRequested never becomes true while attached - same reason GrindRailAbility/WallClimbAbility react to the raw event instead of the grounded jump path.")]
        [SerializeField] private GameEvent onJumpPressed;

        /// <summary>True while actively attached to a pole.</summary>
        public bool IsOnPole { get; private set; }

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private CoreStatsHandler m_CoreStats;
        private bool m_IsActive = true;
        private bool m_ListenersRegistered;

        private Pole m_CurrentPole;
        private float m_HeightAlongAxis;
        private float m_AngleDegrees;
        private Vector3 m_LastOutwardDir = Vector3.forward;
        private float m_RegrabTimer;
        private Pole m_LastPole;

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

            if (!IsOnPole)
            {
                if (m_Motor.IsGrabHeld && TryDetectPole(out Pole pole, out float angle, out float height, out Vector3 outward))
                {
                    EnterPole(pole, angle, height, outward);
                }
                return modifier;
            }

            if (!m_Motor.IsGrabHeld)
            {
                // Letting go simply drops you into freefall - no push, no cooldown, just release.
                ExitPole(applyRegrabCooldown: false);
                return modifier;
            }

            float cost = staminaCostPerSecond * Time.deltaTime;
            if (m_CoreStats != null && !m_CoreStats.TryConsumeStat(StatKeys.Stamina, cost, OwnerClientId))
            {
                // Ran out of stamina mid-climb - lose your grip rather than quietly stopping in place.
                ExitPole(applyRegrabCooldown: true);
                return modifier;
            }

            // From here on this ability owns 100% of this frame's movement - zero out WalkAbility's
            // contribution before it runs (see the Priority discussion above), same as
            // BalanceBeamAbility/GrindRailAbility.
            Vector2 rawInput = m_Motor.MoveInput;
            m_Motor.SetMoveInput(Vector2.zero);

            ProcessPoleMovement(rawInput, ref modifier);
            return modifier;
        }

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

            if (IsOwner)
            {
                ExitPole(applyRegrabCooldown: false);
            }
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;

            if (!m_IsActive && IsOwner)
            {
                ExitPole(applyRegrabCooldown: false);
            }
        }

        #endregion

        #region Pole Movement

        private void ProcessPoleMovement(Vector2 rawInput, ref MovementModifier modifier)
        {
            float climbSpeed = m_Motor.moveSpeed * climbSpeedMultiplier;
            float shimmySpeed = m_Motor.moveSpeed * shimmySpeedMultiplier;

            float halfHeight = m_CurrentPole.WorldHalfHeight;
            m_HeightAlongAxis = Mathf.Clamp(m_HeightAlongAxis + rawInput.y * climbSpeed * Time.deltaTime, -halfHeight, halfHeight);

            float radius = m_CurrentPole.WorldRadius + m_Controller.radius;
            float circumference = 2f * Mathf.PI * Mathf.Max(0.01f, radius);
            float degreesPerSecond = (shimmySpeed / circumference) * 360f;
            // Positive stick-right shimmies clockwise, viewed from above looking down the pole's axis.
            m_AngleDegrees += rawInput.x * degreesPerSecond * Time.deltaTime;

            Vector3 axis = m_CurrentPole.Axis;
            Vector3 refDir = Vector3.Cross(axis, Vector3.up).sqrMagnitude > 0.0001f
                ? Vector3.Cross(axis, Vector3.up).normalized
                : Vector3.forward;
            Vector3 outward = Quaternion.AngleAxis(m_AngleDegrees, axis) * refDir;
            m_LastOutwardDir = outward;

            Vector3 targetPos = m_CurrentPole.Center + axis * m_HeightAlongAxis + outward * radius;

            m_Motor.SetPosition(targetPos, teleport: false);
            m_Motor.SetVerticalVelocity(0f);
            modifier.OverrideGravity = true;
        }

        private void EnterPole(Pole pole, float angleDegrees, float height, Vector3 outward)
        {
            m_CurrentPole = pole;
            IsOnPole = true;
            m_AngleDegrees = angleDegrees;
            m_HeightAlongAxis = height;
            m_LastOutwardDir = outward; // so RotationOverride below faces the right way from frame one
            m_Motor.SetVerticalVelocity(0f);

            m_Motor.RotationOverride = () => m_CurrentPole != null
                ? Quaternion.LookRotation(-m_LastOutwardDir, Vector3.up)
                : m_Motor.transform.rotation;
        }

        private void ExitPole(bool applyRegrabCooldown)
        {
            if (!IsOnPole) return;

            m_LastPole = m_CurrentPole;
            IsOnPole = false;
            m_CurrentPole = null;

            if (applyRegrabCooldown)
            {
                m_RegrabTimer = regrabCooldown;
            }

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

        /// <summary>
        /// Reacts to the raw jump-pressed event rather than CoreMovement.JumpRequested - a pole leaves
        /// the player ungrounded, so CorePlayerManager's own jump handling (grounded-only) never fires
        /// while attached. Mirrors GrindRailAbility.HandleJumpPressed / WallClimbAbility's equivalent.
        /// </summary>
        private void HandleJumpPressed()
        {
            if (!m_IsActive || !IsOwner || m_Motor == null) return;
            if (!IsOnPole) return;

            PerformJumpOff();
        }

        private void PerformJumpOff()
        {
            float jumpVelocity = Mathf.Sqrt(Mathf.Max(0.01f, jumpOffHeightMultiplier * m_Motor.jumpHeight) * -2f * m_Motor.gravity);
            m_Motor.SetVerticalVelocity(jumpVelocity);
            m_Motor.ApplyExternalForce(m_LastOutwardDir * jumpOffPushForce, ForceMode.Impulse);

            ExitPole(applyRegrabCooldown: true);
        }

        #endregion

        #region Detection

        /// <summary>
        /// Scans every active pole for the closest one whose axis-relative height is within its
        /// climbable range and whose outer surface is within grab range of the player - analogous to
        /// GrindRailAbility.TryDetectRail, just gated on Grab being held instead of automatic proximity.
        /// </summary>
        private bool TryDetectPole(out Pole foundPole, out float angleDegrees, out float height, out Vector3 outwardDir)
        {
            foundPole = null;
            angleDegrees = 0f;
            height = 0f;
            outwardDir = Vector3.forward;

            float bestDist = grabDetectionRange;
            Vector3 playerPos = m_Motor.transform.position;

            var poles = Pole.ActivePoles;
            for (int i = 0; i < poles.Count; i++)
            {
                Pole pole = poles[i];
                if (pole == null) continue;
                if (pole == m_LastPole && m_RegrabTimer > 0f) continue;

                Vector3 closestOnAxis = pole.ClosestAxisPoint(playerPos, out float distFromAxis, out bool withinHeight);
                if (!withinHeight) continue;

                float range = Mathf.Min(bestDist, pole.GrabRange);
                float distFromSurface = distFromAxis - pole.WorldRadius;
                if (distFromSurface >= range) continue;

                bestDist = distFromSurface;
                foundPole = pole;

                Vector3 axis = pole.Axis;
                Vector3 toPlayer = playerPos - closestOnAxis;
                Vector3 outward = toPlayer - Vector3.Project(toPlayer, axis);
                Vector3 refDir = Vector3.Cross(axis, Vector3.up).sqrMagnitude > 0.0001f
                    ? Vector3.Cross(axis, Vector3.up).normalized
                    : Vector3.forward;

                if (outward.sqrMagnitude < 0.0001f)
                {
                    outward = refDir;
                }
                outward.Normalize();

                angleDegrees = Vector3.SignedAngle(refDir, outward, axis);
                height = Vector3.Dot(playerPos - pole.Center, axis);
                outwardDir = outward;
            }

            return foundPole != null;
        }

        #endregion
    }
}
