using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Practice Challenges list: every challenge in the <see cref="ChallengeCatalog"/> as a thumbnail
    /// (rendered in the editor by ChallengeThumbnails), the 16x16s then the 32x32s, <see cref="Columns"/> to a
    /// row in a scrolling box, each labelled "16.3" and named "Challenge 16.3" underneath when selected.
    /// Start / Back buttons below.
    ///
    /// Used in two places: the Single Player menu's Practice Challenges button (SinglePlayerController),
    /// and Change Challenge in a practice run (InGameMenu - the Esc menu and the "Retry Challenge?" box).
    /// The owner decides what Start and Back do, and handles B / Esc itself (it calls <see cref="Close"/>).
    ///
    /// Controls: arrows / stick move the selection (up and down cross from the 16s to the 32s); down from
    /// the last row goes to Start, up from the buttons back to the list; A / Enter or a double-click starts;
    /// a click selects. The mouse wheel or the scroll bar scrolls, and the list follows the selection.
    /// The last challenge picked is remembered for next time.
    /// </summary>
    public class ChallengePicker
    {
        public const int Columns = 6;
        private const float CellSize = 96f;
        private const float CellMargin = 4f;
        private const float CellSlot = CellSize + CellMargin * 2f;
        private const float VisibleRows = 3.3f;
        private const float ScrollBarWidth = 20f;

        private static readonly Color SelectedBorder = new Color(1f, 0.85f, 0.2f);
        private static readonly Color SelectedBorderUnfocused = new Color(0.75f, 0.75f, 0.75f);
        private static readonly Color ErrorColor = new Color(1f, 0.55f, 0.45f);
        private static readonly Color StatusColor = new Color(0.8f, 0.8f, 0.8f);

        /// <summary>The challenge last picked (its id), so the list reopens on it.</summary>
        private static string s_LastId;

        private readonly AudioClip m_SelectSound;
        private readonly AudioClip m_CancelSound;
        private readonly Action<ChallengeCatalog.Entry> m_OnStart;
        private readonly Action m_OnBack;

        private readonly ChallengeCatalog m_Catalog;
        private readonly List<ChallengeCatalog.Entry> m_Entries = new List<ChallengeCatalog.Entry>();
        private readonly List<VisualElement> m_Cells = new List<VisualElement>();
        private readonly List<List<int>> m_Rows = new List<List<int>>();          // entry indices, row by row
        private readonly List<VisualElement> m_RowTops = new List<VisualElement>();  // what to scroll to for each row (its section header for a section's first row)
        private readonly List<int> m_RowOf = new List<int>();                       // entry index -> row

        private VisualElement m_Grid;
        private ScrollView m_Scroll;
        private Label m_NameLabel;
        private Label m_StatusLabel;
        private Button m_StartButton;
        private Button m_BackButton;
        private int m_Selected;
        private bool m_Busy;

        /// <summary>The full-screen layer the list is drawn on (hidden until <see cref="Open"/>).</summary>
        public VisualElement Overlay { get; }

        public bool IsOpen => Overlay.style.display == DisplayStyle.Flex;

        /// <summary>The selected challenge (null if there are none).</summary>
        public ChallengeCatalog.Entry Selected => m_Selected >= 0 && m_Selected < m_Entries.Count ? m_Entries[m_Selected] : null;

        /// <param name="parent">Where to add the (full-screen) layer.</param>
        /// <param name="onStart">Start, A/Enter or a double-click, with the selected challenge.</param>
        /// <param name="onBack">The Back button. B / Esc are the owner's to handle.</param>
        public ChallengePicker(VisualElement parent, AudioClip selectSound, AudioClip cancelSound, Action<ChallengeCatalog.Entry> onStart, Action onBack)
        {
            m_SelectSound = selectSound;
            m_CancelSound = cancelSound;
            m_OnStart = onStart;
            m_OnBack = onBack;
            m_Catalog = ChallengeCatalog.Load();

            if (m_Catalog != null)
            {
                foreach (int size in new[] { 16, 32 })
                {
                    var ofSize = new List<ChallengeCatalog.Entry>();
                    foreach (ChallengeCatalog.Entry entry in m_Catalog.Challenges)
                    {
                        if (entry != null && entry.prefab != null && entry.size == size) ofSize.Add(entry);
                    }
                    ofSize.Sort((a, b) => m_Catalog.NumberOf(a).CompareTo(m_Catalog.NumberOf(b)));
                    m_Entries.AddRange(ofSize);
                }
            }

            Overlay = new VisualElement();
            Overlay.style.position = Position.Absolute;
            Overlay.style.left = 0;
            Overlay.style.right = 0;
            Overlay.style.top = 0;
            Overlay.style.bottom = 0;
            Overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.8f);
            Overlay.style.justifyContent = Justify.Center;
            Overlay.style.alignItems = Align.Center;
            Overlay.style.display = DisplayStyle.None;
            parent.Add(Overlay);

            Build();
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void Build()
        {
            var panel = new VisualElement();
            panel.style.backgroundColor = new Color(0.09f, 0.09f, 0.09f);
            SetBorder(panel, 2f, new Color(0.35f, 0.35f, 0.35f));
            panel.style.borderTopLeftRadius = 8;
            panel.style.borderTopRightRadius = 8;
            panel.style.borderBottomLeftRadius = 8;
            panel.style.borderBottomRightRadius = 8;
            panel.style.paddingLeft = 24;
            panel.style.paddingRight = 24;
            panel.style.paddingTop = 18;
            panel.style.paddingBottom = 18;
            panel.style.alignItems = Align.Center;
            Overlay.Add(panel);

            var title = new Label("PRACTICE CHALLENGES");
            title.style.fontSize = 26;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            panel.Add(title);

            var subtitle = new Label("Play one challenge on its own, as many times as you like.");
            subtitle.style.fontSize = 14;
            subtitle.style.color = new Color(0.7f, 0.7f, 0.7f);
            subtitle.style.marginBottom = 10;
            panel.Add(subtitle);

            // m_Grid is the focusable frame (it carries the focus highlight); the ScrollView inside holds
            // explicit rows of Columns cells under a header per size.
            m_Grid = new VisualElement { focusable = true };
            m_Grid.style.flexShrink = 0;
            m_Grid.style.paddingLeft = 4;
            m_Grid.style.paddingRight = 4;
            m_Grid.style.paddingTop = 4;
            m_Grid.style.paddingBottom = 4;
            panel.Add(m_Grid);

            m_Scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                focusable = false,
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible,
                touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped,
            };
            m_Scroll.style.width = Columns * CellSlot + ScrollBarWidth + 8f;
            m_Scroll.style.height = VisibleRows * CellSlot;
            m_Scroll.style.flexShrink = 0;
            m_Scroll.mouseWheelScrollSize = CellSlot / 2f;
            m_Scroll.verticalScroller.Query<VisualElement>().ForEach(e => e.focusable = false); // mouse only
            m_Scroll.horizontalScroller.Query<VisualElement>().ForEach(e => e.focusable = false);
            m_Grid.Add(m_Scroll);

            if (m_Entries.Count == 0)
            {
                var empty = new Label("No challenges yet.");
                empty.style.color = new Color(0.7f, 0.7f, 0.7f);
                empty.style.fontSize = 16;
                empty.style.marginTop = 20;
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                m_Scroll.Add(empty);
            }

            int previousSize = 0;
            VisualElement row = null;
            for (int i = 0; i < m_Entries.Count; i++)
            {
                ChallengeCatalog.Entry entry = m_Entries[i];
                bool newSection = entry.size != previousSize;
                VisualElement header = null;
                if (newSection)
                {
                    previousSize = entry.size;
                    header = new Label(entry.size == 32 ? "32x32 CHALLENGES" : "16x16 CHALLENGES");
                    header.style.color = new Color(0.75f, 0.75f, 0.75f);
                    header.style.fontSize = 15;
                    header.style.marginLeft = CellMargin;
                    header.style.marginTop = m_Rows.Count == 0 ? 0 : 8;
                    header.style.marginBottom = 2;
                    m_Scroll.Add(header);
                }

                if (newSection || m_Rows[m_Rows.Count - 1].Count >= Columns)
                {
                    row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.flexShrink = 0;
                    m_Scroll.Add(row);
                    m_Rows.Add(new List<int>());
                    m_RowTops.Add(header ?? row);
                }

                m_Rows[m_Rows.Count - 1].Add(i);
                m_RowOf.Add(m_Rows.Count - 1);
                row.Add(MakeCell(i, entry));
            }

            m_NameLabel = new Label("");
            m_NameLabel.style.fontSize = 22;
            m_NameLabel.style.color = Color.white;
            m_NameLabel.style.marginTop = 10;
            panel.Add(m_NameLabel);

            m_StatusLabel = new Label("");
            m_StatusLabel.style.fontSize = 15;
            m_StatusLabel.style.color = ErrorColor;
            m_StatusLabel.style.minHeight = 22;
            m_StatusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(m_StatusLabel);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginTop = 4;
            panel.Add(buttons);

            m_StartButton = MenuUI.MakeButton("Start", StartSelected, m_SelectSound, 180f);
            m_StartButton.style.marginRight = 12;
            buttons.Add(m_StartButton);
            m_BackButton = MenuUI.MakeButton("Back", () => { if (!m_Busy) m_OnBack?.Invoke(); }, m_CancelSound, 180f);
            buttons.Add(m_BackButton);

            m_Grid.RegisterCallback<NavigationMoveEvent>(OnGridMove, TrickleDown.TrickleDown);
            m_Grid.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.StopPropagation();
                if (m_Busy || m_Entries.Count == 0) return;
                MenuUI.PlaySound(m_SelectSound);
                StartSelected();
            }, TrickleDown.TrickleDown);
            m_Grid.RegisterCallback<FocusInEvent>(_ => RefreshCells());
            m_Grid.RegisterCallback<FocusOutEvent>(_ => RefreshCells());

            // Up from either button goes back to the list.
            foreach (Button b in new[] { m_StartButton, m_BackButton })
            {
                b.RegisterCallback<NavigationMoveEvent>(evt =>
                {
                    if (evt.direction != NavigationMoveEvent.Direction.Up) return;
                    evt.StopPropagation();
                    m_Grid.Focus();
                }, TrickleDown.TrickleDown);
            }
        }

        private VisualElement MakeCell(int index, ChallengeCatalog.Entry entry)
        {
            var cell = new Image { image = entry.thumbnail, scaleMode = ScaleMode.ScaleAndCrop };
            cell.style.width = CellSize;
            cell.style.height = CellSize;
            cell.style.flexShrink = 0;
            cell.style.marginLeft = CellMargin;
            cell.style.marginRight = CellMargin;
            cell.style.marginTop = CellMargin;
            cell.style.marginBottom = CellMargin;
            cell.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            SetBorder(cell, 3f, Color.clear);

            // "16.3" in the corner, so the list can be scanned by number.
            var number = new Label($"{entry.size}.{(m_Catalog != null ? m_Catalog.NumberOf(entry) : index + 1)}");
            number.style.position = Position.Absolute;
            number.style.left = 0;
            number.style.bottom = 0;
            number.style.fontSize = 13;
            number.style.color = Color.white;
            number.style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
            number.style.paddingLeft = 4;
            number.style.paddingRight = 4;
            number.pickingMode = PickingMode.Ignore;
            cell.Add(number);

            cell.RegisterCallback<ClickEvent>(evt =>
            {
                if (m_Busy) return;
                if (index != m_Selected)
                {
                    Select(index);
                    MenuUI.PlaySound(m_SelectSound);
                }
                if (evt.clickCount >= 2) StartSelected();
            });

            m_Cells.Add(cell);
            return cell;
        }

        // =====================================================================================
        // Open / close / state
        // =====================================================================================

        /// <summary>Shows the list with <paramref name="selectId"/> (or the last one picked) selected, focused on the list.</summary>
        public void Open(string selectId = null)
        {
            SetBusy(false, "");
            Overlay.style.display = DisplayStyle.Flex;

            int index = IndexOfId(selectId);
            if (index < 0) index = IndexOfId(s_LastId);
            Select(Mathf.Max(0, index));

            if (m_Entries.Count > 0) m_Grid.Focus();
            else MenuUI.FocusFirst(m_BackButton);
        }

        public void Close()
        {
            Overlay.style.display = DisplayStyle.None;
            m_Busy = false;
        }

        /// <summary>Greys everything out while a session starts (with a message), or brings it back.</summary>
        public void SetBusy(bool busy, string status)
        {
            m_Busy = busy;
            m_Grid.SetEnabled(!busy);
            m_StartButton.SetEnabled(!busy && m_Entries.Count > 0);
            m_BackButton.SetEnabled(!busy);
            m_StatusLabel.style.color = StatusColor;
            m_StatusLabel.text = status ?? "";
        }

        /// <summary>Shows an error under the list and makes it usable again.</summary>
        public void ShowError(string message)
        {
            SetBusy(false, "");
            m_StatusLabel.style.color = ErrorColor;
            m_StatusLabel.text = message ?? "";
            if (m_Entries.Count > 0) m_Grid.Focus();
        }

        private void StartSelected()
        {
            if (m_Busy) return;
            ChallengeCatalog.Entry entry = Selected;
            if (entry == null) return;
            s_LastId = entry.id;
            m_OnStart?.Invoke(entry);
        }

        private int IndexOfId(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            return m_Entries.FindIndex(e => string.Equals(e.id, id, StringComparison.OrdinalIgnoreCase));
        }

        // =====================================================================================
        // Selection
        // =====================================================================================

        private void OnGridMove(NavigationMoveEvent evt)
        {
            evt.StopPropagation();
            evt.PreventDefault();
            if (m_Busy || m_Entries.Count == 0) return;

            int row = m_RowOf[m_Selected];
            int col = m_Rows[row].IndexOf(m_Selected);

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Left: col = Mathf.Max(0, col - 1); break;
                case NavigationMoveEvent.Direction.Right: col = Mathf.Min(m_Rows[row].Count - 1, col + 1); break;
                case NavigationMoveEvent.Direction.Up: row = Mathf.Max(0, row - 1); break;
                case NavigationMoveEvent.Direction.Down:
                    if (row >= m_Rows.Count - 1)
                    {
                        MenuUI.FocusFirst(m_StartButton);
                        return;
                    }
                    row++;
                    break;
                default: return;
            }

            int next = m_Rows[row][Mathf.Min(col, m_Rows[row].Count - 1)];
            if (next != m_Selected)
            {
                Select(next);
                MenuUI.PlaySound(m_SelectSound);
            }
        }

        private void Select(int index)
        {
            if (m_Entries.Count == 0)
            {
                m_Selected = -1;
                m_NameLabel.text = "";
                m_StartButton.SetEnabled(false);
                return;
            }

            m_Selected = Mathf.Clamp(index, 0, m_Entries.Count - 1);
            m_NameLabel.text = m_Catalog != null ? m_Catalog.DisplayName(m_Entries[m_Selected]) : m_Entries[m_Selected].id;
            m_StatusLabel.text = "";
            RefreshCells();
            ScrollToSelected();
        }

        private void RefreshCells()
        {
            bool gridFocused = m_Grid.focusController != null && m_Grid.focusController.focusedElement == m_Grid;
            for (int i = 0; i < m_Cells.Count; i++)
            {
                bool selected = i == m_Selected;
                m_Cells[i].style.opacity = selected ? 1f : 0.45f;
                SetBorderColor(m_Cells[i], selected ? (gridFocused ? SelectedBorder : SelectedBorderUnfocused) : Color.clear);
            }
        }

        /// <summary>
        /// Scrolls up or down just enough to show the selected row (with its section header if it's a section's
        /// first row). Vertical only - ScrollView.ScrollTo can also slide sideways and clip the list.
        /// </summary>
        private void ScrollToSelected()
        {
            if (m_Selected < 0 || !IsOpen) return;
            int row = m_RowOf[m_Selected];
            VisualElement top = m_RowTops[row];
            VisualElement cell = m_Cells[m_Selected];
            VisualElement rowElement = cell.parent;

            if (float.IsNaN(rowElement.layout.height) || rowElement.layout.height <= 0f || float.IsNaN(m_Scroll.contentViewport.layout.height))
            {
                // Not laid out yet (just opened): try again once it is.
                m_Scroll.schedule.Execute(ScrollToSelected).ExecuteLater(16);
                return;
            }

            float rowTop = top.layout.y - (top == rowElement ? 0f : 2f);
            float rowBottom = rowElement.layout.y + rowElement.layout.height;
            float view = m_Scroll.contentViewport.layout.height;
            Vector2 offset = m_Scroll.scrollOffset;
            if (rowTop < offset.y) offset.y = rowTop;
            else if (rowBottom > offset.y + view) offset.y = rowBottom - view;
            offset.x = 0f;
            m_Scroll.scrollOffset = offset;
        }

        private static void SetBorder(VisualElement element, float width, Color color)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
            SetBorderColor(element, color);
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }
    }
}
