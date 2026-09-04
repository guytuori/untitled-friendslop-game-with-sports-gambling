using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper that turns specific textured parts of an imported map's combined mesh
    /// into gameplay obstacles: any geometry using a material whose name contains "checker" becomes
    /// one or more <see cref="BalanceBeam"/>s, and any geometry using a material whose name contains
    /// "bluecarpet" becomes one or more <see cref="GrindRail"/>s.
    ///
    /// How it works: the target submesh's triangles are grouped into connected components (two
    /// triangles are considered connected if they share an edge - matched by rounded local-space
    /// vertex position, not shared vertex index, since Total Editor 3's export doesn't necessarily weld
    /// vertices between touching pieces of geometry). Each resulting connected component - typically
    /// one physically contiguous run of the checkered/carpeted geometry, e.g. a chain of touching "bar"
    /// shapes - becomes its own beam or rail. This mirrors how BalanceBeamCourseBuilder and
    /// GrindRailCourseBuilder build their hand-authored test courses (see those classes, and
    /// BalanceBeam/GrindRail for the underlying mechanics), except the beam/rail placement, length and
    /// orientation are derived directly from the existing map geometry instead of being hand-authored.
    ///
    /// For each component:
    /// - Balance beams: the two most-distant points define the beam's length and long axis; the
    ///   farthest any point strays sideways from that axis defines its cross-section radius. A
    ///   CapsuleCollider (direction = Z, matching BalanceBeam's expectations) plus a BalanceBeam
    ///   component are added to a new child object positioned/oriented from that data - no visual mesh,
    ///   since the map's own render geometry already shows the checkered strip. No flat collider is
    ///   added or kept for this material (see MapColliderSetup) - the round capsule is the only thing
    ///   holding the player up, same as the hand-built test course; a flat collider underneath would
    ///   just let the player casually walk across it.
    /// - Grind rails: points are bucketed along the component's long axis (roughly one waypoint every
    ///   RailWaypointSpacing map units) and averaged per bucket into a waypoint chain, so a component
    ///   that curves or steps up/down (not just a straight run) still produces a reasonably faithful
    ///   path rather than collapsing to a single straight line between its two ends. A GrindRail
    ///   component is configured with that path - no collider at all, per GrindRail's own design.
    ///
    /// Multiple physically separate checker or bluecarpet regions on the map become multiple separate
    /// beams/rails - for grind rails in particular this is the intended shape of the mechanic, not a
    /// limitation: see GrindRailCourseBuilder's own two-separate-rails-with-a-jump-gap course design,
    /// where snapping onto a *different* rail than the one you left is called out as the intended way
    /// to play.
    ///
    /// Usage: select the map's GameObject instance in the Hierarchy (the same one MapColliderSetup
    /// targets) and run Friendslop > Build Balance Beams And Grind Rails From Map. Run
    /// MapColliderSetup either before or after this tool - it already knows to skip flat colliders for
    /// these two materials, in either order. Make sure the player prefab has BalanceBeamAbility /
    /// GrindRailAbility on it too (Friendslop > Add Balance Beam To .../ Add Grind Rail To ...) or the
    /// new beams/rails won't do anything.
    ///
    /// Safe to re-run: the previous run's "MapBalanceBeams"/"MapGrindRails" holder objects are
    /// destroyed and rebuilt from scratch each time.
    /// </summary>
    public static class MapObstacleSetup
    {
        private const string CheckerKeyword = "checker";
        private const string RailKeyword = "bluecarpet";
        private const string BeamHolderName = "MapBalanceBeams";
        private const string RailHolderName = "MapGrindRails";
        private const string ColliderChildPrefix = "Collider_"; // matches MapColliderSetup's naming
        private const string UndoName = "Build Map Balance Beams And Grind Rails";

        private const float MinCrossSectionRadius = 0.1f;
        private const float RailWaypointSpacing = 2f;
        private const int MinTrianglesPerComponent = 2;

        [MenuItem("Friendslop/Build Balance Beams And Grind Rails From Map")]
        public static void Build()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogError("[Friendslop] Select the map's GameObject in the Hierarchy first (the same one MapColliderSetup targets).");
                return;
            }

            var meshFilter = selected.GetComponent<MeshFilter>();
            var meshRenderer = selected.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshFilter.sharedMesh == null || meshRenderer == null)
            {
                Debug.LogError($"[Friendslop] '{selected.name}' needs a MeshFilter (with a mesh assigned) and a MeshRenderer.");
                return;
            }

            Mesh mesh = meshFilter.sharedMesh;
            Material[] materials = meshRenderer.sharedMaterials;

            int beamSubmesh = FindSubmesh(materials, mesh.subMeshCount, CheckerKeyword);
            int railSubmesh = FindSubmesh(materials, mesh.subMeshCount, RailKeyword);

            if (beamSubmesh < 0 && railSubmesh < 0)
            {
                Debug.LogError($"[Friendslop] Couldn't find a submesh material containing '{CheckerKeyword}' or '{RailKeyword}' on '{selected.name}'.");
                return;
            }

            Undo.SetCurrentGroupName(UndoName);
            int undoGroup = Undo.GetCurrentGroup();

            RemoveExisting(selected.transform, BeamHolderName);
            RemoveExisting(selected.transform, RailHolderName);
            RemoveFlatCollider(selected.transform, CheckerKeyword);
            RemoveFlatCollider(selected.transform, RailKeyword);

            int beamsBuilt = 0;
            if (beamSubmesh >= 0)
            {
                var beamHolder = new GameObject(BeamHolderName);
                Undo.RegisterCreatedObjectUndo(beamHolder, UndoName);
                beamHolder.transform.SetParent(selected.transform, false);

                Vector3[] verts = mesh.vertices;
                int[] subTris = mesh.GetTriangles(beamSubmesh);
                foreach (List<int> component in FindConnectedComponents(verts, subTris))
                {
                    if (component.Count < MinTrianglesPerComponent) continue;
                    BuildBeam(beamHolder.transform, verts, subTris, component, beamsBuilt);
                    beamsBuilt++;
                }
            }

            int railsBuilt = 0;
            if (railSubmesh >= 0)
            {
                var railHolder = new GameObject(RailHolderName);
                Undo.RegisterCreatedObjectUndo(railHolder, UndoName);
                railHolder.transform.SetParent(selected.transform, false);

                Vector3[] verts = mesh.vertices;
                int[] subTris = mesh.GetTriangles(railSubmesh);
                foreach (List<int> component in FindConnectedComponents(verts, subTris))
                {
                    if (component.Count < MinTrianglesPerComponent) continue;
                    BuildRail(railHolder.transform, selected.transform, verts, subTris, component, railsBuilt);
                    railsBuilt++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(selected);

            Debug.Log($"[Friendslop] Built {beamsBuilt} balance beam(s) from '{CheckerKeyword}' geometry and {railsBuilt} grind rail(s) from '{RailKeyword}' geometry on '{selected.name}'. " +
                "Make sure the player prefab has BalanceBeamAbility / GrindRailAbility on it (Friendslop > Add Balance Beam To Platformer/Core Player, Friendslop > Add Grind Rail To Platformer/Core Player). Remember to save the scene.");
        }

        [MenuItem("Friendslop/Remove Map Balance Beams And Grind Rails")]
        public static void Remove()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogError("[Friendslop] Select the map's GameObject in the Hierarchy first.");
                return;
            }

            RemoveExisting(selected.transform, BeamHolderName);
            RemoveExisting(selected.transform, RailHolderName);
            EditorUtility.SetDirty(selected);
            Debug.Log($"[Friendslop] Removed balance beams and grind rails from '{selected.name}'. Re-run Friendslop > Split Selected Map Collider By Material afterward if you want ordinary flat collision back on that geometry.");
        }

        private static int FindSubmesh(Material[] materials, int subMeshCount, string keyword)
        {
            int count = Mathf.Min(materials.Length, subMeshCount);
            for (int i = 0; i < count; i++)
            {
                if (materials[i] != null && materials[i].name.ToLowerInvariant().Contains(keyword))
                {
                    return i;
                }
            }
            return -1;
        }

        private static void RemoveExisting(Transform parent, string childName)
        {
            Transform existing = parent.Find(childName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing.gameObject);
            }
        }

        /// <summary>Removes any "Collider_*" child (from MapColliderSetup) whose name matches the given material keyword, so a beam/rail doesn't end up with a flat collider underneath it too.</summary>
        private static void RemoveFlatCollider(Transform parent, string keyword)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                if (child.name.StartsWith(ColliderChildPrefix) && child.name.ToLowerInvariant().Contains(keyword))
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                }
            }
        }

        /// <summary>
        /// Groups a submesh's triangles into connected components, where two triangles are connected
        /// if they share an edge - matched by rounded local-space vertex position (not shared vertex
        /// index), since adjacent pieces of exported geometry aren't necessarily vertex-welded. Returns
        /// each component as a list of triangle indices local to `subTris` (i.e. 0..subTris.Length/3-1).
        /// </summary>
        private static List<List<int>> FindConnectedComponents(Vector3[] verts, int[] subTris)
        {
            int triCount = subTris.Length / 3;
            var parent = new int[triCount];
            for (int i = 0; i < triCount; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }

            // One entry per edge (as a pair of rounded-position endpoints) - the first triangle seen
            // owning that edge is recorded, and the second triangle to claim the same edge gets unioned
            // with it. Good enough for the manifold-ish, mostly-two-triangles-per-edge geometry these
            // maps produce; a rare edge shared by more than two triangles just risks under-merging
            // (an extra beam/rail instead of one) rather than anything worse.
            var edgeOwner = new Dictionary<(PosKey, PosKey), int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = subTris[t * 3], b = subTris[t * 3 + 1], c = subTris[t * 3 + 2];
                var pa = new PosKey(verts[a]);
                var pb = new PosKey(verts[b]);
                var pc = new PosKey(verts[c]);

                ClaimEdge(edgeOwner, pa, pb, t, Union);
                ClaimEdge(edgeOwner, pb, pc, t, Union);
                ClaimEdge(edgeOwner, pa, pc, t, Union);
            }

            var components = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(t);
                if (!components.TryGetValue(root, out List<int> list))
                {
                    list = new List<int>();
                    components[root] = list;
                }
                list.Add(t);
            }

            return new List<List<int>>(components.Values);
        }

        private static void ClaimEdge(Dictionary<(PosKey, PosKey), int> edgeOwner, PosKey a, PosKey b, int triIndex, System.Action<int, int> union)
        {
            var key = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            if (edgeOwner.TryGetValue(key, out int otherTri))
            {
                union(triIndex, otherTri);
            }
            else
            {
                edgeOwner[key] = triIndex;
            }
        }

        /// <summary>
        /// Builds one balance beam from a connected component's triangles: a CapsuleCollider (direction
        /// = Z, matching BalanceBeam's "local +Z = along beam" expectation) sized and oriented from the
        /// component's own geometry, plus a BalanceBeam component. Positioned in `holder`'s local space,
        /// which has an identity local transform relative to the map object, so local-space math on the
        /// map mesh's own (untransformed) vertex positions lands in the right place without needing to
        /// convert to world space at all.
        /// </summary>
        private static void BuildBeam(Transform holder, Vector3[] verts, int[] subTris, List<int> triIndices, int index)
        {
            List<Vector3> points = CollectPoints(verts, subTris, triIndices);
            FindLongAxis(points, out Vector3 extremeA, out Vector3 extremeB, out float length);
            Vector3 center = (extremeA + extremeB) * 0.5f;
            Vector3 axis = length > 0.0001f ? (extremeB - extremeA) / length : Vector3.forward;
            float radius = Mathf.Max(MaxSidewaysDistance(points, center, axis), MinCrossSectionRadius);

            var root = new GameObject($"Beam_{index}");
            Undo.RegisterCreatedObjectUndo(root, UndoName);
            root.transform.SetParent(holder, false);
            root.transform.localPosition = center;
            root.transform.localRotation = LookRotationSafe(axis);

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.direction = 2; // Z - "along the beam", matching BalanceBeam's expectations.
            capsule.radius = radius;
            capsule.height = length;

            root.AddComponent<BalanceBeam>();
        }

        /// <summary>
        /// Builds one grind rail from a connected component's triangles: points are bucketed along the
        /// component's long axis into a waypoint chain (see class summary), converted to world space
        /// (GrindRail.Configure expects world-space waypoints - see that class), and used to configure
        /// a collider-less GrindRail component.
        /// </summary>
        private static void BuildRail(Transform holder, Transform mapTransform, Vector3[] verts, int[] subTris, List<int> triIndices, int index)
        {
            List<Vector3> points = CollectPoints(verts, subTris, triIndices);
            FindLongAxis(points, out Vector3 extremeA, out Vector3 extremeB, out float length);
            Vector3 center = (extremeA + extremeB) * 0.5f;
            Vector3 axis = length > 0.0001f ? (extremeB - extremeA) / length : Vector3.forward;
            float radius = Mathf.Max(MaxSidewaysDistance(points, center, axis), MinCrossSectionRadius);

            List<Vector3> waypointsLocal = BucketAlongAxis(points, extremeA, axis, length);
            if (waypointsLocal.Count < 2)
            {
                waypointsLocal = new List<Vector3> { extremeA, extremeB };
            }

            var waypointsWorld = new Vector3[waypointsLocal.Count];
            for (int i = 0; i < waypointsLocal.Count; i++)
            {
                waypointsWorld[i] = mapTransform.TransformPoint(waypointsLocal[i]);
            }

            var root = new GameObject($"Rail_{index}");
            Undo.RegisterCreatedObjectUndo(root, UndoName);
            root.transform.SetParent(holder, false);
            root.transform.position = waypointsWorld[0];

            GrindRail rail = root.AddComponent<GrindRail>();
            rail.Configure(waypointsWorld, radius);
        }

        private static List<Vector3> CollectPoints(Vector3[] verts, int[] subTris, List<int> triIndices)
        {
            var points = new List<Vector3>(triIndices.Count * 3);
            foreach (int t in triIndices)
            {
                points.Add(verts[subTris[t * 3]]);
                points.Add(verts[subTris[t * 3 + 1]]);
                points.Add(verts[subTris[t * 3 + 2]]);
            }
            return points;
        }

        /// <summary>Finds the two most-distant points in the set - defines a component's long axis and length. O(n^2), fine for the few hundred points a map's beam/rail geometry produces.</summary>
        private static void FindLongAxis(List<Vector3> points, out Vector3 extremeA, out Vector3 extremeB, out float length)
        {
            extremeA = points[0];
            extremeB = points[0];
            float bestDistSqr = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                for (int j = i + 1; j < points.Count; j++)
                {
                    float d = (points[i] - points[j]).sqrMagnitude;
                    if (d > bestDistSqr)
                    {
                        bestDistSqr = d;
                        extremeA = points[i];
                        extremeB = points[j];
                    }
                }
            }
            length = Mathf.Sqrt(bestDistSqr);
        }

        /// <summary>How far any point strays perpendicular to the (center, axis) line - the component's effective cross-section radius.</summary>
        private static float MaxSidewaysDistance(List<Vector3> points, Vector3 center, Vector3 axis)
        {
            float maxSide = 0f;
            foreach (Vector3 p in points)
            {
                Vector3 toPoint = p - center;
                Vector3 along = Vector3.Project(toPoint, axis);
                float side = (toPoint - along).magnitude;
                if (side > maxSide) maxSide = side;
            }
            return maxSide;
        }

        /// <summary>
        /// Buckets points along the axis (roughly one bucket every RailWaypointSpacing units) and
        /// averages each non-empty bucket into a waypoint, in order from `origin` outward - so a
        /// component that curves or steps up/down produces a multi-point path rather than collapsing
        /// to a single straight segment between its two ends.
        /// </summary>
        private static List<Vector3> BucketAlongAxis(List<Vector3> points, Vector3 origin, Vector3 axis, float length)
        {
            int bucketCount = Mathf.Max(2, Mathf.RoundToInt(length / RailWaypointSpacing) + 1);
            var sums = new Vector3[bucketCount];
            var counts = new int[bucketCount];

            foreach (Vector3 p in points)
            {
                float t = length > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - origin, axis) / length) : 0f;
                int bucket = Mathf.Clamp(Mathf.RoundToInt(t * (bucketCount - 1)), 0, bucketCount - 1);
                sums[bucket] += p;
                counts[bucket]++;
            }

            var waypoints = new List<Vector3>(bucketCount);
            for (int i = 0; i < bucketCount; i++)
            {
                if (counts[i] > 0)
                {
                    waypoints.Add(sums[i] / counts[i]);
                }
            }
            return waypoints;
        }

        /// <summary>Quaternion.LookRotation with a safe up-hint fallback for the (here, unlikely but cheap to guard against) case of a near-vertical beam axis.</summary>
        private static Quaternion LookRotationSafe(Vector3 forward)
        {
            Vector3 upHint = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;
            return Quaternion.LookRotation(forward, upHint);
        }

        /// <summary>Vertex position rounded to the nearest centimeter, used as a dictionary key so two triangles from unwelded-but-touching geometry are still recognized as sharing an edge.</summary>
        private readonly struct PosKey : System.IEquatable<PosKey>, System.IComparable<PosKey>
        {
            private readonly int x, y, z;

            public PosKey(Vector3 v)
            {
                x = Mathf.RoundToInt(v.x * 100f);
                y = Mathf.RoundToInt(v.y * 100f);
                z = Mathf.RoundToInt(v.z * 100f);
            }

            public bool Equals(PosKey other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is PosKey other && Equals(other);
            public override int GetHashCode() => (x * 73856093) ^ (y * 19349663) ^ (z * 83492791);

            public int CompareTo(PosKey other)
            {
                if (x != other.x) return x.CompareTo(other.x);
                if (y != other.y) return y.CompareTo(other.y);
                return z.CompareTo(other.z);
            }
        }
    }
}
