using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper for <see cref="WallSlideAbility"/>: adds the component to a player
    /// prefab and wires its "On Jump Pressed" GameEvent field to the same asset already assigned
    /// on that prefab's CoreInputHandler, so wall jumping works immediately without any manual
    /// dragging in the Inspector.
    ///
    /// Uses PrefabUtility.LoadPrefabContents / SaveAsPrefabAsset (Unity's own prefab-editing API)
    /// rather than hand-editing the .prefab file, so this can't produce a malformed prefab.
    ///
    /// Re-running is safe: if the component is already present, it's left as-is (not duplicated),
    /// though the event field is always re-synced from CoreInputHandler.
    /// </summary>
    public static class WallSlideSetup
    {
        private const string PlatformerPlayerPath = "Assets/Platformer/Prefabs/[BB] PlatformerPlayer.prefab";
        private const string CorePlayerPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";

        [MenuItem("Friendslop/Add Wall Jump To Platformer Player")]
        public static void AddToPlatformerPlayer() => AddWallSlideAbility(PlatformerPlayerPath);

        [MenuItem("Friendslop/Add Wall Jump To Core Player")]
        public static void AddToCorePlayer() => AddWallSlideAbility(CorePlayerPath);

        private static void AddWallSlideAbility(string prefabPath)
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find a prefab at '{prefabPath}'. Has it moved?");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var wallSlide = root.GetComponent<WallSlideAbility>();
                bool wasAdded = wallSlide == null;
                if (wallSlide == null)
                {
                    wallSlide = root.AddComponent<WallSlideAbility>();
                }

                bool wired = TryWireJumpEvent(root, wallSlide);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                if (wasAdded)
                {
                    Debug.Log($"[Friendslop] Added WallSlideAbility to '{prefabPath}'." +
                        (wired ? " Wired its On Jump Pressed event automatically." :
                                 " Couldn't find CoreInputHandler's jump event to copy - assign 'On Jump Pressed' on the new component by hand."));
                }
                else
                {
                    Debug.Log($"[Friendslop] '{prefabPath}' already has WallSlideAbility." +
                        (wired ? " Re-synced its On Jump Pressed event." : ""));
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Copies the GameEvent reference from CoreInputHandler's private "onJumpPressed" field onto
        /// WallSlideAbility's own field of the same name, via SerializedObject so it works regardless
        /// of either field's access modifier.
        /// </summary>
        private static bool TryWireJumpEvent(GameObject root, WallSlideAbility wallSlide)
        {
            var inputHandler = root.GetComponent<CoreInputHandler>();
            if (inputHandler == null) return false;

            var inputSO = new SerializedObject(inputHandler);
            var sourceProp = inputSO.FindProperty("onJumpPressed");
            if (sourceProp == null || sourceProp.objectReferenceValue == null) return false;

            var wallSO = new SerializedObject(wallSlide);
            var targetProp = wallSO.FindProperty("onJumpPressed");
            if (targetProp == null) return false;

            targetProp.objectReferenceValue = sourceProp.objectReferenceValue;
            wallSO.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
