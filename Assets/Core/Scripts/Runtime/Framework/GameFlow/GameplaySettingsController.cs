using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Gameplay Settings screen (Settings > Gameplay, above Graphics). Same small-logo layout and
    /// Restore Defaults / Save Changes / Back column as the other settings screens.
    ///   - LANGUAGE: dropdown of <see cref="Loc.Languages"/> (each in its own language). Picking one shows the
    ///     screen in it straight away; Back without saving puts the saved language back.
    ///   - COLORBLIND MODE: Off / Protanopia / Deuteranopia / Tritanopia - a whole-screen color correction for
    ///     the 3D world (<see cref="ColorblindFilter"/>), applied on Save Changes.
    ///   - SHOW DISPLAY NAMES: moved here from Change Profile. Off shows other players as "Player 1", ...
    ///   - MATURE LANGUAGE FILTER: on by default; bad words in player names show as *** (NameFilter.Mask).
    ///   - ARACHNOPHOBIA MODE / ARACHNOPHILIA MODE: off by default, and never both on - ticking one unticks
    ///     the other. Nothing uses them yet (see GameplaySettingsData).
    /// Saved with the other settings in settings.json (<see cref="GameplaySettingsData"/>) and applied through
    /// <see cref="GameplaySettings"/>.
    ///
    /// Controls follow the Graphics screen: Up/Down move between rows, Right jumps to Save Changes and Left
    /// from the buttons comes back to Language. A on a dropdown enters edit mode (Up/Down change the
    /// choice, A keeps it, B undoes it); A on a checkbox ticks it. B/Esc = Back.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GameplaySettingsController : MonoBehaviour
    {
        [Tooltip("Same logo as the other settings screens.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back is clicked.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when a row or Save Changes is selected.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Back is selected.")]
        [SerializeField] private AudioClip cancelSound;

        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color EditingBackground = new Color(0.85f, 0.55f, 0.15f);
        private static readonly Color HintText = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color StatusText = new Color(0.8f, 0.8f, 0.8f);

        private GameplaySettingsData m_Settings;
        private string m_SavedLanguage;

        private DropdownField m_LanguageDropdown;
        private DropdownField m_ColorblindDropdown;
        private readonly Dictionary<DropdownField, System.Action<int>> m_DropdownChanged = new Dictionary<DropdownField, System.Action<int>>();
        private Toggle m_ShowNamesToggle;
        private Toggle m_FilterToggle;
        private Toggle m_PhobiaToggle;
        private Toggle m_PhiliaToggle;
        private readonly List<VisualElement> m_Rows = new List<VisualElement>();

        private DropdownField m_EditingDropdown; // the dropdown in edit mode (A pressed on it), if any
        private int m_IndexBeforeEdit;
        private bool Editing => m_EditingDropdown != null;

        private Button m_RestoreDefaultsButton;
        private Button m_SaveButton;
        private Button m_BackButton;
        private Button[] m_ButtonsInOrder;
        private Label m_StatusLabel;

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            m_Settings = GameplaySettings.Current.Clone();
            m_SavedLanguage = m_Settings.Language;

            BuildUI(GetComponent<UIDocument>().rootVisualElement);
        }

        private void BuildUI(VisualElement root)
        {
            MenuUI.SetupRoot(root);
            MenuUI.AddSmallLogo(root, titleImage);

            root.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                if (Editing) return; // the dropdown undoes its edit first
                evt.StopPropagation();
                MenuUI.PlaySound(cancelSound);
                OnBackClicked();
            });

            var column = new VisualElement();
            column.style.position = Position.Absolute;
            column.style.left = Length.Percent(50);
            column.style.top = Length.Percent(50);
            column.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            column.style.flexDirection = FlexDirection.Column;
            root.Add(column);

            // LANGUAGE
            var languageNames = new List<string>();
            foreach (Loc.Language language in Loc.Languages) languageNames.Add(language.NativeName);
            m_LanguageDropdown = AddDropdownRow(column, "LANGUAGE", null, languageNames,
                Mathf.Max(0, Loc.IndexOf(m_Settings.Language)), SetLanguage);

            // COLORBLIND MODE
            m_ColorblindDropdown = AddDropdownRow(column, "COLORBLIND MODE", "Adjusts the game's colors",
                new List<string>(ColorblindFilter.ModeNames), m_Settings.ColorblindMode,
                index => m_Settings.ColorblindMode = index);

            // Checkboxes
            m_ShowNamesToggle = AddToggleRow(column, "SHOW DISPLAY NAMES", "Off shows other players as Player 1, Player 2, ...",
                m_Settings.ShowDisplayNames, value => m_Settings.ShowDisplayNames = value);
            m_FilterToggle = AddToggleRow(column, "MATURE LANGUAGE FILTER", "Bad words in player names show as ***",
                m_Settings.MatureLanguageFilter, value => m_Settings.MatureLanguageFilter = value);
            m_PhobiaToggle = AddToggleRow(column, "ARACHNOPHOBIA MODE", "Swaps spiders for something else",
                m_Settings.ArachnophobiaMode, value =>
                {
                    m_Settings.ArachnophobiaMode = value;
                    if (value && m_PhiliaToggle.value) m_PhiliaToggle.value = false; // never both on
                });
            m_PhiliaToggle = AddToggleRow(column, "ARACHNOPHILIA MODE", "Swaps some things to spiders",
                m_Settings.ArachnophiliaMode, value =>
                {
                    m_Settings.ArachnophiliaMode = value;
                    if (value && m_PhobiaToggle.value) m_PhobiaToggle.value = false; // never both on
                });

            // Restore Defaults / Save Changes / Back, bottom right like the other settings screens.
            VisualElement buttons = MenuUI.AddBottomRightColumn(root);
            m_RestoreDefaultsButton = MenuUI.MakeButton("Restore Defaults", OnRestoreDefaultsClicked, selectSound);
            m_SaveButton = MenuUI.MakeButton("Save Changes", OnSaveChangesClicked, selectSound);
            m_BackButton = MenuUI.MakeButton("Back", OnBackClicked, cancelSound);
            m_ButtonsInOrder = new[] { m_RestoreDefaultsButton, m_SaveButton, m_BackButton };
            foreach (Button button in m_ButtonsInOrder)
            {
                Button captured = button;
                buttons.Add(captured);
                captured.RegisterCallback<NavigationMoveEvent>(evt => OnButtonNavigationMove(captured, evt));
            }

            m_StatusLabel = new Label("");
            m_StatusLabel.style.position = Position.Absolute;
            m_StatusLabel.style.right = 260;
            m_StatusLabel.style.bottom = 40 + 56 + 18;
            m_StatusLabel.style.fontSize = 16;
            m_StatusLabel.style.color = StatusText;
            root.Add(m_StatusLabel);

            m_LanguageDropdown.Focus();
            SetHighlight(m_LanguageDropdown, true);
        }

        /// <summary>A labelled dropdown row with A-to-edit behaviour; <paramref name="onChanged"/> gets the new index.</summary>
        private DropdownField AddDropdownRow(VisualElement column, string label, string hint, List<string> choices, int index, System.Action<int> onChanged)
        {
            VisualElement row = MakeRow(column, label);
            var dropdown = new DropdownField(choices, Mathf.Clamp(index, 0, choices.Count - 1));
            dropdown.style.width = 240;
            dropdown.style.flexShrink = 0;
            SetHighlight(dropdown, false);
            dropdown.RegisterCallback<FocusInEvent>(_ => SetHighlight(dropdown, true));
            dropdown.RegisterCallback<FocusOutEvent>(_ => SetHighlight(dropdown, false));
            dropdown.RegisterCallback<NavigationSubmitEvent>(evt => OnDropdownSubmit(dropdown, evt), TrickleDown.TrickleDown);
            dropdown.RegisterCallback<NavigationCancelEvent>(evt => OnDropdownCancel(dropdown, evt), TrickleDown.TrickleDown);
            dropdown.RegisterCallback<NavigationMoveEvent>(evt => OnDropdownNavigationMove(dropdown, evt), TrickleDown.TrickleDown);
            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;
                onChanged(dropdown.index);
                ClearStatus();
            });
            m_DropdownChanged[dropdown] = onChanged;
            row.Add(dropdown);

            if (!string.IsNullOrEmpty(hint))
            {
                var hintLabel = new Label(hint);
                hintLabel.style.color = HintText;
                hintLabel.style.fontSize = 14;
                hintLabel.style.marginLeft = 16;
                row.Add(hintLabel);
            }

            m_Rows.Add(dropdown);
            return dropdown;
        }

        private static VisualElement MakeRow(VisualElement column, string label)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 16;

            var nameLabel = new Label(label);
            nameLabel.style.color = Color.white;
            nameLabel.style.fontSize = 16;
            nameLabel.style.width = 320;
            nameLabel.style.flexShrink = 0;
            row.Add(nameLabel);

            column.Add(row);
            return row;
        }

        private Toggle AddToggleRow(VisualElement column, string label, string hint, bool initial, System.Action<bool> onChanged)
        {
            VisualElement row = MakeRow(column, label);

            var toggle = new Toggle();
            toggle.SetValueWithoutNotify(initial);
            toggle.style.flexShrink = 0;
            toggle.style.paddingLeft = 4;
            toggle.style.paddingRight = 4;
            toggle.style.paddingTop = 2;
            toggle.style.paddingBottom = 2;
            row.Add(toggle);

            var hintLabel = new Label(hint);
            hintLabel.style.color = HintText;
            hintLabel.style.fontSize = 14;
            hintLabel.style.marginLeft = 16;
            row.Add(hintLabel);

            toggle.RegisterValueChangedCallback(evt =>
            {
                onChanged(evt.newValue);
                ClearStatus();
            });

            toggle.style.backgroundColor = Color.clear;
            toggle.RegisterCallback<FocusInEvent>(_ => toggle.style.backgroundColor = FocusedBackground);
            toggle.RegisterCallback<FocusOutEvent>(_ => toggle.style.backgroundColor = Color.clear);

            // A ticks it - handled here (and the Toggle's own default prevented) so it can't double-toggle.
            toggle.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.PreventDefault();
                evt.StopPropagation();
                toggle.value = !toggle.value;
                MenuUI.PlaySound(selectSound);
            }, TrickleDown.TrickleDown);
            toggle.RegisterCallback<NavigationMoveEvent>(evt => OnRowNavigationMove(toggle, evt), TrickleDown.TrickleDown);

            m_Rows.Add(toggle);
            return toggle;
        }

        // =====================================================================================
        // Dropdowns (edit mode, like the Graphics screen's dropdowns)
        // =====================================================================================

        private void SetLanguage(int index)
        {
            if (index < 0 || index >= Loc.Languages.Length) return;
            m_Settings.Language = Loc.Languages[index].Code;
            Loc.SetLanguage(m_Settings.Language); // preview straight away; Back undoes it unless saved
        }

        private void OnDropdownSubmit(DropdownField dropdown, NavigationSubmitEvent evt)
        {
            if (m_EditingDropdown == dropdown)
            {
                m_EditingDropdown = null;
                SetHighlight(dropdown, true);
            }
            else
            {
                m_EditingDropdown = dropdown;
                m_IndexBeforeEdit = dropdown.index;
                dropdown.style.backgroundColor = EditingBackground;
            }
            MenuUI.PlaySound(selectSound);
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void OnDropdownCancel(DropdownField dropdown, NavigationCancelEvent evt)
        {
            if (m_EditingDropdown != dropdown) return;

            m_EditingDropdown = null;
            if (dropdown.index != m_IndexBeforeEdit)
            {
                dropdown.SetValueWithoutNotify(dropdown.choices[m_IndexBeforeEdit]);
                m_DropdownChanged[dropdown](m_IndexBeforeEdit);
            }
            SetHighlight(dropdown, true);
            MenuUI.PlaySound(cancelSound);
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void OnDropdownNavigationMove(DropdownField dropdown, NavigationMoveEvent evt)
        {
            if (m_EditingDropdown == dropdown)
            {
                if (evt.direction == NavigationMoveEvent.Direction.Up || evt.direction == NavigationMoveEvent.Direction.Down)
                {
                    int count = dropdown.choices.Count;
                    int step = evt.direction == NavigationMoveEvent.Direction.Down ? 1 : -1;
                    dropdown.index = ((dropdown.index + step) % count + count) % count;
                }
                Suppress(evt);
                return;
            }
            OnRowNavigationMove(dropdown, evt);
        }

        // =====================================================================================
        // Navigation
        // =====================================================================================

        private void OnRowNavigationMove(VisualElement row, NavigationMoveEvent evt)
        {
            int index = m_Rows.IndexOf(row);
            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    if (index > 0) m_Rows[index - 1].Focus();
                    break;
                case NavigationMoveEvent.Direction.Down:
                    if (index >= 0 && index < m_Rows.Count - 1) m_Rows[index + 1].Focus();
                    else m_SaveButton.Focus();
                    break;
                case NavigationMoveEvent.Direction.Right:
                    m_SaveButton.Focus();
                    break;
            }
            Suppress(evt);
        }

        private void OnButtonNavigationMove(Button button, NavigationMoveEvent evt)
        {
            int index = System.Array.IndexOf(m_ButtonsInOrder, button);
            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    if (index > 0) m_ButtonsInOrder[index - 1].Focus();
                    break;
                case NavigationMoveEvent.Direction.Down:
                    if (index < m_ButtonsInOrder.Length - 1) m_ButtonsInOrder[index + 1].Focus();
                    break;
                case NavigationMoveEvent.Direction.Left:
                    m_LanguageDropdown.Focus();
                    break;
            }
            Suppress(evt);
        }

        private static void Suppress(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private static void SetHighlight(VisualElement element, bool focused)
        {
            element.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
        }

        // =====================================================================================
        // Actions
        // =====================================================================================

        private void OnRestoreDefaultsClicked()
        {
            if (Editing) return;

            m_Settings = new GameplaySettingsData();
            m_LanguageDropdown.SetValueWithoutNotify(m_LanguageDropdown.choices[Mathf.Max(0, Loc.IndexOf(m_Settings.Language))]);
            Loc.SetLanguage(m_Settings.Language);
            m_ColorblindDropdown.SetValueWithoutNotify(m_ColorblindDropdown.choices[m_Settings.ColorblindMode]);
            m_ShowNamesToggle.SetValueWithoutNotify(m_Settings.ShowDisplayNames);
            m_FilterToggle.SetValueWithoutNotify(m_Settings.MatureLanguageFilter);
            m_PhobiaToggle.SetValueWithoutNotify(m_Settings.ArachnophobiaMode);
            m_PhiliaToggle.SetValueWithoutNotify(m_Settings.ArachnophiliaMode);
            ClearStatus();
        }

        private void OnSaveChangesClicked()
        {
            if (Editing) return;

            // Load what's on disk so the other screens' settings in settings.json are kept.
            InputBindingsData data = InputBindingsStore.Load();
            data.Gameplay = m_Settings.Clone();
            InputBindingsStore.Save(data);

            GameplaySettings.Apply(m_Settings);
            m_SavedLanguage = m_Settings.Language;
            m_StatusLabel.text = "Changes saved."; // translated on screen by LocApplier
        }

        private void OnBackClicked()
        {
            if (Editing) return;

            // An unsaved language preview goes back to the saved language.
            if (Loc.CurrentCode != m_SavedLanguage) Loc.SetLanguage(m_SavedLanguage);
            SceneManager.LoadScene(settingsSceneName);
        }

        private void ClearStatus()
        {
            if (m_StatusLabel != null) m_StatusLabel.text = "";
        }
    }
}
