using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The default moving platform prefab (Assets/Core/Prefabs/MovingPlatform.prefab), made from
    /// Assets/Maps/moving_platform.glb - see <see cref="MovingPlatform"/>:
    ///   Moving Platform (MovingPlatform) > Node A, Node B, Platform > moving_platform (Regular: a normal collider)
    /// Platform's origin is the top centre of the model, and Node B starts 8 m from Node A along +Z.
    ///
    ///   - Friendslop > Challenges > Add Moving Platform (also GameObject > Friendslop > Moving Platform): puts one
    ///     under the selected object (e.g. a challenge's root, in its prefab or scene), at the Scene view's
    ///     centre. Move Node A / Node B to set the path; set speed, pause and phase on the root.
    ///   - Friendslop > Challenges > Rebuild Moving Platform Prefab: remakes the prefab (after swapping the
    ///     default model, say). Platforms already placed keep their own node positions and settings.
    /// The prefab is made automatically the first time either is used.
    /// </summary>
    public static class MovingPlatformBuilder
    {
        public const string PrefabPath = "Assets/Core/Prefabs/MovingPlatform.prefab";
        public const string ModelPath = "Assets/Maps/moving_platform.glb";

        /// <summary>Distance from Node A to Node B in a new platform.</summary>
        private const float DefaultTravel = 8f;

        /// <summary>The prefab, made first if it doesn't exist yet (null if moving_platform.glb is missing).</summary>
        public static GameObject GetOrBuildPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            return prefab != null ? prefab : BuildPrefab();
        }

        [MenuItem("Friendslop/Challenges/Rebuild Moving Platform Prefab")]
        private static void RebuildFromMenu()
        {
            GameObject prefab = BuildPrefab();
            if (prefab != null)
            {
                EditorGUIUtility.PingObject(prefab);
                Debug.Log($"[Friendslop] Rebuilt {PrefabPath}.", prefab);
            }
        }

        public static GameObject BuildPrefab()
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null)
            {
                EditorUtility.DisplayDialog("Moving Platform", $"Couldn't find {ModelPath} (or it hasn't imported yet).", "OK");
                return null;
            }

            // Built in a preview scene so nothing touches whatever scene is open.
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Moving Platform");
                SceneManager.MoveGameObjectToScene(root, preview);

                var nodeA = new GameObject("Node A").transform;
                nodeA.SetParent(root.transform, false);
                var nodeB = new GameObject("Node B").transform;
                nodeB.SetParent(root.transform, false);
                nodeB.localPosition = new Vector3(0f, 0f, DefaultTravel);

                var platform = new GameObject("Platform").transform;
                platform.SetParent(root.transform, false);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
                instance.transform.SetParent(platform, false);
                MapObjectPropertySetup.ApplyProperty(instance, MapObjectPropertyType.Regular);

                // Put the model's top centre on Platform's origin.
                Bounds bounds = WorldBounds(instance);
                instance.transform.position -= new Vector3(bounds.center.x, bounds.max.y, bounds.center.z) - platform.position;

                var mover = root.AddComponent<MovingPlatform>();
                mover.Configure(platform, nodeA, nodeB, 3f, 1f, 0f);

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
                return saved ? prefab : null;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static Bounds WorldBounds(GameObject obj)
        {
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.zero);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        /// <summary>
        /// A platform prefab instance under <paramref name="parent"/>, with its nodes at
        /// <paramref name="nodeA"/> / <paramref name="nodeB"/> (parent space) and the given settings.
        /// </summary>
        public static MovingPlatform Place(Transform parent, string name, Vector3 nodeA, Vector3 nodeB,
            float speed, float pause, float phase)
        {
            GameObject prefab = GetOrBuildPrefab();
            if (prefab == null) return null;

            var obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            obj.name = name;
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            var mover = obj.GetComponent<MovingPlatform>();
            mover.NodeA.localPosition = nodeA;
            mover.NodeB.localPosition = nodeB;
            mover.PlatformTransform.localPosition = nodeA;
            mover.Configure(mover.PlatformTransform, mover.NodeA, mover.NodeB, speed, pause, phase);

            // Changed by script on a prefab instance: record them as overrides so they're saved.
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover.NodeA);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover.NodeB);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover.PlatformTransform);
            return mover;
        }

        [MenuItem("Friendslop/Challenges/Add Moving Platform")]
        [MenuItem("GameObject/Friendslop/Moving Platform", false, 10)]
        private static void AddFromMenu(MenuCommand command)
        {
            GameObject prefab = GetOrBuildPrefab();
            if (prefab == null) return;

            Transform parent = (command.context as GameObject)?.transform ?? Selection.activeTransform;
            var obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (parent == null)
            {
                // Inside an open prefab: put it in the prefab's root.
                PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) obj.transform.SetParent(stage.prefabContentsRoot.transform, false);
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view != null) obj.transform.position = view.pivot;

            Undo.RegisterCreatedObjectUndo(obj, "Add Moving Platform");
            Selection.activeGameObject = obj;
        }
    }
}
