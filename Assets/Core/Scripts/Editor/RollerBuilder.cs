using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The default roller prefab (Assets/Core/Prefabs/Roller.prefab), made from Assets/Maps/roller.glb (a 2 m
    /// Total Editor cylinder, shown at x2 = 4 m across and 4 m long) - see <see cref="Roller"/>:
    ///   Roller (MovingPlatform + Roller) > Node A, Node B, Platform > roller (scaled x2, centred, no collider)
    /// Platform's origin is the cylinder's centre, and the nodes mark where the centre goes. The model has no
    /// collider in the prefab; Roller adds a convex one when it starts (roller.glb imports with Read/Write on). A new roller rolls
    /// along +X from Node A (its axis is along Z); turn the root to roll another way, or move the nodes (the axis
    /// is always level and square to A -> B).
    ///
    ///   - Friendslop > Challenges > Add Roller (also GameObject > Friendslop > Roller): puts one under the
    ///     selected object, at the Scene view's centre.
    ///   - Friendslop > Challenges > Rebuild Roller Prefab.
    /// The prefab is made the first time either is used (or when a challenge builder needs it).
    ///
    /// The long roller (2026-11-01, Assets/Core/Prefabs/Roller2x1x1.prefab - Friendslop > Challenges > Add Long
    /// Roller / Rebuild Long Roller Prefab): the same cylinder at x1 across (2 m) and x2 long (4 m) - a 2x1x1 in
    /// Total Editor cells, the size of the black tunnel blocks in Challenge_32_09 / 32_10. It's built a hair smaller
    /// (<see cref="LongFit"/>) so that, tucked inside a block, its ends don't flicker against the block's faces.
    /// </summary>
    public static class RollerBuilder
    {
        public const string PrefabPath = "Assets/Core/Prefabs/Roller.prefab";
        public const string ModelPath = "Assets/Maps/roller.glb";

        /// <summary>The model is a 2 m cylinder from Total Editor; the roller is twice that.</summary>
        public const float ModelScale = 2f;

        public const string LongPrefabPath = "Assets/Core/Prefabs/Roller2x1x1.prefab";

        /// <summary>The long roller: 2 m across, 4 m long (along the model's Z, its axis) - times <see cref="LongFit"/>.</summary>
        public static readonly Vector3 LongModelScale = new Vector3(1f, 1f, 2f);

        /// <summary>Slightly under size, so a roller hidden inside a block exactly its size doesn't z-fight with it.</summary>
        public const float LongFit = 0.99f;

        private const float DefaultTravel = 12f;

        public static GameObject GetOrBuildPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            return prefab != null ? prefab : BuildPrefab();
        }

        public static GameObject GetOrBuildLongPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LongPrefabPath);
            return prefab != null ? prefab : BuildPrefab(LongPrefabPath, LongModelScale * LongFit, "Roller 2x1x1");
        }

        [MenuItem("Friendslop/Challenges/Rebuild Long Roller Prefab")]
        private static void RebuildLongFromMenu()
        {
            GameObject prefab = BuildPrefab(LongPrefabPath, LongModelScale * LongFit, "Roller 2x1x1");
            if (prefab != null)
            {
                EditorGUIUtility.PingObject(prefab);
                Debug.Log($"[Friendslop] Rebuilt {LongPrefabPath}.", prefab);
            }
        }

        [MenuItem("Friendslop/Challenges/Rebuild Roller Prefab")]
        private static void RebuildFromMenu()
        {
            GameObject prefab = BuildPrefab();
            if (prefab != null)
            {
                EditorGUIUtility.PingObject(prefab);
                Debug.Log($"[Friendslop] Rebuilt {PrefabPath}.", prefab);
            }
        }

        public static GameObject BuildPrefab() => BuildPrefab(PrefabPath, Vector3.one * ModelScale, "Roller");

        /// <summary>A roller prefab at <paramref name="path"/> with the cylinder scaled by <paramref name="modelScale"/> (its axis is the model's Z).</summary>
        public static GameObject BuildPrefab(string path, Vector3 modelScale, string rootName)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (model == null)
            {
                EditorUtility.DisplayDialog("Roller", $"Couldn't find {ModelPath} (or it hasn't imported yet).", "OK");
                return null;
            }

            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject(rootName);
                SceneManager.MoveGameObjectToScene(root, preview);

                var nodeA = new GameObject("Node A").transform;
                nodeA.SetParent(root.transform, false);
                var nodeB = new GameObject("Node B").transform;
                nodeB.SetParent(root.transform, false);
                nodeB.localPosition = new Vector3(DefaultTravel, 0f, 0f);

                var platform = new GameObject("Platform").transform;
                platform.SetParent(root.transform, false);

                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview);
                instance.transform.SetParent(platform, false);
                instance.transform.localScale = modelScale;
                foreach (Collider collider in instance.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(collider);

                // Put the cylinder's centre on Platform's origin (it spins about that point).
                Bounds bounds = WorldBounds(instance);
                instance.transform.position -= bounds.center - platform.position;

                root.AddComponent<MovingPlatform>().Configure(platform, nodeA, nodeB, 8f, 0f, 0f);
                root.AddComponent<Roller>();

                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
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
        /// A roller prefab instance under <paramref name="parent"/> rolling Node A -> <paramref name="waypoints"/> ->
        /// Node B (all parent space, the cylinder's centre), with the given settings.
        /// </summary>
        public static MovingPlatform Place(Transform parent, string name, Vector3 nodeA, Vector3[] waypoints, Vector3 nodeB,
            float speed, float pause, float phase) =>
            Place(GetOrBuildPrefab(), parent, name, nodeA, waypoints, nodeB, speed, pause, phase, ease: true, oneWay: false, alignToPath: false);

        /// <summary>
        /// A long (2x1x1) roller on a straight one-way run from <paramref name="nodeA"/> to <paramref name="nodeB"/> (parent
        /// space, the roller's centre), at a steady speed: out of one tunnel block and into the other, then it starts over.
        /// The roller is turned so its axis is square to the run.
        /// </summary>
        public static MovingPlatform PlaceLongOneWay(Transform parent, string name, Vector3 nodeA, Vector3 nodeB, float speed, float phase) =>
            Place(GetOrBuildLongPrefab(), parent, name, nodeA, null, nodeB, speed, 0f, phase, ease: false, oneWay: true, alignToPath: true);

        /// <summary>
        /// The general version. <paramref name="alignToPath"/> turns the roller's root (about Y) so its model rolls
        /// along A -> B (the model's axis is its Z, so it rolls along the root's X); the node positions are still given
        /// in <paramref name="parent"/>'s space.
        /// </summary>
        public static MovingPlatform Place(GameObject prefab, Transform parent, string name, Vector3 nodeA, Vector3[] waypoints, Vector3 nodeB,
            float speed, float pause, float phase, bool ease, bool oneWay, bool alignToPath)
        {
            if (prefab == null) return null;

            var obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            obj.name = name;
            obj.transform.localPosition = Vector3.zero;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = Vector3.one;

            Quaternion toLocal = Quaternion.identity;
            if (alignToPath)
            {
                Vector3 along = nodeB - nodeA;
                along.y = 0f;
                if (along.sqrMagnitude > 1e-6f)
                {
                    Quaternion yaw = Quaternion.FromToRotation(Vector3.right, along.normalized);
                    obj.transform.localRotation = yaw;
                    toLocal = Quaternion.Inverse(yaw);
                }
            }

            var mover = obj.GetComponent<MovingPlatform>();
            mover.NodeA.localPosition = toLocal * nodeA;
            mover.NodeB.localPosition = toLocal * nodeB;
            mover.PlatformTransform.localPosition = toLocal * nodeA;

            var points = new Transform[waypoints != null ? waypoints.Length : 0];
            for (int i = 0; i < points.Length; i++)
            {
                var point = new GameObject($"Waypoint {i + 1}").transform;
                point.SetParent(obj.transform, false);
                point.localPosition = toLocal * waypoints[i];
                point.SetSiblingIndex(mover.NodeB.GetSiblingIndex()); // between Node A and Node B in the Hierarchy
                points[i] = point;
            }
            mover.Configure(mover.PlatformTransform, mover.NodeA, mover.NodeB, speed, pause, phase, ease, points, oneWay);

            PrefabUtility.RecordPrefabInstancePropertyModifications(obj.transform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover.NodeA);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover.NodeB);
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover.PlatformTransform);
            return mover;
        }

        [MenuItem("Friendslop/Challenges/Add Roller")]
        [MenuItem("GameObject/Friendslop/Roller", false, 11)]
        private static void AddFromMenu(MenuCommand command) => AddPrefab(command, GetOrBuildPrefab(), "Add Roller");

        [MenuItem("Friendslop/Challenges/Add Long Roller")]
        [MenuItem("GameObject/Friendslop/Long Roller (2x1x1)", false, 12)]
        private static void AddLongFromMenu(MenuCommand command) => AddPrefab(command, GetOrBuildLongPrefab(), "Add Long Roller");

        private static void AddPrefab(MenuCommand command, GameObject prefab, string undoName)
        {
            if (prefab == null) return;

            Transform parent = (command.context as GameObject)?.transform ?? Selection.activeTransform;
            var obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (parent == null)
            {
                PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
                if (stage != null) obj.transform.SetParent(stage.prefabContentsRoot.transform, false);
            }

            SceneView view = SceneView.lastActiveSceneView;
            if (view != null) obj.transform.position = view.pivot;

            Undo.RegisterCreatedObjectUndo(obj, undoName);
            Selection.activeGameObject = obj;
        }
    }
}
