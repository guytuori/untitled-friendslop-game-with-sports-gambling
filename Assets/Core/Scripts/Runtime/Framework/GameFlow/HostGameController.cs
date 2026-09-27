using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Host Game screen, reached from the main menu's Host Game button: the game-rules filters a host
    /// picks before starting a session. Top to bottom:
    ///   - Presets: Normal / Hard / Very Hard buttons, which set every rule except visibility (see
    ///     HostGamePresets). A fresh screen starts on Normal's values.
    ///   - Visibility: Public Game / Private Game dropdown.
    ///   - Map: a 2x5 grid of map thumbnails (see <see cref="maps"/>), with the hovered/selected map's
    ///     name to the right and its flavor text underneath. Unselected maps are grayed out.
    ///   - Max Players, Round Time, Bonus Time, Lives, Starting Points, Death Penalty, Wager Payout:
    ///     sliders over fixed option lists, each showing its current option to the right.
    ///   - Items and Pickups: On/Off dropdowns.
    ///   - Back (bottom-left, returns to the main menu) and Host Game (bottom-right, placeholder).
    ///     B/Esc while not editing also acts as Back.
    /// The settings column scales down uniformly to fit the screen (e.g. 1280x720) - see
    /// FitColumnToScreen. Back and Host Game are never scaled.
    /// Choices are written straight into <see cref="HostGameRules.Current"/>, which isn't saved to disk -
    /// it just remembers the last setup for as long as the game is running.
    ///
    /// EDIT MODE: same model as AudioSettingsController/GraphicsSettingsController. A focused slider,
    /// dropdown, or the map grid only changes after Submit (A/Enter) puts it into edit mode; Submit again
    /// keeps the change, Cancel (B/Esc) reverts to the value from before editing started. While editing:
    ///   - Sliders: Left/Right move one option (clamped at the ends); Up/Down do nothing.
    ///   - Dropdowns: Up/Down cycle options (wrapping, same as the Graphics screen); Left/Right do nothing.
    ///   - Map grid: all four directions move the selection within the grid (clamped at its edges), and
    ///     nothing can leave the grid until A or B.
    /// The mouse bypasses edit mode entirely, same as the other screens: drag a slider, click a dropdown
    /// to open its list, or click a map thumbnail to select it. Hovering a thumbnail previews its name
    /// and flavor text.
    ///
    /// NAVIGATION (not editing): every focusable element sits in an explicit grid of rows
    /// (<see cref="m_NavRows"/>) - the three presets, then one row per setting, then Back / Host Game.
    /// Up/Down move between rows and Left/Right move within a multi-element row (presets, bottom
    /// buttons); Left/Right on a single-element setting row do nothing. UI Toolkit's own automatic
    /// nearest-neighbor navigation is never used for any direction - same "override it explicitly"
    /// lesson as every other GameFlow screen. Moving down into the bottom row lands on Host Game, and
    /// moving up into the presets row lands on Normal (see <see cref="m_RowEntryColumn"/>).
    ///
    /// Sliders, dropdowns, and the map grid register their Submit/Cancel/Move handlers in the capture
    /// phase (TrickleDown) and as per-element closures, never reading evt.target - the two lessons from
    /// the Audio slider and Graphics dropdown bugs (composite controls' own default actions run before a
    /// bubble-phase handler, and their evt.target can be an internal child element).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HostGameController : MonoBehaviour
    {
        [Serializable]
        public class MapEntry
        {
            public string displayName;
            public Texture2D thumbnail;
            [TextArea] public string flavorText;
        }

        [Tooltip("Maps in grid order, left-to-right then top-to-bottom (5 per row). By convention Last Stop is 9th and Random is 10th, so they land bottom-right.")]
        [SerializeField] private MapEntry[] maps;

        [Tooltip("Scene to load when Back is clicked.")]
        [SerializeField] private string mainMenuSceneName = "MainMenu";

        [Tooltip("Played when entering/confirming an edit, selecting a map, or clicking a preset/Host Game.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Back is selected, or an edit is cancelled.")]
        [SerializeField] private AudioClip cancelSound;

        private const int MapColumns = 5;

        // ----- option lists -----

        private static readonly int[] MaxPlayerOptions = BuildRange(2, 16);
        private static readonly int[] RoundTimeOptions = { 60, 90, 120, 150, 180, 240, 300, 360, 420, 480 };
        private static readonly int[] BonusTimeOptions = { 0, 30, 40, 50, 60, 70, 80, 90, 120, 180 };
        private static readonly int[] LivesOptions = BuildLivesOptions(); // 1-20, then -1 (Unlimited)
        private static readonly int[] StartingPointsOptions = { 0, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1500, 2000 };
        private static readonly int[] DeathPenaltyOptions = { 0, 25, 50, 100, 200, 300, 400, 500, -1 }; // -1 = All
        private static readonly float[] WagerPayoutOptions = { 2.5f, 3f, 3.5f, 4f, 4.5f, 5f };

        private static readonly List<string> VisibilityChoices = new List<string> { "Public Game", "Private Game" };
        private static readonly List<string> ItemsChoices = new List<string> { "Items On", "Items Off" };
        private static readonly List<string> PickupsChoices = new List<string> { "Pickups On", "Pickups Off" };

        private static int[] BuildRange(int min, int max)
        {
            var values = new int[max - min + 1];
            for (int i = 0; i < values.Length; i++) values[i] = min + i;
            return values;
        }

        private static int[] BuildLivesOptions()
        {
            var values = new int[21];
            for (int i = 0; i < 20; i++) values[i] = i + 1;
            values[20] = -1;
            return values;
        }

        private static int IndexOf(int[] options, int value, int fallback)
        {
            int index = Array.IndexOf(options, value);
            return index >= 0 ? index : fallback;
        }

        private static int IndexOf(float[] options, float value, int fallback)
        {
            for (int i = 0; i < options.Length; i++)
            {
                if (Mathf.Approximately(options[i], value)) return i;
            }
            return fallback;
        }

        private static string FormatTime(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
        private static string FormatLives(int lives) => lives < 0 ? "Unlimited" : lives.ToString(CultureInfo.InvariantCulture);
        private static string FormatDeathPenalty(int penalty) => penalty < 0 ? "All" : penalty.ToString(CultureInfo.InvariantCulture);
        private static string FormatPayout(float payout) => payout.ToString("0.#", CultureInfo.InvariantCulture);

        // ----- layout -----

        private const float LabelWidth = 200f;
        private const float ControlWidth = 550f; // ~ the 5-wide map grid's natural width, so value labels line up with the map name
        private const float ValueWidth = 300f;
        private const float RowSpacing = 8f;
        private const float ThumbnailWidth = 96f;
        private const float ThumbnailHeight = 54f;

        // ----- state -----

        private HostGameRulesData m_Rules;

        /// <summary>How a slider, dropdown, or the map grid reads, restores, and adjusts its value in edit mode.</summary>
        private sealed class Editable
        {
            public Func<int> Get;
            public Action<int> Set;
            public Action<NavigationMoveEvent.Direction> Move;
        }

        private readonly Dictionary<VisualElement, Editable> m_Editables = new Dictionary<VisualElement, Editable>();

        /// <summary>The element currently in edit mode, or null.</summary>
        private VisualElement m_Editing;

        /// <summary>m_Editing's value right before editing started, restored on Cancel.</summary>
        private int m_ValueBeforeEdit;

        /// <summary>Explicit navigation grid - see the class summary's NAVIGATION section.</summary>
        private readonly List<VisualElement[]> m_NavRows = new List<VisualElement[]>();

        /// <summary>Which column Up/Down lands on when entering a multi-element row.</summary>
        private readonly List<int> m_RowEntryColumn = new List<int>();

        private readonly Dictionary<VisualElement, (int Row, int Col)> m_NavPositions = new Dictionary<VisualElement, (int Row, int Col)>();

        private VisualElement m_MapArea;
        private readonly List<Image> m_MapCells = new List<Image>();
        private Label m_MapNameLabel;
        private Label m_MapFlavorLabel;
        private int m_SelectedMapIndex;
        private int m_HoveredMapIndex = -1;

        private Button m_NormalPresetButton;

        /// <summary>The scalable settings column - see FitColumnToScreen.</summary>
        private VisualElement m_Column;

        /// <summary>Re-reads m_Rules into each control without firing change callbacks - run after a preset is applied.</summary>
        private readonly List<Action> m_RefreshFromRules = new List<Action>();

        private int MapCount => maps != null ? maps.Length : 0;

        private void Awake()
        {
            // Same gamepad Submit/Cancel fix every GameFlow screen with an EventSystem applies - see
            // GamepadUIBindingFix.
            GamepadUIBindingFix.Apply();

            m_Rules = HostGameRules.Current;

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(root);
        }

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;

            // B (gamepad Cancel) / Esc outside edit mode = Back. Every editable's own Cancel handler runs in
            // the capture phase and stops propagation while it's being edited, so this bubble-phase handler
            // on the root only ever sees a Cancel that nothing else consumed - i.e. one pressed while not
            // editing, on any element (including the buttons, which don't handle Cancel at all).
            root.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                if (m_Editing != null) return;
                evt.StopPropagation();
                if (cancelSound != null)
                {
                    AudioVolumeService.PlayOneShot(cancelSound, AudioCategory.SoundEffects, Vector3.zero);
                }
                OnBackClicked();
            });

            m_Column = new VisualElement();
            m_Column.style.position = Position.Absolute;
            m_Column.style.top = ColumnTop;
            m_Column.style.left = Length.Percent(50);
            m_Column.style.translate = new Translate(Length.Percent(-50), 0);
            m_Column.style.width = LabelWidth + ControlWidth + ValueWidth + 20f;
            m_Column.style.flexDirection = FlexDirection.Column;
            // Scaled from its top-center, so shrinking it keeps it centered and pinned under the top margin.
            m_Column.style.transformOrigin = new TransformOrigin(Length.Percent(50), 0);
            root.Add(m_Column);
            VisualElement column = m_Column;

            // See FitColumnToScreen - fires on the first layout pass and on every resize/resolution change.
            root.RegisterCallback<GeometryChangedEvent>(_ => FitColumnToScreen(root));
            m_Column.RegisterCallback<GeometryChangedEvent>(_ => FitColumnToScreen(root));

            // Presets
            VisualElement presetsRow = MakeRow(column, "PRESETS");
            m_NormalPresetButton = MakeButton("Normal", () => ApplyPreset(HostGamePresets.Normal), selectSound, 160f);
            Button hardButton = MakeButton("Hard", () => ApplyPreset(HostGamePresets.Hard), selectSound, 160f);
            Button veryHardButton = MakeButton("Very Hard", () => ApplyPreset(HostGamePresets.VeryHard), selectSound, 160f);
            hardButton.style.marginLeft = 20;
            veryHardButton.style.marginLeft = 20;
            presetsRow.Add(m_NormalPresetButton);
            presetsRow.Add(hardButton);
            presetsRow.Add(veryHardButton);
            AddNavRow(0, m_NormalPresetButton, hardButton, veryHardButton);

            // Visibility
            AddNavRow(0, AddDropdownRow(column, "VISIBILITY", VisibilityChoices,
                () => m_Rules.IsPublic ? 0 : 1,
                i => m_Rules.IsPublic = i == 0));

            // Map
            BuildMapRows(column);
            AddNavRow(0, m_MapArea);

            // Sliders. The fallback index in each IndexOf is the Normal preset's value, used only if the
            // stored value somehow isn't one of the options.
            AddNavRow(0, AddSliderRow(column, "MAX PLAYERS", MaxPlayerOptions.Length,
                () => IndexOf(MaxPlayerOptions, m_Rules.MaxPlayers, 6),
                i => MaxPlayerOptions[i].ToString(CultureInfo.InvariantCulture),
                i => m_Rules.MaxPlayers = MaxPlayerOptions[i]));

            AddNavRow(0, AddSliderRow(column, "ROUND TIME", RoundTimeOptions.Length,
                () => IndexOf(RoundTimeOptions, m_Rules.RoundTimeSeconds, 4),
                i => FormatTime(RoundTimeOptions[i]),
                i => m_Rules.RoundTimeSeconds = RoundTimeOptions[i]));

            AddNavRow(0, AddSliderRow(column, "BONUS TIME", BonusTimeOptions.Length,
                () => IndexOf(BonusTimeOptions, m_Rules.BonusTimeSeconds, 8),
                i => FormatTime(BonusTimeOptions[i]),
                i => m_Rules.BonusTimeSeconds = BonusTimeOptions[i]));

            AddNavRow(0, AddSliderRow(column, "LIVES", LivesOptions.Length,
                () => IndexOf(LivesOptions, m_Rules.Lives, LivesOptions.Length - 1),
                i => FormatLives(LivesOptions[i]),
                i => m_Rules.Lives = LivesOptions[i]));

            AddNavRow(0, AddSliderRow(column, "STARTING POINTS", StartingPointsOptions.Length,
                () => IndexOf(StartingPointsOptions, m_Rules.StartingPoints, 10),
                i => StartingPointsOptions[i].ToString(CultureInfo.InvariantCulture),
                i => m_Rules.StartingPoints = StartingPointsOptions[i]));

            AddNavRow(0, AddSliderRow(column, "DEATH PENALTY", DeathPenaltyOptions.Length,
                () => IndexOf(DeathPenaltyOptions, m_Rules.DeathPenalty, 2),
                i => FormatDeathPenalty(DeathPenaltyOptions[i]),
                i => m_Rules.DeathPenalty = DeathPenaltyOptions[i]));

            AddNavRow(0, AddSliderRow(column, "WAGER PAYOUT", WagerPayoutOptions.Length,
                () => IndexOf(WagerPayoutOptions, m_Rules.WagerPayout, 1),
                i => FormatPayout(WagerPayoutOptions[i]),
                i => m_Rules.WagerPayout = WagerPayoutOptions[i]));

            // Items / Pickups
            AddNavRow(0, AddDropdownRow(column, "ITEMS", ItemsChoices,
                () => m_Rules.ItemsEnabled ? 0 : 1,
                i => m_Rules.ItemsEnabled = i == 0));
            AddNavRow(0, AddDropdownRow(column, "PICKUPS", PickupsChoices,
                () => m_Rules.PickupsEnabled ? 0 : 1,
                i => m_Rules.PickupsEnabled = i == 0));

            // Back (bottom-left) / Host Game (bottom-right). Not part of m_Column, so they're never scaled.
            Button backButton = MakeButton("Back", OnBackClicked, cancelSound, 200f);
            backButton.style.position = Position.Absolute;
            backButton.style.left = 40;
            backButton.style.bottom = BottomButtonMargin;
            root.Add(backButton);

            Button hostButton = MakeButton("Host Game", OnHostGameClicked, selectSound, 200f);
            hostButton.style.position = Position.Absolute;
            hostButton.style.right = 40;
            hostButton.style.bottom = BottomButtonMargin;
            root.Add(hostButton);

            AddNavRow(1, backButton, hostButton);

            // Nothing is focused by default, and gamepad navigation needs a starting point - start at
            // the top-left element.
            m_NormalPresetButton.Focus();
            SetButtonFocusedVisual(m_NormalPresetButton, true);
        }

        private const float ColumnTop = 30f;
        private const float BottomButtonMargin = 40f;
        private const float BottomButtonHeight = 40f;
        private const float GapAboveBottomButtons = 16f;

        /// <summary>
        /// Uniformly scales the whole settings column (never Back/Host Game) down just enough to fit
        /// between the top margin and the bottom buttons, and within the screen width - so the page fits
        /// at 1280x720 (or any other resolution/panel scale) without per-row layout changes. Never scales
        /// above 1. style.scale is a transform, so it doesn't feed back into layout (no resize loop), and
        /// pointer picking follows it, so the mouse still hits the scaled controls correctly.
        /// </summary>
        private void FitColumnToScreen(VisualElement root)
        {
            float rootHeight = root.layout.height;
            float rootWidth = root.layout.width;
            float columnHeight = m_Column.layout.height;
            float columnWidth = m_Column.layout.width;
            if (float.IsNaN(rootHeight) || float.IsNaN(columnHeight) || columnHeight <= 0f || columnWidth <= 0f) return;

            float availableHeight = rootHeight - ColumnTop - BottomButtonMargin - BottomButtonHeight - GapAboveBottomButtons;
            float availableWidth = rootWidth - 40f;
            float scale = Mathf.Min(1f, availableHeight / columnHeight, availableWidth / columnWidth);
            scale = Mathf.Max(0.1f, scale);

            m_Column.style.scale = new Scale(new Vector2(scale, scale));
        }

        /// <summary>Applies a preset's values to m_Rules and refreshes every control to match (visibility is left alone).</summary>
        private void ApplyPreset(HostGameRulesData preset)
        {
            m_Rules.ApplyPreset(preset);
            foreach (Action refresh in m_RefreshFromRules)
            {
                refresh();
            }
        }

        private VisualElement MakeRow(VisualElement parent, string label)
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

            parent.Add(row);
            return row;
        }

        private static Label MakeValueLabel(string text)
        {
            var label = new Label(text);
            label.style.color = Color.white;
            label.style.fontSize = 16;
            label.style.width = ValueWidth;
            label.style.marginLeft = 20;
            return label;
        }

        // ----- rows -----

        /// <summary>
        /// Builds a label + SliderInt(0..optionCount-1) + value-label row. readIndex maps the current
        /// m_Rules value to a slider index; it's used both for the initial value and whenever a preset is
        /// applied (see m_RefreshFromRules).
        /// </summary>
        private SliderInt AddSliderRow(VisualElement parent, string label, int optionCount, Func<int> readIndex,
            Func<int, string> format, Action<int> onChanged)
        {
            VisualElement row = MakeRow(parent, label);

            int initialIndex = readIndex();
            var slider = new SliderInt { lowValue = 0, highValue = optionCount - 1, value = initialIndex };
            slider.style.width = ControlWidth;
            slider.style.flexShrink = 0;
            row.Add(slider);

            Label valueLabel = MakeValueLabel(format(initialIndex));
            row.Add(valueLabel);

            slider.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;
                valueLabel.text = format(evt.newValue);
                onChanged(evt.newValue);
            });

            m_RefreshFromRules.Add(() =>
            {
                int i = readIndex();
                slider.SetValueWithoutNotify(i);
                valueLabel.text = format(i);
            });

            RegisterEditable(slider, new Editable
            {
                Get = () => slider.value,
                Set = i =>
                {
                    slider.SetValueWithoutNotify(i);
                    valueLabel.text = format(i);
                    onChanged(i);
                },
                // Setting .value fires RegisterValueChangedCallback above, same as a mouse drag would.
                Move = direction =>
                {
                    if (direction == NavigationMoveEvent.Direction.Left)
                        slider.value = Mathf.Max(slider.lowValue, slider.value - 1);
                    else if (direction == NavigationMoveEvent.Direction.Right)
                        slider.value = Mathf.Min(slider.highValue, slider.value + 1);
                }
            });

            return slider;
        }

        private DropdownField AddDropdownRow(VisualElement parent, string label, List<string> choices, Func<int> readIndex,
            Action<int> onChanged)
        {
            VisualElement row = MakeRow(parent, label);

            var dropdown = new DropdownField(new List<string>(choices), readIndex());
            dropdown.style.width = 260;
            dropdown.style.flexShrink = 0;
            row.Add(dropdown);

            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;
                onChanged(dropdown.index);
            });

            m_RefreshFromRules.Add(() => dropdown.SetValueWithoutNotify(dropdown.choices[readIndex()]));

            RegisterEditable(dropdown, new Editable
            {
                Get = () => dropdown.index,
                Set = i =>
                {
                    dropdown.SetValueWithoutNotify(dropdown.choices[i]);
                    onChanged(i);
                },
                // Wraps, same as GraphicsSettingsController's dropdowns. Setting .index fires
                // RegisterValueChangedCallback above.
                Move = direction =>
                {
                    int step = direction == NavigationMoveEvent.Direction.Up ? -1
                             : direction == NavigationMoveEvent.Direction.Down ? 1 : 0;
                    if (step == 0) return;
                    int count = dropdown.choices.Count;
                    dropdown.index = ((dropdown.index + step) % count + count) % count;
                }
            });

            return dropdown;
        }

        private void BuildMapRows(VisualElement parent)
        {
            VisualElement row = MakeRow(parent, "MAP");

            // Explicit row containers of MapColumns cells each, rather than one flex-wrapping container -
            // wrapping depends on the cells' exact rendered widths (theme borders/margins, panel scaling),
            // which is how the first version ended up 4 wide instead of 5.
            m_MapArea = new VisualElement { focusable = true };
            m_MapArea.style.flexDirection = FlexDirection.Column;
            m_MapArea.style.flexShrink = 0;
            m_MapArea.style.paddingLeft = 4;
            m_MapArea.style.paddingRight = 4;
            m_MapArea.style.paddingTop = 4;
            m_MapArea.style.paddingBottom = 4;
            row.Add(m_MapArea);

            VisualElement gridRow = null;
            for (int i = 0; i < MapCount; i++)
            {
                if (i % MapColumns == 0)
                {
                    gridRow = new VisualElement();
                    gridRow.style.flexDirection = FlexDirection.Row;
                    gridRow.style.flexShrink = 0;
                    m_MapArea.Add(gridRow);
                }

                int index = i; // captured per cell
                var cell = new Image { image = maps[i].thumbnail, scaleMode = ScaleMode.ScaleToFit };
                cell.style.width = ThumbnailWidth;
                cell.style.height = ThumbnailHeight;
                cell.style.flexShrink = 0;
                cell.style.marginLeft = 3;
                cell.style.marginRight = 3;
                cell.style.marginTop = 3;
                cell.style.marginBottom = 3;
                cell.style.borderTopWidth = 3;
                cell.style.borderBottomWidth = 3;
                cell.style.borderLeftWidth = 3;
                cell.style.borderRightWidth = 3;

                cell.RegisterCallback<PointerEnterEvent>(_ =>
                {
                    m_HoveredMapIndex = index;
                    RefreshMapInfo();
                });
                cell.RegisterCallback<PointerLeaveEvent>(_ =>
                {
                    if (m_HoveredMapIndex == index) m_HoveredMapIndex = -1;
                    RefreshMapInfo();
                });
                cell.RegisterCallback<ClickEvent>(_ => OnMapCellClicked(index));

                gridRow.Add(cell);
                m_MapCells.Add(cell);
            }

            m_MapNameLabel = MakeValueLabel("");
            row.Add(m_MapNameLabel);

            // Flavor text sits under the grid, lined up with the controls column.
            var flavorRow = new VisualElement();
            flavorRow.style.flexDirection = FlexDirection.Row;
            flavorRow.style.marginBottom = RowSpacing + 6;

            var spacer = new VisualElement();
            spacer.style.width = LabelWidth;
            spacer.style.flexShrink = 0;
            flavorRow.Add(spacer);

            m_MapFlavorLabel = new Label("");
            m_MapFlavorLabel.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_MapFlavorLabel.style.fontSize = 15;
            m_MapFlavorLabel.style.unityFontStyleAndWeight = FontStyle.Italic;
            m_MapFlavorLabel.style.width = ControlWidth;
            m_MapFlavorLabel.style.whiteSpace = WhiteSpace.Normal;
            flavorRow.Add(m_MapFlavorLabel);
            parent.Add(flavorRow);

            SetSelectedMap(FindMapIndex(m_Rules.MapName));
            m_RefreshFromRules.Add(() => SetSelectedMap(FindMapIndex(m_Rules.MapName)));

            RegisterEditable(m_MapArea, new Editable
            {
                Get = () => m_SelectedMapIndex,
                Set = SetSelectedMap,
                Move = MoveMapSelection
            });
        }

        private int FindMapIndex(string mapName)
        {
            for (int i = 0; i < MapCount; i++)
            {
                if (maps[i].displayName == mapName) return i;
            }
            return 0;
        }

        private void SetSelectedMap(int index)
        {
            if (MapCount == 0)
            {
                m_MapNameLabel.text = "No maps configured";
                return;
            }

            m_SelectedMapIndex = Mathf.Clamp(index, 0, MapCount - 1);
            m_Rules.MapName = maps[m_SelectedMapIndex].displayName;
            RefreshMapCells();
            RefreshMapInfo();
        }

        private void MoveMapSelection(NavigationMoveEvent.Direction direction)
        {
            if (MapCount == 0) return;

            int row = m_SelectedMapIndex / MapColumns;
            int col = m_SelectedMapIndex % MapColumns;
            int lastRow = (MapCount - 1) / MapColumns;

            switch (direction)
            {
                case NavigationMoveEvent.Direction.Left: col = Mathf.Max(0, col - 1); break;
                case NavigationMoveEvent.Direction.Right: col = Mathf.Min(MapColumns - 1, col + 1); break;
                case NavigationMoveEvent.Direction.Up: row = Mathf.Max(0, row - 1); break;
                case NavigationMoveEvent.Direction.Down: row = Mathf.Min(lastRow, row + 1); break;
                default: return;
            }

            int next = Mathf.Min(row * MapColumns + col, MapCount - 1);
            if (next != m_SelectedMapIndex) SetSelectedMap(next);
        }

        private static readonly Color SelectedMapBorder = new Color(1f, 0.85f, 0.2f);

        /// <summary>Selected thumbnail at full brightness with a highlighted border; the rest grayed out.</summary>
        private void RefreshMapCells()
        {
            for (int i = 0; i < m_MapCells.Count; i++)
            {
                bool selected = i == m_SelectedMapIndex;
                Image cell = m_MapCells[i];
                cell.style.opacity = selected ? 1f : 0.3f;
                Color border = selected ? SelectedMapBorder : Color.clear;
                cell.style.borderTopColor = border;
                cell.style.borderBottomColor = border;
                cell.style.borderLeftColor = border;
                cell.style.borderRightColor = border;
            }
        }

        /// <summary>Shows the mouse-hovered map's name and flavor text, or the selected map's when nothing is hovered.</summary>
        private void RefreshMapInfo()
        {
            if (MapCount == 0) return;

            int index = m_HoveredMapIndex >= 0 ? m_HoveredMapIndex : m_SelectedMapIndex;
            m_MapNameLabel.text = maps[index].displayName;
            m_MapFlavorLabel.text = maps[index].flavorText;
        }

        private void OnMapCellClicked(int index)
        {
            if (m_Editing != null && m_Editing != m_MapArea) return;

            SetSelectedMap(index);
            m_MapArea.Focus();

            if (m_Editing == m_MapArea)
            {
                ExitEditMode(commit: true); // plays the select sound
            }
            else if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }
        }

        // ----- edit mode -----

        private void RegisterEditable(VisualElement element, Editable editable)
        {
            m_Editables[element] = editable;

            SetFocusedVisual(element, false);
            element.RegisterCallback<FocusInEvent>(_ => OnEditableFocusIn(element));
            element.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (m_Editing != element) SetFocusedVisual(element, false);
            });

            element.RegisterCallback<NavigationSubmitEvent>(evt => OnEditableSubmit(element, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<NavigationCancelEvent>(evt => OnEditableCancel(element, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<NavigationMoveEvent>(evt => OnNavigationMove(element, evt), TrickleDown.TrickleDown);
        }

        private void OnEditableFocusIn(VisualElement element)
        {
            // If the mouse moved focus away from something mid-edit, keep that edit rather than leaving it
            // stuck in edit mode on an element that no longer has focus.
            if (m_Editing != null && m_Editing != element)
            {
                VisualElement previous = m_Editing;
                ExitEditMode(commit: true);
                SetFocusedVisual(previous, false);
            }

            if (m_Editing != element) SetFocusedVisual(element, true);
        }

        private void OnEditableSubmit(VisualElement element, NavigationSubmitEvent evt)
        {
            if (m_Editing == element)
            {
                ExitEditMode(commit: true);
            }
            else if (m_Editing == null)
            {
                EnterEditMode(element);
            }

            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void OnEditableCancel(VisualElement element, NavigationCancelEvent evt)
        {
            if (m_Editing != element) return;

            ExitEditMode(commit: false);
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void EnterEditMode(VisualElement element)
        {
            m_Editing = element;
            m_ValueBeforeEdit = m_Editables[element].Get();
            element.style.backgroundColor = EditingBackground;

            if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }
        }

        private void ExitEditMode(bool commit)
        {
            if (m_Editing == null) return;

            VisualElement element = m_Editing;
            m_Editing = null;
            SetFocusedVisual(element, true);

            if (!commit)
            {
                m_Editables[element].Set(m_ValueBeforeEdit);

                if (cancelSound != null)
                {
                    AudioVolumeService.PlayOneShot(cancelSound, AudioCategory.SoundEffects, Vector3.zero);
                }
            }
            else if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }
        }

        // ----- navigation -----

        private void AddNavRow(int entryColumn, params VisualElement[] elements)
        {
            int row = m_NavRows.Count;
            m_NavRows.Add(elements);
            m_RowEntryColumn.Add(entryColumn);

            for (int col = 0; col < elements.Length; col++)
            {
                m_NavPositions[elements[col]] = (row, col);
            }
        }

        /// <summary>
        /// Every direction is always suppressed, so UI Toolkit's own navigation (and composite controls'
        /// own arrow-key handling) never runs. While editing, the direction goes to that element's
        /// Editable.Move instead of moving focus.
        /// </summary>
        private void OnNavigationMove(VisualElement element, NavigationMoveEvent evt)
        {
            SuppressDefaultNavigation(evt);

            if (m_Editing != null)
            {
                if (m_Editing == element) m_Editables[element].Move(evt.direction);
                return;
            }

            if (!m_NavPositions.TryGetValue(element, out (int Row, int Col) pos)) return;

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up: FocusRow(pos.Row - 1); break;
                case NavigationMoveEvent.Direction.Down: FocusRow(pos.Row + 1); break;
                case NavigationMoveEvent.Direction.Left: FocusCell(pos.Row, pos.Col - 1); break;
                case NavigationMoveEvent.Direction.Right: FocusCell(pos.Row, pos.Col + 1); break;
            }
        }

        private void FocusRow(int row)
        {
            if (row < 0 || row >= m_NavRows.Count) return;

            VisualElement[] elements = m_NavRows[row];
            int col = elements.Length == 1 ? 0 : Mathf.Clamp(m_RowEntryColumn[row], 0, elements.Length - 1);
            elements[col].Focus();
        }

        private void FocusCell(int row, int col)
        {
            VisualElement[] elements = m_NavRows[row];
            if (col < 0 || col >= elements.Length) return;
            elements[col].Focus();
        }

        private static void SuppressDefaultNavigation(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopPropagation();
        }

        // ----- visuals / buttons -----

        // Same colors as the other GameFlow settings screens.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color FocusedText = Color.black;
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color EditingBackground = new Color(0.85f, 0.55f, 0.15f);

        private static void SetFocusedVisual(VisualElement element, bool focused)
        {
            element.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
        }

        private static void SetButtonFocusedVisual(Button button, bool focused)
        {
            button.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
            button.style.color = focused ? FocusedText : UnfocusedText;
        }

        private Button MakeButton(string text, Action onClick, AudioClip clickSound, float width)
        {
            var button = new Button(() =>
            {
                if (m_Editing != null) return; // don't act mid-edit

                if (clickSound != null)
                {
                    AudioVolumeService.PlayOneShot(clickSound, AudioCategory.SoundEffects, Vector3.zero);
                }
                onClick();
            })
            { text = text };
            button.style.width = width;
            button.style.height = 40;
            button.style.fontSize = 18;

            SetButtonFocusedVisual(button, false);
            button.RegisterCallback<FocusInEvent>(_ =>
            {
                if (m_Editing != null)
                {
                    VisualElement previous = m_Editing;
                    ExitEditMode(commit: true);
                    SetFocusedVisual(previous, false);
                }
                SetButtonFocusedVisual(button, true);
            });
            button.RegisterCallback<FocusOutEvent>(_ => SetButtonFocusedVisual(button, false));
            button.RegisterCallback<NavigationMoveEvent>(evt => OnNavigationMove(button, evt));

            return button;
        }

        private void OnHostGameClicked()
        {
            // Placeholder until hosting is wired up (Fusion 2). Logs the chosen rules so they can be checked.
            Debug.Log($"[HostGame] Host Game selected (not implemented yet). Rules: {JsonUtility.ToJson(m_Rules)}");
        }

        private void OnBackClicked()
        {
            SceneManager.LoadScene(mainMenuSceneName);
        }
    }
}
