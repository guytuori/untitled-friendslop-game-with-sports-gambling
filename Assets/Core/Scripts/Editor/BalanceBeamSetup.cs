using UnityEngine;
using UnityEditor;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper for <see cref="BalanceBeamAbility"/>: adds the component to a player
    /// prefab. Unlike WallSlideSetup there's no GameEvent to wire up - BalanceBeamAbility reads
    /// CoreMovement.MoveInput/JumpRequested directly, so adding the component is the whole job.
    ///
    /// Uses PrefabUtility.LoadPrefabContents / SaveAsPrefabAsset (Unity's own prefab-editing API)
    /// rather than hand-editing the .prefab file, so this can't produce a malformed prefab.
    /// Re-running is safe: if the component is already present, it's left alone.
    /// </summary>
    public static class BalanceBeamSetup
    {
        private const string PlatformerPlayerPath = "Assets/Platformer/Prefabs/[BB] PlatformerPlayer.prefab";
        private const string CorePlayerPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";

        [MenuItem("Friendslop/Add Balance Beam To Platformer Player")]
        public static void AddToPlatformerPlayer() => AddBalanceBeamAbility(PlatformerPlayerPath);

        [MenuItem("Friendslop/Add Balance Beam To Core Player")]
        public static void AddToCorePlayer() => AddBalanceBeamAbility(CorePlayerPath);

        private static void AddBalanceBeamAbility(string prefabPath)
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
                bool wasAdded = root.GetComponent<BalanceBeamAbility>() == null;
                if (wasAdded)
                {
                    root.AddComponent<BalanceBeamAbility>();
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);

                Debug.Log(wasAdded
                    ? $"[Friendslop] Added BalanceBeamAbility to '{prefabPath}'."
                    : $"[Friendslop] '{prefabPath}' already has BalanceBeamAbility.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
