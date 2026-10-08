using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps the two lists MatchLayout builds matches from in step with the project, so adding a map or a
    /// challenge is just making its prefab:
    ///   - <see cref="MapCatalog"/> (Resources/MapCatalog): every prefab in <see cref="MapsFolder"/>, by file
    ///     name (the id the Host Game screen's map entries use), with a count of its 16x16 and 32x32 holes.
    ///   - <see cref="ChallengeCatalog"/> (Resources/ChallengeCatalog): every prefab in
    ///     <see cref="ChallengesFolder"/> that has a challenge16/challenge32 corner marker (its start/finish
    ///     glbs carry one); the marker's name gives its size. Prefabs without one are skipped with a warning.
    ///     Prefabs WITHOUT an underscore in their name (Challenge16, Challenge32) are not challenges: they're
    ///     the plain pieces Practice Map fills the holes with, so they're kept out of the random rotation and
    ///     stored as the catalog's practice pieces instead.
    /// Rebuilt automatically when anything in those folders (or a glb in Assets/Maps, since the markers live
    /// there) is imported, moved or deleted, and once when the editor loads if a list doesn't exist yet.
    ///
    /// Menus (Friendslop > Maps):
    ///   - Rebuild Map And Challenge Lists - forces a rebuild and shows a summary.
    ///   - Preview Random Challenges In Selected Map - select a map in an open scene; fills its holes with a
    ///     random layout exactly the way a match does, so you can check the fit. The preview is never saved.
    ///   - Clear Challenge Preview.
    /// </summary>
    [InitializeOnLoad]
    public static class MatchCatalogBuilder
    {
        public const string MapsFolder = "Assets/Core/Prefabs/Maps";
        public const string ChallengesFolder = "Assets/Core/Prefabs/Challenges";
        private const string GlbFolder = "Assets/Maps";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string MapCatalogPath = ResourcesFolder + "/" + MapCatalog.ResourcePath + ".asset";
        private const string ChallengeCatalogPath = ResourcesFolder + "/" + ChallengeCatalog.ResourcePath + ".asset";
        private const string PreviewRootName = "Challenge Preview (not saved)";

        private static bool s_Building;
        private static bool s_Scheduled;

        static MatchCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<MapCatalog>(MapCatalogPath) == null ||
                    AssetDatabase.LoadAssetAtPath<ChallengeCatalog>(ChallengeCatalogPath) == null)
                {
                    Rebuild();
                }
            };
        }

        [MenuItem("Friendslop/Maps/Rebuild Map And Challenge Lists")]
        public static void RebuildFromMenu()
        {
            EditorUtility.DisplayDialog("Maps & Challenges", Rebuild(), "OK");
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
            path.StartsWith(MapsFolder + "/", StringComparison.Ordinal) ||
            path.StartsWith(ChallengesFolder + "/", StringComparison.Ordinal) ||
            (path.StartsWith(GlbFolder + "/", StringComparison.Ordinal) && path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase));

        /// <summary>Rebuilds both lists. Returns a summary (also logged).</summary>
        public static string Rebuild()
        {
            if (s_Building) return "Already rebuilding.";
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Not rebuilt in play mode.";

            s_Building = true;
            var log = new StringBuilder();
            try
            {
                EnsureFolder(ResourcesFolder);

                List<MapCatalog.Entry> maps = BuildMapEntries(log);
                var mapCatalog = LoadOrCreate<MapCatalog>(MapCatalogPath);
                mapCatalog.SetMaps(maps);
                EditorUtility.SetDirty(mapCatalog);

                List<ChallengeCatalog.Entry> challenges = BuildChallengeEntries(log, out GameObject practice16, out GameObject practice32);
                var challengeCatalog = LoadOrCreate<ChallengeCatalog>(ChallengeCatalogPath);
                challengeCatalog.SetChallenges(challenges);
                challengeCatalog.SetPracticePieces(practice16, practice32);
                log.AppendLine($"Practice Map pieces: 16x16 = {(practice16 != null ? practice16.name : "none")}, 32x32 = {(practice32 != null ? practice32.name : "none")}.");
                EditorUtility.SetDirty(challengeCatalog);

                AssetDatabase.SaveAssets();

                string mapList = maps.Count > 0
                    ? string.Join(", ", maps.Select(m => $"{m.id} ({m.sockets16}x16 holes, {m.sockets32}x32 holes)"))
                    : "none";
                int c16 = challenges.Count(c => c.size == 16);
                int c32 = challenges.Count(c => c.size == 32);
                string summary = $"Maps: {mapList}.\nChallenges: {c16} that fit 16x16 holes, {c32} that fit 32x32 holes.\n{log}".TrimEnd();
                Debug.Log("[Maps] " + summary);
                return summary;
            }
            finally
            {
                s_Building = false;
            }
        }

        private static List<MapCatalog.Entry> BuildMapEntries(StringBuilder log)
        {
            var entries = new List<MapCatalog.Entry>();
            foreach (string path in FindPrefabs(MapsFolder))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                // Sanity check: every hole is marked by four corner entities, so each size's count should be a multiple of 4.
                // (Corner markers count too - each hole is three plain markers plus one challengeNNcorner.)
                int markers16 = 0, markers32 = 0, corners16 = 0, corners32 = 0;
                foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                {
                    if (t == prefab.transform || !ChallengeSockets.TryParseMarker(t.name, out int markerSize, out bool isCorner)) continue;
                    if (markerSize == 16) { markers16++; if (isCorner) corners16++; }
                    else { markers32++; if (isCorner) corners32++; }
                }
                foreach ((int size, int count, int corners) in new[] { (16, markers16, corners16), (32, markers32, corners32) })
                {
                    if (count % 4 != 0)
                    {
                        string message = $"Map {prefab.name} has {count} challenge{size} / challenge{size}corner entities, which isn't a multiple of 4 (each hole needs one in every corner). Check for a missing or extra marker.";
                        Debug.LogWarning("[Maps] " + message, prefab);
                        log.AppendLine(message);
                    }
                    if (count > 0 && corners * 4 != count)
                    {
                        string message = $"Map {prefab.name} has {corners} challenge{size}corner entities for {count} challenge{size} markers - each hole should have exactly one corner marker ({count / 4} expected).";
                        Debug.LogWarning("[Maps] " + message, prefab);
                        log.AppendLine(message);
                    }
                }

                List<ChallengeSockets.Socket> sockets = ChallengeSockets.FindSockets(prefab.transform);
                entries.Add(new MapCatalog.Entry
                {
                    id = prefab.name,
                    prefab = prefab,
                    sockets16 = sockets.Count(s => s.Size == 16),
                    sockets32 = sockets.Count(s => s.Size == 32)
                });
            }
            return entries;
        }

        private static List<ChallengeCatalog.Entry> BuildChallengeEntries(StringBuilder log, out GameObject practice16, out GameObject practice32)
        {
            practice16 = null;
            practice32 = null;
            var entries = new List<ChallengeCatalog.Entry>();
            foreach (string path in FindPrefabs(ChallengesFolder))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                if (ChallengeSockets.FindMarker(prefab.transform, out int size) == null)
                {
                    string message = $"Skipped challenge {prefab.name}: no challenge16/challenge32 corner marker (its start/finish glbs should have one).";
                    Debug.LogWarning("[Maps] " + message, prefab);
                    log.AppendLine(message);
                    continue;
                }

                if (!prefab.name.Contains("_"))
                {
                    // Challenge16 / Challenge32: Practice Map's hole fillers, never part of the random rotation.
                    if (size == 16 && practice16 == null) practice16 = prefab;
                    else if (size == 32 && practice32 == null) practice32 = prefab;
                    continue;
                }

                entries.Add(new ChallengeCatalog.Entry { id = prefab.name, size = size, prefab = prefab });
            }
            return entries;
        }

        /// <summary>Prefab paths directly in or under <paramref name="folder"/>, sorted by file name (a fixed order every client shares).</summary>
        private static IEnumerable<string> FindPrefabs(string folder)
        {
            if (!AssetDatabase.IsValidFolder(folder)) return Enumerable.Empty<string>();
            return AssetDatabase.FindAssets("t:Prefab", new[] { folder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Distinct()
                .OrderBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal);
        }

        private static T LoadOrCreate<T>(string path) where T : ScriptableObject
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        // =====================================================================================
        // Preview
        // =====================================================================================

        [MenuItem("Friendslop/Maps/Preview Random Challenges In Selected Map")]
        private static void PreviewRandomChallenges()
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null || EditorUtility.IsPersistent(selected))
            {
                EditorUtility.DisplayDialog("Challenge Preview", "Select a map in an open scene (e.g. LastStop in Scenes/Maps/LastStop) first.", "OK");
                return;
            }

            Transform mapRoot = selected.transform.root;
            List<ChallengeSockets.Socket> sockets = ChallengeSockets.FindSockets(mapRoot);
            if (sockets.Count == 0)
            {
                EditorUtility.DisplayDialog("Challenge Preview", $"'{mapRoot.name}' has no challenge16/challenge32 entities.", "OK");
                return;
            }

            Rebuild();
            ChallengeCatalog catalog = AssetDatabase.LoadAssetAtPath<ChallengeCatalog>(ChallengeCatalogPath);

            ClearPreview();
            var previewRoot = new GameObject(PreviewRootName) { hideFlags = HideFlags.DontSave };
            SceneManagerMove(previewRoot, mapRoot.gameObject);

            int[] picks = MatchLayout.PickChallenges(sockets, catalog, new System.Random());
            int placed = 0;
            for (int i = 0; i < sockets.Count; i++)
            {
                ChallengeCatalog.Entry entry = catalog != null ? catalog.Get(picks[i]) : null;
                if (entry == null) continue;

                GameObject instance = ChallengeSockets.Place(entry.prefab, sockets[i], previewRoot.transform, $"{i:D3}_{entry.id}");
                if (instance == null) continue;
                foreach (Transform t in instance.GetComponentsInChildren<Transform>(true)) t.gameObject.hideFlags = HideFlags.DontSave;
                placed++;
            }

            Selection.activeGameObject = previewRoot;
            Debug.Log($"[Maps] Previewing {placed} challenge(s) in {sockets.Count} hole(s) of '{mapRoot.name}'. Not saved with the scene - use Friendslop > Maps > Clear Challenge Preview to remove it.");
        }

        [MenuItem("Friendslop/Maps/Clear Challenge Preview")]
        private static void ClearPreview()
        {
            for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
            {
                UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name == PreviewRootName) Object.DestroyImmediate(root);
                }
            }
        }

        private static void SceneManagerMove(GameObject go, GameObject sameSceneAs)
        {
            if (go.scene != sameSceneAs.scene) EditorSceneManager.MoveGameObjectToScene(go, sameSceneAs.scene);
        }
    }

    /// <summary>Triggers a rebuild when a map/challenge prefab or a map glb is imported, moved or deleted.</summary>
    internal class MatchCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(MatchCatalogBuilder.IsWatchedPath))
            {
                MatchCatalogBuilder.ScheduleRebuild();
            }
        }
    }
}
