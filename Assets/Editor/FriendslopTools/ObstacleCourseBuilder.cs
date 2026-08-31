using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Friendslop.EditorTools
{
    /// <summary>
    /// Editor utility that builds a simple obstacle course (platforms, a ramp, a narrow beam,
    /// and a couple of jump gaps) into the currently open scene.
    ///
    /// The gaps and heights below are sized against the actual tuning on the
    /// "[BB] PlatformerPlayer" prefab (moveSpeed 3, sprintSpeed 9, jumpHeight 3, gravity -15),
    /// not the CoreMovement class defaults - so a flat running jump clears roughly 3-4m,
    /// a sprinting jump clears roughly 10-11m, and the character can comfortably clear a
    /// ~1.5-2m step up given its ~3m jump apex.
    ///
    /// Run it from the Unity menu: Friendslop > Build Obstacle Course.
    /// Re-running it replaces the previous course rather than stacking a second one.
    /// Remove it again with Friendslop > Remove Obstacle Course.
    /// </summary>
    public static class ObstacleCourseBuilder
    {
        private const string RootName = "ObstacleCourse";

        [MenuItem("Friendslop/Build Obstacle Course")]
        public static void Build()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing != null)
            {
                Undo.DestroyObjectImmediate(existing);
            }

            Vector3 origin = FindOrigin();

            Undo.SetCurrentGroupName("Build Obstacle Course");
            int undoGroup = Undo.GetCurrentGroup();

            GameObject root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Obstacle Course");

            Material startMat = MakeMaterial(new Color(0.30f, 0.85f, 0.40f));      // green - start
            Material pathMat = MakeMaterial(new Color(0.75f, 0.75f, 0.80f));       // light gray - normal platforms
            Material rampMat = MakeMaterial(new Color(0.35f, 0.55f, 0.95f));       // blue - ramp
            Material beamMat = MakeMaterial(new Color(0.95f, 0.55f, 0.20f));       // orange - precision beam
            Material challengeMat = MakeMaterial(new Color(0.85f, 0.20f, 0.30f));  // red - big sprint-jump
            Material goalMat = MakeMaterial(new Color(0.95f, 0.80f, 0.15f));       // gold - finish

            float groundHeight = origin.y;
            float stepUpHeight = origin.y + 1.5f;
            float rampTopHeight = origin.y + 3.5f;
            float challengeHeight = origin.y + 3.0f;

            // Running cursor = the Z coordinate of the far edge of the last piece placed.
            float cursor = origin.z;

            // 0. Start pad - generous landing spot right at spawn.
            cursor = PlacePlatform(root.transform, origin.x, groundHeight, cursor, gap: 0f, footprintZ: 4f, footprintX: 4f, mat: startMat, name: "00_Start");

            // 1. Easy warm-up hop - flat, ~2.5m gap.
            cursor = PlacePlatform(root.transform, origin.x, groundHeight, cursor, gap: 2.5f, footprintZ: 3f, footprintX: 3f, mat: pathMat, name: "01_Hop");

            // 2. Bigger flat hop - ~3.5m gap, needs a real running start.
            cursor = PlacePlatform(root.transform, origin.x, groundHeight, cursor, gap: 3.5f, footprintZ: 3f, footprintX: 3f, mat: pathMat, name: "02_Hop");

            // 3. Step up onto a 1.5m ledge - short gap, tests jump height instead of distance.
            cursor = PlacePlatform(root.transform, origin.x, stepUpHeight, cursor, gap: 1.5f, footprintZ: 3f, footprintX: 3f, mat: pathMat, name: "03_StepUp");

            // 4. Ramp - a walkable incline from the ledge up to 3.5m. No jump required.
            Vector3 rampBottom = new Vector3(origin.x, stepUpHeight, cursor);
            cursor += 6f;
            Vector3 rampTop = new Vector3(origin.x, rampTopHeight, cursor);
            CreateRamp(root.transform, rampBottom, rampTop, width: 3f, thickness: 0.4f, material: rampMat, name: "04_Ramp");

            // 5. Plateau at the top of the ramp.
            cursor = PlacePlatform(root.transform, origin.x, rampTopHeight, cursor, gap: 0f, footprintZ: 4f, footprintX: 4f, mat: pathMat, name: "05_RampTop");

            // 6. Narrow beam - precision test, short gap to get onto it.
            cursor = PlacePlatform(root.transform, origin.x, rampTopHeight, cursor, gap: 2f, footprintZ: 5f, footprintX: 1.5f, mat: beamMat, name: "06_Beam");

            // 7. The big one - a sprint-required leap of faith, stepping down slightly.
            cursor = PlacePlatform(root.transform, origin.x, challengeHeight, cursor, gap: 7f, footprintZ: 3f, footprintX: 3f, mat: challengeMat, name: "07_BigGap");

            // 8. Finish platform.
            cursor = PlacePlatform(root.transform, origin.x, challengeHeight, cursor, gap: 2f, footprintZ: 5f, footprintX: 5f, mat: goalMat, name: "08_Finish");

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            Debug.Log("[Friendslop] Obstacle course built under '" + RootName + "' (" + cursor + "m total length). " +
                       "Press Ctrl+S to save the scene. If it's floating or buried, select '" + RootName + "' and nudge its Y position.");
        }

        [MenuItem("Friendslop/Remove Obstacle Course")]
        public static void Remove()
        {
            GameObject existing = GameObject.Find(RootName);
            if (existing == null)
            {
                Debug.Log("[Friendslop] No obstacle course found in this scene.");
                return;
            }

            Undo.DestroyObjectImmediate(existing);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        /// <summary>
        /// Places one flat platform whose near edge starts `gap` units past the running cursor,
        /// and returns the new cursor (the platform's far edge) so pieces can be chained.
        /// </summary>
        private static float PlacePlatform(Transform parent, float x, float topHeight, float cursor, float gap, float footprintZ, float footprintX, Material mat, string name)
        {
            const float thickness = 0.5f;
            float nearEdge = cursor + gap;
            float center = nearEdge + footprintZ * 0.5f;
            float farEdge = nearEdge + footprintZ;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Build Obstacle Course");
            go.transform.SetParent(parent, true);
            go.transform.position = new Vector3(x, topHeight - thickness * 0.5f, center);
            go.transform.rotation = Quaternion.identity;
            go.transform.localScale = new Vector3(footprintX, thickness, footprintZ);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            go.isStatic = true;

            return farEdge;
        }

        /// <summary>
        /// Creates a tilted box ramp whose walkable top surface runs exactly from
        /// bottomEdgeCenter to topEdgeCenter.
        /// </summary>
        private static void CreateRamp(Transform parent, Vector3 bottomEdgeCenter, Vector3 topEdgeCenter, float width, float thickness, Material material, string name)
        {
            Vector3 diff = topEdgeCenter - bottomEdgeCenter;
            float slopeLength = diff.magnitude;
            Vector3 mid = (bottomEdgeCenter + topEdgeCenter) * 0.5f;

            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Build Obstacle Course");
            go.transform.SetParent(parent, true);
            go.transform.rotation = Quaternion.LookRotation(diff.normalized, Vector3.up);
            // Shift down half the thickness along the ramp's own "up" so the walkable top face
            // (not the box's geometric center) is the line connecting the two edge points.
            go.transform.position = mid - go.transform.up * (thickness * 0.5f);
            go.transform.localScale = new Vector3(width, thickness, slopeLength);
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

            Debug.LogWarning("[Friendslop] Couldn't find a 'SpawnPoints' object in this scene - building the course at world origin (0,0,0). Move the '" + RootName + "' object afterward if it doesn't line up with your spawn.");
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
