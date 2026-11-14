using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Collapse blocks (added 2026-11-13): Assets/Maps/collapse_block.glb, a one-block wooden pallet. Any object whose
    /// name starts with <c>collapse_block</c> - placed in a map or a challenge, set to Regular so it's solid - becomes
    /// one when <see cref="MatchLayout"/> builds the match (like dash panels).
    ///
    /// Stand on one and it sinks one Total Editor block (<see cref="SinkDistance"/>, 2 m) over <see cref="SinkSeconds"/>
    /// (1 s; slow at first, then faster), then disappears. <see cref="RespawnSeconds"/> (5 s) later it's back where it
    /// started - a little later if this machine's player is standing in that spot.
    ///
    /// EVERYONE SEES THE SAME: each machine only checks its own player. The one who steps on it sends the block's
    /// id and the moment it started (on <see cref="SharedClock"/>, the moving platforms' clock) to everyone else
    /// (<see cref="CollapseSync"/>, a static RPC), so every copy sinks, vanishes and comes back on the same timeline.
    /// The id is a hash of the block's place in the hierarchy, which MatchLayout builds the same on every machine.
    ///
    /// Practice Challenges' Retry puts every block back (<see cref="ResetAll"/>). The respawn tracker doesn't record
    /// safe spots on them (<see cref="LocalPlayerOnOne"/>), since they won't be there later.
    /// </summary>
    [DefaultExecutionOrder(-190)] // after the moving platforms (-200), before the players
    public class CollapseBlock : MonoBehaviour
    {
        public const string NamePrefix = "collapse_block";

        /// <summary>Seconds from being stood on to disappearing.</summary>
        public const float SinkSeconds = 1f;

        /// <summary>How far it sinks before it disappears: one Total Editor block.</summary>
        public const float SinkDistance = 2f;

        /// <summary>Seconds it stays gone before coming back.</summary>
        public const float RespawnSeconds = 5f;

        private const float AboveTop = 0.4f;   // how far above the top the feet can be and still count as standing on it
        private const float BelowTop = 0.35f;  // and below (the ground check holds the feet a little low)
        private const float EdgeGrace = 0.2f;  // the capsule can stand on the very edge
        private const float BodyHeight = 1.8f;
        private const float BodyRadius = 0.28f;

        private static readonly List<CollapseBlock> s_All = new List<CollapseBlock>();
        private static readonly Dictionary<int, CollapseBlock> s_ByKey = new Dictionary<int, CollapseBlock>();
        private static int s_StandingFrame = -10;

        /// <summary>Every collapse block in play.</summary>
        public static IReadOnlyList<CollapseBlock> All => s_All;

        /// <summary>This machine's player is standing on a collapse block (this frame or the last).</summary>
        public static bool LocalPlayerOnOne => Time.frameCount - s_StandingFrame <= 1;

        private Vector3 m_BasePosition;
        private float m_MinX, m_MaxX, m_MinZ, m_MaxZ, m_TopY, m_BottomY; // in this object's own space
        private bool m_Ready;
        private bool m_Started; // Start has run (the base position is known)
        private int m_Key;
        private double m_TriggerTime = double.NaN; // on SharedClock; NaN = waiting to be stood on
        private bool m_Hidden;
        private Renderer[] m_Renderers;
        private Collider[] m_Colliders;

        public bool IsIdle => double.IsNaN(m_TriggerTime);

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
            if (added > 0) Debug.Log($"[CollapseBlock] {added} collapse block(s) set up ({s_All.Count} in play).", layout);
        }

        /// <summary>Turns every collapse_block object under <paramref name="root"/> into a collapse block. Returns how many were new.</summary>
        public static int SetUpUnder(Transform root)
        {
            if (root == null) return 0;
            int added = 0;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsBlockName(t.name) || HasBlockParent(t) || t.GetComponent<CollapseBlock>() != null) continue;
                t.gameObject.AddComponent<CollapseBlock>();
                added++;
            }
            return added;
        }

        public static bool IsBlockName(string name) =>
            !string.IsNullOrEmpty(name) && name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);

        private static bool HasBlockParent(Transform t)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                if (IsBlockName(p.name)) return true;
            }
            return false;
        }

        /// <summary>Every block back in place, waiting (Practice Challenges' Retry).</summary>
        public static void ResetAll()
        {
            foreach (CollapseBlock block in s_All) block.Restore();
        }

        // =====================================================================================
        // Setup
        // =====================================================================================

        private void OnEnable() => s_All.Add(this);

        private void OnDisable()
        {
            s_All.Remove(this);
            if (s_ByKey.TryGetValue(m_Key, out CollapseBlock registered) && registered == this) s_ByKey.Remove(m_Key);
        }

        private void Start()
        {
            m_BasePosition = transform.position;
            m_Started = true;
            m_Renderers = GetComponentsInChildren<Renderer>(true);
            m_Colliders = GetComponentsInChildren<Collider>(true);

            // It moves, so its collider wants a kinematic Rigidbody (like the moving platforms).
            if (!TryGetComponent(out Rigidbody body)) body = gameObject.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;

            m_Key = StableHash(HierarchyPath(transform));
            if (s_ByKey.TryGetValue(m_Key, out CollapseBlock other) && other != null && other != this)
            {
                Debug.LogWarning($"[CollapseBlock] '{name}' has the same place in the hierarchy as '{other.name}' - other players won't see it collapse.", this);
            }
            else
            {
                s_ByKey[m_Key] = this;
            }

            MeasureBox();
        }

        /// <summary>The block's box, in its own space, from its meshes.</summary>
        private void MeasureBox()
        {
            m_MinX = m_MinZ = m_BottomY = float.MaxValue;
            m_MaxX = m_MaxZ = m_TopY = float.MinValue;
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
                    m_BottomY = Mathf.Min(m_BottomY, local.y);
                    m_TopY = Mathf.Max(m_TopY, local.y);
                    any = true;
                }
            }
            m_Ready = any;
            if (!any) Debug.LogWarning($"[CollapseBlock] '{name}' has no mesh - it can't be stood on.", this);
        }

        // =====================================================================================
        // Each frame
        // =====================================================================================

        private void Update()
        {
            if (!m_Ready) return;

            bool standing = !m_Hidden && LocalStandingOn();
            if (standing) s_StandingFrame = Time.frameCount;

            double now = SharedClock.Now;
            if (IsIdle)
            {
                if (!standing) return;
                m_TriggerTime = now;
                CollapseSync.Send(m_Key, (float)now);
            }

            double t = now - m_TriggerTime;
            if (t < 0) t = 0;
            if (t < SinkSeconds)
            {
                SetHidden(false);
                float k = (float)(t / SinkSeconds);
                transform.position = m_BasePosition + Vector3.down * (SinkDistance * k * k); // slow at first, then faster
            }
            else if (t < SinkSeconds + RespawnSeconds || LocalBodyInside())
            {
                SetHidden(true);
                transform.position = m_BasePosition;
            }
            else
            {
                Restore();
            }
        }

        private void Restore()
        {
            // Not started yet: nothing to put back (and m_BasePosition isn't known). Practice's Change Challenge builds
            // the new challenge and calls ResetAll in the same frame, before the new blocks' Start - restoring then
            // moved every new block to the world origin (2026-11-14).
            if (!m_Started) return;
            m_TriggerTime = double.NaN;
            transform.position = m_BasePosition;
            SetHidden(false);
        }

        private void SetHidden(bool hidden)
        {
            if (m_Hidden == hidden) return;
            m_Hidden = hidden;
            if (m_Renderers != null) foreach (Renderer r in m_Renderers) if (r != null) r.enabled = !hidden;
            if (m_Colliders != null) foreach (Collider c in m_Colliders) if (c != null) c.enabled = !hidden;
        }

        // =====================================================================================
        // This machine's player
        // =====================================================================================

        private bool LocalStandingOn()
        {
            CorePlayerManager player = NetworkPlayers.Local;
            CoreMovement movement = player != null ? player.CoreMovement : null;
            if (movement == null || !player.IsSpawned) return false;
            if (player.PlayerState != null && player.PlayerState.LifeState == PlayerLifeState.Eliminated) return false;
            if (movement.MovementAbilityActive()) return false; // on a rail, wall or pole
            if (movement.VerticalVelocity > 0.5f) return false;  // on the way up (jumping off it)

            Vector3 feet = transform.InverseTransformPoint(movement.transform.position);
            if (feet.x < m_MinX - EdgeGrace || feet.x > m_MaxX + EdgeGrace || feet.z < m_MinZ - EdgeGrace || feet.z > m_MaxZ + EdgeGrace) return false;

            float scale = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));
            float above = (feet.y - m_TopY) * scale;
            return above <= AboveTop && above >= -BelowTop;
        }

        /// <summary>This machine's player is where the block would come back (so it waits, rather than appear inside them).</summary>
        private bool LocalBodyInside()
        {
            CorePlayerManager player = NetworkPlayers.Local;
            CoreMovement movement = player != null ? player.CoreMovement : null;
            if (movement == null) return false;

            Vector3 feet = transform.InverseTransformPoint(movement.transform.position);
            float scale = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));
            if (feet.x < m_MinX - BodyRadius || feet.x > m_MaxX + BodyRadius || feet.z < m_MinZ - BodyRadius || feet.z > m_MaxZ + BodyRadius) return false;
            float feetY = feet.y, headY = feet.y + BodyHeight / scale;
            return headY > m_BottomY && feetY < m_TopY - 0.05f;
        }

        // =====================================================================================
        // Network
        // =====================================================================================

        /// <summary>Another player stood on block <paramref name="key"/> at <paramref name="time"/> (SharedClock).</summary>
        internal static void Receive(int key, float time)
        {
            if (!s_ByKey.TryGetValue(key, out CollapseBlock block) || block == null) return;
            double now = SharedClock.Now;
            double start = Math.Min(time, now);
            if (now - start > SinkSeconds + RespawnSeconds) return; // long over

            if (block.IsIdle)
            {
                block.m_TriggerTime = start;
            }
            else if (start < block.m_TriggerTime && block.m_TriggerTime - start < 1.0)
            {
                block.m_TriggerTime = start; // two players at once: the earlier one counts
            }
        }

        private static string HierarchyPath(Transform t)
        {
            var path = new System.Text.StringBuilder();
            for (Transform p = t; p != null; p = p.parent)
            {
                // A scene root's index can differ between machines (spawned objects); everything below it is built the same.
                path.Insert(0, p.parent == null ? "/" + p.name : "/" + p.name + "#" + p.GetSiblingIndex());
            }
            return path.ToString();
        }

        /// <summary>FNV-1a - the same on every machine (string.GetHashCode isn't guaranteed to be).</summary>
        private static int StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                {
                    hash ^= c;
                    hash *= 16777619;
                }
                return (int)hash;
            }
        }
    }

    /// <summary>
    /// The collapse blocks' static RPC (static RPCs need a SimulationBehaviour to live on, but no instance or
    /// NetworkObject - same trick as ChatSync). Never added to anything.
    /// </summary>
    public class CollapseSync : SimulationBehaviour
    {
        /// <summary>Tells everyone else that block <paramref name="key"/> was stood on at <paramref name="time"/>.</summary>
        internal static void Send(int key, float time)
        {
            NetworkRunner runner = NetworkPlayers.Runner;
            if (runner == null || !runner.IsRunning || !runner.LocalPlayer.IsRealPlayer) return;
            CollapseRpc(runner, key, time);
        }

        [Rpc]
        public static void CollapseRpc(NetworkRunner runner, int key, float time, RpcInfo info = default)
        {
            if (!info.Source.IsRealPlayer || info.Source == runner.LocalPlayer) return;
            CollapseBlock.Receive(key, time);
        }
    }
}
