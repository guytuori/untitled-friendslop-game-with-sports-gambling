using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Third screen in the boot flow: the same "Parkour Parlay Title Screen" logo as the title screen,
    /// same size and position, over a black background - with Host / Client / Settings / Quit Game
    /// stacked in the bottom-right corner instead of centered. Host and Client call straight into
    /// <see cref="GameNetworkManager"/>'s own StartHostConnection/StartClientConnection - the exact
    /// same methods GameNetworkUI's buttons call in the gameplay test scenes - so this reuses the
    /// project's existing networking entry points rather than reimplementing them.
    ///
    /// Navigable with a gamepad's left stick or d-pad plus the submit button, not just the mouse - see
    /// BuildUI for what that actually takes (an EventSystem + InputSystemUIInputModule is already
    /// enough for UI Toolkit's own navigation and click-on-submit, and this scene has one; the one thing
    /// still needed here is giving the first button focus, since nothing is focused by default and
    /// gamepad navigation has nothing to move from without a starting point).
    ///
    /// Every button plays a click sound (<see cref="selectSound"/> or <see cref="cancelSound"/>) via
    /// AudioVolumeService.PlayOneShot (AudioCategory.SoundEffects) - a thin wrapper around
    /// AudioSource.PlayClipAtPoint that applies the Audio Settings screen's Master/SoundEffects sliders,
    /// still a one-shot fire-and-forget clip rather than a persistent AudioSource. That temporary
    /// GameObject isn't marked DontDestroyOnLoad, so a sound triggered right before this button's own
    /// scene load (Settings, or Host once connected) can get cut short by the unload; this hasn't been
    /// flagged as an issue yet but would need a small delay-before-load, or a persistent DontDestroyOnLoad
    /// AudioSource, to fully fix if it becomes noticeable.
    ///
    /// For that call to have anything to talk to, this scene needs its own GameNetworkManager - unlike
    /// the test scenes, this one doesn't come with the gameplay content that normally carries a
    /// NetworkManager alongside it. This scene carries its own instance of the shared
    /// "[BB] NetworkManager" prefab for exactly that reason; GameNetworkManager.Awake() marks itself
    /// DontDestroyOnLoad, so that same instance survives into whatever scene gets loaded next.
    ///
    /// Once Host is clicked, this loads <see cref="gameplaySceneName"/> - but only on the host/server
    /// side, via NetworkManager's own SceneManager rather than a plain SceneManager.LoadScene. This
    /// project's NetworkConfig has EnableSceneManagement on (see GameNetworkManager's NetworkConfig in
    /// the NetworkManager prefab), which means Netcode itself is responsible for keeping every
    /// connected client's active scene in sync with the host's - a client that called LoadScene on its
    /// own would fight that sync instead of relying on it. That's why OnClientClicked below does
    /// nothing but connect: once connected, Netcode automatically brings that client into whatever
    /// scene the host just loaded (or is about to load), no extra code needed on the client side at all.
    ///
    /// Quit Game exits the build (Application.Quit() is a no-op in the Editor, so this stops Play Mode
    /// there instead).
    ///
    /// Built in code against this GameObject's UIDocument, the same way SplashScreenController and
    /// TitleScreenController are. This scene also carries its own EventSystem, which these buttons need
    /// in order to actually receive clicks.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MainMenuController : MonoBehaviour
    {
        [Tooltip("Same logo as the title screen - wire this up to the same texture as TitleScreenController's titleImage.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("The actual game scene to load once hosting starts. Netcode syncs every connected client to this automatically - see the class summary.")]
        [SerializeField] private string gameplaySceneName = "[BB] Core";

        [Tooltip("Scene to load when Settings is clicked.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when Host, Client, or Settings is selected.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Quit Game is selected.")]
        [SerializeField] private AudioClip cancelSound;

        private void Awake()
        {
            // Keeps gamepad Submit/Cancel on this scene's EventSystem matching the project's actual
            // Nintendo-style pad convention (A/East=confirm, B/South=cancel) instead of Unity's stock
            // Xbox-oriented defaults - see GamepadUIBindingFix for the full explanation. Applied on every
            // GameFlow screen with its own EventSystem for consistency, even though MainMenu doesn't have
            // a South-shortcut collision to fix the way Settings does.
            GamepadUIBindingFix.Apply();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(root);
        }

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;

            if (titleImage != null)
            {
                var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
                // Same 99%/99% box as TitleScreenController's logo - see that class's comment for why
                // matching percentages (rather than some other box) is what makes this render at the
                // same size and position as the title screen.
                title.style.maxWidth = Length.Percent(99);
                title.style.maxHeight = Length.Percent(99);
                title.style.width = Length.Percent(99);
                title.style.height = Length.Percent(99);
                root.Add(title);
            }

            // A separate absolutely-positioned column so the buttons land in the bottom-right corner
            // regardless of the root's centering, which is what keeps the logo centered above.
            var buttonColumn = new VisualElement();
            buttonColumn.style.position = Position.Absolute;
            buttonColumn.style.right = 40;
            buttonColumn.style.bottom = 40;
            buttonColumn.style.flexDirection = FlexDirection.Column;
            buttonColumn.style.alignItems = Align.FlexEnd;

            Button hostButton = MakeButton("Host", OnHostClicked, selectSound);
            buttonColumn.Add(hostButton);
            buttonColumn.Add(MakeButton("Client", OnClientClicked, selectSound));
            buttonColumn.Add(MakeButton("Settings", OnSettingsClicked, selectSound));
            buttonColumn.Add(MakeButton("Quit Game", OnQuitClicked, cancelSound));
            root.Add(buttonColumn);

            // Buttons are focusable and navigable (via the scene's EventSystem +
            // InputSystemUIInputModule) out of the box, but nothing has focus yet at this point, and
            // gamepad Move/Submit only ever act on whatever's currently focused - with no starting
            // focus, the left stick/d-pad would do nothing until the player first clicks a button with
            // a mouse. Focusing Host here gives gamepad-only navigation somewhere to start from
            // immediately, matching how a controller-first menu is expected to behave.
            hostButton.Focus();
            // FocusInEvent normally handles this (see MakeButton), but setting it explicitly too
            // guards against relying on that event firing synchronously the very first time.
            SetFocusedVisual(hostButton, true);
        }

        // Near-black/dim gray for every button that doesn't have focus, and a bright, high-contrast
        // pill for the one that does - a much bigger, harder-to-miss indicator than the runtime
        // theme's default focus ring, especially against a busy background image.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color FocusedText = Color.black;
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);

        /// <summary>
        /// Builds a menu button that plays clickSound (fire-and-forget via PlayClipAtPoint, see the
        /// class summary) before running onClick. clickSound may be null, in which case the button just
        /// runs onClick silently.
        /// </summary>
        private static Button MakeButton(string text, System.Action onClick, AudioClip clickSound)
        {
            var button = new Button(() =>
            {
                if (clickSound != null)
                {
                    AudioVolumeService.PlayOneShot(clickSound, AudioCategory.SoundEffects, Vector3.zero);
                }
                onClick();
            })
            { text = text };
            button.style.width = 200;
            button.style.height = 40;
            button.style.marginTop = 8;
            button.style.marginBottom = 8;
            button.style.fontSize = 18;

            // Every button starts grayed out; whichever one gains focus (by gamepad navigation, mouse
            // click, or the initial hostButton.Focus() call above) gets brightened by these same two
            // callbacks, and reverts the moment focus moves elsewhere.
            SetFocusedVisual(button, false);
            button.RegisterCallback<FocusInEvent>(_ => SetFocusedVisual(button, true));
            button.RegisterCallback<FocusOutEvent>(_ => SetFocusedVisual(button, false));

            return button;
        }

        private static void SetFocusedVisual(Button button, bool focused)
        {
            button.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
            button.style.color = focused ? FocusedText : UnfocusedText;
        }

        private void OnHostClicked()
        {
            GameNetworkManager manager = GameNetworkManager.Instance;
            if (manager == null)
            {
                Debug.LogError("[MainMenu] No GameNetworkManager in the scene - can't start a host.");
                return;
            }

            manager.StartHostConnection();

            // StartHost() (called inside StartHostConnection) is synchronous - by the time it returns,
            // IsListening is already true and SceneManager is ready to use, so there's no need to wait
            // a frame or hook a callback before loading the gameplay scene.
            if (manager.IsListening)
            {
                manager.SceneManager.LoadScene(gameplaySceneName, LoadSceneMode.Single);
            }
            else
            {
                Debug.LogError("[MainMenu] StartHostConnection didn't result in a listening host - not loading the gameplay scene.");
            }
        }

        private void OnClientClicked()
        {
            if (GameNetworkManager.Instance == null)
            {
                Debug.LogError("[MainMenu] No GameNetworkManager in the scene - can't start a client.");
                return;
            }

            // No scene load here on purpose - see the class summary. Netcode's own scene management
            // (EnableSceneManagement is on) brings this client into whatever scene the host loads.
            GameNetworkManager.Instance.StartClientConnection();
        }

        private void OnSettingsClicked()
        {
            SceneManager.LoadScene(settingsSceneName);
        }

        private void OnQuitClicked()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
