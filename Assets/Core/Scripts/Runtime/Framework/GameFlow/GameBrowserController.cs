using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Game Browser: a scrollable table of hosted public games matching
    /// <see cref="GameSearch.CurrentFilter"/> (set by the Normal / Hard / Very Hard search presets or by
    /// Find Games on the Custom screen). One row per game - name, map, players, and every rule - with Back
    /// (bottom-left) and Refresh (bottom-right). Selecting a row will join that game once joining is wired
    /// up; for now it just reports it.
    ///
    /// Where the games come from is an <see cref="IGameSessionSource"/>. Until hosting exists this is
    /// <see cref="SampleGameSessionSource"/> (fake games, so the table can be tested) when
    /// <see cref="useSampleSessions"/> is on, or an empty list when it's off. The real source will be a
    /// Photon Fusion 2 session lobby.
    ///
    /// Navigation is explicit, like the other GameFlow screens: Up/Down move through the rows (the table
    /// scrolls to keep the focused row visible), Down from the last row reaches Refresh, Left/Right move
    /// between Back and Refresh, and Up from either returns to the last focused row. B/Esc goes back. The
    /// mouse wheel scrolls the table and clicking a row selects it.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GameBrowserController : MonoBehaviour
    {
        [Tooltip("Scene Back returns to if the screen that opened the browser didn't set one (see GameSearch.BrowserBackScene).")]
        [SerializeField] private string defaultBackSceneName = "JoinPublic";

        [Tooltip("Show fake games so the table can be tested before real hosting exists. Turn off once the Fusion session source is in.")]
        [SerializeField] private bool useSampleSessions = true;

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private struct Column
        {
            public string Header;
            public float Weight;
            public Func<GameSessionListing, string> Value;
        }

        private static readonly Column[] Columns =
        {
            new Column { Header = "GAME", Weight = 2.2f, Value = s => s.SessionName },
            new Column { Header = "MAP", Weight = 1.8f, Value = s => s.Rules.MapName },
            new Column { Header = "PLAYERS", Weight = 0.9f, Value = s => $"{s.PlayerCount}/{s.Rules.MaxPlayers}" },
            new Column { Header = "ROUND", Weight = 0.8f, Value = s => GameRulesFormat.Time(s.Rules.RoundTimeSeconds) },
            new Column { Header = "BONUS", Weight = 0.8f, Value = s => GameRulesFormat.Time(s.Rules.BonusTimeSeconds) },
            new Column { Header = "LIVES", Weight = 1.0f, Value = s => GameRulesFormat.Lives(s.Rules.Lives) },
            new Column { Header = "START PTS", Weight = 1.0f, Value = s => s.Rules.StartingPoints.ToString(CultureInfo.InvariantCulture) },
            new Column { Header = "PENALTY", Weight = 0.9f, Value = s => GameRulesFormat.DeathPenalty(s.Rules.DeathPenalty) },
            new Column { Header = "PAYOUT", Weight = 0.8f, Value = s => GameRulesFormat.Payout(s.Rules.WagerPayout) },
            new Column { Header = "ITEMS", Weight = 0.7f, Value = s => GameRulesFormat.OnOff(s.Rules.ItemsEnabled) },
            new Column { Header = "PICKUPS", Weight = 0.8f, Value = s => GameRulesFormat.OnOff(s.Rules.PickupsEnabled) },
        };

        private static readonly Color RowText = new Color(0.85f, 0.85f, 0.85f);
        private static readonly Color HeaderText = new Color(1f, 0.85f, 0.2f);

        private IGameSessionSource m_Source;
        private ScrollView m_Scroll;
        private Label m_StatusLabel;
        private Button m_BackButton;
        private Button m_RefreshButton;
        private readonly List<Button> m_Rows = new List<Button>();
        private int m_LastFocusedRow;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            m_Source = useSampleSessions ? new SampleGameSessionSource() : (IGameSessionSource)new EmptyGameSessionSource();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;

            var panel = new VisualElement();
            panel.style.position = Position.Absolute;
            panel.style.top = 30;
            panel.style.left = 40;
            panel.style.right = 40;
            panel.style.bottom = 110;
            panel.style.flexDirection = FlexDirection.Column;
            root.Add(panel);

            var title = new Label("PUBLIC GAMES");
            title.style.color = Color.white;
            title.style.fontSize = 26;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            panel.Add(title);

            m_StatusLabel = new Label("");
            m_StatusLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_StatusLabel.style.fontSize = 16;
            m_StatusLabel.style.marginBottom = 12;
            panel.Add(m_StatusLabel);

            var header = MakeRowContainer();
            header.style.borderBottomWidth = 2;
            header.style.borderBottomColor = HeaderText;
            foreach (Column column in Columns)
            {
                Label label = MakeCell(column.Header, column.Weight);
                label.style.color = HeaderText;
                label.style.unityFontStyleAndWeight = FontStyle.Bold;
                header.Add(label);
            }
            panel.Add(header);

            m_Scroll = new ScrollView(ScrollViewMode.Vertical);
            m_Scroll.style.flexGrow = 1;
            m_Scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            panel.Add(m_Scroll);

            m_BackButton = MenuUI.MakeButton("Back", OnBack, cancelSound);
            m_BackButton.style.position = Position.Absolute;
            m_BackButton.style.left = 40;
            m_BackButton.style.bottom = 40;
            root.Add(m_BackButton);

            m_RefreshButton = MenuUI.MakeButton("Refresh", Refresh, selectSound);
            m_RefreshButton.style.position = Position.Absolute;
            m_RefreshButton.style.right = 40;
            m_RefreshButton.style.bottom = 40;
            root.Add(m_RefreshButton);

            m_BackButton.RegisterCallback<NavigationMoveEvent>(evt => OnBottomButtonMove(evt, m_BackButton));
            m_RefreshButton.RegisterCallback<NavigationMoveEvent>(evt => OnBottomButtonMove(evt, m_RefreshButton));

            MenuUI.RegisterCancelAsBack(root, cancelSound, OnBack);

            Refresh();
        }

        private static VisualElement MakeRowContainer()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 34;
            row.style.paddingLeft = 8;
            row.style.paddingRight = 8;
            return row;
        }

        /// <summary>Columns share the row width by weight (flex-basis 0), so the table fits any screen width.</summary>
        private static Label MakeCell(string text, float weight)
        {
            var label = new Label(text);
            label.style.flexGrow = weight;
            label.style.flexShrink = 1;
            label.style.flexBasis = 0;
            label.style.fontSize = 15;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
            label.style.overflow = Overflow.Hidden;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.textOverflow = TextOverflow.Ellipsis;
            label.style.marginLeft = 0;
            label.style.marginRight = 4;
            label.style.paddingLeft = 0;
            return label;
        }

        private void Refresh()
        {
            bool rowHadFocus = m_Rows.Count > 0 && m_Rows.Exists(r => r.focusController?.focusedElement == r);
            m_StatusLabel.text = "Searching...";

            m_Source.RequestSessions(sessions =>
            {
                GameSearchFilter filter = GameSearch.CurrentFilter ?? new GameSearchFilter();
                var matches = new List<GameSessionListing>();
                foreach (GameSessionListing session in sessions)
                {
                    if (session.Rules != null && filter.Matches(session.Rules)) matches.Add(session);
                }

                BuildRows(matches);

                m_StatusLabel.text = matches.Count == 0
                    ? "No games match these filters."
                    : matches.Count == 1 ? "1 game found." : $"{matches.Count} games found.";

                m_LastFocusedRow = 0;
                if (m_Rows.Count > 0 && (rowHadFocus || !IsAnythingFocused()))
                {
                    MenuUIFocusRow(0);
                }
                else if (m_Rows.Count == 0)
                {
                    MenuUI.FocusFirst(m_RefreshButton);
                }
            });
        }

        private bool IsAnythingFocused() => m_RefreshButton.focusController?.focusedElement != null;

        private void BuildRows(List<GameSessionListing> sessions)
        {
            m_Scroll.Clear();
            m_Rows.Clear();

            for (int i = 0; i < sessions.Count; i++)
            {
                int index = i;
                GameSessionListing session = sessions[i];

                var row = new Button(() => OnRowSelected(session));
                row.text = "";
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.justifyContent = Justify.FlexStart;
                row.style.height = 34;
                row.style.marginLeft = 0;
                row.style.marginRight = 0;
                row.style.marginTop = 1;
                row.style.marginBottom = 1;
                row.style.paddingLeft = 8;
                row.style.paddingRight = 8;

                var cells = new List<Label>(Columns.Length);
                foreach (Column column in Columns)
                {
                    Label cell = MakeCell(column.Value(session), column.Weight);
                    row.Add(cell);
                    cells.Add(cell);
                }

                SetRowFocusedVisual(row, cells, false);
                row.RegisterCallback<FocusInEvent>(_ =>
                {
                    m_LastFocusedRow = index;
                    SetRowFocusedVisual(row, cells, true);
                    // Skip before the first layout pass (the initial focus on row 0, already in view) -
                    // ScrollTo needs real layout positions.
                    if (!float.IsNaN(row.layout.height)) m_Scroll.ScrollTo(row);
                });
                row.RegisterCallback<FocusOutEvent>(_ => SetRowFocusedVisual(row, cells, false));
                row.RegisterCallback<NavigationMoveEvent>(evt => OnRowMove(evt, index));

                m_Scroll.Add(row);
                m_Rows.Add(row);
            }
        }

        private static void SetRowFocusedVisual(Button row, List<Label> cells, bool focused)
        {
            row.style.backgroundColor = focused ? MenuUI.FocusedBackground : MenuUI.UnfocusedBackground;
            Color text = focused ? MenuUI.FocusedText : RowText;
            foreach (Label cell in cells)
            {
                cell.style.color = text;
            }
        }

        private void MenuUIFocusRow(int index)
        {
            if (index < 0 || index >= m_Rows.Count) return;
            m_Rows[index].Focus();
        }

        private void OnRowMove(NavigationMoveEvent evt, int index)
        {
            evt.PreventDefault();
            evt.StopPropagation();

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    MenuUIFocusRow(index - 1);
                    break;
                case NavigationMoveEvent.Direction.Down:
                    if (index + 1 < m_Rows.Count) MenuUIFocusRow(index + 1);
                    else m_RefreshButton.Focus();
                    break;
            }
        }

        private void OnBottomButtonMove(NavigationMoveEvent evt, Button from)
        {
            evt.PreventDefault();
            evt.StopPropagation();

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Left:
                    if (from == m_RefreshButton) m_BackButton.Focus();
                    break;
                case NavigationMoveEvent.Direction.Right:
                    if (from == m_BackButton) m_RefreshButton.Focus();
                    break;
                case NavigationMoveEvent.Direction.Up:
                    MenuUIFocusRow(Mathf.Clamp(m_LastFocusedRow, 0, m_Rows.Count - 1));
                    break;
            }
        }

        private void OnRowSelected(GameSessionListing session)
        {
            MenuUI.PlaySound(selectSound);
            // Placeholder until joining is wired up (Fusion 2: StartGame with SessionName = session.SessionName).
            m_StatusLabel.text = $"Joining \"{session.SessionName}\" isn't hooked up yet.";
            Debug.Log($"[GameBrowser] Join '{session.SessionName}' (not implemented yet).");
        }

        private void OnBack()
        {
            string target = string.IsNullOrEmpty(GameSearch.BrowserBackScene) ? defaultBackSceneName : GameSearch.BrowserBackScene;
            SceneManager.LoadScene(target);
        }
    }
}
