using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Graphics Settings screen, reached from the Settings scene's Graphics button. Same small-
    /// centered-logo look as AudioSettingsController/ChangeKeybindingsController (top:20, left:50%,
    /// translate -50%,0, 22%/22% box) rather than the other GameFlow screens' full 99%/99% logo, for the
    /// same reason: this screen needs room below the logo for its own content, here four dropdown rows
    /// instead of sliders or a rebind list.
    ///
    /// Four dropdowns - Resolution, Frame Rate, VSync, Windowed Mode - each a UI Toolkit DropdownField
    /// over a fixed, hand-authored choice list (see the ResolutionOptions/FrameRateOptions/VSync.../
    /// Windowed... constants below), rather than reading the platform's actual supported-resolution list -
    /// the spec calls out four exact resolutions and two named options per bool setting, not "whatever this
    /// machine supports".
    ///
    /// EDIT MODE: same focused-vs-adjustable split as AudioSettingsController's sliders - Submit (A/gamepad
    /// East, Enter, or a mouse click) on a focused dropdown puts it into "edit mode"
    /// (<see cref="m_EditingDropdown"/>), and only while a dropdown is being edited does Up/Down actually
    /// cycle its selected option. This deliberately mirrors the Audio screen's edit-mode concept but swaps
    /// which axis does the adjusting: the Audio sliders used Left/Right to scrub (a single continuous
    /// value) and always blocked Up/Down outright; here Up/Down cycle through a discrete option list while
    /// editing, and Left/Right are inert in every state - there's nothing for them to do to a dropdown, and
    /// the spec calls this out explicitly ("right and left shouldn't do anything while in edit mode").
    /// Submit again while editing commits the value and exits edit mode; Cancel (B/gamepad South, Esc)
    /// while editing discards the change and reverts to whatever option was selected right before editing
    /// started, then exits edit mode. A dropdown never loses focus while being edited. Unlike the Audio
    /// sliders' 0%/100% ends (a continuous range, clamped with no wraparound), Up/Down while editing here
    /// loop around at either end - Down past the last option wraps to the first and vice versa - since a
    /// dropdown's choices aren't a min/max range, just a cycle of otherwise-equal options (see
    /// CycleDropdown).
    ///
    /// This screen deliberately never opens DropdownField's own native popup menu for gamepad/keyboard
    /// input - Submit/Cancel/Move are all intercepted and handled explicitly (see OnDropdownSubmit/
    /// OnDropdownCancel/OnDropdownNavigationMove) rather than letting UI Toolkit's built-in dropdown
    /// interaction run, for the same reason AudioSettingsController stopped trusting SliderInt's own
    /// built-in Left/Right scrub: DropdownField is also a composite BaseField-derived control, so its
    /// evt.target for a NavigationMoveEvent can't be assumed to be the DropdownField itself either (every
    /// handler here takes its dropdown as an explicit closure parameter, never reads evt.target, for exactly
    /// that reason - see AddDropdownRow), and its own default popup isn't gamepad-navigable in a way this
    /// spec's cycling model needs anyway. A mouse click is unaffected by any of this - DropdownField's own
    /// PointerDown-driven popup is a wholly separate code path (same reason mouse-dragging a slider was
    /// never affected by that screen's own Move-event interception), so clicking a dropdown still opens its
    /// real native option list, and choosing an option there still fires the same
    /// RegisterValueChangedCallback this class hooks for the gamepad/keyboard path.
    ///
    /// Up/Down never use UI Toolkit's own automatic nearest-neighbor navigation, in or out of edit mode -
    /// PreventDefault always runs for them (see OnDropdownNavigationMove), the same "don't trust the
    /// automatic navigation, override it explicitly" lesson every GameFlow settings screen in this project
    /// already established. While NOT editing, Up/Down move focus explicitly to the previous/next dropdown
    /// in <see cref="m_DropdownsInOrder"/> (Resolution/Frame Rate/VSync/Windowed Mode, clamped at each end -
    /// no wraparound), rather than whatever the automatic navigation would have picked.
    ///
    /// Outside edit mode, Right on a focused dropdown is the only thing that leaves the dropdown column -
    /// it always jumps to Save Changes (see "CROSS-CONTROL NAVIGATION" below); Left is inert in every state.
    ///
    /// Values are applied live immediately via GraphicsSettingsService.ApplyLive after every committed
    /// change (the same "live now, only permanent on Save" pattern the Audio screen uses), and only written
    /// into settings.json - the same config file Change Keybindings and Audio Settings use, via the
    /// existing InputBindingsStore/InputBindingsData.Graphics field - when Save Changes is clicked.
    ///
    /// Restore Defaults (above Save Changes, above Back - same order as every other settings screen) resets
    /// all four dropdowns back to their spec defaults (1920 x 1080, 60 FPS, VSync Off, Full Screen) at once,
    /// live-applies that, and refreshes the dropdown visuals. It does NOT save by itself - same "still just
    /// a working change until Save Changes" convention as every other settings screen, and Back discards
    /// any unsaved change the same way too.
    ///
    /// CROSS-CONTROL NAVIGATION: the dropdowns sit in the middle of the screen and the action-button column
    /// is anchored to the bottom-right corner - same misalignment every other GameFlow settings screen
    /// solves for, so neither column ever relies on UI Toolkit's automatic nearest-neighbor navigation for
    /// ANY direction, not just the crossing between them:
    ///   - Dropdowns (not editing): Up/Down move explicitly along Resolution/Frame Rate/VSync/Windowed Mode
    ///     (see OnDropdownNavigationMove); Left is inert; Right always crosses to Save Changes.
    ///   - Action buttons: Up/Down move explicitly along Restore Defaults/Save Changes/Back (see
    ///     OnActionNavigationMove); Left always crosses back to the Resolution dropdown; Right is inert.
    /// Same SuppressDefaultNavigation (PreventDefault + StopPropagation) mechanism throughout.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class GraphicsSettingsController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens - wire this up to the same texture.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back is clicked. This scene's only entry point is Settings, so Back returns there rather than the main menu.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when a dropdown option changes, entering/confirming a dropdown edit, or Restore Defaults/Save Changes is selected.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Back is selected, or a dropdown edit is cancelled.")]
        [SerializeField] private AudioClip cancelSound;

        // Fixed choice lists, in the exact order the spec lists them. Index 0 in each array is what index
        // 0 in the matching DropdownField's choices means - every Index...FromSettings/onChanged pair
        // below has to agree with this ordering.
        private static readonly (int Width, int Height)[] ResolutionOptions =
        {
            (1280, 720),
            (1920, 1080),
            (2560, 1440),
            (3840, 2160),
        };
        private const int DefaultResolutionIndex = 1; // 1920 x 1080

        private static readonly int[] FrameRateOptions = { 30, 60, 120, 144, 165, 240 };
        private const int DefaultFrameRateIndex = 1; // 60

        // Index 0 = VSync On, index 1 = VSync Off - matches the spec's own stated order, with Off (index 1) the default.
        private const int DefaultVSyncIndex = 1;

        // Index 0 = Full Screen, index 1 = Windowed - matches the spec's own stated order, with Full Screen (index 0) the default.
        private const int DefaultWindowedIndex = 0;

        private GraphicsSettingsData m_Settings;

        private DropdownField m_ResolutionDropdown;
        private DropdownField m_FrameRateDropdown;
        private DropdownField m_VSyncDropdown;
        private DropdownField m_WindowedDropdown;

        private readonly Dictionary<DropdownField, System.Action<int>> m_DropdownOnChanged = new Dictionary<DropdownField, System.Action<int>>();

        /// <summary>Fixed Up/Down order the dropdowns navigate in while not editing - see OnDropdownNavigationMove. Populated once all four exist, at the end of BuildUI.</summary>
        private DropdownField[] m_DropdownsInOrder;

        /// <summary>The dropdown currently in edit mode, or null if none - see the class summary's "EDIT MODE" section.</summary>
        private DropdownField m_EditingDropdown;

        /// <summary>The index m_EditingDropdown had right before entering edit mode, restored if the edit is cancelled.</summary>
        private int m_IndexBeforeEdit;

        private Button m_RestoreDefaultsButton;
        private Button m_SaveButton;
        private Button m_BackButton;

        /// <summary>Fixed Up/Down order the action buttons navigate in - see OnActionNavigationMove. Populated once all three exist, at the end of BuildUI.</summary>
        private Button[] m_ButtonsInOrder;

        private void Awake()
        {
            // Same gamepad Submit/Cancel fix every GameFlow screen with an EventSystem applies - see
            // GamepadUIBindingFix.
            GamepadUIBindingFix.Apply();

            // A working copy, not a reference to GraphicsSettingsService.Current directly - mirrors
            // AudioSettingsController's m_Settings pattern, so Back without saving discards whatever was
            // changed this visit. Live-applied immediately so this screen reflects (and edits from) the
            // actually-saved settings rather than GraphicsSettingsData's raw field defaults.
            m_Settings = CloneSettings(InputBindingsStore.Load().Graphics);
            GraphicsSettingsService.ApplyLive(m_Settings);

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(root);
        }

        private static GraphicsSettingsData CloneSettings(GraphicsSettingsData source)
        {
            return new GraphicsSettingsData
            {
                ResolutionWidth = source.ResolutionWidth,
                ResolutionHeight = source.ResolutionHeight,
                FrameRate = source.FrameRate,
                VSyncEnabled = source.VSyncEnabled,
                Fullscreen = source.Fullscreen
            };
        }

        // ----- index <-> settings mapping -----

        private static int ResolutionIndexFromSettings(GraphicsSettingsData settings)
        {
            for (int i = 0; i < ResolutionOptions.Length; i++)
            {
                if (ResolutionOptions[i].Width == settings.ResolutionWidth && ResolutionOptions[i].Height == settings.ResolutionHeight)
                {
                    return i;
                }
            }
            return DefaultResolutionIndex; // saved value doesn't match any known option (e.g. old/edited JSON) - fall back rather than throw
        }

        private static int FrameRateIndexFromSettings(GraphicsSettingsData settings)
        {
            for (int i = 0; i < FrameRateOptions.Length; i++)
            {
                if (FrameRateOptions[i] == settings.FrameRate)
                {
                    return i;
                }
            }
            return DefaultFrameRateIndex;
        }

        private static int VSyncIndexFromSettings(GraphicsSettingsData settings) => settings.VSyncEnabled ? 0 : 1;

        private static int WindowedIndexFromSettings(GraphicsSettingsData settings) => settings.Fullscreen ? 0 : 1;

        private static List<string> BuildResolutionChoices()
        {
            var choices = new List<string>(ResolutionOptions.Length);
            foreach ((int width, int height) in ResolutionOptions)
            {
                choices.Add($"{width} x {height}");
            }
            return choices;
        }

        private static List<string> BuildFrameRateChoices()
        {
            var choices = new List<string>(FrameRateOptions.Length);
            foreach (int fps in FrameRateOptions)
            {
                choices.Add($"{fps} FPS");
            }
            return choices;
        }

        private static List<string> VSyncChoices => new List<string> { "VSync On", "VSync Off" };
        private static List<string> WindowedChoices => new List<string> { "Full Screen", "Windowed" };

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;

            if (titleImage != null)
            {
                var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
                // Same small top-center logo treatment as AudioSettingsController/ChangeKeybindingsController.
                title.style.position = Position.Absolute;
                title.style.top = 20;
                title.style.left = Length.Percent(50);
                title.style.translate = new Translate(Length.Percent(-50), 0);
                title.style.width = Length.Percent(22);
                title.style.height = Length.Percent(22);
                root.Add(title);
            }

            // Dropdown column, centered in the screen - same simple absolute-centering as the Audio
            // screen's slider column.
            var dropdownColumn = new VisualElement();
            dropdownColumn.style.position = Position.Absolute;
            dropdownColumn.style.left = Length.Percent(50);
            dropdownColumn.style.top = Length.Percent(50);
            dropdownColumn.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            dropdownColumn.style.flexDirection = FlexDirection.Column;
            dropdownColumn.style.width = 420;
            root.Add(dropdownColumn);

            m_ResolutionDropdown = AddDropdownRow(dropdownColumn, "RESOLUTION", BuildResolutionChoices(), ResolutionIndexFromSettings(m_Settings), idx =>
            {
                (int width, int height) = ResolutionOptions[idx];
                m_Settings.ResolutionWidth = width;
                m_Settings.ResolutionHeight = height;
                LiveApply();
            });
            m_FrameRateDropdown = AddDropdownRow(dropdownColumn, "FRAME RATE", BuildFrameRateChoices(), FrameRateIndexFromSettings(m_Settings), idx =>
            {
                m_Settings.FrameRate = FrameRateOptions[idx];
                LiveApply();
            });
            m_VSyncDropdown = AddDropdownRow(dropdownColumn, "VSYNC", VSyncChoices, VSyncIndexFromSettings(m_Settings), idx =>
            {
                m_Settings.VSyncEnabled = (idx == 0);
                LiveApply();
            });
            m_WindowedDropdown = AddDropdownRow(dropdownColumn, "WINDOWED MODE", WindowedChoices, WindowedIndexFromSettings(m_Settings), idx =>
            {
                m_Settings.Fullscreen = (idx == 0);
                LiveApply();
            });
            m_DropdownsInOrder = new[] { m_ResolutionDropdown, m_FrameRateDropdown, m_VSyncDropdown, m_WindowedDropdown };

            // Same absolutely-positioned bottom-right column as every other GameFlow settings screen, with
            // Restore Defaults and Save Changes above Back.
            var buttonColumn = new VisualElement();
            buttonColumn.style.position = Position.Absolute;
            buttonColumn.style.right = 40;
            buttonColumn.style.bottom = 40;
            buttonColumn.style.flexDirection = FlexDirection.Column;
            buttonColumn.style.alignItems = Align.FlexEnd;

            m_RestoreDefaultsButton = MakeActionButton("Restore Defaults", OnRestoreDefaultsClicked, selectSound);
            buttonColumn.Add(m_RestoreDefaultsButton);
            m_SaveButton = MakeActionButton("Save Changes", OnSaveChangesClicked, selectSound);
            buttonColumn.Add(m_SaveButton);
            m_BackButton = MakeActionButton("Back", OnBackClicked, cancelSound);
            buttonColumn.Add(m_BackButton);
            m_ButtonsInOrder = new[] { m_RestoreDefaultsButton, m_SaveButton, m_BackButton };
            root.Add(buttonColumn);

            SetupCrossColumnNavigation();

            // See AudioSettingsController.BuildUI for why this is needed: nothing has focus by default,
            // and gamepad Move/Submit only act on whatever's currently focused. Resolution is the first
            // dropdown, matching Master Volume being the Audio screen's default focus.
            m_ResolutionDropdown.Focus();
            SetFocusedVisual(m_ResolutionDropdown, true);
        }

        /// <summary>
        /// Builds one Label + DropdownField row, registers it for edit-mode Submit/Cancel handling (see
        /// OnDropdownSubmit/OnDropdownCancel) and the Right-to-Save-Changes override (see
        /// OnDropdownNavigationMove), and records onChanged so ExitEditMode(commit: false) can revert the
        /// underlying settings field without needing a big per-dropdown switch. Every handler is registered
        /// as a closure over this exact dropdown, rather than a single shared handler that infers "which
        /// dropdown" from evt.target - see the class summary for why that can't be trusted for a composite
        /// control like DropdownField.
        /// </summary>
        private DropdownField AddDropdownRow(VisualElement parent, string label, List<string> choices, int initialIndex, System.Action<int> onChanged)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 14;

            var nameLabel = new Label(label);
            nameLabel.style.color = Color.white;
            nameLabel.style.fontSize = 16;
            nameLabel.style.width = 160;
            row.Add(nameLabel);

            var dropdown = new DropdownField(choices, initialIndex);
            dropdown.style.width = 240;
            SetFocusedVisual(dropdown, false);
            // Focus is always established before a Submit can put a dropdown into edit mode, and
            // EnterEditMode/ExitEditMode apply their own EditingBackground/FocusedBackground override
            // right afterward - so these two only ever need to handle the plain focused/unfocused cases.
            dropdown.RegisterCallback<FocusInEvent>(_ => SetFocusedVisual(dropdown, true));
            dropdown.RegisterCallback<FocusOutEvent>(_ => SetFocusedVisual(dropdown, false));
            // TrickleDown (capture phase) on all three - not the default bubble phase - is what actually
            // keeps DropdownField's own built-in interaction (most importantly: opening its native popup
            // as ITS OWN default action for a Submit) from running ahead of our explicit handling. A
            // bubble-phase registration runs too late to stop that: like SliderInt in AudioSettingsController,
            // DropdownField is a composite control, and its own default action fires at/near the true
            // internal target before an ancestor's bubble-phase callback ever gets a turn - PreventDefault()
            // called that late doesn't retroactively close a popup that already opened. This was exactly
            // the bug reported after the first Graphics Settings pass (a dropdown would stay stuck showing
            // as "editing" and only seem to respond to a second A press - the popup opened invisibly on the
            // first Submit and silently ate the following Up/Down/Submit presses until it closed). Capture
            // phase runs before that default action fires, so PreventDefault()/StopPropagation() here
            // actually prevents it, the same fix already proven for the sliders' Left/Right scrub.
            dropdown.RegisterCallback<NavigationSubmitEvent>(evt => OnDropdownSubmit(dropdown, evt), TrickleDown.TrickleDown);
            dropdown.RegisterCallback<NavigationCancelEvent>(evt => OnDropdownCancel(dropdown, evt), TrickleDown.TrickleDown);
            dropdown.RegisterCallback<NavigationMoveEvent>(evt => OnDropdownNavigationMove(dropdown, evt), TrickleDown.TrickleDown);
            row.Add(dropdown);

            m_DropdownOnChanged[dropdown] = onChanged;

            dropdown.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;
                onChanged(dropdown.index);
            });

            parent.Add(row);
            return dropdown;
        }

        private void LiveApply() => GraphicsSettingsService.ApplyLive(m_Settings);

        /// <summary>Submit (A) on a focused dropdown enters edit mode; Submit again while already editing it commits the value and exits.</summary>
        private void OnDropdownSubmit(DropdownField dropdown, NavigationSubmitEvent evt)
        {
            if (m_EditingDropdown == dropdown)
            {
                ExitEditMode(commit: true);
            }
            else if (m_EditingDropdown == null)
            {
                EnterEditMode(dropdown);
            }

            evt.PreventDefault();
            evt.StopPropagation();
        }

        /// <summary>Cancel (B) while a dropdown is being edited discards the change and reverts to its pre-edit option; a no-op if that dropdown isn't the one being edited.</summary>
        private void OnDropdownCancel(DropdownField dropdown, NavigationCancelEvent evt)
        {
            if (m_EditingDropdown != dropdown) return;

            ExitEditMode(commit: false);
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void EnterEditMode(DropdownField dropdown)
        {
            m_EditingDropdown = dropdown;
            m_IndexBeforeEdit = dropdown.index;
            SetEditingVisual(dropdown, true);

            if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }
        }

        private void ExitEditMode(bool commit)
        {
            if (m_EditingDropdown == null) return;

            DropdownField dropdown = m_EditingDropdown;
            m_EditingDropdown = null;
            SetEditingVisual(dropdown, false);

            if (!commit)
            {
                // Reverts both the visual and the underlying value - RegisterValueChangedCallback already
                // applied (and live-applied, via GraphicsSettingsService) every intermediate option while
                // cycling, so re-running the same onChanged with the pre-edit index undoes that, and
                // SetValueWithoutNotify moves the dropdown's own displayed text back without re-firing that
                // callback a second time.
                dropdown.SetValueWithoutNotify(dropdown.choices[m_IndexBeforeEdit]);
                m_DropdownOnChanged[dropdown](m_IndexBeforeEdit);

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

        /// <summary>
        /// Up/Down always call SuppressDefaultNavigation, editing or not - UI Toolkit's automatic
        /// nearest-neighbor navigation is never allowed to run for them on this screen. While editing,
        /// Up/Down cycle CycleDropdown one option per press instead of moving focus. While NOT editing,
        /// Up/Down move focus to the previous/next dropdown in <see cref="m_DropdownsInOrder"/> instead.
        /// Left/Right are inert in every state - the spec calls this out explicitly for the editing case
        /// ("right and left shouldn't do anything while in edit mode"), and there's nothing for a dropdown
        /// to scrub the way a slider has a continuous value, so Left stays inert while not editing too;
        /// Right while NOT editing is the one exception, leaving the dropdown column for Save Changes (see
        /// "CROSS-CONTROL NAVIGATION" in the class summary).
        ///
        /// Registered with TrickleDown (capture phase) in AddDropdownRow, not the default bubble phase, so
        /// this runs before the event can reach whatever internal element actually implements
        /// DropdownField's own built-in interaction (opening its native popup on Submit, or any arrow-key
        /// handling it implements itself) - the same defensive reasoning AudioSettingsController's sliders
        /// already established for SliderInt, generalized to another BaseField-derived composite control.
        /// Every direction unconditionally calls SuppressDefaultNavigation, so the framework's own default
        /// handling is never given a chance to run at all, regardless of exactly what that handling turns
        /// out to be.
        ///
        /// Takes the dropdown explicitly (registered as a per-dropdown closure in AddDropdownRow) instead
        /// of reading it off evt.target, for the same reason OnSliderNavigationMove does on the Audio
        /// screen: DropdownField is a composite control, and evt.target for a NavigationMoveEvent isn't
        /// safe to assume is the DropdownField itself.
        /// </summary>
        private void OnDropdownNavigationMove(DropdownField dropdown, NavigationMoveEvent evt)
        {
            bool isEditing = m_EditingDropdown == dropdown;

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    if (isEditing) CycleDropdown(dropdown, -1);
                    else FocusAdjacentDropdown(dropdown, -1);
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Down:
                    if (isEditing) CycleDropdown(dropdown, 1);
                    else FocusAdjacentDropdown(dropdown, 1);
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Right:
                    if (!isEditing)
                    {
                        m_SaveButton.Focus();
                    }
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Left:
                    // Inert in every state - see this method's doc comment.
                    SuppressDefaultNavigation(evt);
                    break;
            }
        }

        /// <summary>
        /// Moves the dropdown's selection by one option (step -1 or 1), wrapping around at either end
        /// (Down past the last option goes to the first, Up past the first goes to the last) - unlike the
        /// Audio sliders' 0%/100% ends, which clamp: a dropdown's options aren't a continuous range with a
        /// true minimum/maximum, they're a cycle of otherwise-equal choices, so looping back around reads
        /// as the natural way to reach the far end quickly instead of pressing the same direction repeatedly.
        /// </summary>
        private static void CycleDropdown(DropdownField dropdown, int step)
        {
            int count = dropdown.choices.Count;
            int newIndex = ((dropdown.index + step) % count + count) % count; // wrap both directions, including the negative case C#'s % doesn't handle on its own
            if (newIndex == dropdown.index) return;

            dropdown.index = newIndex; // fires RegisterValueChangedCallback, same as a native popup selection would
        }

        /// <summary>Moves focus to the previous (step -1) or next (step 1) dropdown in m_DropdownsInOrder, or does nothing if already at that end - no wraparound.</summary>
        private void FocusAdjacentDropdown(DropdownField from, int step)
        {
            int index = System.Array.IndexOf(m_DropdownsInOrder, from);
            if (index < 0) return;

            int next = index + step;
            if (next < 0 || next >= m_DropdownsInOrder.Length) return;

            m_DropdownsInOrder[next].Focus();
        }

        /// <summary>Wires the action buttons' own navigation - see OnActionNavigationMove. Registered as per-button closures, matching AddDropdownRow's own registration, rather than one shared handler reading evt.target.</summary>
        private void SetupCrossColumnNavigation()
        {
            m_RestoreDefaultsButton.RegisterCallback<NavigationMoveEvent>(evt => OnActionNavigationMove(m_RestoreDefaultsButton, evt));
            m_SaveButton.RegisterCallback<NavigationMoveEvent>(evt => OnActionNavigationMove(m_SaveButton, evt));
            m_BackButton.RegisterCallback<NavigationMoveEvent>(evt => OnActionNavigationMove(m_BackButton, evt));
        }

        /// <summary>
        /// Same "never trust the automatic navigation, override it explicitly" treatment as the dropdowns
        /// (see OnDropdownNavigationMove) - Up/Down cycle through m_ButtonsInOrder explicitly instead of
        /// relying on UI Toolkit's automatic nearest-neighbor navigation between the three buttons; Left
        /// always crosses back to the Resolution dropdown (see the class summary's "CROSS-CONTROL
        /// NAVIGATION" section); Right is inert - there's nothing to the right of the action column.
        /// </summary>
        private void OnActionNavigationMove(Button button, NavigationMoveEvent evt)
        {
            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    FocusAdjacentButton(button, -1);
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Down:
                    FocusAdjacentButton(button, 1);
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Left:
                    m_ResolutionDropdown.Focus();
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Right:
                    SuppressDefaultNavigation(evt);
                    break;
            }
        }

        /// <summary>Moves focus to the previous (step -1) or next (step 1) button in m_ButtonsInOrder, or does nothing if already at that end - no wraparound. Same pattern as FocusAdjacentDropdown.</summary>
        private void FocusAdjacentButton(Button from, int step)
        {
            int index = System.Array.IndexOf(m_ButtonsInOrder, from);
            if (index < 0) return;

            int next = index + step;
            if (next < 0 || next >= m_ButtonsInOrder.Length) return;

            m_ButtonsInOrder[next].Focus();
        }

        /// <summary>See ChangeKeybindingsController.SuppressDefaultNavigation - PreventDefault (not just StopPropagation) is what's actually required to override UI Toolkit's own automatic navigation.</summary>
        private static void SuppressDefaultNavigation(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopPropagation();
        }

        // Same grayed-out/bright-pill focus treatment as the other GameFlow screens, plus a distinct warm
        // color while a dropdown is being edited - matching AudioSettingsController's EditingBackground for
        // visual consistency between the two settings screens' "actively capturing input" states.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color FocusedText = Color.black;
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color EditingBackground = new Color(0.85f, 0.55f, 0.15f);

        /// <summary>Background-only focus highlight, used for the dropdowns.</summary>
        private static void SetFocusedVisual(VisualElement element, bool focused)
        {
            element.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
        }

        /// <summary>Overrides the normal focus highlight with the warm "actively editing" color while true, or restores the plain focused highlight (the dropdown is still focused, just no longer being edited) while false.</summary>
        private static void SetEditingVisual(VisualElement element, bool editing)
        {
            element.style.backgroundColor = editing ? EditingBackground : FocusedBackground;
        }

        /// <summary>Background + text-color focus highlight, matching every action button elsewhere in this project.</summary>
        private static void SetButtonFocusedVisual(Button button, bool focused)
        {
            button.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
            button.style.color = focused ? FocusedText : UnfocusedText;
        }

        /// <summary>Same click-sound wrapper as AudioSettingsController.MakeActionButton, routed through AudioVolumeService so this screen's own button sounds are affected by the Audio screen's sliders.</summary>
        private static Button MakeActionButton(string text, System.Action onClick, AudioClip clickSound)
        {
            var button = new Button(() =>
            {
                if (clickSound != null)
                {
                    AudioVolumeService.PlayOneShot(clickSound, AudioCategory.SoundEffects, Vector3.zero);
                }
                onClick();
            })
            { text = text };
            button.style.width = 200;
            button.style.height = 40;
            button.style.marginTop = 8;
            button.style.marginBottom = 8;
            button.style.fontSize = 18;

            SetButtonFocusedVisual(button, false);
            button.RegisterCallback<FocusInEvent>(_ => SetButtonFocusedVisual(button, true));
            button.RegisterCallback<FocusOutEvent>(_ => SetButtonFocusedVisual(button, false));

            return button;
        }

        private void OnRestoreDefaultsClicked()
        {
            if (m_EditingDropdown != null) return; // don't reset mid-edit, see class summary

            m_Settings = new GraphicsSettingsData();
            LiveApply();
            RefreshDropdownVisuals();
        }

        /// <summary>Refreshes every dropdown's displayed option from m_Settings without re-firing RegisterValueChangedCallback (SetValueWithoutNotify) - needed after Restore Defaults changes values without going through the usual per-dropdown edit flow.</summary>
        private void RefreshDropdownVisuals()
        {
            m_ResolutionDropdown.SetValueWithoutNotify(m_ResolutionDropdown.choices[ResolutionIndexFromSettings(m_Settings)]);
            m_FrameRateDropdown.SetValueWithoutNotify(m_FrameRateDropdown.choices[FrameRateIndexFromSettings(m_Settings)]);
            m_VSyncDropdown.SetValueWithoutNotify(m_VSyncDropdown.choices[VSyncIndexFromSettings(m_Settings)]);
            m_WindowedDropdown.SetValueWithoutNotify(m_WindowedDropdown.choices[WindowedIndexFromSettings(m_Settings)]);
        }

        private void OnSaveChangesClicked()
        {
            if (m_EditingDropdown != null) return; // don't save mid-edit, see class summary

            // Loads the current on-disk data (rather than just writing a lone GraphicsSettingsData) so
            // saving Graphics here can never clobber Keyboard/Gamepad/Audio settings saved from the other
            // settings screens, or vice versa - all of them share the same settings.json file.
            InputBindingsData data = InputBindingsStore.Load();
            data.Graphics = CloneSettings(m_Settings);
            InputBindingsStore.Save(data);
        }

        private void OnBackClicked()
        {
            if (m_EditingDropdown != null) return; // don't leave mid-edit, see class summary
            SceneManager.LoadScene(settingsSceneName);
        }
    }
}
