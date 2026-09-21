using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Second screen in the boot flow: the "Parkour Parlay Title Screen" logo image over the
    /// "Parkour Parlay Title Screen Background" image, with a "Press Any Key/Button" prompt at the
    /// bottom that flashes on and off, on top of both images. Advances on any key, mouse click, or
    /// gamepad button - the same AnyInputPressed() check as SplashScreenController, not just
    /// Enter/Start - since players reflexively hit whatever their own "confirm" button is out of
    /// muscle memory rather than specifically Start.
    ///
    /// Built in code against this GameObject's UIDocument the same way SplashScreenController is. The
    /// two images live alongside this scene at Assets/Core/Scenes/TitleScreen/ and are wired up on
    /// <see cref="backgroundImage"/> and <see cref="titleImage"/> in the Inspector - paint order there
    /// is background first, then title, then the prompt, so each layers on top of the last.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class TitleScreenController : MonoBehaviour
    {
        [Tooltip("Bottom layer - fills the screen behind everything else.")]
        [SerializeField] private Texture2D backgroundImage;

        [Tooltip("Sits on top of the background image, below the prompt text.")]
        [SerializeField] private Texture2D titleImage;

        [SerializeField] private string promptText = "Press Any Key/Button";
        [SerializeField] private string nextSceneName = "MainMenu";
        [SerializeField] private float promptFlashInterval = 0.5f;
        [SerializeField] private float fadeDuration = 0.4f;

        private VisualElement m_Root;
        private Label m_Prompt;
        private bool m_PromptVisible = true;
        private bool m_Advancing;

        private void Awake()
        {
            m_Root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(m_Root);
        }

        private void OnEnable()
        {
            StartCoroutine(FlashPrompt());
        }

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            // Fallback letterbox color only - shows if the background image's aspect ratio doesn't
            // exactly match the panel's, not a substitute for the background image itself.
            root.style.backgroundColor = Color.black;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;

            if (backgroundImage != null)
            {
                var background = new Image { image = backgroundImage, scaleMode = ScaleMode.ScaleAndCrop };
                background.style.position = Position.Absolute;
                background.style.left = 0;
                background.style.top = 0;
                background.style.right = 0;
                background.style.bottom = 0;
                root.Add(background);
            }

            if (titleImage != null)
            {
                var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
                // The logo PNG is 1920x1080 - the same 16:9 aspect ratio as the panel itself - so
                // ScaleToFit only renders at the box's full width/height when both percentages match;
                // otherwise whichever dimension is tighter wins and the other shrinks to keep aspect.
                // Using the same percentage for both dimensions makes the rendered size equal to that
                // percentage exactly - 99% is what looked best on screen.
                title.style.maxWidth = Length.Percent(99);
                title.style.maxHeight = Length.Percent(99);
                title.style.width = Length.Percent(99);
                title.style.height = Length.Percent(99);
                root.Add(title);
            }

            m_Prompt = new Label(promptText);
            m_Prompt.style.color = Color.white;
            m_Prompt.style.fontSize = 18;
            m_Prompt.style.position = Position.Absolute;
            m_Prompt.style.bottom = 60;
            root.Add(m_Prompt);
        }

        /// <summary>Toggles the prompt label's visibility on a fixed interval - a flat "flash" rather than a fade.</summary>
        private IEnumerator FlashPrompt()
        {
            var wait = new WaitForSeconds(promptFlashInterval);
            while (true)
            {
                yield return wait;
                m_PromptVisible = !m_PromptVisible;
                m_Prompt.style.visibility = m_PromptVisible ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void Update()
        {
            if (m_Advancing) return;

            if (AnyInputPressed())
            {
                m_Advancing = true;
                StopAllCoroutines(); // stop the prompt flash too - it doesn't matter mid-fade
                StartCoroutine(FadeAndLoad());
            }
        }

        /// <summary>
        /// Same any-key/any-button check as SplashScreenController.AnyInputPressed() - polled directly
        /// against the new Input System's devices rather than the legacy Input.anyKeyDown, since this
        /// project's Active Input Handling is set to the new system only.
        /// </summary>
        private static bool AnyInputPressed()
        {
            if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;

            if (Mouse.current != null &&
                (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame))
            {
                return true;
            }

            Gamepad pad = Gamepad.current;
            if (pad != null &&
                (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                 pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame ||
                 pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame))
            {
                return true;
            }

            return false;
        }

        private IEnumerator FadeAndLoad()
        {
            float t = 0f;
            while (t < fadeDuration)
            {
                t += Time.deltaTime;
                m_Root.style.opacity = 1f - Mathf.Clamp01(t / fadeDuration);
                yield return null;
            }

            SceneManager.LoadScene(nextSceneName);
        }
    }
}
