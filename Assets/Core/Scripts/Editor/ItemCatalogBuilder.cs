using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Makes Resources/ItemCatalog (see <see cref="ItemCatalog"/>) with the default items the first time, and
    /// afterwards only adds items it doesn't have yet, fills in the item box model and any empty icon, and
    /// applies renames to names still at their old default (ItemCatalog.EditorTopUp) - other edits made in its
    /// Inspector (chances, prices, tiers, durations, icons you picked) are never overwritten.
    ///
    /// Icons are found by file name anywhere under Assets (ItemCatalog.IconFileName - e.g. RocketShoes.png,
    /// 1Up.png, SpringBoots.png), and given UI-friendly import settings: alpha is transparency, no mipmaps, no
    /// power-of-two rescale, max 512.
    ///
    /// Use sounds are found the same way in Assets/Core/Audio/Clips (ItemCatalog.UseSoundFileName - points.mp3,
    /// item_1up.mp3, item_rocketboot.mp3...) and fill each item's empty use sound.
    ///
    /// Runs when the editor loads, and when Assets/Maps/item_box.glb, an icon png or a use sound is imported.
    /// </summary>
    [InitializeOnLoad]
    public static class ItemCatalogBuilder
    {
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string AssetPath = ResourcesFolder + "/" + ItemCatalog.ResourcePath + ".asset";

        private static bool s_Scheduled;
        private static HashSet<string> s_IconNames;
        private static HashSet<string> s_SoundNames;
        private const string SoundFolder = "Assets/Core/Audio/Clips";

        static ItemCatalogBuilder()
        {
            EditorApplication.delayCall += Rebuild;
        }

        private static HashSet<string> IconNames
        {
            get
            {
                if (s_IconNames == null)
                {
                    s_IconNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ItemId id in Enum.GetValues(typeof(ItemId)))
                    {
                        string name = ItemCatalog.IconFileName(id);
                        if (!string.IsNullOrEmpty(name)) s_IconNames.Add(name);
                    }
                }
                return s_IconNames;
            }
        }

        private static HashSet<string> SoundNames
        {
            get
            {
                if (s_SoundNames == null)
                {
                    s_SoundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (ItemId id in Enum.GetValues(typeof(ItemId)))
                    {
                        string name = ItemCatalog.UseSoundFileName(id);
                        if (!string.IsNullOrEmpty(name)) s_SoundNames.Add(name);
                    }
                }
                return s_SoundNames;
            }
        }

        internal static bool IsWatchedPath(string path)
        {
            if (string.Equals(path, ItemCatalog.DefaultItemBoxModelPath, StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(SoundFolder + "/", StringComparison.OrdinalIgnoreCase) && SoundNames.Contains(Path.GetFileNameWithoutExtension(path))) return true;
            return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && IconNames.Contains(Path.GetFileNameWithoutExtension(path));
        }

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
            if (catalog.EditorTopUp(boxModel, FindIcon, FindSound) || created)
            {
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                Debug.Log($"[Items] {(created ? "Made" : "Updated")} {AssetPath} (item box model: {(boxModel != null ? boxModel.name : "missing - " + ItemCatalog.DefaultItemBoxModelPath)}).", catalog);
            }
        }

        /// <summary>The png called <paramref name="fileName"/> anywhere under Assets (with icon import settings), or null.</summary>
        private static Texture2D FindIcon(string fileName)
        {
            foreach (string guid in AssetDatabase.FindAssets($"{fileName} t:Texture2D", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;

                ApplyIconImportSettings(path);
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            return null;
        }

        /// <summary>The sound called <paramref name="fileName"/> (any audio type) in Assets/Core/Audio/Clips, or null.</summary>
        private static AudioClip FindSound(string fileName)
        {
            if (!AssetDatabase.IsValidFolder(SoundFolder)) return null;
            foreach (string guid in AssetDatabase.FindAssets($"{fileName} t:AudioClip", new[] { SoundFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetFileNameWithoutExtension(path), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                return AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            }
            return null;
        }

        private static void ApplyIconImportSettings(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) return;

            bool changed = false;
            if (importer.textureType != TextureImporterType.Default) { importer.textureType = TextureImporterType.Default; changed = true; }
            if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; changed = true; }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; changed = true; }
            if (importer.npotScale != TextureImporterNPOTScale.None) { importer.npotScale = TextureImporterNPOTScale.None; changed = true; }
            if (importer.maxTextureSize != 512) { importer.maxTextureSize = 512; changed = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp) { importer.wrapMode = TextureWrapMode.Clamp; changed = true; }
            if (changed) importer.SaveAndReimport();
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
