using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Fusion;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-off tool that finishes moving the project from Netcode for GameObjects (NGO) + Unity Gaming Services
    /// to Photon Fusion 2 (Shared mode). The C# side was ported by hand; this does the asset side, which can
    /// only be done from inside the editor:
    ///
    /// 1. Every prefab and scene under Assets/ (dependencies first, so nested prefabs are fixed before the
    ///    prefabs that contain them):
    ///    - removes NGO / UGS / Blocks-session components (NetworkObject, NetworkTransform, NetworkAnimator,
    ///      NetworkRigidbody, ComponentController, NetworkManager, UnityTransport, ...) and missing scripts
    ///      (e.g. the old GameNetworkManager / GameNetworkUI, whose classes no longer exist);
    ///    - adds a Fusion NetworkObject wherever a Fusion NetworkBehaviour needs one. The player gets
    ///      "Destroy When State Authority Leaves"; scene objects (RoundTimer, ChallengeManager, interactables)
    ///      get "Is Master Client Object" so the session's host owns them;
    ///    - replaces NGO NetworkTransforms with Fusion NetworkTransforms, and gives the player a Fusion
    ///      NetworkTransform + NetworkMecanimAnimator;
    ///    - moves the objects the NGO ComponentController hid on elimination into VisualsAddon;
    ///    - deletes the scene "NetworkManager" objects;
    ///    - points GameManager at the player prefab (it now spawns the player itself).
    /// 2. Moves the old networking assets to the OS trash (recoverable): the Blocks multiplayer-session module,
    ///    the NetworkManager prefab, NGO's DefaultNetworkPrefabs list, the multiplayer-session test scene and
    ///    the stubbed-out old networking scripts. Anything still referenced by a kept asset is left alone
    ///    and reported.
    /// 3. Makes sure Blocks.Gameplay.Core is in Fusion's "Assemblies To Weave" list.
    /// 4. Removes the NGO, Unity Transport, Multiplayer Services, Multiplayer Tools and Multiplayer Center packages.
    ///
    /// Run it once, with the NGO packages still installed (it reads the old components to know what to
    /// replace). It's safe to re-run - everything is "add what's missing / remove what's left".
    /// A full report is written to Logs/FusionMigrationReport.txt.
    ///
    /// Usage: Tools > Fusion Migration > Migrate Project to Fusion. Delete this file afterwards if you like.
    /// </summary>
    public static class FusionMigrationTool
    {
        #region Configuration

        private const string MenuPath = "Tools/Fusion Migration/Migrate Project to Fusion";
        private const string GameplayAssemblyName = "Blocks.Gameplay.Core";
        private const string ReportPath = "Logs/FusionMigrationReport.txt";

        /// <summary>Components from assemblies whose name starts with one of these are removed.</summary>
        private static readonly string[] LegacyAssemblyPrefixes =
        {
            "Unity.Netcode",
            "Unity.Networking.Transport",
            "Unity.Services.",
            "Unity.Multiplayer.Tools",
            "Blocks.Sessions",
        };

        /// <summary>
        /// Old gameplay-assembly components that only survive as empty placeholder classes (by name, since their
        /// scripts are trashed at the end of the migration).
        /// </summary>
        private static readonly string[] ObsoleteGameplayComponents = { "GameNetworkManager", "GameNetworkUI", "AutomatedNetworkTransform" };

        /// <summary>Asset paths (files or folders) to move to the trash once nothing kept depends on them.</summary>
        private static readonly string[] TrashCandidates =
        {
            "Assets/Blocks/MultiplayerSession",
            "Assets/Blocks/CommonSession",
            "Assets/Blocks/Common",
            "Assets/Core/Prefabs/[BB] NetworkManager.prefab",
            "Assets/DefaultNetworkPrefabs.asset",
            "Assets/Core/TestScenes/[BB] Core MultiplayerSession.unity",
            "Assets/Core/TestScenes/[BB] Core MultiplayerSession.scenetemplate",
            "Assets/Core/Scripts/Runtime/Framework/Networking/GameNetworkManager.cs",
            "Assets/Core/Scripts/Runtime/Framework/Networking/GameNetworkUI.cs",
            "Assets/Core/Scripts/Runtime/Framework/Networking/NetworkStateViewModel.cs",
            "Assets/Core/Scripts/Runtime/Components/AutomatedNetworkTransform.cs",
        };

        /// <summary>Asset names (any folder, any type except scripts) that belonged to the old NGO session UI.</summary>
        private static readonly string[] TrashCandidateNames = { "GameNetworkUI", "NetworkStateView" };

        private static readonly string[] PackagesToRemove =
        {
            "com.unity.netcode.gameobjects",
            "com.unity.services.multiplayer",
            "com.unity.transport",
            "com.unity.multiplayer.tools",
            "com.unity.multiplayer.center",
        };

        #endregion

        #region State

        private static StringBuilder s_Report;
        private static GameObject s_PlayerPrefab;
        private static HashSet<string> s_SkipPaths;
        private static int s_Warnings;
        private static AddAndRemoveRequest s_AddAndRemoveRequest;

        #endregion

        #region Entry Point

        [MenuItem(MenuPath)]
        public static void Migrate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Fusion Migration", "Exit play mode first.", "OK");
                return;
            }

            if (EditorUtility.scriptCompilationFailed)
            {
                EditorUtility.DisplayDialog("Fusion Migration",
                    "There are script compilation errors. Fix them first - otherwise scripts would look 'missing' " +
                    "and this tool would strip them from your prefabs and scenes.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Migrate Project to Fusion",
                    "This rewrites prefabs and scenes (removing Netcode for GameObjects components and adding Fusion " +
                    "ones), moves the old networking assets to the trash and removes the NGO / Unity Multiplayer " +
                    "packages.\n\nMake sure your project is committed or backed up first.",
                    "Migrate", "Cancel"))
            {
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            s_Report = new StringBuilder();
            s_Warnings = 0;
            string originalScene = SceneManager.GetActiveScene().path;

            try
            {
                s_SkipPaths = ExpandToAssetPaths(GetTrashCandidates());
                Log($"Fusion migration - {DateTime.Now}");
                Log($"Assets that will be trashed (not migrated): {s_SkipPaths.Count}");

                // Not batched with StartAssetEditing on purpose: each prefab must be re-imported before the
                // prefabs that nest it are opened, or they'd see its old components.
                MigratePrefabs();
                AssetDatabase.SaveAssets();

                MigrateScenes();
                EnsureAssemblyIsWoven();
                TrashObsoleteAssets();
            }
            catch (Exception exception)
            {
                Warn($"Migration stopped with an exception: {exception}");
                WriteReport();
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Fusion Migration", $"Stopped with an error - see the Console and {ReportPath}.", "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (!string.IsNullOrEmpty(originalScene) && File.Exists(originalScene))
            {
                EditorSceneManager.OpenScene(originalScene, OpenSceneMode.Single);
            }

            Log("");
            Log($"Removing packages: {string.Join(", ", PackagesToRemove)}");
            WriteReport();

            Debug.Log($"[Fusion Migration] Asset migration finished with {s_Warnings} warning(s). Report: {ReportPath}\n{s_Report}");
            EditorUtility.DisplayDialog("Fusion Migration",
                $"Prefabs and scenes migrated ({s_Warnings} warning(s) - see the Console or {ReportPath}).\n\n" +
                "Unity will now remove the NGO / Unity Multiplayer packages and recompile. That takes a minute; " +
                "check the Package Manager afterwards.", "OK");
            RemovePackages();
        }

        #endregion

        #region Prefabs

        private static void MigratePrefabs()
        {
            Log("");
            Log("== Prefabs ==");

            var prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.StartsWith("Assets/Photon/", StringComparison.Ordinal))
                .Where(path => !s_SkipPaths.Contains(path))
                .Distinct()
                // A nested prefab always has fewer (transitive) prefab dependencies than the prefab containing it,
                // so this processes inner prefabs before the prefabs that contain them.
                .OrderBy(path => AssetDatabase.GetDependencies(path, true).Count(d => d.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            s_PlayerPrefab = FindPlayerPrefab(prefabPaths);
            Log(s_PlayerPrefab != null
                ? $"Player prefab: {AssetDatabase.GetAssetPath(s_PlayerPrefab)}"
                : "Player prefab: NOT FOUND (no prefab has a CorePlayerManager on its root)");

            for (int i = 0; i < prefabPaths.Count; i++)
            {
                string path = prefabPaths[i];
                EditorUtility.DisplayProgressBar("Fusion Migration - prefabs", path, (float)i / prefabPaths.Count);

                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null || !NeedsMigration(asset))
                {
                    continue;
                }

                GameObject contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var changes = new List<string>();
                    MigrateHierarchy(contents, true, changes);
                    if (changes.Count > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        Log($"{path}:");
                        foreach (string change in changes) Log($"    {change}");
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }
        }

        private static GameObject FindPlayerPrefab(List<string> prefabPaths)
        {
            var candidates = prefabPaths
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .Where(go => go != null && go.GetComponent<CorePlayerManager>() != null)
                .ToList();

            if (candidates.Count > 1)
            {
                Warn($"More than one player prefab found ({string.Join(", ", candidates.Select(AssetDatabase.GetAssetPath))}); using the first one for GameManager.");
            }

            return candidates.OrderBy(go => go.name.Contains("CorePlayer") ? 0 : 1).FirstOrDefault();
        }

        /// <summary>Cheap pre-check on the prefab asset so only prefabs that need changes are opened.</summary>
        private static bool NeedsMigration(GameObject asset)
        {
            foreach (Transform t in asset.GetComponentsInChildren<Transform>(true))
            {
                GameObject go = t.gameObject;
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0) return true;

                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component == null) return true;
                    if (IsLegacy(component)) return true;
                    if (component is NetworkBehaviour && component.GetComponentInParent<NetworkObject>(true) == null) return true;
                    if (component is CorePlayerManager || component is GameManager) return true;
                }
            }

            return false;
        }

        #endregion

        #region Scenes

        private static void MigrateScenes()
        {
            Log("");
            Log("== Scenes ==");

            var scenePaths = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !path.StartsWith("Assets/Photon/", StringComparison.Ordinal))
                .Where(path => !s_SkipPaths.Contains(path))
                .Distinct()
                .ToList();

            for (int i = 0; i < scenePaths.Count; i++)
            {
                string path = scenePaths[i];
                EditorUtility.DisplayProgressBar("Fusion Migration - scenes", path, (float)i / scenePaths.Count);

                Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var changes = new List<string>();

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root != null)
                    {
                        MigrateHierarchy(root, false, changes);
                    }
                }

                if (changes.Count > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    Log($"{path}:");
                    foreach (string change in changes) Log($"    {change}");
                }
            }
        }

        #endregion

        #region Hierarchy Migration

        private sealed class LegacyInfo
        {
            public readonly HashSet<GameObject> HadNetworkObject = new HashSet<GameObject>();
            public readonly HashSet<GameObject> HadNetworkTransform = new HashSet<GameObject>();
            public readonly List<GameObject> HadNetworkAnimator = new List<GameObject>();
            public readonly List<GameObject> HadNetworkManager = new List<GameObject>();
            public readonly List<Component> HiddenOnElimination = new List<Component>();
            public readonly List<(Component component, int order)> ToRemove = new List<(Component, int)>();
        }

        /// <summary>
        /// Migrates one hierarchy: a prefab's contents (<paramref name="isPrefabContents"/>) or a scene root.
        /// Components that come from a nested prefab (rather than being added in this context) are left to that
        /// prefab's own pass.
        /// </summary>
        private static void MigrateHierarchy(GameObject root, bool isPrefabContents, List<string> changes)
        {
            var info = new LegacyInfo();
            var gameObjects = root.GetComponentsInChildren<Transform>(true).Select(t => t.gameObject).ToList();
            var reportedMissingPrefabs = new HashSet<GameObject>();
            var missingPrefabRoots = new List<GameObject>();

            // ---- 1. Record what the old components were doing, and queue them for removal.
            foreach (GameObject go in gameObjects)
            {
                if (!isPrefabContents && IsInsideMissingPrefab(go, out GameObject missingRoot))
                {
                    if (reportedMissingPrefabs.Add(missingRoot))
                    {
                        missingPrefabRoots.Add(missingRoot);
                    }
                    continue;
                }

                foreach (Component component in go.GetComponents<Component>())
                {
                    if (component == null || !IsLegacy(component)) continue;

                    Type type = component.GetType();
                    int order = 0;

                    if (type.FullName == "Unity.Netcode.NetworkObject")
                    {
                        info.HadNetworkObject.Add(go);
                        order = 3;
                    }
                    else if (type.FullName == "Unity.Netcode.NetworkManager" || type.Name == "GameNetworkManager")
                    {
                        info.HadNetworkManager.Add(go);
                        order = 2;
                    }
                    else if (DerivesFrom(type, "Unity.Netcode.Components.NetworkTransform"))
                    {
                        info.HadNetworkTransform.Add(go);
                        order = 1;
                    }
                    else if (DerivesFrom(type, "Unity.Netcode.Components.NetworkAnimator"))
                    {
                        info.HadNetworkAnimator.Add(go);
                    }
                    else if (type.Name == "ComponentController")
                    {
                        info.HiddenOnElimination.AddRange(ReadComponentControllerTargets(component));
                    }
                    else if (type.Name.Contains("NetworkRigidbody"))
                    {
                        Warn($"'{GetPath(go)}' had an NGO {type.Name}. Fusion's physics sync lives in the separate " +
                             "Fusion Physics add-on; it now only gets a Fusion NetworkTransform.");
                        info.HadNetworkTransform.Add(go);
                    }

                    if (CanModify(component))
                    {
                        info.ToRemove.Add((component, order));
                    }
                }
            }

            // ---- 2. Scene NetworkManager objects go entirely (the session now lives in FusionSessionService).
            if (!isPrefabContents)
            {
                foreach (GameObject go in info.HadNetworkManager)
                {
                    GameObject target = go;
                    if (PrefabUtility.IsPartOfPrefabInstance(go))
                    {
                        target = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                    }

                    if (target != null && (target.transform.parent == null || !PrefabUtility.IsPartOfPrefabInstance(target.transform.parent)))
                    {
                        changes.Add($"Deleted NetworkManager object '{GetPath(target)}'");
                        info.ToRemove.RemoveAll(entry => entry.component == null || entry.component.transform.IsChildOf(target.transform));
                        info.HadNetworkObject.RemoveWhere(g => g == null || g.transform.IsChildOf(target.transform));
                        info.HadNetworkTransform.RemoveWhere(g => g == null || g.transform.IsChildOf(target.transform));
                        Object.DestroyImmediate(target);
                    }
                }

                // Instances of prefabs that no longer exist can't be edited, and still carry their old NGO
                // components as overrides. They can't work with Fusion (or at all), so they're removed.
                foreach (GameObject missingRoot in missingPrefabRoots)
                {
                    if (missingRoot == null) continue;
                    if (missingRoot.transform.parent != null && PrefabUtility.IsPartOfPrefabInstance(missingRoot.transform.parent))
                    {
                        Warn($"'{GetPath(missingRoot)}' is an instance of a prefab that no longer exists, inside another prefab instance - delete it by hand.");
                        continue;
                    }

                    Warn($"Deleted '{GetPath(missingRoot)}': it's an instance of a prefab that no longer exists " +
                         "(so it did nothing in-game) and still carried old NGO components. Restore it from version control if you need it.");
                    changes.Add($"Deleted missing-prefab instance '{GetPath(missingRoot)}'");
                    Object.DestroyImmediate(missingRoot);
                }

                gameObjects.RemoveAll(go => go == null);

                // The whole hierarchy may have been a NetworkManager (e.g. a scene-root "[BB] NetworkManager").
                if (root == null)
                {
                    return;
                }
            }

            // ---- 3. Hand the ComponentController's "hide when eliminated" list over to VisualsAddon.
            if (info.HiddenOnElimination.Count > 0)
            {
                foreach (VisualsAddon visuals in root.GetComponentsInChildren<VisualsAddon>(true))
                {
                    if (!CanModify(visuals)) continue;

                    var serialized = new SerializedObject(visuals);
                    var list = serialized.FindProperty("hideWhenEliminated");
                    if (list == null || list.arraySize > 0) continue;

                    var targets = info.HiddenOnElimination.Where(c => c != null).Distinct().ToList();
                    list.arraySize = targets.Count;
                    for (int i = 0; i < targets.Count; i++)
                    {
                        list.GetArrayElementAtIndex(i).objectReferenceValue = targets[i];
                    }
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    changes.Add($"VisualsAddon on '{GetPath(visuals.gameObject)}' now hides {targets.Count} component(s) on elimination (was NGO ComponentController)");
                }
            }

            // ---- 4. Remove the legacy components, dependants first.
            foreach (var (component, _) in info.ToRemove.OrderBy(entry => entry.order))
            {
                if (component == null) continue;
                string description = $"Removed {component.GetType().Name} from '{GetPath(component.gameObject)}'";
                Object.DestroyImmediate(component);
                changes.Add(description);
            }

            // ---- 5. Strip missing scripts (old GameNetworkManager/GameNetworkUI etc. and removed modules).
            foreach (GameObject go in gameObjects)
            {
                if (go == null) continue;
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go);
                if (missing == 0) continue;

                if (PrefabUtility.IsPartOfPrefabInstance(go))
                {
                    if (!IsInsideMissingPrefab(go, out _))
                    {
                        Warn($"'{GetPath(go)}' has {missing} missing script(s) coming from its prefab - they're removed when that prefab is migrated; if they remain, remove them in the prefab.");
                    }
                    continue;
                }

                GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
                changes.Add($"Removed {missing} missing script(s) from '{GetPath(go)}'");
            }

            // ---- 6. Player setup (prefab root with CorePlayerManager).
            var player = root.GetComponent<CorePlayerManager>();
            if (player != null && isPrefabContents)
            {
                SetUpPlayer(root, info, changes);
            }

            // ---- 7. Any other GameObject that had an NGO NetworkTransform gets a Fusion one.
            foreach (GameObject go in info.HadNetworkTransform)
            {
                if (go == null || (player != null && go == root)) continue;
                if (go.GetComponentInParent<NetworkObject>(true) == null)
                {
                    FusionEditorUtil.EnsureNetworkObject(go, NetworkObjectFlags.MasterClientObject);
                    changes.Add($"Added Fusion NetworkObject (master client object) to '{GetPath(go)}'");
                }
                if (FusionEditorUtil.EnsureNetworkTransform(go))
                {
                    changes.Add($"Added Fusion NetworkTransform to '{GetPath(go)}' (was NGO NetworkTransform)");
                }
            }

            // ---- 8. Every Fusion NetworkBehaviour needs a NetworkObject on itself or a parent.
            foreach (GameObject go in gameObjects)
            {
                if (go == null || IsInsideMissingPrefab(go, out _)) continue;
                if (!go.TryGetComponent(out NetworkBehaviour _)) continue;
                if (go.GetComponentInParent<NetworkObject>(true) != null) continue;

                // Put it where the NGO NetworkObject used to be, if that was a parent; otherwise on this object.
                GameObject target = go;
                for (Transform t = go.transform; t != null; t = t.parent)
                {
                    if (info.HadNetworkObject.Contains(t.gameObject))
                    {
                        target = t.gameObject;
                        break;
                    }
                }

                FusionEditorUtil.EnsureNetworkObject(target, NetworkObjectFlags.MasterClientObject);
                changes.Add($"Added Fusion NetworkObject (master client object) to '{GetPath(target)}'");
            }

            // ---- 8b. Non-player NetworkObjects are owned by the master client (host), and must survive it leaving.
            //          (A NetworkObject added by hand or by Unity defaults to "Destroy When State Authority Leaves",
            //          which would delete the RoundTimer/ChallengeManager for everyone when the host quits.)
            foreach (NetworkObject networkObject in root.GetComponentsInChildren<NetworkObject>(true))
            {
                if (networkObject.GetComponentInParent<CorePlayerManager>(true) != null) continue;
                if (IsInsideMissingPrefab(networkObject.gameObject, out _)) continue;

                if (FusionEditorUtil.MakeMasterClientObject(networkObject))
                {
                    changes.Add($"NetworkObject on '{GetPath(networkObject.gameObject)}' -> Is Master Client Object, not destroyed when its owner leaves");
                }
            }

            // ---- 9. NGO NetworkAnimators elsewhere -> Fusion NetworkMecanimAnimator next to the Animator.
            foreach (GameObject go in info.HadNetworkAnimator)
            {
                if (go == null || (player != null && isPrefabContents)) continue;
                var animator = go.GetComponent<Animator>();
                if (animator == null) continue;
                if (FusionEditorUtil.EnsureMecanimAnimator(go, animator))
                {
                    changes.Add($"Added Fusion NetworkMecanimAnimator to '{GetPath(go)}' (was NGO NetworkAnimator)");
                }
            }

            // ---- 10. GameManager spawns the player itself now.
            foreach (GameManager gameManager in root.GetComponentsInChildren<GameManager>(true))
            {
                if (s_PlayerPrefab == null) break;

                var serialized = new SerializedObject(gameManager);
                var playerPrefab = serialized.FindProperty("playerPrefab");
                if (playerPrefab == null || playerPrefab.objectReferenceValue != null) continue;

                playerPrefab.objectReferenceValue = s_PlayerPrefab;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                changes.Add($"GameManager on '{GetPath(gameManager.gameObject)}' -> Player Prefab = {s_PlayerPrefab.name}");
            }
        }

        private static void SetUpPlayer(GameObject root, LegacyInfo info, List<string> changes)
        {
            // Owned by the joining player; removed for everyone when that player leaves.
            var networkObject = FusionEditorUtil.EnsureNetworkObject(root, NetworkObjectFlags.DestroyWhenStateAuthorityLeaves);
            if ((networkObject.Flags & NetworkObjectFlags.MasterClientObject) != 0)
            {
                networkObject.Flags &= ~NetworkObjectFlags.MasterClientObject;
                EditorUtility.SetDirty(networkObject);
            }
            changes.Add("Player: Fusion NetworkObject on root (Destroy When State Authority Leaves)");

            // CoreMovement used to *be* the NGO NetworkTransform; now it sits next to a Fusion one.
            if (FusionEditorUtil.EnsureNetworkTransform(root))
            {
                changes.Add("Player: added Fusion NetworkTransform to root");
            }

            foreach (GameObject go in info.HadNetworkTransform)
            {
                if (go == null || go == root) continue;
                if (FusionEditorUtil.EnsureNetworkTransform(go))
                {
                    changes.Add($"Player: added Fusion NetworkTransform to '{GetPath(go)}' (was NGO NetworkTransform)");
                }
            }

            // Animation sync: one NetworkMecanimAnimator on the root, driving the model's Animator.
            Animator animator = null;
            var coreAnimator = root.GetComponentInChildren<CoreAnimator>(true);
            if (coreAnimator != null)
            {
                var serialized = new SerializedObject(coreAnimator);
                animator = serialized.FindProperty("m_Animator")?.objectReferenceValue as Animator;
                if (animator == null) animator = coreAnimator.GetComponent<Animator>();
            }
            if (animator == null) animator = root.GetComponentInChildren<Animator>(true);

            if (animator == null)
            {
                Warn("Player prefab has no Animator - skipped NetworkMecanimAnimator.");
            }
            else if (FusionEditorUtil.EnsureMecanimAnimator(root, animator))
            {
                changes.Add($"Player: added Fusion NetworkMecanimAnimator to root (Animator on '{GetPath(animator.gameObject)}')");
            }
        }

        #endregion

        #region Weaving

        private static void EnsureAssemblyIsWoven()
        {
            Log("");
            Log("== Fusion weaving ==");

            string[] configGuids = AssetDatabase.FindAssets("NetworkProjectConfig");
            string configPath = configGuids.Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(p => p.EndsWith(".fusion", StringComparison.OrdinalIgnoreCase));
            if (configPath == null)
            {
                Warn("NetworkProjectConfig.fusion not found - add 'Blocks.Gameplay.Core' to Assemblies To Weave by hand (Fusion > Network Project Config).");
                return;
            }

            string json = File.ReadAllText(configPath);
            if (json.Contains($"\"{GameplayAssemblyName}\""))
            {
                Log($"{configPath} already weaves {GameplayAssemblyName}");
                return;
            }

            const string key = "\"AssembliesToWeave\": [";
            int index = json.IndexOf(key, StringComparison.Ordinal);
            if (index < 0)
            {
                Warn($"Couldn't find AssembliesToWeave in {configPath} - add '{GameplayAssemblyName}' by hand.");
                return;
            }

            int insertAt = index + key.Length;
            json = json.Insert(insertAt, $"\n        \"{GameplayAssemblyName}\",");
            File.WriteAllText(configPath, json);
            AssetDatabase.ImportAsset(configPath);
            Log($"Added {GameplayAssemblyName} to AssembliesToWeave in {configPath}");
        }

        #endregion

        #region Trash

        private static List<string> GetTrashCandidates()
        {
            var candidates = TrashCandidates.Where(p => AssetDatabase.IsValidFolder(p) || File.Exists(p)).ToList();

            foreach (string name in TrashCandidateNames)
            {
                foreach (string guid in AssetDatabase.FindAssets(name, new[] { "Assets" }))
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetDatabase.IsValidFolder(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileNameWithoutExtension(path) != name) continue;
                    if (!candidates.Contains(path)) candidates.Add(path);
                }
            }

            return candidates;
        }

        private static HashSet<string> ExpandToAssetPaths(IEnumerable<string> entries)
        {
            var result = new HashSet<string>();
            foreach (string entry in entries)
            {
                result.UnionWith(ExpandEntry(entry));
            }
            return result;
        }

        private static IEnumerable<string> ExpandEntry(string entry)
        {
            if (!AssetDatabase.IsValidFolder(entry))
            {
                return new[] { entry };
            }

            return AssetDatabase.FindAssets(string.Empty, new[] { entry })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p))
                .Append(entry);
        }

        private static void TrashObsoleteAssets()
        {
            Log("");
            Log("== Trash ==");

            var entries = GetTrashCandidates();
            var entryAssets = entries.ToDictionary(e => e, e => new HashSet<string>(ExpandEntry(e)));
            var blocked = new HashSet<string>();

            // Keep any entry that a kept asset still depends on; repeat, since a kept entry keeps its own dependencies.
            bool changed = true;
            while (changed)
            {
                changed = false;
                var trashed = new HashSet<string>(entries.Where(e => !blocked.Contains(e)).SelectMany(e => entryAssets[e]));
                var kept = AssetDatabase.GetAllAssetPaths()
                    .Where(p => p.StartsWith("Assets/", StringComparison.Ordinal) && !AssetDatabase.IsValidFolder(p) && !trashed.Contains(p))
                    .ToArray();

                var keptDependencies = new HashSet<string>(AssetDatabase.GetDependencies(kept, false));

                foreach (string entry in entries)
                {
                    if (blocked.Contains(entry)) continue;

                    string usedAsset = entryAssets[entry].FirstOrDefault(keptDependencies.Contains);
                    if (usedAsset == null) continue;

                    blocked.Add(entry);
                    changed = true;
                    string user = kept.FirstOrDefault(k => AssetDatabase.GetDependencies(k, false).Contains(usedAsset));
                    Warn($"Kept '{entry}': '{usedAsset}' is still used by '{user}'.");
                }
            }

            foreach (string entry in entries.Where(e => !blocked.Contains(e)))
            {
                if (AssetDatabase.MoveAssetToTrash(entry))
                {
                    Log($"Trashed {entry}");
                }
                else
                {
                    Warn($"Couldn't move '{entry}' to the trash - delete it by hand.");
                }
            }

            // Remove the Blocks folder itself if it's now empty.
            const string blocksFolder = "Assets/Blocks";
            if (AssetDatabase.IsValidFolder(blocksFolder) && AssetDatabase.FindAssets(string.Empty, new[] { blocksFolder }).Length == 0)
            {
                AssetDatabase.MoveAssetToTrash(blocksFolder);
                Log($"Trashed {blocksFolder} (empty)");
            }
        }

        #endregion

        #region Packages

        private static void RemovePackages()
        {
            s_AddAndRemoveRequest = Client.AddAndRemove(null, PackagesToRemove);
            EditorApplication.update += WaitForPackageRemoval;
        }

        private static void WaitForPackageRemoval()
        {
            if (s_AddAndRemoveRequest == null || !s_AddAndRemoveRequest.IsCompleted) return;

            EditorApplication.update -= WaitForPackageRemoval;

            // Note: a domain reload (from the trashed scripts) can drop this callback - the package removal itself
            // still completes in the Package Manager.
            if (s_AddAndRemoveRequest.Status == StatusCode.Success)
            {
                Debug.Log("[Fusion Migration] Packages removed. Unity will now recompile - the migration is complete.");
            }
            else
            {
                string message = s_AddAndRemoveRequest.Error?.message ?? "unknown error";
                Debug.LogError($"[Fusion Migration] Package removal failed: {message}. Remove {string.Join(", ", PackagesToRemove)} in the Package Manager by hand.");
                EditorUtility.DisplayDialog("Fusion Migration",
                    $"Assets migrated, but removing the packages failed:\n{message}\n\nRemove them in Window > Package Manager.", "OK");
            }

            s_AddAndRemoveRequest = null;
        }

        #endregion

        #region Helpers

        private static bool IsLegacy(Component component)
        {
            Type type = component.GetType();
            if (type.Namespace == "Blocks.Gameplay.Core" && ObsoleteGameplayComponents.Contains(type.Name))
            {
                return true;
            }

            string assemblyName = type.Assembly.GetName().Name;
            return LegacyAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal));
        }

        private static bool DerivesFrom(Type type, string baseTypeFullName)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.FullName == baseTypeFullName) return true;
            }
            return false;
        }

        /// <summary>A component can be removed here unless it comes from a nested prefab (then its own prefab's pass handles it).</summary>
        private static bool CanModify(Component component)
        {
            return !PrefabUtility.IsPartOfPrefabInstance(component) || PrefabUtility.IsAddedComponentOverride(component);
        }

        private static bool IsInsideMissingPrefab(GameObject go, out GameObject instanceRoot)
        {
            instanceRoot = null;
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                if (PrefabUtility.IsPrefabAssetMissing(t.gameObject))
                {
                    instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject) ?? t.gameObject;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Reads NGO ComponentController's entries (a list of { Component, ... }) without referencing NGO.</summary>
        private static IEnumerable<Component> ReadComponentControllerTargets(Component controller)
        {
            var result = new List<Component>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo field in controller.GetType().GetFields(flags))
            {
                if (!(field.GetValue(controller) is IList list)) continue;

                foreach (object entry in list)
                {
                    if (entry == null) continue;
                    if (entry is Component direct)
                    {
                        result.Add(direct);
                        continue;
                    }

                    foreach (FieldInfo entryField in entry.GetType().GetFields(flags))
                    {
                        if (entryField.GetValue(entry) is Component component && component != null && !(component is Transform))
                        {
                            result.Add(component);
                        }
                    }
                }
            }

            return result;
        }

        private static string GetPath(GameObject go)
        {
            if (go == null) return "(destroyed)";
            string path = go.name;
            for (Transform t = go.transform.parent; t != null; t = t.parent)
            {
                path = $"{t.name}/{path}";
            }
            return path;
        }

        private static void Log(string line) => s_Report.AppendLine(line);

        private static void Warn(string line)
        {
            s_Warnings++;
            s_Report.AppendLine($"WARNING: {line}");
            Debug.LogWarning($"[Fusion Migration] {line}");
        }

        private static void WriteReport()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
                File.WriteAllText(ReportPath, s_Report.ToString());
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Fusion Migration] Couldn't write {ReportPath}: {exception.Message}");
            }
        }

        #endregion
    }
}
