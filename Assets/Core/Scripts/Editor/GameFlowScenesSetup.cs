using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-time setup helper that builds the game's boot flow as three separate scenes - Splash Screen,
    /// Title Screen, Main Menu - which is Unity's standard way to structure this (each screen is its
    /// own scene, loaded in sequence via SceneManager.LoadScene, rather than one scene with UI panels
    /// toggled on and off). Registers all three in Build Settings, in that order, ahead of every scene
    /// already listed there, so a build actually launches into the splash screen first (Unity always
    /// boots into build index 0) while every existing test scene stays reachable exactly as before,
    /// just shifted later in the list.
    ///
    /// Each scene gets a single GameObject with a UIDocument - reusing the same BlocksPanelSettings
    /// asset GameNetworkUI already uses, for consistent scaling - plus one of SplashScreenController /
    /// TitleScreenController / MainMenuController. See those classes for what each screen actually does;
    /// none of them use a UXML template, since every screen here is simple enough that a separate
    /// template file wouldn't pull its weight.
    ///
    /// The Main Menu scene additionally gets its own instance of the shared "[BB] NetworkManager"
    /// prefab (so its Host/Client buttons have a live GameNetworkManager to call into - see
    /// MainMenuController) and an EventSystem (so its buttons actually receive clicks at all - Splash
    /// and Title have no clickable elements, so they don't need one).
    ///
    /// Usage: Friendslop > Build Splash, Title And Main Menu Scenes. Safe to re-run, but note that
    /// re-running OVERWRITES all three scene files from scratch - don't hand-edit them afterward if you
    /// plan to re-run this later. Saves (or prompts to save) any currently open scene first via Unity's
    /// own save-changes dialog, and reopens whatever scene was active beforehand once done, so running
    /// this doesn't disrupt whatever you were working on.
    /// </summary>
    public static class GameFlowScenesSetup
    {
        private const string SceneFolder = "Assets/Core/Scenes";
        private const string SplashScenePath = SceneFolder + "/SplashScreen.unity";
        private const string TitleScenePath = SceneFolder + "/TitleScreen.unity";
        private const string MainMenuScenePath = SceneFolder + "/MainMenu.unity";

        private const string PanelSettingsPath = "Assets/Blocks/Common/BlocksPanelSettings.asset";
        private const string NetworkManagerPrefabPath = "Assets/Core/Prefabs/[BB] NetworkManager.prefab";

        [MenuItem("Friendslop/Build Splash, Title And Main Menu Scenes")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[Friendslop] Cancelled - you have unsaved scene changes. Save or discard them first, then re-run this.");
                return;
            }

            string originalScenePath = EditorSceneManager.GetActiveScene().path;

            var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panelSettings == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find the PanelSettings at '{PanelSettingsPath}'. Has it moved?");
                return;
            }

            if (!AssetDatabase.IsValidFolder(SceneFolder))
            {
                Directory.CreateDirectory(SceneFolder);
                AssetDatabase.Refresh();
            }

            BuildSplashScene(panelSettings);
            BuildTitleScene(panelSettings);
            BuildMainMenuScene(panelSettings);

            RegisterInBuildSettings();

            if (!string.IsNullOrEmpty(originalScenePath))
            {
                EditorSceneManager.OpenScene(originalScenePath);
            }

            Debug.Log("[Friendslop] Built SplashScreen, TitleScreen and MainMenu scenes and added them to the top of Build Settings " +
                "(everything already there is preserved, just shifted later). Press Play from SplashScreen, or just build the game, to try the whole flow.");
        }

        private static void BuildSplashScene(PanelSettings panelSettings)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var uiObject = new GameObject("SplashScreenUI");
            var uiDocument = uiObject.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettings;
            uiObject.AddComponent<SplashScreenController>();

            EditorSceneManager.SaveScene(scene, SplashScenePath);
        }

        private static void BuildTitleScene(PanelSettings panelSettings)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var uiObject = new GameObject("TitleScreenUI");
            var uiDocument = uiObject.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettings;
            uiObject.AddComponent<TitleScreenController>();

            EditorSceneManager.SaveScene(scene, TitleScenePath);
        }

        private static void BuildMainMenuScene(PanelSettings panelSettings)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var uiObject = new GameObject("MainMenuUI");
            var uiDocument = uiObject.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettings;
            uiObject.AddComponent<MainMenuController>();

            CreateEventSystem();
            InstantiateNetworkManager();

            EditorSceneManager.SaveScene(scene, MainMenuScenePath);
        }

        /// <summary>
        /// Adds an EventSystem + InputSystemUIInputModule, resolved by type name via reflection rather
        /// than a direct reference, since this project's Input System package (and UGUI's EventSystem)
        /// aren't referenced by this assembly's .asmdef - only the runtime assembly references them.
        /// Reflection sidesteps having to hand-edit that .asmdef's GUID reference list. The exact
        /// namespace/assembly names below are copied from this project's own existing scenes' EventSystem
        /// objects (see e.g. [BB] Core.unity), not guessed.
        /// </summary>
        private static void CreateEventSystem()
        {
            Type eventSystemType = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
            Type inputModuleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

            if (eventSystemType == null || inputModuleType == null)
            {
                Debug.LogError("[Friendslop] Couldn't resolve EventSystem/InputSystemUIInputModule by type name - " +
                    "add an EventSystem (GameObject > UI Toolkit > Event System) to the Main Menu scene by hand, or its buttons won't receive clicks.");
                return;
            }

            var eventSystemObject = new GameObject("EventSystem");
            eventSystemObject.AddComponent(eventSystemType);
            eventSystemObject.AddComponent(inputModuleType);
        }

        private static void InstantiateNetworkManager()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkManagerPrefabPath);
            if (prefab == null)
            {
                Debug.LogError($"[Friendslop] Couldn't find the NetworkManager prefab at '{NetworkManagerPrefabPath}'. " +
                    "Host/Client on the Main Menu won't work until one is added to that scene by hand.");
                return;
            }

            PrefabUtility.InstantiatePrefab(prefab);
        }

        private static void RegisterInBuildSettings()
        {
            string[] flowScenes = { SplashScenePath, TitleScenePath, MainMenuScenePath };

            List<EditorBuildSettingsScene> existing = EditorBuildSettings.scenes
                .Where(s => !flowScenes.Contains(s.path))
                .ToList();

            var combined = new List<EditorBuildSettingsScene>();
            foreach (string path in flowScenes)
            {
                combined.Add(new EditorBuildSettingsScene(path, true));
            }
            combined.AddRange(existing);

            EditorBuildSettings.scenes = combined.ToArray();
        }
    }
}
