using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The between-rounds screen, loaded for everyone (by the master client's RoundTimer) when the team hits
    /// a round's target. A placeholder for now: the round's scores (from MatchProgress, recorded by every
    /// client as the round ended), the team total against the target, the next round's target, and
    /// "Press any key to go to the next round".
    ///
    /// Pressing a key marks this player ready (MatchFlowSync.ReadyRpc, a static RPC - this scene has no
    /// networked objects). Once every player in the session is ready, the master client loads the gameplay
    /// scene again: same map, freshly randomized challenges, a higher target (Target Score Growth), and
    /// everyone starting from Starting Points plus 5% of their final score (see PlayerScore).
    ///
    /// Later this is where between-round choices go (upgrades, voting on the next map, ...).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class RoundResultsController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to go to if the connection is lost.")]
        [SerializeField] private string exitSceneName = "MainMenu";

        [Tooltip("Played when this player presses a key to get ready.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Seconds before key presses count, so a button still held from the round doesn't skip the screen.")]
        [SerializeField] private float inputDelaySeconds = 1f;

        [SerializeField] private float promptFlashInterval = 0.5f;

        private Label m_Prompt;
        private Label m_ReadyLabel;
        private bool m_Ready;
        private bool m_Loading;
        private bool m_Exiting;
        private float m_ShownTime;

        private static NetworkRunner Runner => FusionSessionService.HasInstance ? FusionSessionService.Instance.GameRunner : null;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();
            m_ShownTime = Time.unscaledTime;

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            BuildUI(GetComponent<UIDocument>().rootVisualElement);
        }

        private void OnEnable()
        {
            MatchProgress.ReadyChanged += RefreshReady;
            if (FusionSessionService.HasInstance) FusionSessionService.Instance.GameEnded += HandleGameEnded;
            StartCoroutine(FlashPrompt());
        }

        private void OnDisable()
        {
            MatchProgress.ReadyChanged -= RefreshReady;
            if (FusionSessionService.HasInstance) FusionSessionService.Instance.GameEnded -= HandleGameEnded;
        }

        private void BuildUI(VisualElement root)
        {
            MenuUI.SetupRoot(root);
            root.style.justifyContent = Justify.FlexStart;
            MenuUI.AddSmallLogo(root, titleImage);

            var column = new VisualElement();
            column.style.position = Position.Absolute;
            column.style.top = Length.Percent(26);
            column.style.left = 0;
            column.style.right = 0;
            column.style.alignItems = Align.Center;
            root.Add(column);

            int round = MatchProgress.HasResults ? MatchProgress.LastRoundNumber : MatchProgress.RoundNumber - 1;
            column.Add(MakeLabel($"ROUND {Mathf.Max(1, round)} COMPLETE!", 40, new Color(0.55f, 0.9f, 0.43f), bold: true));
            column.Add(MakeLabel($"Team scored {MatchProgress.LastTeamTotal} of {MatchProgress.LastTeamTarget}", 20, Color.white, bold: false));

            var table = new VisualElement();
            table.style.marginTop = 24;
            table.style.marginBottom = 24;
            table.style.width = 460;
            table.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f);
            table.style.paddingTop = 10;
            table.style.paddingBottom = 10;
            table.style.paddingLeft = 16;
            table.style.paddingRight = 16;
            column.Add(table);

            foreach (RoundResultEntry entry in MatchProgress.LastResults)
            {
                table.Add(MakeRow(entry.Name, entry.Score.ToString(), Color.white, new Color(0.57f, 0.84f, 0.38f)));
            }
            table.Add(MakeRow("TOTAL", MatchProgress.LastTeamTotal.ToString(), Color.white, new Color(1f, 0.84f, 0.35f)));

            column.Add(MakeLabel($"Next round's target: {MatchProgress.NextTeamTarget}", 20, new Color(0.67f, 0.86f, 1f), bold: true));

            m_Prompt = MakeLabel("Press any key to go to the next round", 18, Color.white, bold: false);
            m_Prompt.style.position = Position.Absolute;
            m_Prompt.style.bottom = 90;
            m_Prompt.style.left = 0;
            m_Prompt.style.right = 0;
            root.Add(m_Prompt);

            m_ReadyLabel = MakeLabel("", 16, new Color(0.8f, 0.8f, 0.8f), bold: false);
            m_ReadyLabel.style.position = Position.Absolute;
            m_ReadyLabel.style.bottom = 60;
            m_ReadyLabel.style.left = 0;
            m_ReadyLabel.style.right = 0;
            root.Add(m_ReadyLabel);

            RefreshReady();
        }

        private static Label MakeLabel(string text, int size, Color color, bool bold)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            if (bold) label.style.unityFontStyleAndWeight = FontStyle.Bold;
            return label;
        }

        private static VisualElement MakeRow(string name, string value, Color nameColor, Color valueColor)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.paddingTop = 3;
            row.style.paddingBottom = 3;

            var nameLabel = new Label(name);
            nameLabel.style.fontSize = 18;
            nameLabel.style.color = nameColor;
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(nameLabel);

            var valueLabel = new Label(value);
            valueLabel.style.fontSize = 18;
            valueLabel.style.color = valueColor;
            valueLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(valueLabel);
            return row;
        }

        private IEnumerator FlashPrompt()
        {
            var wait = new WaitForSeconds(promptFlashInterval);
            bool visible = true;
            while (true)
            {
                yield return wait;
                if (m_Ready || m_Prompt == null) continue;
                visible = !visible;
                m_Prompt.style.visibility = visible ? Visibility.Visible : Visibility.Hidden;
            }
        }

        private void Update()
        {
            NetworkRunner runner = Runner;

            if (!m_Ready && Time.unscaledTime - m_ShownTime >= inputDelaySeconds && AnyInputPressed())
            {
                m_Ready = true;
                MenuUI.PlaySound(selectSound);
                if (runner != null && runner.IsRunning)
                {
                    MatchProgress.MarkReady(runner.LocalPlayer.PlayerId);
                    MatchFlowSync.ReadyRpc(runner);
                }
                m_Prompt.text = "Ready! Waiting for everyone else...";
                m_Prompt.style.visibility = Visibility.Visible;
                RefreshReady();
            }

            // The master client starts the next round once everyone in the session is ready.
            if (!m_Loading && runner != null && runner.IsRunning && runner.IsSharedModeMasterClient && EveryoneReady(runner))
            {
                m_Loading = true;
                if (!FusionSessionService.Instance.LoadSceneForEveryone(FusionSessionService.GameplaySceneName))
                {
                    m_Loading = false;
                }
            }
        }

        private static bool EveryoneReady(NetworkRunner runner)
        {
            int players = 0;
            foreach (PlayerRef player in runner.ActivePlayers)
            {
                if (!MatchProgress.IsReady(player.PlayerId)) return false;
                players++;
            }
            return players > 0;
        }

        private void RefreshReady()
        {
            if (m_ReadyLabel == null) return;
            NetworkRunner runner = Runner;
            if (runner == null || !runner.IsRunning)
            {
                m_ReadyLabel.text = "";
                return;
            }

            int players = 0, ready = 0;
            foreach (PlayerRef player in runner.ActivePlayers)
            {
                players++;
                if (MatchProgress.IsReady(player.PlayerId)) ready++;
            }
            m_ReadyLabel.text = $"{ready} / {players} ready";
        }

        private void HandleGameEnded(string message)
        {
            if (m_Exiting) return;
            m_Exiting = true;
            Debug.Log($"[RoundResults] {message} Returning to {exitSceneName}.");
            SceneManager.LoadScene(exitSceneName);
        }

        /// <summary>Any key, mouse button or gamepad button (same check as the title screen).</summary>
        public static bool AnyInputPressed()
        {
            if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame) return true;

            if (Mouse.current != null &&
                (Mouse.current.leftButton.wasPressedThisFrame || Mouse.current.rightButton.wasPressedThisFrame))
            {
                return true;
            }

            Gamepad pad = Gamepad.current;
            return pad != null &&
                   (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                    pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame ||
                    pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame);
        }
    }
}
