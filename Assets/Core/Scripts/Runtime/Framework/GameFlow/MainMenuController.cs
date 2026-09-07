using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Third screen in the boot flow: Host / Client / Quit Game, stacked and centered on a black
    /// background. Host and Client call straight into <see cref="GameNetworkManager"/>'s own
    /// StartHostConnection/StartClientConnection - the exact same methods GameNetworkUI's buttons call
    /// in the gameplay test scenes - so this reuses the project's existing networking entry points
    /// rather than reimplementing them.
    ///
    /// For that call to have anything to talk to, this scene needs its own GameNetworkManager - unlike
    /// the test scenes, this one doesn't come with the gameplay content that normally carries a
    /// NetworkManager alongside it. GameFlowScenesSetup instantiates the shared "[BB] NetworkManager"
    /// prefab into this scene for exactly that reason; GameNetworkManager.Awake() marks itself
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
    /// TitleScreenController are - see GameFlowScenesSetup for how this scene gets created (including
    /// the EventSystem it needs for these buttons to actually receive clicks).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class MainMenuController : MonoBehaviour
    {
        [Tooltip("The actual game scene to load once hosting starts. Netcode syncs every connected client to this automatically - see the class summary.")]
        [SerializeField] private string gameplaySceneName = "[BB] Core";

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
