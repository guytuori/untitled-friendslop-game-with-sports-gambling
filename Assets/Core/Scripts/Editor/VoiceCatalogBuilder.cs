using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps <see cref="VoiceCatalog"/> (Resources/VoiceCatalog) in step with the announcer clips: every audio
    /// file in <see cref="VoiceFolder"/>, by file name. Rebuilt automatically when a clip there is added,
    /// changed, moved or deleted, once when the editor loads if the catalog doesn't exist yet, and via
    /// Friendslop > Audio > Rebuild Voice Clips.
    /// </summary>
    [InitializeOnLoad]
    public static class VoiceCatalogBuilder
    {
        public const string VoiceFolder = "Assets/Core/Audio/Voice";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string CatalogPath = ResourcesFolder + "/" + VoiceCatalog.ResourcePath + ".asset";

        private static bool s_Building;
        private static bool s_Scheduled;

        static VoiceCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                // Missing, or out of step with the folder (a clip added while in play mode or with Unity closed).
                var existing = AssetDatabase.LoadAssetAtPath<VoiceCatalog>(CatalogPath);
                if (existing == null || !MatchesFolder(existing)) Rebuild();
            };
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredEditMode || !s_ChangedInPlayMode) return;
                s_ChangedInPlayMode = false;
                ScheduleRebuild();
            };
        }

        private static bool s_ChangedInPlayMode;

        /// <summary>True if the catalog lists exactly the clips in the Voice folder (by name), all still there.</summary>
        private static bool MatchesFolder(VoiceCatalog catalog)
        {
            var inFolder = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (AssetDatabase.IsValidFolder(VoiceFolder))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:AudioClip", new[] { VoiceFolder }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (IsAudio(path)) inFolder.Add(Path.GetFileNameWithoutExtension(path));
                }
            }
            var inCatalog = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VoiceCatalog.Entry entry in catalog.Clips)
            {
                if (entry == null || entry.clip == null) return false;
                inCatalog.Add(entry.id);
            }
            return inFolder.SetEquals(inCatalog);
        }

        [MenuItem("Friendslop/Audio/Rebuild Voice Clips")]
        public static void RebuildFromMenu()
        {
            EditorUtility.DisplayDialog("Voice Clips", Rebuild(), "OK");
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
            path.StartsWith(VoiceFolder + "/", StringComparison.Ordinal) && IsAudio(path);

        private static bool IsAudio(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".mp3" || extension == ".wav" || extension == ".ogg" || extension == ".aif" || extension == ".aiff";
        }

        /// <summary>Rebuilds the catalog. Returns a summary (also logged).</summary>
        public static string Rebuild()
        {
            if (s_Building) return "Already rebuilding.";
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                s_ChangedInPlayMode = true; // rebuilt as soon as play mode ends
                return "Not rebuilt in play mode - it will be when play mode ends.";
            }

            s_Building = true;
            try
            {
                var entries = new List<VoiceCatalog.Entry>();
                if (AssetDatabase.IsValidFolder(VoiceFolder))
                {
                    IEnumerable<string> paths = AssetDatabase.FindAssets("t:AudioClip", new[] { VoiceFolder })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Where(IsAudio)
                        .Distinct()
                        .OrderBy(p => p, StringComparer.Ordinal);
                    foreach (string path in paths)
                    {
                        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                        if (clip == null) continue;
                        entries.Add(new VoiceCatalog.Entry { id = Path.GetFileNameWithoutExtension(path), clip = clip });
                    }
                }

                EnsureFolder(ResourcesFolder);
                var catalog = AssetDatabase.LoadAssetAtPath<VoiceCatalog>(CatalogPath);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<VoiceCatalog>();
                    AssetDatabase.CreateAsset(catalog, CatalogPath);
                }
                catalog.SetClips(entries);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();

                string summary = entries.Count > 0
                    ? $"Voice clips ({entries.Count}): {string.Join(", ", entries.Select(e => e.id))}."
                    : $"No voice clips in {VoiceFolder}.";
                Debug.Log("[Voice] " + summary);
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

    /// <summary>Triggers a rebuild when a voice clip is imported, moved or deleted.</summary>
    internal class VoiceCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(VoiceCatalogBuilder.IsWatchedPath))
            {
                VoiceCatalogBuilder.ScheduleRebuild();
            }
        }
    }
}
