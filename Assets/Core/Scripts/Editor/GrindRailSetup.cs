using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper for <see cref="GrindRailAbility"/>: adds the component to a player
    /// prefab and wires its "On Jump Pressed" GameEvent field to the same asset already assigned on
    /// that prefab's CoreInputHandler, so jumping off a rail works immediately without any manual
    /// dragging in the Inspector. Same shape as WallSlideSetup, for the same reason - GrindRailAbility
    /// reacts to the raw jump-pressed event rather than CoreMovement.JumpRequested, since a rail has
    /// no collider (CoreMovement.IsGrounded reads false the whole time you're grinding, and
    /// CorePlayerManager's own jump handling only fires while grounded).
    ///
    /// Uses PrefabUtility.LoadPrefabContents / SaveAsPrefabAsset rather than hand-editing the .prefab
    /// file. Re-running is safe: if the component is already present, it's left as-is (not
    /// duplicated), though the event field is always re-synced from CoreInputHandler.
    /// </summary>
    public static class GrindRailSetup
    {
        private const string PlatformerPlayerPath = "Assets/Platformer/Prefabs/[BB] PlatformerPlayer.prefab";
        private const string CorePlayerPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";

        [MenuItem("Friendslop/Add Grind Rail To Platformer Player")]
        public static void AddToPlatformerPlayer() => AddGrindRailAbility(PlatformerPlayerPath);

        [MenuItem("Friendslop/Add Grind Rail To Core Player")]
        public static void AddToCorePlayer() => AddGrindRailAbility(CorePlayerPath);

        private static void AddGrindRailAbility(string prefabPath)
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
                var grindRail = root.GetComponent<GrindRailAbility>();
                bool wasAdded = grindRail == null;
                if (grindRail == null)
                {
                    grindRail = root.AddComponent<GrindRailAbility>();
                }

                bool wired = TryWireJumpEvent(root, grindRail);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                if (wasAdded)
                {
                    Debug.Log($"[Friendslop] Added GrindRailAbility to '{prefabPath}'." +
                        (wired ? " Wired its On Jump Pressed event automatically." :
                                 " Couldn't find CoreInputHandler's jump event to copy - assign 'On Jump Pressed' on the new component by hand."));
                }
                else
                {
                    Debug.Log($"[Friendslop] '{prefabPath}' already has GrindRailAbility." +
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
        /// GrindRailAbility's own field of the same name, via SerializedObject so it works regardless
        /// of either field's access modifier. Same approach as WallSlideSetup.TryWireJumpEvent.
        /// </summary>
        private static bool TryWireJumpEvent(GameObject root, GrindRailAbility grindRail)
        {
            var inputHandler = root.GetComponent<CoreInputHandler>();
            if (inputHandler == null) return false;

            var inputSO = new SerializedObject(inputHandler);
            var sourceProp = inputSO.FindProperty("onJumpPressed");
            if (sourceProp == null || sourceProp.objectReferenceValue == null) return false;

            var grindSO = new SerializedObject(grindRail);
            var targetProp = grindSO.FindProperty("onJumpPressed");
            if (targetProp == null) return false;

            targetProp.objectReferenceValue = sourceProp.objectReferenceValue;
            grindSO.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
