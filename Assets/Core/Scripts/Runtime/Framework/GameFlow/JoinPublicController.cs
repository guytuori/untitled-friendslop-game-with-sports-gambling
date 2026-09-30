using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Join Game > Public. Normal / Hard / Very Hard are quick match: they drop the player straight into
    /// the oldest open public game matching that difficulty's search preset (GameSearchPresets), or, if
    /// there isn't one, host a new public game with that difficulty's host preset (HostGamePresets) -
    /// either way ending in the pre-match lobby (see FusionSessionService.QuickMatchAsync). Normal is
    /// focused by default, so mashing A from the title screen goes Main Menu > Join Game > Public > Normal
    /// and into a game with no decisions. Custom opens the full search filter (the FindGames scene), whose
    /// Find Games shows the Game Browser. Back returns to the Join Game menu; B/Esc also goes back. Input
    /// is ignored while a quick match is connecting.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class JoinPublicController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [SerializeField] private string customSceneName = "FindGames";

        [Tooltip("Scene to load once quick match has joined or created a game.")]
        [SerializeField] private string gameLobbySceneName = "GameLobby";

        [Tooltip("Scene to load when Back (or B/Esc) is pressed.")]
        [SerializeField] private string backSceneName = "JoinGame";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private Label m_StatusLabel;
        private bool m_Busy;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            MenuUI.SetupRoot(root);
            MenuUI.AddFullLogo(root, titleImage);

            VisualElement column = MenuUI.AddBottomRightColumn(root);
            Button normalButton = MenuUI.MakeButton("Normal", () => QuickMatch(GameSearchPresets.Normal, HostGamePresets.Normal), selectSound);
            column.Add(normalButton);
            column.Add(MenuUI.MakeButton("Hard", () => QuickMatch(GameSearchPresets.Hard, HostGamePresets.Hard), selectSound));
            column.Add(MenuUI.MakeButton("Very Hard", () => QuickMatch(GameSearchPresets.VeryHard, HostGamePresets.VeryHard), selectSound));
            column.Add(MenuUI.MakeButton("Custom", OnCustom, selectSound));
            column.Add(MenuUI.MakeButton("Back", OnBack, cancelSound));

            m_StatusLabel = new Label("");
            m_StatusLabel.style.position = Position.Absolute;
            m_StatusLabel.style.right = 260;
            m_StatusLabel.style.bottom = 52;
            m_StatusLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
            m_StatusLabel.style.fontSize = 16;
            root.Add(m_StatusLabel);

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);
            MenuUI.FocusFirst(normalButton);
        }

        private async void QuickMatch(GameSearchFilter filter, HostGameRulesData hostRules)
        {
            if (m_Busy) return;
            m_Busy = true;
            m_StatusLabel.text = "Finding a game...";

            string error = await FusionSessionService.Instance.QuickMatchAsync(filter, hostRules);
            if (this == null) return; // screen closed meanwhile

            if (error == null)
            {
                SceneManager.LoadScene(gameLobbySceneName);
                return;
            }

            m_Busy = false;
            m_StatusLabel.text = error;
        }

        private void OnCustom()
        {
            if (m_Busy) return;
            SceneManager.LoadScene(customSceneName);
        }

        private void OnBack()
        {
            if (m_Busy) return;
            SceneManager.LoadScene(backSceneName);
        }
    }
}
