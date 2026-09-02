using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Blocks.Gameplay.Core;

namespace Friendslop.EditorTools
{
    /// <summary>
    /// Editor utility that builds a grind-rail test area into the currently open scene: a flat start,
    /// then two separate rails with ups/downs and turns, connected by a jumpable gap (to exercise
    /// snapping onto a rail mid-air, including a *different* rail than the one you left - unlike
    /// balance beams, this is the intended way to play), ending with the second rail's tail extended
    /// out over the landing platform's footprint the same way BalanceBeamCourseBuilder's beam does.
    ///
    /// Each rail is a <see cref="GrindRail"/> (a poly-line of world-space waypoints - see that class)
    /// with a purely cosmetic chain of rounded capsule segments for a visual, one per waypoint pair.
    /// Rails have no collider at all - grinding is a proximity-snap mechanic, not a physical one, so
    /// nothing needs to physically support the player standing on it.
    ///
    /// Built parallel to (offset from) the other Friendslop test courses so re-running any of them
    /// doesn't disturb the others. Run from the Unity menu: Friendslop > Build Grind Rail Test Area.
    /// Re-running replaces the previous version. Remove it with Friendslop > Remove Grind Rail Test
    /// Area.
    ///
    /// Make sure the player prefab actually has GrindRailAbility on it first (Friendslop > Add Grind
    /// Rail To Platformer Player / To Core Player).
    /// </summary>
    public static class GrindRailCourseBuilder
    {
        private const string RootName = "GrindRailCourse";
        private const string UndoName = "Build Grind Rail Test Area";

        // East of spawn, well clear of WallJumpCourseBuilder's +14 and BalanceBeamCourseBuilder's -14.
        private const float LateralOffsetFromSpawn = 30f;

        [MenuItem("Friendslop/Build Grind Rail Test Area")]
        public static void Build()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }

            Vector3 spawn = FindOrigin();
            Vector3 origin = spawn + new Vector3(LateralOffsetFromSpawn, 0f, 0f);
            float baseX = origin.x;
            float groundHeight = origin.y;

            Undo.SetCurrentGroupName(UndoName);
            int undoGroup = Undo.GetCurrentGroup();

