using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The game-over screen, loaded for everyone (by the master client's RoundTimer) when the team misses a
    /// round's target. A placeholder so the game loops: a blank screen with a flashing "Return to Main
    /// Menu" prompt, styled like the title screen's "Press Any Key/Button". Any key, mouse click or gamepad
    /// button leaves the session and goes back to the main menu. The real final-score content comes later
    /// (MatchProgress already holds the last round's results).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class FinalScoreController : MonoBehaviour
    {
        [SerializeField] private string promptText = "Return to Main Menu";
        [SerializeField] private string mainMenuSceneName = "MainMenu";
        [SerializeField] private float promptFlashInterval = 0.5f;

        [Tooltip("Seconds before key presses count, so a button still held from the round doesn't skip the screen.")]
        [SerializeField] private float inputDelaySeconds = 1f;

        private Label m_Prompt;
        private float m_ShownTime;
        private bool m_Leaving;

        private void Awake()
        {
            m_ShownTime = Time.unscaledTime;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;

            m_Prompt = new Label(promptText);
            m_Prompt.style.color = Color.white;
            m_Prompt.style.fontSize = 18;
            m_Prompt.style.position = Position.Absolute;
            m_Prompt.style.bottom = 60;
            root.Add(m_Prompt);
        }

        private void OnEnable()
        {
            StartCoroutine(FlashPrompt());
            if (FusionSessionService.HasInstance) FusionSessionService.Instance.GameEnded += HandleGameEnded;
        }

        private void OnDisable()
        {
            if (FusionSessionService.HasInstance) FusionSessionService.Instance.GameEnded -= HandleGameEnded;
        }

        private IEnumerator FlashPrompt()
        {
            var wait = new WaitForSeconds(promptFlashInterval);
            bool visible = true;
            while (!m_Leaving)
            {
                yield return wait;
                visible = !visible;
                m_Prompt.style.visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void Update()
        {
            if (m_Leaving || Time.unscaledTime - m_ShownTime < inputDelaySeconds) return;
            if (RoundResultsController.AnyInputPressed()) ReturnToMainMenu();
        }

        private async void ReturnToMainMenu()
        {
            m_Leaving = true;
            m_Prompt.style.visibility = Visibility.Visible;
            m_Prompt.text = "Leaving...";

            if (FusionSessionService.HasInstance)
            {
                await FusionSessionService.Instance.LeaveGameAsync();
            }
            SceneManager.LoadScene(mainMenuSceneName);
        }

        private void HandleGameEnded(string message)
        {
            if (m_Leaving) return;
            m_Leaving = true;
            SceneManager.LoadScene(mainMenuSceneName);
        }
    }
}
