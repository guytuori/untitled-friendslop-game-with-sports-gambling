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
    /// Looks after the hand-edited CharacterCatalog (Resources/CharacterCatalog.asset - the list the Change
    /// Profile screen's character selector shows). It never adds, removes or reorders characters - the list
    /// is whatever's typed into it - it only fills in what each entry left blank:
    ///   - id: the model's file name;
    ///   - display name: the id with spaces ("FastFoodGuy" -> "Fast Food Guy");
    ///   - thumbnail: a front-view render of the model's T-pose, saved in <see cref="ThumbnailFolder"/> as
    ///     "{id}.png". Thumbnails it rendered itself are re-rendered when the model (or its import settings)
    ///     changes; a picture assigned by hand from anywhere else is left alone.
    /// It also sets an FBX up as a Humanoid if it isn't one yet, the same way Friendslop > Player Model does
    /// (bone map for the BlenderProjects rigs, auto-mapping for others), since the shared animations need it.
    /// Problems (no model, duplicate ids, not Humanoid) are logged as warnings; those entries just aren't
    /// offered in game.
    ///
    /// Runs automatically when the list is saved or one of its models is reimported. Menus (Friendslop >
    /// Characters): Edit Character List (selects it), Refresh Character List, Re-render Character Thumbnails.
    /// The list's Inspector has the same buttons.
    /// </summary>
    [InitializeOnLoad]
    public static class CharacterCatalogBuilder
    {
        public const string ThumbnailFolder = "Assets/Core/Art/Models/Characters/Thumbnails";
        public const string ResourcesFolder = "Assets/Core/Resources";
        public const string CatalogPath = ResourcesFolder + "/" + CharacterCatalog.ResourcePath + ".asset";
        // Rendered big enough for the Change Profile screen's double-size preview (192 px) to stay sharp at
        // 2x UI scale; the 96 px list cells use the mipmaps.
        private const int ThumbnailSize = 384;

        private static bool s_Building;
        private static bool s_Scheduled;
        private static HashSet<string> s_ModelPaths;

        static CharacterCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath) == null)
                {
                    Debug.LogWarning($"[Characters] There's no character list at {CatalogPath} - use Friendslop > Characters > Edit Character List to make one.");
                }
            };
        }

        // =====================================================================================
        // Menus
        // =====================================================================================

        [MenuItem("Friendslop/Characters/Edit Character List")]
        public static void SelectCatalog()
        {
            CharacterCatalog catalog = LoadOrCreateCatalog();
            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        [MenuItem("Friendslop/Characters/Refresh Character List")]
        public static void RefreshFromMenu()
        {
            EditorUtility.DisplayDialog("Characters", Refresh(forceThumbnails: false), "OK");
        }

        [MenuItem("Friendslop/Characters/Re-render Character Thumbnails")]
        public static void RerenderFromMenu()
        {
            EditorUtility.DisplayDialog("Characters", Refresh(forceThumbnails: true), "OK");
        }

        // =====================================================================================
        // Automatic refresh
        // =====================================================================================

        /// <summary>The list itself, or a model it uses.</summary>
        internal static bool IsWatchedPath(string path)
        {
            if (path == CatalogPath) return true;
            if (s_ModelPaths == null) CacheModelPaths(AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath));
            return s_ModelPaths.Contains(path);
        }

        internal static void ScheduleRefresh()
        {
            if (s_Building || s_Scheduled) return;
            s_Scheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_Scheduled = false;
                Refresh(forceThumbnails: false);
            };
        }

        private static void CacheModelPaths(CharacterCatalog catalog)
        {
            s_ModelPaths = new HashSet<string>(StringComparer.Ordinal);
            if (catalog == null) return;
            foreach (CharacterCatalog.Entry entry in catalog.Characters)
            {
                if (entry?.model != null) s_ModelPaths.Add(AssetDatabase.GetAssetPath(entry.model));
            }
        }

        // =====================================================================================
        // Refresh
        // =====================================================================================

        /// <summary>Fills in blank ids, names and thumbnails and checks every entry. Returns a summary (also logged).</summary>
        public static string Refresh(bool forceThumbnails)
        {
            if (s_Building) return "Already refreshing.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Not refreshed in play mode.";

            s_Building = true;
            var problems = new StringBuilder();
            try
            {
                CharacterCatalog catalog = LoadOrCreateCatalog();
                List<CharacterCatalog.Entry> entries = catalog.EditableCharacters;
                bool changed = false;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                int usable = 0;

                for (int i = 0; i < entries.Count; i++)
                {
                    CharacterCatalog.Entry entry = entries[i];
                    if (entry == null) continue;

                    string label = $"Entry {i}" + (string.IsNullOrWhiteSpace(entry.id) ? "" : $" ({entry.id})");
                    if (entry.model == null)
                    {
                        problems.AppendLine($"{label} has no model - it won't be offered.");
                        continue;
                    }

                    string modelPath = AssetDatabase.GetAssetPath(entry.model);

                    if (string.IsNullOrWhiteSpace(entry.id))
                    {
                        entry.id = Path.GetFileNameWithoutExtension(modelPath);
                        changed = true;
                    }
                    if (string.IsNullOrWhiteSpace(entry.displayName))
                    {
                        entry.displayName = ObjectNames.NicifyVariableName(entry.id);
                        changed = true;
                    }
                    if (!ids.Add(entry.id))
                    {
                        problems.AppendLine($"Entry {i}: id '{entry.id}' is already used by an earlier entry - only the first one is offered.");
                        continue;
                    }

                    if (!EnsureHumanoid(modelPath, out string humanoidError))
                    {
                        problems.AppendLine($"{entry.id}: {humanoidError}");
                    }

                    if (NeedsThumbnail(entry, modelPath, forceThumbnails))
                    {
                        Texture2D thumbnail = RenderThumbnailAsset(entry.id, entry.model);
                        if (thumbnail != null && thumbnail != entry.thumbnail)
                        {
                            entry.thumbnail = thumbnail;
                            changed = true;
                        }
                    }

                    usable++;
                }

                if (changed)
                {
                    EditorUtility.SetDirty(catalog);
                    AssetDatabase.SaveAssetIfDirty(catalog);
                }
                CacheModelPaths(catalog);

                string summary = $"Character list: {usable} character(s) ready ({string.Join(", ", catalog.GetSelectable().Select(e => e.id))}).";
                if (problems.Length > 0)
                {
                    Debug.LogWarning("[Characters] " + problems.ToString().TrimEnd(), catalog);
                    summary += "\n\n" + problems.ToString().TrimEnd();
                }
                Debug.Log("[Characters] " + summary.Split('\n')[0], catalog);
                return summary;
            }
            finally
            {
                s_Building = false;
            }
        }

        private static CharacterCatalog LoadOrCreateCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            EnsureFolder(ResourcesFolder);
            catalog = ScriptableObject.CreateInstance<CharacterCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            return catalog;
        }

        /// <summary>Makes an FBX a Humanoid if it isn't one. False (with a reason) if it still isn't.</summary>
        private static bool EnsureHumanoid(string modelPath, out string error)
        {
            error = null;
            if (HasHumanoidAvatar(modelPath)) return true;

            if (AssetImporter.GetAtPath(modelPath) is ModelImporter)
            {
                var log = new StringBuilder();
                if (!PlayerModelSwapTool.TryConfigureHumanoidModel(modelPath, PlayerModelSwapTool.GetPlayerHeight(), log, out error)) return false;
                if (HasHumanoidAvatar(modelPath)) return true;
            }

            error = $"{modelPath} has no valid Humanoid avatar (the player animations need one), so swapping to it won't work.";
            return false;
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

        private static string ThumbnailPathFor(string id) => $"{ThumbnailFolder}/{id}.png";

        /// <summary>
        /// Blank, or one of ours that's out of date (wrong name, too small, older than the model or its import
        /// settings). Hand-picked pictures from outside <see cref="ThumbnailFolder"/> are never replaced.
        /// </summary>
        private static bool NeedsThumbnail(CharacterCatalog.Entry entry, string modelPath, bool force)
        {
            if (entry.thumbnail == null) return true;

            string current = AssetDatabase.GetAssetPath(entry.thumbnail);
            if (!current.StartsWith(ThumbnailFolder + "/", StringComparison.Ordinal)) return false; // chosen by hand
            if (force) return true;

            string expected = ThumbnailPathFor(entry.id);
            if (current != expected || !File.Exists(expected)) return true;
            if (entry.thumbnail.width < ThumbnailSize) return true;

            DateTime rendered = File.GetLastWriteTimeUtc(expected);
            return rendered < File.GetLastWriteTimeUtc(modelPath) ||
                   (File.Exists(modelPath + ".meta") && rendered < File.GetLastWriteTimeUtc(modelPath + ".meta"));
        }

        private static Texture2D RenderThumbnailAsset(string id, GameObject model)
        {
            EnsureFolder(ThumbnailFolder);
            string thumbnailPath = ThumbnailPathFor(id);

            Texture2D rendered = RenderThumbnail(model);
            if (rendered == null) return AssetDatabase.LoadAssetAtPath<Texture2D>(thumbnailPath);

            File.WriteAllBytes(thumbnailPath, rendered.EncodeToPNG());
            Object.DestroyImmediate(rendered);
            AssetDatabase.ImportAsset(thumbnailPath, ImportAssetOptions.ForceUpdate);

            if (AssetImporter.GetAtPath(thumbnailPath) is TextureImporter textureImporter)
            {
                textureImporter.textureType = TextureImporterType.Default;
                textureImporter.mipmapEnabled = true; // drawn at a quarter size in the list
                textureImporter.maxTextureSize = 512;
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

    /// <summary>Refreshes the character list when it's saved or one of its models is imported, moved or deleted.</summary>
    internal class CharacterCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(CharacterCatalogBuilder.IsWatchedPath))
            {
                CharacterCatalogBuilder.ScheduleRefresh();
            }
        }
    }

    /// <summary>The character list's Inspector: the normal list, plus buttons to fill in blanks or re-render thumbnails now.</summary>
    [CustomEditor(typeof(CharacterCatalog))]
    internal class CharacterCatalogEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "The characters on the Change Profile screen, in this order (the first is the default for new players). " +
                "Each entry only needs a model; a blank id, display name or thumbnail is filled in when the list is saved. " +
                "Don't change an id after people have picked that character.",
                MessageType.Info);

            DrawDefaultInspector();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fill In Blanks Now"))
                {
                    serializedObject.ApplyModifiedProperties();
                    CharacterCatalogBuilder.Refresh(forceThumbnails: false);
                }
                if (GUILayout.Button("Re-render All Thumbnails"))
                {
                    serializedObject.ApplyModifiedProperties();
                    CharacterCatalogBuilder.Refresh(forceThumbnails: true);
                }
            }
        }
    }
}
