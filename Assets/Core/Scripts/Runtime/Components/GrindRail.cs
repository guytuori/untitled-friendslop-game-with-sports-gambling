using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A grindable rail: an ordered poly-line path (a chain of world-space waypoints) that
    /// <see cref="GrindRailAbility"/> can snap the player onto and slide them along, following
    /// whatever ups/downs and turns the waypoints describe. Unlike <see cref="BalanceBeam"/> - a
    /// single straight local +Z axis - a rail isn't straight, so every query here works in terms of
    /// "arc length" (distance travelled along the path from waypoints[0]) rather than a single local
    /// coordinate.
    ///
    /// Deliberately has no collider at all: rails are a magnetic-snap mechanic (like most
    /// grinding-genre games), not a physical surface - GrindRailAbility finds rails to snap onto by
    /// proximity to the path itself (see <see cref="ActiveRails"/>), whether the player is grounded,
    /// airborne, or mid-jump between two rails. The visual mesh (built by GrindRailCourseBuilder as a
    /// chain of capsule segments, one per waypoint pair) is purely cosmetic.
    ///
    /// Built and configured entirely at runtime by GrindRailCourseBuilder via <see cref="Configure"/>
    /// - there's no Inspector authoring workflow for this yet.
    /// </summary>
     [ExecuteInEditMode]
    public class GrindRail : MonoBehaviour
    {
        [Tooltip("World-space points defining the rail's path, in order. Needs at least 2.")]
        [SerializeField] private Vector3[] waypoints = System.Array.Empty<Vector3>();

        [Tooltip("Rail thickness - riders are offset this far above the path itself, matching the visual capsule segments' radius.")]
        [SerializeField] private float radius = 0.15f;

        [Tooltip("How far past either end (in arc-length meters) the player can still be considered 'on' the rail before it releases them - same idea as BalanceBeam's exitMargin.")]
        [SerializeField] private float exitMargin = 0.35f;
        [SerializeField] private bool isTube;

        private float[] m_CumulativeLength;
        private float[] m_TurnAngleAtWaypoint;
        private bool m_TablesBuilt;

        private static readonly List<GrindRail> s_ActiveRails = new List<GrindRail>();

        /// <summary>Every enabled GrindRail in the scene - what GrindRailAbility scans for a snap candidate.</summary>
        public static IReadOnlyList<GrindRail> ActiveRails => s_ActiveRails;

        public float Radius => radius;

        public float TotalLength
        {
            get
            {
                EnsureTables();
                return m_CumulativeLength.Length > 0 ? m_CumulativeLength[m_CumulativeLength.Length - 1] : 0f;
            }
        }

        public void GenerateWaypoints(Transform parent, float stepDistance = 1.0f, float maxUpwardAngle = 45.0f)
        {
            List<Vector3> generatedPoints = new List<Vector3>();

            if (parent == null)
            {
                return;
            }

            // Process each child transform
            foreach (Transform child in parent)
            {
                MeshFilter filter = child.GetComponent<MeshFilter>();
                Collider collider = child.GetComponent<Collider>();

                if (child.childCount > 0)
                {
                    if (filter == null)
                    {
                        filter = child.GetChild(0).GetComponent<MeshFilter>();
                    }
                    if (collider == null)
                    {
                        collider = child.GetChild(0).GetComponent<Collider>();
                    }
                }
                if (filter == null || collider == null)
                {
                    continue; // Skip children without a valid mesh
                }
                // Get local bounds dimensions
                Vector3 localMin = filter.sharedMesh.bounds.min;
                Vector3 localMax = filter.sharedMesh.bounds.max;

                // Calculate length and height accounting for lossy scale
                float meshForwardLength = Mathf.Abs(localMax.z - localMin.z) * child.lossyScale.z;
                float meshHeight = Mathf.Abs(localMax.y - localMin.y) * child.lossyScale.y;

                Vector3 forwardDir = child.forward;
                Vector3 upDir = child.up;

                // Max distance for the ray to cover the height of the mesh + padding
                float maxRayDistance = meshHeight + 5.0f;

                // Step along the child's local Z (forward) direction
                for (float dist = 0f; dist <= meshForwardLength; dist += stepDistance)
                {
                    // Point along the local bottom/center line of the mesh
                    Vector3 baseSamplePos = child.TransformPoint(new Vector3(0, localMin.y, localMin.z + (dist / child.lossyScale.z)));

                    // Position ray origin slightly above the local top of the child's mesh surface
                    Vector3 rayOrigin = baseSamplePos + (upDir * (meshHeight + 1.0f));

                    // Cast ray downward along the child's local down vector (-child.up)
                    Ray ray = new Ray(rayOrigin, -upDir);

                    if (collider != null)
                    {
                        // Precise hit testing using child collider
                        if (collider.Raycast(ray, out RaycastHit hit, maxRayDistance))
                        {
                            // Verify the hit surface normal aligns with the child's local up vector
                            if (Vector3.Angle(hit.normal, upDir) <= maxUpwardAngle)
                            {
                                generatedPoints.Add(hit.point - transform.position);
                            }
                        }
                    }
                    else
                    {
                        // Fallback: place the point on the child's local top surface
                        Vector3 fallbackPoint = baseSamplePos + (upDir * meshHeight);
                        generatedPoints.Add(fallbackPoint - transform.position);
                    }
                }
            }

            waypoints = generatedPoints.ToArray();
        }


        public void GenerateCirclePointsXZ(int pointCount = 16,float width = 25 )
        {
            Vector3 centerPosition = transform.position;
            List<Vector3> points = new List<Vector3>();
            float angleStep =  720f / pointCount;
            int count = 0;
            for (int i = 0; i < pointCount; i++)
            {
                float currentAngle = i * angleStep;
                float angleInRadians = currentAngle * Mathf.Deg2Rad;

                // Calculate local X and Z offsets
                float x = Mathf.Cos(angleInRadians) * radius;
                float z = Mathf.Sin(angleInRadians) * radius;

                Vector3 point = centerPosition + new Vector3(x * width,  z * width, count * 2);
                points.Add(point - transform.position);
                count++;
            }
            waypoints = points.ToArray();
        }

        private void OnEnable()
        {
            s_ActiveRails.Add(this);
            if (waypoints == null || waypoints.Length == 0)
            { 
             
                if (isTube)
                {
                    GenerateCirclePointsXZ();
                }
                else 
                {
                    GenerateWaypoints(transform, 1);
                }
            }
        }

        private void OnDisable()
        {
            s_ActiveRails.Remove(this);
        }

        /// <summary>Sets the rail's path and thickness. Safe to call any time - rebuilds the internal length/turn tables immediately.</summary>
        public void Configure(Vector3[] points, float railRadius)
        {
            waypoints = points ?? System.Array.Empty<Vector3>();
            radius = railRadius;
            m_TablesBuilt = false;
            EnsureTables();
        }

        private void EnsureTables()
        {
            if (m_TablesBuilt) return;
            BuildTables();
            m_TablesBuilt = true;
        }

        private void BuildTables()
        {
            int n = waypoints.Length;
            m_CumulativeLength = new float[Mathf.Max(n, 1)];
            m_TurnAngleAtWaypoint = new float[Mathf.Max(n, 1)];
            if (n == 0) return;

            m_CumulativeLength[0] = 0f;
            for (int i = 1; i < n; i++)
            {
                m_CumulativeLength[i] = m_CumulativeLength[i - 1] + Vector3.Distance(waypoints[i - 1], waypoints[i]);
            }

            // Signed angle (around world up) between the incoming and outgoing segment at each
            // interior waypoint - positive/negative distinguishes a left turn from a right turn, which
            // is what lets GrindRailAbility push the balance meter toward the *outside* of a turn
            // (centrifugal-feeling) rather than just toward a random side.
            for (int i = 1; i < n - 1; i++)
            {
                Vector3 inDir = (waypoints[i] - waypoints[i - 1]).normalized + transform.position;
                Vector3 outDir = (waypoints[i + 1] - waypoints[i]).normalized + transform.position;
                m_TurnAngleAtWaypoint[i] = Vector3.SignedAngle(inDir, outDir, Vector3.up);
            }
        }

        /// <summary>
        /// Projects a world position onto the whole path and returns the arc length of the closest
        /// point on it, plus whether that closest point actually falls within the rail's playable
        /// range (including <see cref="exitMargin"/> grace at either end) - a position far past one
        /// end still returns *a* closest point, just with withinRail false.
        /// </summary>
        public float ProjectToArcLength(Vector3 worldPosition, out bool withinRail)
        {
            EnsureTables();
            withinRail = false;
            if (waypoints.Length < 2) return 0f;

            float bestDistSqr = float.MaxValue;
            float bestArc = 0f;

            for (int i = 0; i < waypoints.Length - 1; i++)
            {
                Vector3 a = waypoints[i] + transform.position;
                Vector3 b = waypoints[i + 1] + transform.position;
                Vector3 ab = b - a;
                float lenSqr = ab.sqrMagnitude;
                float t = lenSqr > 0.0001f ? Mathf.Clamp01(Vector3.Dot(worldPosition - a, ab) / lenSqr) : 0f;
                Vector3 closest = a + ab * t;
                float distSqr = (worldPosition - closest).sqrMagnitude;
                if (distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    bestArc = m_CumulativeLength[i] + ab.magnitude * t;
                }
            }

            withinRail = IsArcWithinRail(bestArc);
            return Mathf.Clamp(bestArc, 0f, TotalLength);
        }

        /// <summary>True if the given (unclamped) arc length is within the rail's playable range, including the end-grace margin.</summary>
        public bool IsArcWithinRail(float arcLength) => arcLength >= -exitMargin && arcLength <= TotalLength + exitMargin;

        /// <summary>
        /// Evaluates the rail at a given arc length: the raw path point (the rider's actual position
        /// is this plus <see cref="Radius"/> straight up, applied by the caller so this class doesn't
        /// need to know which axis "up" means for balance offsetting), the forward tangent (pointing
        /// in the direction of increasing arc length), the horizontal right vector (perpendicular to
        /// the tangent, same handedness as Transform.right), and the turn sharpness at that point
        /// (signed - positive/negative distinguishes turn direction, magnitude is degrees of bend,
        /// smoothly blended between the waypoints on either side of the current segment).
        /// </summary>
        public void Evaluate(float arcLength, out Vector3 point, out Vector3 tangent, out Vector3 right, out float turnSharpness)
        {
            EnsureTables();
            arcLength = Mathf.Clamp(arcLength, 0f, TotalLength);

            if (waypoints.Length < 2)
            {
                point = transform.position;
                tangent = transform.forward;
                right = transform.right;
                turnSharpness = 0f;
                return;
            }

            int segment = FindSegment(arcLength);
            Vector3 a = waypoints[segment] + transform.position;
            Vector3 b = waypoints[segment + 1] + transform.position;
            float segLen = m_CumulativeLength[segment + 1] - m_CumulativeLength[segment];
            float t = segLen > 0.0001f ? (arcLength - m_CumulativeLength[segment]) / segLen : 0f;

            point = Vector3.Lerp(a, b, t);
            tangent = (b - a).normalized;
            if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.forward;
            right = Vector3.Cross(Vector3.up, tangent).normalized;

            float turnAtStart = m_TurnAngleAtWaypoint[segment];
            float turnAtEnd = segment + 1 < m_TurnAngleAtWaypoint.Length ? m_TurnAngleAtWaypoint[segment + 1] : turnAtStart;
            turnSharpness = Mathf.Lerp(turnAtStart, turnAtEnd, t);
        }

        private int FindSegment(float arcLength)
        {
            for (int i = 0; i < m_CumulativeLength.Length - 1; i++)
            {
                if (arcLength <= m_CumulativeLength[i + 1] || i == m_CumulativeLength.Length - 2)
                {
                    return i;
                }
            }

            return 0;
        }

        public int FindClosestWaypoint(Vector3 _position)
        {
            if (waypoints == null || waypoints.Length == 0)
            { return -1; }
            int count = 0;

            while (count < waypoints.Length)
            {

                count++;
            }

            return 0;
        }

        private void OnDrawGizmosSelected()
        {
            if (waypoints == null || waypoints.Length < 2) return;

            Gizmos.color = Color.cyan;
            for (int i = 0; i < waypoints.Length - 1; i++)
            {
                Gizmos.DrawLine(waypoints[i] + transform.position + Vector3.up * radius, waypoints[i + 1] + transform.position + Vector3.up * radius);
                Gizmos.DrawWireSphere(waypoints[i] + transform.position + Vector3.up * radius, radius);
            }
            Gizmos.DrawWireSphere(waypoints[waypoints.Length - 1] + transform.position + Vector3.up * radius, radius);
        }
    }
}
