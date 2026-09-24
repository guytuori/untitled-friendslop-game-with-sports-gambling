using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The "change controls" screen, reached from the Settings scene's Change Keybindings button. Shows
    /// only ONE control scheme's bindings at a time - keyboard+mouse or gamepad - based on
    /// <see cref="EnteredViaGamepad"/>, a static flag SettingsController sets right before loading this
    /// scene depending on which device was used to click/submit its Change Keybindings button (see that
    /// class for how it detects that). A static field is enough here since it's plain data, not a Unity
    /// object - it survives the scene load without needing DontDestroyOnLoad.
    ///
    /// Each row is Command name + a button showing the current binding; clicking (or gamepad-submitting)
    /// a row starts "awaiting rebind" for that command - the button's text changes to a prompt, and the
    /// next matching input (any key for keyboard rows, any button for gamepad rows) becomes the new
    /// binding. Move is the one gamepad row that's different: per spec, it only ever listens for the
    /// left or right stick moving past a deadzone and completely ignores button presses, since Move is
    /// inherently a 2D stick command, not a single button.
    ///
    /// MENU NAVIGATION STAYS FIXED (gamepad only): entering a row's rebind mode and backing out of this
    /// screen always mean Submit (East/"A") and Cancel (South/"B") respectively, no matter what the
    /// player rebinds Jump/Grab/etc. to via the rows below - so A and B remain fully assignable to any
    /// gameplay command here, same as every other button. That's not a coincidence that needs enforcing
    /// in this class: Submit/Cancel live on the separate stock UI actions asset GamepadUIBindingFix
    /// controls, entirely independent of GameplayInputSystem_Actions (the asset these rows actually
    /// rebind) - so a player setting, say, Jump to East never touches what Submit is bound to, and this
    /// screen's own navigation can't drift regardless of what's rebound below.
    ///
    /// The Submit press that activates a row's Button (mouse click, keyboard Enter/Space, or gamepad
    /// East) is still "just pressed" on the very frame BeginRebind runs on - polling for a capture that
    /// same frame would immediately grab that same press as the new binding, before the player's let go
    /// of it. m_RebindStartedFrame records which frame BeginRebind ran on so Update can skip polling for
    /// exactly that one frame - see Update for why one frame is always enough regardless of how long the
    /// button is then held.
    ///
    /// While awaiting a rebind, this temporarily sets EventSystem.sendNavigationEvents to false - without
    /// that, the exact same physical input being captured (a stick push, a gamepad button) would
    /// simultaneously also move UI focus or re-submit the row being rebound, since the scene's own
    /// InputSystemUIInputModule processes Move/Submit from those same devices every frame. This is the
    /// same category of collision GamepadUIBindingFix resolves for Submit/Cancel - toggling
    /// sendNavigationEvents off for the duration of a capture is what avoids it here.
    ///
    /// REAL vs COSMETIC commands: most rows drive Unity's own real gameplay input
    /// (GameplayInputSystem_Actions, the same asset CoreInputHandler reads for actual movement/jump/etc.)
    /// via the official InputAction.ApplyBindingOverride mechanism - rebinding one of these rows actually
    /// changes what key/button drives gameplay, immediately, in this same process. That covers Forwards/
    /// Back/Left/Right/Jump/Run/Grab/OpenMenu on keyboard, and Move/Jump/Run/Grab/OpenMenu on gamepad -
    /// see <see cref="GetRealAction"/>/<see cref="GetRealCompositePart"/> for exactly how each row maps to
    /// an action (and, for the four directional keyboard rows, to one part of Move's WASD composite).
    /// The Wager rows (WagerOption1-3 on keyboard, WagerCursorUp/WagerCursorDown/WagerSelect on gamepad)
    /// don't correspond to any real action yet - the user's own words: "don't worry about what these mean
    /// yet, they'll be added to the main part of the game later" - so those stay purely cosmetic, stored
    /// in <see cref="InputBindingsData"/>/<see cref="InputBindingsStore"/> same as before.
    /// <see cref="IsRealCommand"/> is what the rest of this class branches on to tell the two apart.
    ///
    /// Real overrides are applied immediately (each CompleteRebind call) to a private
    /// GameplayInputSystem_Actions instance constructed here - not the one CoreInputHandler owns, since
    /// this scene has no player object - but both instances share the SAME underlying
    /// GameplayInputSystem_Actions.inputactions asset, and ApplyBindingOverride's effect lives on the
    /// InputAction/InputActionAsset object itself rather than on any one wrapper instance, so an override
    /// applied here is visible to any other instance of the same asset for the rest of this process.
    /// Overrides only become permanent across a full restart once Save Changes writes them to
    /// input-binding-overrides.json via InputBindingOverridesStore.Save - see OnSaveChangesClicked - and
    /// CoreInputHandler.Awake() calls InputBindingOverridesStore.ApplySavedOverrides on its own instance
    /// so a saved rebind is picked up the next time gameplay starts, even from a fresh process.
    ///
    /// One known gap left as-is: the real "Forwards"/"Back"/"Left"/"Right" rows each override only the
    /// PRIMARY keyboard binding for that composite part (the WASD key) - the redundant arrow-key
    /// alternate binding already on the same action is left untouched, since this screen only exposes one
    /// binding per row. Rebinding Forwards away from W doesn't remove Up Arrow as an alternate way to move
    /// forward.
    ///
    /// Bindings are loaded from <see cref="InputBindingsStore"/> into a working in-memory copy on Awake,
    /// edited freely as the player rebinds rows, and only written back to disk when Save Changes is
    /// clicked - Back without saving discards whatever was changed this visit, the usual settings-screen
    /// convention. The same applies to real overrides: they take effect on gameplay immediately (since
    /// they're applied live to the shared asset as each row is rebound), but only persist past this
    /// process if Save Changes is clicked before leaving. All three action buttons (Restore Defaults,
    /// Save Changes, Back) are disabled while a rebind capture is in progress, so an accidental mouse
    /// click can't interrupt a capture that's mid-flight.
    ///
    /// Restore Defaults (above Save Changes, above Back) resets BOTH control schemes' bindings - real and
    /// cosmetic - back to their defaults in one click, regardless of which scheme this visit is showing:
    /// clicking it while looking at the keyboard layout also resets the gamepad bindings the player isn't
    /// currently looking at, and vice versa. Like every other rebind here, it only touches the in-memory
    /// working copy - it's still just a working change until Save Changes is clicked. See
    /// OnRestoreDefaultsClicked for how each half (real vs cosmetic) is actually reset.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class ChangeKeybindingsController : MonoBehaviour
    {
        /// <summary>
        /// Set by SettingsController right before it loads this scene - true if Change Keybindings was
        /// reached via a gamepad button, false if it was reached by mouse click or keyboard Enter.
        /// Defaults to false so opening this scene directly (e.g. pressing Play with it as the active
        /// scene) falls back to the keyboard+mouse layout rather than an unset/undefined one.
        /// </summary>
        public static bool EnteredViaGamepad;

        [Tooltip("Same logo as the other GameFlow screens - wire this up to the same texture.")]
        [SerializeField] private Texture2D titleImage;

        [Tooltip("Scene to load when Back is clicked. This scene's only entry point is Settings, so Back returns there rather than the main menu.")]
        [SerializeField] private string settingsSceneName = "Settings";

        [Tooltip("Played when a binding row or Save Changes is selected.")]
        [SerializeField] private AudioClip selectSound;

        [Tooltip("Played when Back is selected.")]
        [SerializeField] private AudioClip cancelSound;

        private InputBindingsData m_Bindings;
        private GameplayInputSystem_Actions m_GameplayActions;
        private bool m_UseGamepad;
        private string m_AwaitingRebindCommand;
        private int m_RebindStartedFrame = -1;
        private readonly Dictionary<string, Button> m_RowButtons = new Dictionary<string, Button>();
        private Button m_FirstFocusable;
        private Button m_RestoreDefaultsButton;
        private Button m_SaveButton;
        private Button m_BackButton;

        private void Awake()
        {
            // See the class summary's "RESOLVED COLLISION" note on SettingsController - this scene has
            // the same gamepad Submit/Cancel fix applied for the same reason (Save Changes/Back use
            // Submit/Cancel like any other menu button).
            GamepadUIBindingFix.Apply();

            m_Bindings = InputBindingsStore.Load();

            // Real gameplay bindings - a separate instance from CoreInputHandler's, but backed by the
            // same shared asset, so overrides applied through this instance affect real gameplay too -
            // see the class summary's "REAL vs COSMETIC" section.
            m_GameplayActions = new GameplayInputSystem_Actions();
            InputBindingOverridesStore.ApplySavedOverrides(m_GameplayActions.asset);

            m_UseGamepad = EnteredViaGamepad;

            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            BuildUI(root);
        }

        private void OnDestroy()
        {
            m_GameplayActions?.Dispose();
        }

        private void BuildUI(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.style.backgroundColor = Color.black;

            if (titleImage != null)
            {
                var title = new Image { image = titleImage, scaleMode = ScaleMode.ScaleToFit };
                // Much smaller than the other GameFlow screens' 99% logo - this screen needs room for
                // the rebinding list below, so the logo is just a small top-center identifier here.
                title.style.position = Position.Absolute;
                title.style.top = 20;
                title.style.left = Length.Percent(50);
                title.style.translate = new Translate(Length.Percent(-50), 0);
                title.style.width = Length.Percent(22);
                title.style.height = Length.Percent(22);
                root.Add(title);
            }

            var list = new VisualElement();
            list.style.position = Position.Absolute;
            list.style.left = 60;
            list.style.top = 160;
            list.style.flexDirection = FlexDirection.Column;
            root.Add(list);

            m_RowButtons.Clear();
            m_FirstFocusable = null;

            if (m_UseGamepad)
            {
                AddRow(list, "MOVE", "Move");
                AddRow(list, "JUMP", "Jump");
                AddRow(list, "RUN", "Run");
                AddRow(list, "GRAB", "Grab");
                AddRow(list, "WAGER CURSOR UP", "WagerCursorUp");
                AddRow(list, "WAGER CURSOR DOWN", "WagerCursorDown");
                AddRow(list, "WAGER SELECT", "WagerSelect");
                AddRow(list, "OPEN MENU", "OpenMenu");
            }
            else
            {
                AddRow(list, "FORWARDS", "Forwards");
                AddRow(list, "BACK", "Back");
                AddRow(list, "LEFT", "Left");
                AddRow(list, "RIGHT", "Right");
                AddRow(list, "JUMP", "Jump");
                AddRow(list, "RUN", "Run");
                AddRow(list, "GRAB", "Grab");
                AddRow(list, "WAGER OPTION1", "WagerOption1");
                AddRow(list, "WAGER OPTION2", "WagerOption2");
                AddRow(list, "WAGER OPTION3", "WagerOption3");
                AddRow(list, "OPEN MENU", "OpenMenu");
            }

            // Same absolutely-positioned bottom-right column as the other GameFlow screens, with Restore
            // Defaults and Save Changes added above Back (Restore Defaults on top, per spec).
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
            root.Add(buttonColumn);

            // The row list and the action column aren't visually aligned (rows start at left:60, the
            // action column is anchored to the right edge instead), so UI Toolkit's automatic
            // nearest-neighbor navigation can't reliably jump between them left/right - see
            // SetupCrossColumnNavigation for the explicit fix.
            SetupCrossColumnNavigation();

            // See MainMenuController.BuildUI for why this is needed: nothing has focus by default, and
            // gamepad Move/Submit only act on whatever's currently focused. The first binding row gets
            // focus here rather than Save Changes, since the rows are the reason this screen exists.
            Button toFocus = m_FirstFocusable != null ? m_FirstFocusable : m_SaveButton;
            toFocus.Focus();
            SetFocusedVisual(toFocus, true);
        }

        /// <summary>Builds one Command-label + binding-button row and registers it in m_RowButtons.</summary>
        private void AddRow(VisualElement parent, string label, string commandKey)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 6;

            var commandLabel = new Label(label);
            commandLabel.style.color = Color.white;
            commandLabel.style.fontSize = 16;
            commandLabel.style.width = 220;
            row.Add(commandLabel);

            var bindingButton = new Button(() => BeginRebind(commandKey))
            {
                text = GetBindingDisplay(commandKey)
            };
            bindingButton.style.width = 160;
            bindingButton.style.height = 34;
            bindingButton.style.marginTop = 4;
            bindingButton.style.marginBottom = 4;
            bindingButton.style.fontSize = 16;

            SetFocusedVisual(bindingButton, false);
            bindingButton.RegisterCallback<FocusInEvent>(_ => SetFocusedVisual(bindingButton, true));
            bindingButton.RegisterCallback<FocusOutEvent>(_ => SetFocusedVisual(bindingButton, false));
            row.Add(bindingButton);

            parent.Add(row);

            m_RowButtons[commandKey] = bindingButton;
            if (m_FirstFocusable == null)
            {
                m_FirstFocusable = bindingButton;
            }
        }

        /// <summary>
        /// Wires explicit left/right gamepad-navigation between the row list and the action column,
        /// since they aren't visually aligned and UI Toolkit's automatic navigation only reliably jumps
        /// between elements that roughly line up. Pressing right from ANY row always lands on Save
        /// Changes (the middle, most central action button); pressing left from ANY of Restore Defaults/
        /// Save Changes/Back always lands on the first row (whichever command that is for the active
        /// scheme - Move for gamepad, Forwards for keyboard+mouse). Every other direction (up/down within
        /// either column) is left untouched, so normal automatic navigation still handles those. Must run
        /// after both the rows and the action buttons exist, since it references m_SaveButton/
        /// m_FirstFocusable - called once at the end of BuildUI.
        /// </summary>
        private void SetupCrossColumnNavigation()
        {
            foreach (Button rowButton in m_RowButtons.Values)
            {
                rowButton.RegisterCallback<NavigationMoveEvent>(OnRowNavigationMove);
            }

            m_RestoreDefaultsButton.RegisterCallback<NavigationMoveEvent>(OnActionNavigationMove);
            m_SaveButton.RegisterCallback<NavigationMoveEvent>(OnActionNavigationMove);
            m_BackButton.RegisterCallback<NavigationMoveEvent>(OnActionNavigationMove);
        }

        private void OnRowNavigationMove(NavigationMoveEvent evt)
        {
            if (evt.direction != NavigationMoveEvent.Direction.Right) return;

            m_SaveButton.Focus();
            SuppressDefaultNavigation(evt);
        }

        private void OnActionNavigationMove(NavigationMoveEvent evt)
        {
            if (evt.direction != NavigationMoveEvent.Direction.Left) return;
            if (m_FirstFocusable == null) return;

            m_FirstFocusable.Focus();
            SuppressDefaultNavigation(evt);
        }

        /// <summary>
        /// StopPropagation alone only stops the event bubbling further up the visual tree - it does NOT
        /// stop UI Toolkit's own built-in "find the nearest focusable element in this direction" logic,
        /// which runs as that event's separate default action regardless of propagation. PreventDefault
        /// is what actually suppresses that - without it, the automatic nearest-neighbor jump still runs
        /// right after our manual .Focus() call above and immediately overrides it (which is exactly what
        /// was happening before this method existed: the manual focus call had no visible effect except by
        /// coincidence on rows already spatially close to the action column).
        /// </summary>
        private static void SuppressDefaultNavigation(NavigationMoveEvent evt)
        {
            evt.PreventDefault();
            evt.StopPropagation();
        }

        // Same grayed-out/bright-pill focus treatment as the other GameFlow screens, plus a distinct
        // warm color while a row is awaiting a rebind, so it's obvious which one is capturing input.
        private static readonly Color FocusedBackground = new Color(0.92f, 0.92f, 0.92f);
        private static readonly Color FocusedText = Color.black;
        private static readonly Color UnfocusedBackground = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color UnfocusedText = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color AwaitingBackground = new Color(0.85f, 0.55f, 0.15f);
        private static readonly Color AwaitingText = Color.black;

        /// <summary>Same click-sound wrapper as MainMenuController.MakeButton - see that class for why.</summary>
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

            SetFocusedVisual(button, false);
            button.RegisterCallback<FocusInEvent>(_ => SetFocusedVisual(button, true));
            button.RegisterCallback<FocusOutEvent>(_ => SetFocusedVisual(button, false));

            return button;
        }

        private static void SetFocusedVisual(Button button, bool focused)
        {
            button.style.backgroundColor = focused ? FocusedBackground : UnfocusedBackground;
            button.style.color = focused ? FocusedText : UnfocusedText;
        }

        private void BeginRebind(string commandKey)
        {
            // Ignore clicks on other rows while one capture is already in progress.
            if (m_AwaitingRebindCommand != null) return;

            if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }

            m_AwaitingRebindCommand = commandKey;
            m_RebindStartedFrame = Time.frameCount;
            Button button = m_RowButtons[commandKey];
            button.text = m_UseGamepad && commandKey == "Move" ? "Move a stick..." : "Press...";
            button.style.backgroundColor = AwaitingBackground;
            button.style.color = AwaitingText;

            // Suppress the EventSystem's own Move/Submit/Cancel processing while capturing raw input -
            // see the class summary for why this matters (otherwise the same input we're capturing also
            // moves focus or re-submits the row being rebound).
            if (EventSystem.current != null)
            {
                EventSystem.current.sendNavigationEvents = false;
            }
        }

        private void CompleteRebind(string rawValue)
        {
            string commandKey = m_AwaitingRebindCommand;
            m_AwaitingRebindCommand = null;

            if (EventSystem.current != null)
            {
                EventSystem.current.sendNavigationEvents = true;
            }

            if (IsRealCommand(commandKey))
            {
                ApplyRealBinding(commandKey, rawValue);
            }
            else
            {
                SetBinding(commandKey, rawValue);
            }

            Button button = m_RowButtons[commandKey];
            button.text = GetBindingDisplay(commandKey);
            SetFocusedVisual(button, true); // it's the element that was just interacted with
        }

        private void Update()
        {
            if (m_AwaitingRebindCommand == null) return;

            // The Submit press (gamepad button, or Enter/Space on keyboard) that activated this row's
            // Button and triggered BeginRebind is still "just pressed" on this exact frame - polling
            // immediately would capture that same still-active press as the new binding before the
            // player's had any chance to press something else. wasPressedThisFrame is only ever true on
            // the single frame a press started, so skipping polling for exactly that one frame (the frame
            // BeginRebind ran on) is enough, regardless of how long the player then holds the button down.
            if (Time.frameCount == m_RebindStartedFrame) return;

            if (m_UseGamepad)
            {
                PollGamepadRebind();
            }
            else
            {
                PollKeyboardRebind();
            }
        }

        private void PollKeyboardRebind()
        {
            if (Keyboard.current == null) return;

            foreach (KeyControl key in Keyboard.current.allKeys)
            {
                if (key.wasPressedThisFrame)
                {
                    // key.name is the real Input System control name (e.g. "w", "escape", "1"), the same
                    // format a binding path uses after "<Keyboard>/" - keeps captured keyboard input
                    // consistent whether it ends up as a real override path or a cosmetic Wager value.
                    CompleteRebind(key.name);
                    return;
                }
            }
        }

        private void PollGamepadRebind()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return;

            if (m_AwaitingRebindCommand == "Move")
            {
                // Move only ever binds to a stick - button presses are ignored entirely, per spec, so
                // there's nothing else to check here.
                const float deadzone = 0.5f;
                if (pad.leftStick.ReadValue().sqrMagnitude > deadzone * deadzone)
                {
                    CompleteRebind("leftStick");
                }
                else if (pad.rightStick.ReadValue().sqrMagnitude > deadzone * deadzone)
                {
                    CompleteRebind("rightStick");
                }
                return;
            }

            if (pad.buttonSouth.wasPressedThisFrame) { CompleteRebind("buttonSouth"); return; }
            if (pad.buttonEast.wasPressedThisFrame) { CompleteRebind("buttonEast"); return; }
            if (pad.buttonNorth.wasPressedThisFrame) { CompleteRebind("buttonNorth"); return; }
            if (pad.buttonWest.wasPressedThisFrame) { CompleteRebind("buttonWest"); return; }
            if (pad.leftShoulder.wasPressedThisFrame) { CompleteRebind("leftShoulder"); return; }
            if (pad.rightShoulder.wasPressedThisFrame) { CompleteRebind("rightShoulder"); return; }
            if (pad.leftTrigger.wasPressedThisFrame) { CompleteRebind("leftTrigger"); return; }
            if (pad.rightTrigger.wasPressedThisFrame) { CompleteRebind("rightTrigger"); return; }
            if (pad.startButton.wasPressedThisFrame) { CompleteRebind("start"); return; }
            if (pad.selectButton.wasPressedThisFrame) { CompleteRebind("select"); return; }
        }

        #region Real commands (backed by GameplayInputSystem_Actions)

        /// <summary>True for every row that's backed by a real GameplayInputSystem_Actions action rather than the cosmetic InputBindingsData store - see the class summary's "REAL vs COSMETIC" section.</summary>
        private bool IsRealCommand(string commandKey) => GetRealAction(commandKey) != null;

        /// <summary>Maps a row's commandKey to the actual InputAction it should rebind, for whichever scheme (keyboard or gamepad) this screen is currently showing. Returns null for a cosmetic-only (Wager) command.</summary>
        private InputAction GetRealAction(string commandKey)
        {
            var player = m_GameplayActions.Player;

            if (m_UseGamepad)
            {
                return commandKey switch
                {
                    "Move" => player.Move,
                    "Jump" => player.Jump,
                    "Run" => player.Sprint,
                    "Grab" => player.Grab,
                    "OpenMenu" => player.Menu,
                    _ => null
                };
            }

            return commandKey switch
            {
                "Forwards" => player.Move,
                "Back" => player.Move,
                "Left" => player.Move,
                "Right" => player.Move,
                "Jump" => player.Jump,
                "Run" => player.Sprint,
                "Grab" => player.Grab,
                "OpenMenu" => player.Menu,
                _ => null
            };
        }

        /// <summary>For the four keyboard directional rows, which named part of Move's WASD composite they target. Null for every other row (including all gamepad rows - gamepad Move is a single non-composite stick binding, not a composite).</summary>
        private static string GetRealCompositePart(string commandKey)
        {
            return commandKey switch
            {
                "Forwards" => "up",
                "Back" => "down",
                "Left" => "left",
                "Right" => "right",
                _ => null
            };
        }

        /// <summary>The control-scheme group name to search bindings for, matching GameplayInputSystem_Actions' own scheme names ("Keyboard&amp;Mouse"/"Gamepad").</summary>
        private string GetRealGroup() => m_UseGamepad ? "Gamepad" : "Keyboard&Mouse";

        private void ApplyRealBinding(string commandKey, string rawValue)
        {
            InputAction action = GetRealAction(commandKey);
            if (action == null) return;

            int bindingIndex = FindBindingIndex(action, GetRealGroup(), GetRealCompositePart(commandKey));
            if (bindingIndex < 0)
            {
                Debug.LogWarning($"[ChangeKeybindings] Couldn't find a {GetRealGroup()} binding for '{commandKey}' on action '{action.name}' - rebind not applied.");
                return;
            }

            string newPath = m_UseGamepad ? $"<Gamepad>/{rawValue}" : $"<Keyboard>/{rawValue}";
            action.ApplyBindingOverride(bindingIndex, newPath);
        }

        /// <summary>
        /// Finds the index of the one binding on <paramref name="action"/> that belongs to
        /// <paramref name="requiredGroup"/> - and, if <paramref name="compositePartName"/> is set, that's
        /// also the named part of a composite (e.g. "up" for Move's WASD composite) rather than a plain
        /// binding. Returns -1 if nothing matches.
        /// </summary>
        private static int FindBindingIndex(InputAction action, string requiredGroup, string compositePartName)
        {
            if (action == null) return -1;

            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding b = bindings[i];

                if (compositePartName != null)
                {
                    if (!b.isPartOfComposite || !string.Equals(b.name, compositePartName, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else
                {
                    // A plain, single binding - not a composite's parent entry (e.g. "WASD") and not one
                    // of its parts.
                    if (b.isComposite || b.isPartOfComposite) continue;
                }

                if (GroupsContain(b.groups, requiredGroup))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool GroupsContain(string groups, string requiredGroup)
        {
            if (string.IsNullOrEmpty(groups)) return false;

            foreach (string g in groups.Split(';'))
            {
                if (string.Equals(g, requiredGroup, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        #endregion

        #region Cosmetic commands (Wager rows only - see InputBindingsData)

        /// <summary>Reads the current raw binding value for a cosmetic Wager commandKey out of the right struct for the active scheme.</summary>
        private string GetBindingRaw(string commandKey)
        {
            if (m_UseGamepad)
            {
                return commandKey switch
                {
                    "WagerCursorUp" => m_Bindings.Gamepad.WagerCursorUp,
                    "WagerCursorDown" => m_Bindings.Gamepad.WagerCursorDown,
                    "WagerSelect" => m_Bindings.Gamepad.WagerSelect,
                    _ => "?"
                };
            }

            return commandKey switch
            {
                "WagerOption1" => m_Bindings.Keyboard.WagerOption1,
                "WagerOption2" => m_Bindings.Keyboard.WagerOption2,
                "WagerOption3" => m_Bindings.Keyboard.WagerOption3,
                _ => "?"
            };
        }

        private void SetBinding(string commandKey, string rawValue)
        {
            if (m_UseGamepad)
            {
                switch (commandKey)
                {
                    case "WagerCursorUp": m_Bindings.Gamepad.WagerCursorUp = rawValue; break;
                    case "WagerCursorDown": m_Bindings.Gamepad.WagerCursorDown = rawValue; break;
                    case "WagerSelect": m_Bindings.Gamepad.WagerSelect = rawValue; break;
                }
                return;
            }

            switch (commandKey)
            {
                case "WagerOption1": m_Bindings.Keyboard.WagerOption1 = rawValue; break;
                case "WagerOption2": m_Bindings.Keyboard.WagerOption2 = rawValue; break;
                case "WagerOption3": m_Bindings.Keyboard.WagerOption3 = rawValue; break;
            }
        }

        #endregion

        #region Display formatting (shared by real and cosmetic rows)

        private string GetBindingDisplay(string commandKey)
        {
            if (IsRealCommand(commandKey))
            {
                InputAction action = GetRealAction(commandKey);
                int bindingIndex = FindBindingIndex(action, GetRealGroup(), GetRealCompositePart(commandKey));
                if (bindingIndex < 0) return "?";

                // effectivePath reflects any override already applied (via ApplyBindingOverride above),
                // falling back to the original path if this binding has never been rebound.
                string controlName = StripDevicePrefix(action.bindings[bindingIndex].effectivePath);
                return m_UseGamepad ? GamepadControlDisplayName(controlName) : KeyboardControlDisplayName(controlName);
            }

            string raw = GetBindingRaw(commandKey);
            return m_UseGamepad ? GamepadControlDisplayName(raw) : KeyboardControlDisplayName(raw);
        }

        /// <summary>Strips the "&lt;Keyboard&gt;/" or "&lt;Gamepad&gt;/" device prefix off an effective binding path, leaving the bare control name (e.g. "w", "buttonSouth").</summary>
        private static string StripDevicePrefix(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            int slash = path.IndexOf('/');
            return slash >= 0 && slash + 1 < path.Length ? path.Substring(slash + 1) : path;
        }

        /// <summary>Turns a bare keyboard control name (as returned by KeyControl.name, e.g. "w", "leftShift", "escape", "1") into a short on-screen label.</summary>
        private static string KeyboardControlDisplayName(string controlName)
        {
            if (string.IsNullOrEmpty(controlName)) return controlName;

            return controlName switch
            {
                "leftShift" => "L Shift",
                "rightShift" => "R Shift",
                "leftCtrl" => "L Ctrl",
                "rightCtrl" => "R Ctrl",
                "leftAlt" => "L Alt",
                "rightAlt" => "R Alt",
                "escape" => "Esc",
                "space" => "Space",
                "upArrow" => "Up",
                "downArrow" => "Down",
                "leftArrow" => "Left",
                "rightArrow" => "Right",
                _ when controlName.Length == 1 => controlName.ToUpperInvariant(),
                _ => char.ToUpperInvariant(controlName[0]) + controlName.Substring(1)
            };
        }

        /// <summary>
        /// Turns a bare gamepad control name into a short on-screen label, using this project's own
        /// controller-label convention: South="B", East="A", North="X", West="Y" (Nintendo-style, as
        /// established for this project), and L1/L2/R1/R2 for the shoulders/triggers, matching the
        /// mockup.
        /// </summary>
        private static string GamepadControlDisplayName(string controlName)
        {
            return controlName switch
            {
                "leftStick" => "Left Stick",
                "rightStick" => "Right Stick",
                "buttonSouth" => "B",
                "buttonEast" => "A",
                "buttonNorth" => "X",
                "buttonWest" => "Y",
                "leftShoulder" => "L1",
                "rightShoulder" => "R1",
                "leftTrigger" => "L2",
                "rightTrigger" => "R2",
                "start" => "Start",
                "select" => "Select",
                _ => controlName
            };
        }

        #endregion

        private void OnRestoreDefaultsClicked()
        {
            if (m_AwaitingRebindCommand != null) return; // don't reset mid-capture, see class summary

            if (selectSound != null)
            {
                AudioVolumeService.PlayOneShot(selectSound, AudioCategory.SoundEffects, Vector3.zero);
            }

            // Clears every override this screen could have applied to the real gameplay actions,
            // reverting them to GameplayInputSystem_Actions' own baked-in defaults - for BOTH control
            // schemes at once, regardless of which one is currently shown (m_UseGamepad only affects
            // which rows are drawn, not which scheme this clears). This only removes overrides; it
            // doesn't touch actions this screen never exposes a row for (Look, Previous, Next, etc.),
            // since nothing else in the project applies overrides to those.
            m_GameplayActions.asset.RemoveAllBindingOverrides();

            // Resets the cosmetic Wager bindings the same way - both schemes at once, not just the one
            // on screen.
            m_Bindings = new InputBindingsData();

            // This doesn't call Save - Restore Defaults only resets the in-memory working copy, same as
            // any other rebind on this screen. Leaving without clicking Save Changes discards it, same
            // as the class summary's usual "Back without saving" behavior.
            RefreshAllRowLabels();
        }

        /// <summary>Refreshes every currently-visible row's button text from the underlying data - needed after Restore Defaults changes bindings without going through the usual per-row BeginRebind/CompleteRebind flow.</summary>
        private void RefreshAllRowLabels()
        {
            foreach (var pair in m_RowButtons)
            {
                pair.Value.text = GetBindingDisplay(pair.Key);
            }
        }

        private void OnSaveChangesClicked()
        {
            if (m_AwaitingRebindCommand != null) return; // don't save mid-capture, see class summary
            InputBindingsStore.Save(m_Bindings);
            InputBindingOverridesStore.Save(m_GameplayActions.asset);
        }

        private void OnBackClicked()
        {
            if (m_AwaitingRebindCommand != null) return; // don't leave mid-capture, see class summary
            SceneManager.LoadScene(settingsSceneName);
        }
    }
}
