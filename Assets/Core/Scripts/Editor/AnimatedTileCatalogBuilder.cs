using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps <see cref="AnimatedTileCatalog"/> (Resources/AnimatedTileCatalog) in step with the tile textures:
    /// every texture under <see cref="TilesFolder"/> named <c>name_1</c> with a <c>name_2</c> in the same
    /// folder is an animated tile. Only an underscore before the number counts (helipad1 isn't animated).
    /// Rebuilt automatically when a texture under Assets/Maps is added, changed, moved or deleted, once when
    /// the editor loads if the catalog doesn't exist yet, and via Friendslop > Maps > Rebuild Animated Tiles.
    /// </summary>
    [InitializeOnLoad]
    public static class AnimatedTileCatalogBuilder
    {
        public const string TilesFolder = "Assets/Maps";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string CatalogPath = ResourcesFolder + "/" + AnimatedTileCatalog.ResourcePath + ".asset";

        private static bool s_Building;
        private static bool s_Scheduled;

        static AnimatedTileCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<AnimatedTileCatalog>(CatalogPath) == null) Rebuild();
            };
        }

        [MenuItem("Friendslop/Maps/Rebuild Animated Tiles")]
        public static void RebuildFromMenu()
        {
            EditorUtility.DisplayDialog("Animated Tiles", Rebuild(), "OK");
        }

        internal static void ScheduleRebuild()
        {
            if (s_Building || s_Scheduled) return;
            s_Scheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_Scheduled = false;
                Rebuild();
            };
        }

        internal static bool IsWatchedPath(string path) =>
            path.StartsWith(TilesFolder + "/", StringComparison.Ordinal) && IsImage(path);

        private static bool IsImage(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" || extension == ".psd" || extension == ".bmp" || extension == ".gif";
        }

        /// <summary>Rebuilds the catalog. Returns a summary (also logged).</summary>
        public static string Rebuild()
        {
            if (s_Building) return "Already rebuilding.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Not rebuilt in play mode.";

            s_Building = true;
            try
            {
                var entries = new List<AnimatedTileCatalog.Entry>();
                if (AssetDatabase.IsValidFolder(TilesFolder))
                {
                    List<string> paths = AssetDatabase.FindAssets("t:Texture2D", new[] { TilesFolder })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Where(IsImage)
                        .Distinct()
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList();
                    var byPath = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

                    foreach (string path in paths)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(path);
                        if (!AnimatedTileCatalog.TryGetTileId(fileName, out string id)) continue;

                        string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                        string secondPath = paths.FirstOrDefault(p =>
                            string.Equals(Path.GetDirectoryName(p)?.Replace('\\', '/'), folder, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetFileNameWithoutExtension(p), id + "_2", StringComparison.OrdinalIgnoreCase));
                        if (secondPath == null || !byPath.Contains(secondPath))
                        {
                            Debug.LogWarning($"[AnimatedTiles] {path} looks like an animated tile's first frame, but there's no {id}_2 next to it - it won't animate.");
                            continue;
                        }

                        var first = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                        var second = AssetDatabase.LoadAssetAtPath<Texture2D>(secondPath);
                        if (first == null || second == null) continue;
                        entries.Add(new AnimatedTileCatalog.Entry { id = id, first = first, second = second });
                    }
                }

                EnsureFolder(ResourcesFolder);
                var catalog = AssetDatabase.LoadAssetAtPath<AnimatedTileCatalog>(CatalogPath);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<AnimatedTileCatalog>();
                    AssetDatabase.CreateAsset(catalog, CatalogPath);
                }
                catalog.SetTiles(entries);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();

                string summary = entries.Count > 0
                    ? $"Animated tiles ({entries.Count}): {string.Join(", ", entries.Select(e => e.id))}."
                    : "No animated tiles (no name_1 / name_2 texture pairs under Assets/Maps).";
                Debug.Log("[AnimatedTiles] " + summary);
                return summary;
            }
            finally
            {
                s_Building = false;
            }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    /// <summary>Triggers a rebuild when a tile texture is imported, moved or deleted.</summary>
    internal class AnimatedTileCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(AnimatedTileCatalogBuilder.IsWatchedPath))
            {
                AnimatedTileCatalogBuilder.ScheduleRebuild();
            }
        }
    }
}
