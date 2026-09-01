using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Blocks.Gameplay.Core;

namespace Friendslop.EditorTools
{
    /// <summary>
    /// Editor utility that builds a balance-beam test area into the currently open scene: a flat
    /// start, then two beams spanning a pit (with a short jumpable gap between them, to exercise
    /// snapping onto a beam mid-jump rather than only by walking onto one), ending on a safe landing
    /// platform.
    ///
    /// Each beam is a thin box with a <see cref="BalanceBeam"/> component - see that class for how
    /// its dimensions are read from the box. Built parallel to (offset from) both
    /// ObstacleCourseBuilder's and WallJumpCourseBuilder's courses so re-running any of the three
    /// tools doesn't disturb the others. Run from the Unity menu: Friendslop > Build Balance Beam
    /// Test Area. Re-running replaces the previous version. Remove it with Friendslop > Remove
    /// Balance Beam Test Area.
    ///
    /// Make sure the player prefab actually has BalanceBeamAbility on it first (Friendslop > Add
    /// Balance Beam To Platformer Player / To Core Player).
    /// </summary>
    public static class BalanceBeamCourseBuilder
    {
        private const string RootName = "BalanceBeamCourse";
        private const string UndoName = "Build Balance Beam Test Area";

        // West side of spawn, mirroring WallJumpCourseBuilder's +14 offset to the east.
        private const float LateralOffsetFromSpawn = -14f;

        [MenuItem("Friendslop/Build Balance Beam Test Area")]
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
            Material beamMat = MakeMaterial(new Color(0.65f, 0.45f, 0.25f));  // wood-ish brown - beams
            Material goalMat = MakeMaterial(new Color(0.95f, 0.80f, 0.15f));  // gold - finish

            const float beamWidth = 0.3f;
            const float beamHeight = 0.3f;

            float cursor = origin.z;

            // 00. Start pad.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 0f, footprintZ: 6f, footprintX: 6f, mat: startMat, name: "00_Start");

            // 01. Beam one.
            const float approach1 = 2f;
            const float beam1Length = 9f;
            float beam1NearZ = cursor + approach1;
            float beam1CenterZ = beam1NearZ + beam1Length * 0.5f;
            CreateBeam(root.transform,
                new Vector3(baseX, groundHeight + beamHeight * 0.5f, beam1CenterZ),
                new Vector3(beamWidth, beamHeight, beam1Length),
                beamMat, "01_Beam");
            cursor = beam1NearZ + beam1Length;

            // 02. Short jumpable gap to beam two - exercises snapping onto a beam mid-jump rather
            // than only by walking onto one, and tests re-entry after a normal (non-beam) jump.
            const float interBeamGap = 2f;
            const float beam2Length = 7f;
            float beam2NearZ = cursor + interBeamGap;
            float beam2CenterZ = beam2NearZ + beam2Length * 0.5f;
            CreateBeam(root.transform,
                new Vector3(baseX, groundHeight + beamHeight * 0.5f, beam2CenterZ),
                new Vector3(beamWidth, beamHeight, beam2Length),
                beamMat, "02_Beam");
            cursor = beam2NearZ + beam2Length;

            // No pit floor - a genuine void below both beams, same convention as ObstacleCourseBuilder
            // and WallJumpCourseBuilder (no fall-recovery trigger either; see the wall jump course's
            // notes for the same caveat).

            // 03. Landing platform.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 2.5f, footprintZ: 6f, footprintX: 8f, mat: goalMat, name: "03_Landing");

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            Debug.Log("[Friendslop] Balance beam test area built under '" + RootName + "' (" + (cursor - origin.z) + "m total length, offset " +
                       LateralOffsetFromSpawn + "m sideways from spawn). Press Ctrl+S to save the scene. " +
                       "Make sure the player prefab has BalanceBeamAbility on it (Friendslop > Add Balance Beam To Platformer Player).");
        }

        [MenuItem("Friendslop/Remove Balance Beam Test Area")]
        public static void Remove()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing == null)
            {
                Debug.Log("[Friendslop] No balance beam test area found in this scene.");
                return;
            }

            Undo.DestroyObjectImmediate(existing);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        /// <summary>
        /// Creates a beam: a thin box with a BalanceBeam component. Built unrotated (local +Z runs
        /// along the beam's length, matching BalanceBeam's expectations) so it lines up with world Z,
        /// the same forward axis every other Friendslop test course uses.
        /// </summary>
        private static void CreateBeam(Transform parent, Vector3 center, Vector3 size, Material material, string name)
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
            go.AddComponent<BalanceBeam>();
        }

        /// <summary>
        /// Places one flat platform whose near edge starts `gap` units past the running cursor,
        /// and returns the new cursor (the platform's far edge) so pieces can be chained.
        /// Mirrors ObstacleCourseBuilder.PlacePlatform / WallJumpCourseBuilder.PlacePlatform.
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

            Debug.LogWarning("[Friendslop] Couldn't find a 'SpawnPoints' object in this scene - building the balance beam area at world origin (0,0,0). Move the '" + RootName + "' object afterward if it doesn't line up with your spawn.");
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
