using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The game-rules screen, used in two modes (see <see cref="mode"/>):
    ///   - HostGame (the HostGame scene): the rules a host picks before starting a session, written into
    ///     <see cref="HostGameRules.Current"/>. Bottom-right button is Host Game, which creates the
    ///     session through Photon Fusion 2 (FusionSessionService) and opens the pre-match lobby.
    ///   - FindGamesFilter (the FindGames scene, Join Game > Public > Custom): the same rows minus
    ///     Visibility, each with an "All" checkbox on the left, editing <see cref="GameSearch.CustomFilter"/>.
    ///     A checked All means that rule isn't filtered on; its control is dimmed, and changing the control
    ///     unchecks All. Presets here apply the search presets (GameSearchPresets), and the bottom-right
    ///     button is Find Games, which opens the Game Browser with this filter.
    ///
    /// Rows, top to bottom: Presets (Normal / Hard / Very Hard), Visibility (host only), Map (2x5 grid of
    /// thumbnails - see <see cref="maps"/> - with the hovered/selected map's name to the right and flavor
    /// text underneath; unselected maps grayed out), Max Players, Round Time, Bonus Time, Lives, Starting
    /// Points, Death Penalty, Wager Payout (sliders over fixed option lists, current option shown to the
    /// right), Items and Pickups (On/Off dropdowns). Back is bottom-left; B/Esc while not editing also
    /// acts as Back. Choices aren't saved to disk - they're remembered for as long as the game runs.
    ///
    /// The settings column scales down uniformly to fit the screen (e.g. 1280x720) - see
    /// FitColumnToScreen. The two bottom buttons are never scaled.
    ///
    /// EDIT MODE: same model as AudioSettingsController/GraphicsSettingsController. A focused slider,
    /// dropdown, or the map grid only changes after Submit (A/Enter) puts it into edit mode; Submit again
    /// keeps the change, Cancel (B/Esc) reverts it (including an All checkbox it unchecked). While editing:
    ///   - Sliders: Left/Right move one option (clamped at the ends); Up/Down do nothing.
    ///   - Dropdowns: Up/Down cycle options (wrapping, same as the Graphics screen); Left/Right do nothing.
    ///   - Map grid: all four directions move the selection within the grid (clamped at its edges), and
    ///     nothing can leave the grid until A or B.
    /// All checkboxes aren't editables - A just toggles them. The mouse bypasses edit mode entirely: drag
    /// a slider, click a dropdown to open its list, click a map thumbnail or a checkbox.
    ///
    /// NAVIGATION (not editing): every focusable element sits in an explicit grid of rows
    /// (<see cref="m_NavRows"/>). Up/Down move between rows, keeping the same column when both rows have
    /// the same number of elements (so in FindGamesFilter mode you can run straight down the All
    /// checkboxes) and otherwise landing on that row's entry column (the control, Normal, or the
    /// bottom-right button). Left/Right move within a row. UI Toolkit's automatic navigation is never used.
    ///
    /// Sliders, dropdowns, checkboxes and the map grid register Submit/Cancel/Move in the capture phase
    /// (TrickleDown) as per-element closures, never reading evt.target - the lessons from the Audio slider
    /// and Graphics dropdown bugs (composite controls' own default actions run before a bubble-phase
    /// handler, and their evt.target can be an internal child element).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HostGameController : MonoBehaviour
    {
        public enum ScreenMode
        {
            HostGame,
            FindGamesFilter
        }

        [Serializable]
        public class MapEntry
        {
            public string displayName;
            public Texture2D thumbnail;
            [TextArea] public string flavorText;

            [Tooltip("HostGame mode only: the map prefab this entry plays - its file name in Assets/Core/Prefabs/Maps (e.g. LastStop). " +
                     "Leave empty on Random: hosting it picks one of the other entries' maps.")]
            public string mapPrefab;
        }

        [Tooltip("HostGame: rules for hosting. FindGamesFilter: the Join Game > Public > Custom search filter.")]
        [SerializeField] private ScreenMode mode = ScreenMode.HostGame;

        [Tooltip("Maps in grid order, left-to-right then top-to-bottom (5 per row). By convention Last Stop is 9th and Random is 10th, so they land bottom-right.")]
        [SerializeField] private MapEntry[] maps;

        [Tooltip("Scene to load when Back (or B/Esc) is pressed.")]
        [FormerlySerializedAs("mainMenuSceneName")]
        [SerializeField] private string backSceneName = "MainMenu";

        [Tooltip("FindGamesFilter mode only: scene Find Games opens.")]
        [SerializeField] private string gameBrowserSceneName = "GameBrowser";

        [Tooltip("HostGame mode only: scene opened once the session has been created.")]
        [SerializeField] private string gameLobbySceneName = "GameLobby";

        [Tooltip("Played when entering/confirming an edit, selecting a map, toggling All, or clicking a preset/Host Game/Find Games.")]
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

        // ----- layout -----

        private const float AllToggleWidth = 80f;
        private const float LabelWidth = 200f;
        private const float ControlWidth = 550f; // ~ the 5-wide map grid's natural width, so value labels line up with the map name
        private const float ValueWidth = 300f;
        private const float RowSpacing = 8f;
        private const float ThumbnailWidth = 96f;
        private const float ThumbnailHeight = 54f;
        private const float ColumnTop = 30f;
        private const float BottomButtonMargin = 40f;
        private const float BottomButtonHeight = 40f;
        private const float GapAboveBottomButtons = 16f;

        // ----- state -----

        private bool IsFilterMode => mode == ScreenMode.FindGamesFilter;

        /// <summary>Host mode: HostGameRules.Current. Filter mode: m_Filter.Values. Every row reads/writes through this.</summary>
        private HostGameRulesData m_Rules;

        /// <summary>Filter mode only (null in host mode): GameSearch.CustomFilter.</summary>
        private GameSearchFilter m_Filter;

        /// <summary>How a slider, dropdown, or the map grid reads, restores, and adjusts its value in edit mode.</summary>
        private sealed class Editable
        {
            public Func<int> Get;
            public Action<int> Set;
            public Action<NavigationMoveEvent.Direction> Move;
        }

        private readonly Dictionary<VisualElement, Editable> m_Editables = new Dictionary<VisualElement, Editable>();

        /// <summary>Filter mode: each control's All checkbox.</summary>
        private readonly Dictionary<VisualElement, Toggle> m_AllToggles = new Dictionary<VisualElement, Toggle>();

        /// <summary>The element currently in edit mode, or null.</summary>
        private VisualElement m_Editing;

        /// <summary>m_Editing's value right before editing started, restored on Cancel.</summary>
        private int m_ValueBeforeEdit;

        /// <summary>Filter mode: the whole filter right before editing started, restored on Cancel (covers All flags and LivesMoreThan).</summary>
        private GameSearchFilter m_FilterBeforeEdit;

        /// <summary>Explicit navigation grid - see the class summary's NAVIGATION section.</summary>
        private readonly List<VisualElement[]> m_NavRows = new List<VisualElement[]>();

        /// <summary>Which column Up/Down lands on when entering a row whose shape differs from the one being left.</summary>
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

        /// <summary>Re-reads m_Rules (and m_Filter's All flags) into each control without firing change callbacks - run after a preset or a cancelled edit.</summary>
        private readonly List<Action> m_RefreshFromRules = new List<Action>();

        private int MapCount => maps != null ? maps.Length : 0;

        /// <summary>Host mode: status line above the bottom buttons ("Creating game...", connection errors).</summary>
        private Label m_StatusLabel;

        /// <summary>True while Host Game is creating the session - buttons and Back are ignored meanwhile.</summary>
        private bool m_Busy;

        private void Awake()
        {
            // Same gamepad Submit/Cancel fix every GameFlow screen with an EventSystem applies - see
            // GamepadUIBindingFix.
            GamepadUIBindingFix.Apply();

            if (IsFilterMode)
            {
                m_Filter = GameSearch.CustomFilter;
                m_Rules = m_Filter.Values;
            }
            else
            {
                m_Rules = HostGameRules.Current;
            }

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
            // editing, on any element (including buttons and checkboxes, which don't handle Cancel at all).
            root.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                if (m_Editing != null || m_Busy) return;
                evt.StopPropagation();
                PlaySound(cancelSound);
                OnBackClicked();
            });

            m_Column = new VisualElement();
            m_Column.style.position = Position.Absolute;
            m_Column.style.top = ColumnTop;
            m_Column.style.left = Length.Percent(50);
            m_Column.style.translate = new Translate(Length.Percent(-50), 0);
            m_Column.style.width = (IsFilterMode ? AllToggleWidth : 0f) + LabelWidth + ControlWidth + ValueWidth + 20f;
            m_Column.style.flexDirection = FlexDirection.Column;
            // Scaled from its top-center, so shrinking it keeps it centered and pinned under the top margin.
            m_Column.style.transformOrigin = new TransformOrigin(Length.Percent(50), 0);
            root.Add(m_Column);
            VisualElement column = m_Column;

            // See FitColumnToScreen - fires on the first layout pass and on every resize/resolution change.
            root.RegisterCallback<GeometryChangedEvent>(_ => FitColumnToScreen(root));
            m_Column.RegisterCallback<GeometryChangedEvent>(_ => FitColumnToScreen(root));

            // Presets
            VisualElement presetsRow = MakeRow(column, "PRESETS", out _, hasAllToggle: false);
            m_NormalPresetButton = MakeButton("Normal", () => ApplyPreset(0), selectSound, 160f);
            Button hardButton = MakeButton("Hard", () => ApplyPreset(1), selectSound, 160f);
            Button veryHardButton = MakeButton("Very Hard", () => ApplyPreset(2), selectSound, 160f);
            hardButton.style.marginLeft = 20;
            veryHardButton.style.marginLeft = 20;
            presetsRow.Add(m_NormalPresetButton);
            presetsRow.Add(hardButton);
            presetsRow.Add(veryHardButton);
            AddNavRow(0, m_NormalPresetButton, hardButton, veryHardButton);

            // Visibility (hosting only - searches only ever list public games)
            if (!IsFilterMode)
            {
                AddDropdownRow(column, "VISIBILITY", VisibilityChoices,
                    () => m_Rules.IsPublic ? 0 : 1,
                    i => m_Rules.IsPublic = i == 0,
                    null);
            }

            // Map
            BuildMapRows(column);

            // Sliders. The fallback index in each IndexOf is the Normal host preset's value, used only if
            // the stored value somehow isn't one of the options.
            AddSliderRow(column, "MAX PLAYERS", MaxPlayerOptions.Length,
                () => IndexOf(MaxPlayerOptions, m_Rules.MaxPlayers, 6),
                i => MaxPlayerOptions[i].ToString(CultureInfo.InvariantCulture),
                i => m_Rules.MaxPlayers = MaxPlayerOptions[i],
                Flag(() => m_Filter.AllMaxPlayers, v => m_Filter.AllMaxPlayers = v));

            AddSliderRow(column, "ROUND TIME", RoundTimeOptions.Length,
                () => IndexOf(RoundTimeOptions, m_Rules.RoundTimeSeconds, 4),
                i => GameRulesFormat.Time(RoundTimeOptions[i]),
                i => m_Rules.RoundTimeSeconds = RoundTimeOptions[i],
                Flag(() => m_Filter.AllRoundTimes, v => m_Filter.AllRoundTimes = v));

            AddSliderRow(column, "BONUS TIME", BonusTimeOptions.Length,
                () => IndexOf(BonusTimeOptions, m_Rules.BonusTimeSeconds, 8),
                i => GameRulesFormat.Time(BonusTimeOptions[i]),
                i => m_Rules.BonusTimeSeconds = BonusTimeOptions[i],
                Flag(() => m_Filter.AllBonusTimes, v => m_Filter.AllBonusTimes = v));

            // In filter mode the Hard search preset means "more than 5 lives" (GameSearchFilter.LivesMoreThan);
            // the label says so, and any edit to this slider turns it back into an exact match.
            AddSliderRow(column, "LIVES", LivesOptions.Length,
                () => IndexOf(LivesOptions, m_Rules.Lives, LivesOptions.Length - 1),
                i => m_Filter != null && m_Filter.LivesMoreThan
                    ? $"More than {GameRulesFormat.Lives(LivesOptions[i])}"
                    : GameRulesFormat.Lives(LivesOptions[i]),
                i =>
                {
                    m_Rules.Lives = LivesOptions[i];
                    if (m_Filter != null) m_Filter.LivesMoreThan = false;
                },
                Flag(() => m_Filter.AllLives, v => m_Filter.AllLives = v));

            AddSliderRow(column, "STARTING POINTS", StartingPointsOptions.Length,
                () => IndexOf(StartingPointsOptions, m_Rules.StartingPoints, 10),
                i => StartingPointsOptions[i].ToString(CultureInfo.InvariantCulture),
                i => m_Rules.StartingPoints = StartingPointsOptions[i],
                Flag(() => m_Filter.AllStartingPoints, v => m_Filter.AllStartingPoints = v));

            AddSliderRow(column, "DEATH PENALTY", DeathPenaltyOptions.Length,
                () => IndexOf(DeathPenaltyOptions, m_Rules.DeathPenalty, 2),
                i => GameRulesFormat.DeathPenalty(DeathPenaltyOptions[i]),
                i => m_Rules.DeathPenalty = DeathPenaltyOptions[i],
                Flag(() => m_Filter.AllDeathPenalties, v => m_Filter.AllDeathPenalties = v));

            AddSliderRow(column, "WAGER PAYOUT", WagerPayoutOptions.Length,
                () => IndexOf(WagerPayoutOptions, m_Rules.WagerPayout, 1),
                i => GameRulesFormat.Payout(WagerPayoutOptions[i]),
                i => m_Rules.WagerPayout = WagerPayoutOptions[i],
                Flag(() => m_Filter.AllWagerPayouts, v => m_Filter.AllWagerPayouts = v));

            // Items / Pickups
            AddDropdownRow(column, "ITEMS", ItemsChoices,
                () => m_Rules.ItemsEnabled ? 0 : 1,
                i => m_Rules.ItemsEnabled = i == 0,
                Flag(() => m_Filter.AllItems, v => m_Filter.AllItems = v));
            AddDropdownRow(column, "PICKUPS", PickupsChoices,
                () => m_Rules.PickupsEnabled ? 0 : 1,
                i => m_Rules.PickupsEnabled = i == 0,
                Flag(() => m_Filter.AllPickups, v => m_Filter.AllPickups = v));

            // Back (bottom-left) / Host Game or Find Games (bottom-right). Not part of m_Column, so they're
            // never scaled.
            Button backButton = MakeButton("Back", OnBackClicked, cancelSound, 200f);
            backButton.style.position = Position.Absolute;
            backButton.style.left = 40;
            backButton.style.bottom = BottomButtonMargin;
            root.Add(backButton);

            Button primaryButton = IsFilterMode
                ? MakeButton("Find Games", OnFindGamesClicked, selectSound, 200f)
                : MakeButton("Host Game", OnHostGameClicked, selectSound, 200f);
            primaryButton.style.position = Position.Absolute;
            primaryButton.style.right = 40;
            primaryButton.style.bottom = BottomButtonMargin;
            root.Add(primaryButton);

            AddNavRow(1, backButton, primaryButton);

            m_StatusLabel = new Label("");
            m_StatusLabel.style.position = Position.Absolute;
            m_StatusLabel.style.right = 260;
            m_StatusLabel.style.bottom = BottomButtonMargin + 10;
            m_StatusLabel.style.color = new Color(0.9f, 0.9f, 0.9f);
            m_StatusLabel.style.fontSize = 16;
            root.Add(m_StatusLabel);

            // Nothing is focused by default, and gamepad navigation needs a starting point - start at
            // the top-left element.
            m_NormalPresetButton.Focus();
            SetButtonFocusedVisual(m_NormalPresetButton, true);
        }

        /// <summary>Getter/setter pair for one of m_Filter's All flags, or null in host mode (no checkbox).</summary>
        private (Func<bool> Get, Action<bool> Set)? Flag(Func<bool> get, Action<bool> set)
        {
            if (!IsFilterMode) return null;
            return (get, set);
        }

        /// <summary>
        /// Uniformly scales the whole settings column (never the bottom buttons) down just enough to fit
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

        /// <summary>0 = Normal, 1 = Hard, 2 = Very Hard. Host mode applies HostGamePresets (visibility untouched); filter mode applies GameSearchPresets.</summary>
        private void ApplyPreset(int presetIndex)
        {
            if (IsFilterMode)
            {
                GameSearchFilter preset = presetIndex == 0 ? GameSearchPresets.Normal
                                        : presetIndex == 1 ? GameSearchPresets.Hard
                                        : GameSearchPresets.VeryHard;
                m_Filter.CopyFrom(preset);
            }
            else
            {
                HostGameRulesData preset = presetIndex == 0 ? HostGamePresets.Normal
                                         : presetIndex == 1 ? HostGamePresets.Hard
                                         : HostGamePresets.VeryHard;
                m_Rules.ApplyPreset(preset);
            }

            RefreshAllControls();
        }

        private void RefreshAllControls()
        {
            foreach (Action refresh in m_RefreshFromRules)
            {
                refresh();
            }
        }

        /// <summary>
        /// Adds a row to parent: in filter mode an All checkbox (or, for rows without one, an equally wide
        /// spacer so labels line up), then the row's name label.
        /// </summary>
        private VisualElement MakeRow(VisualElement parent, string label, out Toggle allToggle, bool hasAllToggle)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = RowSpacing;

            allToggle = null;
            if (IsFilterMode)
            {
                if (hasAllToggle)
                {
                    allToggle = new Toggle("All");
                    allToggle.style.width = AllToggleWidth - 10f;
                    allToggle.style.marginRight = 10;
                    allToggle.style.flexShrink = 0;
                    allToggle.labelElement.style.minWidth = 0;
                    allToggle.labelElement.style.marginRight = 6;
                    row.Add(allToggle);
                }
                else
                {
                    var spacer = new VisualElement();
                    spacer.style.width = AllToggleWidth;
                    spacer.style.flexShrink = 0;
                    row.Add(spacer);
                }
            }

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
        /// Builds a label + SliderInt(0..optionCount-1) + value-label row (plus an All checkbox when flag
        /// is non-null) and adds it to the navigation grid. readIndex maps the current m_Rules value to a
        /// slider index; it's used for the initial value and whenever the controls are refreshed.
        /// </summary>
        private void AddSliderRow(VisualElement parent, string label, int optionCount, Func<int> readIndex,
            Func<int, string> format, Action<int> onChanged, (Func<bool> Get, Action<bool> Set)? flag)
        {
            VisualElement row = MakeRow(parent, label, out Toggle allToggle, flag.HasValue);

            int initialIndex = readIndex();
            var slider = new SliderInt { lowValue = 0, highValue = optionCount - 1, value = initialIndex };
            slider.style.width = ControlWidth;
            slider.style.flexShrink = 0;
            row.Add(slider);

            Label valueLabel = MakeValueLabel(format(initialIndex));
            row.Add(valueLabel);

            // Any change that reaches here is user-driven (mouse drag or edit-mode Left/Right) - refreshes
            // and reverts use SetValueWithoutNotify - so it also unchecks this row's All box.
            slider.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;
                onChanged(evt.newValue);
                valueLabel.text = format(evt.newValue);
                UncheckAll(slider);
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
                    onChanged(i);
                    valueLabel.text = format(i);
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

            FinishControlRow(slider, allToggle, flag);
        }

        private void AddDropdownRow(VisualElement parent, string label, List<string> choices, Func<int> readIndex,
            Action<int> onChanged, (Func<bool> Get, Action<bool> Set)? flag)
        {
            VisualElement row = MakeRow(parent, label, out Toggle allToggle, flag.HasValue);

            var dropdown = new DropdownField(new List<string>(choices), readIndex());
            dropdown.style.width = 260;
            dropdown.style.flexShrink = 0;
            row.Add(dropdown);

            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;
                onChanged(dropdown.index);
                UncheckAll(dropdown);
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

            FinishControlRow(dropdown, allToggle, flag);
        }

        private void BuildMapRows(VisualElement parent)
        {
            var flag = Flag(() => m_Filter.AllMaps, v => m_Filter.AllMaps = v);
            VisualElement row = MakeRow(parent, "MAP", out Toggle allToggle, flag.HasValue);

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
            spacer.style.width = (IsFilterMode ? AllToggleWidth : 0f) + LabelWidth;
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

            FinishControlRow(m_MapArea, allToggle, flag);
        }

        /// <summary>Wires a row's All checkbox (filter mode) and adds the row to the navigation grid: [checkbox, control] or just [control].</summary>
        private void FinishControlRow(VisualElement control, Toggle allToggle, (Func<bool> Get, Action<bool> Set)? flag)
        {
            if (allToggle == null || !flag.HasValue)
            {
                AddNavRow(0, control);
                return;
            }

            Func<bool> get = flag.Value.Get;
            Action<bool> set = flag.Value.Set;

            m_AllToggles[control] = allToggle;
            allToggle.SetValueWithoutNotify(get());
            SetAllDimmed(control, get());

            allToggle.RegisterValueChangedCallback(evt =>
            {
                set(evt.newValue);
                SetAllDimmed(control, evt.newValue);
            });

            m_RefreshFromRules.Add(() =>
            {
                allToggle.SetValueWithoutNotify(get());
                SetAllDimmed(control, get());
            });

            SetToggleFocusedVisual(allToggle, false);
            allToggle.RegisterCallback<FocusInEvent>(_ =>
            {
                CommitEditIfFocusMovedAway(allToggle);
                SetToggleFocusedVisual(allToggle, true);
            });
            allToggle.RegisterCallback<FocusOutEvent>(_ => SetToggleFocusedVisual(allToggle, false));

            // A toggles it. Handled here (and the Toggle's own default Submit action prevented) so it
            // can't double-toggle, and so it plays the same select sound as everything else.
            allToggle.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.PreventDefault();
                evt.StopPropagation();
                if (m_Editing != null) return;
                allToggle.value = !allToggle.value;
                PlaySound(selectSound);
            }, TrickleDown.TrickleDown);
            allToggle.RegisterCallback<NavigationMoveEvent>(evt => OnNavigationMove(allToggle, evt), TrickleDown.TrickleDown);

            AddNavRow(1, allToggle, control);
        }

        /// <summary>A checked All box dims its control, since that rule isn't being filtered on.</summary>
        private static void SetAllDimmed(VisualElement control, bool all)
        {
            control.style.opacity = all ? 0.35f : 1f;
        }

        /// <summary>Called on any user-driven change to a control: in filter mode that rule is now being filtered on.</summary>
        private void UncheckAll(VisualElement control)
        {
            if (m_AllToggles.TryGetValue(control, out Toggle toggle) && toggle.value)
            {
                toggle.value = false; // fires the toggle's callback, which updates the filter flag and dimming
            }
        }

        /// <summary>
        /// The map prefab the given grid entry plays: its own <see cref="MapEntry.mapPrefab"/>, or - for Random,
        /// or any entry without one - a random pick among the entries that have one. Empty if none do.
        /// </summary>
        private string ResolveMapPrefab(int index)
        {
            if (MapCount == 0) return "";
            if (index >= 0 && index < MapCount && !string.IsNullOrWhiteSpace(maps[index].mapPrefab))
            {
                return maps[index].mapPrefab.Trim();
            }

            var choices = new List<string>();
            foreach (MapEntry entry in maps)
            {
                if (!string.IsNullOrWhiteSpace(entry.mapPrefab)) choices.Add(entry.mapPrefab.Trim());
            }
            return choices.Count > 0 ? choices[UnityEngine.Random.Range(0, choices.Count)] : "";
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
            if (next != m_SelectedMapIndex)
            {
                SetSelectedMap(next);
                UncheckAll(m_MapArea);
            }
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
            UncheckAll(m_MapArea);
            m_MapArea.Focus();

            if (m_Editing == m_MapArea)
            {
                ExitEditMode(commit: true); // plays the select sound
            }
            else
            {
                PlaySound(selectSound);
            }
        }

        // ----- edit mode -----

        private void RegisterEditable(VisualElement element, Editable editable)
        {
            m_Editables[element] = editable;

            SetFocusedVisual(element, false);
            element.RegisterCallback<FocusInEvent>(_ =>
            {
                CommitEditIfFocusMovedAway(element);
                if (m_Editing != element) SetFocusedVisual(element, true);
            });
            element.RegisterCallback<FocusOutEvent>(_ =>
            {
                if (m_Editing != element) SetFocusedVisual(element, false);
            });

            element.RegisterCallback<NavigationSubmitEvent>(evt => OnEditableSubmit(element, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<NavigationCancelEvent>(evt => OnEditableCancel(element, evt), TrickleDown.TrickleDown);
            element.RegisterCallback<NavigationMoveEvent>(evt => OnNavigationMove(element, evt), TrickleDown.TrickleDown);
        }

        /// <summary>
        /// If the mouse moved focus to <paramref name="newlyFocused"/> while something else was mid-edit,
        /// keep that edit rather than leaving it stuck in edit mode on an element that no longer has focus.
        /// </summary>
        private void CommitEditIfFocusMovedAway(VisualElement newlyFocused)
        {
            if (m_Editing == null || m_Editing == newlyFocused) return;

            VisualElement previous = m_Editing;
            ExitEditMode(commit: true);
            SetFocusedVisual(previous, false);
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
            m_FilterBeforeEdit = m_Filter?.Clone();
            element.style.backgroundColor = EditingBackground;
            PlaySound(selectSound);
        }

        private void ExitEditMode(bool commit)
        {
            if (m_Editing == null) return;

            VisualElement element = m_Editing;
            m_Editing = null;
            SetFocusedVisual(element, true);

            if (!commit)
            {
                if (m_FilterBeforeEdit != null)
                {
                    // Filter mode: restore the whole filter (the value plus any All box / "more than"
                    // flag the edit changed) and redraw every control from it.
                    m_Filter.CopyFrom(m_FilterBeforeEdit);
                    RefreshAllControls();
                }
                else
                {
                    m_Editables[element].Set(m_ValueBeforeEdit);
                }

                PlaySound(cancelSound);
            }
            else
            {
                PlaySound(selectSound);
            }

            m_FilterBeforeEdit = null;
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
                case NavigationMoveEvent.Direction.Up: FocusRow(pos.Row - 1, pos.Row, pos.Col); break;
                case NavigationMoveEvent.Direction.Down: FocusRow(pos.Row + 1, pos.Row, pos.Col); break;
                case NavigationMoveEvent.Direction.Left: FocusCell(pos.Row, pos.Col - 1); break;
                case NavigationMoveEvent.Direction.Right: FocusCell(pos.Row, pos.Col + 1); break;
            }
        }

        private void FocusRow(int row, int fromRow, int fromCol)
        {
            if (row < 0 || row >= m_NavRows.Count) return;

            VisualElement[] elements = m_NavRows[row];
            int col = m_NavRows[fromRow].Length == elements.Length ? fromCol : m_RowEntryColumn[row];
            elements[Mathf.Clamp(col, 0, elements.Length - 1)].Focus();
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

        private static void SetToggleFocusedVisual(Toggle toggle, bool focused)
        {
            toggle.style.backgroundColor = focused ? FocusedBackground : Color.clear;
            toggle.labelElement.style.color = focused ? FocusedText : Color.white;
        }

        private void PlaySound(AudioClip clip)
        {
            if (clip != null)
            {
                AudioVolumeService.PlayOneShot(clip, AudioCategory.UISoundEffects, Vector3.zero);
            }
        }

        private Button MakeButton(string text, Action onClick, AudioClip clickSound, float width)
        {
            var button = new Button(() =>
            {
                if (m_Editing != null || m_Busy) return; // don't act mid-edit or while creating the game
                PlaySound(clickSound);
                onClick();
            })
            { text = text };
            button.style.width = width;
            button.style.height = 40;
            button.style.fontSize = 18;

            SetButtonFocusedVisual(button, false);
            button.RegisterCallback<FocusInEvent>(_ =>
            {
                CommitEditIfFocusMovedAway(button);
                SetButtonFocusedVisual(button, true);
            });
            button.RegisterCallback<FocusOutEvent>(_ => SetButtonFocusedVisual(button, false));
            button.RegisterCallback<NavigationMoveEvent>(evt => OnNavigationMove(button, evt));

            return button;
        }

        private async void OnHostGameClicked()
        {
            if (m_Busy) return;
            m_Busy = true;
            m_StatusLabel.text = "Creating game...";

            HostGameRulesData rules = m_Rules.Clone();
            rules.MapPrefab = ResolveMapPrefab(m_SelectedMapIndex);
            Debug.Log($"[HostGame] Map '{rules.MapName}' plays map prefab '{rules.MapPrefab}'.");

            string error = await FusionSessionService.Instance.HostGameAsync(rules);
            if (this == null) return; // screen closed meanwhile

            if (error == null)
            {
                SceneManager.LoadScene(gameLobbySceneName);
                return;
            }

            m_Busy = false;
            m_StatusLabel.text = error;
        }

        private void OnFindGamesClicked()
        {
            GameSearch.CurrentFilter = m_Filter.Clone();
            GameSearch.BrowserBackScene = SceneManager.GetActiveScene().name;
            SceneManager.LoadScene(gameBrowserSceneName);
        }

        private void OnBackClicked()
        {
            SceneManager.LoadScene(backSceneName);
        }
    }
}
