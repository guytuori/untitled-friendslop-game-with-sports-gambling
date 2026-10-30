using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A deadly roller (added 2026-10-30): it moves back and forth along a <see cref="MovingPlatform"/> path (Node A,
    /// optional waypoints, Node B - same speed / pause / phase settings, same network clock, so everyone sees it in
    /// the same place), spins as it rolls, and kills the player who touches it.
    ///
    /// Layout (Assets/Core/Prefabs/Roller.prefab, Friendslop > Challenges > Add Roller):
    ///   Roller           MovingPlatform + this
    ///     Node A / Node B (and any waypoints)   where the roller's CENTRE goes
    ///     Platform       the moving part; its origin is the centre of the model (roller.glb at x2 scale - a
    ///                    4 m long, 4 m wide cylinder). Solid (<see cref="solid"/>): a convex MeshCollider is
    ///                    added to the model when it starts, so you can't run into it (changed 2026-10-30 - the
    ///                    user found they clipped into it a little before dying).
    ///
    ///   - The cylinder's axis is taken to be level and square to the direction from Node A to Node B (it rolls
    ///     along the path). Its radius and length are measured from the model when the roller starts, so any
    ///     cylinder model and scale work.
    ///   - Spinning: the angle is the distance travelled along the path over the radius, so it never slides.
    ///   - Touching: each machine checks only its own player - the player's body (a 0.28 m radius capsule,
    ///     1.8 m tall) against the cylinder, counting anything within <see cref="touchMargin"/> of its surface
    ///     (so bumping into the solid roller counts) - and kills them like falling off the map
    ///     (LocalRespawnTracker.KillNow).
    /// </summary>
    [RequireComponent(typeof(MovingPlatform))]
    [DisallowMultipleComponent]
    public class Roller : MonoBehaviour
    {
        private const float PlayerRadius = 0.28f;
        private const float PlayerHeight = 1.8f;

        [Tooltip("Kills the player who touches it.")]
        [SerializeField] private bool deadly = true;

        [Tooltip("Spins as it moves, like it's really rolling.")]
        [SerializeField] private bool spin = true;

        [Tooltip("Solid: you can't run into it or through it (a convex collider is added to the model when it starts).")]
        [SerializeField] private bool solid = true;

        [Tooltip("How close to its surface counts as touching it (meters). Positive = bumping into the solid roller counts; negative = a little leeway.")]
        [SerializeField] private float touchMargin = 0.15f;

        private MovingPlatform m_Mover;
        private Transform m_Part;
        private Quaternion m_RestRotation;
        private Vector3 m_Axis = Vector3.forward;   // world, along the cylinder
        private float m_Radius = 1f;
        private float m_HalfLength = 1f;
        private Vector3 m_CentreOffset;             // model centre - Platform origin, at rest (world)
        private bool m_Measured;

        public float Radius => m_Radius;
        public float HalfLength => m_HalfLength;

        private void Awake()
        {
            m_Mover = GetComponent<MovingPlatform>();
        }

        private void Start()
        {
            Measure();
            if (solid) AddColliders();
        }

        /// <summary>A convex MeshCollider on each piece of the model that has none (it moves with Platform's kinematic Rigidbody).</summary>
        private void AddColliders()
        {
            if (m_Part == null) return;
            foreach (MeshFilter filter in m_Part.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;
                var collider = filter.gameObject.AddComponent<MeshCollider>();
                collider.sharedMesh = filter.sharedMesh;
                collider.convex = true;
            }
        }

        /// <summary>The axis (level, square to A -> B) and the cylinder's radius / half length, from its model at rest.</summary>
        private void Measure()
        {
            m_Part = m_Mover != null ? m_Mover.PlatformTransform : null;
            if (m_Part == null || m_Mover.NodeA == null || m_Mover.NodeB == null) return;

            m_RestRotation = m_Part.rotation;
            Vector3 along = m_Mover.NodeB.position - m_Mover.NodeA.position;
            along.y = 0f;
            Vector3 axis = Vector3.Cross(Vector3.up, along);
            m_Axis = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward;

            Renderer[] renderers = m_Part.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            Vector3 e = bounds.extents;
            m_HalfLength = Mathf.Abs(m_Axis.x) * e.x + Mathf.Abs(m_Axis.y) * e.y + Mathf.Abs(m_Axis.z) * e.z;
            m_Radius = e.y;
            m_CentreOffset = bounds.center - m_Part.position;
            m_Measured = true;
        }

        private void LateUpdate()
        {
            if (!m_Measured || m_Part == null) return;

            if (spin)
            {
                float degrees = m_Mover.DistanceAlongPath / Mathf.Max(0.01f, m_Radius) * Mathf.Rad2Deg;
                m_Part.rotation = Quaternion.AngleAxis(degrees, m_Axis) * m_RestRotation;
            }

            if (deadly) CheckLocalPlayer();
        }

        private void CheckLocalPlayer()
        {
            CorePlayerManager player = NetworkPlayers.Local;
            LocalRespawnTracker tracker = LocalRespawnTracker.Local;
            if (player == null || tracker == null) return;
            if (player.PlayerState != null && player.PlayerState.LifeState == PlayerLifeState.Eliminated) return;

            if (Touches(player.transform.position)) tracker.KillNow();
        }

        /// <summary>Whether a player standing at <paramref name="feet"/> touches the cylinder.</summary>
        public bool Touches(Vector3 feet)
        {
            Vector3 centre = m_Part.position + m_CentreOffset;
            Vector3 bottom = feet + Vector3.up * PlayerRadius;
            Vector3 top = feet + Vector3.up * (PlayerHeight - PlayerRadius);

            // The point of the player's body line nearest the cylinder's axis line.
            Vector3 q = ClosestOnSegmentToLine(bottom, top, centre, m_Axis);
            Vector3 d = q - centre;
            float along = Vector3.Dot(d, m_Axis);
            float reach = PlayerRadius + touchMargin;
            if (Mathf.Abs(along) > m_HalfLength + reach) return false; // past an end
            float across = (d - along * m_Axis).magnitude;
            return across < m_Radius + reach;
        }

        private static Vector3 ClosestOnSegmentToLine(Vector3 a, Vector3 b, Vector3 linePoint, Vector3 lineDir)
        {
            Vector3 u = b - a;
            Vector3 w = a - linePoint;
            float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, lineDir), vv = Vector3.Dot(lineDir, lineDir);
            float uw = Vector3.Dot(u, w), vw = Vector3.Dot(lineDir, w);
            float denominator = uu * vv - uv * uv;
            float s = denominator > 1e-6f ? (uv * vw - vv * uw) / denominator : 0f;
            return a + u * Mathf.Clamp01(s);
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!m_Measured || m_Part == null) return;
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.8f);
            Vector3 centre = m_Part.position + m_CentreOffset;
            Gizmos.DrawLine(centre - m_Axis * m_HalfLength, centre + m_Axis * m_HalfLength);
        }
#endif
    }
}
