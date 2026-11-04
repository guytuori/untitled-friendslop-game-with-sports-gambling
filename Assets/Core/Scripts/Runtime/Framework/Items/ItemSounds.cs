using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Item sounds while an item is working (added 2026-11-04), for the local player only, at the Sound Effects
    /// volume. Each item's own clip is its ItemDefinition.useSound (item_doublejump, item_jetpack, ...), the same
    /// clip that plays when the item is used.
    ///   - Air jumps (any, with or without boots): the 1st plays item_doublejump, the 2nd item_triplejump, the
    ///     3rd item_quadruplejump - so the player hears which air jump they've just spent.
    ///   - Jetpack: item_jetpack while it's burning, cut off the moment it stops; every new burn starts the
    ///     clip again from the beginning.
    ///   - Spring Shoes / Super Spring Shoes: their sound on every jump off the ground.
    ///   - Gravity Boots: their sound on every jump (off the ground, a rail, a wall or a pole).
    ///   - Big Arms: their sound on every jump off a pole or a climbing wall.
    ///   - Rocket Shoes / Super Rocket Shoes: their sound whenever the player starts moving from a standstill
    ///     on the ground.
    ///   - Slippery Boots: their sound on a loop while the player is moving on the ground (not in the air).
    ///   - Magnet Boots: their sound on a loop while the player is moving on slippery ground (where they're
    ///     actually doing something).
    /// Added at runtime to the local player's avatar by NetworkPlayers.Register (no prefab setup).
    /// </summary>
    public class ItemSounds : MonoBehaviour
    {
        /// <summary>Slower than this on the ground counts as stopped (Rocket Shoes).</summary>
        private const float StoppedSpeed = 0.5f;
        /// <summary>Faster than this after a stop counts as having started moving (Rocket Shoes).</summary>
        private const float StartedSpeed = 1.5f;
        /// <summary>Faster than this on the ground counts as running (Slippery / Magnet Boots).</summary>
        private const float RunningSpeed = 1f;
        /// <summary>Below this grip a floor counts as slippery (Magnet Boots).</summary>
        private const float SlipperyGrip = 0.999f;

        private CorePlayerManager m_Player;
        private CoreMovement m_Movement;
        private JetpackAbility m_Jetpack;
        private AudioSource m_Loop;
        private AudioSource m_Jet;
        private bool m_JetOn;
        private int m_AirJumpFrame = -1;
        private bool m_WasStopped = true;
        private ItemId m_LastActive;

        public static void Attach(CorePlayerManager player)
        {
            if (player == null || !player.IsOwner) return;
            if (player.GetComponent<ItemSounds>() != null) return;
            var sounds = player.gameObject.AddComponent<ItemSounds>();
            sounds.m_Player = player;
        }

        private void Awake()
        {
            m_Loop = MakeSource(loop: true);
            m_Jet = MakeSource(loop: false);
        }

        private AudioSource MakeSource(bool loop)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 0f; // heard by this player only, like the use sound
            return source;
        }

        private void Start()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();
            m_Movement = m_Player != null ? m_Player.CoreMovement : GetComponent<CoreMovement>();
        }

        private void OnEnable()
        {
            PlayerActions.Performed += HandleAction;
            DoubleJumpAddon.AirJumped += HandleAirJump;
        }

        private void OnDisable()
        {
            PlayerActions.Performed -= HandleAction;
            DoubleJumpAddon.AirJumped -= HandleAirJump;
            StopLoop();
            StopJet();
        }

        /// <summary>The timed item running right now, or None.</summary>
        private static ItemId ActiveItem
        {
            get
            {
                LocalItemSlot slot = LocalItemSlot.Local;
                return slot != null && slot.State == LocalItemSlot.SlotState.Active ? slot.Item : ItemId.None;
            }
        }

        private static AudioClip ClipOf(ItemId id) => ItemCatalog.Get(id)?.useSound;

        private static void Play(ItemId id) => AudioVolumeService.PlayOneShot(ClipOf(id), AudioCategory.SoundEffects, Vector3.zero);

        // =====================================================================================
        // Jumps
        // =====================================================================================

        private void HandleAirJump(GameObject actor, int airJump)
        {
            if (actor != gameObject) return;
            m_AirJumpFrame = Time.frameCount; // the Jump report that follows is this air jump, not one off the ground

            switch (airJump)
            {
                case 1: Play(ItemId.DoubleJumpBoots); break;
                case 2: Play(ItemId.TripleJumpBoots); break;
                case 3: Play(ItemId.QuadrupleJumpBoots); break;
            }
        }

        private void HandleAction(GameObject actor, PlayerAction action)
        {
            if (actor != gameObject) return;
            ItemId active = ActiveItem;
            if (active == ItemId.None) return;

            bool groundJump = action == PlayerAction.Jump && Time.frameCount != m_AirJumpFrame;
            switch (active)
            {
                case ItemId.SpringShoes:
                case ItemId.SuperSpringShoes:
                    if (groundJump) Play(active);
                    break;
                case ItemId.GravityBoots:
                    if (groundJump || action != PlayerAction.Jump) Play(active);
                    break;
                case ItemId.BigArms:
                    if (action == PlayerAction.PoleJump || action == PlayerAction.WallJump) Play(active);
                    break;
            }
        }

        // =====================================================================================
        // Running, sliding, burning
        // =====================================================================================

        private void Update()
        {
            if (m_Movement == null)
            {
                StopLoop();
                StopJet();
                return;
            }

            bool alive = m_Player == null || m_Player.PlayerState == null || m_Player.PlayerState.LifeState != PlayerLifeState.Eliminated;
            ItemId active = alive ? ActiveItem : ItemId.None;
            bool grounded = m_Movement.IsGrounded && !m_Movement.MovementAbilityActive();
            float speed = m_Movement.CurrentSpeed;
            float volume = AudioVolumeService.GetMultiplier(AudioCategory.SoundEffects);

            // A new item: Rocket Shoes put on mid-run wait for the next stop before sounding.
            if (active != m_LastActive)
            {
                m_LastActive = active;
                m_WasStopped = !(grounded && speed > StartedSpeed);
            }

            // Rocket Shoes: starting to move from a standstill on the ground.
            if (active == ItemId.RocketShoes || active == ItemId.SuperRocketShoes)
            {
                if (grounded && speed < StoppedSpeed) m_WasStopped = true;
                else if (m_WasStopped && grounded && speed > StartedSpeed)
                {
                    m_WasStopped = false;
                    Play(active);
                }
            }

            // Slippery Boots: running on the ground. Magnet Boots: running on slippery ground.
            bool running = grounded && speed > RunningSpeed;
            AudioClip loop = null;
            if (active == ItemId.SlipperyBoots && running) loop = ClipOf(active);
            else if (active == ItemId.MagnetBoots && running && m_Movement.SurfaceGrip < SlipperyGrip) loop = ClipOf(active);
            SetLoop(loop, volume);

            // Jetpack: only while it burns, from the top every time it lights.
            if (m_Jetpack == null) m_Jetpack = m_Movement.GetComponent<JetpackAbility>();
            bool burning = alive && m_Jetpack != null && m_Jetpack.IsEquipped && m_Jetpack.IsBurning;
            if (burning && !m_JetOn)
            {
                AudioClip clip = ClipOf(ItemId.Jetpack);
                if (clip != null)
                {
                    m_Jet.clip = clip;
                    m_Jet.time = 0f;
                    m_Jet.Play();
                }
                m_JetOn = true;
            }
            else if (!burning && m_JetOn)
            {
                StopJet();
            }
            m_Jet.volume = volume;
        }

        private void SetLoop(AudioClip clip, float volume)
        {
            if (clip == null)
            {
                StopLoop();
                return;
            }
            if (m_Loop.clip != clip || !m_Loop.isPlaying)
            {
                m_Loop.clip = clip;
                m_Loop.Play();
            }
            m_Loop.volume = volume;
        }

        private void StopLoop()
        {
            if (m_Loop != null && m_Loop.isPlaying) m_Loop.Stop();
        }

        private void StopJet()
        {
            m_JetOn = false;
            if (m_Jet != null && m_Jet.isPlaying) m_Jet.Stop();
        }
    }
}
