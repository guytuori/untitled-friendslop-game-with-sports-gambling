using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper that wires up the score tracker and round timer:
    /// - Creates the shared GameRulesConfig asset (starting score, round duration) if it doesn't exist yet.
    /// - Adds a PlayerScore component to the "[BB] CorePlayer" prefab, pointed at that config.
    /// - Places a single RoundTimer NetworkObject directly in the "[BB] Core" gameplay scene, also pointed at
    ///   that config.
    ///
    /// CoreHUD reads both of these to draw the scoreboard (left-middle) and the round timer (top-middle) - see
    /// CoreHUD's "Scoreboard & Round Timer" region.
    ///
    /// Usage: Friendslop > Setup Game Rules (Score & Timer). Safe to re-run - it only creates what's missing,
    /// it never duplicates the PlayerScore component or the scene's RoundTimer, and it never overwrites an
    /// existing GameRulesConfig asset's values. Saves (or prompts to save) any currently open scene first, and
    /// reopens whatever scene was active beforehand once done.
    /// </summary>
    public static class GameRulesSetup
    {
        private const string GameRulesConfigPath = "Assets/Core/Data/GameRulesConfig.asset";
        private const string PlayerPrefabPath = "Assets/Core/Prefabs/[BB] CorePlayer.prefab";
        private const string GameplayScenePath = "Assets/Core/Scenes/[BB] Core.unity";

        [MenuItem("Friendslop/Setup Game Rules (Score & Timer)")]
        public static void Setup()
        {
            var config = GetOrCreateGameRulesConfig();
            if (config == null) return;

            AddPlayerScoreToPlayerPrefab(config);
            AddRoundTimerToGameplayScene(config);

            Debug.Log("[Friendslop] Game rules are wired up: every player now has a PlayerScore (starting score " +
                $"{config.startingScore}, from '{GameRulesConfigPath}'), and the gameplay scene has a RoundTimer " +
                $"counting down from {config.roundDurationSeconds}s. CoreHUD shows both automatically.");
        }

        private static GameRulesConfig GetOrCreateGameRulesConfig()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameRulesConfig>(GameRulesConfigPath);
            if (existing != null) return existing;

            string folder = Path.GetDirectoryName(GameRulesConfigPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogError($"[Friendslop] Expected data folder '{folder}' to already exist. Has it moved?");
                return null;
            }

            var config = ScriptableObject.CreateInstance<GameRulesConfig>();
            AssetDatabase.CreateAsset(config, GameRulesConfigPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Friendslop] Created '{GameRulesConfigPath}' with the default starting score (1000) and " +
                "round duration (120s). Edit that asset directly to change either.");
            return config;
        }

        private static void AddPlayerScoreToPlayerPrefab(GameRulesConfig config)
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
                var playerScore = root.GetComponent<PlayerScore>();
                bool wasAlreadyPresent = playerScore != null;
                if (!wasAlreadyPresent)
                {
                    playerScore = root.AddComponent<PlayerScore>();
                }

                AssignGameRulesConfig(playerScore, config);

                PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);

                Debug.Log(wasAlreadyPresent
                    ? $"[Friendslop] '{PlayerPrefabPath}' already had a PlayerScore - just re-pointed it at '{GameRulesConfigPath}'."
                    : $"[Friendslop] Added PlayerScore to '{PlayerPrefabPath}', pointed at '{GameRulesConfigPath}'.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static void AddRoundTimerToGameplayScene(GameRulesConfig config)
        {
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

            var existingTimer = Object.FindFirstObjectByType<RoundTimer>();
            bool wasAlreadyPresent = existingTimer != null;

            var timer = existingTimer;
            if (!wasAlreadyPresent)
            {
                var timerObject = new GameObject("RoundTimer");
                timer = timerObject.AddComponent<RoundTimer>();
            }

            AssignGameRulesConfig(timer, config);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Debug.Log(wasAlreadyPresent
                ? $"[Friendslop] '{GameplayScenePath}' already had a RoundTimer - just re-pointed it at '{GameRulesConfigPath}'."
                : $"[Friendslop] Added a RoundTimer to '{GameplayScenePath}', pointed at '{GameRulesConfigPath}'.");

            if (!string.IsNullOrEmpty(originalScenePath) && originalScenePath != GameplayScenePath)
            {
                EditorSceneManager.OpenScene(originalScenePath);
            }
        }

        /// <summary>
        /// Sets the private, serialized "gameRulesConfig" field shared by PlayerScore and RoundTimer via
        /// SerializedObject, matching this project's established pattern (see WallClimbSetup, GrindRailSetup,
        /// PoleGrabSetup) for wiring private fields from editor code without loosening their access modifiers.
        /// </summary>
        private static void AssignGameRulesConfig(Object target, GameRulesConfig config)
        {
            var serializedObject = new SerializedObject(target);
            var property = serializedObject.FindProperty("gameRulesConfig");
            if (property == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find a 'gameRulesConfig' field on '{target.GetType().Name}'. " +
                    "Assign the GameRulesConfig asset to it by hand in the Inspector.");
                return;
            }

            property.objectReferenceValue = config;
            serializedObject.ApplyModifiedProperties();
        }
    }
}
