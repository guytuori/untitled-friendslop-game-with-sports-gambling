using System;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Makes Resources/ItemCatalog (see <see cref="ItemCatalog"/>) with the default items the first time, and
    /// afterwards only adds items it doesn't have yet and fills in the item box model if it's empty - edits
    /// made in its Inspector (chances, prices, tiers, durations, icons) are never overwritten. Runs when the
    /// editor loads and when Assets/Maps/item_box.glb is imported.
    /// </summary>
    [InitializeOnLoad]
    public static class ItemCatalogBuilder
    {
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string AssetPath = ResourcesFolder + "/" + ItemCatalog.ResourcePath + ".asset";

        private static bool s_Scheduled;

        static ItemCatalogBuilder()
        {
            EditorApplication.delayCall += Rebuild;
        }

        internal static bool IsWatchedPath(string path) =>
            string.Equals(path, ItemCatalog.DefaultItemBoxModelPath, StringComparison.OrdinalIgnoreCase);

        internal static void Schedule()
        {
            if (s_Scheduled) return;
            s_Scheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_Scheduled = false;
                Rebuild();
            };
        }

        [MenuItem("Friendslop/Items/Select Item List")]
        private static void SelectCatalog()
        {
            Rebuild();
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(AssetPath);
            if (catalog != null)
            {
                Selection.activeObject = catalog;
                EditorGUIUtility.PingObject(catalog);
            }
        }

        public static void Rebuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (!AssetDatabase.IsValidFolder(ResourcesFolder)) AssetDatabase.CreateFolder("Assets/Core", "Resources");

            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>(AssetPath);
            bool created = false;
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ItemCatalog>();
                AssetDatabase.CreateAsset(catalog, AssetPath);
                created = true;
            }

            var boxModel = AssetDatabase.LoadAssetAtPath<GameObject>(ItemCatalog.DefaultItemBoxModelPath);
            if (catalog.EditorTopUp(boxModel) || created)
            {
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Items] {(created ? "Made" : "Updated")} {AssetPath} (item box model: {(boxModel != null ? boxModel.name : "missing - " + ItemCatalog.DefaultItemBoxModelPath)}).", catalog);
            }
        }
    }

    internal class ItemCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string[] list in new[] { imported, moved })
            {
                foreach (string path in list)
                {
                    if (!ItemCatalogBuilder.IsWatchedPath(path)) continue;
                    ItemCatalogBuilder.Schedule();
                    return;
                }
            }
        }
    }
}
