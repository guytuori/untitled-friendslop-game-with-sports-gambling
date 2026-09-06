using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Second screen in the boot flow: the game's title in white text on a black background, with a
    /// "Press Enter/Start" prompt at the bottom that flashes on and off. Waits specifically for Enter
    /// (keyboard) or Start (gamepad) - not any key, unlike SplashScreenController - then fades to black
    /// and loads the main menu.
    ///
    /// Built in code against this GameObject's UIDocument the same way SplashScreenController is - see
    /// that class and GameFlowScenesSetup for how this scene gets created and registered in Build
    /// Settings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class TitleScreenController : MonoBehaviour
    {
        [SerializeField] private string titleText = "Untitled Friendslop Game With Sports Gambling";
        [SerializeField] private string promptText = "Press Enter/Start";
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
            root.style.backgroundColor = Color.black;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;

            var title = new Label(titleText);
            title.style.color = Color.white;
            title.style.fontSize = 30;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.maxWidth = Length.Percent(80);
            root.Add(title);

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

            bool confirmPressed =
                (Keyboard.current != null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)) ||
                (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);

            if (confirmPressed)
            {
                m_Advancing = true;
                StopAllCoroutines(); // stop the prompt flash too - it doesn't matter mid-fade
                StartCoroutine(FadeAndLoad());
            }
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
