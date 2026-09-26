using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Audio Settings screen, reached from the Settings scene's Audio button. Same small-centered-
    /// logo look as ChangeKeybindingsController (top:20, left:50%, translate -50%,0, 22%/22% box) rather
    /// than the other GameFlow screens' full 99%/99% logo, since - like Change Keybindings - this screen
    /// needs room below the logo for its own content: here, four volume sliders instead of a rebind list.
    ///
    /// Four sliders - Master Volume, Music, Sound Effects, Voice - each a SliderInt from 0 to 10
    /// (inclusive), giving exactly the 11 notches (0%, 10%, 20%, ... 100%) the spec calls for by
    /// construction, with the displayed percentage simply being the slider's integer value times 10.
    ///
    /// EDIT MODE: a slider being focused ("selected") is NOT the same as it being adjustable - Submit
    /// (A/gamepad East, Enter, or a mouse click) on a focused slider puts it into "edit mode"
    /// (<see cref="m_EditingSlider"/>), and only while a slider is being edited do Left/Right actually
    /// change its value. This is done explicitly by OnSliderNavigationMove (one notch per press, clamped to
    /// 0/10) rather than by leaning on UI Toolkit's own built-in slider scrub behavior - see that method's
    /// doc comment for why. Mouse dragging is unaffected either way (a separate PointerDown-based code
    /// path). The slider never loses focus while being edited, even at 0%/100% where Left/Right have
    /// nothing further to move to. Submit again while editing commits the value and exits edit mode; Cancel
    /// (B/gamepad South, Esc) while editing discards the change and reverts to whatever the value was right
    /// before editing started, then exits edit mode.
    ///
    /// Up/Down never use UI Toolkit's own automatic nearest-neighbor navigation, in or out of edit mode -
    /// PreventDefault always runs for them (see OnSliderNavigationMove), the same "don't trust the
    /// automatic navigation, override it explicitly" lesson ChangeKeybindingsController's own cross-column
    /// fix already established for Left/Right there. While editing, Up/Down are fully suppressed with no
    /// effect at all, so a stray navigation press can't silently abandon an in-progress edit without an
    /// explicit commit or cancel. While NOT editing, Up/Down still move focus, but to the previous/next
    /// slider in <see cref="m_SlidersInOrder"/> explicitly (Master/Music/SoundEffects/Voice, clamped at
    /// each end - no wraparound) rather than whatever the automatic navigation would have picked.
    ///
    /// Outside edit mode, Left/Right on a focused slider are the ONLY thing that leave the slider column
    /// entirely - Left is inert (there's nothing to scrub without first entering edit mode) and Right
    /// always jumps to Save Changes (see "CROSS-CONTROL NAVIGATION" below).
    ///
    /// Values stack multiplicatively, not additively: a played sound's actual volume is
    /// Master% * Category% (e.g. Master 50%, Music 20% plays music at 10% - .5 * .2) - see
    /// AudioVolumeService.GetMultiplier for where that math actually lives. This screen only edits an
    /// in-memory AudioSettingsData working copy and calls AudioVolumeService.ApplyLive after every
    /// committed change so the effect is audible immediately (the same "live now, only permanent on Save"
    /// pattern ChangeKeybindingsController uses for real gameplay rebinds), and only writes it into
    /// keybindings.json - the same config file Change Keybindings uses, via the existing
    /// InputBindingsStore/InputBindingsData.Audio field - when Save Changes is clicked.
    ///
    /// Restore Defaults (above Save Changes, above Back - same order as Change Keybindings) resets all
    /// four sliders back to 100% at once, live-applies that, and refreshes the slider visuals. It does
    /// NOT save by itself - same "still just a working change until Save Changes" convention as every
    /// other settings screen in this project, and Back discards any unsaved change the same way too.
    ///
    /// CROSS-CONTROL NAVIGATION: the sliders sit in the middle of the screen and the action-button column
    /// is anchored to the bottom-right corner - same misalignment ChangeKeybindingsController solves for
    /// its row list vs its action column, so neither column ever relies on UI Toolkit's automatic
    /// nearest-neighbor navigation for ANY direction, not just the crossing between them:
    ///   - Sliders (not editing): Up/Down move explicitly along Master/Music/SoundEffects/Voice (see
    ///     OnSliderNavigationMove); Left is inert; Right always crosses to Save Changes.
    ///   - Action buttons: Up/Down move explicitly along Restore Defaults/Save Changes/Back (see
    ///     OnActionNavigationMove); Left always crosses back to the Master Volume slider; Right is
    ///     inert.
    /// Same SuppressDefaultNavigation (PreventDefault + StopPropagation) mechanism throughout - see
    /// ChangeKeybindingsController for why StopPropagation alone isn't enough to override UI Toolkit's
    /// automatic navigation.
    ///
    /// NAVIGATION EVENT TARGETS: every Submit/Cancel/Move handler on this screen takes its slider or
    /// button as an explicit parameter, captured by closure at registration time (see AddSliderRow and
    /// SetupCrossColumnNavigation), rather than inferring "which element" from evt.target. This is load-
    /// bearing for the sliders specifically: SliderInt is a composite control, and evt.target for a
    /// NavigationMoveEvent resolves to an internal leaf element rather than the outer SliderInt, so casting
    /// it throws InvalidCastException (confirmed on-device with a gamepad d-pad and stick). Registering per-
    /// element closures sidesteps that regardless of exactly which internal element evt.target turns out to
    /// be, for any event type.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class AudioSettingsController : MonoBehaviour
    {
        [Tooltip("Same logo as the other GameFlow screens - wire this up to the same texture.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back is clicked. This scene's only entry point is Settings, so Back returns there rather than the main menu.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when a slider notch changes, entering/confirming a slider edit, or Restore Defaults/Save Changes is selected.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Back is selected, or a slider edit is cancelled.")]
        [SerializeField] private AudioClip cancelSound;

        private AudioSettingsData m_Settings;

        private SliderInt m_MasterSlider;
        private SliderInt m_MusicSlider;
        private SliderInt m_SoundEffectsSlider;
        private SliderInt m_VoiceSlider;

        private readonly Dictionary<SliderInt, Label> m_SliderValueLabels = new Dictionary<SliderInt, Label>();
        private readonly Dictionary<SliderInt, System.Action<int>> m_SliderOnChanged = new Dictionary<SliderInt, System.Action<int>>();

        /// <summary>Fixed Up/Down order the sliders navigate in while not editing - see OnSliderNavigationMove. Populated once all four exist, at the end of BuildUI.</summary>
        private SliderInt[] m_SlidersInOrder;

        /// <summary>The slider currently in edit mode, or null if none - see the class summary's "EDIT MODE" section.</summary>
        private SliderInt m_EditingSlider;

        /// <summary>The value m_EditingSlider had right before entering edit mode, restored if the edit is cancelled.</summary>
        private int m_ValueBeforeEdit;

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

            // A working copy, not a reference to AudioVolumeService.Current directly - mirrors
            // ChangeKeybindingsController's m_Bindings pattern, so Back without saving discards whatever
            // was changed this visit. Live-applied immediately so this screen's own click sounds (and
            // anything else already playing) reflect the saved settings the instant it opens, rather than
            // AudioSettingsData's raw field defaults.
            m_Settings = CloneSettings(InputBindingsStore.Load().Audio);
            AudioVolumeService.ApplyLive(m_Settings);

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(root);
        }

        private static AudioSettingsData CloneSettings(AudioSettingsData source)
        {
            return new AudioSettingsData
            {
                MasterVolume = source.MasterVolume,
                MusicVolume = source.MusicVolume,
                SoundEffectsVolume = source.SoundEffectsVolume,
                VoiceVolume = source.VoiceVolume
            };
        }

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;

            if (titleImage != null)
            {
                var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
                // Same small top-center logo treatment as ChangeKeybindingsController - see that class's
                // BuildUI for why this box (rather than the other screens' 99%/99%) is what produces it.
                title.style.position = Position.Absolute;
                title.style.top = 20;
                title.style.left = Length.Percent(50);
                title.style.translate = new Translate(Length.Percent(-50), 0);
                title.style.width = Length.Percent(22);
                title.style.height = Length.Percent(22);
                root.Add(title);
            }

            // Slider column, centered in the screen - there's no long list to fit here the way Change
            // Keybindings has, so simple absolute-centering is enough.
            var sliderColumn = new VisualElement();
            sliderColumn.style.position = Position.Absolute;
            sliderColumn.style.left = Length.Percent(50);
            sliderColumn.style.top = Length.Percent(50);
            sliderColumn.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            sliderColumn.style.flexDirection = FlexDirection.Column;
            sliderColumn.style.width = 420;
            root.Add(sliderColumn);

            m_MasterSlider = AddSliderRow(sliderColumn, "MASTER VOLUME", m_Settings.MasterVolume, v => { m_Settings.MasterVolume = v; LiveApply(); });
            m_MusicSlider = AddSliderRow(sliderColumn, "MUSIC", m_Settings.MusicVolume, v => { m_Settings.MusicVolume = v; LiveApply(); });
            m_SoundEffectsSlider = AddSliderRow(sliderColumn, "SOUND EFFECTS", m_Settings.SoundEffectsVolume, v => { m_Settings.SoundEffectsVolume = v; LiveApply(); });
            m_VoiceSlider = AddSliderRow(sliderColumn, "VOICE", m_Settings.VoiceVolume, v => { m_Settings.VoiceVolume = v; LiveApply(); });
            m_SlidersInOrder = new[] { m_MasterSlider, m_MusicSlider, m_SoundEffectsSlider, m_VoiceSlider };

            // Same absolutely-positioned bottom-right column as the other GameFlow screens, with Restore
            // Defaults and Save Changes above Back - identical order to ChangeKeybindingsController.
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

            // See MainMenuController.BuildUI for why this is needed: nothing has focus by default, and
            // gamepad Move/Submit only act on whatever's currently focused.
            m_MasterSlider.Focus();
            SetFocusedVisual(m_MasterSlider, true);
        }

        /// <summary>
        /// Builds one Label + SliderInt(0-10) + percent-value-Label row, registers it for edit-mode
        /// Submit/Cancel handling (see OnSliderSubmit/OnSliderCancel) and the Right-to-Save-Changes
        /// override (see OnSliderNavigationMove), and records onChanged/its value Label so
        /// ExitEditMode(commit: false) can revert both the underlying settings field and the on-screen
        /// text without needing a big per-slider switch.
        /// </summary>
        private SliderInt AddSliderRow(VisualElement parent, string label, int initialValue, System.Action<int> onChanged)
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

            var slider = new SliderInt { lowValue = 0, highValue = 10, value = initialValue };
            slider.style.width = 180;
            SetFocusedVisual(slider, false);
            // Focus is always established before a Submit can put a slider into edit mode, and
            // EnterEditMode/ExitEditMode apply their own EditingBackground/FocusedBackground override
            // right afterward - so these two only ever need to handle the plain focused/unfocused cases.
            slider.RegisterCallback<FocusInEvent>(_ => SetFocusedVisual(slider, true));
            slider.RegisterCallback<FocusOutEvent>(_ => SetFocusedVisual(slider, false));
            // Registered as closures over this exact slider, rather than a single shared handler that
            // infers "which slider" from evt.target - see OnSliderNavigationMove's doc comment for why
            // evt.target can't be trusted to be the SliderInt itself for a NavigationMoveEvent.
            slider.RegisterCallback<NavigationSubmitEvent>(evt => OnSliderSubmit(slider, evt));
            slider.RegisterCallback<NavigationCancelEvent>(evt => OnSliderCancel(slider, evt));
            // TrickleDown (capture phase), not the default bubble phase - see OnSliderNavigationMove's doc
            // comment for why this is required to actually suppress the slider's own built-in Left/Right
            // scrub while not editing, rather than merely reacting after it already happened.
            slider.RegisterCallback<NavigationMoveEvent>(evt => OnSliderNavigationMove(slider, evt), TrickleDown.TrickleDown);
            row.Add(slider);

            var valueLabel = new Label(PercentText(initialValue));
            valueLabel.style.color = Color.white;
            valueLabel.style.fontSize = 16;
            valueLabel.style.width = 60;
            valueLabel.style.marginLeft = 10;
            row.Add(valueLabel);

            m_SliderValueLabels[slider] = valueLabel;
            m_SliderOnChanged[slider] = onChanged;

            slider.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue == evt.previousValue) return;

                valueLabel.text = PercentText(evt.newValue);
                onChanged(evt.newValue);
            });

            parent.Add(row);
            return slider;
        }

        private static string PercentText(int notches) => $"{notches * 10}%";

        private void LiveApply() => AudioVolumeService.ApplyLive(m_Settings);

        /// <summary>
        /// Submit (A) on a focused slider enters edit mode; Submit again while already editing it commits
        /// the value and exits. Takes the slider explicitly (registered as a per-slider closure in
        /// AddSliderRow) rather than reading evt.target - see OnSliderNavigationMove's doc comment for why.
        /// </summary>
        private void OnSliderSubmit(SliderInt slider, NavigationSubmitEvent evt)
        {
            if (m_EditingSlider == slider)
            {
                ExitEditMode(commit: true);
            }
            else if (m_EditingSlider == null)
            {
                EnterEditMode(slider);
            }

            evt.PreventDefault();
            evt.StopPropagation();
        }

        /// <summary>
        /// Cancel (B) while a slider is being edited discards the change and reverts to its pre-edit value;
        /// a no-op if that slider isn't the one being edited. Takes the slider explicitly (registered as a
        /// per-slider closure in AddSliderRow) rather than reading evt.target - see
        /// OnSliderNavigationMove's doc comment for why.
        /// </summary>
        private void OnSliderCancel(SliderInt slider, NavigationCancelEvent evt)
        {
            if (m_EditingSlider != slider) return;

            ExitEditMode(commit: false);
            evt.PreventDefault();
            evt.StopPropagation();
        }

        private void EnterEditMode(SliderInt slider)
        {
            m_EditingSlider = slider;
            m_ValueBeforeEdit = slider.value;
            SetEditingVisual(slider, true);

            if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }
        }

        private void ExitEditMode(bool commit)
        {
            if (m_EditingSlider == null) return;

            SliderInt slider = m_EditingSlider;
            m_EditingSlider = null;
            SetEditingVisual(slider, false);

            if (!commit)
            {
                // Reverts both the visual and the underlying value - RegisterValueChangedCallback already
                // applied (and live-applied, via AudioVolumeService) every intermediate notch while
                // scrubbing, so re-running the same onChanged with the pre-edit value undoes that, and
                // SetValueWithoutNotify moves the slider's own visual back without re-firing that callback
                // a second time.
                slider.SetValueWithoutNotify(m_ValueBeforeEdit);
                m_SliderValueLabels[slider].text = PercentText(m_ValueBeforeEdit);
                m_SliderOnChanged[slider](m_ValueBeforeEdit);

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
        /// nearest-neighbor navigation is never allowed to run for them on this screen (see the class
        /// summary). While editing, that's the whole story - Up/Down have no effect at all, only
        /// suppression. While NOT editing, Up/Down still move focus, just to the explicit previous/next
        /// slider in <see cref="m_SlidersInOrder"/> instead. Left/Right: while editing, both are left
        /// completely alone (UI Toolkit's own default action scrubs the slider, and it never loses focus
        /// even at 0/10 where there's nothing further to move to); while NOT editing, Right always leaves
        /// the slider column for Save Changes (see "CROSS-CONTROL NAVIGATION") and Left is suppressed with
        /// no effect (there's nothing to scrub without first entering edit mode via Submit).
        ///
        /// Takes the slider explicitly (registered as a per-slider closure in AddSliderRow) instead of
        /// reading it off evt.target: SliderInt is a composite control (BaseSlider/BaseField wrap an inner
        /// "drag container" element), and for a NavigationMoveEvent specifically, evt.target resolves to
        /// that inner leaf element rather than the outer SliderInt itself - casting it to SliderInt throws
        /// InvalidCastException (confirmed on device, both d-pad and stick). NavigationSubmitEvent's
        /// evt.target apparently does resolve to the outer SliderInt (Submit/A worked before this fix), but
        /// relying on that distinction was fragile, so every slider handler now takes its element as an
        /// explicit parameter instead of inferring it from the event at all.
        ///
        /// Registered with TrickleDown (capture phase) rather than the default bubble phase - see
        /// AddSliderRow - so this runs as early as possible, before the event can reach whatever internal
        /// element actually implements the slider's own built-in Left/Right scrub-the-value behavior.
        ///
        /// This method no longer leans on that built-in behavior at all, in either state - it used to leave
        /// Left/Right alone while editing and let UI Toolkit's own default action handle the scrub, but that
        /// proved unreliable specifically for gamepad/keyboard input once this handler moved to the capture
        /// phase (mouse dragging is a wholly separate PointerDown-based code path and was never affected
        /// either way): the built-in scrub stopped firing for a NavigationMoveEvent at all once something
        /// upstream in this same event's capture pass got involved, in a way that isn't practical to fully
        /// pin down or rely on without being able to run the game and inspect it directly. Rather than
        /// depend further on exactly when/whether that internal behavior runs, Left/Right while editing now
        /// adjust slider.value directly by one notch (clamped to lowValue/highValue - this is also what
        /// naturally satisfies "doesn't go past max/min"), which fires the same RegisterValueChangedCallback
        /// a mouse drag would have, so the label/onChanged/LiveApply path is identical either way. Every
        /// direction now unconditionally calls SuppressDefaultNavigation, editing or not, so the framework's
        /// own default handling is never given a chance to run at all.
        /// </summary>
        private void OnSliderNavigationMove(SliderInt slider, NavigationMoveEvent evt)
        {
            bool isEditing = m_EditingSlider == slider;

            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    if (!isEditing) FocusAdjacentSlider(slider, -1);
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Down:
                    if (!isEditing) FocusAdjacentSlider(slider, 1);
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Right:
                    if (isEditing)
                    {
                        slider.value = Mathf.Min(slider.highValue, slider.value + 1);
                    }
                    else
                    {
                        m_SaveButton.Focus();
                    }
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Left:
                    if (isEditing)
                    {
                        slider.value = Mathf.Max(slider.lowValue, slider.value - 1);
                    }
                    // else: inert while not editing - nothing to scrub without entering edit mode first.
                    SuppressDefaultNavigation(evt);
                    break;
            }
        }

        /// <summary>Moves focus to the previous (step -1) or next (step 1) slider in m_SlidersInOrder, or does nothing if already at that end - no wraparound.</summary>
        private void FocusAdjacentSlider(SliderInt from, int step)
        {
            int index = System.Array.IndexOf(m_SlidersInOrder, from);
            if (index < 0) return;

            int next = index + step;
            if (next < 0 || next >= m_SlidersInOrder.Length) return;

            m_SlidersInOrder[next].Focus();
        }

        /// <summary>
        /// Wires the action buttons' own navigation - see OnActionNavigationMove. Named to match its old
        /// purpose, but now covers all four directions on the button column, not just the
        /// Left-to-Master-Volume crossing. Registered as per-button closures (matching the sliders' own
        /// AddSliderRow registration) rather than one shared handler reading evt.target - Button isn't
        /// known to have the same composite-leaf-element issue SliderInt does, but the closure form costs
        /// nothing and keeps every navigation handler on this screen following the same, now-proven-safe
        /// pattern instead of leaving one still trusting evt.target's exact identity.
        /// </summary>
        private void SetupCrossColumnNavigation()
        {
            m_RestoreDefaultsButton.RegisterCallback<NavigationMoveEvent>(evt => OnActionNavigationMove(m_RestoreDefaultsButton, evt));
            m_SaveButton.RegisterCallback<NavigationMoveEvent>(evt => OnActionNavigationMove(m_SaveButton, evt));
            m_BackButton.RegisterCallback<NavigationMoveEvent>(evt => OnActionNavigationMove(m_BackButton, evt));
        }

        /// <summary>
        /// Same "never trust the automatic navigation, override it explicitly" treatment as the sliders
        /// (see OnSliderNavigationMove) - Up/Down cycle through m_ButtonsInOrder explicitly instead of
        /// relying on UI Toolkit's automatic nearest-neighbor navigation between the three buttons; Left
        /// always crosses back to the Master Volume slider (see the class summary's "CROSS-CONTROL
        /// NAVIGATION" section); Right is inert - there's nothing to the right of the action column.
        /// Takes the button explicitly (see SetupCrossColumnNavigation) rather than reading evt.target.
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
                    m_MasterSlider.Focus();
                    SuppressDefaultNavigation(evt);
                    break;

                case NavigationMoveEvent.Direction.Right:
                    SuppressDefaultNavigation(evt);
                    break;
            }
        }

        /// <summary>Moves focus to the previous (step -1) or next (step 1) button in m_ButtonsInOrder, or does nothing if already at that end - no wraparound. Same pattern as FocusAdjacentSlider.</summary>
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

        // Same grayed-out/bright-pill focus treatment as the other GameFlow screens, plus a distinct
        // warm color while a slider is being edited - same AwaitingBackground/AwaitingText warm-orange
        // ChangeKeybindingsController uses for its own in-progress rebind capture, for visual consistency
        // between the two settings screens' "actively capturing input" states.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color FocusedText = Color.black;
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color EditingBackground = new Color(0.85f, 0.55f, 0.15f);

        /// <summary>Background-only focus highlight, used for the sliders (their own value text stays white regardless of focus).</summary>
        private static void SetFocusedVisual(VisualElement element, bool focused)
        {
            element.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
        }

        /// <summary>Overrides the normal focus highlight with the warm "actively editing" color while true, or restores the plain focused highlight (the slider is still focused, just no longer being edited) while false.</summary>
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

        /// <summary>Same click-sound wrapper as ChangeKeybindingsController.MakeActionButton, routed through AudioVolumeService so this screen's own button sounds are affected by the sliders being adjusted on it.</summary>
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
            if (m_EditingSlider != null) return; // don't reset mid-edit, see class summary

            m_Settings = new AudioSettingsData();
            LiveApply();
            RefreshSliderVisuals();
        }

        /// <summary>Refreshes every slider's position and value label from m_Settings without re-firing RegisterValueChangedCallback (SetValueWithoutNotify) - needed after Restore Defaults changes values without going through the usual per-slider edit flow.</summary>
        private void RefreshSliderVisuals()
        {
            m_MasterSlider.SetValueWithoutNotify(m_Settings.MasterVolume);
            m_MusicSlider.SetValueWithoutNotify(m_Settings.MusicVolume);
            m_SoundEffectsSlider.SetValueWithoutNotify(m_Settings.SoundEffectsVolume);
            m_VoiceSlider.SetValueWithoutNotify(m_Settings.VoiceVolume);

            m_SliderValueLabels[m_MasterSlider].text = PercentText(m_Settings.MasterVolume);
            m_SliderValueLabels[m_MusicSlider].text = PercentText(m_Settings.MusicVolume);
            m_SliderValueLabels[m_SoundEffectsSlider].text = PercentText(m_Settings.SoundEffectsVolume);
            m_SliderValueLabels[m_VoiceSlider].text = PercentText(m_Settings.VoiceVolume);
        }

        private void OnSaveChangesClicked()
        {
            if (m_EditingSlider != null) return; // don't save mid-edit, see class summary

            // Loads the current on-disk data (rather than just writing a lone AudioSettingsData) so
            // saving Audio here can never clobber Keyboard/Gamepad bindings saved from the Change
            // Keybindings screen, or vice versa - both screens share the same keybindings.json file.
            InputBindingsData data = InputBindingsStore.Load();
            data.Audio = CloneSettings(m_Settings);
            InputBindingsStore.Save(data);
        }

        private void OnBackClicked()
        {
            if (m_EditingSlider != null) return; // don't leave mid-edit, see class summary
            SceneManager.LoadScene(settingsSceneName);
        }
    }
}
