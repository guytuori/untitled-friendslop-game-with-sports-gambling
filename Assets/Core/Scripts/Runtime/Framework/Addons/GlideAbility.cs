using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Glider item (2026-11-08), modelled on Minecraft's elytra. Added to the local player at runtime the first
    /// time a Glider is used (ItemEffects), and only does anything between <see cref="Equip"/> and
    /// <see cref="Unequip"/> - the item's 20 s timer.
    ///
    /// CONTROLS
    ///   - Jump, then press Jump again in the air: you start gliding (walking off a ledge and pressing Jump works
    ///     too). Press Jump while gliding to stop - you fall with the speed you had, and can press Jump again to
    ///     glide again.
    ///   - Forward / back tilts you down / up (Gameplay settings > Invert Glider Controls, on by default, "like a
    ///     plane"; off swaps them). The angle stays where you leave it, like the elytra's look angle, between
    ///     <see cref="MinPitch"/> (climbing) and <see cref="MaxPitch"/> (diving).
    ///   - Left / right turns you (<see cref="TurnRate"/>), banking into the turn. The camera stays free.
    ///   - Use Item while gliding: 30% of the stamina bar (<see cref="BoostStaminaCost"/>) for +100% run speed on top
    ///     of your current speed (<see cref="BoostSpeedScale"/>) - the elytra's firework, toned down: up to three
    ///     per glide on a full bar, since stamina doesn't come back while gliding.
    ///
    /// FLIGHT (speeds along the flight path, which is where you're pointing - there's no separate sink)
    ///   - Gravity along the path (<see cref="GlideGravity"/>): pointing down speeds you up, pointing up slows you
    ///     down (trading speed for height); air drag (squared) holds you at run speed at the
    ///     <see cref="CruiseAngle"/> - the angle a glide starts at. Level, you keep your height but slowly lose
    ///     speed; a 45 degree dive levels off at about 2x run speed.
    ///   - Starting: at run speed (or faster if you were already moving faster). Falling when you start, your
    ///     fall carries into it: you start pointing down the way you were falling (up to 45 degrees) at that speed.
    ///   - Stall: below <see cref="StallSpeed"/> (climbed until the speed ran out) the nose drops on its own to
    ///     <see cref="StallDivePitch"/> and you get control back once it's at <see cref="StallRecoverPitch"/>.
    ///   - The boost is its own part of the speed: it fades at <see cref="BoostFade"/> (about 3 s), and climbing
    ///     eats into it once the glide speed is gone.
    ///   - Flying into a wall or ceiling loses the speed the collision took off.
    ///
    /// ENDS: Jump (keeps your speed), landing (keeps your speed for the slide), the item running out (keeps your
    /// speed), grabbing a rail, wall or pole (they take over), death / respawn / teleport (stops dead).
    ///
    /// LOOK: the "glide" animation (Glide.fbx, set up by the editor's GlideAnimationSetup), with the model tilted
    /// to the flight angle and banked in turns. Only the model tilts; the body (CharacterController) stays upright.
    /// Other players see it through the model's NetworkTransform and the Animator's state, like the pole swing.
    /// </summary>
    public class GlideAbility : MonoBehaviour, IMovementAbility
    {
        /// <summary>Degrees nose-down where the glide holds run speed - also the angle a glide starts at.</summary>
        public const float CruiseAngle = 10f;

        /// <summary>Gravity along the flight path (m/s²): what speeds up a dive and slows down a climb.</summary>
        public const float GlideGravity = 12f;

        /// <summary>Steepest climb and dive (degrees; negative = nose up).</summary>
        public const float MinPitch = -60f;
        public const float MaxPitch = 80f;

        /// <summary>Tilting speed at full stick (degrees per second).</summary>
        public const float PitchRate = 75f;

        /// <summary>Turning speed at full stick (degrees per second), and how far the model banks.</summary>
        public const float TurnRate = 90f;
        public const float MaxBank = 35f;

        /// <summary>Slower than this (m/s) and not already pointing well down: a stall.</summary>
        public const float StallSpeed = 2.5f;

        /// <summary>A stall drops the nose to this (degrees) at <see cref="StallPitchRate"/>; control comes back at <see cref="StallRecoverPitch"/>.</summary>
        public const float StallDivePitch = 50f;
        public const float StallRecoverPitch = 40f;
        public const float StallPitchRate = 150f;

        /// <summary>A falling start points down the way you were falling, up to this (degrees).</summary>
        public const float MaxStartPitch = 45f;

        /// <summary>The stamina boost, in multiples of run speed, and how fast it fades (m/s²).</summary>
        public const float BoostSpeedScale = 1f;

        /// <summary>Stamina each boost costs, as a share of the full bar (2026-11-08: 30%, so a full bar gives three).</summary>
        public const float BoostStaminaCost = 0.3f;
        public const float BoostFade = 3f;

        /// <summary>Safety cap on the total speed, in multiples of run speed.</summary>
        public const float MaxSpeedScale = 3.5f;

        /// <summary>Height (m) of the middle of the glide pose above the feet - the point the model tilts round.</summary>
        private const float ModelPivotHeight = 0.9f;

        /// <summary>How quickly the bank follows the stick.</summary>
        private const float BankSmoothing = 6f;

        // After JumpAbility (10), which sets the take-off speed - the same slot as the Jetpack - and before the
        // pole (8) and rail (6) abilities, so a grab mid-glide takes over on the same frame.
        public int Priority => 9;

        public float StaminaCost => 0f;

        private CoreMovement m_Motor;
        private CharacterController m_Controller;
        private CorePlayerManager m_Player;
        private CoreInputHandler m_Input;
        private CoreStatsHandler m_Stats;
        private CoreAnimator m_Animator;
        private Transform m_Model;
        private InputBindingsData m_Bindings;

        private bool m_Equipped;
        private bool m_Gliding;
        private int m_SeenPresses;
        private bool m_WasHeld;
        private int m_LastAbilityFrame = -10;
        private float m_StartTime;
        private int m_GlideFrames;

        private Vector3 m_Heading = Vector3.forward; // level, the way the glide goes
        private float m_Pitch;                       // degrees, + = nose down
        private float m_Bank;                        // degrees, + = left wing down (Unity's roll)
        private float m_GlideSpeed;                  // m/s along the path, from gravity and drag
        private float m_BoostSpeed;                  // m/s on top, from the stamina boost
        private bool m_Stalled;
        private float m_StartHeight;
        private float m_LastSpeed; // the speed last frame's move was made at (for the collision check)

        private System.Func<Quaternion> m_Facing;
        private bool m_ModelTilted;
        private Vector3 m_ModelLocalPosition;
        private Quaternion m_ModelLocalRotation;

        /// <summary>The glider is on (the item's timer is running).</summary>
        public bool IsEquipped => m_Equipped;

        /// <summary>Gliding right now.</summary>
        public bool IsGliding => m_Gliding;

        /// <summary>The speed along the flight path right now (m/s).</summary>
        public float Speed => m_GlideSpeed + m_BoostSpeed;

        /// <summary>The player's glider, added (and registered with its CoreMovement) the first time.</summary>
        public static GlideAbility For(CorePlayerManager player)
        {
            if (player == null || player.CoreMovement == null) return null;
            CoreMovement motor = player.CoreMovement;
            var glide = motor.GetComponent<GlideAbility>();
            if (glide == null)
            {
                glide = motor.gameObject.AddComponent<GlideAbility>();
                motor.AddAbility(glide); // calls Initialize
            }
            return glide;
        }

        public void Initialize(CoreMovement motor)
        {
            m_Motor = motor;
            m_Controller = motor.GetComponent<CharacterController>();
        }

        /// <summary>The Glider item was used: Jump in the air now starts a glide.</summary>
        public void Equip(CorePlayerManager player)
        {
            m_Player = player;
            m_Input = player != null ? player.CoreInput : null;
            if (m_Input == null && player != null) m_Input = player.GetComponentInChildren<CoreInputHandler>();
            m_Stats = player != null ? player.CoreStats : null;
            if (m_Animator == null) m_Animator = (player != null ? player.gameObject : gameObject).GetComponentInChildren<CoreAnimator>();
            if (m_Animator == null) m_Animator = GetComponentInChildren<CoreAnimator>();
            m_Model = m_Animator != null ? m_Animator.transform : null;
            if (m_Bindings == null)
            {
                m_Bindings = InputBindingsStore.Load();
                InputBindingsStore.Saved += ReloadBindings;
            }
            m_SeenPresses = m_Input != null ? m_Input.JumpPressCount : 0;
            m_WasHeld = m_Input != null && m_Input.JumpHeld;
            m_Equipped = true;
        }

        /// <summary>The item ran out: a glide in progress ends (keeping its speed) on the next frame.</summary>
        public void Unequip()
        {
            m_Equipped = false;
        }

        /// <summary>Stops a glide dead, without keeping its speed (CoreMovement.ResetMovementForces - respawn, teleport, Retry).</summary>
        public void Cancel()
        {
            if (m_Gliding) StopGlide("respawned or teleported", carry: false, exitAnimation: true);
        }

        public bool TryActivate() => false;

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        private void OnDisable()
        {
            if (m_Gliding) StopGlide("disabled", carry: false, exitAnimation: true);
        }

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
        }

        // =====================================================================================
        // Each frame
        // =====================================================================================

        public MovementModifier Process()
        {
            var modifier = new MovementModifier();
            if (m_Motor == null) return modifier;

            // Last frame's tilt comes off first, so nothing else (a pole grab saving the model's pose) sees it.
            RestoreModel();

            float dt = Time.deltaTime;
            bool abilityActive = m_Motor.MovementAbilityActive();
            if (abilityActive) m_LastAbilityFrame = Time.frameCount;

            // A Jump press: the press counter moved (catches a tap shorter than a frame), or Jump went down.
            int presses = m_Input != null ? m_Input.JumpPressCount : m_SeenPresses;
            bool held = m_Input != null && m_Input.JumpHeld;
            bool newPress = presses != m_SeenPresses || (held && !m_WasHeld);
            m_SeenPresses = presses;
            m_WasHeld = held;

            if (m_Gliding)
            {
                bool landed = m_Motor.IsGrounded && m_Motor.VerticalVelocity <= 0.1f && Time.time - m_StartTime > 0.1f;
                if (abilityActive) StopGlide("grabbed a rail, wall or pole", carry: false, exitAnimation: false);
                else if (!IsAlive) StopGlide("eliminated", carry: false, exitAnimation: true);
                else if (!m_Equipped) StopGlide("the item ran out", carry: true, exitAnimation: true);
                else if (newPress) StopGlide("Jump pressed", carry: true, exitAnimation: true);
                else if (landed) StopGlide("landed", carry: true, exitAnimation: true);
            }
            else if (newPress && CanStart(abilityActive))
            {
                StartGlide();
            }

            if (!m_Gliding) return modifier;

            KeepSpeedThatGotThrough();
            if (UseItemPressed()) TryBoost();
            Fly(dt);

            Vector3 velocity = Velocity;
            m_LastSpeed = velocity.magnitude;
            m_Motor.SetVerticalVelocity(velocity.y);
            modifier.ArealVelocity = new Vector3(velocity.x, 0f, velocity.z);
            modifier.OverrideGravity = true;
            m_GlideFrames++;
            return modifier;
        }

        /// <summary>The model tilts to the flight angle and banks - after the Animator and CoreMovement's facing.</summary>
        private void LateUpdate()
        {
            if (!m_Gliding || m_Model == null) return;

            m_ModelLocalPosition = m_Model.localPosition;
            m_ModelLocalRotation = m_Model.localRotation;
            m_ModelTilted = true;

            Vector3 right = Vector3.Cross(Vector3.up, m_Heading).normalized;
            Quaternion tilt = Quaternion.AngleAxis(m_Pitch, right) * Quaternion.AngleAxis(m_Bank, m_Heading);
            Vector3 pivot = m_Model.position + Vector3.up * ModelPivotHeight;
            Vector3 fromPivot = m_Model.position - pivot;
            m_Model.SetPositionAndRotation(pivot + tilt * fromPivot, tilt * m_Model.rotation);
        }

        private void RestoreModel()
        {
            if (!m_ModelTilted) return;
            m_ModelTilted = false;
            if (m_Model == null) return;
            m_Model.localPosition = m_ModelLocalPosition;
            m_Model.localRotation = m_ModelLocalRotation;
        }

        private bool IsAlive => m_Player == null || m_Player.PlayerState == null || m_Player.PlayerState.LifeState != PlayerLifeState.Eliminated;

        private bool CanStart(bool abilityActive)
        {
            if (!m_Equipped || !IsAlive) return false;
            // A press on the ground is a normal jump; the press that jumps off a rail, wall or pole isn't a glide.
            if (m_Motor.IsGrounded || m_Motor.JumpRequested) return false;
            if (abilityActive || Time.frameCount - m_LastAbilityFrame <= 1) return false;
            return true;
        }

        private float RunSpeed => Mathf.Max(1f, m_Motor.moveSpeed);

        /// <summary>The way the glide is pointing (unit length).</summary>
        private Vector3 Direction
        {
            get
            {
                float pitch = m_Pitch * Mathf.Deg2Rad;
                return m_Heading * Mathf.Cos(pitch) + Vector3.down * Mathf.Sin(pitch);
            }
        }

        private Vector3 Velocity => Direction * Speed;

        // =====================================================================================
        // Starting and stopping
        // =====================================================================================

        private void StartGlide()
        {
            Vector3 horizontal = m_Motor.HorizontalVelocity;
            float vertical = m_Motor.VerticalVelocity;

            Vector3 heading = horizontal.sqrMagnitude > 0.25f ? horizontal : FacingForward();
            heading.y = 0f;
            m_Heading = heading.sqrMagnitude > 0.0001f ? heading.normalized : Vector3.forward;

            // Falling: start pointing down the way you were falling (the fall turns into speed). Rising (just
            // jumped): start at the cruise angle - the rest of the jump's rise is given up.
            m_Pitch = CruiseAngle;
            if (vertical < 0f)
            {
                float fallAngle = Mathf.Atan2(-vertical, horizontal.magnitude) * Mathf.Rad2Deg;
                m_Pitch = Mathf.Clamp(fallAngle, CruiseAngle, MaxStartPitch);
            }
            float along = Vector3.Dot(horizontal + Vector3.up * vertical, Direction);
            m_GlideSpeed = Mathf.Max(RunSpeed, along);
            m_BoostSpeed = 0f;
            m_Bank = 0f;
            m_Stalled = false;

            m_Gliding = true;
            m_StartTime = Time.time;
            m_GlideFrames = 0;
            m_StartHeight = m_Motor.transform.position.y;
            m_Motor.SetGlideState(true);

            m_Facing = () => Quaternion.LookRotation(m_Heading, Vector3.up);
            m_Motor.RotationOverride = m_Facing;

            if (m_Stats != null) m_Stats.SetRegenerationPaused(StatKeys.Stamina, true);

            if (m_Animator != null)
            {
                if (m_Animator.Animator != null) m_Animator.Animator.ResetTrigger(m_Animator.m_AnimIDTrigger_exitAbility);
                m_Animator.PlayAnimation(m_Animator.ANIMATION_glide);
            }

            Debug.Log($"[Items] Glide started at {Speed:0.#} m/s, {m_Pitch:0} degrees down (was moving {horizontal.magnitude:0.#} across, {vertical:0.#} up).", this);
        }

        /// <param name="carry">Keep the glide's speed (WalkAbility.CarryMomentum) and its fall.</param>
        /// <param name="exitAnimation">Leave the glide animation (not when a rail, wall or pole has just started its own).</param>
        private void StopGlide(string reason, bool carry, bool exitAnimation)
        {
            Vector3 velocity = Velocity;
            m_Gliding = false;
            RestoreModel();

            if (m_Motor != null)
            {
                m_Motor.SetGlideState(false);
                if (m_Motor.RotationOverride == m_Facing) m_Motor.RotationOverride = null;

                if (carry)
                {
                    var walk = m_Motor.GetComponent<WalkAbility>();
                    if (walk != null) walk.CarryMomentum(new Vector3(velocity.x, 0f, velocity.z));
                    m_Motor.SetVerticalVelocity(velocity.y);
                }
            }
            m_Facing = null;

            if (m_Stats != null) m_Stats.SetRegenerationPaused(StatKeys.Stamina, false);
            if (exitAnimation && m_Animator != null) m_Animator.SetAnimationTrigger(m_Animator.m_AnimIDTrigger_exitAbility);

            float drop = m_Motor != null ? m_StartHeight - m_Motor.transform.position.y : 0f;
            Debug.Log($"[Items] Glide ended: {reason} after {Time.time - m_StartTime:0.##} s, at {velocity.magnitude:0.#} m/s ({drop:0.#} m lower than the start).", this);
        }

        private Vector3 FacingForward()
        {
            Transform facing = m_Motor.RotationTransform != null ? m_Motor.RotationTransform : m_Motor.transform;
            return facing.forward;
        }

        // =====================================================================================
        // Flying
        // =====================================================================================

        /// <summary>
        /// Hit something last frame (a wall, a ceiling)? Keep only the share of the speed that got through.
        /// Compared with the speed last frame's move was made at - not the speed now, which would read a boost
        /// (or any speed-up since) as a collision and take it straight back off (fixed 2026-11-08).
        /// </summary>
        private void KeepSpeedThatGotThrough()
        {
            if (m_GlideFrames < 2 || m_Controller == null || !m_Controller.enabled || m_LastSpeed <= 0.01f) return;
            float actual = m_Controller.velocity.magnitude;
            if (actual >= m_LastSpeed * 0.95f) return;
            float keep = Mathf.Clamp01(actual / m_LastSpeed);
            m_GlideSpeed *= keep;
            m_BoostSpeed *= keep;
        }

        private void Fly(float dt)
        {
            if (dt <= 0f) return;

            Vector2 stick = m_Motor.MoveInput;
            float stickX = Mathf.Clamp(stick.x, -1f, 1f);
            float stickY = Mathf.Clamp(stick.y, -1f, 1f);
            // + = nose down. Inverted (the default, "like a plane"): forward tilts you down.
            float pitchInput = GameplaySettings.Current.InvertGliderControls ? stickY : -stickY;

            // Pitch, or the stall taking it over.
            if (!m_Stalled && Speed < StallSpeed && m_Pitch < StallRecoverPitch)
            {
                m_Stalled = true;
                Debug.Log($"[Items] Glide stalled ({Speed:0.#} m/s, {m_Pitch:0} degrees) - nose dropping.", this);
            }
            if (m_Stalled)
            {
                m_Pitch = Mathf.MoveTowards(m_Pitch, StallDivePitch, StallPitchRate * dt);
                if (m_Pitch >= StallRecoverPitch) m_Stalled = false;
            }
            else
            {
                m_Pitch = Mathf.Clamp(m_Pitch + pitchInput * PitchRate * dt, MinPitch, MaxPitch);
            }

            // Turning, and the bank that goes with it (turning right = right wing down = negative roll).
            if (Mathf.Abs(stickX) > 0.01f)
            {
                m_Heading = Quaternion.AngleAxis(stickX * TurnRate * dt, Vector3.up) * m_Heading;
                m_Heading.y = 0f;
                m_Heading = m_Heading.sqrMagnitude > 0.0001f ? m_Heading.normalized : Vector3.forward;
            }
            m_Bank = Mathf.Lerp(m_Bank, -stickX * MaxBank, 1f - Mathf.Exp(-BankSmoothing * dt));

            // Speed: gravity along the path, against squared drag that balances it at run speed at the cruise angle.
            float run = RunSpeed;
            float drag = GlideGravity * Mathf.Sin(CruiseAngle * Mathf.Deg2Rad) / (run * run);
            float sin = Mathf.Sin(m_Pitch * Mathf.Deg2Rad);
            m_GlideSpeed += (GlideGravity * sin - drag * m_GlideSpeed * m_GlideSpeed) * dt;
            if (m_GlideSpeed < 0f)
            {
                // Climbing on the boost alone: it pays for the climb.
                m_BoostSpeed = Mathf.Max(0f, m_BoostSpeed + m_GlideSpeed);
                m_GlideSpeed = 0f;
            }
            m_BoostSpeed = Mathf.Max(0f, m_BoostSpeed - BoostFade * dt);

            float cap = MaxSpeedScale * run;
            if (Speed > cap)
            {
                float over = Speed - cap;
                float fromBoost = Mathf.Min(over, m_BoostSpeed);
                m_BoostSpeed -= fromBoost;
                m_GlideSpeed -= over - fromBoost;
            }
        }

        // =====================================================================================
        // The stamina boost
        // =====================================================================================

        private bool UseItemPressed()
        {
            if (m_Bindings == null || InGameMenu.IsOpen || CoreInputHandler.GameplayInputBlocked) return false;
            return BoundControls.WasPressed(m_Bindings.Keyboard.UseItem, m_Bindings.Gamepad.UseItem);
        }

        /// <summary>Use Item while gliding: 30% of the stamina bar buys +100% run speed, all at once.</summary>
        private void TryBoost()
        {
            if (m_Stats == null) return;
            float max = m_Stats.GetMaxValue(StatKeys.Stamina);
            float current = m_Stats.GetCurrentValue(StatKeys.Stamina);
            float cost = max * BoostStaminaCost;
            if (max <= 0f || current < cost - 0.001f)
            {
                Debug.Log($"[Items] Glide boost needs {BoostStaminaCost:P0} stamina ({current:0.#} / {max:0.#}).", this);
                return;
            }
            if (!m_Stats.TryConsumeStat(StatKeys.Stamina, Mathf.Min(cost, current), m_Player != null ? m_Player.OwnerClientId : 0)) return;

            float before = Speed;
            m_BoostSpeed += BoostSpeedScale * RunSpeed;
            m_Stalled = false;

            ItemDefinition rocket = ItemCatalog.Get(ItemId.RocketShoes);
            if (rocket != null && rocket.useSound != null) AudioVolumeService.PlayOneShot(rocket.useSound, AudioCategory.SoundEffects, Vector3.zero);
            Debug.Log($"[Items] Glide boost: {before:0.#} -> {Speed:0.#} m/s ({m_Stats.GetCurrentValue(StatKeys.Stamina):0.#} stamina left).", this);
        }
    }
}
