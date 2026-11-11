using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// In-game chat (2026-11-10): the chat box in the bottom-left corner and the message builder. Messages
    /// themselves are <see cref="ChatLog"/>'s.
    ///
    /// WHERE: in a multiplayer session (never in practice) - the lobby (room filling up), the gameplay scene
    /// (the round, and the Game Results screen at the end), and the Round Results screen between rounds. One
    /// hidden object made at startup does it all (no scene setup): it puts its own small UIDocument on each of
    /// those scenes, on the scene's own panel settings, above everything else. Wiped when each round starts.
    ///
    /// THE BOX: bottom 20% of the screen, 15% wide. Each message is "HH:mm:ss Name: message"; new ones slide in
    /// at the bottom and push the older ones up, which fade out as they reach the top of the box - so how many
    /// show depends on the screen and on how long they are (about 5-6 typical ones at 1080p).
    ///
    /// TYPING (keyboard): Open Chat (Enter) opens the box and every key goes to it - none to the game; Open Chat
    /// again sends (and closes), Open Menu (Esc) closes without sending. At most 255 characters (the cursor
    /// turns red and nothing more goes in). A message can't follow this player's last one within 0.33 s - an
    /// Enter pressed too soon just leaves the text there for the next press.
    ///
    /// THE MESSAGE BUILDER (gamepad Open Chat = Select, or anyone in a Message Builder Chat Only game - with the
    /// keyboard the Move Forward / Back keys go up and down, Strafe Right picks, Strafe Left goes back):
    ///   - in a round: a cursor on the scoreboard's player names (Up / Down, A picks, B goes back to the game);
    ///     then, in the middle of the screen, that player's name, Should / Should Not, every action the wagers
    ///     can be about (map-only ones only on their maps), and "in his current challenge." / "soon." / "by the
    ///     end of the game." - the chosen item of each list stays on one line and the lists move. A picks and
    ///     moves on (the last one sends), B goes back a list, Start closes at any point;
    ///   - in the lobby, between rounds and on the Game Results screen: a short list of set phrases
    ///     (<see cref="ChatPhrases"/>).
    /// A gamepad player can still type on a keyboard (Enter) unless the game is Message Builder Chat Only.
    ///
    /// While the chat is open, gameplay input is blocked (CoreInputHandler, as for the Esc menu), BoundControls
    /// reports nothing, the menus' own Submit / Cancel / move events are held back (so Enter doesn't also press
    /// the lobby's Start Match, and Esc doesn't also Leave), and the Esc menu / Game Results' Esc ignore the press
    /// that closed it.
    /// </summary>
    public class ChatOverlay : MonoBehaviour
    {
        private const string LobbyScene = "GameLobby";
        private const string BetweenRoundsScene = "RoundResults";
        private const string MatchScene = "[BB] Core";

        private const float WidthPercent = 15f;
        private const float HeightPercent = 20f;
        private const float EdgeMargin = 16f;
        private const float InputHeight = 30f;
        private const int FontSize = 14;
        private const float FadeZone = 34f;        // messages fade out across this many pixels at the top of the box
        private const float SlideSpeed = 500f;     // pixels per second (faster when a lot arrives at once)
        private const float GuardSeconds = 0.2f;   // after closing: the menus ignore Submit / Cancel this long

        private const float RowHeight = 36f;       // one builder list item
        private const float RepeatDelay = 0.4f;
        private const float RepeatInterval = 0.12f;

        private static readonly Color BoxBackground = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color InputBackground = new Color(0.05f, 0.05f, 0.07f, 0.85f);
        private static readonly Color HintColor = new Color(1f, 1f, 1f, 0.55f);
        private static readonly Color FullColor = new Color(1f, 0.35f, 0.3f);

        private static ChatOverlay s_Instance;
        private static int s_ClosedFrame = -10;
        private static float s_GuardUntil;

        /// <summary>True while the chat box or the message builder is open on this machine.</summary>
        public static bool IsOpen { get; private set; }

        /// <summary>True while typing (as opposed to the message builder).</summary>
        public static bool IsTyping { get; private set; }

        /// <summary>Every key and button belongs to the chat right now (BoundControls reports nothing).</summary>
        public static bool CapturesInput => IsOpen;

        /// <summary>The Menu button (Esc / Start) is the chat's right now - or it just closed the chat this frame.</summary>
        public static bool SwallowsMenu => IsOpen || Time.frameCount - s_ClosedFrame <= 1;

        /// <summary>For "press any key" screens: this press is opening, typing in or closing the chat.</summary>
        public static bool BlocksAnyKey => SwallowsMenu || (s_Instance != null && s_Instance.m_Root != null && s_Instance.OpenPressed() != 0);

        /// <summary>The chat box is on screen in this scene.</summary>
        public static bool Available => s_Instance != null && s_Instance.m_Root != null;

        /// <summary>Whether <paramref name="doc"/> is the chat's own (screens that hide every other UI keep it).</summary>
        public static bool IsChatDocument(UIDocument doc) => doc != null && s_Instance != null && doc == s_Instance.m_Document;

        private InputBindingsData m_Bindings;
        private GameplayInputSystem_Actions m_Actions;
        private NetworkRunner m_LastRunner;

        // UI (rebuilt in each scene)
        private UIDocument m_Document;
        private VisualElement m_Root, m_Box, m_Viewport, m_Lines, m_InputBox;
        private Label m_Hint, m_Counter;
        private TextField m_Field;
        private VisualElement m_GuardedTree;
        private float m_Slide;
        private float m_NextPanelSearch;

        // Open / close
        private Focusable m_PreviousFocus;
        private bool m_RestoreFocus;
        private bool m_BlockedGameplay;
        private int m_OpenedFrame;
        private int m_FocusFieldFrame = -1;

        // The message builder
        private enum Step { None, Player, Should, Action, When, Preset }

        private class Column
        {
            public VisualElement Root, Stack;
            public readonly List<Label> Items = new List<Label>();
        }

        private enum ColumnState { Hidden, Active, Chosen, Pending }

        private VisualElement m_Builder;
        private Label m_BuilderHelp;
        private Column m_NameColumn, m_ShouldColumn, m_ActionColumn, m_WhenColumn, m_PresetColumn;
        private Step m_Step;
        private ChatPhrases.Context m_Context;
        private bool m_UseScoreboard;
        private readonly List<ulong> m_Players = new List<ulong>();
        private readonly List<int> m_ActionIds = new List<int>();
        private readonly List<int> m_PresetIds = new List<int>();
        private int m_PlayerIndex, m_ShouldIndex, m_ActionIndex, m_WhenIndex, m_PresetIndex;
        private int m_HeldDirection;
        private float m_NextRepeat;
        private int m_HeldSideways;

        // =====================================================================================
        // Setup
        // =====================================================================================

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Instance = null;
            IsOpen = false;
            IsTyping = false;
            s_ClosedFrame = -10;
            s_GuardUntil = 0f;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Create()
        {
            if (s_Instance != null) return;
            var go = new GameObject("Chat") { hideFlags = HideFlags.HideInHierarchy };
            DontDestroyOnLoad(go);
            s_Instance = go.AddComponent<ChatOverlay>();
        }

        private void Awake()
        {
            m_Bindings = InputBindingsStore.Load();
            m_Actions = new GameplayInputSystem_Actions();
            InputBindingOverridesStore.ApplySavedOverrides(m_Actions.asset);
            m_Actions.Player.Menu.Enable();
            m_Actions.Player.Move.Enable();

            InputBindingsStore.Saved += ReloadBindings;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            ChatLog.Added += HandleLineAdded;
            ChatLog.Cleared += HandleCleared;
        }

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            ChatLog.Added -= HandleLineAdded;
            ChatLog.Cleared -= HandleCleared;
            Close();
            m_Actions?.Dispose();
            if (s_Instance == this) s_Instance = null;
        }

        private void ReloadBindings()
        {
            m_Bindings = InputBindingsStore.Load();
            if (m_Actions != null) InputBindingOverridesStore.ApplySavedOverrides(m_Actions.asset);
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return; // Esc > Settings screens load on top of the game
            Close();
            ForgetUI();
            if (scene.name == MatchScene && !PracticeMode.IsActive) ChatLog.Clear(); // a new round
        }

        private static bool IsChatScene(string name) => name == LobbyScene || name == BetweenRoundsScene || name == MatchScene;

        // =====================================================================================
        // Every frame
        // =====================================================================================

        private void Update()
        {
            NetworkRunner runner = FusionSessionService.HasInstance ? FusionSessionService.Instance.GameRunner : null;
            if (runner != m_LastRunner)
            {
                m_LastRunner = runner; // a session started or ended
                ChatLog.Clear();
            }

            bool wanted = runner != null && runner.IsRunning && !PracticeMode.IsActive && IsChatScene(SceneManager.GetActiveScene().name);
            if (!wanted)
            {
                Close();
                if (m_Document != null) Destroy(m_Document.gameObject);
                ForgetUI();
                return;
            }

            if (m_Document == null)
            {
                ForgetUI();
                if (Time.unscaledTime < m_NextPanelSearch || !BuildUI()) return;
            }
            if (m_GuardedTree == null) GuardMenus();

            UpdateHint();
            AnimateLines(Time.unscaledDeltaTime);

            if (m_RestoreFocus && Time.unscaledTime >= s_GuardUntil)
            {
                m_RestoreFocus = false;
                if (m_PreviousFocus is VisualElement previous && previous.panel != null) previous.Focus();
                m_PreviousFocus = null;
            }

            if (IsOpen)
            {
                if (IsTyping) UpdateTyping();
                else UpdateBuilder();
                return;
            }

            int opened = OpenPressed();
            if (opened != 0 && CanOpen()) Open(fromGamepad: opened == 2);
        }

        /// <summary>1 if Open Chat was pressed on the keyboard this frame, 2 on a gamepad, 0 if not.</summary>
        private int OpenPressed()
        {
            if (m_Bindings == null) return 0;
            string key = m_Bindings.Keyboard.OpenChat;
            if (BoundControls.KeyboardPressed(key)) return 1;
            if (key == "enter" && Keyboard.current != null && Keyboard.current.numpadEnterKey.wasPressedThisFrame) return 1;
            return BoundControls.GamepadPressed(m_Bindings.Gamepad.OpenChat) ? 2 : 0;
        }

        private bool CanOpen()
        {
            return !IsOpen && m_Root != null && Time.unscaledTime >= s_GuardUntil
                   && !InGameMenu.IsOpen && !SettingsOverlay.IsOpen;
        }

        /// <summary>Open Menu (Esc / Start - however it's bound), or plain Esc.</summary>
        private bool MenuPressed()
        {
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) return true;
            return m_Actions != null && m_Actions.Player.Menu.WasPressedThisFrame();
        }

        // =====================================================================================
        // Open / close
        // =====================================================================================

        private void Open(bool fromGamepad)
        {
            bool builder = fromGamepad || !ChatLog.TypingAllowed;
            IsOpen = true;
            IsTyping = !builder;
            m_OpenedFrame = Time.frameCount;
            m_PreviousFocus = m_Root.panel?.focusController?.focusedElement;
            m_RestoreFocus = false;

            if (SceneManager.GetActiveScene().name == MatchScene && !CoreInputHandler.GameplayInputBlocked)
            {
                CoreInputHandler.SetGameplayInputBlocked(true);
                m_BlockedGameplay = true;
            }

            m_Box.style.backgroundColor = BoxBackground;
            if (IsTyping)
            {
                m_Hint.style.display = DisplayStyle.None;
                m_Field.style.display = DisplayStyle.Flex;
                m_Field.SetValueWithoutNotify("");
                m_FocusFieldFrame = Time.frameCount + 1; // next frame: the key that opened the box doesn't type
            }
            else
            {
                StartBuilder();
            }
        }

        private void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            IsTyping = false;
            s_ClosedFrame = Time.frameCount;
            s_GuardUntil = Time.unscaledTime + GuardSeconds;

            if (m_Field != null)
            {
                m_Field.Blur();
                m_Field.style.display = DisplayStyle.None;
            }
            if (m_Hint != null) m_Hint.style.display = DisplayStyle.Flex;
            if (m_Box != null) m_Box.style.backgroundColor = new Color(0f, 0f, 0f, 0f);
            if (m_Builder != null) m_Builder.style.display = DisplayStyle.None;
            m_Step = Step.None;
            if (CoreHUD.Local != null) CoreHUD.Local.SetScoreboardCursor(0);

            if (m_BlockedGameplay)
            {
                CoreInputHandler.SetGameplayInputBlocked(false);
                m_BlockedGameplay = false;
            }
            m_RestoreFocus = true; // once the guard is over, so the closing press doesn't reach the menu
        }

        // =====================================================================================
        // Typing
        // =====================================================================================

        private void UpdateTyping()
        {
            if (Time.frameCount == m_FocusFieldFrame || (Time.frameCount > m_FocusFieldFrame && m_Field.panel?.focusController?.focusedElement != m_Field))
            {
                m_Field.Focus(); // and back again if a click took it away
            }

            int length = m_Field.value != null ? m_Field.value.Length : 0;
            bool full = length >= ChatLog.MaxLength;
            m_Counter.text = length >= ChatLog.MaxLength - 55 ? $"{length}/{ChatLog.MaxLength}" : "";
            m_Counter.style.color = full ? FullColor : HintColor;
            m_Field.textSelection.cursorColor = full ? FullColor : Color.white;

            if (Time.frameCount == m_OpenedFrame) return;

            if (MenuPressed())
            {
                Close();
                return;
            }

            if (SendPressed())
            {
                string text = ChatLog.Clean(m_Field.value);
                if (text.Length == 0)
                {
                    Close();
                    return;
                }
                // Too soon after the last message: the text stays for the next press.
                if (!ChatLog.CanSendNow) return;
                if (ChatLog.SendText(text))
                {
                    m_Field.SetValueWithoutNotify("");
                    Close();
                }
            }
        }

        /// <summary>Enter (either one), or Open Chat if it's bound to something that doesn't type a character.</summary>
        private bool SendPressed()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return false;
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame) return true;
            string key = m_Bindings != null ? m_Bindings.Keyboard.OpenChat : null;
            bool typesSomething = string.IsNullOrEmpty(key) || key.Length == 1 || key == "space";
            return !typesSomething && BoundControls.KeyboardPressed(key);
        }

        // =====================================================================================
        // The message builder
        // =====================================================================================

        private ChatPhrases.Context CurrentContext()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene == LobbyScene) return ChatPhrases.Context.Lobby;
            if (scene == BetweenRoundsScene) return ChatPhrases.Context.BetweenRounds;
            if (scene == MatchScene) return GameResults.IsActive ? ChatPhrases.Context.Results : ChatPhrases.Context.Round;
            return ChatPhrases.Context.None;
        }

        private void StartBuilder()
        {
            m_Context = CurrentContext();
            m_HeldDirection = 0;
            m_HeldSideways = 0;

            if (m_Context == ChatPhrases.Context.Round)
            {
                m_Players.Clear();
                if (CoreHUD.Local != null) m_Players.AddRange(CoreHUD.Local.ScoreboardOrder);
                m_UseScoreboard = m_Players.Count > 0;
                if (!m_UseScoreboard) m_Players.AddRange(NetworkPlayers.Ids);
                if (m_Players.Count == 0) { Close(); return; }

                // Start on the first player who isn't this one.
                m_PlayerIndex = Mathf.Max(0, m_Players.FindIndex(id => id != NetworkPlayers.LocalClientId));

                m_ActionIds.Clear();
                for (int i = 0; i < ChatPhrases.Actions.Length; i++)
                {
                    if (ChatPhrases.IsActionAvailable(i)) m_ActionIds.Add(i);
                }
                m_ShouldIndex = 0;
                m_ActionIndex = 0;
                m_WhenIndex = 0;

                SetItems(m_ShouldColumn, new List<string> { Loc.T(ChatPhrases.ShouldLabels[0]), Loc.T(ChatPhrases.ShouldLabels[1]) });
                var actions = new List<string>();
                foreach (int id in m_ActionIds) actions.Add(ChatPhrases.ActionLabel(id));
                SetItems(m_ActionColumn, actions);
                var whens = new List<string>();
                foreach (string when in ChatPhrases.WhenLabels) whens.Add(Loc.T(when));
                SetItems(m_WhenColumn, whens);
                var names = new List<string>();
                foreach (ulong id in m_Players) names.Add(NetworkPlayers.GetDisplayName(id));
                SetItems(m_NameColumn, names);
                m_Step = Step.Player;
            }
            else
            {
                m_PresetIds.Clear();
                foreach (int id in ChatPhrases.PresetsFor(m_Context))
                {
                    if (id == ChatPhrases.PresetTiltControls && ChatLog.TiltControlsSent) continue;
                    m_PresetIds.Add(id);
                }
                if (m_PresetIds.Count == 0) { Close(); return; }
                var phrases = new List<string>();
                foreach (int id in m_PresetIds) phrases.Add(ChatPhrases.Preset(id));
                SetItems(m_PresetColumn, phrases);
                m_PresetIndex = 0;
                m_Step = Step.Preset;
            }

            m_Builder.style.display = DisplayStyle.Flex;
            RenderBuilder();
        }

        private void UpdateBuilder()
        {
            if (Time.frameCount == m_OpenedFrame) return;

            if (MenuPressed())
            {
                Close();
                return;
            }

            // Up / Down: the Move action (left stick, Move Forward / Back keys and the arrows) and the d-pad, held to repeat.
            Vector2 move = m_Actions.Player.Move.ReadValue<Vector2>();
            float vertical = move.y;
            Gamepad pad = Gamepad.current;
            if (pad != null && Application.isFocused) vertical += pad.dpad.ReadValue().y;
            int direction = vertical > 0.5f ? -1 : vertical < -0.5f ? 1 : 0; // up = the item above
            if (direction != m_HeldDirection)
            {
                m_HeldDirection = direction;
                if (direction != 0)
                {
                    MoveCursor(direction);
                    m_NextRepeat = Time.unscaledTime + RepeatDelay;
                }
            }
            else if (direction != 0 && Time.unscaledTime >= m_NextRepeat)
            {
                MoveCursor(direction);
                m_NextRepeat = Time.unscaledTime + RepeatInterval;
            }

            // Keyboard: Strafe Right picks, Strafe Left goes back (a gamepad's stick sideways does nothing).
            bool keyboardMove = m_Actions.Player.Move.activeControl?.device is Keyboard;
            int sideways = keyboardMove ? (move.x > 0.5f ? 1 : move.x < -0.5f ? -1 : 0) : 0;
            bool keyboardPick = false, keyboardBack = false;
            if (sideways != m_HeldSideways)
            {
                m_HeldSideways = sideways;
                keyboardPick = sideways > 0;
                keyboardBack = sideways < 0;
            }

            Keyboard keyboard = Keyboard.current;
            bool enter = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
            bool pick = keyboardPick || enter || BoundControls.GamepadPressed(GamepadUIBindingFix.SubmitControl);
            bool back = keyboardBack || BoundControls.GamepadPressed(GamepadUIBindingFix.CancelControl);

            if (pick) Pick();
            else if (back) Back();
        }

        private void MoveCursor(int direction)
        {
            switch (m_Step)
            {
                case Step.Player: m_PlayerIndex = Mathf.Clamp(m_PlayerIndex + direction, 0, m_Players.Count - 1); break;
                case Step.Should: m_ShouldIndex = Mathf.Clamp(m_ShouldIndex + direction, 0, 1); break;
                case Step.Action: m_ActionIndex = Mathf.Clamp(m_ActionIndex + direction, 0, Mathf.Max(0, m_ActionIds.Count - 1)); break;
                case Step.When: m_WhenIndex = Mathf.Clamp(m_WhenIndex + direction, 0, ChatPhrases.WhenLabels.Length - 1); break;
                case Step.Preset: m_PresetIndex = Mathf.Clamp(m_PresetIndex + direction, 0, m_PresetIds.Count - 1); break;
                default: return;
            }
            RenderBuilder();
        }

        private void Pick()
        {
            switch (m_Step)
            {
                case Step.Player:
                    m_Step = Step.Should;
                    break;
                case Step.Should:
                    m_Step = m_ActionIds.Count > 0 ? Step.Action : Step.Should;
                    break;
                case Step.Action:
                    m_Step = Step.When;
                    break;
                case Step.When:
                    if (!ChatLog.CanSendNow) return; // too soon after the last message - pick again in a moment
                    if (ChatLog.SendBuilder((int)m_Players[m_PlayerIndex], m_ShouldIndex == 0, m_ActionIds[m_ActionIndex], m_WhenIndex)) Close();
                    return;
                case Step.Preset:
                    if (!ChatLog.CanSendNow) return;
                    if (ChatLog.SendPreset(m_PresetIds[m_PresetIndex])) Close();
                    return;
                default:
                    return;
            }
            RenderBuilder();
        }

        private void Back()
        {
            switch (m_Step)
            {
                case Step.Player:
                case Step.Preset:
                    Close();
                    return;
                case Step.Should: m_Step = Step.Player; break;
                case Step.Action: m_Step = Step.Should; break;
                case Step.When: m_Step = Step.Action; break;
                default: return;
            }
            RenderBuilder();
        }

        private void RenderBuilder()
        {
            if (m_Step == Step.Preset)
            {
                ShowColumn(m_NameColumn, ColumnState.Hidden, 0);
                ShowColumn(m_ShouldColumn, ColumnState.Hidden, 0);
                ShowColumn(m_ActionColumn, ColumnState.Hidden, 0);
                ShowColumn(m_WhenColumn, ColumnState.Hidden, 0);
                ShowColumn(m_PresetColumn, ColumnState.Active, m_PresetIndex);
            }
            else
            {
                bool choosingPlayer = m_Step == Step.Player;
                if (CoreHUD.Local != null) CoreHUD.Local.SetScoreboardCursor(m_UseScoreboard ? m_Players[m_PlayerIndex] : 0);

                // Picking a player: on the scoreboard (the middle stays empty) - or, without one, in the middle.
                ShowColumn(m_NameColumn, choosingPlayer ? (m_UseScoreboard ? ColumnState.Hidden : ColumnState.Active) : ColumnState.Chosen, m_PlayerIndex);
                ShowColumn(m_ShouldColumn, choosingPlayer ? ColumnState.Hidden : StateOf(Step.Should), m_ShouldIndex);
                ShowColumn(m_ActionColumn, choosingPlayer ? ColumnState.Hidden : StateOf(Step.Action), m_ActionIndex);
                ShowColumn(m_WhenColumn, choosingPlayer ? ColumnState.Hidden : StateOf(Step.When), m_WhenIndex);
                ShowColumn(m_PresetColumn, ColumnState.Hidden, 0);
            }
            m_BuilderHelp.text = HelpText();
        }

        private ColumnState StateOf(Step column)
        {
            if (m_Step == column) return ColumnState.Active;
            return m_Step > column ? ColumnState.Chosen : ColumnState.Pending;
        }

        /// <summary>"[A] Select   [B] Back   [START] Close" - with this player's keys.</summary>
        private string HelpText()
        {
            string pick, back, close;
            if (!UsingKeyboard())
            {
                pick = ControlNames.Gamepad(GamepadUIBindingFix.SubmitControl);
                back = ControlNames.Gamepad(GamepadUIBindingFix.CancelControl);
                close = ControlNames.Gamepad("start");
            }
            else
            {
                pick = KeyboardMoveKey("right") ?? "D";
                back = KeyboardMoveKey("left") ?? "A";
                close = ControlNames.Keyboard("escape");
            }
            return Loc.F("[{0}] Select   [{1}] Back   [{2}] Close", pick, back, close);
        }

        /// <summary>The key bound to one part ("left", "right") of Move's keyboard composite, as shown to the player.</summary>
        private string KeyboardMoveKey(string part)
        {
            if (m_Actions == null) return null;
            foreach (InputBinding binding in m_Actions.Player.Move.bindings)
            {
                if (!binding.isPartOfComposite || !string.Equals(binding.name, part, System.StringComparison.OrdinalIgnoreCase)) continue;
                string path = binding.effectivePath;
                if (string.IsNullOrEmpty(path) || !path.StartsWith("<Keyboard>/")) continue;
                return ControlNames.Keyboard(path.Substring("<Keyboard>/".Length));
            }
            return null;
        }

        private static bool UsingKeyboard()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return true;
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && keyboard.lastUpdateTime >= pad.lastUpdateTime;
        }

        // =====================================================================================
        // Builder columns
        // =====================================================================================

        private Column MakeColumn(VisualElement parent)
        {
            var column = new Column();
            column.Root = new VisualElement { pickingMode = PickingMode.Ignore };
            column.Root.style.height = RowHeight;
            column.Root.style.flexShrink = 0;
            column.Root.style.marginLeft = 8;
            column.Root.style.marginRight = 8;
            column.Root.style.overflow = Overflow.Visible;
            column.Stack = new VisualElement { pickingMode = PickingMode.Ignore };
            column.Stack.style.flexShrink = 0;
            column.Stack.style.alignItems = Align.Center;
            column.Root.Add(column.Stack);
            parent.Add(column.Root);
            return column;
        }

        private static void SetItems(Column column, List<string> texts)
        {
            column.Stack.Clear();
            column.Items.Clear();
            foreach (string text in texts)
            {
                var label = new Label(text) { pickingMode = PickingMode.Ignore, enableRichText = false };
                label.style.height = RowHeight;
                label.style.marginTop = 0;
                label.style.marginBottom = 0;
                label.style.paddingLeft = 12;
                label.style.paddingRight = 12;
                label.style.fontSize = 20;
                label.style.color = Color.white;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.NoWrap;
                label.style.textShadow = new TextShadow { offset = new Vector2(1.5f, 1.5f), blurRadius = 3f, color = new Color(0f, 0f, 0f, 0.9f) };
                label.style.borderTopLeftRadius = label.style.borderTopRightRadius = 6f;
                label.style.borderBottomLeftRadius = label.style.borderBottomRightRadius = 6f;
                label.style.borderTopColor = label.style.borderBottomColor = Color.white;
                label.style.borderLeftColor = label.style.borderRightColor = Color.white;
                column.Items.Add(label);
                column.Stack.Add(label);
            }
        }

        /// <summary>Lines the column up so item <paramref name="index"/> sits on the builder's line, styled for <paramref name="state"/>.</summary>
        private static void ShowColumn(Column column, ColumnState state, int index)
        {
            column.Root.style.display = state == ColumnState.Hidden ? DisplayStyle.None : DisplayStyle.Flex;
            if (state == ColumnState.Hidden) return;

            column.Stack.style.translate = new Translate(0f, -index * RowHeight);
            for (int i = 0; i < column.Items.Count; i++)
            {
                Label label = column.Items[i];
                bool selected = i == index;
                int distance = Mathf.Abs(i - index);

                float border = selected && state != ColumnState.Pending ? 2f : 0f;
                label.style.borderTopWidth = label.style.borderBottomWidth = border;
                label.style.borderLeftWidth = label.style.borderRightWidth = border;
                Color edge = state == ColumnState.Active ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                label.style.borderTopColor = label.style.borderBottomColor = edge;
                label.style.borderLeftColor = label.style.borderRightColor = edge;
                label.style.backgroundColor = selected && state != ColumnState.Pending ? new Color(0f, 0f, 0f, 0.55f) : new Color(0f, 0f, 0f, 0f);

                float opacity;
                if (selected) opacity = state == ColumnState.Pending ? 0.5f : 1f;
                else opacity = state == ColumnState.Active ? Mathf.Max(0f, 0.75f - 0.18f * distance) : 0f; // only the list being picked from shows the others
                label.style.opacity = opacity;
                label.style.visibility = opacity > 0f ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // =====================================================================================
        // The box
        // =====================================================================================

        private void HandleLineAdded(ChatLine line)
        {
            if (m_Lines == null) return;
            AddLineLabel(line, slide: true);
        }

        private void HandleCleared()
        {
            m_Lines?.Clear();
            m_Slide = 0f;
        }

        private void AddLineLabel(ChatLine line, bool slide)
        {
            var label = new Label(ChatLog.ToRichText(line)) { pickingMode = PickingMode.Ignore, enableRichText = true };
            label.style.fontSize = FontSize;
            label.style.color = Color.white;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginTop = 0;
            label.style.marginBottom = 2;
            label.style.paddingTop = 0;
            label.style.paddingBottom = 0;
            label.style.textShadow = new TextShadow { offset = new Vector2(1f, 1f), blurRadius = 2f, color = new Color(0f, 0f, 0f, 0.95f) };
            m_Lines.Add(label);
            while (m_Lines.childCount > ChatLog.MaxLines) m_Lines.RemoveAt(0);

            if (!slide) return;
            // Everything above slides up by the new line's height once it's laid out.
            EventCallback<GeometryChangedEvent> onLaidOut = null;
            onLaidOut = evt =>
            {
                label.UnregisterCallback(onLaidOut);
                float height = evt.newRect.height;
                if (!float.IsNaN(height)) m_Slide += height + 2f;
            };
            label.RegisterCallback(onLaidOut);
        }

        /// <summary>The slide up, and the fade out across the top of the box.</summary>
        private void AnimateLines(float deltaTime)
        {
            if (m_Lines == null) return;

            m_Slide = Mathf.MoveTowards(m_Slide, 0f, Mathf.Max(SlideSpeed, m_Slide * 6f) * deltaTime);
            m_Lines.style.translate = new Translate(0f, m_Slide);

            float viewport = m_Viewport.layout.height;
            float lines = m_Lines.layout.height;
            if (float.IsNaN(viewport) || float.IsNaN(lines)) return;

            float top = viewport - lines + m_Slide; // where the oldest line's top is, in the box
            foreach (VisualElement child in m_Lines.Children())
            {
                float y = top + child.layout.y;
                if (float.IsNaN(y)) continue;
                float opacity = y >= FadeZone ? 1f : Mathf.Clamp01(y / FadeZone);
                if (!Mathf.Approximately(child.resolvedStyle.opacity, opacity)) child.style.opacity = opacity;
            }
        }

        private void UpdateHint()
        {
            if (m_Hint == null || m_Bindings == null) return;
            bool gamepad = !UsingKeyboard();
            string key = gamepad ? ControlNames.Gamepad(m_Bindings.Gamepad.OpenChat) : ControlNames.Keyboard(m_Bindings.Keyboard.OpenChat);
            string text = string.IsNullOrEmpty(key) ? "" : Loc.F("[{0}] Chat", key);
            if (m_Hint.text != text) m_Hint.text = text;
        }

        // =====================================================================================
        // Building the UI (once per scene)
        // =====================================================================================

        private void ForgetUI()
        {
            UnguardMenus();
            m_Document = null;
            m_Root = m_Box = m_Viewport = m_Lines = m_InputBox = m_Builder = null;
            m_Hint = m_Counter = m_BuilderHelp = null;
            m_Field = null;
            m_NameColumn = m_ShouldColumn = m_ActionColumn = m_WhenColumn = m_PresetColumn = null;
            m_Slide = 0f;
        }

        private bool BuildUI()
        {
            m_NextPanelSearch = Time.unscaledTime + 0.5f;

            PanelSettings panelSettings = null;
            foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (doc.panelSettings != null) { panelSettings = doc.panelSettings; break; }
            }
            if (panelSettings == null) return false; // the scene's UI isn't up yet - try again shortly

            var go = new GameObject("ChatOverlay");
            SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
            go.SetActive(false);
            m_Document = go.AddComponent<UIDocument>();
            m_Document.panelSettings = panelSettings;
            m_Document.sortingOrder = 80f;
            go.SetActive(true);

            m_Root = m_Document.rootVisualElement;
            m_Root.style.flexGrow = 1;
            m_Root.pickingMode = PickingMode.Ignore;

            BuildBuilder();
            BuildBox();

            foreach (ChatLine line in ChatLog.Lines) AddLineLabel(line, slide: false);
            GameFont.Apply(m_Root);
            return true;
        }

        private void BuildBox()
        {
            m_Box = new VisualElement { name = "chat-box", pickingMode = PickingMode.Ignore };
            m_Box.style.position = Position.Absolute;
            m_Box.style.left = EdgeMargin;
            m_Box.style.bottom = EdgeMargin;
            m_Box.style.width = Length.Percent(WidthPercent);
            m_Box.style.height = Length.Percent(HeightPercent);
            m_Box.style.paddingLeft = m_Box.style.paddingRight = 6;
            m_Box.style.paddingTop = m_Box.style.paddingBottom = 4;
            m_Box.style.borderTopLeftRadius = m_Box.style.borderTopRightRadius = 6f;
            m_Box.style.borderBottomLeftRadius = m_Box.style.borderBottomRightRadius = 6f;
            m_Root.Add(m_Box);

            m_Viewport = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Viewport.style.flexGrow = 1;
            m_Viewport.style.overflow = Overflow.Hidden;
            m_Box.Add(m_Viewport);

            m_Lines = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Lines.style.position = Position.Absolute;
            m_Lines.style.left = 0;
            m_Lines.style.right = 0;
            m_Lines.style.bottom = 0;
            m_Viewport.Add(m_Lines);

            m_InputBox = new VisualElement { pickingMode = PickingMode.Ignore };
            m_InputBox.style.height = InputHeight;
            m_InputBox.style.marginTop = 4;
            m_InputBox.style.flexShrink = 0;
            m_InputBox.style.justifyContent = Justify.Center;
            m_Box.Add(m_InputBox);

            m_Hint = new Label { pickingMode = PickingMode.Ignore };
            m_Hint.style.fontSize = 13;
            m_Hint.style.color = HintColor;
            m_Hint.style.paddingLeft = 6;
            m_Hint.style.unityTextAlign = TextAnchor.MiddleLeft;
            m_Hint.style.height = InputHeight;
            m_Hint.style.backgroundColor = new Color(0f, 0f, 0f, 0.3f);
            m_Hint.style.borderTopLeftRadius = m_Hint.style.borderTopRightRadius = 4f;
            m_Hint.style.borderBottomLeftRadius = m_Hint.style.borderBottomRightRadius = 4f;
            m_Hint.style.textShadow = new TextShadow { offset = new Vector2(1f, 1f), blurRadius = 2f, color = new Color(0f, 0f, 0f, 0.9f) };
            m_InputBox.Add(m_Hint);

            m_Field = new TextField { maxLength = ChatLog.MaxLength, multiline = false };
            m_Field.style.display = DisplayStyle.None;
            m_Field.style.height = InputHeight;
            m_Field.style.marginLeft = m_Field.style.marginRight = 0;
            m_Field.style.marginTop = m_Field.style.marginBottom = 0;
            m_Field.style.fontSize = FontSize;
            VisualElement input = m_Field.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.backgroundColor = InputBackground;
                input.style.color = Color.white;
                input.style.borderTopWidth = input.style.borderBottomWidth = 1.5f;
                input.style.borderLeftWidth = input.style.borderRightWidth = 1.5f;
                input.style.borderTopColor = input.style.borderBottomColor = Color.white;
                input.style.borderLeftColor = input.style.borderRightColor = Color.white;
                input.style.borderTopLeftRadius = input.style.borderTopRightRadius = 4f;
                input.style.borderBottomLeftRadius = input.style.borderBottomRightRadius = 4f;
                input.style.paddingLeft = 6;
            }
            m_InputBox.Add(m_Field);

            m_Counter = new Label { pickingMode = PickingMode.Ignore };
            m_Counter.style.position = Position.Absolute;
            m_Counter.style.right = 6;
            m_Counter.style.top = -18;
            m_Counter.style.fontSize = 11;
            m_Counter.style.color = HintColor;
            m_InputBox.Add(m_Counter);
        }

        private void BuildBuilder()
        {
            m_Builder = new VisualElement { name = "chat-builder", pickingMode = PickingMode.Ignore };
            m_Builder.style.position = Position.Absolute;
            m_Builder.style.left = m_Builder.style.right = m_Builder.style.top = m_Builder.style.bottom = 0;
            m_Builder.style.backgroundColor = new Color(0f, 0f, 0f, 0.35f);
            m_Builder.style.display = DisplayStyle.None;
            m_Root.Add(m_Builder);

            // The line every list's chosen item sits on, across the middle of the screen.
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.position = Position.Absolute;
            row.style.left = 0;
            row.style.right = 0;
            row.style.top = Length.Percent(50);
            row.style.marginTop = -RowHeight * 0.5f;
            row.style.height = RowHeight;
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.Center;
            row.style.alignItems = Align.FlexStart;
            row.style.overflow = Overflow.Visible;
            m_Builder.Add(row);

            m_NameColumn = MakeColumn(row);
            m_ShouldColumn = MakeColumn(row);
            m_ActionColumn = MakeColumn(row);
            m_WhenColumn = MakeColumn(row);
            m_PresetColumn = MakeColumn(row);

            m_BuilderHelp = new Label { pickingMode = PickingMode.Ignore };
            m_BuilderHelp.style.position = Position.Absolute;
            m_BuilderHelp.style.left = 0;
            m_BuilderHelp.style.right = 0;
            m_BuilderHelp.style.bottom = Length.Percent(24);
            m_BuilderHelp.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_BuilderHelp.style.fontSize = 15;
            m_BuilderHelp.style.color = new Color(0.9f, 0.9f, 0.9f);
            m_BuilderHelp.style.textShadow = new TextShadow { offset = new Vector2(1f, 1f), blurRadius = 2f, color = new Color(0f, 0f, 0f, 0.9f) };
            m_Builder.Add(m_BuilderHelp);
        }

        // =====================================================================================
        // Keeping the menus out of it
        // =====================================================================================

        /// <summary>
        /// Holds back the UI's own Submit / Cancel / move events (the whole panel - every screen's UI shares it)
        /// while the chat is open, just after it closes, and for the Enter that opens it - so Enter doesn't
        /// also press the lobby's Start Match, Esc / B don't also Leave, and the d-pad doesn't move the menus.
        /// </summary>
        private void GuardMenus()
        {
            VisualElement tree = m_Root?.panel?.visualTree;
            if (tree == null) return;
            m_GuardedTree = tree;
            tree.RegisterCallback<NavigationSubmitEvent>(HoldSubmit, TrickleDown.TrickleDown);
            tree.RegisterCallback<NavigationCancelEvent>(HoldCancel, TrickleDown.TrickleDown);
            tree.RegisterCallback<NavigationMoveEvent>(HoldMove, TrickleDown.TrickleDown);
        }

        private void UnguardMenus()
        {
            if (m_GuardedTree == null) return;
            m_GuardedTree.UnregisterCallback<NavigationSubmitEvent>(HoldSubmit, TrickleDown.TrickleDown);
            m_GuardedTree.UnregisterCallback<NavigationCancelEvent>(HoldCancel, TrickleDown.TrickleDown);
            m_GuardedTree.UnregisterCallback<NavigationMoveEvent>(HoldMove, TrickleDown.TrickleDown);
            m_GuardedTree = null;
        }

        private void HoldSubmit(NavigationSubmitEvent evt)
        {
            bool openingWithEnter = OpenPressed() == 1 && CanOpen();
            if (IsOpen || Time.unscaledTime < s_GuardUntil || openingWithEnter) evt.StopImmediatePropagation();
        }

        private void HoldCancel(NavigationCancelEvent evt)
        {
            if (IsOpen || Time.unscaledTime < s_GuardUntil) evt.StopImmediatePropagation();
        }

        private void HoldMove(NavigationMoveEvent evt)
        {
            if (IsOpen) evt.StopImmediatePropagation();
        }
    }
}
