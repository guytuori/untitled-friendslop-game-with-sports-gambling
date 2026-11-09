using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Riding a zoo dinosaur (2026-11-08). Interact next to a dinosaur (DinosaurInteract) climbs onto its back: it
    /// roars, and the player clings on in the wall-climb pose, along for the ride - no control, the dinosaur just
    /// carries on with its slow walk and its habits. Jump or Interact again lets go (a little hop off).
    ///
    /// The dinosaurs are in step for everyone (DinosaurWander's networking), so the rider - who is moved by their
    /// own machine onto its own copy of the dinosaur - shows up on the same dinosaur for everyone else, through the
    /// player's own NetworkTransform; the cling pose through the model's NetworkTransform and the Animator state.
    ///
    /// Added to the local player at runtime (<see cref="For"/>). While riding, CoreMovement.IsRiding is set, which
    /// counts as a movement ability: walking, jumping, air jumps, the glider, the jetpack and dash panels all stand
    /// aside. A rail, pole or wall grabbed mid-ride takes over (the ride just ends). Respawning, a teleport or a
    /// Retry ends it too (CoreMovement.ResetMovementForces -> <see cref="Cancel"/>).
    /// Counted for the dinosaur wagers by LocalActionTracker (rides, seconds riding).
    /// </summary>
    [DefaultExecutionOrder(50)] // LateUpdate after the dinosaurs' own (their graze bending)
    public class DinosaurRider : MonoBehaviour, IMovementAbility
    {
        /// <summary>Up speed (m/s) when hopping off.</summary>
        private const float HopUpSpeed = 6f;

        /// <summary>Sideways push when hopping off (an impulse - CoreMovement's external force).</summary>
        private const float HopPush = 8f;

        /// <summary>The clinging body lies this far above the back, and is moved back this far so it's centred on the saddle.</summary>
        private const float BodyLift = 0.2f;
        private const float BodyBack = 0.9f;

        /// <summary>Riders spread along the back so two on one dinosaur don't sit in each other (meters apart).</summary>
        private const float RiderSpacing = 0.9f;

        // Before JumpAbility (10) and everything else, so the ride is sorted out first each frame.
        public int Priority => 11;

        public float StaminaCost => 0f;

        private static DinosaurRider s_Local;

        private CoreMovement m_Motor;
        private CorePlayerManager m_Player;
        private CoreInputHandler m_Input;
        private CoreAnimator m_Animator;
        private Transform m_Model;

        private DinosaurWander m_Dinosaur;
        private int m_DinosaurIndex = -1;
        private int m_SeenJumpPresses;
        private float m_StartedAt;
        private float m_Slot;

        private System.Func<Quaternion> m_Facing;
        private System.Func<Vector3, Vector3> m_Hold;
        private System.Func<Vector3, Vector3> m_PreviousMoveOverride;

        private bool m_ModelPosed;
        private Vector3 m_ModelLocalPosition;
        private Quaternion m_ModelLocalRotation;

        /// <summary>On a dinosaur right now.</summary>
        public bool IsRiding => m_Dinosaur != null;

        /// <summary>The dinosaur being ridden, or null.</summary>
        public DinosaurWander Dinosaur => m_Dinosaur;

        /// <summary>Is this machine's player riding <paramref name="dinosaur"/>? (Its interact arrow hides then.)</summary>
        public static bool IsLocalPlayerRiding(DinosaurWander dinosaur) =>
            s_Local != null && dinosaur != null && s_Local.m_Dinosaur == dinosaur;

        /// <summary>The player's rider ability, added (and registered with its CoreMovement) the first time.</summary>
        public static DinosaurRider For(CorePlayerManager player)
        {
            if (player == null || player.CoreMovement == null) return null;
            CoreMovement motor = player.CoreMovement;
            var rider = motor.GetComponent<DinosaurRider>();
            if (rider == null)
            {
                rider = motor.gameObject.AddComponent<DinosaurRider>();
                motor.AddAbility(rider); // calls Initialize
            }
            rider.m_Player = player;
            if (player.IsOwner) s_Local = rider;
            return rider;
        }

        public void Initialize(CoreMovement motor) => m_Motor = motor;

        public bool TryActivate() => false;

        private void OnDisable()
        {
            if (IsRiding) GetOff(hop: false, exitAnimation: true, reason: "disabled");
        }

        private void OnDestroy()
        {
            if (s_Local == this) s_Local = null;
        }

        // =====================================================================================
        // Getting on and off
        // =====================================================================================

        /// <summary>Can this player climb on now? (Alive, spawned, not on a rail, wall or pole, round still open.)</summary>
        public bool CanMount()
        {
            if (IsRiding || m_Motor == null || m_Player == null || !m_Player.IsSpawned) return false;
            if (m_Player.PlayerState != null && m_Player.PlayerState.LifeState == PlayerLifeState.Eliminated) return false;
            if (m_Motor.MovementAbilityActive() || !m_Motor.IsMovementEnabled) return false;
            return ChallengeManager.IsRoundOpenFor(m_Player.OwnerClientId);
        }

        /// <summary>Climbs onto dinosaur <paramref name="index"/> (Dinosaurs.All) - it roars for everyone.</summary>
        public bool Mount(int index)
        {
            DinosaurWander dinosaur = Dinosaurs.Get(index);
            if (dinosaur == null || !CanMount()) return false;

            m_Dinosaur = dinosaur;
            m_DinosaurIndex = index;
            m_StartedAt = Time.time;
            m_Input = m_Player.CoreInput != null ? m_Player.CoreInput : m_Player.GetComponentInChildren<CoreInputHandler>();
            m_SeenJumpPresses = m_Input != null ? m_Input.JumpPressCount : 0;
            m_Animator = m_Motor.GetComponentInChildren<CoreAnimator>();
            m_Model = m_Animator != null ? m_Animator.transform : null;
            int slot = (int)(m_Player.OwnerClientId % 3); // 0, 1, 2 -> middle, ahead, behind
            m_Slot = slot == 0 ? 0f : slot == 1 ? RiderSpacing : -RiderSpacing;

            // Nothing else moves the body or turns it while riding.
            m_PreviousMoveOverride = m_Motor.FinalMoveCalculationOverride;
            // (Harmless if left behind: Pole Swing 2 saves whatever override was set and puts it back after a swing.)
            m_Hold = move => m_Dinosaur != null ? Vector3.zero : move;
            m_Motor.FinalMoveCalculationOverride = m_Hold;
            m_Facing = () => m_Dinosaur != null ? Quaternion.LookRotation(m_Dinosaur.Facing, Vector3.up) : m_Motor.transform.rotation;
            m_Motor.RotationOverride = m_Facing;
            m_Motor.SetRideState(true);
            m_Motor.SetVerticalVelocity(0f);
            m_Motor.ApplyExternalForce(Vector3.zero, ForceMode.Impulse); // drops any push still going
            var walk = m_Motor.GetComponent<WalkAbility>();
            if (walk != null) walk.ResetMomentum();

            m_Motor.SetPosition(Seat(), teleport: true);

            if (m_Animator != null)
            {
                if (m_Animator.Animator != null) m_Animator.Animator.ResetTrigger(m_Animator.m_AnimIDTrigger_exitAbility);
                m_Animator.PlayAnimation(m_Animator.ANIMATION_wall_hang);
            }

            Dinosaurs.RequestSound(index); // it roars
            Debug.Log($"[Dinosaurs] Riding '{dinosaur.name}'.", this);
            return true;
        }

        /// <summary>Jump or Interact while riding: a little hop off.</summary>
        public void Dismount() => GetOff(hop: true, exitAnimation: true, reason: "let go");

        /// <summary>Respawn, teleport, Retry (CoreMovement.ResetMovementForces): off at once, no hop.</summary>
        public void Cancel()
        {
            if (IsRiding) GetOff(hop: false, exitAnimation: true, reason: "respawned or teleported");
        }

        private void GetOff(bool hop, bool exitAnimation, string reason)
        {
            DinosaurWander dinosaur = m_Dinosaur;
            m_Dinosaur = null;
            m_DinosaurIndex = -1;
            RestoreModel();
            if (m_Motor == null) return;

            m_Motor.SetRideState(false);
            if (m_Motor.RotationOverride == m_Facing) m_Motor.RotationOverride = null;
            if (m_Motor.FinalMoveCalculationOverride == m_Hold) m_Motor.FinalMoveCalculationOverride = m_PreviousMoveOverride;
            m_Facing = null;
            m_Hold = null;
            m_PreviousMoveOverride = null;

            if (exitAnimation && m_Animator != null) m_Animator.SetAnimationTrigger(m_Animator.m_AnimIDTrigger_exitAbility);

            if (hop)
            {
                // Off to the side the stick points (camera-relative), or the dinosaur's right.
                Vector3 side = dinosaur != null ? Vector3.Cross(Vector3.up, dinosaur.Facing) : m_Motor.transform.right;
                Vector2 stick = m_Motor.MoveInput;
                if (stick.sqrMagnitude > 0.04f)
                {
                    Vector3 input = Quaternion.Euler(0f, m_Motor.TargetRotationY, 0f) * new Vector3(stick.x, 0f, stick.y);
                    if (input.sqrMagnitude > 0.0001f) side = input.normalized;
                }
                m_Motor.SetVerticalVelocity(HopUpSpeed);
                m_Motor.ApplyExternalForce(side * HopPush, ForceMode.Impulse);
            }

            if (dinosaur != null) Debug.Log($"[Dinosaurs] Off '{dinosaur.name}' ({reason}) after {Time.time - m_StartedAt:0.#} s.", this);
        }

        // =====================================================================================
        // Each frame
        // =====================================================================================

        public MovementModifier Process()
        {
            var modifier = new MovementModifier();
            if (!IsRiding || m_Motor == null) return modifier;

            RestoreModel(); // last frame's pose comes off before anything else looks at the model

            int presses = m_Input != null ? m_Input.JumpPressCount : m_SeenJumpPresses;
            bool jumped = presses != m_SeenJumpPresses;
            m_SeenJumpPresses = presses;

            bool eliminated = m_Player != null && m_Player.PlayerState != null && m_Player.PlayerState.LifeState == PlayerLifeState.Eliminated;
            bool takenOver = m_Motor.RotationOverride != m_Facing || m_Motor.FinalMoveCalculationOverride != m_Hold; // a rail, pole or wall grabbed
            if (m_Dinosaur == null || !m_Dinosaur.isActiveAndEnabled) GetOff(hop: false, exitAnimation: true, reason: "the dinosaur went away");
            else if (eliminated) GetOff(hop: false, exitAnimation: true, reason: "eliminated");
            else if (takenOver) GetOff(hop: false, exitAnimation: false, reason: "grabbed something else");
            else if (jumped) GetOff(hop: true, exitAnimation: true, reason: "jumped off");

            if (!IsRiding) return modifier;

            m_Motor.SetVerticalVelocity(0f);
            modifier.OverrideGravity = true;
            return modifier;
        }

        /// <summary>Where the body sits: the dinosaur's saddle point, spread along its back by rider.</summary>
        private Vector3 Seat() => m_Dinosaur.SaddlePoint + m_Dinosaur.Facing * m_Slot;

        /// <summary>After the dinosaur has moved and been posed this frame: put the body on its back and lay the model along it.</summary>
        private void LateUpdate()
        {
            if (!IsRiding || m_Motor == null) return;

            m_Motor.SetPosition(Seat(), teleport: false);

            if (m_Model == null) return;
            m_ModelLocalPosition = m_Model.localPosition;
            m_ModelLocalRotation = m_Model.localRotation;
            m_ModelPosed = true;

            // Clinging on, face down along the back, head towards the dinosaur's head (the wall-climb pose turned
            // flat): tip the model forward 90 degrees, lift it clear of the back and centre it on the seat.
            Vector3 forward = m_Dinosaur.Facing;
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            Quaternion lieDown = Quaternion.AngleAxis(90f, right);
            Vector3 seat = m_Motor.transform.position;
            m_Model.SetPositionAndRotation(seat + Vector3.up * BodyLift - forward * BodyBack, lieDown * m_Model.rotation);
        }

        private void RestoreModel()
        {
            if (!m_ModelPosed) return;
            m_ModelPosed = false;
            if (m_Model == null) return;
            m_Model.localPosition = m_ModelLocalPosition;
            m_Model.localRotation = m_ModelLocalRotation;
        }
    }
}
