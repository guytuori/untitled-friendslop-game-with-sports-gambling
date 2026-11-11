using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The spike timings every spike challenge uses, so a player learns them once: one delay and attack speed for
    /// spikes that point up, one for spikes that point down, and one retract speed for both. A spike waits its delay
    /// after someone passes in front of it, flies <see cref="Travel"/> at its attack speed, then goes back at
    /// <see cref="RetractSpeed"/>. Worked out against the challenges (player 1.8 m tall, run speed 9 m/s; simulated
    /// at 30, 60 and 144 fps):
    ///   - SPIKES UP (hidden under a 2 m floor, tips 3 m below its top; a ceiling 4 m above the floor since
    ///     2026-11-11, after the first play): running across a spike tile at ~11.5 m/s or more gets you off it in time - so at 150% of run
    ///     speed (13.5 m/s: Rocket Shoes, dash panels, the glider) you're safe, and at plain run speed (9 m/s) you're
    ///     hit, even jumping: the spike is fast enough (100 m/s) that being up at the ceiling only buys a few
    ///     hundredths of a second (you'd need ~10 m/s there). Without the ceiling (first version: 0.15 s, 50 m/s) a
    ///     full-height jump got over at run speed;
    ///   - SPIKES DOWN (hidden above the 2 m ceiling slab, tips 7 m above its underside, ceiling 4 m above the
    ///     floor): the delay went from 0.15 to 0.3 s on 2026-11-11, after the first play, as the side routes' icy turns took too long -
    ///     running under one is now safe at ~5 m/s or more (was ~7), and jumping with your head at the ceiling at
    ///     ~5.25 m/s (was ~8). The pits themselves still need a full-speed jump (about 8.6 m of air under the 4 m
    ///     ceiling, for 8 m pits).
    /// </summary>
    public static class SpikeTiming
    {
        /// <summary>Seconds between a player passing over a spike pointing up and the spike moving.</summary>
        public const float UpDelay = 0.17f;

        /// <summary>How fast a spike pointing up flies out (m/s).</summary>
        public const float UpAttackSpeed = 100f;

        /// <summary>Seconds between a player passing under a spike pointing down and the spike moving.</summary>
        public const float DownDelay = 0.3f;

        /// <summary>How fast a spike pointing down flies out (m/s).</summary>
        public const float DownAttackSpeed = 50f;

        /// <summary>How fast every spike goes back (m/s) - slower, so the way it came stays deadly for a while.</summary>
        public const float RetractSpeed = 4f;

        /// <summary>How far it flies: six Total Editor blocks.</summary>
        public const float Travel = 12f;

        public static float DelayFor(bool down) => down ? DownDelay : UpDelay;
        public static float AttackSpeedFor(bool down) => down ? DownAttackSpeed : UpAttackSpeed;
    }

    /// <summary>
    /// The spikes of a spike challenge (2026-11-11). Put on the challenge's root by ExampleChallengeBuilder, with the
    /// spikes.glb model. When the challenge appears, every <c>spikesup</c> / <c>spikesdown</c> entity (Total Editor)
    /// under it gets a spike:
    ///   - spikes.glb (4 x 2 x 4 blocks, steel base with the points on top) at 25%, so it fits one 2 m block: a
    ///     2 x 2 m base, 1 m tall. <c>spikesup</c> points up, its base on the bottom of the entity's block (the
    ///     block under a floor tile); <c>spikesdown</c> points down, its base on the top of its block (hidden above
    ///     a ceiling);
    ///   - a spike waits hidden until a player (anyone - every machine watches every player) passes in front of it:
    ///     their middle inside its 2 x 2 m column, anywhere along the 12 m it would fly. After a short pause
    ///     (<see cref="SpikeTiming"/>) it flies six blocks in the direction of its points - straight through any
    ///     floor, ceiling or player, it has no collision - then slowly goes back and hides, ready again;
    ///   - touching it while it's out kills (every side - the points, the sides and the flat base). Each machine
    ///     only checks its own player, the same kill as the death plane (LocalRespawnTracker.KillNow).
    /// Nothing is networked: every machine sees the same players set the spikes off (others a fraction later).
    ///
    /// Hidden spikes don't draw. The columns are looked up by block, so hundreds of spikes cost next to nothing.
    /// </summary>
    public class SpikeField : MonoBehaviour
    {
        public const string UpMarker = "spikesup";
        public const string DownMarker = "spikesdown";

        private const float BlockSize = 2f;
        private const float PlayerRadius = 0.28f;
        private const float PlayerHeight = 1.8f;

        [Tooltip("spikes.glb - scaled to Model Scale and centred on each spike's block.")]
        [SerializeField] private GameObject spikeModel;

        [Tooltip("spikes.glb is 4 x 2 x 4 Total Editor blocks; 0.25 makes it one block across and half a block tall.")]
        [SerializeField] private float modelScale = 0.25f;

        private enum State { Hidden, Waiting, Out, Back }

        private class Spike
        {
            public Transform Root;
            public Renderer[] Renderers;
            public bool Down;
            public Vector2Int Column;   // block, in this object's space
            public float BaseY;         // the base's flat face when hidden, in this object's space
            public float Offset, PreviousOffset;
            public State State;
            public float GoAt;
            public bool Shown;
        }

        private readonly List<Spike> m_Spikes = new List<Spike>();
        private readonly Dictionary<Vector2Int, List<Spike>> m_ByColumn = new Dictionary<Vector2Int, List<Spike>>();
        private readonly List<Spike> m_Moving = new List<Spike>();
        private Bounds m_Area; // every spike's whole column, in this object's space
        private Vector3 m_ModelOffset;

        /// <summary>How many spikes this challenge has.</summary>
        public int Count => m_Spikes.Count;

        private void Start()
        {
            Build();
        }

        // =====================================================================================
        // Setup
        // =====================================================================================

        private void Build()
        {
            if (spikeModel == null)
            {
                Debug.LogWarning($"[SpikeField] {name} has no spike model - no spikes.", this);
                return;
            }

            var markers = new List<(Transform marker, bool down)>();
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.StartsWith(DownMarker, System.StringComparison.OrdinalIgnoreCase)) markers.Add((t, true));
                else if (t.name.StartsWith(UpMarker, System.StringComparison.OrdinalIgnoreCase)) markers.Add((t, false));
            }
            if (markers.Count == 0) return;

            var holder = new GameObject("[Spikes]").transform;
            holder.SetParent(transform, false);
            m_ModelOffset = MeasureModel();

            bool first = true;
            foreach ((Transform marker, bool down) in markers)
            {
                Vector3 local = transform.InverseTransformPoint(marker.position);
                var spike = new Spike
                {
                    Down = down,
                    Column = new Vector2Int(Mathf.FloorToInt(local.x / BlockSize), Mathf.FloorToInt(local.z / BlockSize)),
                    BaseY = local.y + (down ? 0.5f : -0.5f) * BlockSize, // the far face of the entity's block
                };

                spike.Root = new GameObject(down ? "Spike (down)" : "Spike (up)").transform;
                spike.Root.SetParent(holder, false);
                spike.Root.localRotation = down ? Quaternion.Euler(180f, 0f, 0f) : Quaternion.identity;
                GameObject model = Instantiate(spikeModel, spike.Root, false);
                model.transform.localScale = Vector3.one * modelScale;
                model.transform.localPosition = m_ModelOffset;
                model.transform.localRotation = Quaternion.identity;
                foreach (Collider c in model.GetComponentsInChildren<Collider>(true)) Destroy(c); // nothing to bump into
                spike.Renderers = model.GetComponentsInChildren<Renderer>(true);
                Place(spike);
                Show(spike, false);

                m_Spikes.Add(spike);
                if (!m_ByColumn.TryGetValue(spike.Column, out List<Spike> list))
                {
                    list = new List<Spike>(1);
                    m_ByColumn[spike.Column] = list;
                }
                list.Add(spike);

                Bounds column = ZoneOf(spike);
                if (first) { m_Area = column; first = false; }
                else m_Area.Encapsulate(column);
            }
        }

        /// <summary>
        /// Where the model goes inside a spike's root so its base sits on the root (points up its +Y) and it's centred
        /// across - the glb's own origin is a corner (and the import mirrors x).
        /// </summary>
        private Vector3 MeasureModel()
        {
            // Read straight off the model asset (its root's own space), then scaled - nothing to instantiate.
            Transform modelRoot = spikeModel.transform;
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (MeshFilter filter in spikeModel.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null) continue;
                Bounds b = filter.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y, (i & 4) == 0 ? b.min.z : b.max.z);
                    Vector3 p = modelRoot.InverseTransformPoint(filter.transform.TransformPoint(corner)) * modelScale;
                    if (!any) { min = max = p; any = true; }
                    else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                }
            }
            if (!any) return Vector3.zero;
            return new Vector3(-(min.x + max.x) * 0.5f, -min.y, -(min.z + max.z) * 0.5f);
        }

        /// <summary>The spike's whole column - where it can be, and where passing sets it off - in this object's space.</summary>
        private static Bounds ZoneOf(Spike spike)
        {
            float low = spike.Down ? spike.BaseY - 0.5f * BlockSize - SpikeTiming.Travel : spike.BaseY;
            float high = spike.Down ? spike.BaseY : spike.BaseY + 0.5f * BlockSize + SpikeTiming.Travel;
            var b = new Bounds();
            b.SetMinMax(new Vector3(spike.Column.x * BlockSize, low, spike.Column.y * BlockSize),
                        new Vector3((spike.Column.x + 1) * BlockSize, high, (spike.Column.y + 1) * BlockSize));
            return b;
        }

        /// <summary>The spike body's height range (this object's space) for an offset.</summary>
        private static void BodyRange(Spike spike, float offset, out float low, out float high)
        {
            float height = 0.5f * BlockSize; // 1 m
            if (spike.Down) { high = spike.BaseY - offset; low = high - height; }
            else { low = spike.BaseY + offset; high = low + height; }
        }

        private void Place(Spike spike)
        {
            float y = spike.Down ? spike.BaseY - spike.Offset : spike.BaseY + spike.Offset;
            spike.Root.localPosition = new Vector3((spike.Column.x + 0.5f) * BlockSize, y, (spike.Column.y + 0.5f) * BlockSize);
        }

        private static void Show(Spike spike, bool show)
        {
            spike.Shown = show;
            foreach (Renderer r in spike.Renderers)
            {
                if (r != null) r.enabled = show;
            }
        }

        // =====================================================================================
        // Every frame
        // =====================================================================================

        private void Update()
        {
            if (m_Spikes.Count == 0) return;
            float now = Time.time;

            SetOffPassedSpikes(now);

            for (int i = m_Moving.Count - 1; i >= 0; i--)
            {
                Spike spike = m_Moving[i];
                spike.PreviousOffset = spike.Offset;
                switch (spike.State)
                {
                    case State.Waiting:
                        if (now < spike.GoAt) continue;
                        spike.State = State.Out;
                        Show(spike, true);
                        spike.Offset = Mathf.Min(SpikeTiming.Travel, SpikeTiming.AttackSpeedFor(spike.Down) * (now - spike.GoAt));
                        break;
                    case State.Out:
                        spike.Offset = Mathf.Min(SpikeTiming.Travel, spike.Offset + SpikeTiming.AttackSpeedFor(spike.Down) * Time.deltaTime);
                        if (spike.Offset >= SpikeTiming.Travel) spike.State = State.Back;
                        break;
                    case State.Back:
                        spike.Offset = Mathf.Max(0f, spike.Offset - SpikeTiming.RetractSpeed * Time.deltaTime);
                        if (spike.Offset <= 0f)
                        {
                            spike.State = State.Hidden;
                            Show(spike, false);
                            Place(spike);
                            m_Moving.RemoveAt(i);
                            continue;
                        }
                        break;
                }
                Place(spike);
            }

            KillLocalPlayerIfTouching();
        }

        /// <summary>Any player whose middle is in a hidden spike's column sets it off.</summary>
        private void SetOffPassedSpikes(float now)
        {
            foreach (CorePlayerManager player in NetworkPlayers.All)
            {
                if (player == null) continue;
                Vector3 feet = transform.InverseTransformPoint(player.transform.position);
                if (feet.x < m_Area.min.x || feet.x > m_Area.max.x || feet.z < m_Area.min.z || feet.z > m_Area.max.z) continue;
                if (feet.y + PlayerHeight < m_Area.min.y || feet.y > m_Area.max.y) continue;

                var column = new Vector2Int(Mathf.FloorToInt(feet.x / BlockSize), Mathf.FloorToInt(feet.z / BlockSize));
                if (!m_ByColumn.TryGetValue(column, out List<Spike> spikes)) continue;
                foreach (Spike spike in spikes)
                {
                    if (spike.State != State.Hidden) continue;
                    Bounds zone = ZoneOf(spike);
                    if (feet.y + PlayerHeight < zone.min.y || feet.y > zone.max.y) continue;
                    spike.State = State.Waiting;
                    spike.GoAt = now + SpikeTiming.DelayFor(spike.Down);
                    spike.Offset = spike.PreviousOffset = 0f;
                    m_Moving.Add(spike);
                }
            }
        }

        /// <summary>This machine's player touching a spike that's out (anywhere it passed through this frame) dies.</summary>
        private void KillLocalPlayerIfTouching()
        {
            if (m_Moving.Count == 0) return;
            CorePlayerManager player = NetworkPlayers.Local;
            LocalRespawnTracker tracker = LocalRespawnTracker.Local;
            if (player == null || tracker == null) return;
            if (player.PlayerState != null && player.PlayerState.LifeState == PlayerLifeState.Eliminated) return;

            Vector3 feet = transform.InverseTransformPoint(player.transform.position);
            float bodyLow = feet.y + PlayerRadius, bodyHigh = feet.y + PlayerHeight - PlayerRadius; // the capsule's middle line

            foreach (Spike spike in m_Moving)
            {
                if (spike.State == State.Waiting) continue;

                // The space the spike swept through this frame (it can move a metre in one frame).
                BodyRange(spike, spike.PreviousOffset, out float lowA, out float highA);
                BodyRange(spike, spike.Offset, out float lowB, out float highB);
                float low = Mathf.Min(lowA, lowB), high = Mathf.Max(highA, highB);

                float x0 = spike.Column.x * BlockSize, z0 = spike.Column.y * BlockSize;
                float dx = Mathf.Max(0f, Mathf.Max(x0 - feet.x, feet.x - (x0 + BlockSize)));
                float dz = Mathf.Max(0f, Mathf.Max(z0 - feet.z, feet.z - (z0 + BlockSize)));
                float dy = Mathf.Max(0f, Mathf.Max(low - bodyHigh, bodyLow - high));
                if (dx * dx + dy * dy + dz * dz < PlayerRadius * PlayerRadius)
                {
                    tracker.KillNow();
                    return;
                }
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.6f);
            foreach (Spike spike in m_Spikes)
            {
                BodyRange(spike, spike.Offset, out float low, out float high);
                Gizmos.DrawWireCube(new Vector3((spike.Column.x + 0.5f) * BlockSize, (low + high) * 0.5f, (spike.Column.y + 0.5f) * BlockSize),
                    new Vector3(BlockSize, high - low, BlockSize));
            }
        }
#endif
    }
}
