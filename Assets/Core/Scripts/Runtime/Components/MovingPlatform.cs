using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A platform that moves back and forth between two invisible nodes, forever (added 2026-10-28).
    ///
    /// Layout (Assets/Core/Prefabs/MovingPlatform.prefab, made by Friendslop > Challenges > Add Moving Platform):
    ///   Moving Platform      this component; stays put
    ///     Node A             an empty - where the platform's top centre is at one end
    ///     Node B             the other end
    ///     Platform           the part that moves (its origin is the top centre of its model); holds the model
    ///                        (moving_platform.glb by default, set to Regular so it has a normal collider)
    /// Drop the prefab into a challenge (or a map) and move Node A / Node B - the Scene view draws the path and
    /// a ghost of the platform at both ends. Any model works: put it under Platform with its top centre on
    /// Platform's origin. The nodes can be anywhere, so it can go sideways, up and down, or diagonally.
    ///
    /// Movement: Node A -> pause -> Node B -> pause -> Node A ..., at <see cref="speed"/> m/s on average,
    /// easing in and out at the ends if <see cref="easeInOut"/> is on. <see cref="phase"/> (0-1) shifts it
    /// along its cycle, so several platforms with the same settings don't move in lockstep.
    ///
    /// Everyone sees the same thing: the position is worked out from the network clock (<see cref="SharedClock"/>),
    /// not moved step by step, so nothing is networked and late joiners match too.
    ///
    /// Riding: CoreMovement carries a player standing on a platform along with it (<see cref="FrameDelta"/>).
    /// The platform moves first each frame (<see cref="MovingPlatformRunner"/>), before any player.
    /// Not carried: rotation (platforms don't turn), and a platform moving sideways into a player who isn't
    /// on it doesn't push them.
    /// </summary>
    [DisallowMultipleComponent]
    public class MovingPlatform : MonoBehaviour
    {
        [Tooltip("The part that moves (the Platform child). Its origin is the top centre of its model.")]
        [SerializeField] private Transform platform;

        [Tooltip("One end of the path: where Platform's origin (the top centre) goes.")]
        [SerializeField] private Transform nodeA;

        [Tooltip("The other end of the path.")]
        [SerializeField] private Transform nodeB;

        [Tooltip("Average speed between the nodes, in meters per second.")]
        [Min(0.1f)]
        [SerializeField] private float speed = 3f;

        [Tooltip("Seconds it waits at each end before heading back.")]
        [Min(0f)]
        [SerializeField] private float pauseSeconds = 1f;

        [Tooltip("Where in its cycle it starts (0-1): 0 = just arrived at Node A, 0.5 = just arrived at Node B (with equal pauses). Use different values so platforms don't all move together.")]
        [Range(0f, 1f)]
        [SerializeField] private float phase;

        [Tooltip("Slow down into each end and speed up out of it, instead of stopping dead.")]
        [SerializeField] private bool easeInOut = true;

        private static readonly List<MovingPlatform> s_Active = new List<MovingPlatform>();
        private bool m_Snap = true;

        /// <summary>Every enabled platform.</summary>
        public static IReadOnlyList<MovingPlatform> Active => s_Active;

        /// <summary>How far Platform moved this frame (world space). A player standing on it moves the same.</summary>
        public Vector3 FrameDelta { get; private set; }

        /// <summary>Platform's velocity this frame (world space, m/s).</summary>
        public Vector3 Velocity { get; private set; }

        public Transform PlatformTransform => platform;
        public Transform NodeA => nodeA;
        public Transform NodeB => nodeB;

        /// <summary>Seconds for one full cycle: A -> B -> A, both pauses included.</summary>
        public float CycleSeconds => 2f * (TravelSeconds + pauseSeconds);

        private float TravelSeconds => nodeA != null && nodeB != null
            ? Vector3.Distance(nodeA.position, nodeB.position) / Mathf.Max(0.1f, speed)
            : 0f;

        /// <summary>Sets everything up (the Moving Platform builder and challenge builders use this).</summary>
        public void Configure(Transform movingPart, Transform a, Transform b, float metersPerSecond, float pause, float cyclePhase, bool ease = true)
        {
            platform = movingPart;
            nodeA = a;
            nodeB = b;
            speed = Mathf.Max(0.1f, metersPerSecond);
            pauseSeconds = Mathf.Max(0f, pause);
            phase = Mathf.Repeat(cyclePhase, 1f);
            easeInOut = ease;
        }

        private void Awake()
        {
            // A moving collider wants a kinematic Rigidbody (otherwise PhysX treats it as static scenery being
            // teleported every frame).
            if (platform != null)
            {
                if (!platform.TryGetComponent(out Rigidbody body)) body = platform.gameObject.AddComponent<Rigidbody>();
                body.isKinematic = true;
                body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;
            }
        }

        private void OnEnable()
        {
            s_Active.Add(this);
            m_Snap = true;
            MovingPlatformRunner.Ensure();
        }

        private void OnDisable()
        {
            s_Active.Remove(this);
            FrameDelta = Vector3.zero;
            Velocity = Vector3.zero;
        }

        /// <summary>0 at Node A .. 1 at Node B, at time <paramref name="time"/> (seconds on the shared clock).</summary>
        public float ProgressAt(double time)
        {
            float travel = TravelSeconds;
            float cycle = 2f * (travel + pauseSeconds);
            if (cycle <= 0f) return 0f;

            double cycles = time / cycle + phase;
            float u = (float)((cycles - Math.Floor(cycles)) * cycle); // seconds into this cycle

            float s;
            if (u < pauseSeconds) s = 0f;                                             // waiting at A
            else if (u < pauseSeconds + travel) s = (u - pauseSeconds) / travel;      // A -> B
            else if (u < 2f * pauseSeconds + travel) s = 1f;                         // waiting at B
            else s = 1f - (u - 2f * pauseSeconds - travel) / Mathf.Max(0.0001f, travel); // B -> A

            s = Mathf.Clamp01(s);
            return easeInOut ? s * s * (3f - 2f * s) : s;
        }

        /// <summary>Moves Platform to where it is at <paramref name="time"/>.</summary>
        internal void Step(double time, float deltaTime)
        {
            if (platform == null || nodeA == null || nodeB == null)
            {
                FrameDelta = Vector3.zero;
                Velocity = Vector3.zero;
                return;
            }

            Vector3 before = platform.position;
            platform.position = Vector3.Lerp(nodeA.position, nodeB.position, ProgressAt(time));

            if (m_Snap)
            {
                m_Snap = false; // the first placement is a jump into position, not movement to carry riders with
                FrameDelta = Vector3.zero;
                Velocity = Vector3.zero;
                return;
            }
            FrameDelta = platform.position - before;
            Velocity = deltaTime > 0f ? FrameDelta / deltaTime : Vector3.zero;
        }

        internal static void StepAll(double time, float deltaTime)
        {
            if (s_Active.Count == 0) return;
            for (int i = 0; i < s_Active.Count; i++) s_Active[i].Step(time, deltaTime);
            Physics.SyncTransforms(); // so this frame's ground checks see the platforms where they now are
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (nodeA == null || nodeB == null) return;

            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.9f);
            Gizmos.DrawLine(nodeA.position, nodeB.position);
            Gizmos.DrawSphere(nodeA.position, 0.25f);
            Gizmos.DrawSphere(nodeB.position, 0.25f);

            // A ghost of the platform at each end.
            if (platform == null) return;
            Renderer[] renderers = platform.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            Vector3 offset = bounds.center - platform.position;

            Gizmos.color = new Color(1f, 0.8f, 0.2f, 0.35f);
            Gizmos.DrawWireCube(nodeA.position + offset, bounds.size);
            Gizmos.DrawWireCube(nodeB.position + offset, bounds.size);
        }

        private void OnDrawGizmosSelected()
        {
            if (nodeA == null || nodeB == null) return;
            UnityEditor.Handles.Label(nodeA.position + Vector3.up * 0.6f, "A");
            UnityEditor.Handles.Label(nodeB.position + Vector3.up * 0.6f, "B");
        }
