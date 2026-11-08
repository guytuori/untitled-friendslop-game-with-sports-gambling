using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Dash panels (added 2026-11-07), as in the 3D Sonic games: Assets/Maps/dash_panel_1x1.glb (one block) and
    /// dash_panel_2x2.glb (2 x 2 blocks). Any object whose name starts with <c>dash_panel</c> - placed in a map or a
    /// challenge, set to Regular so it's solid - becomes one when <see cref="MatchLayout"/> builds the match.
    /// Their chevron texture (dash_panel_1 / dash_panel_2) animates like water (AnimatedTiles).
    ///
    /// Touching one (standing on it, or landing on it) on the local player's machine:
    ///   - turns the player to face the way the arrows point (the model's -Z, so rotate the object to aim it);
    ///   - sets their speed to <see cref="SpeedMultiplier"/> (1.5) x run speed - the same as Rocket Shoes - or keeps
    ///     it if they're already faster, held for <see cref="BoostSeconds"/> on the ground (WalkAbility.Dash), even
    ///     with the stick let go;
    ///   - centres them across the panel, without moving them along it (land on its near edge and you run along it
    ///     for a moment; land on its far edge and you're straight off);
    ///   - plays the Rocket Shoes sound (once per panel touched).
    /// Jumping during the boost gives the rail jump's limited air control: half for 0.2 s (WalkAbility.CarryMomentum).
    /// The boost's speed carries into the air and onto grind rails (GrindRail2 starts you that much faster).
    /// </summary>
    public class DashPanel : MonoBehaviour
    {
        /// <summary>Speed on a dash panel, as a multiple of run speed (CoreMovement.moveSpeed) - the same as Rocket Shoes.</summary>
        public const float SpeedMultiplier = 1.5f;

        /// <summary>How long the boost holds on the ground after the panel (seconds), before normal running takes over.</summary>
        public const float BoostSeconds = 1f;

        /// <summary>Jumping during the boost: air control is scaled by this for <see cref="JumpReducedControlSeconds"/>.</summary>
        public const float JumpAirControlScale = 0.5f;
        public const float JumpReducedControlSeconds = 0.2f;

        public const string NamePrefix = "dash_panel";

        private const float AboveTop = 0.4f;   // how far above the panel's top the feet can be and still touch it
        private const float BelowTop = 0.35f;  // and below (the ground check holds the feet a little low)

        private static readonly List<DashPanel> s_All = new List<DashPanel>();

        /// <summary>Every dash panel in play.</summary>
        public static IReadOnlyList<DashPanel> All => s_All;

        // The top surface, in this object's own space.
        private float m_MinX, m_MaxX, m_MinZ, m_MaxZ, m_TopY;
        private bool m_Ready;
        private bool m_Touching;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            MatchLayout.Built -= OnMatchBuilt;
            MatchLayout.Built += OnMatchBuilt;
        }

        private static void OnMatchBuilt(MatchLayout layout)
        {
            if (layout == null) return;
            int added = SetUpUnder(layout.transform);
            if (added > 0) Debug.Log($"[DashPanel] {added} dash panel(s) set up ({s_All.Count} in play).", layout);
        }

        /// <summary>Turns every dash_panel object under <paramref name="root"/> into a dash panel. Returns how many were new.</summary>
        public static int SetUpUnder(Transform root)
        {
            if (root == null) return 0;
            int added = 0;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsPanelName(t.name) || HasPanelParent(t) || t.GetComponent<DashPanel>() != null) continue;
                t.gameObject.AddComponent<DashPanel>();
                added++;
            }
            return added;
        }

        public static bool IsPanelName(string name) =>
            !string.IsNullOrEmpty(name) && name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);

        private static bool HasPanelParent(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                if (IsPanelName(p.name)) return true;
            }
            return false;
        }

        private void OnEnable() => s_All.Add(this);

        private void OnDisable()
        {
            s_All.Remove(this);
            m_Touching = false;
        }

        private void Start() => MeasureTop();

        /// <summary>The panel's top rectangle, in its own space, from its renderers.</summary>
        private void MeasureTop()
        {
            m_MinX = m_MinZ = float.MaxValue;
            m_MaxX = m_MaxZ = float.MinValue;
            m_TopY = float.MinValue;
            bool any = false;
            foreach (MeshFilter filter in GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                Bounds b = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 local = transform.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    m_MinX = Mathf.Min(m_MinX, local.x);
                    m_MaxX = Mathf.Max(m_MaxX, local.x);
                    m_MinZ = Mathf.Min(m_MinZ, local.z);
                    m_MaxZ = Mathf.Max(m_MaxZ, local.z);
                    m_TopY = Mathf.Max(m_TopY, local.y);
                    any = true;
                }
            }
            m_Ready = any;
            if (!any) Debug.LogWarning($"[DashPanel] '{name}' has no mesh - it can't be touched.", this);
        }

        /// <summary>The way the arrows point (level, world space).</summary>
        public Vector3 Direction
        {
            get
            {
                Vector3 d = transform.TransformDirection(Vector3.back);
                d.y = 0f;
                return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.forward;
            }
        }

        private void Update()
        {
            if (!m_Ready) return;

            CorePlayerManager player = NetworkPlayers.Local;
            CoreMovement movement = player != null ? player.CoreMovement : null;
            bool touching = movement != null && IsTouching(player, movement);
            if (touching) Boost(player, movement, !m_Touching);
            m_Touching = touching;
        }

        private bool IsTouching(CorePlayerManager player, CoreMovement movement)
        {
            if (!player.IsSpawned) return false;
            if (player.PlayerState != null && player.PlayerState.LifeState == PlayerLifeState.Eliminated) return false;
            if (movement.MovementAbilityActive()) return false; // on a rail, wall or pole
            if (movement.VerticalVelocity > 0.5f) return false; // on the way up (jumping off it)

            Vector3 feet = transform.InverseTransformPoint(movement.transform.position);
            if (feet.x < m_MinX || feet.x > m_MaxX || feet.z < m_MinZ || feet.z > m_MaxZ) return false;

            // Scaled panels: compare heights in world metres.
            float scale = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));
            float above = (feet.y - m_TopY) * scale;
            return above <= AboveTop && above >= -BelowTop;
        }

        private void Boost(CorePlayerManager player, CoreMovement movement, bool first)
        {
            Vector3 direction = Direction;

            // Centre across the panel (not along it).
            Vector3 feet = transform.InverseTransformPoint(movement.transform.position);
            float centreX = (m_MinX + m_MaxX) * 0.5f;
            if (Mathf.Abs(feet.x - centreX) > 0.01f)
            {
                Vector3 world = transform.TransformPoint(new Vector3(centreX, feet.y, feet.z));
                world.y = movement.transform.position.y;
                movement.SetPosition(world, teleport: false);
            }

            // Face the arrows.
            Quaternion facing = Quaternion.LookRotation(direction, Vector3.up);
            movement.transform.rotation = facing;
            if (movement.RotationTransform != null) movement.RotationTransform.rotation = facing;

            // 1.5x run speed (or whatever faster speed they arrived with), along the arrows.
            float speed = Mathf.Max(SpeedMultiplier * movement.moveSpeed, movement.HorizontalVelocity.magnitude);
            WalkAbility walk = movement.GetComponent<WalkAbility>();
            if (walk != null) walk.Dash(direction, speed, BoostSeconds, JumpReducedControlSeconds, JumpAirControlScale);

            if (first)
            {
                ItemDefinition rocket = ItemCatalog.Get(ItemId.RocketShoes);
                if (rocket != null && rocket.useSound != null) AudioVolumeService.PlayOneShot(rocket.useSound, AudioCategory.SoundEffects, Vector3.zero);
                Debug.Log($"[DashPanel] '{name}': dash at {speed:0.0} m/s.", this);
            }
        }
    }
}
