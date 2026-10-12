using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps <see cref="DinosaurCatalog"/> (Resources/DinosaurCatalog) in step with the dinosaur models: every
    /// model under <see cref="DinosaurFolder"/>, with its mesh and its idle / walk animation (the clips whose
    /// names contain "idle" and "walk", e.g. "TRex_Idle" / "TRex_Walk"), plus <see cref="SoundPath"/> as the
    /// Interact sound. Rebuilt automatically when anything in that folder or the sound changes, once when the
    /// editor loads if the catalog doesn't exist yet, and via Friendslop > Maps > Rebuild Dinosaur List.
    ///
    /// To add a dinosaur: put its model (with idle and walk animations) in the folder, and place it in a map
    /// prefab. Nothing needs adding to the dinosaur itself.
    /// </summary>
    [InitializeOnLoad]
    public static class DinosaurCatalogBuilder
    {
        public const string DinosaurFolder = "Assets/_models/Dinosaur Animated Pack";
        public const string SoundPath = "Assets/Core/Audio/Clips/dinosaur.mp3";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string CatalogPath = ResourcesFolder + "/" + DinosaurCatalog.ResourcePath + ".asset";

        private static bool s_Building;
        private static bool s_Scheduled;

        static DinosaurCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<DinosaurCatalog>(CatalogPath) == null) Rebuild();
            };
        }

        [MenuItem("Friendslop/Maps/Rebuild Dinosaur List")]
        public static void RebuildFromMenu()
        {
            EditorUtility.DisplayDialog("Dinosaurs", Rebuild(), "OK");
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
            path.StartsWith(DinosaurFolder + "/", StringComparison.Ordinal) ||
            string.Equals(path, SoundPath, StringComparison.OrdinalIgnoreCase);

        /// <summary>Rebuilds the catalog. Returns a summary (also logged).</summary>
        public static string Rebuild()
        {
            if (s_Building) return "Already rebuilding.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Not rebuilt in play mode.";

            s_Building = true;
            try
            {
                var entries = new List<DinosaurCatalog.Entry>();
                var problems = new List<string>();

                if (AssetDatabase.IsValidFolder(DinosaurFolder))
                {
                    List<string> paths = AssetDatabase.FindAssets("t:Model", new[] { DinosaurFolder })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Distinct()
                        .OrderBy(p => p, StringComparer.Ordinal)
                        .ToList();

                    foreach (string path in paths)
                    {
                        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (model == null) continue;

                        Mesh mesh = model.GetComponentInChildren<SkinnedMeshRenderer>(true)?.sharedMesh;
                        AnimationClip[] clips = AssetDatabase.LoadAllAssetsAtPath(path)
                            .OfType<AnimationClip>()
                            .Where(c => !c.name.StartsWith("__preview__", StringComparison.Ordinal))
                            .ToArray();
                        AnimationClip idle = FindClip(clips, "idle");
                        AnimationClip walk = FindClip(clips, "walk");

                        string id = Path.GetFileNameWithoutExtension(path);
                        if (mesh == null) { problems.Add($"{id}: no skinned mesh - skipped."); continue; }
                        if (walk == null) problems.Add($"{id}: no walk animation - it'll stand still.");
                        if (idle == null) problems.Add($"{id}: no idle animation.");

                        entries.Add(new DinosaurCatalog.Entry { id = id, model = model, mesh = mesh, idle = idle, walk = walk });
                    }
                }

                var sound = AssetDatabase.LoadAssetAtPath<AudioClip>(SoundPath);
                if (sound == null) problems.Add($"No {SoundPath} - Interact won't make a sound.");

                EnsureFolder(ResourcesFolder);
                var catalog = AssetDatabase.LoadAssetAtPath<DinosaurCatalog>(CatalogPath);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<DinosaurCatalog>();
                    AssetDatabase.CreateAsset(catalog, CatalogPath);
                }
                catalog.Set(entries, sound);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();

                string summary = entries.Count > 0
                    ? $"Dinosaurs ({entries.Count}): {string.Join(", ", entries.Select(e => e.id))}."
                    : $"No dinosaur models in {DinosaurFolder}.";
                foreach (string problem in problems) Debug.LogWarning("[Dinosaurs] " + problem);
                Debug.Log("[Dinosaurs] " + summary);
                return problems.Count > 0 ? summary + "\n\n" + string.Join("\n", problems) : summary;
            }
            finally
            {
                s_Building = false;
            }
        }

        /// <summary>The clip named "..._walk" (or failing that, the first containing "walk"), ignoring case.</summary>
        private static AnimationClip FindClip(AnimationClip[] clips, string word)
        {
            return clips.FirstOrDefault(c => c.name.EndsWith("_" + word, StringComparison.OrdinalIgnoreCase))
                ?? clips.FirstOrDefault(c => c.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    /// <summary>Triggers a rebuild when a dinosaur model or the dinosaur sound is imported, moved or deleted.</summary>
    internal class DinosaurCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(DinosaurCatalogBuilder.IsWatchedPath))
            {
                DinosaurCatalogBuilder.ScheduleRebuild();
            }
        }
    }
}