            GameObject root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, UndoName);

            Material startMat = MakeMaterial(new Color(0.30f, 0.85f, 0.40f)); // green - start
            Material railMat = MakeMaterial(new Color(0.55f, 0.60f, 0.68f));  // steel-ish gray-blue - rails
            Material goalMat = MakeMaterial(new Color(0.95f, 0.80f, 0.15f));  // gold - finish

            const float railRadius = 0.18f;
            const float approach = 2f;

            float cursor = origin.z;

            // 00. Start pad.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 0f, footprintZ: 6f, footprintX: 6f, mat: startMat, name: "00_Start");

            // 01. Rail A - flat entry, then a downhill (speeds you up), a turn to the right, a mild
            // uphill (slows you down), a turn back to the left, and a flat run-out to the jump-off
            // point. All still x/z/y deltas relative to the previous waypoint.
            Vector3 railAStart = new Vector3(baseX, groundHeight + railRadius, cursor + approach);
            Vector3[] railA = BuildPath(railAStart,
                new Vector3(0f, 0f, 5f),      // flat entry
                new Vector3(0f, -2.5f, 6f),   // downhill
                new Vector3(4f, 0f, 4f),      // turn right
                new Vector3(0f, 1.5f, 5f),    // mild uphill
                new Vector3(-3f, -0.5f, 4f),  // turn left
                new Vector3(0f, 0f, 3f));     // flat run-out to the gap
            CreateRail(root.transform, railA, railRadius, railMat, "01_RailA");
            Vector3 railAEnd = railA[railA.Length - 1];

            // 02. A genuine gap - no rail, no floor - between the tail of Rail A and the start of
            // Rail B, close enough to clear with a well-timed jump but far enough that walking (or
            // just falling) off the end of Rail A doesn't accidentally carry you onto Rail B for free.
            Vector3 railBStart = railAEnd + new Vector3(2f, -1.5f, 5f);

            // 03. Rail B - another downhill/turn/uphill/turn sequence, ending with a flat run-out
            // extended out over the landing platform's footprint (see the overlap below) rather than
            // stopping short of it.
            Vector3[] railB = BuildPath(railBStart,
                new Vector3(0f, -1f, 5f),     // downhill, continuing the descent
                new Vector3(-3f, 0f, 4f),     // turn left
                new Vector3(0f, 0.5f, 5f),    // mild uphill
                new Vector3(3f, -0.5f, 4f),   // turn right
                new Vector3(0f, 0f, 6f));     // flat run-out over the landing platform
            CreateRail(root.transform, railB, railRadius, railMat, "02_RailB");
            Vector3 railBEnd = railB[railB.Length - 1];

            // 04. Landing platform - positioned under Rail B's already-placed tail (same overlap
            // trick BalanceBeamCourseBuilder uses) so grinding off the end lands you on solid ground
            // with nothing to fall into, and at a height that roughly matches where the rail ends up
            // after all the ups and downs rather than the course's original start height.
            const float overlapIntoLanding = 1.5f;
            float landingTopHeight = railBEnd.y - railRadius;
            Vector3 landingNearEdgeCenter = new Vector3(railBEnd.x, landingTopHeight, railBEnd.z - overlapIntoLanding);
            PlaceLandingPlatform(root.transform, landingNearEdgeCenter, footprintX: 8f, footprintZ: 8f, mat: goalMat, name: "03_Landing");

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            Debug.Log("[Friendslop] Grind rail test area built under '" + RootName + "', offset " +
                       LateralOffsetFromSpawn + "m sideways from spawn. Press Ctrl+S to save the scene. " +
                       "Make sure the player prefab has GrindRailAbility on it (Friendslop > Add Grind Rail To Platformer Player).");
        }

        [MenuItem("Friendslop/Remove Grind Rail Test Area")]
        public static void Remove()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing == null)
            {
                Debug.Log("[Friendslop] No grind rail test area found in this scene.");
                return;
            }

            Undo.DestroyObjectImmediate(existing);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        /// <summary>Builds a chain of waypoints starting at `start`, each subsequent point offset from the previous one by the matching delta.</summary>
        private static Vector3[] BuildPath(Vector3 start, params Vector3[] deltas)
        {
            var points = new Vector3[deltas.Length + 1];
            points[0] = start;
            for (int i = 0; i < deltas.Length; i++)
            {
                points[i + 1] = points[i] + deltas[i];
            }

            return points;
        }

        /// <summary>
        /// Creates a rail: a root object holding the (collider-less) GrindRail component configured
        /// with the given waypoints, plus one purely-cosmetic capsule mesh segment per waypoint pair.
        /// </summary>
        private static void CreateRail(Transform parent, Vector3[] waypoints, float radius, Material material, string name)
        {
            GameObject root = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(root, UndoName);
            root.transform.SetParent(parent, true);
            root.transform.position = waypoints.Length > 0 ? waypoints[0] : Vector3.zero;
            root.transform.rotation = Quaternion.identity;

            GrindRail rail = root.AddComponent<GrindRail>();
            rail.Configure(waypoints, radius);

            for (int i = 0; i < waypoints.Length - 1; i++)
            {
                CreateRailSegmentVisual(root.transform, waypoints[i], waypoints[i + 1], radius, material, name + "_Segment" + i);
            }
        }

        /// <summary>
        /// One visual-only capsule segment between two waypoints - built from CreatePrimitive purely
        /// for its mesh, with its auto-added collider removed (rails aren't physical - see the class
        /// summary), positioned at the segment midpoint and rotated so the mesh's long axis (normally
        /// local Y) points from `a` to `b`. Same scaling convention as BalanceBeamCourseBuilder's
        /// single-segment capsule: the default primitive capsule is 2 units tall with radius 0.5 at
        /// scale 1.
        /// </summary>
        private static void CreateRailSegmentVisual(Transform parent, Vector3 a, Vector3 b, float radius, Material material, string name)
        {
            Vector3 delta = b - a;
            float length = delta.magnitude;
            if (length < 0.0001f) return;
            Vector3 direction = delta / length;

            // LookRotation needs an up-hint that isn't parallel to `direction` - falls back to world
            // forward for the (currently untested, but cheap to guard against) case of a near-vertical
            // segment, where Vector3.up itself wouldn't work as the hint.
            Vector3 upHint = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.999f ? Vector3.forward : Vector3.up;

            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(visual, UndoName);
            Object.DestroyImmediate(visual.GetComponent<CapsuleCollider>());
            visual.name = name;
            visual.transform.SetParent(parent, true);
            visual.transform.position = (a + b) * 0.5f;
            visual.transform.rotation = Quaternion.LookRotation(direction, upHint) * Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(radius / 0.5f, length / 2f, radius / 0.5f);
            visual.GetComponent<Renderer>().sharedMaterial = material;
            visual.isStatic = true;
        }

        /// <summary>
        /// Places one flat platform whose near edge starts `gap` units past the running cursor, and
        /// returns the new cursor (the platform's far edge) so pieces can be chained. Mirrors the
        /// other Friendslop course builders' PlacePlatform.
        /// </summary>
        private static float PlacePlatform(Transform parent, float x, float topHeight, float cursor, float gap, float footprintZ, float footprintX, Material mat, string name)
        {
            const float thickness = 0.5f;
            float nearEdge = cursor + gap;
            float center = nearEdge + footprintZ * 0.5f;
            float farEdge = nearEdge + footprintZ;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(parent, true);
            go.transform.position = new Vector3(x, topHeight - thickness * 0.5f, center);
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = new Vector3(footprintX, thickness, footprintZ);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;

            return farEdge;
        }

        /// <summary>
        /// Places a platform whose near-edge-center (top surface, not box center) is given directly,
        /// rather than chained off a running cursor - used for the landing platform, whose position
        /// has to line up with wherever Rail B's last waypoint ended up after all its ups and downs.
        /// </summary>
        private static void PlaceLandingPlatform(Transform parent, Vector3 nearEdgeTopCenter, float footprintX, float footprintZ, Material mat, string name)
        {
            const float thickness = 0.5f;
            Vector3 center = nearEdgeTopCenter + new Vector3(0f, -thickness * 0.5f, footprintZ * 0.5f);

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(parent, true);
            go.transform.position = center;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = new Vector3(footprintX, thickness, footprintZ);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;
        }

        private static Vector3 FindOrigin()
        {
            GameObject spawn = GameObject.Find("SpawnPoints");
            if (spawn != null)
            {
                return spawn.transform.position;
            }

            Debug.LogWarning("[Friendslop] Couldn't find a 'SpawnPoints' object in this scene - building the grind rail area at world origin (0,0,0). Move the '" + RootName + "' object afterward if it doesn't line up with your spawn.");
            return Vector3.zero;
        }

        private static Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            Material material = new Material(shader);
            material.color = color;
            return material;
        }
    }
}
