using System.Collections.Generic;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Game Results screen (2026-11-09): what everyone sees once the team misses a round's target and the
    /// match is over. It replaces the old Final Score scene - it's a popup inside the gameplay scene, so the
    /// map is still there behind it:
    ///   - the screen fades to black and back in on the course flyover (the same camera path as the start of
    ///     each round, <see cref="MatchIntro.FlyoverFor"/>, from the beginning) with every avatar and the HUD
    ///     hidden, behind a dark see-through panel with white text and lines;
    ///   - the panel holds a table, Mario Party results style: a row per player (best score first, this
    ///     player's row lit up) and a column per stat the wagers are about - score (the gold column), pickups,
    ///     items, jumps, deaths, running / walking / ice time, rails, walls, poles (all, vertical and
    ///     horizontal), dinosaur rides. Map-only columns (pickups, items, dinosaurs) only show when the
    ///     round(s) on screen had them. The header row and the player column stay put while the rest scrolls
    ///     both ways (mouse wheel, Shift + wheel sideways, the scroll bars, arrow keys / WASD, either stick or
    ///     the d-pad);
    ///   - it opens on the totals of all rounds; Buy Yes / Buy No (or the &lt; &gt; buttons) step forwards /
    ///     backwards through each round's own table and back round to the totals;
    ///   - Leave Game at the bottom asks "Return to the Main Menu?" (No focused) - Yes leaves the session and
    ///     goes to the main menu. Esc / Start asks the same; Esc / Start / B in the question means No. Each
    ///     player leaves on their own; the others stay on the screen.
    ///
    /// The numbers come from <see cref="MatchStats"/> (every client recorded every round as it ended). The
    /// target-missed song (MusicTracks.RoundLost) starts when the round ends and carries on through this.
    ///
    /// Added at runtime by <see cref="RoundTimer"/> when it reaches its GameOver phase (no scene setup); goes
    /// when the scene does. The Esc menu is switched off meanwhile (this screen has its own way out).
    /// </summary>
    public class GameResults : MonoBehaviour
    {
        public const string MainMenuSceneName = "MainMenu";

        private const float FadeToBlackSeconds = 0.45f;
        private const float FadeInSeconds = 0.6f;
        private const float InputGraceSeconds = 1f;      // presses still mashed from the round don't count yet
        private const float ScrollSpeed = 900f;          // pixels per second, stick / keys held fully
        private const float StickDeadZone = 0.25f;

        // Table layout (panel pixels).
        private const float NameWidth = 270f;
        private const float RankWidth = 40f;
        private const float ScoreWidth = 130f;
        private const float CellWidth = 124f;
        private const float RowHeight = 38f;
        private const float HeaderHeight = 60f;

        private static readonly Color LineColor = new Color(1f, 1f, 1f, 0.3f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.6f);
        private static readonly Color Gold = new Color(1f, 0.82f, 0.3f);
        private static readonly Color GoldTint = new Color(1f, 0.8f, 0.2f, 0.14f);
        private static readonly Color LocalTint = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color Dim = new Color(0.82f, 0.82f, 0.82f);
        private static readonly Color PassedColor = new Color(0.55f, 1f, 0.55f);
        private static readonly Color MissedColor = new Color(1f, 0.5f, 0.45f);

        private enum Kind { Count, Seconds }
        private enum Needs { Nothing, Pickups, Items, Dinosaurs }

        private readonly struct Column
        {
            public readonly string Header;
            public readonly WagerStat Stat;
            public readonly Kind Kind;
            public readonly Needs Needs;

            public Column(string header, WagerStat stat, Kind kind, Needs needs = Needs.Nothing)
            {
                Header = header; Stat = stat; Kind = kind; Needs = needs;
            }
        }

        /// <summary>The stat columns after Score, left to right. Headers are LocStrings keys.</summary>
        private static readonly Column[] Columns =
        {
            new Column("PICKUPS", WagerStat.Pickups, Kind.Count, Needs.Pickups),
            new Column("ITEMS", WagerStat.Items, Kind.Count, Needs.Items),
            new Column("JUMPS", WagerStat.Jumps, Kind.Count),
            new Column("DEATHS", WagerStat.Deaths, Kind.Count),
            new Column("RUN TIME", WagerStat.RunSeconds, Kind.Seconds),
            new Column("WALK TIME", WagerStat.WalkSeconds, Kind.Seconds),
            new Column("ICE TIME", WagerStat.IceSeconds, Kind.Seconds),
            new Column("RAIL TIME", WagerStat.RailSeconds, Kind.Seconds),
            new Column("RAIL JUMPS", WagerStat.RailJumps, Kind.Count),
            new Column("WALL TIME", WagerStat.WallSeconds, Kind.Seconds),
            new Column("WALL JUMPS", WagerStat.WallJumps, Kind.Count),
            new Column("POLE TIME", WagerStat.PoleSeconds, Kind.Seconds),
            new Column("POLE JUMPS", WagerStat.PoleJumps, Kind.Count),
            new Column("POLE ROTATIONS", WagerStat.PoleRotations, Kind.Count),
            new Column("VERTICAL POLE TIME", WagerStat.VerticalPoleSeconds, Kind.Seconds),
            new Column("VERTICAL POLE JUMPS", WagerStat.VerticalPoleJumps, Kind.Count),
            new Column("VERTICAL POLE ROTATIONS", WagerStat.VerticalPoleRotations, Kind.Count),
            new Column("HORIZONTAL POLE TIME", WagerStat.HorizontalPoleSeconds, Kind.Seconds),
            new Column("HORIZONTAL POLE JUMPS", WagerStat.HorizontalPoleJumps, Kind.Count),
            new Column("HORIZONTAL POLE ROTATIONS", WagerStat.HorizontalPoleRotations, Kind.Count),
            new Column("DINOSAUR RIDES", WagerStat.DinosaurRides, Kind.Count, Needs.Dinosaurs),
            new Column("DINOSAUR RIDE TIME", WagerStat.DinosaurRideSeconds, Kind.Seconds, Needs.Dinosaurs),
        };

        /// <summary>One player's line in a table (a round's, or everything added up).</summary>
        private class Row
        {
            public int PlayerId;
            public string Name;
            public int Score;
            public readonly int[] Values = new int[WagerStats.StatCount];
        }

        /// <summary>One table: all rounds (Round == null) or one round.</summary>
        private class View
        {
            public MatchStats.Round Round;
            public bool Pickups, Items, Dinosaurs;
            public readonly List<Row> Rows = new List<Row>();
        }

        /// <summary>True while the screen is up on this machine.</summary>
        public static bool IsActive { get; private set; }

        private readonly List<View> m_Views = new List<View>();
        private int m_ViewIndex;

        private float m_StartTime, m_ShownTime;
        private bool m_TookOver, m_Leaving;
        private MatchIntro.IFlyover m_Flyover;
        private InputBindingsData m_Bindings;

        // What this took over, given back in OnDestroy.
        private readonly Dictionary<UIDocument, StyleEnum<DisplayStyle>> m_HiddenDocuments = new Dictionary<UIDocument, StyleEnum<DisplayStyle>>();
        private readonly HashSet<Renderer> m_HiddenRenderers = new HashSet<Renderer>();
        private CinemachineBrain m_PausedBrain;
        private float m_NextHideRefresh;

        // UI
        private UIDocument m_Document;
        private VisualElement m_Root, m_Cover, m_Panel;
        private Label m_ViewTitle, m_PrevHint, m_NextHint, m_InfoLabel;
        private StarLabel m_ResultLabel;
        private VisualElement m_HeaderViewport, m_HeaderRow, m_NamesViewport, m_NamesColumn;
        private ScrollView m_Body;
        private Button m_LeaveButton, m_YesButton, m_NoButton;
        private VisualElement m_Confirm;
        private Label m_ConfirmStatus;

        /// <summary>Opens the screen (called by RoundTimer on every client when the match is over).</summary>
        public static void Begin(RoundTimer round)
        {
            if (round == null || round.GetComponent<GameResults>() != null) return;
            round.gameObject.AddComponent<GameResults>();
        }

        private void Start()
        {
            IsActive = true;
            m_StartTime = Time.unscaledTime;

            m_Bindings = InputBindingsStore.Load();
            InputBindingsStore.Saved += ReloadBindings;
            CoreInputHandler.MenuPressed += HandleMenuPressed;
            InGameMenu.Suppress(true); // this screen has its own way out

            BuildViews();
            BuildOverlay();
            if (m_Root != null) ShowView(0);
        }

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
            CoreInputHandler.MenuPressed -= HandleMenuPressed;
            InGameMenu.Suppress(false);
            Restore();
            if (m_Document != null) Destroy(m_Document.gameObject);
            IsActive = false;
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        // =====================================================================================
        // Every frame
        // =====================================================================================

        private void Update()
        {
            if (m_Root == null) return;

            if (!m_TookOver)
            {
                // Fade the round's last moment to black, then swap to the flyover behind the results.
                float t = Time.unscaledTime - m_StartTime;
                m_Cover.style.opacity = Mathf.Clamp01(t / FadeToBlackSeconds);
                if (t < FadeToBlackSeconds) return;

                m_TookOver = true;
                m_ShownTime = Time.unscaledTime;
                m_Panel.style.display = DisplayStyle.Flex;
                MenuUI.FocusFirst(m_LeaveButton);
            }

            float k = Mathf.Clamp01((Time.unscaledTime - m_ShownTime) / FadeInSeconds);
            m_Cover.style.opacity = 1f - k;
            m_Panel.style.opacity = k;

            HideEverythingElse();
            if (!m_Leaving)
            {
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            }
            FreezeHeaders();
            UpdateKeyHints();

            if (m_Leaving || Time.unscaledTime - m_ShownTime < InputGraceSeconds) return;
            HandleInput();
        }

        private void LateUpdate()
        {
            if (!m_TookOver) return;

            Camera camera = Camera.main;
            if (camera == null) return;

            CinemachineBrain brain = camera.GetComponent<CinemachineBrain>();
            if (brain != null && brain.enabled)
            {
                brain.enabled = false;
                m_PausedBrain = brain;
            }

            m_Flyover ??= MatchIntro.FlyoverFor(MatchLayout.Current);
            if (m_Flyover != null && m_Flyover.TryEvaluate(Time.unscaledTime - m_ShownTime, out Vector3 position, out Quaternion rotation))
            {
                camera.transform.SetPositionAndRotation(position, rotation);
            }
        }

        /// <summary>Every avatar (and nameplate), and every other UI on screen - the HUD, wagers, items...</summary>
        private void HideEverythingElse()
        {
            if (Time.unscaledTime >= m_NextHideRefresh)
            {
                m_NextHideRefresh = Time.unscaledTime + 0.5f;

                foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                {
                    if (doc == m_Document || doc.rootVisualElement == null || m_HiddenDocuments.ContainsKey(doc)) continue;
                    if (ChatOverlay.IsChatDocument(doc)) continue; // the chat stays (2026-11-10)
                    m_HiddenDocuments[doc] = doc.rootVisualElement.style.display;
                }

                foreach (CorePlayerManager player in NetworkPlayers.All)
                {
                    if (player == null) continue;
                    foreach (Renderer r in player.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r == null || r.forceRenderingOff) continue;
                        r.forceRenderingOff = true;
                        m_HiddenRenderers.Add(r);
                    }
                }
            }

            // Every frame, in case something shows its own UI again.
            foreach (KeyValuePair<UIDocument, StyleEnum<DisplayStyle>> pair in m_HiddenDocuments)
            {
                VisualElement root = pair.Key != null ? pair.Key.rootVisualElement : null;
                if (root != null && root.style.display != DisplayStyle.None) root.style.display = DisplayStyle.None;
            }
        }

        private void Restore()
        {
            foreach (KeyValuePair<UIDocument, StyleEnum<DisplayStyle>> pair in m_HiddenDocuments)
            {
                VisualElement root = pair.Key != null ? pair.Key.rootVisualElement : null;
                if (root != null) root.style.display = pair.Value;
            }
            m_HiddenDocuments.Clear();

            foreach (Renderer r in m_HiddenRenderers)
            {
                if (r != null) r.forceRenderingOff = false;
            }
            m_HiddenRenderers.Clear();

            if (m_PausedBrain != null) m_PausedBrain.enabled = true;
            m_PausedBrain = null;
        }

        // =====================================================================================
        // Input
        // =====================================================================================

        private void HandleInput()
        {
            if (ChatOverlay.IsOpen) return; // typing / the message builder has the keys and sticks
            bool confirmOpen = m_Confirm.style.display == DisplayStyle.Flex;

            if (confirmOpen)
            {
                if (GamepadCancelPressed()) CloseConfirm();
            }
            else if (m_Bindings != null)
            {
                if (BoundControls.WasPressed(m_Bindings.Keyboard.BetYes, m_Bindings.Gamepad.BetYes)) ShowView(m_ViewIndex + 1);
                else if (BoundControls.WasPressed(m_Bindings.Keyboard.BetNo, m_Bindings.Gamepad.BetNo)) ShowView(m_ViewIndex - 1);
            }

            // Space presses the focused button too (as in the Esc menu).
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) SubmitFocusedButton();

            if (!confirmOpen)
            {
                Vector2 scroll = ScrollInput();
                if (scroll != Vector2.zero) m_Body.scrollOffset += new Vector2(scroll.x, -scroll.y) * (ScrollSpeed * Time.unscaledDeltaTime);
            }
        }

        /// <summary>Esc / Start (the Menu button): ask about leaving - or, if already asking, No.</summary>
        private void HandleMenuPressed(InputControl control)
        {
            if (!m_TookOver || m_Leaving || Time.unscaledTime - m_ShownTime < InputGraceSeconds) return;
            if (ChatOverlay.SwallowsMenu) return; // that Esc / Start closed the chat
            if (m_Confirm.style.display == DisplayStyle.Flex) CloseConfirm();
            else OpenConfirm();
        }

        /// <summary>Arrow keys / WASD, both sticks and the d-pad: x right, y up; each axis -1..1.</summary>
        private static Vector2 ScrollInput()
        {
            Vector2 v = Vector2.zero;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.leftArrowKey.isPressed || keyboard.aKey.isPressed) v.x -= 1f;
                if (keyboard.rightArrowKey.isPressed || keyboard.dKey.isPressed) v.x += 1f;
                if (keyboard.upArrowKey.isPressed || keyboard.wKey.isPressed) v.y += 1f;
                if (keyboard.downArrowKey.isPressed || keyboard.sKey.isPressed) v.y -= 1f;
            }

            Gamepad pad = Gamepad.current;
            if (pad != null && Application.isFocused)
            {
                v += DeadZone(pad.leftStick.ReadValue()) + DeadZone(pad.rightStick.ReadValue()) + pad.dpad.ReadValue();
            }
            return new Vector2(Mathf.Clamp(v.x, -1f, 1f), Mathf.Clamp(v.y, -1f, 1f));
        }

        private static Vector2 DeadZone(Vector2 stick) => stick.magnitude < StickDeadZone ? Vector2.zero : stick;

        private static bool GamepadCancelPressed()
        {
            if (!Application.isFocused) return false;
            foreach (Gamepad pad in Gamepad.all)
            {
                if (pad.TryGetChildControl<ButtonControl>(GamepadUIBindingFix.CancelControl) is ButtonControl cancel && cancel.wasPressedThisFrame) return true;
            }
            return false;
        }

        private void SubmitFocusedButton()
        {
            if (!(m_Root.panel?.focusController?.focusedElement is Button button)) return;
            if (!button.enabledInHierarchy) return;
            using (NavigationSubmitEvent evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        private static bool UsingGamepad()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;
            Keyboard keyboard = Keyboard.current;
            return keyboard == null || pad.lastUpdateTime > keyboard.lastUpdateTime;
        }

        private void UpdateKeyHints()
        {
            if (m_Bindings == null) return;
            bool gamepad = UsingGamepad();
            string no = gamepad ? ControlNames.Gamepad(m_Bindings.Gamepad.BetNo) : ControlNames.Keyboard(m_Bindings.Keyboard.BetNo);
            string yes = gamepad ? ControlNames.Gamepad(m_Bindings.Gamepad.BetYes) : ControlNames.Keyboard(m_Bindings.Keyboard.BetYes);
            string prev = string.IsNullOrEmpty(no) ? "" : $"[{no}]";
            string next = string.IsNullOrEmpty(yes) ? "" : $"[{yes}]";
            if (m_PrevHint.text != prev) m_PrevHint.text = prev;
            if (m_NextHint.text != next) m_NextHint.text = next;
        }

        // =====================================================================================
        // Leaving
        // =====================================================================================

        private void OpenConfirm()
        {
            if (m_Leaving) return;
            m_ConfirmStatus.text = "";
            m_Confirm.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_NoButton); // No is the safe default
        }

        private void CloseConfirm()
        {
            if (m_Leaving) return;
            m_Confirm.style.display = DisplayStyle.None;
            MenuUI.FocusFirst(m_LeaveButton);
        }

        private async void Leave()
        {
            if (m_Leaving) return;
            m_Leaving = true;
            m_YesButton.SetEnabled(false);
            m_NoButton.SetEnabled(false);
            m_LeaveButton.SetEnabled(false);
            m_ConfirmStatus.text = Loc.T("Leaving...");

            if (FusionSessionService.HasInstance)
            {
                await FusionSessionService.Instance.LeaveGameAsync();
            }
            SceneManager.LoadScene(MainMenuSceneName);
        }

        // =====================================================================================
        // The tables
        // =====================================================================================

        /// <summary>All rounds added up first, then each round.</summary>
        private void BuildViews()
        {
            m_Views.Clear();
            IReadOnlyList<MatchStats.Round> rounds = MatchStats.Rounds;

            var total = new View();
            var byId = new Dictionary<int, Row>();
            foreach (MatchStats.Round round in rounds)
            {
                total.Pickups |= round.HasPickups;
                total.Items |= round.HasItems;
                total.Dinosaurs |= round.HasDinosaurs;
                foreach (MatchStats.PlayerLine line in round.Players)
                {
                    if (!byId.TryGetValue(line.PlayerId, out Row row))
                    {
                        row = new Row { PlayerId = line.PlayerId };
                        byId[line.PlayerId] = row;
                        total.Rows.Add(row);
                    }
                    row.Name = line.Name; // the latest
                    row.Score += line.Score;
                    for (int i = 0; i < row.Values.Length && i < line.Values.Length; i++) row.Values[i] += line.Values[i];
                }
            }
            m_Views.Add(total);

            foreach (MatchStats.Round round in rounds)
            {
                var view = new View { Round = round, Pickups = round.HasPickups, Items = round.HasItems, Dinosaurs = round.HasDinosaurs };
                foreach (MatchStats.PlayerLine line in round.Players)
                {
                    var row = new Row { PlayerId = line.PlayerId, Name = line.Name, Score = line.Score };
                    for (int i = 0; i < row.Values.Length && i < line.Values.Length; i++) row.Values[i] = line.Values[i];
                    view.Rows.Add(row);
                }
                m_Views.Add(view);
            }

            foreach (View view in m_Views)
            {
                view.Rows.Sort((a, b) =>
                {
                    int byScore = b.Score.CompareTo(a.Score);
                    return byScore != 0 ? byScore : string.Compare(a.Name, b.Name, System.StringComparison.OrdinalIgnoreCase);
                });
            }
        }

        private void ShowView(int index)
        {
            int count = Mathf.Max(1, m_Views.Count);
            m_ViewIndex = ((index % count) + count) % count;
            View view = m_Views.Count > 0 ? m_Views[m_ViewIndex] : new View();

            if (view.Round == null)
            {
                m_ViewTitle.text = Loc.T("ALL ROUNDS");
                int cleared = 0;
                foreach (MatchStats.Round round in MatchStats.Rounds) if (round.Passed) cleared++;
                m_InfoLabel.text = Loc.F("Rounds cleared: {0}", cleared);
                m_ResultLabel.style.display = DisplayStyle.None;
            }
            else
            {
                MatchStats.Round round = view.Round;
                m_ViewTitle.text = Loc.F("ROUND {0}", round.RoundNumber);
                m_InfoLabel.text = MapName(round.MapId);
                m_ResultLabel.text = Loc.F("Team scored {0} of {1}", StarLabel.Amount(round.TeamTotal), StarLabel.Amount(round.TeamTarget)) +
                                     " - " + Loc.T(round.Passed ? "TARGET REACHED!" : "TARGET MISSED");
                m_ResultLabel.style.color = round.Passed ? PassedColor : MissedColor;
                m_ResultLabel.style.display = DisplayStyle.Flex;
            }

            BuildTable(view);
        }

        private void BuildTable(View view)
        {
            m_HeaderRow.Clear();
            m_NamesColumn.Clear();
            m_Body.Clear();

            var columns = new List<Column>();
            foreach (Column column in Columns)
            {
                if (column.Needs == Needs.Pickups && !view.Pickups) continue;
                if (column.Needs == Needs.Items && !view.Items) continue;
                if (column.Needs == Needs.Dinosaurs && !view.Dinosaurs) continue;
                columns.Add(column);
            }
            float width = ScoreWidth + columns.Count * CellWidth;

            // Header row (the "PLAYER" corner is fixed, outside it).
            m_HeaderRow.style.width = width;
            Label scoreHeader = Cell(Loc.T("SCORE"), ScoreWidth, HeaderHeight, 15);
            scoreHeader.style.color = Gold;
            scoreHeader.style.backgroundColor = GoldTint;
            m_HeaderRow.Add(scoreHeader);
            foreach (Column column in columns) m_HeaderRow.Add(Cell(Loc.T(column.Header), CellWidth, HeaderHeight, 12));

            if (view.Rows.Count == 0)
            {
                Label none = Cell(Loc.T("No results"), NameWidth, RowHeight, 16);
                none.style.borderRightWidth = 0;
                m_NamesColumn.Add(none);
                return;
            }

            int local = (int)NetworkPlayers.LocalClientId;
            foreach (Row row in view.Rows)
            {
                int rank = 1;
                foreach (Row other in view.Rows) if (other.Score > row.Score) rank++;
                bool isLocal = row.PlayerId == local;

                // Player column: rank and name.
                VisualElement nameRow = RowElement(NameWidth, isLocal);
                Label rankLabel = Cell(rank.ToString(), RankWidth, RowHeight, 18);
                rankLabel.style.color = Dim;
                rankLabel.style.borderRightWidth = 0;
                nameRow.Add(rankLabel);
                Label name = Cell(row.Name, NameWidth - RankWidth, RowHeight, 18);
                name.style.unityTextAlign = TextAnchor.MiddleLeft;
                name.style.whiteSpace = WhiteSpace.NoWrap;
                name.style.overflow = Overflow.Hidden;
                name.style.textOverflow = TextOverflow.Ellipsis;
                name.style.paddingLeft = 6;
                nameRow.Add(name);
                m_NamesColumn.Add(nameRow);

                // The numbers.
                VisualElement bodyRow = RowElement(width, isLocal);
                VisualElement scoreCell = new VisualElement();
                SizeCell(scoreCell, ScoreWidth, RowHeight);
                scoreCell.style.backgroundColor = GoldTint;
                scoreCell.style.alignItems = Align.Center;
                scoreCell.style.justifyContent = Justify.Center;
                var score = new StarLabel(StarLabel.Amount(row.Score), Justify.Center) { pickingMode = PickingMode.Ignore };
                score.style.color = Gold;
                score.style.fontSize = 18;
                scoreCell.Add(score);
                bodyRow.Add(scoreCell);

                foreach (Column column in columns)
                {
                    int value = row.Values[(int)column.Stat];
                    bodyRow.Add(Cell(column.Kind == Kind.Seconds ? Seconds(value) : value.ToString(), CellWidth, RowHeight, 17));
                }
                m_Body.Add(bodyRow);
            }
        }

        /// <summary>Keeps the header row and the player column lined up with the scrolled numbers.</summary>
        private void FreezeHeaders()
        {
            Vector2 offset = m_Body.scrollOffset;
            m_HeaderRow.style.translate = new Translate(-offset.x, 0f);
            m_NamesColumn.style.translate = new Translate(0f, -offset.y);

            // Leave room for the scroll bars so the frozen parts end where the numbers do.
            Scroller across = m_Body.horizontalScroller, down = m_Body.verticalScroller;
            float bottom = across != null && across.resolvedStyle.display == DisplayStyle.Flex ? across.layout.height : 0f;
            float right = down != null && down.resolvedStyle.display == DisplayStyle.Flex ? down.layout.width : 0f;
            if (float.IsNaN(bottom)) bottom = 0f;
            if (float.IsNaN(right)) right = 0f;
            m_NamesViewport.style.marginBottom = bottom;
            m_HeaderViewport.style.marginRight = right;
        }

        /// <summary>"12.3s", or "1:02.3" from a minute up.</summary>
        private static string Seconds(int tenths)
        {
            tenths = Mathf.Max(0, tenths);
            int whole = tenths / 10, fraction = tenths % 10;
            return whole < 60 ? $"{whole}.{fraction}s" : $"{whole / 60}:{whole % 60:00}.{fraction}";
        }

        /// <summary>"DinosaurZoo" -> "Dinosaur Zoo" (map names aren't translated).</summary>
        private static string MapName(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return "";
            var sb = new System.Text.StringBuilder(mapId.Length + 4);
            for (int i = 0; i < mapId.Length; i++)
            {
                char c = mapId[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(mapId[i - 1])) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void BuildOverlay()
        {
            PanelSettings panelSettings = null;
            foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (doc.panelSettings != null) { panelSettings = doc.panelSettings; break; }
            }
            if (panelSettings == null)
            {
                Debug.LogWarning("[GameResults] No UI panel settings found in the scene - can't show the results.", this);
                return;
            }

            var go = new GameObject("GameResultsOverlay");
            go.SetActive(false);
            m_Document = go.AddComponent<UIDocument>();
            m_Document.panelSettings = panelSettings;
            m_Document.sortingOrder = 60f;
            go.SetActive(true);

            m_Root = m_Document.rootVisualElement;
            m_Root.style.flexGrow = 1;
            m_Root.pickingMode = PickingMode.Ignore;

            // Ignore the UI's own Cancel (Esc / B): the Menu button and the direct read in HandleInput do it.
            m_Root.RegisterCallback<NavigationCancelEvent>(evt => evt.StopPropagation());
            NavigationDedupe.Attach(m_Root, "Game results");

            m_Cover = new VisualElement { pickingMode = PickingMode.Ignore };
            Stretch(m_Cover);
            m_Cover.style.backgroundColor = Color.black;
            m_Cover.style.opacity = 0f;
            m_Root.Add(m_Cover);

            // The panel.
            m_Panel = new VisualElement();
            m_Panel.style.position = Position.Absolute;
            // The chat has the bottom-left corner (2026-11-10): the panel starts to the right of it.
            m_Panel.style.left = Length.Percent(ChatOverlay.Available ? 17 : 5);
            m_Panel.style.right = Length.Percent(5);
            m_Panel.style.top = Length.Percent(5);
            m_Panel.style.bottom = Length.Percent(5);
            m_Panel.style.backgroundColor = new Color(0f, 0f, 0f, 0.72f);
            Border(m_Panel, 2f, BorderColor);
            Rounded(m_Panel, 10f);
            m_Panel.style.paddingTop = 18;
            m_Panel.style.paddingBottom = 14;
            m_Panel.style.paddingLeft = 24;
            m_Panel.style.paddingRight = 24;
            m_Panel.style.opacity = 0f;
            m_Panel.style.display = DisplayStyle.None;
            m_Root.Add(m_Panel);

            var title = new Label(Loc.T("GAME RESULTS"));
            title.style.fontSize = 34;
            title.style.color = Color.white;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.marginBottom = 6;
            m_Panel.Add(title);

            // < [2]  ALL ROUNDS  [1] >
            var selector = new VisualElement();
            selector.style.flexDirection = FlexDirection.Row;
            selector.style.justifyContent = Justify.Center;
            selector.style.alignItems = Align.Center;
            m_Panel.Add(selector);

            selector.Add(ArrowButton("<", () => ShowView(m_ViewIndex - 1)));
            m_PrevHint = HintLabel();
            selector.Add(m_PrevHint);
            m_ViewTitle = new Label();
            m_ViewTitle.style.fontSize = 24;
            m_ViewTitle.style.color = Color.white;
            m_ViewTitle.style.minWidth = 260;
            m_ViewTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            selector.Add(m_ViewTitle);
            m_NextHint = HintLabel();
            selector.Add(m_NextHint);
            selector.Add(ArrowButton(">", () => ShowView(m_ViewIndex + 1)));

            var info = new VisualElement();
            info.style.flexDirection = FlexDirection.Row;
            info.style.justifyContent = Justify.Center;
            info.style.alignItems = Align.Center;
            info.style.marginTop = 2;
            info.style.marginBottom = 10;
            info.style.minHeight = 26;
            m_Panel.Add(info);
            m_InfoLabel = new Label();
            m_InfoLabel.style.fontSize = 17;
            m_InfoLabel.style.color = Dim;
            m_InfoLabel.style.marginRight = 14;
            info.Add(m_InfoLabel);
            m_ResultLabel = new StarLabel("", Justify.Center) { pickingMode = PickingMode.Ignore };
            m_ResultLabel.style.fontSize = 17;
            info.Add(m_ResultLabel);

            m_Panel.Add(BuildTableFrame());

            // Leave Game
            var bottom = new VisualElement();
            bottom.style.flexDirection = FlexDirection.Row;
            bottom.style.justifyContent = Justify.Center;
            bottom.style.marginTop = 8;
            m_LeaveButton = MenuUI.MakeButton(Loc.T("Leave Game"), OpenConfirm, null, 260f);
            bottom.Add(m_LeaveButton);
            m_Panel.Add(bottom);

            BuildConfirm();

            // Only the Leave Game / Yes / No buttons take focus, so the stick and d-pad just scroll.
            m_Root.Query<VisualElement>().ForEach(e =>
            {
                if (e != m_LeaveButton && e != m_YesButton && e != m_NoButton) e.focusable = false;
            });

            foreach (Label label in m_Root.Query<Label>().ToList()) label.pickingMode = PickingMode.Ignore;
            GameFont.Apply(m_Root);
        }

        /// <summary>
        /// The table: a fixed "PLAYER" corner, a header row that only scrolls sideways, a player column that only
        /// scrolls up and down, and the numbers (a ScrollView both ways) driving the other two.
        /// </summary>
        private VisualElement BuildTableFrame()
        {
            var frame = new VisualElement();
            frame.style.flexGrow = 1;
            frame.style.flexShrink = 1;
            frame.style.minHeight = 120;
            Border(frame, 1f, BorderColor);

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.flexShrink = 0;
            top.style.height = HeaderHeight;
            top.style.borderBottomWidth = 2;
            top.style.borderBottomColor = BorderColor;
            frame.Add(top);

            Label corner = Cell(Loc.T("PLAYER"), NameWidth, HeaderHeight, 15);
            corner.style.borderRightWidth = 2;
            corner.style.borderRightColor = BorderColor;
            top.Add(corner);

            m_HeaderViewport = new VisualElement();
            m_HeaderViewport.style.flexGrow = 1;
            m_HeaderViewport.style.overflow = Overflow.Hidden;
            m_HeaderViewport.RegisterCallback<WheelEvent>(evt =>
            {
                m_Body.scrollOffset += new Vector2(evt.delta.y * m_Body.mouseWheelScrollSize, 0f);
                evt.StopPropagation();
            });
            top.Add(m_HeaderViewport);
            m_HeaderRow = new VisualElement();
            m_HeaderRow.style.flexDirection = FlexDirection.Row;
            m_HeaderRow.style.flexShrink = 0;
            m_HeaderRow.style.height = HeaderHeight;
            m_HeaderViewport.Add(m_HeaderRow);

            var rest = new VisualElement();
            rest.style.flexDirection = FlexDirection.Row;
            rest.style.flexGrow = 1;
            rest.style.flexShrink = 1;
            frame.Add(rest);

            m_NamesViewport = new VisualElement();
            m_NamesViewport.style.width = NameWidth;
            m_NamesViewport.style.flexShrink = 0;
            m_NamesViewport.style.overflow = Overflow.Hidden;
            m_NamesViewport.style.borderRightWidth = 2;
            m_NamesViewport.style.borderRightColor = BorderColor;
            m_NamesViewport.RegisterCallback<WheelEvent>(evt =>
            {
                m_Body.scrollOffset += new Vector2(0f, evt.delta.y * m_Body.mouseWheelScrollSize);
                evt.StopPropagation();
            });
            rest.Add(m_NamesViewport);
            m_NamesColumn = new VisualElement();
            m_NamesColumn.style.flexShrink = 0;
            m_NamesViewport.Add(m_NamesColumn);

            m_Body = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            m_Body.style.flexGrow = 1;
            m_Body.style.flexShrink = 1;
            m_Body.horizontalScrollerVisibility = ScrollerVisibility.Auto;
            m_Body.verticalScrollerVisibility = ScrollerVisibility.Auto;
            // Shift + wheel scrolls sideways.
            m_Body.RegisterCallback<WheelEvent>(evt =>
            {
                if (!evt.shiftKey) return;
                float delta = Mathf.Abs(evt.delta.y) > Mathf.Abs(evt.delta.x) ? evt.delta.y : evt.delta.x;
                m_Body.scrollOffset += new Vector2(delta * m_Body.mouseWheelScrollSize, 0f);
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
            rest.Add(m_Body);

            return frame;
        }

        private void BuildConfirm()
        {
            m_Confirm = new VisualElement();
            Stretch(m_Confirm);
            m_Confirm.style.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
            m_Confirm.style.alignItems = Align.Center;
            m_Confirm.style.justifyContent = Justify.Center;
            m_Confirm.style.display = DisplayStyle.None;
            m_Panel.Add(m_Confirm); // over the table, inside the panel (so it fades with it)

            var box = new VisualElement();
            box.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f, 0.95f);
            Border(box, 2f, BorderColor);
            Rounded(box, 10f);
            box.style.paddingTop = 20;
            box.style.paddingBottom = 16;
            box.style.paddingLeft = 40;
            box.style.paddingRight = 40;
            box.style.alignItems = Align.Center;
            m_Confirm.Add(box);

            var question = new Label(Loc.T("Return to the Main Menu?"));
            question.style.fontSize = 26;
            question.style.color = Color.white;
            question.style.marginBottom = 12;
            question.style.unityTextAlign = TextAnchor.MiddleCenter;
            box.Add(question);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.Center;
            m_YesButton = MenuUI.MakeButton(Loc.T("Yes"), Leave, null, 140f);
            m_NoButton = MenuUI.MakeButton(Loc.T("No"), CloseConfirm, null, 140f);
            m_YesButton.style.marginRight = 10;
            m_NoButton.style.marginLeft = 10;
            buttons.Add(m_YesButton);
            buttons.Add(m_NoButton);
            box.Add(buttons);

            m_ConfirmStatus = new Label("");
            m_ConfirmStatus.style.fontSize = 14;
            m_ConfirmStatus.style.color = Dim;
            m_ConfirmStatus.style.marginTop = 6;
            m_ConfirmStatus.style.unityTextAlign = TextAnchor.MiddleCenter;
            box.Add(m_ConfirmStatus);
        }

        private Button ArrowButton(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.width = 40;
            button.style.height = 34;
            button.style.fontSize = 22;
            button.style.color = Color.white;
            button.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f);
            Border(button, 1f, BorderColor);
            Rounded(button, 6f);
            button.style.marginLeft = 4;
            button.style.marginRight = 4;
            return button;
        }

        private static Label HintLabel()
        {
            var label = new Label();
            label.style.fontSize = 15;
            label.style.color = Dim;
            label.style.minWidth = 44;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            return label;
        }

        private static VisualElement RowElement(float width, bool highlighted)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexShrink = 0;
            row.style.width = width;
            row.style.height = RowHeight;
            row.style.borderBottomWidth = 1;
            row.style.borderBottomColor = LineColor;
            if (highlighted) row.style.backgroundColor = LocalTint;
            return row;
        }

        private static Label Cell(string text, float width, float height, int fontSize)
        {
            var label = new Label(text) { pickingMode = PickingMode.Ignore };
            SizeCell(label, width, height);
            label.style.fontSize = fontSize;
            label.style.color = Color.white;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.paddingLeft = 4;
            label.style.paddingRight = 4;
            return label;
        }

        private static void SizeCell(VisualElement cell, float width, float height)
        {
            cell.style.width = width;
            cell.style.minWidth = width;
            cell.style.height = height;
            cell.style.flexShrink = 0;
            cell.style.marginLeft = 0;
            cell.style.marginRight = 0;
            cell.style.marginTop = 0;
            cell.style.marginBottom = 0;
            cell.style.paddingTop = 0;
            cell.style.paddingBottom = 0;
            cell.style.borderRightWidth = 1;
            cell.style.borderRightColor = LineColor;
        }

        private static void Stretch(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0;
            element.style.right = 0;
            element.style.top = 0;
            element.style.bottom = 0;
        }

        private static void Border(VisualElement element, float width, Color color)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        private static void Rounded(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }
    }
}
