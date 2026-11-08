using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Grind Rail 2 (2026-11-05): a fresh take on grinding, replacing Dan's GrindRailAbility the way Pole Swing 2
    /// replaced his pole swing. While it's on the player and enabled, GrindRailAbility steps aside (it does
    /// nothing); disable this component on CorePlayer to get the old rails back.
    ///
    /// THE DESIGN (from the user):
    ///   - Hold Parkour to snap onto a rail (any <see cref="GrindRail"/> within <see cref="snapRange"/>) and grind
    ///     along it. No balance - you're on the rail and you stay on it.
    ///   - Grinding burns stamina, fast: a full bar lasts <see cref="fullTankBlocks"/> (8) blocks of rail starting
    ///     at the normal grind speed. Run out, or let go of Parkour, and you fall off.
    ///   - You speed up the longer you grind (2026-11-07: with no cap - only stamina limits it, so hitting a rail
    ///     fast, e.g. off a dash panel, ends up faster still), reaching after a full tank the speed that carries a
    ///     jump from the rail about
    ///     <see cref="topSpeedJumpBlocks"/> (12) blocks. The two numbers set the acceleration and the stamina
    ///     drain (see <see cref="Tuning"/>), so they can be tuned in blocks.
    ///   - The speed you start at depends on how fast you hit the rail: grind speed x (your speed / run speed),
    ///     never below the normal grind speed. E.g. landing at 1.5x run speed from an earlier rail starts you
    ///     at 1.5x grind speed; jumping on with Super Rocket Shoes (2x) starts you at 2x. The base speeds never
    ///     change.
    ///   - Leaving keeps your speed as momentum (WalkAbility.CarryMomentum):
    ///       Jump: off in the rail's direction (with the normal jump height, plus any upward slope), with half
    ///             the air control for <see cref="jumpReducedControlSeconds"/> (0.2 s);
    ///       letting go of Parkour / out of stamina / off the end: carry on the way the rail was going, with
    ///             normal air control.
    ///   - Once off a rail, no rail can grab you for <see cref="snapCooldown"/> (0.3 s) - so falling off doesn't
    ///     just snap you straight back on. Running off the end of a rail onto another rail joined to it carries
    ///     straight on.
    ///   - Loops (closed rails) go round until you jump, let go or run out of stamina.
    ///
    /// Same set-up as Pole Swing 2: a plain MonoBehaviour (IMovementAbility + IPlayerAddon) on CorePlayer.prefab;
    /// Jump comes from the jump-pressed GameEvent (a rail has no collider, so the normal grounded jump never
    /// fires on one). Only the owner moves; other players see it through the NetworkTransform and animator.
    /// </summary>
    public class GrindRail2Ability : MonoBehaviour, IMovementAbility, IPlayerAddon
    {
        /// <summary>One Total Editor block, in meters.</summary>
        public const float BlockSize = 2f;

        #region Settings

        [Header("Snapping On")]
        [Tooltip("How close (meters) the player's feet have to be to the top of a rail, holding Parkour, to snap onto it.")]
        [SerializeField] private float snapRange = 0.9f;
        [Tooltip("After leaving a rail (jump, letting go of Parkour, out of stamina, off the end), no rail can grab the player for this long (seconds).")]
        [SerializeField] private float snapCooldown = 0.3f;

        [Header("Speed")]
        [Tooltip("The normal grind speed, as a multiple of run speed (CoreMovement.moveSpeed). Hitting a rail faster than running starts you that much faster.")]
        [SerializeField] private float grindSpeedMultiplier = 1f;
        [Tooltip("Blocks of rail a full stamina bar lasts from the normal grind speed (also how far it takes to reach top speed).")]
        [SerializeField] private float fullTankBlocks = 8f;
        [Tooltip("The speed a full tank of grinding builds up to from the normal grind speed, given as how far (in blocks, along flat ground) a jump off the rail at that speed carries.")]
        [SerializeField] private float topSpeedJumpBlocks = 12f;
        [Tooltip("Safety cap: never faster than this many times that full-tank speed (hitting rails fast keeps adding speed; stamina is the real limit).")]
        [SerializeField] private float speedCapMultiplier = 3f;

        [Header("Jumping Off")]
        [Tooltip("Height of the jump off a rail, as a multiple of the normal jump height (CoreMovement.jumpHeight).")]
        [SerializeField] private float jumpHeightMultiplier = 1f;
        [Tooltip("After JUMPING off a rail, air control is scaled by this for the time below (0.5 = half).")]
        [SerializeField] private float jumpAirControlScale = 0.5f;
        [Tooltip("How long (seconds) air control stays reduced after jumping off a rail.")]
        [SerializeField] private float jumpReducedControlSeconds = 0.2f;

        [Tooltip("The jump-pressed GameEvent (the same asset CoreInputHandler raises and Pole Swing 2 listens to).")]
        [SerializeField] private GameEvent onJumpPressed;

        #endregion

        #region IMovementAbility

        /// <summary>Same slot as GrindRailAbility: before WalkAbility (0), so Walk sees the grind state the same frame.</summary>
        public int Priority => 6;

        public float StaminaCost => 0f;

        #endregion

        /// <summary>True while grinding.</summary>
        public bool IsOnRail => m_Rail != null;

        /// <summary>Current grind speed (m/s), 0 off a rail.</summary>
        public float Speed => IsOnRail ? m_Speed : 0f;

        private CoreMovement m_Motor;
        private CorePlayerManager m_Player;
        private CoreStatsHandler m_Stats;
        private WalkAbility m_Walk;
        private CoreAnimator m_Animator;

        private bool m_IsActive = true;
        private bool m_Listening;

        private GrindRail m_Rail;
        private float m_Arc;
        private float m_Direction = 1f; // +1 along the rail's arc length, -1 against it
        private float m_Speed;
        private float m_TopSpeed;       // this ride's safety cap (speedCapMultiplier x the full-tank speed)
        private float m_Distance;       // ridden on this rail (for the log)
        private float m_Cooldown;

        private bool IsOwner => m_Player != null && m_Player.IsOwner;

        /// <summary>
        /// The numbers behind the block-based settings, worked out from the player's run speed, gravity and jump:
        ///   - start: the normal grind speed (grindSpeedMultiplier x run speed);
        ///   - top: the speed whose jump (normal jump height) lands topSpeedJumpBlocks away on flat ground;
        ///   - accel: reaches top from start in exactly fullTankBlocks of rail;
        ///   - drain: stamina per second so a full bar runs out just as that happens.
        /// </summary>
        public void Tuning(out float start, out float top, out float accel, out float drainPerSecond)
        {
            float run = m_Motor != null ? m_Motor.moveSpeed : 9f;
            float g = m_Motor != null ? Mathf.Max(0.1f, -m_Motor.gravity) : 30f;
            start = Mathf.Max(0.1f, grindSpeedMultiplier * run);

            float jumpUp = JumpUpSpeed(g);
            float airTime = 2f * jumpUp / g;
            top = Mathf.Max(start, topSpeedJumpBlocks * BlockSize / Mathf.Max(0.05f, airTime));

            float distance = Mathf.Max(0.5f, fullTankBlocks * BlockSize);
            accel = (top * top - start * start) / (2f * distance);
            // Seconds a full bar lasts: until top speed, or (if there's nothing to speed up to) the whole distance at start speed.
            float fullTankSeconds = accel > 0.001f ? (top - start) / accel : distance / start;

            float maxStamina = m_Stats != null ? m_Stats.GetMaxValue(StatKeys.Stamina) : 100f;
            drainPerSecond = maxStamina / Mathf.Max(0.05f, fullTankSeconds);
        }

        private float JumpUpSpeed(float g) =>
            Mathf.Sqrt(2f * g * Mathf.Max(0.01f, (m_Motor != null ? m_Motor.jumpHeight : 5f) * jumpHeightMultiplier));

        #region IMovementAbility

        public void Initialize(CoreMovement movementController)
        {
            m_Motor = movementController;
            m_Walk = m_Motor.GetComponent<WalkAbility>();
        }

        public MovementModifier Process()
        {
            var modifier = new MovementModifier();
            if (!isActiveAndEnabled || !m_IsActive || m_Motor == null || !IsOwner) return modifier;

            if (m_Cooldown > 0f) m_Cooldown -= Time.deltaTime;

            if (!IsOnRail)
            {
                if (m_Cooldown <= 0f && m_Motor.IsGrabHeld && HasStaminaToStart()
                    && TryFindRail(null, out GrindRail rail, out float arc)) GetOn(rail, arc);
                if (!IsOnRail) return modifier;
            }

            Grind(Time.deltaTime);
            if (IsOnRail) modifier.OverrideGravity = true;
            return modifier;
        }

        public bool TryActivate() => false;

        #endregion

        #region IPlayerAddon

        public void Initialize(CorePlayerManager playerManager)
        {
            m_Player = playerManager;
            m_Stats = playerManager.CoreStats;
            m_Animator = GetComponentInChildren<CoreAnimator>();
        }

        public void OnPlayerSpawn()
        {
            if (!m_Listening && onJumpPressed != null)
            {
                onJumpPressed.RegisterListener(HandleJumpPressed);
                m_Listening = true;
            }
        }

        public void OnPlayerDespawn()
        {
            if (m_Listening && onJumpPressed != null)
            {
                onJumpPressed.UnregisterListener(HandleJumpPressed);
                m_Listening = false;
            }
            if (IsOnRail) GetOff(null);
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;
            if (IsOnRail) GetOff(null); // dying (or a teleport): just let go - no momentum
        }

        private void OnDisable()
        {
            if (IsOnRail) GetOff(null);
        }

        #endregion

        #region Getting on

        private bool HasStaminaToStart()
        {
            if (m_Stats == null) return true;
            Tuning(out _, out _, out _, out float drain);
            return m_Stats.GetCurrentValue(StatKeys.Stamina) >= drain * 0.05f; // at least a few frames' worth
        }

        /// <summary>The nearest rail whose top is within snapRange of the feet (skipping <paramref name="except"/>).</summary>
        private bool TryFindRail(GrindRail except, out GrindRail found, out float foundArc)
        {
            found = null;
            foundArc = 0f;
            float best = snapRange;
            Vector3 feet = m_Motor.transform.position;

            IReadOnlyList<GrindRail> rails = GrindRail.ActiveRails;
            for (int i = 0; i < rails.Count; i++)
            {
                GrindRail rail = rails[i];
                if (rail == null || rail == except || !rail.isActiveAndEnabled) continue;

                float arc = rail.ProjectToArcLength(feet, out bool within);
                if (!within) continue;
                rail.Evaluate(arc, out Vector3 point, out _, out _, out _);
                float distance = Vector3.Distance(feet, point + Vector3.up * rail.Radius);
                if (distance >= best) continue;

                best = distance;
                found = rail;
                foundArc = arc;
            }
            return found != null;
        }

        /// <summary>
        /// Onto <paramref name="rail"/> at <paramref name="arc"/>. <paramref name="keepSpeed"/>: carrying straight on
        /// from a rail joined to this one (same speed, same way along).
        /// </summary>
        private void GetOn(GrindRail rail, float arc, bool keepSpeed = false)
        {
            Tuning(out float start, out float top, out _, out _);

            rail.Evaluate(arc, out _, out Vector3 tangent, out _, out _);

            // Which way: the way the player was moving along it (or facing, if barely moving). While grinding the
            // body is placed rather than moved, so a joined rail goes by the way along the last one.
            Vector3 moving = m_Motor.HorizontalVelocity;
            float along;
            if (keepSpeed && IsOnRail)
            {
                along = Vector3.Dot(TravelDirection(), tangent);
            }
            else
            {
                Vector3 heading = moving.sqrMagnitude > 0.25f ? moving : m_Motor.transform.forward;
                Vector3 flatTangent = new Vector3(tangent.x, 0f, tangent.z);
                along = flatTangent.sqrMagnitude > 0.0001f ? Vector3.Dot(heading, flatTangent) : -m_Motor.VerticalVelocity * tangent.y;
            }
            float direction = along < 0f ? -1f : 1f;

            float hitSpeed = keepSpeed ? m_Speed : moving.magnitude;
            float run = Mathf.Max(0.1f, m_Motor.moveSpeed);
            float entry = keepSpeed ? m_Speed : start * Mathf.Max(1f, hitSpeed / run);

            bool wasOn = IsOnRail;
            m_Rail = rail;
            m_Arc = arc;
            m_Direction = direction;
            m_Speed = entry;
            m_TopSpeed = Mathf.Max(top * Mathf.Max(1f, speedCapMultiplier), entry);
            m_Distance = 0f;

            m_Motor.SetVerticalVelocity(0f);
            m_Motor.SetGrindState(true);
            m_Motor.RotationOverride = FacingAlongRail;
            PlaceOnRail();

            if (!wasOn && m_Animator != null)
            {
                m_Animator.Animator.ResetTrigger(m_Animator.m_AnimIDTrigger_exitAbility);
                m_Animator.PlayAnimation(m_Animator.ANIMATION_grindrail_slide);
            }

            Debug.Log($"[Rail] On '{rail.name}' at {entry:0.0} m/s (hit at {hitSpeed:0.0} m/s; grind speed {start:0.0}, top {top:0.0}).", this);
        }

        #endregion

        #region Grinding

        private void Grind(float dt)
        {
            if (m_Rail == null || !m_Rail.isActiveAndEnabled)
            {
                GetOff("the rail went away");
                return;
            }
            if (!m_Motor.IsGrabHeld)
            {
                GetOff("let go of Parkour");
                return;
            }

            Tuning(out _, out _, out float accel, out float drain);
            if (m_Stats != null && !m_Stats.TryConsumeStat(StatKeys.Stamina, drain * dt, m_Player.OwnerClientId))
            {
                GetOff("out of stamina");
                return;
            }

            m_Speed = Mathf.Min(m_Speed + accel * dt, m_TopSpeed);

            float next = m_Arc + m_Direction * m_Speed * dt;
            if (!m_Rail.IsArcWithinRail(next))
            {
                // Off the end: straight onto a rail joined to this one, if there is one; otherwise carry on into the air.
                m_Arc = Mathf.Clamp(next, 0f, m_Rail.TotalLength);
                PlaceOnRail();
                if (TryFindRail(m_Rail, out GrindRail joined, out float joinedArc))
                {
                    GetOn(joined, joinedArc, keepSpeed: true);
                    return;
                }
                GetOff("off the end");
                return;
            }

            m_Distance += m_Speed * dt;
            m_Arc = next;
            m_Motor.SetVerticalVelocity(0f);
            PlaceOnRail();
        }

        private void PlaceOnRail()
        {
            m_Rail.Evaluate(m_Arc, out Vector3 point, out _, out _, out _);
            m_Motor.SetPosition(point + Vector3.up * m_Rail.Radius, teleport: false);
        }

        /// <summary>The way along the rail the player is going (unit vector, 3D).</summary>
        private Vector3 TravelDirection()
        {
            m_Rail.Evaluate(m_Arc, out _, out Vector3 tangent, out _, out _);
            return m_Direction >= 0f ? tangent : -tangent;
        }

        private Quaternion FacingAlongRail()
        {
            if (m_Rail == null) return m_Motor.transform.rotation;
            Vector3 travel = TravelDirection();
            Vector3 flat = new Vector3(travel.x, 0f, travel.z);
            return flat.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : m_Motor.transform.rotation;
        }

        #endregion

        #region Getting off

        private void HandleJumpPressed()
        {
            if (!m_IsActive || !IsOwner || !IsOnRail) return;

            Vector3 travel = TravelDirection();
            Vector3 flat = new Vector3(travel.x, 0f, travel.z);
            if (flat.sqrMagnitude < 0.01f) flat = m_Motor.transform.forward; // a vertical bit of rail: off the way the player faces
            flat.y = 0f;

            float g = Mathf.Max(0.1f, -m_Motor.gravity);
            float up = JumpUpSpeed(g) + Mathf.Max(0f, travel.y * m_Speed); // an upward slope adds to the jump
            Vector3 horizontal = flat.normalized * m_Speed;

            PlayerActions.Report(m_Motor.gameObject, PlayerAction.RailJump); // wagers, Gravity Boots sound
            Leave("jumped", horizontal, up, jumpReducedControlSeconds, jumpAirControlScale);
        }

        /// <summary>Lets go, keeping the rail's velocity as momentum (normal air control). <paramref name="reason"/> null = no momentum (death, teleport).</summary>
        private void GetOff(string reason)
        {
            if (!IsOnRail) return;
            if (reason == null)
            {
                Leave(null, Vector3.zero, 0f, 0f, 1f);
                return;
            }

            Vector3 velocity = TravelDirection() * m_Speed;
            Leave(reason, new Vector3(velocity.x, 0f, velocity.z), velocity.y, 0f, 1f);
        }

        private void Leave(string reason, Vector3 horizontal, float vertical, float reducedSeconds, float controlScale)
        {
            float speed = m_Speed;
            float distance = m_Distance;
            string railName = m_Rail != null ? m_Rail.name : "?";

            m_Rail = null;
            m_Cooldown = snapCooldown;
            m_Motor.SetGrindState(false);
            m_Motor.RotationOverride = null;
            if (m_Animator != null) m_Animator.SetAnimationTrigger(m_Animator.m_AnimIDTrigger_exitAbility);

            if (reason == null) return;

            m_Motor.SetVerticalVelocity(vertical);
            if (m_Walk != null) m_Walk.CarryMomentum(horizontal, reducedSeconds, controlScale);
            Debug.Log($"[Rail] Off '{railName}' ({reason}) at {speed:0.0} m/s after {distance / BlockSize:0.0} blocks.", this);
        }

        #endregion
    }
}
