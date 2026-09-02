using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Blocks.Gameplay.Core;

namespace Friendslop.EditorTools
{
    /// <summary>
    /// Editor utility that builds a balance-beam test area into the currently open scene: a flat
    /// start, then one continuous beam spanning a pit, ending with its far end extended out over the
    /// landing platform's footprint rather than stopping short of it - so a clean walk-off transitions
    /// straight onto solid ground with no approach gap to fall into. There's deliberately no gap
    /// partway through the beam anymore: with the beam's rounded top and the ability's long re-entry
    /// cooldown after any detach, a mid-beam gap would just be a dead stop rather than an interesting
    /// choice, so the whole span is one uninterrupted beam.
    ///
    /// Each beam is a <see cref="CapsuleCollider"/> (direction = Z, i.e. along the beam) with a
    /// matching visual capsule mesh rotated to line up, plus a <see cref="BalanceBeam"/> component -
    /// see that class for how its dimensions are read from the capsule. The round cross-section is
    /// the point: without BalanceBeamAbility actively locking the player to the centerline, there's no
    /// flat spot to stand on. Built parallel to (offset from) both ObstacleCourseBuilder's and
    /// WallJumpCourseBuilder's courses so re-running any of the three tools doesn't disturb the
    /// others. Run from the Unity menu: Friendslop > Build Balance Beam Test Area. Re-running replaces
    /// the previous version. Remove it with Friendslop > Remove Balance Beam Test Area.
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

            const float beamRadius = 0.15f;

            float cursor = origin.z;

            // 00. Start pad.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: 0f, footprintZ: 6f, footprintX: 6f, mat: startMat, name: "00_Start");

            // 01. One continuous beam - no mid-span gap (see class summary for why) - with its far end
            // extended out over the landing platform's footprint instead of stopping short of it.
            const float approach = 2f;
            const float beamLength = 18f;
            const float landingFootprintZ = 6f;
            const float overlapIntoLanding = 1.5f;

            float beamNearZ = cursor + approach;
            float beamCenterZ = beamNearZ + beamLength * 0.5f;
            CreateBeam(root.transform,
                new Vector3(baseX, groundHeight + beamRadius, beamCenterZ),
                beamLength, beamRadius,
                beamMat, "01_Beam");
            cursor = beamNearZ + beamLength;

            // No pit floor - a genuine void below the beam, same convention as ObstacleCourseBuilder
            // and WallJumpCourseBuilder (no fall-recovery trigger either; see the wall jump course's
            // notes for the same caveat).

            // 02. Landing platform - its near edge sits *underneath* the beam's already-placed far end
            // (negative gap = overlap) rather than starting an approach gap after it, so a clean
            // walk-off the beam lands you on solid ground with nothing to fall into.
            cursor = PlacePlatform(root.transform, baseX, groundHeight, cursor, gap: -overlapIntoLanding, footprintZ: landingFootprintZ, footprintX: 8f, mat: goalMat, name: "02_Landing");

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
        /// Creates a beam: a root object holding the functional CapsuleCollider (direction = Z, i.e.
        /// along the beam's length - matching BalanceBeam's "local +Z = along beam" expectations) and
        /// the BalanceBeam component, plus a child visual-only capsule mesh rotated 90 degrees to line
        /// up with it. The root itself stays unrotated (Quaternion.identity) so it lines up with world
        /// Z, the same forward axis every other Friendslop test course uses, and so BalanceBeam's
        /// Forward/Right properties (which just read transform.forward/right) come out correct -
        /// CapsuleCollider.direction lets the collider's long axis be Z without needing to rotate the
        /// transform itself, so only the cosmetic child mesh needs the 90-degree twist.
        /// </summary>
        private static void CreateBeam(Transform parent, Vector3 center, float length, float radius, Material material, string name)
        {
            GameObject root = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(root, UndoName);
            root.transform.SetParent(parent, true);
            root.transform.position = center;
            root.transform.rotation = Quaternion.identity;
            root.isStatic = true;

            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.direction = 2; // Z axis - "along the beam".
            capsule.radius = radius;
            capsule.height = length;

            // Visual only, built from CreatePrimitive purely for its capsule mesh - its own auto-added
            // collider is removed since the functional one lives on the root instead. The default
            // primitive capsule is 2 units tall (along its own local Y) with radius 0.5 at scale 1;
            // scale it to match, then rotate it so that authored "up" axis points along the root's Z.
            GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(visual, UndoName);
            Object.DestroyImmediate(visual.GetComponent<CapsuleCollider>());
            visual.name = name + "_Visual";
            visual.transform.SetParent(root.transform, false);
            visual.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(radius / 0.5f, length / 2f, radius / 0.5f);
            visual.GetComponent<Renderer>().sharedMaterial = material;
            visual.isStatic = true;

            root.AddComponent<BalanceBeam>();
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
