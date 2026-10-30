using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// POLE SWING 2 - poles done the way the 3D Sonic games do them: grab the pole and you spin round it, and
    /// Jump lets go at whatever point of the swing you're at, flying off the way you were moving at that moment.
    ///
    /// CLIMBING (VERTICAL) POLES (2026-10-20): the same idea turned on its side - you spin round the pole
    /// level, body out sideways with your hands on it, at a steady speed (no gravity along a level circle):
    /// your entry speed, between <see cref="minSpinSpeed"/> and <see cref="maxSpinSpeed"/>. Jump flies you off
    /// along the circle - whichever way you're going at that point - with the usual small boost up.
    /// Up / Down shimmies you up and down the pole while you spin.
    ///
    /// HORIZONTAL POLES (bars): Left / Right shimmies you along the bar while you swing, relative to the
    /// camera. Releasing at different points of the swing:
    ///   - at the bottom of the swing you're moving forwards, so you fly mostly forwards;
    ///   - on the way up in front you're moving up, so you fly up;
    ///   - over the top you're moving backwards, and on the way down you're moving down.
    /// It's ordinary pendulum physics rather than a timing window: you're fastest at the bottom and slowest
    /// at the top (gravity, scaled by <see cref="swingGravityScale"/>), with just enough speed kept that you
    /// always make it over the top (<see cref="minTopSpeed"/>). Coming in faster spins you faster, up to
    /// <see cref="maxTopSpeed"/> at the top.
    ///
    /// Started as a copy of the swing half of Dan's PoleGrabAbility (which is left as it was). While this
    /// component is on the player and enabled, PoleGrabAbility leaves every pole to it; disable or remove it
    /// to get Dan's climbing and swinging back. Horizontal = <see cref="Pole.IsHorizontal"/>.
    ///
    /// GRABBING: hold Grab while your hands (<see cref="hangDistance"/> above your feet) come within
    /// <see cref="grabRange"/> of the pole, moving (or facing, if you're standing still) at least
    /// <see cref="minApproachAngle"/> degrees across it - so running along a bar doesn't catch it. (Any
    /// level approach is "across" an upright pole.)
    ///
    /// WHILE SWINGING:
    ///   - the body (CharacterController) waits under the bar (or beside an upright pole), so the camera stays
    ///     steady;
    ///   - the character model spins round the pole, hands on it, and the model's NetworkTransform shows the
    ///     swing to everyone else;
    ///   - shimmying moves the hand-hold along the pole, up to its ends (<see cref="shimmySpeed"/>);
    ///   - stamina drains (<see cref="staminaCostPerSecond"/>), and running out lets go.
    ///
    /// LETTING GO:
    ///   - Jump launches you along the swing at that moment (times the launch settings, plus a small boost
    ///     up);
    ///   - releasing Grab just lets go with the speed you had.
    ///   - Either way the body is first put where the model was, so you leave from where you see yourself.
    ///
    /// A plain MonoBehaviour (no networked state of its own), found by CoreMovement (IMovementAbility) and
    /// CorePlayerManager (IPlayerAddon) like the other abilities. It only acts for this machine's own player.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PoleSwing2Ability : MonoBehaviour, IMovementAbility, IPlayerAddon
    {
        #region Settings

        [Header("Grabbing")]
        [Tooltip("How close (meters, from the bar's surface) your hands need to get to the bar while holding Grab.")]
        [SerializeField] private float grabRange = 1.0f;
        [Tooltip("Your hands are this far above your feet while hanging - also the radius your feet swing round the bar at.")]
        [SerializeField] private float hangDistance = 2.0f;
        [Tooltip("How square-on (degrees between your direction and the bar) you have to come at the bar to catch it. 90 = straight across.")]
        [SerializeField, Range(0f, 90f)] private float minApproachAngle = 45f;
        [Tooltip("After letting go, how long before the same bar can be grabbed again.")]
        [SerializeField] private float regrabCooldown = 0.4f;

        [Header("Swinging")]
        [Tooltip("Gravity during the swing, as a fraction of normal gravity. Lower = a slower, floatier swing.")]
        [SerializeField] private float swingGravityScale = 0.6f;
        [Tooltip("Speed (m/s) always kept at the top of the swing, so you go all the way round.")]
        [SerializeField] private float minTopSpeed = 3.5f;
        [Tooltip("Fastest speed (m/s) at the top of the swing, however fast you came in.")]
        [SerializeField] private float maxTopSpeed = 8f;
        [Tooltip("Stamina drained per second while on the bar (0 = none). Running out lets go.")]
        [SerializeField] private float staminaCostPerSecond = 8f;

        [Header("Climbing Poles (upright)")]
        [Tooltip("Slowest spin (m/s, feet speed) round an upright pole - you spin at your entry speed, but never slower than this.")]
        [SerializeField] private float minSpinSpeed = 6f;
        [Tooltip("Fastest spin (m/s) round an upright pole, however fast you came in.")]
        [SerializeField] private float maxSpinSpeed = 10f;

        [Header("Shimmying")]
        [Tooltip("How fast (m/s) Up/Down (upright poles) or Left/Right (bars) moves your hands along the pole while you swing.")]
        [SerializeField] private float shimmySpeed = 2.5f;

        [Header("Launch (Jump)")]
        [Tooltip("Multiplier on the swing's up/down speed when you jump off.")]
        [SerializeField] private float verticalLaunchScale = 1.12f;
        [Tooltip("Multiplier on the swing's sideways speed when you jump off. It's applied as an impulse that fades quickly (like every push in CoreMovement), so it's higher than 1 to carry you a fair way.")]
        [SerializeField] private float horizontalLaunchScale = 4f;
        [Tooltip("Extra upward speed (m/s) added to every jump off, so a jump at the bottom still lifts you a little.")]
        [SerializeField] private float launchUpBonus = 3.35f;

        [Header("Input Event")]
        [Tooltip("The jump-pressed GameEvent (the same asset CoreInputHandler raises and PoleGrabAbility listens to).")]
        [SerializeField] private GameEvent onJumpPressed;

        #endregion

        #region IMovementAbility

        /// <summary>Same slot as PoleGrabAbility / GrindRailAbility (between JumpAbility's 10 and WalkAbility's 0).</summary>
        public int Priority => 8;

        public float StaminaCost => 0f;

        #endregion

        /// <summary>True while hanging on (and spinning round) a bar.</summary>
        public bool IsSwinging => m_Pole != null;

        /// <summary>True while spinning round an upright (climbing) pole; false on a bar or off the pole.</summary>
        public bool IsOnUprightPole => m_Pole != null && m_Upright;

        /// <summary>Complete turns round poles since this avatar spawned (counted for wagers by LocalActionTracker).</summary>
        public int CompletedRotations { get; private set; }

        private float m_TurnRadians; // swept on this grab since the last complete turn

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private CorePlayerManager m_Player;
        private CoreStatsHandler m_Stats;
        private CoreAnimator m_Animator;
        private Transform m_Model;

        private bool m_IsActive = true;
        private bool m_Listening;

        // The swing.
        private Pole m_Pole;
        private Vector3 m_Pivot;   // the point on the bar's axis the hands hold
        private Vector3 m_Down;    // straight down, square to the bar
        private Vector3 m_Forward; // level and square to the bar, the way the swing goes at the bottom
        private float m_Radius;
        private float m_Angle;     // radians from the bottom, increasing along the swing
        private float m_TopSpeedSq;
        private bool m_Upright;    // a climbing pole: spin level round it
        private float m_PlaneGravity; // gravity along the swing's circle (0 for an upright pole)
        private Vector3 m_BodyOffset; // where the body waits, from the hand-hold
        private Vector3 m_ModelLocalPosition;
        private Quaternion m_ModelLocalRotation;
        private System.Func<Vector3, Vector3> m_PreviousMoveOverride;

        // Before grabbing: how the player was moving (for the approach angle and the entry speed).
        private Vector3 m_LastPosition;
        private bool m_HasLastPosition;
        private Vector3 m_Velocity;

        private float m_RegrabTimer;
        private Pole m_LastPole;

        private bool IsOwner => m_Player != null && m_Player.IsOwner;
        private float BaseGravity => Mathf.Abs(m_Motor != null ? m_Motor.gravity : -15f) * Mathf.Max(0.05f, swingGravityScale);

        #region IMovementAbility

        public void Initialize(CoreMovement movementController)
        {
            m_Motor = movementController;
            m_Controller = m_Motor.GetComponent<CharacterController>();
        }

        public MovementModifier Process()
        {
            var modifier = new MovementModifier();
            if (!isActiveAndEnabled || !m_IsActive || m_Motor == null || m_Controller == null) return modifier;

            if (m_RegrabTimer > 0f) m_RegrabTimer -= Time.deltaTime;

            if (!IsSwinging)
            {
                TrackVelocity();
                if (m_Motor.IsGrabHeld && TryFindBar(out Pole pole, out Vector3 pivot)) Grab(pole, pivot);
                return modifier;
            }

            if (m_Pole == null || !m_Pole.isActiveAndEnabled)
            {
                LetGo(jumped: false);
                return modifier;
            }

            if (!m_Motor.IsGrabHeld)
            {
                LetGo(jumped: false);
                return modifier;
            }

            if (staminaCostPerSecond > 0f && m_Stats != null
                && !m_Stats.TryConsumeStat(StatKeys.Stamina, staminaCostPerSecond * Time.deltaTime, m_Player.OwnerClientId))
            {
                LetGo(jumped: false);
                return modifier;
            }

            Swing(Time.deltaTime);
            modifier.OverrideGravity = true;
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
            m_Model = m_Animator != null ? m_Animator.transform : null;
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
            if (IsSwinging) LetGo(jumped: false, launch: false);
        }

        public void OnLifeStateChanged(PlayerLifeState previousState, PlayerLifeState newState)
        {
            m_IsActive = newState == PlayerLifeState.InitialSpawn || newState == PlayerLifeState.Respawned;
            if (!m_IsActive && IsSwinging) LetGo(jumped: false, launch: false);
            m_HasLastPosition = false;
        }

        private void OnDisable()
        {
            if (IsSwinging) LetGo(jumped: false, launch: false);
        }

        #endregion

        #region Grabbing

        private void TrackVelocity()
        {
            Vector3 position = transform.position;
            if (m_HasLastPosition && Time.deltaTime > 0f)
            {
                Vector3 velocity = (position - m_LastPosition) / Time.deltaTime;
                // A respawn or teleport isn't movement.
                m_Velocity = velocity.sqrMagnitude < 40f * 40f ? velocity : Vector3.zero;
            }
            m_LastPosition = position;
            m_HasLastPosition = true;
        }

        /// <summary>The nearest pole the hands are within reach of, coming at it square enough.</summary>
        private bool TryFindBar(out Pole found, out Vector3 pivot)
        {
            found = null;
            pivot = default;
            Vector3 hands = transform.position + Vector3.up * hangDistance;

            Vector3 approach = new Vector3(m_Velocity.x, 0f, m_Velocity.z);
            if (approach.sqrMagnitude < 0.25f) approach = new Vector3(transform.forward.x, 0f, transform.forward.z); // standing still: the way you face
            approach.Normalize();

            float best = float.MaxValue;
            var poles = Pole.ActivePoles;
            for (int i = 0; i < poles.Count; i++)
            {
                Pole pole = poles[i];
                if (pole == null) continue;
                if (pole == m_LastPole && m_RegrabTimer > 0f) continue;

                Vector3 onAxis = pole.ClosestAxisPoint(hands, out float fromAxis, out bool withinLength);
                if (!withinLength) continue;
                float fromSurface = fromAxis - pole.WorldRadius;
                if (fromSurface > Mathf.Min(grabRange, pole.GrabRange) || fromSurface >= best) continue;

                // Coming at it across, not along it.
                Vector3 along = pole.Axis;
                along.y = 0f;
                if (along.sqrMagnitude > 0.0001f && approach.sqrMagnitude > 0.0001f)
                {
                    float angle = Vector3.Angle(approach, along.normalized); // 0 = along the bar, 90 = straight across
                    if (Mathf.Min(angle, 180f - angle) < minApproachAngle) continue;
                }

                best = fromSurface;
                found = pole;
                pivot = onAxis;
            }
            return found != null;
        }

        private void Grab(Pole pole, Vector3 pivot)
        {
            Vector3 axis = pole.Axis.normalized;

            // The swing's plane: square to the pole. "Down" is gravity in that plane - or, round an upright pole
            // where gravity is along the pole, the side the player is on. "Forward" is square to both, pointing
            // the way the player was going (or facing).
            Vector3 down = Vector3.ProjectOnPlane(Vector3.down, axis);
            float tilt = down.magnitude; // 1 for a level bar, 0 for an upright pole
            if (tilt > 0.05f) down /= tilt;
            else
            {
                down = Vector3.ProjectOnPlane(transform.position - pivot, axis);
                if (down.sqrMagnitude < 0.0001f) down = Vector3.ProjectOnPlane(-transform.forward, axis);
                if (down.sqrMagnitude < 0.0001f) return;
                down.Normalize();
                tilt = 0f;
            }
            Vector3 forward = Vector3.Cross(axis, down).normalized;
            Vector3 heading = m_Velocity.sqrMagnitude > 0.25f ? m_Velocity : transform.forward;
            if (Vector3.Dot(Vector3.ProjectOnPlane(heading, Vector3.up), forward) < 0f) forward = -forward;

            m_Pole = pole;
            m_Pivot = pivot;
            m_Down = down;
            m_Forward = forward;
            m_Radius = Mathf.Max(0.5f, hangDistance);
            m_Upright = !pole.IsHorizontal;
            m_PlaneGravity = BaseGravity * tilt;
            float alongNow = Vector3.Dot(m_Pivot - pole.Center, axis);
            m_Pivot += axis * (ClampAlong(alongNow) - alongNow);

            // Where the body waits: hanging under a bar; beside an upright pole, against it, about where the
            // player was standing (a little below the hands, so the camera stays near them).
            m_BodyOffset = m_Upright
                ? down * (pole.WorldRadius + m_Controller.radius) + Vector3.down * (m_Radius * 0.5f)
                : down * m_Radius;

            // Where the feet are now, as an angle round the bar (nearly always just below it).
            Vector3 fromPivot = Vector3.ProjectOnPlane(transform.position - pivot, axis);
            m_Angle = fromPivot.sqrMagnitude > 0.01f
                ? Mathf.Atan2(Vector3.Dot(fromPivot, forward), Vector3.Dot(fromPivot, down))
                : 0f;
            m_TurnRadians = 0f;

            // Swing speed: the speed coming in along the swing, turned into "speed at the top" (energy kept),
            // never less than enough to go all the way round.
            // (Round an upright pole there's no gravity along the circle, so that's simply the spin speed.)
            Vector3 tangent = Tangent(m_Angle);
            float entrySpeed = Mathf.Max(0f, Vector3.Dot(m_Velocity, tangent));
            float topSq = entrySpeed * entrySpeed - 2f * m_PlaneGravity * m_Radius * (1f + Mathf.Cos(m_Angle));
            m_TopSpeedSq = m_Upright
                ? Mathf.Clamp(topSq, minSpinSpeed * minSpinSpeed, maxSpinSpeed * maxSpinSpeed)
                : Mathf.Clamp(topSq, minTopSpeed * minTopSpeed, maxTopSpeed * maxTopSpeed);

            if (m_Model != null)
            {
                m_ModelLocalPosition = m_Model.localPosition;
                m_ModelLocalRotation = m_Model.localRotation;
            }

            // The body waits under the bar; nothing else moves it (WalkAbility would otherwise nudge it).
            m_PreviousMoveOverride = m_Motor.FinalMoveCalculationOverride;
            m_Motor.FinalMoveCalculationOverride = _ => Vector3.zero;
            m_Motor.SetVerticalVelocity(0f);
            // The body faces along the swing under a bar, or the pole beside an upright one.
            Vector3 face = m_Upright ? Vector3.ProjectOnPlane(-down, Vector3.up) : m_Forward;
            if (face.sqrMagnitude < 0.0001f) face = transform.forward;
            Quaternion facing = Quaternion.LookRotation(face.normalized, Vector3.up);
            m_Motor.RotationOverride = () => facing;

            if (m_Animator != null)
            {
                if (m_Animator.Animator != null) m_Animator.Animator.ResetTrigger(m_Animator.m_AnimIDTrigger_exitAbility);
                m_Animator.PlayAnimation(m_Animator.ANIMATION_pole_hang);
            }

            Swing(0f);
        }

        #endregion

        #region Swinging

        /// <summary>The way the swing is moving at <paramref name="angle"/> (radians from the bottom).</summary>
        private Vector3 Tangent(float angle) => -Mathf.Sin(angle) * m_Down + Mathf.Cos(angle) * m_Forward;

        /// <summary>Where the feet are at <paramref name="angle"/>.</summary>
        private Vector3 FeetAt(float angle) => m_Pivot + m_Radius * (Mathf.Cos(angle) * m_Down + Mathf.Sin(angle) * m_Forward);

        /// <summary>Speed along the swing at <paramref name="angle"/>: energy kept, so fastest at the bottom.</summary>
        private float SpeedAt(float angle) => Mathf.Sqrt(Mathf.Max(0f, m_TopSpeedSq + 2f * m_PlaneGravity * m_Radius * (1f + Mathf.Cos(angle))));

        private void Swing(float deltaTime)
        {
            float step = SpeedAt(m_Angle) / m_Radius * deltaTime;
            m_Angle += step;
            if (m_Angle > Mathf.PI * 2f) m_Angle -= Mathf.PI * 2f;

            // A full turn round the pole from where it was grabbed (or the last full turn) counts as a rotation.
            m_TurnRadians += step;
            while (m_TurnRadians >= Mathf.PI * 2f)
            {
                m_TurnRadians -= Mathf.PI * 2f;
                CompletedRotations++;
            }

            Shimmy(deltaTime);

            // The body waits under the bar / beside the pole.
            m_Motor.SetPosition(m_Pivot + m_BodyOffset, teleport: false);
            m_Motor.SetVerticalVelocity(0f);

            // The model goes round: feet on the circle, head towards the bar, facing along the swing.
            if (m_Model != null)
            {
                Vector3 towardBar = -(Mathf.Cos(m_Angle) * m_Down + Mathf.Sin(m_Angle) * m_Forward);
                m_Model.SetPositionAndRotation(FeetAt(m_Angle), Quaternion.LookRotation(Tangent(m_Angle), towardBar));
            }
        }

        /// <summary>
        /// Moves the hand-hold along the pole: Up/Down on an upright pole (up is up), Left/Right on a bar (as the
        /// camera sees it). It stops at the pole's ends.
        /// </summary>
        private void Shimmy(float deltaTime)
        {
            if (m_Pole == null || deltaTime <= 0f || shimmySpeed <= 0f) return;

            Vector3 axis = m_Pole.Axis.normalized;
            Vector2 input = m_Motor.MoveInput;
            float along;
            if (m_Upright)
            {
                along = input.y * Mathf.Sign(Vector3.Dot(axis, Vector3.up));
            }
            else
            {
                Camera camera = Camera.main;
                Vector3 right = camera != null ? camera.transform.right : Vector3.Cross(Vector3.up, m_Forward);
                right = Vector3.ProjectOnPlane(right, Vector3.up);
                float facing = Vector3.Dot(axis, right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.Cross(Vector3.up, m_Forward));
                along = Mathf.Abs(facing) < 0.2f ? 0f : input.x * Mathf.Sign(facing); // looking straight along the bar: no left/right
            }
            if (Mathf.Abs(along) < 0.1f) return;

            float now = Vector3.Dot(m_Pivot - m_Pole.Center, axis);
            m_Pivot += axis * (ClampAlong(now + along * shimmySpeed * deltaTime) - now);
        }

        /// <summary>
        /// Keeps the hand-hold on the pole. On an upright pole the body waits below the hands, so the hands stay
        /// that far above the bottom end (the body never ends up under the floor the pole stands on).
        /// </summary>
        private float ClampAlong(float along)
        {
            float half = m_Pole.WorldHalfHeight;
            if (!m_Upright) return Mathf.Clamp(along, -half, half);
            float bodyDrop = Mathf.Min(m_Radius * 0.5f, half);
            bool axisUp = Vector3.Dot(m_Pole.Axis, Vector3.up) >= 0f;
            return axisUp ? Mathf.Clamp(along, -half + bodyDrop, half) : Mathf.Clamp(along, -half, half - bodyDrop);
        }

        #endregion

        #region Letting go

        private void HandleJumpPressed()
        {
            if (!IsOwner || !IsSwinging || !m_IsActive) return;
            LetGo(jumped: true);
        }

        /// <summary>
        /// Leaves the bar from where the model is, moving the way the swing was (scaled up and with a boost when
        /// <paramref name="jumped"/>). <paramref name="launch"/> false just puts everything back (death, despawn).
        /// </summary>
        private void LetGo(bool jumped, bool launch = true)
        {
            Vector3 feet = FeetAt(m_Angle);
            Vector3 velocity = Tangent(m_Angle) * SpeedAt(m_Angle);

            m_LastPole = m_Pole;
            m_Pole = null;
            m_RegrabTimer = regrabCooldown;
            m_HasLastPosition = false;

            if (m_Model != null)
            {
                m_Model.localPosition = m_ModelLocalPosition;
                m_Model.localRotation = m_ModelLocalRotation;
            }
            if (m_Motor == null) return;

            m_Motor.FinalMoveCalculationOverride = m_PreviousMoveOverride;
            m_PreviousMoveOverride = null;
            m_Motor.RotationOverride = null;
            if (m_Animator != null) m_Animator.SetAnimationTrigger(m_Animator.m_AnimIDTrigger_exitAbility);
            if (!launch) return;

            m_Motor.SetPosition(feet, teleport: true);

            Vector3 sideways = new Vector3(velocity.x, 0f, velocity.z);
            float up = velocity.y;
            if (jumped)
            {
                up = up * verticalLaunchScale + launchUpBonus;
                sideways *= horizontalLaunchScale;
                PlayerActions.Report(m_Motor.gameObject, PlayerAction.PoleJump); // counted for wagers (LocalActionTracker)
            }
            m_Motor.SetVerticalVelocity(up);
            if (sideways.sqrMagnitude > 0.0001f) m_Motor.ApplyExternalForce(sideways, ForceMode.Impulse);
        }

        #endregion
    }
}
