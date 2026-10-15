using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The menus' shared look and movement, with nothing to set up per screen:
    ///
    /// BACKDROP - behind every menu screen, a darkened, blurred copy of the title screen picture
    /// (Resources/MenuBackground.png), slightly zoomed and drifting slowly. It's one persistent UI
    /// document (sorted behind the menus' own) that lives across scene loads, and its drift is worked out
    /// from the clock, so it carries on smoothly from screen to screen instead of starting over. The menu
    /// screens' own black backgrounds are made see-through when they load. Hidden on the splash and title
    /// screens and in gameplay; it fades in when the menus are first reached.
    ///
    /// TRANSITIONS - menu screens call <see cref="LoadScene"/> instead of SceneManager.LoadScene: the current
    /// screen slides a little to the side and fades out (<see cref="OutSeconds"/>, input ignored meanwhile),
    /// then the next one slides in from the other side and fades in (<see cref="InSeconds"/>). Going deeper
    /// (Main Menu > Settings > Audio) moves left; going back (<see cref="Depth"/> decreases) moves right, so
    /// the menus feel like a stack. Screens opened over a running game (Esc > Settings) are additive loads
    /// and aren't touched - they still appear instantly.
    ///
    /// FULL-SIZE LOGO - the big centred logo (Main Menu, Settings, Single Player, Join Game, Join Public:
    /// an Image straight under the screen's root, 99% wide) doesn't slide with its screen. The screen's own
    /// copy is hidden and an identical one is drawn on the persistent backdrop layer instead, so between two
    /// screens where it's the same picture in the same place it simply stays put. Going to a screen without
    /// it (the small-logo screens, which slide normally) it fades out where it is; coming back, it fades in
    /// where it is. Never moves. From the title screen (which keeps its logo up while the rest fades) to
    /// Main Menu it's simply there - same picture, same place, no fade.
    /// </summary>
    public static class MenuTransition
    {
        public const float OutSeconds = 0.13f;
        public const float InSeconds = 0.2f;
        public const float SlidePixels = 70f;

        /// <summary>How dark the backdrop is (1 = the picture as is).</summary>
        public const float BackdropBrightness = 0.38f;

        public const string BackdropResource = "MenuBackground";

        /// <summary>Menu screens (get the backdrop and the slide-in).</summary>
        private static readonly HashSet<string> MenuScenes = new HashSet<string>
        {
            "MainMenu", "Settings", "GameplaySettings", "GraphicsSettings", "AudioSettings", "ChangeKeybindings",
            "ChangeProfile", "SinglePlayer", "HostGame", "JoinGame", "JoinPublic", "JoinPrivate", "FindGames",
            "GameBrowser", "GameLobby", "RoundResults", "FinalScore",
        };

        /// <summary>How deep each menu is - moving to a shallower one slides the other way (Back).</summary>
        private static readonly Dictionary<string, int> Depths = new Dictionary<string, int>
        {
            { "MainMenu", 0 },
            { "Settings", 1 }, { "SinglePlayer", 1 }, { "JoinGame", 1 }, { "HostGame", 1 },
            { "GameplaySettings", 2 }, { "GraphicsSettings", 2 }, { "AudioSettings", 2 }, { "ChangeKeybindings", 2 },
            { "ChangeProfile", 2 }, { "JoinPublic", 2 }, { "JoinPrivate", 2 },
            { "FindGames", 3 }, { "GameBrowser", 3 },
            { "GameLobby", 4 },
        };

        private static Runner s_Runner;
        private static bool s_Leaving;
        private static int s_Direction = 1;          // +1 = deeper (slides left), -1 = back (slides right)
        private static bool s_FromMenu;              // the previous screen was a menu (else the backdrop fades in)
        private static string s_PreviousScene;       // the last scene loaded on its own (Single)

        /// <summary>The title screen's scene name - its logo carries straight over to Main Menu's.</summary>
        public const string TitleSceneName = "TitleScreen";

        private static UIDocument s_Backdrop;
        private static Image s_BackdropImage;
        private static Texture2D s_BackdropTexture;

        // The persistent full-size logo (see the class summary).
        public const float LogoFadeSeconds = 0.18f;
        private static Image s_Logo;
        private static float s_LogoOpacity, s_LogoTarget;
        private static Rect s_LogoRect;
        private static bool s_LogoPlaced;
        private static float s_LastTick;

        /// <summary>True while a screen is sliding out (its buttons are ignored).</summary>
        public static bool IsLeaving => s_Leaving;

        public static int Depth(string scene) => scene != null && Depths.TryGetValue(scene, out int d) ? d : int.MaxValue / 2;

        public static bool IsMenuScene(string scene) => scene != null && MenuScenes.Contains(scene);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Runner = null;
            s_Leaving = false;
            s_Direction = 1;
            s_FromMenu = false;
            s_PreviousScene = null;
            s_Backdrop = null;
            s_BackdropImage = null;
            s_Logo = null;
            s_LogoOpacity = s_LogoTarget = 0f;
            s_LogoPlaced = false;
            s_BackdropTexture = null;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        /// <summary>
        /// Use instead of SceneManager.LoadScene from a menu screen: slides this screen out, then loads
        /// <paramref name="sceneName"/>, which slides in. Ignored while a screen is already leaving.
        /// </summary>
        public static void LoadScene(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName) || s_Leaving) return;
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                SceneManager.LoadScene(sceneName); // not in Build Settings: let Unity report it as usual
                return;
            }

            Scene current = SceneManager.GetActiveScene();
            s_Direction = Depth(sceneName) < Depth(current.name) ? -1 : 1;

            List<VisualElement> roots = MenuRoots(current);
            if (roots.Count == 0 || !EnsureRunner())
            {
                SceneManager.LoadScene(sceneName);
                return;
            }

            s_Leaving = true;
            foreach (VisualElement root in roots) root.SetEnabled(false); // no more clicks / navigation
            s_Runner.StartCoroutine(SlideOutThenLoad(roots, sceneName));
        }

        private static IEnumerator SlideOutThenLoad(List<VisualElement> roots, string sceneName)
        {
            float t = 0f;
            while (t < OutSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / OutSeconds);
                float e = k * k; // ease in
                foreach (VisualElement root in roots)
                {
                    if (root == null) continue;
                    root.style.opacity = 1f - e;
                    root.style.translate = new Translate(-s_Direction * SlidePixels * e, 0);
                }
                yield return null;
            }

            s_FromMenu = true;
            s_Leaving = false;
            SceneManager.LoadScene(sceneName);
        }

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return; // e.g. Esc > Settings over a game: leave it alone
            s_Leaving = false;

            bool menu = IsMenuScene(scene.name);
            if (!menu)
            {
                SetBackdropVisible(false);
                s_FromMenu = false;
                s_Direction = 1;
                s_PreviousScene = scene.name;
                return;
            }

            bool fadeBackdrop = !s_FromMenu || s_Backdrop == null || s_BackdropImage == null;
            List<VisualElement> roots = MenuRoots(scene);
            foreach (VisualElement root in roots) root.style.backgroundColor = Color.clear;

            if (roots.Count > 0) EnsureBackdrop(roots[0]);
            AdoptFullLogo(roots);
            SetBackdropVisible(true);

            if (EnsureRunner())
            {
                foreach (VisualElement root in roots)
                {
                    root.style.opacity = 0f;
                    root.style.translate = new Translate(s_Direction * SlidePixels, 0);
                }
                s_Runner.StartCoroutine(SlideIn(roots, fadeBackdrop));
            }

            s_FromMenu = true;
            s_Direction = 1;
            s_PreviousScene = scene.name;
        }

        private static IEnumerator SlideIn(List<VisualElement> roots, bool fadeBackdrop)
        {
            int direction = s_Direction;
            float t = 0f;
            float duration = fadeBackdrop ? Mathf.Max(InSeconds, 0.35f) : InSeconds;
            while (t < duration)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / duration);
                float e = 1f - (1f - k) * (1f - k) * (1f - k); // ease out
                foreach (VisualElement root in roots)
                {
                    if (root == null) continue;
                    root.style.opacity = e;
                    root.style.translate = new Translate(direction * SlidePixels * (1f - e), 0);
                }
                if (fadeBackdrop && s_BackdropImage != null) s_BackdropImage.style.opacity = e;
                yield return null;
            }
            foreach (VisualElement root in roots)
            {
                if (root == null) continue;
                root.style.opacity = 1f;
                root.style.translate = new Translate(0, 0);
            }
            if (s_BackdropImage != null) s_BackdropImage.style.opacity = 1f;
        }

        /// <summary>The root of every UI document in <paramref name="scene"/> that's showing something.</summary>
        private static List<VisualElement> MenuRoots(Scene scene)
        {
            var roots = new List<VisualElement>();
            if (!scene.IsValid() || !scene.isLoaded) return roots;
            foreach (GameObject go in scene.GetRootGameObjects())
            {
                foreach (UIDocument doc in go.GetComponentsInChildren<UIDocument>(false))
                {
                    if (doc == null || doc == s_Backdrop || !doc.isActiveAndEnabled) continue;
                    VisualElement root = doc.rootVisualElement;
                    if (root != null && root.childCount > 0) roots.Add(root);
                }
            }
            return roots;
        }

        // =====================================================================================
        // Backdrop
        // =====================================================================================

        private static void EnsureBackdrop(VisualElement menuRoot)
        {
            if (s_BackdropTexture == null) s_BackdropTexture = Resources.Load<Texture2D>(BackdropResource);
            if (s_BackdropTexture == null) return;

            PanelSettings panel = null;
            foreach (UIDocument doc in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (doc != s_Backdrop && doc.rootVisualElement == menuRoot) { panel = doc.panelSettings; break; }
            }
            if (panel == null) return;

            if (s_Backdrop == null)
            {
                var go = new GameObject("MenuBackdrop");
                go.SetActive(false);
                Object.DontDestroyOnLoad(go);
                s_Backdrop = go.AddComponent<UIDocument>();
                s_Backdrop.panelSettings = panel;
                s_Backdrop.sortingOrder = -1000f; // behind every menu document on the same panel
                go.SetActive(true);
            }
            else if (s_Backdrop.panelSettings != panel)
            {
                s_Backdrop.panelSettings = panel;
            }

            VisualElement root = s_Backdrop.rootVisualElement;
            if (root == null) return;
            if (s_BackdropImage == null || s_BackdropImage.parent != root)
            {
                root.Clear();
                root.style.flexGrow = 1;
                root.style.backgroundColor = Color.black;
                root.style.overflow = Overflow.Hidden;
                root.pickingMode = PickingMode.Ignore;

                s_BackdropImage = new Image
                {
                    image = s_BackdropTexture,
                    scaleMode = ScaleMode.ScaleAndCrop,
                    tintColor = new Color(BackdropBrightness, BackdropBrightness, BackdropBrightness, 1f),
                    pickingMode = PickingMode.Ignore,
                };
                // A bit bigger than the screen, so it can drift without showing an edge.
                s_BackdropImage.style.position = Position.Absolute;
                s_BackdropImage.style.left = Length.Percent(-6);
                s_BackdropImage.style.top = Length.Percent(-6);
                s_BackdropImage.style.width = Length.Percent(112);
                s_BackdropImage.style.height = Length.Percent(112);
                root.Add(s_BackdropImage);

                s_Logo = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                s_Logo.style.position = Position.Absolute;
                // Until a screen's logo has been measured: the standard full-size box (99%, centred), which is
                // where the title screen and the full-logo menus all put it.
                s_Logo.style.left = Length.Percent(0.5f);
                s_Logo.style.top = Length.Percent(0.5f);
                s_Logo.style.width = Length.Percent(99);
                s_Logo.style.height = Length.Percent(99);
                s_Logo.style.opacity = 0f;
                s_LogoOpacity = s_LogoTarget = 0f;
                s_LogoPlaced = false;
                root.Add(s_Logo);

                s_LastTick = Time.realtimeSinceStartup;
                s_BackdropImage.schedule.Execute(Drift).Every(16);
                Drift();
            }
        }

        /// <summary>A slow figure-eight drift, from the real-time clock so it never restarts between screens.</summary>
        private static void Drift()
        {
            if (s_BackdropImage == null) return;
            float now = Time.realtimeSinceStartup;
            float dt = Mathf.Clamp(now - s_LastTick, 0f, 0.1f);
            s_LastTick = now;
            if (s_Logo != null && !Mathf.Approximately(s_LogoOpacity, s_LogoTarget))
            {
                s_LogoOpacity = Mathf.MoveTowards(s_LogoOpacity, s_LogoTarget, dt / LogoFadeSeconds);
                s_Logo.style.opacity = s_LogoOpacity;
            }
            float t = Time.realtimeSinceStartup;
            float x = Mathf.Sin(t * 2f * Mathf.PI / 70f) * 4.5f;   // percent of the image's own size
            float y = Mathf.Sin(t * 2f * Mathf.PI / 47f) * 3f;
            s_BackdropImage.style.translate = new Translate(Length.Percent(x * 0.9f), Length.Percent(y * 0.9f));
        }

        private static void SetBackdropVisible(bool visible)
        {
            if (s_Backdrop == null) return;
            VisualElement root = s_Backdrop.rootVisualElement;
            if (root != null) root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible && s_BackdropImage != null) s_BackdropImage.style.opacity = 0f;
            if (!visible) SetLogo(0f, instant: true);
        }

        // =====================================================================================
        // Full-size logo
        // =====================================================================================

        /// <summary>The screen's big centred logo: an Image straight under a root, 99% wide (MenuUI.AddFullLogo and the inline copies).</summary>
        private static Image FindFullLogo(VisualElement root)
        {
            foreach (VisualElement child in root.Children())
            {
                if (child is Image image && image.image != null &&
                    image.style.width.keyword == StyleKeyword.Undefined &&
                    image.style.width.value.unit == LengthUnit.Percent && image.style.width.value.value >= 90f)
                {
                    return image;
                }
            }
            return null;
        }

        /// <summary>
        /// On a menu screen's arrival: hide its own full-size logo and show the persistent one in the same
        /// place - left as it is if it's already that picture there, faded in otherwise; or fade the persistent
        /// one out if this screen has none.
        /// </summary>
        private static void AdoptFullLogo(List<VisualElement> roots)
        {
            Image own = null;
            foreach (VisualElement root in roots)
            {
                own = FindFullLogo(root);
                if (own != null) break;
            }

            if (own == null || s_Logo == null)
            {
                SetLogo(0f, instant: false);
                return;
            }

            own.style.visibility = Visibility.Hidden; // keeps its place in the layout; ours is drawn instead
            bool same = s_LogoTarget > 0f && s_Logo.image == own.image;
            if (!same)
            {
                s_Logo.image = own.image;
                if (s_PreviousScene == TitleSceneName)
                {
                    // Straight on from the title screen's logo, which stayed up: just be there.
                    SetLogo(1f, instant: true);
                }
                else
                {
                    // A different picture (or none showing): start invisible, fade in once it's placed.
                    SetLogo(0f, instant: true);
                }
            }

            Image target = own;
            void Place(GeometryChangedEvent _)
            {
                if (s_Logo == null || target.panel == null) return;
                Rect r = target.layout; // relative to the screen's root, which fills the panel like ours
                if (r.width < 1f || r.height < 1f) return;
                Rect shown = s_LogoPlaced ? s_LogoRect : DefaultLogoRect(target.parent);
                bool moved = Mathf.Abs(r.x - shown.x) > 2f || Mathf.Abs(r.y - shown.y) > 2f ||
                             Mathf.Abs(r.width - shown.width) > 2f || Mathf.Abs(r.height - shown.height) > 2f;
                if (moved && s_LogoOpacity > 0f)
                {
                    // Somewhere else / another size: never slide it there - drop it out and fade in afresh.
                    SetLogo(0f, instant: true);
                }
                if (moved || !s_LogoPlaced)
                {
                    s_LogoRect = r;
                    s_Logo.style.left = r.x;
                    s_Logo.style.top = r.y;
                    s_Logo.style.width = r.width;
                    s_Logo.style.height = r.height;
                    s_LogoPlaced = true;
                }
                SetLogo(1f, instant: false);
            }
            own.RegisterCallback<GeometryChangedEvent>(Place);
            if (own.layout.width >= 1f) Place(null);
        }

        /// <summary>The standard full-size logo box (99% of the screen, centred) inside <paramref name="parent"/>.</summary>
        private static Rect DefaultLogoRect(VisualElement parent)
        {
            Rect p = parent != null ? parent.layout : new Rect(0, 0, 0, 0);
            return new Rect(p.width * 0.005f, p.height * 0.005f, p.width * 0.99f, p.height * 0.99f);
        }

        private static void SetLogo(float opacity, bool instant)
        {
            s_LogoTarget = opacity;
            if (!instant || s_Logo == null) return;
            s_LogoOpacity = opacity;
            s_Logo.style.opacity = opacity;
        }

        private static bool EnsureRunner()
        {
            if (s_Runner != null) return true;
            var go = new GameObject("MenuTransition") { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(go);
            s_Runner = go.AddComponent<Runner>();
            return true;
        }

        private class Runner : MonoBehaviour { }
    }
}
