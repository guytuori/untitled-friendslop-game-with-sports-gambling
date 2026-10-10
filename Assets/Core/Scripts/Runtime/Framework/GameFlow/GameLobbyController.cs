using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The pre-match lobby, shown to the host after Host Game and to everyone who joins (from the Game
    /// Browser or by room code). Shows the room code, whether the game is public or private, the player
    /// count out of the max, and the list of players, all kept current by FusionSessionService (every
    /// client sees the session's players directly in Fusion's Shared mode).
    ///
    /// The host (the session's master client) gets Start Match (starts the match as-is: the session is
    /// closed and hidden, and Fusion loads the gameplay scene for everyone) and Leave (if the host leaves,
    /// Photon makes another player the host, who then gets Start Match). Start Match needs
    /// at least FusionSessionService.MinPlayersToStart players (it's dimmed until then), and presses are
    /// ignored for the first <see cref="StartMatchGraceSeconds"/> after the lobby opens - so a player
    /// mashing A through quick match, who lands here as a brand-new host with Start Match selected,
    /// doesn't instantly start a match by themselves. Everyone else gets Leave and a "waiting for the host" line. B/Esc = Leave. If the game
    /// ends from the other side (the connection drops), the reason is shown and the button becomes Back.
    ///
    /// There are no player names yet, so players are numbered in join order, with the host and "you"
    /// marked.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GameLobbyController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load after leaving the game (or once it has ended).")]
        [SerializeField] private string leaveSceneName = "MainMenu";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private FusionSessionService m_Service;
        private Label m_CodeLabel;
        private Label m_VisibilityLabel;
        private Label m_CountLabel;
        private Label m_PlayersLabel;
        private Label m_StatusLabel;
        private Button m_StartButton;
        private Button m_LeaveButton;
        private bool m_Ended;
        private bool m_Leaving;
        private bool m_Starting;

        /// <summary>See the class summary - Start Match presses in this window after the lobby opens are ignored.</summary>
        private const float StartMatchGraceSeconds = 3f;
        private float m_OpenedAt;

        private bool StartMatchAllowed =>
            Time.unscaledTime - m_OpenedAt >= StartMatchGraceSeconds &&
            m_Service.Players.Count >= FusionSessionService.MinPlayersToStart;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();
            m_OpenedAt = Time.unscaledTime;
            m_Service = FusionSessionService.Instance;

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;
            MenuUI.AddSmallLogo(root, titleImage);

            var center = new VisualElement();
            center.style.position = Position.Absolute;
            center.style.left = Length.Percent(50);
            center.style.top = Length.Percent(55);
            center.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            center.style.alignItems = Align.Center;
            root.Add(center);

            center.Add(MakeLabel("ROOM CODE", 18, new Color(0.8f, 0.8f, 0.8f)));
            m_CodeLabel = MakeLabel("", 64, Color.white);
            m_CodeLabel.style.letterSpacing = 12;
            center.Add(m_CodeLabel);

            m_VisibilityLabel = MakeLabel("", 16, new Color(0.8f, 0.8f, 0.8f));
            m_VisibilityLabel.style.marginBottom = 24;
            center.Add(m_VisibilityLabel);

            m_CountLabel = MakeLabel("", 24, new Color(1f, 0.85f, 0.2f));
            m_CountLabel.style.marginBottom = 8;
            center.Add(m_CountLabel);

            m_PlayersLabel = MakeLabel("", 18, Color.white);
            m_PlayersLabel.style.unityTextAlign = TextAnchor.UpperCenter;
            center.Add(m_PlayersLabel);

            m_StatusLabel = MakeLabel("", 16, new Color(0.8f, 0.8f, 0.8f));
            m_StatusLabel.style.marginTop = 20;
            center.Add(m_StatusLabel);

            VisualElement column = MenuUI.AddBottomRightColumn(root);
            // No click sound here - OnStartMatch plays it only when the press is actually accepted.
            m_StartButton = MenuUI.MakeButton("Start Match", OnStartMatch, null);
            m_LeaveButton = MenuUI.MakeButton("Leave", OnLeave, cancelSound);
            column.Add(m_StartButton);
            column.Add(m_LeaveButton);

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnLeave);

            m_Service.RosterChanged += Refresh;
            m_Service.GameEnded += OnGameEnded;
            SessionProfiles.Changed += Refresh; // names arrive a moment after each player joins

            if (!m_Service.InGame)
            {
                // Opened without a game (e.g. playing this scene directly).
                ShowEnded("Not connected to a game.");
            }
            else
            {
                Refresh();
            }

            MenuUI.FocusFirst(m_Service.IsHost ? m_StartButton : m_LeaveButton);
        }

        private void OnDestroy()
        {
            if (m_Service == null) return;
            m_Service.RosterChanged -= Refresh;
            m_Service.GameEnded -= OnGameEnded;
            SessionProfiles.Changed -= Refresh;
        }

        private static Label MakeLabel(string text, int size, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            return label;
        }

        private void Refresh()
        {
            if (m_Ended) return;

            m_CodeLabel.text = m_Service.RoomCode;
            m_VisibilityLabel.text = m_Service.IsPublic ? "PUBLIC GAME" : "PRIVATE GAME - share the code to invite players";

            int count = m_Service.Players.Count;
            m_CountLabel.text = m_Service.MaxPlayers > 0 ? $"PLAYERS {count} / {m_Service.MaxPlayers}" : $"PLAYERS {count}";

            var list = new StringBuilder();
            for (int i = 0; i < m_Service.Players.Count; i++)
            {
                LobbyPlayer player = m_Service.Players[i];
                list.Append(NetworkPlayers.GetPlayerLabel((ulong)player.PlayerId));
                if (player.IsHost) list.Append(" (Host)");
                if (player.IsLocal) list.Append(" (You)");
                if (i < m_Service.Players.Count - 1) list.Append('\n');
            }
            m_PlayersLabel.text = list.ToString();

            bool isHost = m_Service.IsHost;
            bool enoughPlayers = count >= FusionSessionService.MinPlayersToStart;
            m_StartButton.style.display = isHost ? DisplayStyle.Flex : DisplayStyle.None;
            m_StartButton.style.opacity = enoughPlayers ? 1f : 0.4f;
            if (!m_Starting)
            {
                m_StatusLabel.text = !isHost ? "Waiting for the host to start the match..."
                    : enoughPlayers ? "Start the match whenever you're ready."
                    : "Waiting for at least one more player to join.";
            }
        }

        private void OnStartMatch()
        {
            if (m_Ended || m_Starting || !m_Service.IsHost || !StartMatchAllowed) return;

            MenuUI.PlaySound(selectSound);
            m_Starting = true;
            m_StatusLabel.text = "Starting match...";
            if (!m_Service.StartMatch())
            {
                m_Starting = false;
                m_StatusLabel.text = "Couldn't start the match - see the console.";
            }
        }

        private async void OnLeave()
        {
            if (m_Leaving) return;
            m_Leaving = true;

            if (!m_Ended)
            {
                m_StatusLabel.text = "Leaving...";
                await m_Service.LeaveGameAsync();
                if (this == null) return;
            }

            SceneManager.LoadScene(leaveSceneName);
        }

        private void OnGameEnded(string message)
        {
            ShowEnded(message);
        }

        private void ShowEnded(string message)
        {
            m_Ended = true;
            m_StatusLabel.text = message;
            m_CountLabel.text = "";
            m_PlayersLabel.text = "";
            m_StartButton.style.display = DisplayStyle.None;
            m_LeaveButton.text = "Back";
            MenuUI.FocusFirst(m_LeaveButton);
        }
    }
}
