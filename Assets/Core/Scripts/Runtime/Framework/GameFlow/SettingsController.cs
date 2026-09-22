using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Settings screen, reached from the main menu's Settings button. Same visual treatment as
    /// MainMenuController: the "Parkour Parlay Title Screen" logo at the same size and position (99%
    /// box on both axes - see TitleScreenController's comment for why that's what makes it render at
    /// that size), over a black background, with the menu buttons stacked in the bottom-right corner
    /// instead of centered.
    ///
    /// Buttons are Graphics / Audio / Change Keybindings / Back. Graphics and Audio don't do anything
    /// yet - they just log that they were clicked, as placeholders until there's an actual
    /// graphics/audio options panel to show. Change Keybindings loads <see cref="keybindingsSceneName"/>.
    /// Back returns to <see cref="mainMenuSceneName"/> - same as pressing Esc or the gamepad's South
    /// (bottom-face) button, via the raw poll in Update() below.
    ///
    /// RESOLVED COLLISION: this scene's South-is-Back shortcut used to collide with gamepad Submit, since
    /// Unity's stock InputSystemUIInputModule actions asset binds Submit to gamepad South by default -
    /// backwards from this project's actual Nintendo-style pad convention (A/East=confirm, B/South=cancel).
    /// Pressing South while Graphics, Audio, or Change Keybindings had focus used to fire BOTH this
    /// scene's "South = Back" shortcut and the UI module's own Submit on the focused button in the same
    /// frame, bouncing a gamepad player straight back to Main Menu instead of (or alongside) actually
    /// activating the button - including Change Keybindings itself. Fixed by GamepadUIBindingFix.Apply()
    /// (called in Awake below), which overrides Submit's gamepad binding to East and Cancel's to South at
    /// runtime - see that class for the full explanation and why it's a runtime override rather than a
    /// fork of the shared stock asset (guid ca9f5fa95ffab41fb9a615ab714db018). With Submit off of South,
    /// pressing South now only ever triggers this scene's own Back shortcut (plus the UI module's Cancel
    /// action, which is a no-op on a plain Button) - no more double-fire, and gamepad players can select
    /// Graphics/Audio/Change Keybindings normally.
    ///
    /// Every button plays a click sound the same way MainMenuController's do - see that class's summary
    /// for the PlayClipAtPoint/DontDestroyOnLoad caveat, which applies here too.
    ///
    /// Navigable with a gamepad's left stick/d-pad plus the submit button, the same way
    /// MainMenuController is - see that class's comment for what that takes. This scene carries its own
    /// EventSystem for the same reason MainMenu does: buttons need one to receive clicks and gamepad
    /// navigation at all, and nothing carries an EventSystem across scene loads the way
    /// GameNetworkManager does for itself.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class SettingsController : MonoBehaviour
    {
        [Tooltip("Same logo as the title screen and main menu - wire this up to the same texture.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Change Keybindings is clicked.")]
        [SerializeField] private string keybindingsSceneName = "ChangeKeybindings";

        [Tooltip("Scene to load when Back is clicked, or when Esc/gamepad South is pressed.")]
        [SerializeField] private string mainMenuSceneName = "MainMenu";

        [Tooltip("Played when Graphics, Audio, or Change Keybindings is selected.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Back is selected, including via the Esc/gamepad-South shortcut.")]
        [SerializeField] private AudioClip cancelSound;

        private void Awake()
        {
            // See the class summary's "RESOLVED COLLISION" note - this is what actually fixes the
            // South-is-both-Back-and-Submit collision.
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
                // Same 99%/99% box as the title screen and main menu's logo - keeps it the same size
                // and position across all three screens.
                title.style.maxWidth = Length.Percent(99);
                title.style.maxHeight = Length.Percent(99);
                title.style.width = Length.Percent(99);
                title.style.height = Length.Percent(99);
                root.Add(title);
            }

            // Same absolutely-positioned bottom-right column as MainMenuController, for the same
            // reason: keeps the buttons out of the way of the centered logo above.
            var buttonColumn = new VisualElement();
            buttonColumn.style.position = Position.Absolute;
            buttonColumn.style.right = 40;
            buttonColumn.style.bottom = 40;
            buttonColumn.style.flexDirection = FlexDirection.Column;
            buttonColumn.style.alignItems = Align.FlexEnd;

            Button graphicsButton = MakeButton("Graphics", OnGraphicsClicked, selectSound);
            buttonColumn.Add(graphicsButton);
            buttonColumn.Add(MakeButton("Audio", OnAudioClicked, selectSound));
            buttonColumn.Add(MakeButton("Change Keybindings", OnChangeKeybindingsClicked, selectSound));
            buttonColumn.Add(MakeButton("Back", OnBackClicked, cancelSound));
            root.Add(buttonColumn);

            // See MainMenuController.BuildUI for why this is needed: nothing has focus by default, and
            // gamepad Move/Submit only act on whatever's currently focused.
            graphicsButton.Focus();
            SetFocusedVisual(graphicsButton, true);
        }

        // Same grayed-out/bright-pill focus treatment as MainMenuController - see that class for why.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color FocusedText = Color.black;
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);

        /// <summary>Same click-sound wrapper as MainMenuController.MakeButton - see that class for why.</summary>
        private static Button MakeButton(string text, System.Action onClick, AudioClip clickSound)
        {
            var button = new Button(() =>
            {
                if (clickSound != null)
                {
                    AudioSource.PlayClipAtPoint(clickSound, Vector3.zero);
                }
                onClick();
            })
            { text = text };
            button.style.width = 200;
            button.style.height = 40;
            button.style.marginTop = 8;
            button.style.marginBottom = 8;
            button.style.fontSize = 18;

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

        private void Update()
        {
            // Esc/gamepad-South shortcut for Back.
            bool backShortcutPressed =
                (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ||
                (Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame);

            if (backShortcutPressed)
            {
                if (cancelSound != null)
                {
                    AudioSource.PlayClipAtPoint(cancelSound, Vector3.zero);
                }
                OnBackClicked();
            }
        }

        private void OnGraphicsClicked()
        {
            // No graphics options panel yet - just confirms the button works until there's something
            // real to show.
            Debug.Log("[Settings] Graphics selected.");
        }

        private void OnAudioClicked()
        {
            // No audio options panel yet - same placeholder purpose as OnGraphicsClicked above.
            Debug.Log("[Settings] Audio selected.");
        }

        private void OnChangeKeybindingsClicked()
        {
            // ChangeKeybindingsController shows only the control scheme matching whatever device
            // actually triggered this click - a raw poll of the gamepad's buttons at the moment the
            // click handler runs, since a mouse click or keyboard-Enter Submit won't have any of them
            // pressed this frame. Set before the scene loads, since it's a static field ChangeKeybindings
            // reads in its own Awake().
            ChangeKeybindingsController.EnteredViaGamepad = WasTriggeredByGamepad();
            SceneManager.LoadScene(keybindingsSceneName);
        }

        private static bool WasTriggeredByGamepad()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;

            return pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                   pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame ||
                   pad.startButton.wasPressedThisFrame;
        }

        private void OnBackClicked()
        {
            SceneManager.LoadScene(mainMenuSceneName);
        }
    }
}
