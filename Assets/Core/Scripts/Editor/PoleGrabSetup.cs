using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper for <see cref="PoleGrabAbility"/>: adds the component to a player prefab
    /// and wires its "On Jump Pressed" GameEvent field to the same asset already assigned on that
    /// prefab's CoreInputHandler, so jumping off a pole works immediately without any manual dragging
    /// in the Inspector. Same shape as WallSlideSetup/GrindRailSetup, for the same reason -
    /// PoleGrabAbility reacts to the raw jump-pressed event rather than CoreMovement.JumpRequested,
    /// since a pole leaves the player ungrounded the whole time they're attached.
    ///
    /// Uses PrefabUtility.LoadPrefabContents / SaveAsPrefabAsset rather than hand-editing the .prefab
    /// file. Re-running is safe: if the component is already present, it's left as-is (not
    /// duplicated), though the event field is always re-synced from CoreInputHandler.
    ///
    /// Doesn't wire the Grab GameEvent itself - that's a single BoolEvent asset shared across
    /// CoreInputHandler and CorePlayerManager (see onGrabStateChanged on each), not something
    /// PoleGrabAbility/WallClimbAbility hold a reference to directly; they read CoreMovement.IsGrabHeld
    /// instead. Make sure both of those fields point at the same BoolEvent asset (create one via
    /// Game Events > Bool Event if the project doesn't have one yet, e.g. "OnGrabStateChanged").
    /// </summary>
    public static class PoleGrabSetup
    {
        private const string PlatformerPlayerPath = "Assets/Platformer/Prefabs/[BB] PlatformerPlayer.prefab";
        private const string CorePlayerPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";

        [MenuItem("Friendslop/Add Pole Grab To Platformer Player")]
        public static void AddToPlatformerPlayer() => AddPoleGrabAbility(PlatformerPlayerPath);

        [MenuItem("Friendslop/Add Pole Grab To Core Player")]
        public static void AddToCorePlayer() => AddPoleGrabAbility(CorePlayerPath);

        private static void AddPoleGrabAbility(string prefabPath)
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
                var poleGrab = root.GetComponent<PoleGrabAbility>();
                bool wasAdded = poleGrab == null;
                if (poleGrab == null)
                {
                    poleGrab = root.AddComponent<PoleGrabAbility>();
                }

                bool wired = TryWireJumpEvent(root, poleGrab);

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                if (wasAdded)
                {
                    Debug.Log($"[Friendslop] Added PoleGrabAbility to '{prefabPath}'." +
                        (wired ? " Wired its On Jump Pressed event automatically." :
                                 " Couldn't find CoreInputHandler's jump event to copy - assign 'On Jump Pressed' on the new component by hand.") +
                        " Also make sure CoreInputHandler's and CorePlayerManager's 'On Grab State Changed' fields point at the same BoolEvent asset (not auto-wired by this tool).");
                }
                else
                {
                    Debug.Log($"[Friendslop] '{prefabPath}' already has PoleGrabAbility." +
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
        /// PoleGrabAbility's own field of the same name, via SerializedObject so it works regardless of
        /// either field's access modifier. Same approach as WallSlideSetup/GrindRailSetup.
        /// </summary>
        private static bool TryWireJumpEvent(GameObject root, PoleGrabAbility poleGrab)
        {
            var inputHandler = root.GetComponent<CoreInputHandler>();
            if (inputHandler == null) return false;

            var inputSO = new SerializedObject(inputHandler);
            var sourceProp = inputSO.FindProperty("onJumpPressed");
            if (sourceProp == null || sourceProp.objectReferenceValue == null) return false;

            var poleSO = new SerializedObject(poleGrab);
            var targetProp = poleSO.FindProperty("onJumpPressed");
            if (targetProp == null) return false;

            targetProp.objectReferenceValue = sourceProp.objectReferenceValue;
            poleSO.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }
    }
}
