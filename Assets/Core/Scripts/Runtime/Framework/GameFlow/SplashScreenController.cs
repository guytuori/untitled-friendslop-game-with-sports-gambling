using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The very first screen in the boot flow: "Splash Screen" in white text on a black background.
    /// Advances to the title screen after <see cref="displayDuration"/> seconds, or as soon as any
    /// key/button is pressed, whichever comes first - fading to black first so the cut isn't jarring.
    ///
    /// Built entirely in code against this GameObject's UIDocument (see BuildUI) rather than a
    /// separate UXML template asset, since this whole screen is a single centered label - not worth a
    /// template file. Setup: see Friendslop > Build Splash, Title And Main Menu Scenes
    /// (GameFlowScenesSetup), which creates this scene, adds this component, and registers it as the
    /// first scene in Build Settings so it's what a build actually launches into.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class SplashScreenController : MonoBehaviour
    {
        [Tooltip("Scene to load once this screen is done (by timeout or key press).")]
        [SerializeField] private string nextSceneName = "TitleScreen";

        [Tooltip("Advance automatically after this many seconds even with no input.")]
        [SerializeField] private float displayDuration = 2f;

        [Tooltip("How long the fade-to-black transition takes before the next scene loads.")]
        [SerializeField] private float fadeDuration = 0.4f;

        private VisualElement m_Root;
        private float m_Timer;
        private bool m_Advancing;

        private void Awake()
        {
            m_Root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(m_Root);
        }

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;
            root.style.justifyContent = Justify.Center;
            root.style.alignItems = Align.Center;

            var label = new Label("Splash Screen");
            label.style.color = Color.white;
            label.style.fontSize = 36;
            root.Add(label);
        }

        private void Update()
        {
            if (m_Advancing) return;

            m_Timer += Time.deltaTime;
            if (m_Timer >= displayDuration || AnyInputPressed())
            {
                m_Advancing = true;
                StartCoroutine(FadeAndLoad());
            }
        }

        /// <summary>
        /// Polled directly against the new Input System's devices rather than the legacy
        /// Input.anyKeyDown, since this project's Active Input Handling is set to the new system only -
        /// the legacy Input class throws if called at all in that mode.
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
