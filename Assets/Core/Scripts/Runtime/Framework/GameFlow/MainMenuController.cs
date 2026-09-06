using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Third screen in the boot flow, and the current end of it: Host / Client / Quit Game, stacked
    /// and centered on a black background. Host and Client call straight into
    /// <see cref="GameNetworkManager"/>'s own StartHostConnection/StartClientConnection - the exact
    /// same methods GameNetworkUI's buttons call in the gameplay test scenes - so this reuses the
    /// project's existing networking entry points rather than reimplementing them.
    ///
    /// For that call to have anything to talk to, this scene needs its own GameNetworkManager - unlike
    /// the test scenes, this one doesn't come with the gameplay content that normally carries a
    /// NetworkManager alongside it. GameFlowScenesSetup instantiates the shared "[BB] NetworkManager"
    /// prefab into this scene for exactly that reason; GameNetworkManager.Awake() marks itself
    /// DontDestroyOnLoad, so that same instance survives into whatever scene gets loaded next.
    ///
    /// Deliberately doesn't load a gameplay scene after Host/Client - per the design brief this is "for
    /// now", so this menu just starts the connection, matching what the existing buttons already did
    /// and nothing more. Quit Game exits the build (Application.Quit() is a no-op in the Editor, so
    /// this stops Play Mode there instead).
    ///
    /// Built in code against this GameObject's UIDocument, the same way SplashScreenController and
    /// TitleScreenController are - see GameFlowScenesSetup for how this scene gets created (including
    /// the EventSystem it needs for these buttons to actually receive clicks).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MainMenuController : MonoBehaviour
    {
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

            var title = new Label("Untitled Friendslop Game With Sports Gambling");
            title.style.color = Color.white;
            title.style.fontSize = 22;
            title.style.marginBottom = 40;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.whiteSpace = WhiteSpace.Normal;
            title.style.maxWidth = Length.Percent(80);
            root.Add(title);

            root.Add(MakeButton("Host", OnHostClicked));
            root.Add(MakeButton("Client", OnClientClicked));
            root.Add(MakeButton("Quit Game", OnQuitClicked));
        }

        private static Button MakeButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.width = 200;
            button.style.height = 40;
            button.style.marginTop = 8;
            button.style.marginBottom = 8;
            button.style.fontSize = 18;
            return button;
        }

        private void OnHostClicked()
        {
            if (GameNetworkManager.Instance == null)
            {
                Debug.LogError("[MainMenu] No GameNetworkManager in the scene - can't start a host.");
                return;
            }
            GameNetworkManager.Instance.StartHostConnection();
        }

        private void OnClientClicked()
        {
            if (GameNetworkManager.Instance == null)
            {
                Debug.LogError("[MainMenu] No GameNetworkManager in the scene - can't start a client.");
                return;
            }
            GameNetworkManager.Instance.StartClientConnection();
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
