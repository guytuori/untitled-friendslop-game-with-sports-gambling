using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Third screen in the boot flow: the same "Parkour Parlay Title Screen" logo as the title screen,
    /// same size and position, over a black background - with Join Game / Host Game / Single Player /
    /// Settings / Quit Game stacked in the bottom-right corner instead of centered. Join Game loads the
    /// Join Game menu (<see cref="joinGameSceneName"/>, see JoinGameController: Public browsing or Private
    /// room codes) and Host Game loads the Host Game rules screen (<see cref="hostGameSceneName"/>, see
    /// HostGameController); actually hosting/joining sessions isn't wired up yet (it'll be built on Photon
    /// Fusion 2). Single Player is a placeholder for now (it just logs) - it'll also get a real
    /// implementation as part of the move to Fusion 2, whose own single-player mode is the natural fit.
    ///
    /// Navigable with a gamepad's left stick or d-pad plus the submit button, not just the mouse - see
    /// BuildUI for what that actually takes (an EventSystem + InputSystemUIInputModule is already
    /// enough for UI Toolkit's own navigation and click-on-submit, and this scene has one; the one thing
    /// still needed here is giving the first button focus, since nothing is focused by default and
    /// gamepad navigation has nothing to move from without a starting point).
    ///
    /// Every button plays a click sound (<see cref="selectSound"/> or <see cref="cancelSound"/>) via
    /// AudioVolumeService.PlayOneShot (AudioCategory.UISoundEffects), which applies the Audio Settings
    /// screen's Master/SoundEffects sliders and plays on a DontDestroyOnLoad one-shot object, so a click
    /// sound survives the scene load its own button triggers (Host Game, Settings).
    ///
    /// This scene still carries an instance of the shared "[BB] NetworkManager" prefab
    /// (GameNetworkManager, Netcode for GameObjects) from when Host/Client connected directly from here.
    /// Nothing on this menu uses it any more; it'll go away with the move to Fusion 2.
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

        [Tooltip("Scene to load when Join Game is clicked - the Public/Private join menu.")]
        [SerializeField] private string joinGameSceneName = "JoinGame";

        [Tooltip("Scene to load when Host Game is clicked - the game-rules setup screen.")]
        [SerializeField] private string hostGameSceneName = "HostGame";

        [Tooltip("Scene to load when Settings is clicked.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when Join Game, Host Game, Single Player, or Settings is selected.")]
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

            Button joinButton = MakeButton("Join Game", OnJoinClicked, selectSound);
            buttonColumn.Add(joinButton);
            buttonColumn.Add(MakeButton("Host Game", OnHostClicked, selectSound));
            buttonColumn.Add(MakeButton("Single Player", OnSinglePlayerClicked, selectSound));
            buttonColumn.Add(MakeButton("Settings", OnSettingsClicked, selectSound));
            buttonColumn.Add(MakeButton("Quit Game", OnQuitClicked, cancelSound));
            root.Add(buttonColumn);

            // Buttons are focusable and navigable (via the scene's EventSystem +
            // InputSystemUIInputModule) out of the box, but nothing has focus yet at this point, and
            // gamepad Move/Submit only ever act on whatever's currently focused - with no starting
            // focus, the left stick/d-pad would do nothing until the player first clicks a button with
            // a mouse. Focusing the top button (Join Game) here gives gamepad-only navigation somewhere
            // to start from immediately, matching how a controller-first menu is expected to behave.
            joinButton.Focus();
            // FocusInEvent normally handles this (see MakeButton), but setting it explicitly too
            // guards against relying on that event firing synchronously the very first time.
            SetFocusedVisual(joinButton, true);
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
                    AudioVolumeService.PlayOneShot(clickSound, AudioCategory.UISoundEffects, Vector3.zero);
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
            // click, or the initial joinButton.Focus() call above) gets brightened by these same two
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
            SceneManager.LoadScene(hostGameSceneName);
        }

        private void OnJoinClicked()
        {
            SceneManager.LoadScene(joinGameSceneName);
        }

        private void OnSinglePlayerClicked()
        {
            // Placeholder until the Fusion 2 migration - see the class summary.
            Debug.Log("[MainMenu] Single Player selected (not implemented yet).");
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
