using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Change Profile screen, reached from the Single Player menu. Edits this player's PlayerProfile:
    ///   - DISPLAY NAME: text box, up to NameFilter.MaxLength (24) characters, default "Parkour Player".
    ///     Shown to everyone in the lobby and in game ("Player 1: name"). Checked against the bad-word
    ///     list (NameFilter) when saving.
    ///   - SHOW DISPLAY NAMES: checkbox, on by default. Off shows other players only as "Player 1",
    ///     "Player 2", ... on this machine.
    ///   - CHARACTER: a scrolling box of T-pose thumbnails, 3 per row and 2.5 rows tall, one per character in
    ///     the hand-edited CharacterCatalog list (Resources/CharacterCatalog), in its order. The
    ///     selected (or hovered) character's name is shown to the right, with a double-size picture under
    ///     it. That's the model this player uses in games.
    /// Nothing is saved until Save Changes; Back (below it in the bottom-right column, like the other
    /// screens, or B/Esc) leaves without saving.
    ///
    /// Controls follow the Host Game screen's conventions: explicit row navigation (Up/Down between rows,
    /// down through Save Changes to Back at the bottom), A toggles the checkbox, and the character grid uses edit
    /// mode - A to start choosing, the stick/d-pad to move, A to keep the choice or B to undo it. The mouse
    /// can click straight on a thumbnail. Typing a name needs a keyboard (same as room codes); Left/Right in
    /// the text box move the caret. Composite controls handle Submit/Cancel/Move in the capture phase
    /// (TrickleDown) as per-element closures.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class ChangeProfileController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back (or B/Esc) is pressed.")]
        [SerializeField] private string backSceneName = "SinglePlayer";

        [Tooltip("Played when toggling, choosing a character, or saving.")]
        [SerializeField] private AudioClip selectSound;

        [SerializeField] private AudioClip cancelSound;

        private const int CharacterColumns = 3;
        private const float VisibleRows = 2.5f;   // the grid box shows 2.5 rows; the rest scrolls
        private const float LabelWidth = 240f;
        private const float ThumbnailSize = 96f;  // list cells; the preview on the right is twice this
        private const float CellSlot = ThumbnailSize + 12f; // a cell plus its 3px margins and 3px borders
        private const float PreviewSize = ThumbnailSize * 2f;
        private const float RowSpacing = 14f;
        private const float BottomButtonMargin = 40f;
        private const float BottomButtonHeight = 40f;
        private const float ButtonSlot = BottomButtonHeight + 16f; // a button plus its 8px top/bottom margins

        // Same colors as the other GameFlow screens.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color EditingBackground = new Color(0.85f, 0.55f, 0.15f);
        private static readonly Color SelectedBorder = new Color(1f, 0.85f, 0.2f);
        private static readonly Color ErrorText = new Color(1f, 0.45f, 0.4f);
        private static readonly Color StatusText = new Color(0.8f, 0.8f, 0.8f);
        private static readonly Color HintText = new Color(0.6f, 0.6f, 0.6f);

        private CharacterCatalog m_Catalog;
        private IReadOnlyList<CharacterCatalog.Entry> m_Characters = new List<CharacterCatalog.Entry>();

        private VisualElement m_Column;
        private TextField m_NameField;
        private Toggle m_ShowNamesToggle;
        private VisualElement m_Grid;
        private ScrollView m_GridScroll;
        private readonly List<Image> m_Cells = new List<Image>();
        private Label m_CharacterNameLabel;
        private Image m_CharacterPreview;
        private Label m_StatusLabel;
        private Button m_BackButton;
        private Button m_SaveButton;

        private int m_SelectedIndex = -1;
        private int m_HoveredIndex = -1;

        /// <summary>True while the character grid is in edit mode; m_IndexBeforeEdit is restored on Cancel.</summary>
        private bool m_EditingGrid;
        private int m_IndexBeforeEdit;

        /// <summary>Explicit navigation: rows top to bottom, elements left to right.</summary>
        private readonly List<VisualElement[]> m_NavRows = new List<VisualElement[]>();
        private readonly Dictionary<VisualElement, (int Row, int Col)> m_NavPositions = new Dictionary<VisualElement, (int Row, int Col)>();

        private int CharacterCount => m_Characters.Count;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            m_Catalog = CharacterCatalog.Load();
            if (m_Catalog != null) m_Characters = m_Catalog.GetSelectable();

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(root);
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void BuildUI(VisualElement root)
        {
            MenuUI.SetupRoot(root);
            MenuUI.AddSmallLogo(root, titleImage);

            // B/Esc = Back, unless something focused consumed it (the grid does while editing).
            root.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                if (m_EditingGrid) return;
                evt.StopPropagation();
                MenuUI.PlaySound(cancelSound);
                OnBack();
            });

            m_Column = new VisualElement();
            m_Column.style.position = Position.Absolute;
            m_Column.style.top = Length.Percent(27);
            m_Column.style.left = Length.Percent(50);
            m_Column.style.translate = new Translate(Length.Percent(-50), 0);
            m_Column.style.flexDirection = FlexDirection.Column;
            root.Add(m_Column);

            BuildNameRow();
            BuildShowNamesRow();
            BuildCharacterRow();

            // Save Changes with Back below it, in the bottom-right column like the other screens. Never scaled.
            VisualElement buttons = MenuUI.AddBottomRightColumn(root);

            // No click sound here - OnSave plays select or cancel depending on whether it worked.
            m_SaveButton = MakeButton("Save Changes", OnSave, null);
            buttons.Add(m_SaveButton);

            m_BackButton = MakeButton("Back", OnBack, cancelSound);
            buttons.Add(m_BackButton);

            // Same 8px spacing as the other screens' stacked buttons (MakeButton zeroes the margins).
            foreach (Button b in new[] { m_SaveButton, m_BackButton })
            {
                b.style.marginTop = 8;
                b.style.marginBottom = 8;
            }

            AddNavRow(m_SaveButton);
            AddNavRow(m_BackButton);

            // Status messages sit to the left of Save Changes.
            m_StatusLabel = new Label("");
            m_StatusLabel.style.position = Position.Absolute;
            m_StatusLabel.style.right = 260;
            m_StatusLabel.style.bottom = BottomButtonMargin + ButtonSlot + 18;
            m_StatusLabel.style.fontSize = 16;
            m_StatusLabel.style.color = StatusText;
            root.Add(m_StatusLabel);

            m_NameField.Focus();
        }

        private VisualElement MakeRow(string label)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = RowSpacing;

            var nameLabel = new Label(label);
            nameLabel.style.color = Color.white;
            nameLabel.style.fontSize = 16;
            nameLabel.style.width = LabelWidth;
            nameLabel.style.flexShrink = 0;
            row.Add(nameLabel);

            m_Column.Add(row);
            return row;
        }

        private static Label MakeHint(string text)
        {
            var hint = new Label(text);
            hint.style.color = HintText;
            hint.style.fontSize = 14;
            hint.style.marginLeft = 16;
            return hint;
        }

        private void BuildNameRow()
        {
            VisualElement row = MakeRow("DISPLAY NAME");

            m_NameField = new TextField { maxLength = NameFilter.MaxLength, value = PlayerProfile.DisplayName };
            m_NameField.style.width = 360;
            m_NameField.style.fontSize = 20;
            m_NameField.style.flexShrink = 0;
            row.Add(m_NameField);
            row.Add(MakeHint($"Up to {NameFilter.MaxLength} characters"));

            m_NameField.RegisterValueChangedCallback(_ => ClearStatus());

            // Up/Down leave the text box; Left/Right stay inside it for the caret (that comes from key
            // handling, not this event, so preventing the default navigation doesn't affect it).
            m_NameField.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                evt.PreventDefault();
                if (evt.direction == NavigationMoveEvent.Direction.Up || evt.direction == NavigationMoveEvent.Direction.Down)
                {
                    evt.StopPropagation();
                    MoveFocus(m_NameField, evt.direction);
                }
            }, TrickleDown.TrickleDown);

            // Enter in the text box = Save Changes.
            m_NameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.StopPropagation();
                    OnSave();
                }
            }, TrickleDown.TrickleDown);

            AddNavRow(m_NameField);
        }

        private void BuildShowNamesRow()
        {
            VisualElement row = MakeRow("SHOW DISPLAY NAMES");

            m_ShowNamesToggle = new Toggle { value = PlayerProfile.ShowDisplayNames };
            m_ShowNamesToggle.style.flexShrink = 0;
            m_ShowNamesToggle.style.paddingLeft = 4;
            m_ShowNamesToggle.style.paddingRight = 4;
            m_ShowNamesToggle.style.paddingTop = 2;
            m_ShowNamesToggle.style.paddingBottom = 2;
            row.Add(m_ShowNamesToggle);
            row.Add(MakeHint("Off shows other players as Player 1, Player 2, ..."));

            m_ShowNamesToggle.RegisterValueChangedCallback(_ => ClearStatus());

            SetToggleHighlight(false);
            m_ShowNamesToggle.RegisterCallback<FocusInEvent>(_ => SetToggleHighlight(true));
            m_ShowNamesToggle.RegisterCallback<FocusOutEvent>(_ => SetToggleHighlight(false));

            // A toggles it - handled here (and the Toggle's own default prevented) so it can't double-toggle.
            m_ShowNamesToggle.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.PreventDefault();
                evt.StopPropagation();
                m_ShowNamesToggle.value = !m_ShowNamesToggle.value;
                MenuUI.PlaySound(selectSound);
            }, TrickleDown.TrickleDown);
            m_ShowNamesToggle.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                Suppress(evt);
                MoveFocus(m_ShowNamesToggle, evt.direction);
            }, TrickleDown.TrickleDown);

            AddNavRow(m_ShowNamesToggle);
        }

        private void BuildCharacterRow()
        {
            VisualElement row = MakeRow("CHARACTER");
            row.style.alignItems = Align.FlexStart;

            // A fixed-size box, CharacterColumns cells wide and VisibleRows tall, with a scroll bar for the
            // rest (mouse wheel / drag the bar; with a gamepad the list scrolls to follow the selection).
            // m_Grid is the focusable frame (it carries the focus/edit highlight); the ScrollView inside
            // holds explicit rows of CharacterColumns cells (not one flex-wrapping container, whose wrapping
            // depends on exact rendered widths - see HostGameController's map grid).
            m_Grid = new VisualElement { focusable = true };
            m_Grid.style.flexDirection = FlexDirection.Column;
            m_Grid.style.flexShrink = 0;
            m_Grid.style.paddingLeft = 4;
            m_Grid.style.paddingRight = 4;
            m_Grid.style.paddingTop = 4;
            m_Grid.style.paddingBottom = 4;
            row.Add(m_Grid);

            m_GridScroll = new ScrollView(ScrollViewMode.Vertical)
            {
                focusable = false,
                horizontalScrollerVisibility = ScrollerVisibility.Hidden,
                verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible,
                touchScrollBehavior = ScrollView.TouchScrollBehavior.Clamped,
            };
            m_GridScroll.style.width = CharacterColumns * CellSlot + 20f; // + the vertical scroll bar
            m_GridScroll.style.height = VisibleRows * CellSlot;
            m_GridScroll.style.flexShrink = 0;
            m_GridScroll.mouseWheelScrollSize = CellSlot / 2f;
            // The scroll bar is for the mouse only - keep gamepad/keyboard focus on the grid itself.
            m_GridScroll.verticalScroller.Query<VisualElement>().ForEach(e => e.focusable = false);
            m_Grid.Add(m_GridScroll);

            VisualElement gridRow = null;
            for (int i = 0; i < CharacterCount; i++)
            {
                if (i % CharacterColumns == 0)
                {
                    gridRow = new VisualElement();
                    gridRow.style.flexDirection = FlexDirection.Row;
                    gridRow.style.flexShrink = 0;
                    m_GridScroll.Add(gridRow);
                }

                int index = i; // captured per cell
                var cell = new Image { image = m_Characters[i].thumbnail, scaleMode = ScaleMode.ScaleToFit };
                cell.style.width = ThumbnailSize;
                cell.style.height = ThumbnailSize;
                cell.style.flexShrink = 0;
                cell.style.marginLeft = 3;
                cell.style.marginRight = 3;
                cell.style.marginTop = 3;
                cell.style.marginBottom = 3;
                cell.style.borderTopWidth = 3;
                cell.style.borderBottomWidth = 3;
                cell.style.borderLeftWidth = 3;
                cell.style.borderRightWidth = 3;
                cell.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);

                cell.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    m_HoveredIndex = index;
                    RefreshCharacterName();
                });
                cell.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    if (m_HoveredIndex == index) m_HoveredIndex = -1;
                    RefreshCharacterName();
                });
                cell.RegisterCallback<ClickEvent>(_ => OnCellClicked(index));

                gridRow.Add(cell);
                m_Cells.Add(cell);
            }

            // Right of the grid: the hovered (or selected) character's name, with a big picture of them under it.
            var info = new VisualElement();
            info.style.flexDirection = FlexDirection.Column;
            info.style.marginLeft = 20;
            info.style.flexShrink = 0;
            row.Add(info);

            m_CharacterNameLabel = new Label("");
            m_CharacterNameLabel.style.color = Color.white;
            m_CharacterNameLabel.style.fontSize = 18;
            m_CharacterNameLabel.style.marginTop = 8;
            m_CharacterNameLabel.style.minWidth = 220;
            info.Add(m_CharacterNameLabel);

            m_CharacterPreview = new Image { scaleMode = ScaleMode.ScaleToFit };
            m_CharacterPreview.style.width = PreviewSize;
            m_CharacterPreview.style.height = PreviewSize;
            m_CharacterPreview.style.marginTop = 10;
            m_CharacterPreview.style.flexShrink = 0;
            m_CharacterPreview.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
            info.Add(m_CharacterPreview);

            int saved = IndexOfCharacter(PlayerProfile.CharacterId);
            SetSelected(saved >= 0 ? saved : 0);

            SetGridHighlight(false);
            m_Grid.RegisterCallback<FocusInEvent>(_ => { if (!m_EditingGrid) SetGridHighlight(true); });
            m_Grid.RegisterCallback<FocusOutEvent>(_ =>
            {
                // Focus moved away (mouse) while choosing: keep the choice.
                if (m_EditingGrid) ExitGridEdit(commit: true, playSound: false);
                SetGridHighlight(false);
            });

            m_Grid.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.PreventDefault();
                evt.StopPropagation();
                if (CharacterCount == 0) return;
                if (m_EditingGrid) ExitGridEdit(commit: true, playSound: true);
                else EnterGridEdit();
            }, TrickleDown.TrickleDown);

            m_Grid.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                if (!m_EditingGrid) return;
                evt.PreventDefault();
                evt.StopPropagation();
                ExitGridEdit(commit: false, playSound: true);
            }, TrickleDown.TrickleDown);

            m_Grid.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                Suppress(evt);
                if (m_EditingGrid) MoveSelection(evt.direction);
                else MoveFocus(m_Grid, evt.direction);
            }, TrickleDown.TrickleDown);

            AddNavRow(m_Grid);
        }

        // =====================================================================================
        // Character grid
        // =====================================================================================

        private void SetSelected(int index)
        {
            if (CharacterCount == 0)
            {
                m_SelectedIndex = -1;
                m_CharacterNameLabel.text = "No characters found";
                return;
            }

            m_SelectedIndex = Mathf.Clamp(index, 0, CharacterCount - 1);
            for (int i = 0; i < m_Cells.Count; i++)
            {
                bool selected = i == m_SelectedIndex;
                Image cell = m_Cells[i];
                cell.style.opacity = selected ? 1f : 0.35f;
                Color border = selected ? SelectedBorder : Color.clear;
                cell.style.borderTopColor = border;
                cell.style.borderBottomColor = border;
                cell.style.borderLeftColor = border;
                cell.style.borderRightColor = border;
            }
            RefreshCharacterName();
            ScrollToCell(m_SelectedIndex);
        }

        /// <summary>Scrolls the grid so the given cell is in view (it may not be laid out on the first frame yet).</summary>
        private void ScrollToCell(int index)
        {
            if (m_GridScroll == null || index < 0 || index >= m_Cells.Count) return;
            Image cell = m_Cells[index];
            if (float.IsNaN(cell.layout.height) || cell.layout.height <= 0f)
            {
                // Not laid out yet (the screen is still being built): scroll once it is.
                EventCallback<GeometryChangedEvent> onLayout = null;
                onLayout = _ =>
                {
                    cell.UnregisterCallback(onLayout);
                    if (m_Cells.IndexOf(cell) == m_SelectedIndex) m_GridScroll.ScrollTo(cell);
                };
                cell.RegisterCallback(onLayout);
                return;
            }
            m_GridScroll.ScrollTo(cell);
        }

        /// <summary>The hovered character's name, or the selected one's when nothing is hovered.</summary>
        private void RefreshCharacterName()
        {
            if (CharacterCount == 0) return;
            int index = m_HoveredIndex >= 0 ? m_HoveredIndex : m_SelectedIndex;
            CharacterCatalog.Entry entry = m_Characters[index];
            m_CharacterNameLabel.text = entry.DisplayNameOrId;
            m_CharacterPreview.image = entry.thumbnail;
        }

        private void MoveSelection(NavigationMoveEvent.Direction direction)
        {
            if (CharacterCount == 0) return;

            int row = m_SelectedIndex / CharacterColumns;
            int col = m_SelectedIndex % CharacterColumns;
            int lastRow = (CharacterCount - 1) / CharacterColumns;

            switch (direction)
            {
                case NavigationMoveEvent.Direction.Left: col = Mathf.Max(0, col - 1); break;
                case NavigationMoveEvent.Direction.Right: col = Mathf.Min(CharacterColumns - 1, col + 1); break;
                case NavigationMoveEvent.Direction.Up: row = Mathf.Max(0, row - 1); break;
                case NavigationMoveEvent.Direction.Down: row = Mathf.Min(lastRow, row + 1); break;
                default: return;
            }

            int next = Mathf.Min(row * CharacterColumns + col, CharacterCount - 1);
            if (next != m_SelectedIndex)
            {
                SetSelected(next);
                ClearStatus();
            }
        }

        private void OnCellClicked(int index)
        {
            SetSelected(index);
            ClearStatus();
            m_Grid.Focus();
            if (m_EditingGrid) ExitGridEdit(commit: true, playSound: true);
            else MenuUI.PlaySound(selectSound);
        }

        private void EnterGridEdit()
        {
            m_EditingGrid = true;
            m_IndexBeforeEdit = m_SelectedIndex;
            m_Grid.style.backgroundColor = EditingBackground;
            MenuUI.PlaySound(selectSound);
        }

        private void ExitGridEdit(bool commit, bool playSound)
        {
            if (!m_EditingGrid) return;
            m_EditingGrid = false;
            SetGridHighlight(m_Grid.focusController?.focusedElement == m_Grid);

            if (!commit) SetSelected(m_IndexBeforeEdit);
            if (playSound) MenuUI.PlaySound(commit ? selectSound : cancelSound);
        }

        // =====================================================================================
        // Navigation / visuals
        // =====================================================================================

        private void AddNavRow(params VisualElement[] elements)
        {
            int row = m_NavRows.Count;
            m_NavRows.Add(elements);
            for (int col = 0; col < elements.Length; col++)
            {
                m_NavPositions[elements[col]] = (row, col);
            }
        }

        private void MoveFocus(VisualElement from, NavigationMoveEvent.Direction direction)
        {
            if (!m_NavPositions.TryGetValue(from, out (int Row, int Col) pos)) return;

            int row = pos.Row;
            int col = pos.Col;
            switch (direction)
            {
                case NavigationMoveEvent.Direction.Up: row--; break;
                case NavigationMoveEvent.Direction.Down: row++; break;
                case NavigationMoveEvent.Direction.Left: col--; break;
                case NavigationMoveEvent.Direction.Right: col++; break;
                default: return;
            }

            if (row < 0 || row >= m_NavRows.Count) return;
            VisualElement[] elements = m_NavRows[row];

            if (row != pos.Row)
            {
                // Every row has a single element now (the buttons are stacked), so moving up/down lands on it.
                col = elements.Length - 1;
            }
            else if (col < 0 || col >= elements.Length)
            {
                return;
            }

            elements[col].Focus();
        }

        private static void Suppress(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void SetGridHighlight(bool focused)
        {
            m_Grid.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
        }

        private void SetToggleHighlight(bool focused)
        {
            m_ShowNamesToggle.style.backgroundColor = focused ? FocusedBackground : Color.clear;
        }

        private Button MakeButton(string text, System.Action onClick, AudioClip clickSound)
        {
            Button button = MenuUI.MakeButton(text, () =>
            {
                if (m_EditingGrid) return;
                MenuUI.PlaySound(clickSound);
                onClick();
            }, null);
            button.style.marginTop = 0;
            button.style.marginBottom = 0;
            button.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                Suppress(evt);
                MoveFocus(button, evt.direction);
            });
            return button;
        }

        private void ClearStatus()
        {
            if (m_StatusLabel != null) m_StatusLabel.text = "";
        }

        private void ShowStatus(string text, bool error)
        {
            m_StatusLabel.text = text;
            m_StatusLabel.style.color = error ? ErrorText : StatusText;
        }

        // =====================================================================================
        // Actions
        // =====================================================================================

        private void OnSave()
        {
            string name = NameFilter.Clean(m_NameField.value);
            string problem = NameFilter.Validate(name);
            if (problem != null)
            {
                MenuUI.PlaySound(cancelSound);
                ShowStatus(problem, error: true);
                m_NameField.Focus();
                return;
            }

            string characterId = m_SelectedIndex >= 0 && m_SelectedIndex < CharacterCount
                ? m_Characters[m_SelectedIndex].id
                : PlayerProfile.CharacterId;
            PlayerProfile.Save(name, m_ShowNamesToggle.value, characterId);

            m_NameField.SetValueWithoutNotify(name);
            MenuUI.PlaySound(selectSound);
            ShowStatus("Changes saved.", error: false);
        }

        private int IndexOfCharacter(string id)
        {
            for (int i = 0; i < m_Characters.Count; i++)
            {
                if (m_Characters[i].id == id) return i;
            }
            return -1;
        }

        private void OnBack() => SceneManager.LoadScene(backSceneName);
    }
}
