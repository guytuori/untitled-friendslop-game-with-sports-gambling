using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper that turns specific textured parts of an imported map's combined mesh
    /// into gameplay obstacles: any geometry using a material whose name contains "balance_beam" becomes
    /// one or more <see cref="BalanceBeam"/>s, any geometry using a material whose name contains
    /// "grind_rail" becomes one or more <see cref="GrindRail"/>s, any geometry using a material
    /// whose name contains "pole" becomes one or more <see cref="Pole"/>s (fireman's poles - see
    /// PoleGrabAbility), and any geometry using a material whose name contains "player_spawn_point"
    /// becomes one or more <see cref="PlayerSpawnPoint"/> markers (see GameManager, which picks
    /// randomly among them to place a player on spawn/respawn).
    ///
    /// How it works: the target submesh's triangles are grouped into connected components (two
    /// triangles are considered connected if they share an edge - matched by rounded local-space
    /// vertex position, not shared vertex index, since Total Editor 3's export doesn't necessarily weld
    /// vertices between touching pieces of geometry). Each resulting connected component - typically
    /// one physically contiguous run of the balance_beam/grind_rail geometry, e.g. a chain of touching
    /// "bar" shapes - becomes its own beam or rail. This mirrors how BalanceBeamCourseBuilder and
    /// GrindRailCourseBuilder build their hand-authored test courses (see those classes, and
    /// BalanceBeam/GrindRail for the underlying mechanics), except the beam/rail placement, length and
    /// orientation are derived directly from the existing map geometry instead of being hand-authored.
    ///
    /// For each component:
    /// - Balance beams: points are bucketed along the component's long axis into a waypoint chain, the
    ///   same technique used for grind rails just below - a straight beam collapses back to exactly two
    ///   waypoints (i.e. one segment, same as this tool always built), but a bent/dogleg run of
    ///   balance_beam geometry produces several. One CapsuleCollider (direction = Z, matching
    ///   BalanceBeam's expectations) plus a BalanceBeam component are built per consecutive waypoint
    ///   pair - no visual mesh, since the map's own render geometry already shows the balance_beam
    ///   strip - sized by a single cross-section radius measured against the whole waypoint chain
    ///   rather than a single straight line between the beam's two ends (see MaxDistanceToPolyline), so
    ///   a bend doesn't force every segment into a comically oversized tube just to cover how far the
    ///   bend strays from that line. Consecutive segments are linked via BalanceBeam.NextInChain/
    ///   PreviousInChain so BalanceBeamAbility can hand the player off across a joint without it reading
    ///   as falling off the beam. No flat collider is added or kept for this material (see
    ///   MapColliderSetup) - the round capsule(s) are the only thing holding the player up, same as the
    ///   hand-built test course; a flat collider underneath would just let the player casually walk
    ///   across it.
    /// - Grind rails: points are bucketed along the component's long axis (roughly one waypoint every
    ///   RailWaypointSpacing map units) and averaged per bucket into a waypoint chain, so a component
    ///   that curves or steps up/down (not just a straight run) still produces a reasonably faithful
    ///   path rather than collapsing to a single straight line between its two ends. A GrindRail
    ///   component is configured with that path and a single cross-section radius measured the same
    ///   tighter way described above for balance beams (distance to the whole waypoint chain, not to a
    ///   single straight line between the rail's two ends) - no collider at all, per GrindRail's own
    ///   design.
    /// - Poles: same long-axis/cross-section analysis as beams, but oriented so the CapsuleCollider's
    ///   axis (direction = Y, matching Pole's expectations) points along whatever direction the
    ///   component's long axis turned out to be - vertical for an upright pole, but tilted geometry
    ///   would still work. A CapsuleCollider plus a Pole component are added to a new child object - no
    ///   visual mesh, since the map's own render geometry already shows the pole. The collider stays
    ///   solid (see Pole's class summary for why that's compatible with PoleGrabAbility).
    /// - Player spawn points: no long-axis/waypoint analysis at all - each connected component just
    ///   becomes a single bare marker GameObject (a <see cref="PlayerSpawnPoint"/>, no collider, no
    ///   visual mesh) positioned at the component's centroid. A spawn tile doesn't need a shape or
    ///   orientation the way a beam/rail/pole does, just a point in space for GameManager to place a
    ///   player at. See MapColliderSetup for how the "player_spawn_point" tiles themselves are made
    ///   invisible and non-solid.
    ///
    /// Multiple physically separate balance_beam, grind_rail, pole or player_spawn_point regions on the
    /// map become multiple separate beams/rails/poles/spawn points - for grind rails in particular this
    /// is the intended shape of the mechanic, not a limitation: see GrindRailCourseBuilder's own
    /// two-separate-rails-with-a-jump-gap course design, where snapping onto a *different* rail than the
    /// one you left is called out as the intended way to play.
    ///
    /// Usage: select the map's GameObject instance in the Hierarchy (the same one MapColliderSetup
    /// targets) and run Friendslop > Build Balance Beams And Grind Rails And Poles From Map. Run
    /// MapColliderSetup either before or after this tool - it already knows to skip flat colliders for
    /// these materials, in either order. Make sure the player prefab has BalanceBeamAbility /
    /// GrindRailAbility / PoleGrabAbility on it too (Friendslop > Add Balance Beam To .../ Add Grind
    /// Rail To .../ Add Pole Grab To ...) or the new beams/rails/poles won't do anything.
    ///
    /// Safe to re-run: the previous run's "MapBalanceBeams"/"MapGrindRails"/"MapPoles"/
    /// "MapPlayerSpawnPoints" holder objects are destroyed and rebuilt from scratch each time.
    /// </summary>
    public static class MapObstacleSetup
    {
        private const string BalanceBeamKeyword = "balance_beam";
        private const string GrindRailKeyword = "grind_rail";
        private const string PoleKeyword = "pole";
        private const string PlayerSpawnPointKeyword = "player_spawn_point";
        private const string BeamHolderName = "MapBalanceBeams";
        private const string RailHolderName = "MapGrindRails";
        private const string PoleHolderName = "MapPoles";
        private const string SpawnPointHolderName = "MapPlayerSpawnPoints";
        private const string ColliderChildPrefix = "Collider_"; // matches MapColliderSetup's naming
        private const string UndoName = "Build Map Balance Beams And Grind Rails And Poles";

        private const float MinCrossSectionRadius = 0.1f;
        private const float RailWaypointSpacing = 2f;
        private const int MinTrianglesPerComponent = 2;

        [MenuItem("Friendslop/Build Balance Beams And Grind Rails And Poles From Map")]
        public static void Build()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
            {
                Debug.LogError("[Friendslop] Select the map's GameObject in the Hierarchy first (the same one MapColliderSetup targets).");
                return;
            }

            RunOnMapObject(selected);
        }

        /// <summary>
        /// The actual per-map-piece logic, callable directly against a known GameObject rather than only
        /// via the current Hierarchy selection - see SceneRefreshSetup (Friendslop > Refresh Everything
        /// From Assets), which finds every map piece in the scene structurally and calls this on each one
        /// without anything needing to be selected first.
        /// </summary>
        public static void RunOnMapObject(GameObject selected)
        {
            var meshFilter = selected.GetComponent<MeshFilter>();
            var meshRenderer = selected.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshFilter.sharedMesh == null || meshRenderer == null)
            {
                Debug.LogError($"[Friendslop] '{selected.name}' needs a MeshFilter (with a mesh assigned) and a MeshRenderer.");
                return;
            }

            Mesh mesh = meshFilter.sharedMesh;
            Material[] materials = meshRenderer.sharedMaterials;

            int beamSubmesh = FindSubmesh(materials, mesh.subMeshCount, BalanceBeamKeyword);
            int railSubmesh = FindSubmesh(materials, mesh.subMeshCount, GrindRailKeyword);
            int poleSubmesh = FindSubmesh(materials, mesh.subMeshCount, PoleKeyword);
            int spawnSubmesh = FindSubmesh(materials, mesh.subMeshCount, PlayerSpawnPointKeyword);

            if (beamSubmesh < 0 && railSubmesh < 0 && poleSubmesh < 0 && spawnSubmesh < 0)
            {
                Debug.LogError($"[Friendslop] Couldn't find a submesh material containing '{BalanceBeamKeyword}', '{GrindRailKeyword}', '{PoleKeyword}' or '{PlayerSpawnPointKeyword}' on '{selected.name}'.");
                return;
            }

            Undo.SetCurrentGroupName(UndoName);
            int undoGroup = Undo.GetCurrentGroup();

            RemoveExisting(selected.transform, BeamHolderName);
            RemoveExisting(selected.transform, RailHolderName);
            RemoveExisting(selected.transform, PoleHolderName);
            RemoveExisting(selected.transform, SpawnPointHolderName);
            RemoveFlatCollider(selected.transform, BalanceBeamKeyword);
            RemoveFlatCollider(selected.transform, GrindRailKeyword);
            RemoveFlatCollider(selected.transform, PoleKeyword);
            RemoveFlatCollider(selected.transform, PlayerSpawnPointKeyword);

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

            int polesBuilt = 0;
            if (poleSubmesh >= 0)
            {
                var poleHolder = new GameObject(PoleHolderName);
                Undo.RegisterCreatedObjectUndo(poleHolder, UndoName);
                poleHolder.transform.SetParent(selected.transform, false);

                Vector3[] verts = mesh.vertices;
                int[] subTris = mesh.GetTriangles(poleSubmesh);
                foreach (List<int> component in FindConnectedComponents(verts, subTris))
                {
                    if (component.Count < MinTrianglesPerComponent) continue;
                    BuildPole(poleHolder.transform, verts, subTris, component, polesBuilt);
                    polesBuilt++;
                }
            }

            int spawnPointsBuilt = 0;
            if (spawnSubmesh >= 0)
            {
                var spawnHolder = new GameObject(SpawnPointHolderName);
                Undo.RegisterCreatedObjectUndo(spawnHolder, UndoName);
                spawnHolder.transform.SetParent(selected.transform, false);

                Vector3[] verts = mesh.vertices;
                int[] subTris = mesh.GetTriangles(spawnSubmesh);
                foreach (List<int> component in FindConnectedComponents(verts, subTris))
                {
                    if (component.Count < MinTrianglesPerComponent) continue;
                    BuildSpawnPoint(spawnHolder.transform, verts, subTris, component, spawnPointsBuilt);
                    spawnPointsBuilt++;
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(selected);

            Debug.Log($"[Friendslop] Built {beamsBuilt} balance beam(s) from '{BalanceBeamKeyword}' geometry, {railsBuilt} grind rail(s) from '{GrindRailKeyword}' geometry, {polesBuilt} pole(s) from '{PoleKeyword}' geometry, and {spawnPointsBuilt} player spawn point(s) from '{PlayerSpawnPointKeyword}' geometry on '{selected.name}'. " +
                "Make sure the player prefab has BalanceBeamAbility / GrindRailAbility / PoleGrabAbility on it (Friendslop > Add Balance Beam To .../ Add Grind Rail To .../ Add Pole Grab To Platformer/Core Player). Remember to save the scene." +
                (spawnPointsBuilt > 0 ? " GameManager auto-discovers PlayerSpawnPoint markers at runtime, so no manual wiring is needed for these." : ""));
        }

        [MenuItem("Friendslop/Remove Map Balance Beams And Grind Rails And Poles")]
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
            RemoveExisting(selected.transform, PoleHolderName);
            RemoveExisting(selected.transform, SpawnPointHolderName);
            EditorUtility.SetDirty(selected);
            Debug.Log($"[Friendslop] Removed balance beams, grind rails, poles and player spawn points from '{selected.name}'. Re-run Friendslop > Split Selected Map Collider By Material afterward if you want ordinary flat collision back on that geometry.");
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
        /// Builds one balance beam - or, for a bent/dogleg run of balance_beam geometry, a chain of
        /// several straight CapsuleCollider segments end-to-end - from a connected component's
        /// triangles. Bucketing the component into a waypoint chain first (same technique BuildRail uses
        /// for its own bends) and building one capsule per consecutive waypoint pair means each segment
        /// only has to be as fat as the beam's own true cross-section (see MaxDistanceToPolyline)
        /// instead of a single straight capsule inflating its radius to cover however far a bend strays
        /// from the straight line between the component's two farthest points. A straight beam still
        /// collapses to exactly one segment, same as before - BucketAlongAxis's own fallback returns
        /// just the two extreme points when the beam is short enough that only one bucket fits.
        /// Positioned in `holder`'s local space, which has an identity local transform relative to the
        /// map object, so local-space math on the map mesh's own (untransformed) vertex positions lands
        /// in the right place without needing to convert to world space at all.
        /// </summary>
        private static void BuildBeam(Transform holder, Vector3[] verts, int[] subTris, List<int> triIndices, int index)
        {
            List<Vector3> points = CollectPoints(verts, subTris, triIndices);
            FindLongAxis(points, out Vector3 extremeA, out Vector3 extremeB, out float length);
            Vector3 axis = length > 0.0001f ? (extremeB - extremeA) / length : Vector3.forward;

            List<Vector3> waypoints = BucketAlongAxis(points, extremeA, axis, length);
            if (waypoints.Count < 2)
            {
                waypoints = new List<Vector3> { extremeA, extremeB };
            }

            float radius = Mathf.Max(MaxDistanceToPolyline(points, waypoints), MinCrossSectionRadius);

            BalanceBeam previousSegment = null;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                Vector3 a = waypoints[i];
                Vector3 b = waypoints[i + 1];
                float segmentLength = Vector3.Distance(a, b);
                if (segmentLength < 0.001f) continue;

                Vector3 segmentAxis = (b - a) / segmentLength;
                Vector3 segmentCenter = (a + b) * 0.5f;

                var root = new GameObject(waypoints.Count > 2 ? $"Beam_{index}_{i}" : $"Beam_{index}");
                Undo.RegisterCreatedObjectUndo(root, UndoName);
                root.transform.SetParent(holder, false);
                root.transform.localPosition = segmentCenter;
                root.transform.localRotation = LookRotationSafe(segmentAxis);

                var capsule = root.AddComponent<CapsuleCollider>();
                capsule.direction = 2; // Z - "along the beam", matching BalanceBeam's expectations.
                capsule.radius = radius;
                capsule.height = segmentLength;

                var segment = root.AddComponent<BalanceBeam>();

                // Segment i's local +Z end lands exactly at waypoint b, which is also segment i+1's
                // local -Z start (same shared point) - so linking them here is just "the segment built
                // right before this one, if any" with no extra geometry lookup needed.
                if (previousSegment != null)
                {
                    previousSegment.NextInChain = segment;
                    segment.PreviousInChain = previousSegment;
                }
                previousSegment = segment;
            }
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
            Vector3 axis = length > 0.0001f ? (extremeB - extremeA) / length : Vector3.forward;

            List<Vector3> waypointsLocal = BucketAlongAxis(points, extremeA, axis, length);
            if (waypointsLocal.Count < 2)
            {
                waypointsLocal = new List<Vector3> { extremeA, extremeB };
            }

            // Measured against the whole waypoint chain rather than the single straight line between
            // the rail's two ends (see MaxDistanceToPolyline) - a bent rail's interior points are close
            // to the path that actually follows the bend, so this doesn't inflate the rider's height
            // offset the way measuring straight-line cross-section would.
            float radius = Mathf.Max(MaxDistanceToPolyline(points, waypointsLocal), MinCrossSectionRadius);

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

        /// <summary>
        /// Builds one pole from a connected component's triangles: a CapsuleCollider (direction = Y,
        /// matching Pole's "local +Y = along pole" expectation) sized from the component's own long
        /// axis and cross-section, plus a Pole component. The root's rotation maps local +Y onto the
        /// axis found (FromToRotation), so a perfectly vertical pillar produces an upright pole and a
        /// tilted one is still oriented correctly - not just the vertical-only case this map's pillars
        /// happen to be. Positioned in `holder`'s local space, same as BuildBeam.
        /// </summary>
        private static void BuildPole(Transform holder, Vector3[] verts, int[] subTris, List<int> triIndices, int index)
        {
            List<Vector3> points = CollectPoints(verts, subTris, triIndices);
            FindLongAxis(points, out Vector3 extremeA, out Vector3 extremeB, out float length);
            Vector3 center = (extremeA + extremeB) * 0.5f;
            Vector3 axis = length > 0.0001f ? (extremeB - extremeA) / length : Vector3.up;
            float radius = Mathf.Max(MaxSidewaysDistance(points, center, axis), MinCrossSectionRadius);

            var root = new GameObject($"Pole_{index}");
            Undo.RegisterCreatedObjectUndo(root, UndoName);
            root.transform.SetParent(holder, false);
            root.transform.localPosition = center;
            root.transform.localRotation = Quaternion.FromToRotation(Vector3.up, axis);

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.direction = 1; // Y - "along the pole", matching Pole's expectations.
            capsule.radius = radius;
            capsule.height = length;

            root.AddComponent<Pole>();
        }

        /// <summary>
        /// Builds one player spawn point marker from a connected component's triangles: a single bare
        /// GameObject with a <see cref="PlayerSpawnPoint"/> component, positioned at the component's
        /// centroid - no collider, no visual mesh, no orientation analysis, since a spawn point only
        /// needs a location for GameManager to place a player at (see that class). Positioned in
        /// `holder`'s local space, same as BuildBeam/BuildPole - identity local transform relative to
        /// the map object, so local-space math on the map mesh's own vertices lands in the right place.
        /// Deliberately left at the tile's own (possibly slightly-above-floor) height rather than being
        /// projected down onto the ground, so a spawned player drops the remaining distance naturally.
        /// </summary>
        private static void BuildSpawnPoint(Transform holder, Vector3[] verts, int[] subTris, List<int> triIndices, int index)
        {
            List<Vector3> points = CollectPoints(verts, subTris, triIndices);

            Vector3 centroid = Vector3.zero;
            foreach (Vector3 p in points)
            {
                centroid += p;
            }
            centroid /= points.Count;

            var root = new GameObject($"SpawnPoint_{index}");
            Undo.RegisterCreatedObjectUndo(root, UndoName);
            root.transform.SetParent(holder, false);
            root.transform.localPosition = centroid;

            root.AddComponent<PlayerSpawnPoint>();
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
        /// For every point, finds the closest point on the given polyline (e.g. the waypoint chain from
        /// BucketAlongAxis) and returns the largest such perpendicular distance seen across all points -
        /// a much tighter "cross-section radius" for a bent beam/rail than MaxSidewaysDistance's single
        /// straight line between the component's two extreme ends, since a bend's own interior points
        /// sit close to the polyline that actually follows the bend, not far from some ruler-straight
        /// shortcut between its ends.
        /// </summary>
        private static float MaxDistanceToPolyline(List<Vector3> points, List<Vector3> polyline)
        {
            if (polyline.Count < 2) return 0f;

            float maxDistSqr = 0f;
            foreach (Vector3 p in points)
            {
                float closestSqr = float.MaxValue;
                for (int i = 0; i < polyline.Count - 1; i++)
                {
                    Vector3 a = polyline[i];
                    Vector3 b = polyline[i + 1];
                    Vector3 ab = b - a;
                    float lenSqr = ab.sqrMagnitude;
                    float t = lenSqr > 0.0001f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSqr) : 0f;
                    Vector3 closest = a + ab * t;
                    float distSqr = (p - closest).sqrMagnitude;
                    if (distSqr < closestSqr) closestSqr = distSqr;
                }
                if (closestSqr > maxDistSqr) maxDistSqr = closestSqr;
            }
            return Mathf.Sqrt(maxDistSqr);
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
