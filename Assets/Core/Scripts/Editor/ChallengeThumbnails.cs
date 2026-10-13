using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The pictures in the Practice Challenges list: a 3/4 view of each challenge prefab from above and
    /// behind its start (looking towards its finish), rendered to <see cref="ThumbnailFolder"/>/{id}.png and
    /// stored on its <see cref="ChallengeCatalog"/> entry by MatchCatalogBuilder.
    ///
    /// A thumbnail is only re-rendered when the challenge prefab or anything it uses (its glbs, textures,
    /// materials) is newer than the picture, so rebuilding the lists stays quick with a couple of hundred
    /// challenges. Friendslop > Maps > Re-render Challenge Thumbnails redoes them all.
    /// </summary>
    public static class ChallengeThumbnails
    {
        public const string ThumbnailFolder = "Assets/Core/Art/Challenges/Thumbnails";

        /// <summary>Rendered size in pixels (shown at half size in the list, so it stays sharp on big screens).</summary>
        public const int ThumbnailSize = 192;

        private static bool s_ForceNext;

        [MenuItem("Friendslop/Maps/Re-render Challenge Thumbnails")]
        private static void RerenderAll()
        {
            s_ForceNext = true;
            try
            {
                EditorUtility.DisplayDialog("Challenge Thumbnails", MatchCatalogBuilder.Rebuild(), "OK");
            }
            finally
            {
                s_ForceNext = false;
            }
        }

        /// <summary>The challenge's thumbnail - the existing picture if it's up to date, otherwise a fresh render.</summary>
        public static Texture2D Ensure(string id, GameObject prefab)
        {
            if (prefab == null || string.IsNullOrEmpty(id)) return null;

            string path = $"{ThumbnailFolder}/{id}.png";
            string prefabPath = AssetDatabase.GetAssetPath(prefab);
            if (!s_ForceNext && File.Exists(path) && !IsOutOfDate(path, prefabPath))
            {
                var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (existing != null) return existing;
            }

            Texture2D rendered = Render(prefab);
            if (rendered == null) return AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            EnsureFolder(ThumbnailFolder);
            File.WriteAllBytes(path, rendered.EncodeToPNG());
            Object.DestroyImmediate(rendered);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.mipmapEnabled = true; // drawn at half size
                importer.maxTextureSize = 256;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.alphaIsTransparency = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>Older than the prefab or any asset it depends on (re-exported glbs, changed textures...).</summary>
        private static bool IsOutOfDate(string thumbnailPath, string prefabPath)
        {
            DateTime rendered = File.GetLastWriteTimeUtc(thumbnailPath);
            foreach (string dependency in AssetDatabase.GetDependencies(prefabPath, true))
            {
                if (dependency.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(dependency) && File.GetLastWriteTimeUtc(dependency) > rendered) return true;
            }
            return false;
        }

        private static Texture2D Render(GameObject prefab)
        {
            var preview = new PreviewRenderUtility();
            try
            {
                Camera camera = preview.camera;
                camera.fieldOfView = 30f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.47f, 0.66f, 0.85f, 1f); // sky blue
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 2000f;

                preview.lights[0].intensity = 1.2f;
                preview.lights[0].transform.rotation = Quaternion.Euler(50f, 30f, 0f);
                preview.lights[1].intensity = 0.6f;
                preview.lights[1].transform.rotation = Quaternion.Euler(30f, -150f, 0f);
                preview.ambientColor = new Color(0.5f, 0.5f, 0.5f);

                GameObject instance = preview.InstantiatePrefabInScene(prefab);
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return null;

                Bounds bounds = renderers[0].bounds;
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);

                // Look from behind the start towards the finish if the challenge marks them; otherwise diagonally.
                Vector3 forward = new Vector3(1f, 0f, 1f).normalized;
                Transform start = FindByPrefix(instance.transform, "challengestart");
                Transform finish = FindByPrefix(instance.transform, "challengefinish");
                if (start != null && finish != null)
                {
                    Vector3 along = finish.position - start.position;
                    along.y = 0f;
                    if (along.sqrMagnitude > 1f) forward = along.normalized;
                }

                Quaternion view = Quaternion.LookRotation(forward) * Quaternion.Euler(50f, 0f, 0f); // 50 degrees down
                float radius = bounds.extents.magnitude;
                float distance = radius / Mathf.Sin(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.85f; // a little cropping is fine
                camera.transform.rotation = view;
                camera.transform.position = bounds.center - view * Vector3.forward * distance;

                preview.BeginStaticPreview(new Rect(0, 0, ThumbnailSize, ThumbnailSize));
                preview.Render(true);
                return preview.EndStaticPreview();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Maps] Couldn't render a thumbnail for {prefab.name}: {e.Message}", prefab);
                return null;
            }
            finally
            {
                preview.Cleanup();
            }
        }

        private static Transform FindByPrefix(Transform root, string prefix) =>
            root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
