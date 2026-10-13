using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Opens one of the menu's settings screens (Graphics, Audio, Change Keybindings) on top of a running
    /// game - Esc > Settings in a match. The very same scene and controller as from the main menu, loaded
    /// additively over the gameplay scene, so there's one copy of each screen to maintain.
    ///
    /// What changes for a screen opened this way (each controller calls <see cref="PrepareIfOverlay"/> in
    /// Awake and <see cref="TryClose"/> from Back):
    ///   - Back (and B / Esc) closes the screen and returns to the Escape menu's Settings list, instead of
    ///     loading the Settings menu scene - which would end the match;
    ///   - the scene's own EventSystem and AudioListener are switched off (the gameplay scene has its own);
    ///   - its background is slightly see-through, so the game shows faintly behind it.
    /// Changes apply straight away, as they do from the main menu (new keybindings reach the player's input
    /// through <see cref="InputBindingsStore.Saved"/>).
    /// </summary>
    public static class SettingsOverlay
    {
        private static string s_SceneName;
        private static Scene s_Scene;
        private static bool s_HasScene;
        private static bool s_Closing;

        private static UIDocument s_Document;

        /// <summary>The last frame the screen itself received a Cancel (B / Esc) - see <see cref="SendCancel"/>.</summary>
        public static int LastCancelFrame { get; private set; } = -1;

        /// <summary>True from <see cref="Open"/> until the screen has been closed.</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>Raised once the screen has gone (Back) - InGameMenu shows its Settings list again.</summary>
        public static event Action Closed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        // A full scene change (the round ended, the player left...) takes the overlay with it.
        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode == LoadSceneMode.Single) Reset();
        }

        private static void Reset()
        {
            IsOpen = false;
            s_Closing = false;
            s_SceneName = null;
            s_HasScene = false;
            s_Document = null;
        }

        /// <summary>Loads settings screen <paramref name="sceneName"/> over the current game.</summary>
        public static void Open(string sceneName)
        {
            if (IsOpen || string.IsNullOrEmpty(sceneName)) return;
            IsOpen = true;
            s_Closing = false;
            s_SceneName = sceneName;
            s_HasScene = false;
            SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        }

        /// <summary>Is <paramref name="page"/> the screen opened over the game?</summary>
        public static bool IsOverlay(Component page)
        {
            if (!IsOpen || page == null) return false;
            Scene scene = page.gameObject.scene;
            if (s_HasScene) return scene == s_Scene;
            return scene.name == s_SceneName && SceneManager.sceneCount > 1;
        }

        /// <summary>Called by each settings screen's Awake: adapts it if it's the in-game overlay (see the class summary).</summary>
        public static void PrepareIfOverlay(Component page, UIDocument document)
        {
            if (!IsOverlay(page)) return;
            Scene scene = page.gameObject.scene;
            s_Scene = scene;
            s_HasScene = true;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (EventSystem eventSystem in root.GetComponentsInChildren<EventSystem>(true)) eventSystem.gameObject.SetActive(false);
                foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true)) listener.enabled = false;
            }

            if (document != null)
            {
                s_Document = document;
                document.sortingOrder = 1000f; // above the HUD, if they share a panel
                document.rootVisualElement.style.backgroundColor = new Color(0f, 0f, 0f, 0.9f);
                document.rootVisualElement.RegisterCallback<NavigationCancelEvent>(_ => LastCancelFrame = Time.frameCount, TrickleDown.TrickleDown);
            }
        }

        /// <summary>
        /// B / Esc for the screen, sent by InGameMenu when the UI's own Cancel didn't arrive (it doesn't always
        /// reach UI in the gameplay scene). Goes to the focused element, so the screen's own handling applies -
        /// e.g. B undoes a dropdown edit first rather than leaving.
        /// </summary>
        public static void SendCancel()
        {
            VisualElement root = s_Document != null ? s_Document.rootVisualElement : null;
            if (root == null || root.panel == null) return;
            VisualElement target = root.panel.focusController?.focusedElement as VisualElement ?? root;
            using (NavigationCancelEvent evt = NavigationCancelEvent.GetPooled())
            {
                evt.target = target;
                target.SendEvent(evt);
            }
        }

        /// <summary>
        /// A settings screen's Back: if it's the in-game overlay, closes it (and returns true - the caller must
        /// NOT load the Settings scene). Safe to call more than once while it's closing.
        /// </summary>
        public static bool TryClose(Component page)
        {
            if (!IsOverlay(page)) return false;
            if (s_Closing) return true;
            s_Closing = true;

            AsyncOperation unload = SceneManager.UnloadSceneAsync(page.gameObject.scene);
            if (unload == null) Finish();
            else unload.completed += _ => Finish();
            return true;
        }

        private static void Finish()
        {
            Reset();
            Closed?.Invoke();
        }
    }
}
