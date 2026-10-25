using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Practice modes only: a list of every item (looks and works like the Practice Challenges list, with the
    /// item pictures as the thumbnails) that puts the chosen one straight into the player's item slot, ready to
    /// use - for testing items without hunting for item boxes. CoreHUD adds it at runtime in practice.
    ///
    /// Opened (and closed again) with the Open Item Menu (Practice Mode) key - 5 / Select by default, the old
    /// Open Wagers Menu key. While it's open, gameplay input is blocked and the cursor is free, like the Esc menu.
    ///
    /// Controls: arrows / stick move the selection, down from the last row goes to Give Item; A / Enter or a
    /// double-click gives the selected item and closes the list; a click selects; B / Esc or Back closes it.
    /// The last item given is selected next time.
    /// </summary>
    public class PracticeItemMenu : MonoBehaviour
    {
        private const int Columns = 6;
        private const float CellSize = 96f;
        private const float CellMargin = 4f;
        private const float CellSlot = CellSize + CellMargin * 2f;
        private const float VisibleRows = 3.3f;
        private const float ScrollBarWidth = 20f;

        private static readonly Color SelectedBorder = new Color(1f, 0.85f, 0.2f);
        private static readonly Color SelectedBorderUnfocused = new Color(0.75f, 0.75f, 0.75f);

        private static ItemId s_LastGiven = ItemId.None;

        /// <summary>True while the list is showing.</summary>
        public static bool IsOpen { get; private set; }

        private readonly List<ItemDefinition> m_Items = new List<ItemDefinition>();
        private readonly List<VisualElement> m_Cells = new List<VisualElement>();

        private VisualElement m_Overlay;
        private VisualElement m_Grid;
        private ScrollView m_Scroll;
        private Label m_NameLabel;
        private Label m_DescriptionLabel;
        private Button m_GiveButton;
        private Button m_BackButton;
        private int m_Selected;
        private float m_OpenedTime;
        private InputBindingsData m_Bindings;

        public void Initialize(VisualElement root)
        {
            GamepadUIBindingFix.Apply(); // A = confirm / B = back, like every menu
            m_Bindings = InputBindingsStore.Load();
            InputBindingsStore.Saved += ReloadBindings;

            foreach (ItemDefinition item in ItemCatalog.Items)
            {
                if (item != null && item.id != ItemId.None) m_Items.Add(item);
            }
            m_Items.Sort((a, b) => ((int)a.id).CompareTo((int)b.id));

            Build(root);
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
            if (IsOpen)
            {
                IsOpen = false;
                CoreInputHandler.SetGameplayInputBlocked(false);
                InGameMenu.Suppress(false);
            }
            m_Overlay?.RemoveFromHierarchy();
        }

        private void Update()
        {
            if (m_Bindings == null) return;
            if (!BoundControls.WasPressed(m_Bindings.Keyboard.OpenItemMenu, m_Bindings.Gamepad.OpenItemMenu)) return;

            if (IsOpen) Close();
            else if (!InGameMenu.IsOpen && !SettingsOverlay.IsOpen) Open();
        }

        // =====================================================================================
        // Layout (same look as ChallengePicker)
        // =====================================================================================

        private void Build(VisualElement root)
        {
            m_Overlay = new VisualElement { name = "practice-item-menu" };
            m_Overlay.style.position = Position.Absolute;
            m_Overlay.style.left = 0;
            m_Overlay.style.right = 0;
            m_Overlay.style.top = 0;
            m_Overlay.style.bottom = 0;
            m_Overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.8f);
            m_Overlay.style.justifyContent = Justify.Center;
            m_Overlay.style.alignItems = Align.Center;
            m_Overlay.style.display = DisplayStyle.None;
            root.Add(m_Overlay);

            // B / Esc closes it.
            m_Overlay.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                evt.StopPropagation();
                if (Time.unscaledTime - m_OpenedTime < 0.2f) return;
                Close();
            });

            var panel = new VisualElement();
            panel.style.backgroundColor = new Color(0.09f, 0.09f, 0.09f);
            ItemTile.SetBorder(panel, 2f, new Color(0.35f, 0.35f, 0.35f));
            ItemTile.SetRadius(panel, 8);
            panel.style.paddingLeft = 24;
            panel.style.paddingRight = 24;
            panel.style.paddingTop = 18;
            panel.style.paddingBottom = 18;
            panel.style.alignItems = Align.Center;
            m_Overlay.Add(panel);

            var title = new Label(Loc.T("PRACTICE ITEMS"));
            title.style.fontSize = 26;
            title.style.color = Color.white;
            title.style.marginBottom = 4;
            panel.Add(title);

            var subtitle = new Label(Loc.T("Pick an item to put it straight into your item slot."));
            subtitle.style.fontSize = 14;
            subtitle.style.color = new Color(0.7f, 0.7f, 0.7f);
            subtitle.style.marginBottom = 10;
            panel.Add(subtitle);

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
            m_Scroll.verticalScroller.Query<VisualElement>().ForEach(e => e.focusable = false);
            m_Scroll.horizontalScroller.Query<VisualElement>().ForEach(e => e.focusable = false);
            m_Grid.Add(m_Scroll);

            VisualElement row = null;
            for (int i = 0; i < m_Items.Count; i++)
            {
                if (i % Columns == 0)
                {
                    row = new VisualElement();
                    row.style.flexDirection = FlexDirection.Row;
                    row.style.flexShrink = 0;
                    m_Scroll.Add(row);
                }
                row.Add(MakeCell(i, m_Items[i]));
            }

            m_NameLabel = new Label("");
            m_NameLabel.style.fontSize = 22;
            m_NameLabel.style.color = Color.white;
            m_NameLabel.style.marginTop = 10;
            panel.Add(m_NameLabel);

            m_DescriptionLabel = new Label("");
            m_DescriptionLabel.style.fontSize = 15;
            m_DescriptionLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_DescriptionLabel.style.minHeight = 22;
            m_DescriptionLabel.style.maxWidth = Columns * CellSlot;
            m_DescriptionLabel.style.whiteSpace = WhiteSpace.Normal;
            m_DescriptionLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(m_DescriptionLabel);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.marginTop = 4;
            panel.Add(buttons);

            m_GiveButton = MenuUI.MakeButton(Loc.T("Give Item"), GiveSelected, null, 180f);
            m_GiveButton.style.marginRight = 12;
            buttons.Add(m_GiveButton);
            m_BackButton = MenuUI.MakeButton(Loc.T("Back"), Close, null, 180f);
            buttons.Add(m_BackButton);

            m_Grid.RegisterCallback<NavigationMoveEvent>(OnGridMove, TrickleDown.TrickleDown);
            m_Grid.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.StopPropagation();
                GiveSelected();
            }, TrickleDown.TrickleDown);
            m_Grid.RegisterCallback<FocusInEvent>(_ => RefreshCells());
            m_Grid.RegisterCallback<FocusOutEvent>(_ => RefreshCells());

            foreach (Button b in new[] { m_GiveButton, m_BackButton })
            {
                b.RegisterCallback<NavigationMoveEvent>(evt =>
                {
                    if (evt.direction != NavigationMoveEvent.Direction.Up) return;
                    evt.StopPropagation();
                    m_Grid.Focus();
                }, TrickleDown.TrickleDown);
            }

            GameFont.Apply(m_Overlay);
        }

        private VisualElement MakeCell(int index, ItemDefinition item)
        {
            var cell = new VisualElement();
            cell.style.width = CellSize;
            cell.style.height = CellSize;
            cell.style.flexShrink = 0;
            cell.style.marginLeft = CellMargin;
            cell.style.marginRight = CellMargin;
            cell.style.marginTop = CellMargin;
            cell.style.marginBottom = CellMargin;
            cell.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            ItemTile.SetBorder(cell, 3f, Color.clear);

            // The item's picture (or its name on a coloured tile, if it has no picture yet).
            VisualElement tile = ItemTile.Make(13);
            tile.style.borderTopWidth = tile.style.borderBottomWidth = tile.style.borderLeftWidth = tile.style.borderRightWidth = 0;
            ItemTile.Show(tile, item);
            ItemTile.SetBorder(tile, 0f, Color.clear);
            cell.Add(tile);

            // "30s" or "1x" in the corner - timed or single use.
            var corner = new Label(item.IsTimed ? $"{Mathf.RoundToInt(item.durationSeconds)}s" : "1x");
            corner.style.position = Position.Absolute;
            corner.style.left = 0;
            corner.style.bottom = 0;
            corner.style.fontSize = 13;
            corner.style.color = Color.white;
            corner.style.backgroundColor = new Color(0f, 0f, 0f, 0.65f);
            corner.style.paddingLeft = 4;
            corner.style.paddingRight = 4;
            corner.pickingMode = PickingMode.Ignore;
            cell.Add(corner);

            cell.RegisterCallback<ClickEvent>(evt =>
            {
                if (index != m_Selected) Select(index);
                if (evt.clickCount >= 2) GiveSelected();
            });

            m_Cells.Add(cell);
            return cell;
        }

        // =====================================================================================
        // Open / close / give
        // =====================================================================================

        private void Open()
        {
            if (m_Items.Count == 0 || LocalItemSlot.Local == null) return;

            IsOpen = true;
            m_OpenedTime = Time.unscaledTime;
            InGameMenu.Suppress(true); // Esc / Start back out of this list instead of opening the Esc menu
            CoreInputHandler.SetGameplayInputBlocked(true);
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            m_Overlay.style.display = DisplayStyle.Flex;
            int index = m_Items.FindIndex(i => i.id == s_LastGiven);
            Select(Mathf.Max(0, index));
            m_Grid.Focus();
        }

        private void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            m_Overlay.style.display = DisplayStyle.None;
            m_Overlay.panel?.focusController?.focusedElement?.Blur();
            CoreInputHandler.SetGameplayInputBlocked(false);
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;

            // A frame later, so the Esc / Start that closed this doesn't also open the Esc menu.
            StartCoroutine(UnsuppressNextFrame());
        }

        private IEnumerator UnsuppressNextFrame()
        {
            yield return null;
            if (!IsOpen) InGameMenu.Suppress(false);
        }

        private void GiveSelected()
        {
            if (m_Selected < 0 || m_Selected >= m_Items.Count) return;
            ItemDefinition item = m_Items[m_Selected];
            LocalItemSlot slot = LocalItemSlot.Local;
            if (slot == null) return;

            s_LastGiven = item.id;
            slot.GiveNow(item.id);
            Close();
        }

        // =====================================================================================
        // Selection
        // =====================================================================================

        private void OnGridMove(NavigationMoveEvent evt)
        {
            evt.StopPropagation();
            evt.PreventDefault();
            if (m_Items.Count == 0) return;

            int rows = (m_Items.Count + Columns - 1) / Columns;
            int row = m_Selected / Columns;
            int col = m_Selected % Columns;

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Left: col = Mathf.Max(0, col - 1); break;
                case NavigationMoveEvent.Direction.Right: col = col + 1; break;
                case NavigationMoveEvent.Direction.Up: row = Mathf.Max(0, row - 1); break;
                case NavigationMoveEvent.Direction.Down:
                    if (row >= rows - 1)
                    {
                        MenuUI.FocusFirst(m_GiveButton);
                        return;
                    }
                    row++;
                    break;
                default: return;
            }

            int rowStart = row * Columns;
            int rowEnd = Mathf.Min(m_Items.Count - 1, rowStart + Columns - 1);
            int next = Mathf.Clamp(rowStart + col, rowStart, rowEnd);
            if (next != m_Selected) Select(next);
        }

        private void Select(int index)
        {
            m_Selected = Mathf.Clamp(index, 0, m_Items.Count - 1);
            ItemDefinition item = m_Items[m_Selected];
            m_NameLabel.text = item.displayName;
            string timing = item.IsTimed ? Loc.F("Lasts {0} s", Mathf.RoundToInt(item.durationSeconds)) : Loc.T("Single use");
            m_DescriptionLabel.text = $"{item.description} ({timing})";
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
                Color c = selected ? (gridFocused ? SelectedBorder : SelectedBorderUnfocused) : Color.clear;
                m_Cells[i].style.borderTopColor = m_Cells[i].style.borderBottomColor = m_Cells[i].style.borderLeftColor = m_Cells[i].style.borderRightColor = c;
            }
        }

        private void ScrollToSelected()
        {
            if (!IsOpen || m_Selected < 0) return;
            VisualElement rowElement = m_Cells[m_Selected].parent;
            if (float.IsNaN(rowElement.layout.height) || rowElement.layout.height <= 0f || float.IsNaN(m_Scroll.contentViewport.layout.height))
            {
                m_Scroll.schedule.Execute(ScrollToSelected).ExecuteLater(16);
                return;
            }

            float rowTop = rowElement.layout.y;
            float rowBottom = rowTop + rowElement.layout.height;
            float view = m_Scroll.contentViewport.layout.height;
            Vector2 offset = m_Scroll.scrollOffset;
            if (rowTop < offset.y) offset.y = rowTop;
            else if (rowBottom > offset.y + view) offset.y = rowBottom - view;
            offset.x = 0f;
            m_Scroll.scrollOffset = offset;
        }
    }
}
