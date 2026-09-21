using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Placeholder for the "change controls" screen, reached from the Settings scene's Change
    /// Keybindings button. Same visual treatment as SettingsController/MainMenuController: the
    /// "Parkour Parlay Title Screen" logo at the same size and position, over a black background - but
    /// for now the only content is a single Back button in the bottom-right corner, since the actual
    /// keybinding-remap UI is still being designed. Back returns to <see cref="settingsSceneName"/>
    /// (this scene's only entry point) - that's an assumption, not something explicitly specified yet,
    /// since this scene is still a stand-in for a real mockup.
    ///
    /// Built the same way as the other GameFlow screens: in code against this GameObject's UIDocument,
    /// with its own EventSystem for click/gamepad-navigation support (see MainMenuController's comment
    /// for what that takes).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class ChangeKeybindingsController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens - wire this up to the same texture.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back is clicked. Assumed to be Settings, since that's this scene's only entry point for now.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when Back is selected.")]
        [SerializeField] private AudioClip cancelSound;

        private void Awake()
        {
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
                // Same 99%/99% box as the other GameFlow screens' logo - keeps it the same size and
                // position everywhere it appears.
                title.style.maxWidth = Length.Percent(99);
                title.style.maxHeight = Length.Percent(99);
                title.style.width = Length.Percent(99);
                title.style.height = Length.Percent(99);
                root.Add(title);
            }

            // Same absolutely-positioned bottom-right column as the other GameFlow screens.
            var buttonColumn = new VisualElement();
            buttonColumn.style.position = Position.Absolute;
            buttonColumn.style.right = 40;
            buttonColumn.style.bottom = 40;
            buttonColumn.style.flexDirection = FlexDirection.Column;
            buttonColumn.style.alignItems = Align.FlexEnd;

            Button backButton = MakeButton("Back", OnBackClicked, cancelSound);
            buttonColumn.Add(backButton);
            root.Add(buttonColumn);

            // Only one button on this placeholder screen, so it starts (and stays) focused - see
            // MainMenuController.BuildUI for why something needs initial focus at all.
            backButton.Focus();
            SetFocusedVisual(backButton, true);
        }

        // Same grayed-out/bright-pill focus treatment as the other GameFlow screens.
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

        private void OnBackClicked()
        {
            SceneManager.LoadScene(settingsSceneName);
        }
    }
}
