using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Escape menu in a match: Settings (not hooked up yet), Quit to Main Menu, Quit Game and Return to
    /// Game, drawn over the local player's HUD (CoreHUD adds this component at runtime - no scene setup).
    ///
    /// Opened and closed with the gameplay Menu action (Esc / gamepad Start, rebindable on Change
    /// Keybindings). Esc / gamepad B inside the menu goes back: out of the "Are you sure?" box first,
    /// otherwise straight back to the game (same as Return to Game). Those two are read straight from
    /// the keyboard and gamepads (see Update) rather than relying on UI Toolkit's Cancel event, which
    /// doesn't reliably reach the HUD's panel in the gameplay scene. Space presses the focused button,
    /// the same as Enter / gamepad A. The game keeps running while it's open, but this player's gameplay input is blocked
    /// (<see cref="CoreInputHandler.SetGameplayInputBlocked"/>), so the stick and WASD drive the menu, not the
    /// character, and the mouse cursor is freed.
    ///
    /// Quit to Main Menu and Quit Game ask "Are you sure?" first. No is focused by default and Yes stays
    /// disabled for <see cref="ConfirmDelaySeconds"/> seconds, so a mashed button can't quit by accident.
    ///
    /// PRACTICE CHALLENGES (<see cref="PracticeMode.IsChallenge"/>) has its own menu: Retry, Change Challenge,
    /// Settings and Return to Menu (back to the Single Player menu), none of which ask first. Reaching the
    /// end point opens the "Retry Challenge?" box (with the run's time): Retry, Return to Menu or Change
    /// Challenge - B / Esc / Start don't close it. Change Challenge shows the <see cref="ChallengePicker"/>
    /// list; B / Esc / Back there returns to whichever box opened it. Retry is instant - the player goes
    /// back to the spawn point and the course resets in place (<see cref="PracticeRetry"/>). Picking a
    /// challenge swaps it in place too (and the practice map, for the other size) - only if that can't be
    /// done does it start a fresh run through the Single Player menu (see <see cref="Restart"/>).
    /// </summary>
    public class InGameMenu : MonoBehaviour
    {
        public const float ConfirmDelaySeconds = 2f;
        private const string MainMenuSceneName = "MainMenu";
        private const string SinglePlayerSceneName = "SinglePlayer";

        /// <summary>True while any player's Escape menu is open on this machine (there's only ever one).</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>
        /// While true the Menu button does nothing (the round's "3, 2, 1" countdown, see RoundAnnouncer).
        /// Use <see cref="Suppress"/>, which also closes the menu if it's open.
        /// </summary>
        public static bool Suppressed { get; private set; }

        private static InGameMenu s_Instance;

        /// <summary>
        /// Turns the Menu button off (and closes the menu, unless a settings screen is up - that's left to its
        /// own Back) or back on.
        /// </summary>
        public static void Suppress(bool suppressed)
        {
            Suppressed = suppressed;
            if (!suppressed || s_Instance == null || !IsOpen) return;
            if (SettingsOverlay.IsOpen || s_Instance.m_Finished || s_Instance.m_Leaving) return;
            if (s_Instance.m_Picker != null && s_Instance.m_Picker.IsOpen) s_Instance.ClosePicker();
            s_Instance.Close();
        }

        private VisualElement m_Overlay;
        private VisualElement m_MenuPanel;
        private VisualElement m_ConfirmPanel;
        private Label m_ConfirmLabel;
        private Label m_StatusLabel;
        private Button m_FirstButton;
        private Button m_YesButton;
        private Button m_NoButton;
        private System.Action m_ConfirmAction;
        private Coroutine m_YesDelayCoroutine;
        private float m_OpenedTime = float.NegativeInfinity;
        private bool m_Leaving;
        private float m_LastBackTime = float.NegativeInfinity;

        // Settings (Esc > Settings): a list of the settings screens, each opened over the game (SettingsOverlay).
        private VisualElement m_HudRoot;
        private VisualElement m_SettingsPanel;
        private Button m_SettingsFirstButton;
        private Button m_LastSettingsButton;
        private bool m_FinishPending; // reached the end point while a settings screen was up

        // Practice Challenges only.
        private bool m_ChallengeMode;
        private VisualElement m_FinishPanel;
        private Label m_FinishTimeLabel;
        private Label m_FinishStatusLabel;
        private Button m_FinishFirstButton;
        private ChallengePicker m_Picker;
        private bool m_PickerFromFinish;
        private bool m_Finished;

        /// <summary>Ignore back presses this soon after opening (the press that opened the menu).</summary>
        private const float OpenGraceSeconds = 0.2f;

        /// <summary>Builds the (hidden) menu on <paramref name="root"/> and starts listening for the Menu button.</summary>
        public void Initialize(VisualElement root)
        {
            // Same Nintendo-style A = confirm / B = back swap every menu screen applies (see GamepadUIBindingFix).
            GamepadUIBindingFix.Apply();

            m_ChallengeMode = PracticeMode.IsChallenge;
            m_HudRoot = root;
            s_Instance = this;
            BuildUI(root);
            CoreInputHandler.MenuPressed += HandleMenuPressed;
            SettingsOverlay.Closed += HandleSettingsClosed;
            if (m_ChallengeMode) PracticeMode.ChallengeFinished += ShowFinished;
        }

        private void OnDestroy()
        {
            CoreInputHandler.MenuPressed -= HandleMenuPressed;
            PracticeMode.ChallengeFinished -= ShowFinished;
            if (s_Instance == this) s_Instance = null;
            SettingsOverlay.Closed -= HandleSettingsClosed;
            if (IsOpen)
            {
                IsOpen = false;
                CoreInputHandler.SetGameplayInputBlocked(false);
            }
            m_Overlay?.RemoveFromHierarchy();
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void BuildUI(VisualElement root)
        {
            m_Overlay = new VisualElement { name = "ingame-menu" };
            m_Overlay.style.position = Position.Absolute;
            m_Overlay.style.left = 0;
            m_Overlay.style.right = 0;
            m_Overlay.style.top = 0;
            m_Overlay.style.bottom = 0;
            m_Overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.7f);
            m_Overlay.style.alignItems = Align.Center;
            m_Overlay.style.justifyContent = Justify.Center;
            m_Overlay.style.display = DisplayStyle.None;
            root.Add(m_Overlay);

            // B / Esc: back out of whatever's showing (bubble phase, so nothing else needs to handle it).
            m_Overlay.RegisterCallback<NavigationCancelEvent>(evt =>
            {
                evt.StopPropagation();
                // The Esc that opened the menu also reaches the UI as Cancel a moment later - ignore it.
                if (Time.unscaledTime - m_OpenedTime < OpenGraceSeconds) return;
                BackOncePerFrame();
            });

            // Main menu panel
            m_MenuPanel = MakePanel("MENU");
            if (m_ChallengeMode)
            {
                m_FirstButton = AddButton(m_MenuPanel, "Retry", Retry);
                AddButton(m_MenuPanel, "Change Challenge", () => OpenPicker(fromFinish: false));
                AddButton(m_MenuPanel, "Settings", OnSettings);
                AddButton(m_MenuPanel, "Return to Menu", () => LeaveTo(SinglePlayerSceneName, m_StatusLabel));
            }
            else
            {
                m_FirstButton = AddButton(m_MenuPanel, "Settings", OnSettings);
                AddButton(m_MenuPanel, "Quit to Main Menu", () => Confirm("Quit to the main menu?", QuitToMainMenu));
                AddButton(m_MenuPanel, "Quit Game", () => Confirm("Quit the game?", QuitGame));
                AddButton(m_MenuPanel, "Return to Game", Close);
            }

            m_StatusLabel = new Label("");
            m_StatusLabel.style.color = new Color(0.85f, 0.85f, 0.85f);
            m_StatusLabel.style.fontSize = 14;
            m_StatusLabel.style.marginTop = 8;
            m_StatusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_MenuPanel.Add(m_StatusLabel);
            m_Overlay.Add(m_MenuPanel);

            // "Are you sure?" panel
            m_ConfirmPanel = MakePanel("ARE YOU SURE?");
            m_ConfirmLabel = new Label("");
            m_ConfirmLabel.style.color = Color.white;
            m_ConfirmLabel.style.fontSize = 18;
            m_ConfirmLabel.style.marginBottom = 10;
            m_ConfirmLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_ConfirmPanel.Add(m_ConfirmLabel);

            var buttonRow = new VisualElement();
            buttonRow.style.flexDirection = FlexDirection.Row;
            buttonRow.style.justifyContent = Justify.Center;
            m_YesButton = MenuUI.MakeButton("Yes", () => m_ConfirmAction?.Invoke(), null, 140f);
            m_NoButton = MenuUI.MakeButton("No", CloseConfirm, null, 140f);
            m_YesButton.style.marginRight = 10;
            m_NoButton.style.marginLeft = 10;
            buttonRow.Add(m_YesButton);
            buttonRow.Add(m_NoButton);
            m_ConfirmPanel.Add(buttonRow);
            m_ConfirmPanel.style.display = DisplayStyle.None;
            m_Overlay.Add(m_ConfirmPanel);

            // Settings list
            m_SettingsPanel = MakePanel("SETTINGS");
            Button graphics = null, audio = null, keys = null;
            graphics = AddButton(m_SettingsPanel, "Graphics", () => OpenSettingsScreen("GraphicsSettings", graphics));
            audio = AddButton(m_SettingsPanel, "Audio", () => OpenSettingsScreen("AudioSettings", audio));
            keys = AddButton(m_SettingsPanel, "Change Keybindings", () =>
            {
                ChangeKeybindingsController.EnteredViaGamepad = GamepadButtonPressedThisFrame(); // gamepad rows if chosen with a gamepad
                OpenSettingsScreen("ChangeKeybindings", keys);
            });
            AddButton(m_SettingsPanel, "Back", CloseSettingsList);
            m_SettingsFirstButton = graphics;
            m_SettingsPanel.style.display = DisplayStyle.None;
            m_Overlay.Add(m_SettingsPanel);

            if (m_ChallengeMode) BuildChallengeUI();
        }

        /// <summary>Practice Challenges: the "Retry Challenge?" box and the challenge list.</summary>
        private void BuildChallengeUI()
        {
            m_FinishPanel = MakePanel("Retry Challenge?");
            m_FinishTimeLabel = new Label("");
            m_FinishTimeLabel.style.color = new Color(1f, 0.85f, 0.35f);
            m_FinishTimeLabel.style.fontSize = 20;
            m_FinishTimeLabel.style.marginBottom = 12;
            m_FinishTimeLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_FinishPanel.Add(m_FinishTimeLabel);

            m_FinishFirstButton = AddButton(m_FinishPanel, "Retry", Retry);
            AddButton(m_FinishPanel, "Return to Menu", () => LeaveTo(SinglePlayerSceneName, m_FinishStatusLabel));
            AddButton(m_FinishPanel, "Change Challenge", () => OpenPicker(fromFinish: true));

            m_FinishStatusLabel = new Label("");
            m_FinishStatusLabel.style.color = new Color(0.85f, 0.85f, 0.85f);
            m_FinishStatusLabel.style.fontSize = 14;
            m_FinishStatusLabel.style.marginTop = 8;
            m_FinishStatusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_FinishPanel.Add(m_FinishStatusLabel);
            m_FinishPanel.style.display = DisplayStyle.None;
            m_Overlay.Add(m_FinishPanel);

            m_Picker = new ChallengePicker(m_Overlay, null, null, PickChallenge, ClosePicker);
            m_Picker.Overlay.style.backgroundColor = Color.clear; // the menu's own dimming is already behind it
        }

        private static VisualElement MakePanel(string title)
        {
            var panel = new VisualElement();
            panel.style.backgroundColor = new Color(0.06f, 0.06f, 0.08f, 0.95f);
            panel.style.borderTopLeftRadius = 10;
            panel.style.borderTopRightRadius = 10;
            panel.style.borderBottomLeftRadius = 10;
            panel.style.borderBottomRightRadius = 10;
            panel.style.paddingTop = 20;
            panel.style.paddingBottom = 20;
            panel.style.paddingLeft = 40;
            panel.style.paddingRight = 40;
            panel.style.alignItems = Align.Center;

            var titleLabel = new Label(title);
            titleLabel.style.color = Color.white;
            titleLabel.style.fontSize = 28;
            titleLabel.style.marginBottom = 12;
            panel.Add(titleLabel);
            return panel;
        }

        private static Button AddButton(VisualElement parent, string text, System.Action onClick)
        {
            Button button = MenuUI.MakeButton(text, onClick, null, 280f);
            parent.Add(button);
            return button;
        }

        // =====================================================================================
        // Open / close
        // =====================================================================================

        private void HandleMenuPressed(InputControl control)
        {
            if (m_Leaving) return;
            if (Suppressed && !IsOpen) return; // the round's countdown
            if (m_Finished || (m_Picker != null && m_Picker.IsOpen)) return; // pick something (or B / Esc out of the list)
            if (SettingsOverlay.IsOpen) return; // a settings screen is up - its own Back closes it

            if (!IsOpen)
            {
                Open();
                return;
            }

            // Esc is also the UI's Cancel key, which already backs out one step (see BuildUI) - don't
            // also close the whole menu for it. Gamepad Start (or a rebound key) closes the menu.
            if (control is KeyControl key && key.keyCode == Key.Escape) return;
            Close();
        }

        private void Open()
        {
            IsOpen = true;
            m_OpenedTime = Time.unscaledTime;
            CoreInputHandler.SetGameplayInputBlocked(true);

            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            m_StatusLabel.text = "";
            m_ConfirmPanel.style.display = DisplayStyle.None;
            m_SettingsPanel.style.display = DisplayStyle.None;
            m_MenuPanel.style.display = DisplayStyle.Flex;
            m_Overlay.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_FirstButton);
        }

        private void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            StopYesDelay();
            m_ConfirmAction = null;

            m_Overlay.style.display = DisplayStyle.None;
            m_Overlay.panel?.focusController?.focusedElement?.Blur();
            CoreInputHandler.SetGameplayInputBlocked(false);

            // Back to gameplay's locked cursor - unless a betting overlay still needs it.
            bool bettingOpen = ChallengeManager.Instance != null && ChallengeManager.Instance.IsBettingWindowActive;
            UnityEngine.Cursor.lockState = bettingOpen ? CursorLockMode.None : CursorLockMode.Locked;
            UnityEngine.Cursor.visible = bettingOpen;
        }

        /// <summary>Esc and gamepad B, read directly: back out of the confirm box, or close the menu.</summary>
        private void Update()
        {
            if (!IsOpen || m_Leaving) return;
            if (Time.unscaledTime - m_OpenedTime < OpenGraceSeconds) return;

            bool back = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            bool settingsScreenOpen = SettingsOverlay.IsOpen;
            if (!back && Application.isFocused)
            {
                foreach (Gamepad pad in Gamepad.all)
                {
                    if (pad.TryGetChildControl<ButtonControl>(GamepadUIBindingFix.CancelControl) is ButtonControl cancel && cancel.wasPressedThisFrame)
                    {
                        back = true;
                        break;
                    }
                }
            }

            if (settingsScreenOpen)
            {
                // The settings screen handles its own B / Esc; make sure it gets one if the UI's Cancel doesn't arrive.
                if (back) StartCoroutine(ForwardBackToSettingsScreen(Time.frameCount));
                return;
            }

            if (back)
            {
                BackOncePerFrame();
                return;
            }

            // Space selects too (as well as Enter) - the thumb is already on it, the other hand on the mouse.
            if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) SubmitFocusedButton();
        }

        /// <summary>Presses the focused menu button, exactly as Enter / gamepad A would (sound included).</summary>
        private void SubmitFocusedButton()
        {
            if (!(m_Overlay.panel?.focusController?.focusedElement is Button button)) return;
            if (!button.enabledInHierarchy) return; // e.g. "Yes" during its 2-second delay

            using (NavigationSubmitEvent evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        /// <summary>
        /// The UI's Cancel event and the direct read above can both see the same press (possibly a frame
        /// apart) - only act on it once, so one press never backs out two steps.
        /// </summary>
        private void BackOncePerFrame()
        {
            if (Time.unscaledTime - m_LastBackTime < 0.15f) return;
            m_LastBackTime = Time.unscaledTime;
            Back();
        }

        private void Back()
        {
            if (m_Leaving) return;
            if (m_Picker != null && m_Picker.IsOpen) ClosePicker();
            else if (m_Finished) return; // the "Retry Challenge?" box stays until something's picked
            else if (m_ConfirmPanel.style.display == DisplayStyle.Flex) CloseConfirm();
            else if (m_SettingsPanel.style.display == DisplayStyle.Flex) CloseSettingsList();
            else Close();
        }

        // =====================================================================================
        // Settings (Graphics / Audio / Change Keybindings, over the game)
        // =====================================================================================

        private void OnSettings()
        {
            m_StatusLabel.text = "";
            m_MenuPanel.style.display = DisplayStyle.None;
            m_SettingsPanel.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_SettingsFirstButton);
        }

        private void CloseSettingsList()
        {
            m_SettingsPanel.style.display = DisplayStyle.None;
            m_MenuPanel.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_FirstButton);
        }

        /// <summary>
        /// Shows the menu's own settings screen (<paramref name="sceneName"/>) over the game - the same scene as
        /// from the main menu (<see cref="SettingsOverlay"/>). The HUD hides meanwhile; its Back comes back here.
        /// The game keeps running and gameplay input stays blocked, as with the rest of this menu.
        /// </summary>
        private void OpenSettingsScreen(string sceneName, Button from)
        {
            if (SettingsOverlay.IsOpen || m_Leaving) return;
            m_LastSettingsButton = from;
            m_Overlay.panel?.focusController?.focusedElement?.Blur();
            m_HudRoot.style.display = DisplayStyle.None;
            SettingsOverlay.Open(sceneName);
        }

        private void HandleSettingsClosed()
        {
            if (m_HudRoot == null) return;
            m_HudRoot.style.display = DisplayStyle.Flex;
            if (!IsOpen) return;
            m_MenuPanel.style.display = DisplayStyle.None;
            m_SettingsPanel.style.display = DisplayStyle.Flex;
            m_Overlay.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_LastSettingsButton != null ? m_LastSettingsButton : m_SettingsFirstButton);

            if (m_FinishPending)
            {
                m_FinishPending = false;
                m_SettingsPanel.style.display = DisplayStyle.None;
                ShowFinished();
            }
        }

        /// <summary>Two frames on: if the settings screen didn't get that B / Esc as a UI Cancel itself, send it one.</summary>
        private IEnumerator ForwardBackToSettingsScreen(int pressFrame)
        {
            yield return null;
            yield return null;
            if (SettingsOverlay.IsOpen && SettingsOverlay.LastCancelFrame < pressFrame) SettingsOverlay.SendCancel();
        }

        private static bool GamepadButtonPressedThisFrame()
        {
            Gamepad pad = Gamepad.current;
            return pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                                   pad.buttonNorth.wasPressedThisFrame || pad.buttonWest.wasPressedThisFrame);
        }

        // =====================================================================================
        // Practice Challenges
        // =====================================================================================

        /// <summary>The end point was reached: freeze the player and ask "Retry Challenge?".</summary>
        private void ShowFinished()
        {
            if (m_Leaving || m_FinishPanel == null) return;
            if (SettingsOverlay.IsOpen) { m_FinishPending = true; return; } // shown once the settings screen closes
            m_Finished = true;

            IsOpen = true;
            m_OpenedTime = Time.unscaledTime;
            CoreInputHandler.SetGameplayInputBlocked(true);
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            StopYesDelay();
            m_ConfirmAction = null;
            m_Picker.Close();
            m_MenuPanel.style.display = DisplayStyle.None;
            m_ConfirmPanel.style.display = DisplayStyle.None;
            m_FinishTimeLabel.text = Loc.F("Time: {0}", PracticeMode.FormatRunTime(PracticeMode.RunSeconds));
            m_FinishStatusLabel.text = "";
            m_FinishPanel.style.display = DisplayStyle.Flex;
            m_Overlay.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_FinishFirstButton);
        }

        private void OpenPicker(bool fromFinish)
        {
            if (m_Leaving) return;
            m_PickerFromFinish = fromFinish;
            m_MenuPanel.style.display = DisplayStyle.None;
            m_FinishPanel.style.display = DisplayStyle.None;
            m_Picker.Open(PracticeMode.ChallengeId);
        }

        private void ClosePicker()
        {
            if (m_Leaving) return;
            m_Picker.Close();
            if (m_PickerFromFinish)
            {
                m_FinishPanel.style.display = DisplayStyle.Flex;
                MenuUI.FocusFirst(m_FinishFirstButton);
            }
            else
            {
                m_MenuPanel.style.display = DisplayStyle.Flex;
                MenuUI.FocusFirst(m_FirstButton);
            }
        }

        /// <summary>Quick retry: straight back to the start of the same run (<see cref="PracticeRetry"/>), no loading.</summary>
        private void Retry()
        {
            if (m_Leaving) return;
            if (!PracticeRetry.Run())
            {
                // Nothing to reset in place (e.g. the player hasn't spawned yet): start a fresh run instead.
                Restart(null, m_Finished ? m_FinishStatusLabel : m_StatusLabel);
                return;
            }

            m_Finished = false;
            m_Picker.Close();
            m_FinishPanel.style.display = DisplayStyle.None;
            Close(); // back to the game: input unblocked, cursor locked
        }

        private void PickChallenge(ChallengeCatalog.Entry entry)
        {
            if (entry == null) return;
            if (m_Leaving) return;
            if (PracticeRetry.ChangeChallenge(entry.id))
            {
                // Swapped in place - straight into the new challenge.
                m_Finished = false;
                m_Picker.Close();
                m_FinishPanel.style.display = DisplayStyle.None;
                Close();
                return;
            }

            // Couldn't swap in place: start a fresh run instead.
            m_Picker.SetBusy(true, "Loading...");
            Restart(entry.id, null);
        }

        /// <summary>
        /// Starts a fresh practice run of <paramref name="challengeId"/> (or the same challenge again): leaves this
        /// session and goes back through the Single Player menu, which starts the new one straight away
        /// (<see cref="SinglePlayerController.QueuePracticeChallenge"/>) - exactly the path a run from the menu
        /// takes. (Reloading the gameplay scene inside the running session left a stale player behind.)
        /// </summary>
        private void Restart(string challengeId, Label statusLabel)
        {
            if (m_Leaving) return;
            string id = !string.IsNullOrEmpty(challengeId) ? challengeId : PracticeMode.ChallengeId;
            SinglePlayerController.QueuePracticeChallenge(id);
            LeaveTo(SinglePlayerSceneName, statusLabel);
            if (statusLabel != null) statusLabel.text = Loc.T("Loading...");
        }

        // =====================================================================================
        // Buttons
        // =====================================================================================

        private void Confirm(string question, System.Action onYes)
        {
            m_ConfirmAction = onYes;
            m_ConfirmLabel.text = question;
            m_MenuPanel.style.display = DisplayStyle.None;
            m_ConfirmPanel.style.display = DisplayStyle.Flex;

            StopYesDelay();
            if (PracticeMode.IsActive)
            {
                // Nothing to lose in practice - Yes works straight away.
                m_YesButton.text = "Yes";
                m_YesButton.focusable = true;
                m_YesButton.SetEnabled(true);
            }
            else
            {
                m_YesDelayCoroutine = StartCoroutine(EnableYesAfterDelay());
            }
            MenuUI.FocusFirst(m_NoButton); // No is the safe default
        }

        private IEnumerator EnableYesAfterDelay()
        {
            m_YesButton.SetEnabled(false);
            m_YesButton.focusable = false;
            float end = Time.unscaledTime + ConfirmDelaySeconds;
            while (Time.unscaledTime < end)
            {
                m_YesButton.text = Loc.F("Yes ({0})", Mathf.CeilToInt(end - Time.unscaledTime));
                yield return null;
            }
            m_YesButton.text = "Yes";
            m_YesButton.focusable = true;
            m_YesButton.SetEnabled(true);
            m_YesDelayCoroutine = null;
        }

        private void StopYesDelay()
        {
            if (m_YesDelayCoroutine != null)
            {
                StopCoroutine(m_YesDelayCoroutine);
                m_YesDelayCoroutine = null;
            }
        }

        private void CloseConfirm()
        {
            StopYesDelay();
            m_ConfirmAction = null;
            m_ConfirmPanel.style.display = DisplayStyle.None;
            m_MenuPanel.style.display = DisplayStyle.Flex;
            MenuUI.FocusFirst(m_FirstButton);
        }

        private void QuitToMainMenu()
        {
            if (m_Leaving) return;
            StopYesDelay();
            m_YesButton.SetEnabled(false);
            m_NoButton.SetEnabled(false);
            LeaveTo(MainMenuSceneName, m_ConfirmLabel);
        }

        /// <summary>Leaves the session and loads <paramref name="sceneName"/> (the main menu, or the Single Player menu from practice).</summary>
        private async void LeaveTo(string sceneName, Label statusLabel)
        {
            if (m_Leaving) return;
            m_Leaving = true;
            if (statusLabel != null) statusLabel.text = Loc.T("Leaving...");

            IsOpen = false;
            CoreInputHandler.SetGameplayInputBlocked(false);
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            if (FusionSessionService.HasInstance)
            {
                await FusionSessionService.Instance.LeaveGameAsync();
            }
            SceneManager.LoadScene(sceneName);
        }

        private void QuitGame()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
