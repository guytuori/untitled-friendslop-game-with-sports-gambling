using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Jetpack item's flying. Added to the local player at runtime the first time a Jetpack is used
    /// (ItemEffects), and only does anything between <see cref="Equip"/> and <see cref="Unequip"/> - the item's
    /// timer.
    ///
    ///   - Controls: like a double jump, press Jump in the air - but hold it: it burns for as long as Jump is
    ///     held. Let go and press again as often as you like without landing. A press on the ground is a normal
    ///     jump, and holding it on into the air doesn't light the jetpack (press again). It won't light on a
    ///     rail, wall or pole, or with the press that jumps off one.
    ///   - Fuel is stamina: a full stamina bar lasts <see cref="FullTankSeconds"/> of burning. Running dry cuts
    ///     it out until Jump is pressed again with stamina back.
    ///   - Up: thrust builds from <see cref="StartThrust"/> to <see cref="MaxThrust"/> times gravity over
    ///     <see cref="RampSeconds"/> of continuous burn (letting go starts the build-up again). The climb tops
    ///     out at a full normal jump's take-off speed (sqrt(2 g jumpHeight) - about 17 m/s on CorePlayer).
    ///     Lit while falling, it first brakes the fall hard (<see cref="FallBrake"/>), so holding Jump stops
    ///     the drop within a fraction of a second instead of taking a second or more to bite.
    ///   - Lighting and cutting out are logged ("[Items] Jetpack ...") with the reason, to check the controls.
    ///   - Forward: it also pushes the way the player faces, building up to <see cref="ForwardSpeedScale"/>
    ///     times run speed (CoreMovement.moveSpeed) in total over the same <see cref="RampSeconds"/>. After the
    ///     burn the push fades out gently in the air, quickly once landed.
    ///   - Animation: none of its own yet - it uses the normal in-air animation, as the double jump does.
    /// </summary>
    public class JetpackAbility : MonoBehaviour, IMovementAbility
    {
        /// <summary>Seconds of burning a full stamina bar gives.</summary>
        public const float FullTankSeconds = 8f;

        /// <summary>Seconds of continuous burning for the thrust (and the forward push) to build up fully.</summary>
        public const float RampSeconds = 2f;

        /// <summary>Thrust when it lights, in multiples of gravity (a little more than hovering).</summary>
        public const float StartThrust = 1.25f;

        /// <summary>Thrust after <see cref="RampSeconds"/> of burning, in multiples of gravity.</summary>
        public const float MaxThrust = 2f;

        /// <summary>
        /// Extra braking while still falling, in multiples of gravity: stops a fall at about 17 m/s in ~0.2 s, so
        /// lighting the jetpack mid-fall is felt straight away (the thrust build-up alone takes over a second).
        /// </summary>
        public const float FallBrake = 3f;

        /// <summary>Top speed across the ground while burning, in multiples of run speed.</summary>
        public const float ForwardSpeedScale = 1.5f;

        /// <summary>How fast the forward push fades after the burn, in the air and on the ground (m/s²).</summary>
        private const float AirCoastDecel = 3f;
        private const float GroundCoastDecel = 30f;

        // Just after the regular movement abilities that set the vertical speed (Jump is 10).
        public int Priority => 9;

        public float StaminaCost => 0f;

        private CoreMovement m_Motor;
        private CoreInputHandler m_Input;
        private CoreStatsHandler m_Stats;

        private bool m_Equipped;
        private bool m_Burning;
        private float m_BurnTime;
        private int m_SeenPresses;
        private bool m_WasHeld;
        private bool m_Armed;
        private int m_LastAbilityFrame = -10;
        private float m_StartHeight;
        private Vector3 m_Boost;

        /// <summary>The jetpack is on (the item's timer is running).</summary>
        public bool IsEquipped => m_Equipped;

        /// <summary>Burning right now.</summary>
        public bool IsBurning => m_Burning;

        /// <summary>The player's jetpack, added (and registered with its CoreMovement) the first time.</summary>
        public static JetpackAbility For(CorePlayerManager player)
        {
            if (player == null || player.CoreMovement == null) return null;
            CoreMovement motor = player.CoreMovement;
            var jetpack = motor.GetComponent<JetpackAbility>();
            if (jetpack == null)
            {
                jetpack = motor.gameObject.AddComponent<JetpackAbility>();
                motor.AddAbility(jetpack); // calls Initialize
            }
            return jetpack;
        }

        public void Initialize(CoreMovement motor)
        {
            m_Motor = motor;
        }

        /// <summary>The Jetpack item was used: Jump in the air now lights it.</summary>
        public void Equip(CorePlayerManager player)
        {
            m_Input = player != null ? player.CoreInput : null;
            if (m_Input == null && player != null) m_Input = player.GetComponentInChildren<CoreInputHandler>();
            m_Stats = player != null ? player.CoreStats : null;
            m_SeenPresses = m_Input != null ? m_Input.JumpPressCount : 0;
            m_WasHeld = m_Input != null && m_Input.JumpHeld;
            m_Armed = false;
            m_Equipped = true;
            StopBurn();
        }

        /// <summary>The item ran out.</summary>
        public void Unequip()
        {
            m_Equipped = false;
            StopBurn();
        }

        public bool TryActivate() => false;

        public MovementModifier Process()
        {
            var modifier = new MovementModifier();
            if (m_Motor == null) return modifier;

            float dt = Time.deltaTime;
            bool abilityActive = m_Motor.MovementAbilityActive();
            if (abilityActive) m_LastAbilityFrame = Time.frameCount;

            // A press: the press counter moved (catches a tap shorter than a frame), or Jump went from up to
            // down since last frame.
            int presses = m_Input != null ? m_Input.JumpPressCount : m_SeenPresses;
            bool held = m_Input != null && m_Input.JumpHeld;
            bool newPress = presses != m_SeenPresses || (held && !m_WasHeld);
            m_SeenPresses = presses;
            m_WasHeld = held;

            // Landed = on the ground and not going up (the ground check can brush a ledge or wall on the way
            // up; that isn't a landing).
            bool landed = m_Motor.IsGrounded && m_Motor.VerticalVelocity <= 0.1f;
            // On a rail, wall or pole, or the press that just jumped off one: not the jetpack.
            bool blocked = abilityActive || Time.frameCount - m_LastAbilityFrame <= 1;

            // A press in the air arms it; it stays armed for as long as Jump stays held. A press on the ground
            // (a normal jump) doesn't, even if Jump is held on into the air.
            // (Judged on IsGrounded alone, and not on a frame with a normal jump requested: JumpAbility runs first
            // and has already set the take-off speed, so "landed" would read false on the jump's own frame.)
            if (newPress) m_Armed = m_Equipped && !m_Motor.IsGrounded && !m_Motor.JumpRequested && !blocked;
            if (!held || landed || !m_Equipped) m_Armed = false;

            bool canBurn = m_Armed && !blocked;
            if (!canBurn)
            {
                if (m_Burning) StopBurn(!m_Equipped ? "the item ran out" : !held ? "Jump let go" : landed ? "landed" : "on a rail, wall or pole");
            }
            else if (!m_Burning)
            {
                StartBurn();
            }

            if (m_Burning && !BurnFuel(dt))
            {
                StopBurn("out of stamina");
                m_Armed = false; // needs a new press once there's stamina again
            }

            if (m_Burning)
            {
                m_BurnTime += dt;
                Climb(dt);
                Push(dt);
                modifier.OverrideGravity = true; // Climb did gravity itself
            }
            else if (abilityActive)
            {
                m_Boost = Vector3.zero; // a rail / wall / pole is in charge
            }
            else
            {
                m_Boost = Vector3.MoveTowards(m_Boost, Vector3.zero, (m_Motor.IsGrounded ? GroundCoastDecel : AirCoastDecel) * dt);
            }

            modifier.ArealVelocity = m_Boost;
            return modifier;
        }

        private void StartBurn()
        {
            m_Burning = true;
            m_BurnTime = 0f;
            m_StartHeight = m_Motor.transform.position.y;
            Debug.Log($"[Items] Jetpack lit (vertical speed {m_Motor.VerticalVelocity:0.#} m/s, grounded {m_Motor.IsGrounded}).", this);
        }

        private void StopBurn(string reason = null)
        {
            if (m_Burning && reason != null)
            {
                float rise = m_Motor.transform.position.y - m_StartHeight;
                string jump = m_Input != null ? m_Input.JumpDebugState : "no input";
                Debug.Log($"[Items] Jetpack cut out after {m_BurnTime:0.##} s: {reason} (vertical speed {m_Motor.VerticalVelocity:0.#} m/s, rose {rise:0.##} m, grounded {m_Motor.IsGrounded}, jump: {jump}).", this);
            }
            m_Burning = false;
            m_BurnTime = 0f; // letting go restarts the thrust build-up
        }

        /// <summary>Takes this frame's stamina; false when there isn't enough left.</summary>
        private bool BurnFuel(float dt)
        {
            if (m_Stats == null) return true;
            float max = m_Stats.GetMaxValue(StatKeys.Stamina);
            if (max <= 0f) return true; // no stamina stat: unlimited
            return m_Stats.TryConsumeStat(StatKeys.Stamina, max / FullTankSeconds * dt);
        }

        private float Ramp => Mathf.Clamp01(m_BurnTime / RampSeconds);

        private void Climb(float dt)
        {
            float g = Mathf.Max(0.01f, -m_Motor.gravity);
            float thrust = g * Mathf.Lerp(StartThrust, MaxThrust, Ramp);
            float maxClimb = Mathf.Sqrt(2f * g * Mathf.Max(0.01f, m_Motor.jumpHeight));

            float v = m_Motor.VerticalVelocity;
            if (v < 0f) v = Mathf.Min(0f, v + FallBrake * g * dt); // stop a fall fast first
            if (v < maxClimb) v = Mathf.Min(maxClimb, v + (thrust - g) * dt);
            else v = Mathf.Max(maxClimb, v - g * dt); // already faster (a spring jump): let gravity bring it down to the cap
            m_Motor.SetVerticalVelocity(v);
        }

        private void Push(float dt)
        {
            Transform facing = m_Motor.RotationTransform != null ? m_Motor.RotationTransform : m_Motor.transform;
            Vector3 dir = facing.forward;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            float topSpeed = m_Motor.moveSpeed * ForwardSpeedScale;

            // Everything else moving the player sideways (their run, carried momentum), from last frame.
            Vector3 other = m_Motor.HorizontalVelocity - m_Boost;
            other.y = 0f;

            m_Boost += dir * (topSpeed / RampSeconds) * dt;

            // Altogether no faster than the top speed - unless already going faster without the jetpack.
            float limit = Mathf.Max(topSpeed, other.magnitude);
            Vector3 total = other + m_Boost;
            if (total.magnitude > limit) m_Boost = total.normalized * limit - other;
        }
    }
}
