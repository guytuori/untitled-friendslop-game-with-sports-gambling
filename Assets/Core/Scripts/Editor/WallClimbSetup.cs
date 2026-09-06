using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper for <see cref="WallClimbAbility"/>: adds the component to a player prefab
    /// and wires its "On Jump Pressed" GameEvent field to the same asset already assigned on that
    /// prefab's CoreInputHandler, so wall climbing/jumping works immediately without any manual
    /// dragging in the Inspector.
    ///
    /// (Recreated under this name after the old WallSlideSetup.cs - which this repurposed - was
    /// deleted along with WallSlideAbility.cs. Both player prefabs already had WallClimbAbility added
    /// and wired before that deletion, so this tool isn't needed to fix anything currently broken -
    /// it's here for adding WallClimbAbility to a new prefab, or re-syncing the jump event later.)
    ///
    /// Uses PrefabUtility.LoadPrefabContents / SaveAsPrefabAsset (Unity's own prefab-editing API)
    /// rather than hand-editing the .prefab file, so this can't produce a malformed prefab.
    ///
    /// Re-running is safe: if the component is already present, it's left as-is (not duplicated),
    /// though the event field is always re-synced from CoreInputHandler.
    /// </summary>
    public static class WallClimbSetup
    {
        private const string PlatformerPlayerPath = "Assets/Platformer/Prefabs/[BB] PlatformerPlayer.prefab";
        private const string CorePlayerPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";

        [MenuItem("Friendslop/Add Wall Climb To Platformer Player")]
        public static void AddToPlatformerPlayer() => AddWallClimbAbility(PlatformerPlayerPath);

        [MenuItem("Friendslop/Add Wall Climb To Core Player")]
        public static void AddToCorePlayer() => AddWallClimbAbility(CorePlayerPath);

        private static void AddWallClimbAbility(string prefabPath)
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
                var wallClimb = root.GetComponent<WallClimbAbility>();
                bool wasAdded = wallClimb == null;
                if (wallClimb == null)
                {
                    wallClimb = root.AddComponent<WallClimbAbility>();
                }

                bool wired = TryWireJumpEvent(root, wallClimb);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                if (wasAdded)
                {
                    Debug.Log($"[Friendslop] Added WallClimbAbility to '{prefabPath}'." +
                        (wired ? " Wired its On Jump Pressed event automatically." :
                                 " Couldn't find CoreInputHandler's jump event to copy - assign 'On Jump Pressed' on the new component by hand."));
                }
                else
                {
                    Debug.Log($"[Friendslop] '{prefabPath}' already has WallClimbAbility." +
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
        /// WallClimbAbility's own field of the same name, via SerializedObject so it works regardless
        /// of either field's access modifier.
        /// </summary>
        private static bool TryWireJumpEvent(GameObject root, WallClimbAbility wallClimb)
        {
            var inputHandler = root.GetComponent<CoreInputHandler>();
            if (inputHandler == null) return false;

            var inputSO = new SerializedObject(inputHandler);
            var sourceProp = inputSO.FindProperty("onJumpPressed");
            if (sourceProp == null || sourceProp.objectReferenceValue == null) return false;

            var wallSO = new SerializedObject(wallClimb);
            var targetProp = wallSO.FindProperty("onJumpPressed");
            if (targetProp == null) return false;

            targetProp.objectReferenceValue = sourceProp.objectReferenceValue;
            wallSO.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
