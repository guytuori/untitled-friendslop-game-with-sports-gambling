using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper for the challenge/betting system:
    /// - Places a single ChallengeManager NetworkObject directly in the "[BB] Core" gameplay scene (the
    ///   same way RoundTimer is placed - see GameRulesSetup), pointed at the project's shared
    ///   OnStatDepleted event so its death penalty actually fires.
    /// - Adds a ChallengePauseGate to the "[BB] CorePlayer" prefab, so every player actually freezes during
    ///   a challenge's betting window.
    ///
    /// Run this once; safe to re-run (it only creates/adds what's missing, and never overwrites an existing
    /// ChallengeManager's tuned values). Individual challenges themselves (ChallengeZone + its start/finish
    /// triggers) are set up separately, per map piece, by
    /// "Friendslop > Split Selected Map Collider By Material" - see MapColliderSetup.
    /// </summary>
    public static class ChallengeSystemSetup
    {
        private const string PlayerPrefabPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";
        private const string GameplayScenePath = "Assets/Core/Scenes/[BB] Core.unity";
        private const string OnStatDepletedEventPath = "Assets/Core/GameEvents/Stats/OnStatDepleted.asset";

        [MenuItem("Friendslop/Setup Challenge System")]
        public static void Setup()
        {
            AddChallengePauseGateToPlayerPrefab();
            AddChallengeManagerToGameplayScene();

            Debug.Log("[Friendslop] Challenge system is wired up: every player freezes during a betting window, " +
                "and the gameplay scene has a ChallengeManager applying the death penalty and running betting " +
                "windows. Now place challenges themselves by running 'Friendslop > Split Selected Map Collider " +
                "By Material' on each challenge map piece (e.g. test_jump_challenge_1) - see MapColliderSetup.");
        }

        private static void AddChallengePauseGateToPlayerPrefab()
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find the player prefab at '{PlayerPrefabPath}'. Has it moved?");
                return;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(PlayerPrefabPath);
            try
            {
                bool wasAlreadyPresent = root.GetComponent<ChallengePauseGate>() != null;
                if (!wasAlreadyPresent)
                {
                    root.AddComponent<ChallengePauseGate>();
                }

                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);

                Debug.Log(wasAlreadyPresent
                    ? $"[Friendslop] '{PlayerPrefabPath}' already had a ChallengePauseGate."
                    : $"[Friendslop] Added ChallengePauseGate to '{PlayerPrefabPath}'.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void AddChallengeManagerToGameplayScene()
        {
            var onStatDepletedEvent = AssetDatabase.LoadAssetAtPath<StatDepletedEvent>(OnStatDepletedEventPath);
            if (onStatDepletedEvent == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find the OnStatDepleted event at '{OnStatDepletedEventPath}'. Has it moved? The death penalty won't fire without it.");
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Friendslop] Cancelled - you have unsaved scene changes. Save or discard them first, then re-run this.");
                return;
            }

            string originalScenePath = EditorSceneManager.GetActiveScene().path;

            Scene scene = EditorSceneManager.OpenScene(GameplayScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"[Friendslop] Couldn't open the gameplay scene at '{GameplayScenePath}'. Has it moved?");
                return;
            }

            var existingManager = Object.FindFirstObjectByType<ChallengeManager>();
            bool wasAlreadyPresent = existingManager != null;

            var manager = existingManager;
            if (!wasAlreadyPresent)
            {
                var managerObject = new GameObject("ChallengeManager");
                manager = managerObject.AddComponent<ChallengeManager>();
            }

            if (onStatDepletedEvent != null)
            {
                var serializedManager = new SerializedObject(manager);
                var eventProperty = serializedManager.FindProperty("onStatDepletedEvent");
                if (eventProperty != null)
                {
                    eventProperty.objectReferenceValue = onStatDepletedEvent;
                    serializedManager.ApplyModifiedProperties();
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log(wasAlreadyPresent
                ? $"[Friendslop] '{GameplayScenePath}' already had a ChallengeManager - re-pointed its death-penalty event."
                : $"[Friendslop] Added a ChallengeManager to '{GameplayScenePath}'.");

            if (!string.IsNullOrEmpty(originalScenePath) && originalScenePath != GameplayScenePath)
            {
                EditorSceneManager.OpenScene(originalScenePath);
            }
        }
    }
}
