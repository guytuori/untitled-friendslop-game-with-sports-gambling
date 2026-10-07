using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Controls the player's Animator component based on the state of the <see cref="CoreMovement"/> controller.
    /// This component is responsible for setting locomotion parameters (speed, grounded, jump, etc.)
    /// and handling Animation Events to trigger sound effects like footsteps and landing sounds.
    ///
    /// Networking (Photon Fusion, Shared mode): only the owning client drives the Animator; the Fusion
    /// NetworkMecanimAnimator on the player's root replicates the parameters and states to everyone else.
    /// Triggers go through that NetworkMecanimAnimator so they reach other clients too. (This component
    /// used to be a Netcode for GameObjects NetworkAnimator itself.)
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class CoreAnimator : MonoBehaviour
    {
        #region Fields & Properties

        [Tooltip("The Animator this component drives (on this GameObject).")]
        [SerializeField] private Animator m_Animator;

        /// <summary>The Animator this component drives.</summary>
        public Animator Animator => m_Animator;

        private CorePlayerManager m_PlayerManager;
        private NetworkMecanimAnimator m_NetworkAnimator;

        /// <summary>True on the client that owns this player (the only one that drives the Animator).</summary>
        private bool IsOwner => m_PlayerManager != null && m_PlayerManager.IsOwner;

        [Header("Component Dependencies")]
        [Tooltip("Reference to the CoreMovement component to get movement state information.")]
        [SerializeField] private CoreMovement coreMovement;

        [Header("Sound Effects")]
        [Tooltip("Sound definition for footstep sounds.")]
        [SerializeField] private SoundDef soundDefFootstep;
        
        private readonly int m_AnimIDSpeed = Animator.StringToHash("Speed");
        private readonly int m_AnimIDGrounded = Animator.StringToHash("Grounded");
        private readonly int m_AnimIDJump = Animator.StringToHash("Jump");
        private readonly int m_AnimIDFreeFall = Animator.StringToHash("FreeFall");
        private readonly int m_AnimIDMotionSpeed = Animator.StringToHash("MotionSpeed");

        private readonly int m_AnimIDActionIndex = Animator.StringToHash("ActionIndex");

        public readonly int m_AnimIDTrigger_exitAbility = Animator.StringToHash("ExitAbility");

        public readonly string ANIMATION_grindrail_slide = "slide";
        public readonly string ANIMATION_wall_hang = "wall_hang_idle";

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            if (m_Animator == null) m_Animator = GetComponent<Animator>();
            m_PlayerManager = GetComponentInParent<CorePlayerManager>();
            m_NetworkAnimator = GetComponentInParent<NetworkMecanimAnimator>();

            if (coreMovement == null)
            {
                Debug.LogError("[Core Animator] needs a CoreMovement component.");
            }

            if (soundDefFootstep == null)
            {
                Debug.LogError("[Core Animator] Footstep SoundDef is not assigned.");
            }
        }

        private void Update()
        {
            // We only want the owner to drive the Animator.
            // The NetworkMecanimAnimator propagates these changes to other clients.
            if (!IsOwner || coreMovement == null) return;

            UpdateLocomotionParameters();
        }

        #endregion

        #region Animation Events

        public void OnFootstepWalk(AnimationEvent animationEvent)
        {
            // >=0.5 - We want to be sure that if both walk and run weights are equal, that we only trigger one SFX.
            if (animationEvent.animatorClipInfo.weight >= 0.5f)
            {
                OnFootstep(animationEvent, 0, 0.75f, 0);
            }
        }

        public void OnFootstepRun(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                OnFootstep(animationEvent, 500, 1, 2000);
            }
        }

        /// <summary>
        /// This method is called by an AnimationEvent defined in the walk/run animation clips.
        /// It plays a random footstep sound.
        /// </summary>
        /// <param name="animationEvent">Data from the animation event.</param>
        /// <param name="walkRunPitchCents"></param>
        /// <param name="walkRunVolumeScale"></param>
        /// <param name="filterCutoffOffset"></param>
        public void OnFootstep(AnimationEvent animationEvent, float walkRunPitchCents, float walkRunVolumeScale, float filterCutoffOffset)
        {
            var overrideData = new SoundEmitter.SoundDefOverrideData
            {
                BasePitchInCents = walkRunPitchCents,
                VolumeScale = walkRunVolumeScale,
                BaseLowPassCutoff = filterCutoffOffset
            };

            CoreDirector.RequestAudio(soundDefFootstep)
                .AttachedTo(transform)
                .WithOverrides(overrideData)
                .AsReserved(SoundEmitter.ReservedInfo.ReservedEmitterAndAudioSources)
                .Play();
        }

        /// <summary>
        /// This method is called by an AnimationEvent defined in the landing animation clip.
        /// It plays the landing sound effect.
        /// </summary>
        /// <param name="animationEvent">Data from the animation event.</param>
        public void OnLand(AnimationEvent animationEvent)
        {
            CoreDirector.RequestAudio(soundDefFootstep)
                .AttachedTo(transform)
                .AsReserved(SoundEmitter.ReservedInfo.ReservedEmitterAndAudioSources)
                .Play();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Reads the current state from the CoreMovement component and updates the Animator parameters accordingly.
        /// </summary>
        private void UpdateLocomotionParameters()
        {
            bool isGrinding = coreMovement.IsGrinding;
            bool isClimbing = coreMovement.IsClimbing;
            bool isPoleGrabbing = coreMovement.IsPoleGrabbing;

            bool isGrounded = coreMovement.IsGrounded;
            bool inAir = !isGrounded && !isClimbing && !isPoleGrabbing;

            float verticalVelocity = coreMovement.VerticalVelocity;

            

            // Set booleans for grounded, jumping, and falling states.
            Animator.SetBool(m_AnimIDGrounded, isGrounded);
            Animator.SetBool(m_AnimIDJump, inAir && verticalVelocity > 0.1f);
            Animator.SetBool(m_AnimIDFreeFall, inAir && verticalVelocity <= 0.1f);

            // Set floats for speed and input magnitude to drive blend trees.
            Animator.SetFloat(m_AnimIDSpeed, coreMovement.CurrentSpeed);
            Animator.SetFloat(m_AnimIDMotionSpeed, coreMovement.InputMagnitude);

        }

        public void TurnInPlaceStart()
        {
            Animator.applyRootMotion = true;
        }

        public void TurnInPlaceEnd()
        {
            Animator.applyRootMotion = false;
        }

        #endregion



        #region animation parameters
        public void PlayAnimation(string _animation)
        {
            if (Animator == null) { return; }

            if (ContainsAnimation(Animator, _animation))
            { Animator.Play(_animation); }
        }


        public void SetAnimationTrigger(string _animParameter)
        {
            if (Animator == null) { return; }

            if (ContainsParam(Animator, _animParameter))
            { SetTriggerNetworked(Animator.StringToHash(_animParameter)); }
        }
        public void SetAnimationTrigger(int _animParameter)
        {
            if (Animator == null) { return; }

            SetTriggerNetworked(_animParameter);
        }

        /// <summary>
        /// Animator triggers aren't picked up by NetworkMecanimAnimator unless they go through it - so the
        /// owner sets them there (it forwards to the local Animator too); everyone else just sets them locally.
        /// </summary>
        private void SetTriggerNetworked(int triggerHash)
        {
            if (IsOwner && m_NetworkAnimator != null && m_NetworkAnimator.Object != null && m_NetworkAnimator.Object.IsValid)
            {
                m_NetworkAnimator.SetTrigger(triggerHash);
            }
            else
            {
                Animator.SetTrigger(triggerHash);
            }
        }
        public void SetAnimationParameter(string _animParameter, int _animState)
        {
            if (Animator == null) { return; }

            if (ContainsParam(Animator, _animParameter))
            { Animator.SetInteger(_animParameter, _animState); }
        }

        public void SetAnimationParameter(string _animParameter, float _animState)
        {
            if (Animator == null) { return; }

            if (ContainsParam(Animator, _animParameter))
            { Animator.SetFloat(_animParameter, _animState); }
        }

        public void SetAnimationParameter(string _animParameter, bool _animState)
        {
            if (Animator == null) { return; }

            if (ContainsParam(Animator, _animParameter))
            { Animator.SetBool(_animParameter, _animState); }
        }

        public bool ContainsParam(Animator _Anim, string _ParamName)
        {
            foreach (AnimatorControllerParameter param in _Anim.parameters)
            {
                if (param.name.ToLower() == _ParamName.ToLower()) return true;
            }
            return false;
        }
        public bool ContainsAnimation(Animator _Anim, string _name)
        {

            return _Anim.HasState(0, Animator.StringToHash(_name));
        }
        #endregion


    }
}