#endif
    }

    /// <summary>
    /// Moves every <see cref="MovingPlatform"/> once a frame, before anything else runs (so players standing on
    /// them are carried by this frame's movement). Made on demand, hidden.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [AddComponentMenu("")]
    public class MovingPlatformRunner : MonoBehaviour
    {
        private static MovingPlatformRunner s_Instance;

        internal static void Ensure()
        {
            if (s_Instance != null || !Application.isPlaying) return;
            var go = new GameObject("[Moving Platforms]") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<MovingPlatformRunner>();
        }

        private void Update()
        {
            if (MovingPlatform.Active.Count == 0) return;
            MovingPlatform.StepAll(SharedClock.Now, Time.deltaTime);
        }
    }

    /// <summary>
    /// A clock every machine agrees on (to within the network delay): Fusion's simulation time once a session
    /// is running, smoothed so it advances evenly every frame; this machine's own time before that.
    /// </summary>
    public static class SharedClock
    {
        private static double s_Offset;
        private static bool s_Synced;
        private static int s_Frame = -1;

        /// <summary>Seconds on the shared clock.</summary>
        public static double Now
        {
            get
            {
                Refresh();
                return Time.timeAsDouble + s_Offset;
            }
        }

        private static void Refresh()
        {
            if (s_Frame == Time.frameCount) return;
            s_Frame = Time.frameCount;

            NetworkRunner runner = NetworkPlayers.Runner;
            if (runner == null || !runner.IsRunning) return; // keep whatever offset we had

            double target = runner.SimulationTime - Time.timeAsDouble;
            if (!s_Synced || Math.Abs(target - s_Offset) > 0.5)
            {
                s_Offset = target; // first sync, or a big jump (new session): snap
                s_Synced = true;
            }
            else
            {
                // SimulationTime moves in ticks; ease towards it so platforms glide instead of stepping.
                s_Offset += (target - s_Offset) * Math.Min(1.0, Time.unscaledDeltaTime * 2.0);
            }
        }
    }
}
