using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The single "make everything in the scene match the current state of its assets" button. Replaces the
    /// old one-tool-per-feature setup scripts (GameRulesSetup, ChallengeSystemSetup, BalanceBeamSetup,
    /// WallClimbSetup, PoleGrabSetup, GrindRailSetup) with one idempotent pass, run whenever you want to be
    /// sure a scene/prefab is fully caught up with recent code or asset changes before testing:
    ///
    /// - PLAYERS: finds every player prefab in the project (any prefab whose root has a CorePlayerManager -
    ///   not a hardcoded path, so a renamed/second player prefab is picked up automatically), and makes sure
    ///   it has EVERY concrete component type implementing <see cref="IPlayerAddon"/> or
    ///   <see cref="IPlayerRequiredComponent"/> found anywhere in the project (via UnityEditor.TypeCache -
    ///   no hardcoded component list). A brand new ability/addon just needs to implement one of those two
    ///   interfaces to be added to every player prefab automatically the next time this runs.
    ///
    /// - GAME LOGIC ENTITIES (RoundTimer, ChallengeManager, and anything added later): any concrete type
    ///   implementing <see cref="ISceneSingleton"/> is found the same way and ensured to exist exactly once,
    ///   directly in the gameplay scene (created if missing). A brand new manager-style class just needs to
    ///   implement ISceneSingleton to be swept up here too, with zero changes needed to this file.
    ///
    /// - CROSS-REFERENCE WIRING, generic in both directions:
    ///     1. Sibling field copy (<see cref="WireSiblingFields"/>): among the components just ensured to
    ///        exist together (all of one player prefab's components, or one scene singleton on its own),
    ///        any serialized field sharing the same NAME and exact TYPE on two different components gets its
    ///        value copied from whichever side already has it set to whichever side is still null - e.g.
    ///        CoreInputHandler's "onJumpPressed" GameEvent onto WallClimbAbility/GrindRailAbility/
    ///        PoleGrabAbility's own "onJumpPressed" field, or "onGrabStateChanged" between CoreInputHandler
    ///        and CorePlayerManager. No field/type name is hardcoded - it works for any matching pair.
    ///     2. Singleton ScriptableObject auto-fill (<see cref="AutoWireSingletonAssets"/>): for anything
    ///        sibling copy didn't fill in, any still-unassigned serialized field whose type derives from
    ///        ScriptableObject is wired to the one existing project asset of that exact type (found via
    ///        AssetDatabase, not a hardcoded path), or a freshly created default instance if none exists yet.
    ///        If more than one asset of that type exists, the field is left alone and a warning is logged
    ///        instead of guessing. This is what keeps GameRulesConfig wired on PlayerScore/RoundTimer, and
    ///        the project's StatDepletedEvent wired on ChallengeManager, without this script knowing either
    ///        of those types by name.
    ///
    /// - MAPS: any MeshFilter+MeshRenderer anywhere in the scene whose shared mesh has more than one submesh
    ///   (this project's own signature for "an imported map piece" - every map is exported one submesh per
    ///   material/texture) gets <see cref="MapColliderSetup.RunOnMapObject"/> and
    ///   <see cref="MapObstacleSetup.RunOnMapObject"/> re-run on it automatically - the same per-material
    ///   collider splitting (ice, climbable walls, challenge start/finish triggers) and balance-beam/grind-
    ///   rail/pole extraction those tools already do, just applied to every map piece in the scene instead of
    ///   one at a time by hand.
    ///
    /// Safe to re-run any time, as often as you like: every step only creates or wires what's missing or
    /// still unset, and never duplicates a component or overwrites a reference someone already deliberately
    /// assigned. Saves the gameplay scene and any changed/created project assets when done, and reopens
    /// whatever scene you had open before running this.
    ///
    /// Usage: Friendslop > Refresh Everything From Assets.
    /// </summary>
    public static class SceneRefreshSetup
    {
        private const string GameplayScenePath = "Assets/Core/Scenes/[BB] Core.unity";
        private const string GeneratedAssetFolder = "Assets/Core/Data/Generated";

        [MenuItem("Friendslop/Refresh Everything From Assets")]
        public static void RefreshAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Friendslop] Cancelled - you have unsaved scene changes. Save or discard them first, then re-run this.");
                return;
            }

            s_SingletonAssetCache.Clear();
            string originalScenePath = EditorSceneManager.GetActiveScene().path;

            var summary = new RefreshSummary();

            RefreshPlayerPrefabs(summary);

            Scene scene = EditorSceneManager.OpenScene(GameplayScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"[Friendslop] Couldn't open the gameplay scene at '{GameplayScenePath}'. Has it moved? Player prefabs were still refreshed - re-run this once the scene path is fixed to finish the rest.");
                return;
            }

            RefreshSceneSingletons(summary);
            RefreshMapPieces(scene, summary);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            if (!string.IsNullOrEmpty(originalScenePath) && originalScenePath != GameplayScenePath)
            {
                EditorSceneManager.OpenScene(originalScenePath);
            }

            Debug.Log($"[Friendslop] Refresh complete - {summary}");
        }

        private class RefreshSummary
        {
            public int PlayerPrefabsRefreshed;
            public int PlayerComponentsAdded;
            public int SceneSingletonsRefreshed;
            public int SceneSingletonsCreated;
            public int SiblingFieldsWired;
            public int SingletonAssetsWired;
            public int SingletonAssetsCreated;
            public int MapPiecesRefreshed;

            public override string ToString() =>
                $"{PlayerPrefabsRefreshed} player prefab(s) refreshed ({PlayerComponentsAdded} component(s) added), " +
                $"{SceneSingletonsRefreshed} game-logic singleton(s) present ({SceneSingletonsCreated} newly created), " +
                $"{SiblingFieldsWired} sibling field(s) wired, {SingletonAssetsWired} config/event asset reference(s) wired " +
                $"({SingletonAssetsCreated} newly created), {MapPiecesRefreshed} map piece(s) rebuilt. " +
                "Remember to hit Play (or build) and test.";
        }

        #region Players

        private static void RefreshPlayerPrefabs(RefreshSummary summary)
        {
            foreach (string prefabPath in FindPlayerPrefabPaths())
            {
                GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    int addedCount = 0;
                    foreach (Type addonType in GetConcreteTypesDerivedFrom<IPlayerAddon>())
                    {
                        GetOrAddComponent(root, addonType, out bool wasAdded);
                        if (wasAdded) addedCount++;
                    }
                    foreach (Type requiredType in GetConcreteTypesDerivedFrom<IPlayerRequiredComponent>())
                    {
                        GetOrAddComponent(root, requiredType, out bool wasAdded);
                        if (wasAdded) addedCount++;
                    }
                    summary.PlayerComponentsAdded += addedCount;

                    Component[] allComponents = root.GetComponentsInChildren<Component>(true);
                    summary.SiblingFieldsWired += WireSiblingFields(allComponents);
                    foreach (Component component in allComponents)
                    {
                        summary.SingletonAssetsWired += AutoWireSingletonAssets(component, summary);
                    }

                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    summary.PlayerPrefabsRefreshed++;
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        /// <summary>Any prefab whose root GameObject has a CorePlayerManager is treated as a player prefab - no hardcoded path, so a second/third player prefab (or one renamed/moved) is picked up automatically.</summary>
        private static List<string> FindPlayerPrefabPaths()
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.GetComponent<CorePlayerManager>() != null)
                {
                    paths.Add(path);
                }
            }
            return paths;
        }

        #endregion

        #region Game logic entities (scene singletons)

        private static void RefreshSceneSingletons(RefreshSummary summary)
        {
            foreach (Type type in GetConcreteTypesDerivedFrom<ISceneSingleton>())
            {
                UnityEngine.Object[] existingObjects = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
                var existing = existingObjects.Length > 0 ? existingObjects[0] as Component : null;
                bool wasCreated = existing == null;

                Component instance = existing;
                if (wasCreated)
                {
                    var go = new GameObject(type.Name);
                    instance = go.AddComponent(type);
                }

                summary.SiblingFieldsWired += WireSiblingFields(new[] { instance });
                summary.SingletonAssetsWired += AutoWireSingletonAssets(instance, summary);

                EditorUtility.SetDirty(instance);
                summary.SceneSingletonsRefreshed++;
                if (wasCreated) summary.SceneSingletonsCreated++;
            }
        }

        #endregion

        #region Maps

        private static void RefreshMapPieces(Scene scene, RefreshSummary summary)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MeshFilter meshFilter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (meshFilter.sharedMesh == null || meshFilter.sharedMesh.subMeshCount <= 1) continue;
                    if (meshFilter.GetComponent<MeshRenderer>() == null) continue;

                    MapColliderSetup.RunOnMapObject(meshFilter.gameObject);

                    // MapObstacleSetup.RunOnMapObject logs an error if none of its three obstacle
                    // keywords are present - fine when a person deliberately selects one known obstacle
                    // map piece by hand, but this runs on every qualifying mesh in the scene, and most map
                    // pieces (plain floors/walls/ice) legitimately have none of them. Skip the call
                    // entirely for those instead of letting it log a false "error" every single refresh.
                    if (HasObstacleMaterial(meshFilter.GetComponent<MeshRenderer>()))
                    {
                        MapObstacleSetup.RunOnMapObject(meshFilter.gameObject);
                    }

                    summary.MapPiecesRefreshed++;
                }
            }
        }

        // Keep in sync with MapObstacleSetup's own CheckerKeyword/RailKeyword/PoleKeyword constants.
        private static readonly string[] ObstacleMaterialKeywords = { "checker", "bluecarpet", "pillar" };

        private static bool HasObstacleMaterial(MeshRenderer meshRenderer)
        {
            if (meshRenderer == null) return false;

            foreach (Material material in meshRenderer.sharedMaterials)
            {
                if (material == null) continue;
                string nameLower = material.name.ToLowerInvariant();
                foreach (string keyword in ObstacleMaterialKeywords)
                {
                    if (nameLower.Contains(keyword)) return true;
                }
            }
            return false;
        }

        #endregion

        #region Generic field wiring

        /// <summary>
        /// For every pair of components in <paramref name="components"/>, copies any Unity-serialized field
        /// that shares the same NAME and exact TYPE on both sides from whichever side already has a non-null
        /// value to whichever side is still null. This is what wires e.g. CoreInputHandler's "onJumpPressed"
        /// onto WallClimbAbility/GrindRailAbility/PoleGrabAbility, and "onGrabStateChanged" between
        /// CoreInputHandler and CorePlayerManager - without either field's name being hardcoded here.
        /// </summary>
        private static int WireSiblingFields(IReadOnlyList<Component> components)
        {
            int wiredCount = 0;

            // Field name -> the first non-null (fieldType, value) found for it among these components.
            var knownValues = new Dictionary<string, (Type fieldType, UnityEngine.Object value)>();

            foreach (Component component in components)
            {
                if (component == null) continue;
                foreach (FieldInfo field in GetUnitySerializedFields(component.GetType()))
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;

                    var value = field.GetValue(component) as UnityEngine.Object;
                    if (value == null) continue;
                    if (!knownValues.ContainsKey(field.Name))
                    {
                        knownValues[field.Name] = (field.FieldType, value);
                    }
                }
            }

            foreach (Component component in components)
            {
                if (component == null) continue;
                foreach (FieldInfo field in GetUnitySerializedFields(component.GetType()))
                {
                    if (!typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType)) continue;
                    if (field.GetValue(component) != null) continue;
                    if (!knownValues.TryGetValue(field.Name, out var known)) continue;
                    if (known.fieldType != field.FieldType) continue;

                    field.SetValue(component, known.value);
                    EditorUtility.SetDirty(component);
                    wiredCount++;
                }
            }

            return wiredCount;
        }

        /// <summary>
        /// For any currently-unassigned serialized field on <paramref name="component"/> whose type derives
        /// from ScriptableObject, wires it to the single existing project asset of that exact type (creating
        /// a fresh default instance if none exists yet), found purely via an AssetDatabase type search - no
        /// hardcoded type or field name anywhere. If more than one asset of that type exists, the field is
        /// left unassigned and a warning is logged instead of guessing which one is canonical.
        /// </summary>
        private static int AutoWireSingletonAssets(Component component, RefreshSummary summary)
        {
            if (component == null) return 0;
            int wiredCount = 0;

            foreach (FieldInfo field in GetUnitySerializedFields(component.GetType()))
            {
                if (!typeof(ScriptableObject).IsAssignableFrom(field.FieldType)) continue;
                if (field.GetValue(component) != null) continue;

                ScriptableObject asset = FindOrCreateSingletonAsset(field.FieldType, summary);
                if (asset == null) continue;

                field.SetValue(component, asset);
                EditorUtility.SetDirty(component);
                wiredCount++;
            }

            return wiredCount;
        }

        private static readonly Dictionary<Type, ScriptableObject> s_SingletonAssetCache = new Dictionary<Type, ScriptableObject>();

        private static ScriptableObject FindOrCreateSingletonAsset(Type type, RefreshSummary summary)
        {
            if (s_SingletonAssetCache.TryGetValue(type, out ScriptableObject cached) && cached != null) return cached;

            string[] guids = AssetDatabase.FindAssets($"t:{type.Name}");
            if (guids.Length > 1)
            {
                Debug.LogWarning($"[Friendslop] Found {guids.Length} '{type.Name}' assets in the project - can't tell which one is canonical, so left any unassigned '{type.Name}' field as-is. Assign it by hand, or delete the duplicates.");
                return null;
            }

            ScriptableObject asset;
            if (guids.Length == 1)
            {
                asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            else
            {
                if (!AssetDatabase.IsValidFolder(GeneratedAssetFolder))
                {
                    Directory.CreateDirectory(GeneratedAssetFolder);
                    AssetDatabase.Refresh();
                }

                asset = ScriptableObject.CreateInstance(type);
                string path = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedAssetFolder}/{type.Name}.asset");
                AssetDatabase.CreateAsset(asset, path);
                AssetDatabase.SaveAssets();
                summary.SingletonAssetsCreated++;
                Debug.LogWarning($"[Friendslop] No '{type.Name}' asset existed yet - created one with default values at '{path}'. Review/tune it.");
            }

            s_SingletonAssetCache[type] = asset;
            return asset;
        }

        #endregion

        #region Reflection helpers

        private static IEnumerable<Type> GetConcreteTypesDerivedFrom<T>()
        {
            foreach (Type type in TypeCache.GetTypesDerivedFrom<T>())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                if (!typeof(Component).IsAssignableFrom(type)) continue;
                yield return type;
            }
        }

        private static Component GetOrAddComponent(GameObject root, Type type, out bool wasAdded)
        {
            var existing = root.GetComponent(type);
            wasAdded = existing == null;
            return wasAdded ? root.AddComponent(type) : existing;
        }

        /// <summary>Walks up the type hierarchy (stopping at UnityEngine.Object) collecting every field Unity would actually serialize: public fields, plus private/protected fields marked [SerializeField] - excluding static and readonly fields and anything marked [NonSerialized], matching Unity's own serialization rules.</summary>
        private static IEnumerable<FieldInfo> GetUnitySerializedFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null && t != typeof(UnityEngine.Object); t = t.BaseType)
            {
                foreach (FieldInfo field in t.GetFields(flags))
                {
                    if (IsUnitySerializedField(field)) yield return field;
                }
            }
        }

        private static bool IsUnitySerializedField(FieldInfo field)
        {
            if (field.IsStatic || field.IsInitOnly) return false;
            if (field.GetCustomAttribute<NonSerializedAttribute>() != null) return false;
            if (field.IsPublic) return true;
            return field.GetCustomAttribute<SerializeField>() != null;
        }

        #endregion
    }
}
