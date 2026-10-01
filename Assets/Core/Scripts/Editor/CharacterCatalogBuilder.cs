using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps the CharacterCatalog (Resources/CharacterCatalog.asset - the list the Change Profile screen's
    /// character selector shows) in step with the models in <see cref="CharactersFolder"/>, so adding a
    /// character is just dropping its model in that folder:
    ///   - every model (FBX or prefab) in the folder becomes an entry, sorted by name, with its file name as
    ///     both id and display name;
    ///   - an FBX that isn't set up as a Humanoid yet is set up the same way Friendslop > Player Model does
    ///     it (bone map for the BlenderProjects rigs, scaled to the player's height) - models that can't be
    ///     made Humanoid are skipped with a warning, since the shared animations need it;
    ///   - each gets a front-view thumbnail of its T-pose, rendered into <see cref="ThumbnailFolder"/> (only
    ///     re-rendered when the model changes).
    /// Runs automatically whenever something in the folder is imported, moved or deleted, and once when the
    /// editor loads if the catalog doesn't exist yet. Friendslop > Characters > Rebuild Character List
    /// forces a full rebuild (including thumbnails).
    /// </summary>
    [InitializeOnLoad]
    public static class CharacterCatalogBuilder
    {
        public const string CharactersFolder = "Assets/Core/Art/Models/Characters";
        public const string ThumbnailFolder = CharactersFolder + "/Thumbnails";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string CatalogPath = ResourcesFolder + "/" + CharacterCatalog.ResourcePath + ".asset";
        private const int ThumbnailSize = 256;

        private static bool s_Building;
        private static bool s_Scheduled;

        static CharacterCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath) == null) Rebuild(forceThumbnails: false);
            };
        }

        [MenuItem("Friendslop/Characters/Rebuild Character List")]
        public static void RebuildFromMenu()
        {
            string summary = Rebuild(forceThumbnails: true);
            EditorUtility.DisplayDialog("Characters", summary, "OK");
        }

        /// <summary>Called by <see cref="CharacterFolderWatcher"/> when anything in the folder changes.</summary>
        internal static void ScheduleRebuild()
        {
            if (s_Building || s_Scheduled) return;
            s_Scheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_Scheduled = false;
                Rebuild(forceThumbnails: false);
            };
        }

        internal static bool IsInCharactersFolder(string path) =>
            path.StartsWith(CharactersFolder + "/", StringComparison.Ordinal) &&
            !path.StartsWith(ThumbnailFolder + "/", StringComparison.Ordinal);

        /// <summary>Rebuilds the catalog. Returns a one-paragraph summary (also logged).</summary>
        public static string Rebuild(bool forceThumbnails)
        {
            if (s_Building) return "Already rebuilding.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Not rebuilt in play mode.";

            s_Building = true;
            var log = new StringBuilder();
            try
            {
                if (!AssetDatabase.IsValidFolder(CharactersFolder))
                {
                    return Log($"No {CharactersFolder} folder - the character list is empty.");
                }

                EnsureFolder(ResourcesFolder);
                EnsureFolder(ThumbnailFolder);

                var entries = new List<CharacterCatalog.Entry>();
                foreach (string path in FindModelPaths())
                {
                    CharacterCatalog.Entry entry = BuildEntry(path, forceThumbnails, log);
                    if (entry != null) entries.Add(entry);
                }

                CharacterCatalog catalog = AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<CharacterCatalog>();
                    AssetDatabase.CreateAsset(catalog, CatalogPath);
                }

                catalog.SetCharacters(entries);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();

                string names = entries.Count > 0 ? string.Join(", ", entries.Select(e => e.id)) : "none";
                return Log($"Character list rebuilt: {entries.Count} character(s) ({names}).\n{log}".TrimEnd());
            }
            finally
            {
                s_Building = false;
            }
        }

        private static string Log(string message)
        {
            Debug.Log($"[Characters] {message}");
            return message;
        }

        private static IEnumerable<string> FindModelPaths()
        {
            return AssetDatabase.FindAssets("t:GameObject", new[] { CharactersFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(IsInCharactersFolder)
                .Distinct()
                .OrderBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        private static CharacterCatalog.Entry BuildEntry(string path, bool forceThumbnail, StringBuilder log)
        {
            string id = Path.GetFileNameWithoutExtension(path);

            // FBX straight from Blender: make it a Humanoid the same way the Player Model tool does.
            if (AssetImporter.GetAtPath(path) is ModelImporter importer && !HasHumanoidAvatar(path))
            {
                if (!PlayerModelSwapTool.TryConfigureHumanoidModel(path, PlayerModelSwapTool.GetPlayerHeight(), log, out string error))
                {
                    Debug.LogWarning($"[Characters] Skipped {path}: {error}");
                    log.AppendLine($"Skipped {id}: {error}");
                    return null;
                }
            }

            if (!HasHumanoidAvatar(path))
            {
                string reason = $"Skipped {id}: it has no valid Humanoid avatar (the player animations need one).";
                Debug.LogWarning($"[Characters] {reason} ({path})");
                log.AppendLine(reason);
                return null;
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return new CharacterCatalog.Entry
            {
                id = id,
                displayName = id,
                model = model,
                thumbnail = GetOrRenderThumbnail(path, model, forceThumbnail)
            };
        }

        private static bool HasHumanoidAvatar(string path)
        {
            Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            if (avatar == null)
            {
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var animator = model != null ? model.GetComponentInChildren<Animator>(true) : null;
                avatar = animator != null ? animator.avatar : null;
            }
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        // =====================================================================================
        // Thumbnails
        // =====================================================================================

        private static Texture2D GetOrRenderThumbnail(string modelPath, GameObject model, bool force)
        {
            string thumbnailPath = $"{ThumbnailFolder}/{Path.GetFileNameWithoutExtension(modelPath)}.png";

            bool upToDate = File.Exists(thumbnailPath) &&
                            File.GetLastWriteTimeUtc(thumbnailPath) >= File.GetLastWriteTimeUtc(modelPath) &&
                            File.GetLastWriteTimeUtc(thumbnailPath) >= File.GetLastWriteTimeUtc(modelPath + ".meta");
            if (!force && upToDate)
            {
                return AssetDatabase.LoadAssetAtPath<Texture2D>(thumbnailPath);
            }

            Texture2D rendered = RenderThumbnail(model);
            if (rendered == null) return AssetDatabase.LoadAssetAtPath<Texture2D>(thumbnailPath);

            File.WriteAllBytes(thumbnailPath, rendered.EncodeToPNG());
            Object.DestroyImmediate(rendered);
            AssetDatabase.ImportAsset(thumbnailPath, ImportAssetOptions.ForceUpdate);

            if (AssetImporter.GetAtPath(thumbnailPath) is TextureImporter textureImporter)
            {
                textureImporter.textureType = TextureImporterType.Default;
                textureImporter.mipmapEnabled = false;
                textureImporter.alphaIsTransparency = true;
                textureImporter.wrapMode = TextureWrapMode.Clamp;
                textureImporter.npotScale = TextureImporterNPOTScale.None;
                textureImporter.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Texture2D>(thumbnailPath);
        }

        /// <summary>Front view (+Z side, which is where the player models face) of the model in its rest T-pose.</summary>
        private static Texture2D RenderThumbnail(GameObject model)
        {
            var preview = new PreviewRenderUtility();
            try
            {
                Camera camera = preview.camera;
                camera.fieldOfView = 25f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 1f);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 100f;

                preview.lights[0].intensity = 1.2f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35f, 160f, 0f);
                preview.lights[1].intensity = 0.7f;
                preview.lights[1].transform.rotation = Quaternion.Euler(20f, -40f, 0f);
                preview.ambientColor = new Color(0.45f, 0.45f, 0.45f);

                GameObject instance = preview.InstantiatePrefabInScene(model);
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return null;

                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);

                // Far enough back that the whole T-pose (wider than it is tall) fits in the square image.
                float halfSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.08f;
                float distance = halfSize / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) + bounds.extents.z;
                camera.transform.position = bounds.center + Vector3.forward * distance;
                camera.transform.LookAt(bounds.center);

                preview.BeginStaticPreview(new Rect(0, 0, ThumbnailSize, ThumbnailSize));
                preview.Render(true);
                return preview.EndStaticPreview();
            }
            finally
            {
                preview.Cleanup();
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

    /// <summary>Triggers a catalog rebuild when anything in the characters folder is imported, moved or deleted.</summary>
    internal class CharacterFolderWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(CharacterCatalogBuilder.IsInCharactersFolder))
            {
                CharacterCatalogBuilder.ScheduleRebuild();
            }
        }
    }
}
