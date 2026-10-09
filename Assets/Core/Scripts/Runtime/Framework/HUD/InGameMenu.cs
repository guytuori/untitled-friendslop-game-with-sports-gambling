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
    /// </summary>
    public class InGameMenu : MonoBehaviour
    {
        public const float ConfirmDelaySeconds = 2f;
        private const string MainMenuSceneName = "MainMenu";

        /// <summary>True while any player's Escape menu is open on this machine (there's only ever one).</summary>
        public static bool IsOpen { get; private set; }

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

        /// <summary>Ignore back presses this soon after opening (the press that opened the menu).</summary>
        private const float OpenGraceSeconds = 0.2f;

        /// <summary>Builds the (hidden) menu on <paramref name="root"/> and starts listening for the Menu button.</summary>
        public void Initialize(VisualElement root)
        {
            // Same Nintendo-style A = confirm / B = back swap every menu screen applies (see GamepadUIBindingFix).
            GamepadUIBindingFix.Apply();

            BuildUI(root);
            CoreInputHandler.MenuPressed += HandleMenuPressed;
        }

        private void OnDestroy()
        {
            CoreInputHandler.MenuPressed -= HandleMenuPressed;
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
            m_FirstButton = AddButton(m_MenuPanel, "Settings", OnSettings);
            AddButton(m_MenuPanel, "Quit to Main Menu", () => Confirm("Quit to the main menu?", QuitToMainMenu));
            AddButton(m_MenuPanel, "Quit Game", () => Confirm("Quit the game?", QuitGame));
            AddButton(m_MenuPanel, "Return to Game", Close);

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
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
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
            if (m_ConfirmPanel.style.display == DisplayStyle.Flex) CloseConfirm();
            else Close();
        }

        // =====================================================================================
        // Buttons
        // =====================================================================================

        private void OnSettings()
        {
            // Placeholder - the in-game settings screen comes later.
            m_StatusLabel.text = "Settings aren't available in a match yet.";
        }

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
                m_YesButton.text = $"Yes ({Mathf.CeilToInt(end - Time.unscaledTime)})";
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

        private async void QuitToMainMenu()
        {
            if (m_Leaving) return;
            m_Leaving = true;
            StopYesDelay();
            m_ConfirmLabel.text = "Leaving...";
            m_YesButton.SetEnabled(false);
            m_NoButton.SetEnabled(false);

            IsOpen = false;
            CoreInputHandler.SetGameplayInputBlocked(false);
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            if (FusionSessionService.HasInstance)
            {
                await FusionSessionService.Instance.LeaveGameAsync();
            }
            SceneManager.LoadScene(MainMenuSceneName);
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
