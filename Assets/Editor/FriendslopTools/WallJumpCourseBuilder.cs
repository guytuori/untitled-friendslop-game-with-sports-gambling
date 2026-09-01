using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Friendslop.EditorTools
{
    /// <summary>
    /// Editor utility that builds a wall-jump test area into the currently open scene: a couple of
    /// standalone practice walls on solid ground, followed by a pit spanned by two long parallel
    /// walls that the player has to zig-zag along by wall-jumping back and forth, ending on a safe
    /// landing platform.
    ///
    /// Sized against the same tuning ObstacleCourseBuilder uses (the "[BB] PlatformerPlayer"
    /// prefab: moveSpeed 3, sprintSpeed 9, jumpHeight 3, gravity -15) plus WallSlideAbility's
    /// defaults (wallJumpHeight 2.2, wallJumpPushForce 7). The wall-to-wall gap in particular is a
    /// starting estimate, not a measured value - a wall jump's actual horizontal distance depends a
    /// lot on how much forward/sideways input the player holds through the jump (air control adds
    /// on top of the launch impulse), so playtest it and adjust wallGap below, or the corresponding
    /// fields on WallSlideAbility, if the gap feels too easy or too far.
    ///
    /// Built parallel to (offset from) ObstacleCourseBuilder's course rather than after it, so
    /// re-running either tool doesn't disturb the other. Run from the Unity menu:
    /// Friendslop > Build Wall Jump Test Area. Re-running replaces the previous version rather than
    /// stacking a second one. Remove it again with Friendslop > Remove Wall Jump Test Area.
    /// </summary>
    public static class WallJumpCourseBuilder
    {
        private const string RootName = "WallJumpCourse";
        private const string UndoName = "Build Wall Jump Test Area";

        // How far sideways (world +X) to offset this course from world origin / SpawnPoints, so it
        // doesn't overlap ObstacleCourseBuilder's course (which runs along +Z from the same origin).
        private const float LateralOffsetFromSpawn = 14f;

        [MenuItem("Friendslop/Build Wall Jump Test Area")]
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

            Material startMat = MakeMaterial(new Color(0.30f, 0.85f, 0.40f));       // green - start
            Material pathMat = MakeMaterial(new Color(0.75f, 0.75f, 0.80f));        // light gray - safe ground
            Material practiceWallMat = MakeMaterial(new Color(0.35f, 0.55f, 0.95f)); // blue - practice walls (safe ground below)
            Material corridorWallMat = MakeMaterial(new Color(0.85f, 0.20f, 0.30f)); // red - walls over the pit
            Material goalMat = MakeMaterial(new Color(0.95f, 0.80f, 0.15f));        // gold - finish

            float cursor = origin.z;

            // ---- Section A: flat area with a couple of standalone practice walls (solid ground throughout) ----

            // 00. Start pad.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 0f, footprintZ: 6f, footprintX: 8f, mat: startMat, name: "00_Start");

            // Solid ground running the whole length of section A, so missing a wall jump here just
            // means landing on the floor, not falling - this section is purely about learning the
            // slide/jump feel before the pit shows up.
            float sectionAGroundNear = cursor;

            // 01. Practice wall - straight-on approach, jump/slide/jump back the way you came.
            const float wallARunway = 3f;
            const float wallAWidth = 6f, wallAThickness = 0.6f, wallAHeight = 5f;
            float wallANearZ = cursor + wallARunway;
            float wallACenterZ = wallANearZ + wallAThickness * 0.5f;
            PlaceBox(root.transform,
                new Vector3(baseX, groundHeight + wallAHeight * 0.5f, wallACenterZ),
                new Vector3(wallAWidth, wallAHeight, wallAThickness),
                practiceWallMat, "01_PracticeWallA");
            cursor = wallANearZ + wallAThickness;

            // 02. Practice wall - offset sideways so the approach isn't just a repeat of wall A.
            const float wallBRunway = 3f;
            const float wallBLateralOffset = 2.5f;
            const float wallBWidth = 6f, wallBThickness = 0.6f, wallBHeight = 5f;
            float wallBNearZ = cursor + wallBRunway;
            float wallBCenterZ = wallBNearZ + wallBThickness * 0.5f;
            PlaceBox(root.transform,
                new Vector3(baseX + wallBLateralOffset, groundHeight + wallBHeight * 0.5f, wallBCenterZ),
                new Vector3(wallBWidth, wallBHeight, wallBThickness),
                practiceWallMat, "02_PracticeWallB");
            cursor = wallBNearZ + wallBThickness;

            // 03. Launch pad - last solid ground before the pit.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 2.5f, footprintZ: 4f, footprintX: 8f, mat: pathMat, name: "03_PitEdge");
            float sectionAGroundFar = cursor;

            // Fill section A's ground as one continuous slab so there's no accidental gap under the
            // practice walls (PlacePlatform's individual pads already cover the start/edge, this
            // covers the runway sections between and under the walls). Sunk 2cm below the named
            // platforms' surfaces so the two overlapping floors don't z-fight where they coincide.
            float sectionAGroundLength = sectionAGroundFar - sectionAGroundNear;
            const float fillThickness = 0.5f;
            const float fillSurfaceRecess = 0.02f;
            PlaceBox(root.transform,
                new Vector3(baseX + wallBLateralOffset * 0.5f, groundHeight - fillSurfaceRecess - fillThickness * 0.5f, sectionAGroundNear + sectionAGroundLength * 0.5f),
                new Vector3(8f + wallBLateralOffset, fillThickness, sectionAGroundLength),
                pathMat, "03b_SectionAGroundFill");

            // ---- Section B: the pit, spanned by two long parallel walls ----

            const float approachGap = 2.5f;       // running-jump distance from the launch pad into the first wall
            const float corridorLength = 18f;     // how far the parallel walls run along Z
            const float wallGap = 3.0f;            // distance between the walls' inner faces - the actual jump distance
            const float corridorWallThickness = 0.6f;
            const float corridorWallTop = 4f;      // height above ground the walls stick up
            const float pitDepth = 20f;            // how far down the walls (and the void) extend

            float corridorNearZ = cursor + approachGap;
            float corridorFarZ = corridorNearZ + corridorLength;
            float corridorWallHeight = corridorWallTop + pitDepth;
            float corridorWallCenterY = groundHeight + corridorWallTop - corridorWallHeight * 0.5f;
            float corridorCenterZ = corridorNearZ + corridorLength * 0.5f;

            PlaceBox(root.transform,
                new Vector3(baseX - wallGap * 0.5f - corridorWallThickness * 0.5f, corridorWallCenterY, corridorCenterZ),
                new Vector3(corridorWallThickness, corridorWallHeight, corridorLength),
                corridorWallMat, "04_CorridorWall_Left");

            PlaceBox(root.transform,
                new Vector3(baseX + wallGap * 0.5f + corridorWallThickness * 0.5f, corridorWallCenterY, corridorCenterZ),
                new Vector3(corridorWallThickness, corridorWallHeight, corridorLength),
                corridorWallMat, "04_CorridorWall_Right");

            cursor = corridorFarZ;

            // 05. Landing platform - solid ground again, safely past the pit.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 2.5f, footprintZ: 8f, footprintX: 10f, mat: goalMat, name: "05_Landing");

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            Debug.Log("[Friendslop] Wall jump test area built under '" + RootName + "' (" + (cursor - origin.z) + "m total length, offset " +
                       LateralOffsetFromSpawn + "m sideways from spawn). Press Ctrl+S to save the scene. " +
                       "The wall-to-wall gap (" + wallGap + "m) is an estimate - if it's too easy or too far, tune 'wallGap' here or the " +
                       "wall jump height/push force on WallSlideAbility. Make sure the player prefab actually has WallSlideAbility on it first " +
                       "(Friendslop > Add Wall Jump To Platformer Player).");
        }

        [MenuItem("Friendslop/Remove Wall Jump Test Area")]
        public static void Remove()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing == null)
            {
                Debug.Log("[Friendslop] No wall jump test area found in this scene.");
                return;
            }

            Undo.DestroyObjectImmediate(existing);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        /// <summary>
        /// Places one flat platform whose near edge starts `gap` units past the running cursor,
        /// and returns the new cursor (the platform's far edge) so pieces can be chained.
        /// Mirrors ObstacleCourseBuilder.PlacePlatform.
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
        /// Places an axis-aligned box at an explicit center and size - used for walls, where
        /// specifying exact bounds is clearer than the top-surface cursor chaining PlacePlatform uses.
        /// </summary>
        private static void PlaceBox(Transform parent, Vector3 center, Vector3 size, Material material, string name)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(parent, true);
            go.transform.position = center;
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material;
            go.isStatic = true;
        }

        private static Vector3 FindOrigin()
        {
            GameObject spawn = GameObject.Find("SpawnPoints");
            if (spawn != null)
            {
                return spawn.transform.position;
            }

            Debug.LogWarning("[Friendslop] Couldn't find a 'SpawnPoints' object in this scene - building the wall jump area at world origin (0,0,0). Move the '" + RootName + "' object afterward if it doesn't line up with your spawn.");
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
