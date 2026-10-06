using UnityEngine;
using UnityEngine.InputSystem;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Handles core player input using Unity's Input System and broadcasts actions via GameEvents.
    /// This component handles core movement inputs (Move, Look, Jump, Sprint).
    ///
    /// Local same-PC testing: by default every instance responds to any connected Keyboard/Mouse/Gamepad
    /// (whatever the "Keyboard&Mouse"/"Gamepad" control schemes bind - see GameplayInputSystem_Actions),
    /// which is what a normal remote player wants. When testing two clients side by side on one machine,
    /// that means both windows fight over the same WASD keys and the same gamepad. Launching the second
    /// window/build with the "-player2input" command-line argument switches ONLY that instance's owned
    /// player to the "Keyboard_P2" control scheme instead (T/F/G/H to move, R to grab, keyboard+mouse only,
    /// no gamepad at all) - see AlternateBindingsGroup and the new bindings tagged "Keyboard_P2" in the
    /// .inputactions asset. Add more command-line-selected schemes here the same way if a third local
    /// player is ever needed.
    ///
    /// Rebindable controls: this asset's bindings can be overridden at runtime by the player via the
    /// Change Keybindings screen (see ChangeKeybindingsController), which persists overrides through
    /// InputBindingOverridesStore. ApplySavedOverridesIfAny below applies whatever was last saved, right
    /// after this instance's own GameplayInputSystem_Actions is constructed - so a player's rebinds take
    /// effect the moment gameplay starts, not just on the settings screen that changed them.
    /// </summary>
    public class CoreInputHandler : CoreNetworkBehaviour
    {
        #region Fields

        private const string AlternateBindingsCommandLineArg = "-player2input";
        private const string AlternateBindingsGroup = "Keyboard_P2";

        [Header("Core Game Events")]
        [Tooltip("Raised when the player provides movement input.")]
        [SerializeField] private Vector2Event onMoveInput;
        [Tooltip("Raised when the player provides look/camera input.")]
        [SerializeField] private Vector2Event onLookInput;
        [Tooltip("Raised when the jump button is pressed.")]
        [SerializeField] private GameEvent onJumpPressed;
        [Tooltip("Raised when the jump button is released.")]
        [SerializeField] private GameEvent onJumpReleased;
        [Tooltip("Raised when the sprint state changes (pressed or released).")]
        [SerializeField] private BoolEvent onSprintStateChanged;
        [Tooltip("Raised when the grab state changes (held or released) - see PoleGrabAbility / WallClimbAbility.")]
        [SerializeField] private BoolEvent onGrabStateChanged;
        [Tooltip("Raised when the primary action button is pressed.")]
        [SerializeField] private GameEvent onPrimaryActionPressed;
        [Tooltip("Raised when the primary action button is released.")]
        [SerializeField] private GameEvent onPrimaryActionReleased;
        [Tooltip("Raised when the menu button is pressed.")]
        [SerializeField] private GameEvent onMenuPressed;

        private GameplayInputSystem_Actions m_InputActions;

        /// <summary>
        /// Raised (with the control that was pressed) when the local player presses the Menu action - Esc /
        /// gamepad Start by default. InGameMenu opens and closes the Escape menu with it. Still raised while
        /// gameplay input is blocked.
        /// </summary>
        public static event System.Action<InputControl> MenuPressed;

        /// <summary>True while a menu (the Escape menu) has taken over the local player's input.</summary>
        public static bool GameplayInputBlocked { get; private set; }

        private static event System.Action s_Blocked;

        /// <summary>
        /// The right stick's current position (raw, -1..1), raised whenever it changes - including back to
        /// zero. Gamepad look doesn't go through onLookInput: that event carries per-frame deltas (right for
        /// the mouse), but a stick reports a position that only changes when the stick moves, so a stick
        /// held still used to turn the camera once and then stop. CoreCameraController keeps the latest
        /// position and turns the camera every frame from it (see its Gamepad Look settings).
        /// </summary>
        public event System.Action<Vector2> StickLookChanged;

        #endregion

        #region Unity Lifecycle & Network Callbacks

        private void Awake()
        {
            m_InputActions = new GameplayInputSystem_Actions();

            // Picks up whatever bindings the player last saved from the Change Keybindings screen (see
            // the class summary's "Rebindable controls" note) - a no-op, falling back to this asset's
            // own defaults, if nothing's been saved yet.
            InputBindingOverridesStore.ApplySavedOverrides(m_InputActions.asset);
        }

        public override void OnNetworkSpawn()
        {
            if (IsOwner && m_InputActions != null)
            {
                ApplyAlternateBindingsIfRequested();
                RegisterInputActions();
                m_InputActions.Player.Enable();
                s_Blocked += ReleaseEverything;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner && m_InputActions != null)
            {
                m_InputActions.Player.Disable();
                UnregisterInputActions();
                s_Blocked -= ReleaseEverything;
            }
        }

        /// <summary>
        /// Blocks (or unblocks) every gameplay action except Menu for the local player - used while the
        /// Escape menu is open, so the stick/WASD/mouse drive the menu instead of the character. Blocking
        /// also lets go of anything held (movement, look, sprint, grab, jump).
        /// </summary>
        public static void SetGameplayInputBlocked(bool blocked)
        {
            if (GameplayInputBlocked == blocked) return;
            GameplayInputBlocked = blocked;
            if (blocked) s_Blocked?.Invoke();
        }

        private void ReleaseEverything()
        {
            onMoveInput?.Raise(Vector2.zero);
            onLookInput?.Raise(Vector2.zero);
            StickLookChanged?.Invoke(Vector2.zero);
            onSprintStateChanged?.Raise(false);
            onGrabStateChanged?.Raise(false);
            onJumpReleased?.Raise();
            onPrimaryActionReleased?.Raise();
        }

        #endregion

        #region Local Testing: Alternate Bindings

        /// <summary>
        /// If this process was launched with "-player2input" on the command line, restricts this owned
        /// player's input to the "Keyboard_P2" control scheme (T/F/G/H move, R grab, keyboard+mouse only)
        /// instead of the default "Keyboard&Mouse"/"Gamepad" schemes. Only ever affects this one local
        /// instance's owned player - remote players are never touched, since input is only ever wired up
        /// for the owner (see OnNetworkSpawn above).
        /// </summary>
        private void ApplyAlternateBindingsIfRequested()
        {
            if (!ShouldUseAlternateBindings()) return;

            m_InputActions.asset.bindingMask = InputBinding.MaskByGroup(AlternateBindingsGroup);

            var devices = new System.Collections.Generic.List<InputDevice>();
            if (Keyboard.current != null) devices.Add(Keyboard.current);
            if (Mouse.current != null) devices.Add(Mouse.current);
            m_InputActions.asset.devices = devices.ToArray();
        }

        private static bool ShouldUseAlternateBindings()
        {
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, AlternateBindingsCommandLineArg, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region Input Registration

        private void RegisterInputActions()
        {
            m_InputActions.Player.Move.performed += HandleMove;
            m_InputActions.Player.Move.canceled += HandleMove;

            m_InputActions.Player.Look.performed += HandleLook;
            m_InputActions.Player.Look.canceled += HandleLook;

            m_InputActions.Player.Jump.performed += HandleJumpPressed;
            m_InputActions.Player.Jump.canceled += HandleJumpReleased;

            m_InputActions.Player.Sprint.started += HandleSprintState;
            m_InputActions.Player.Sprint.canceled += HandleSprintState;

            m_InputActions.Player.Grab.started += HandleGrabState;
            m_InputActions.Player.Grab.canceled += HandleGrabState;

            m_InputActions.Player.PrimaryAction.started += HandlePrimaryActionPressed;
            m_InputActions.Player.PrimaryAction.canceled += HandlePrimaryActionReleased;

            m_InputActions.Player.Menu.performed += HandleMenuPressed;
        }

        private void UnregisterInputActions()
        {
            m_InputActions.Player.Move.performed -= HandleMove;
            m_InputActions.Player.Move.canceled -= HandleMove;

            m_InputActions.Player.Look.performed -= HandleLook;
            m_InputActions.Player.Look.canceled -= HandleLook;

            m_InputActions.Player.Jump.performed -= HandleJumpPressed;
            m_InputActions.Player.Jump.canceled -= HandleJumpReleased;

            m_InputActions.Player.Sprint.started -= HandleSprintState;
            m_InputActions.Player.Sprint.canceled -= HandleSprintState;

            m_InputActions.Player.Grab.started -= HandleGrabState;
            m_InputActions.Player.Grab.canceled -= HandleGrabState;

            m_InputActions.Player.PrimaryAction.started -= HandlePrimaryActionPressed;
            m_InputActions.Player.PrimaryAction.canceled -= HandlePrimaryActionReleased;

            m_InputActions.Player.Menu.performed -= HandleMenuPressed;
        }

        #endregion

        #region Input Handlers

        private void HandleMove(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onMoveInput?.Raise(context.ReadValue<Vector2>()); }
        private void HandleLook(InputAction.CallbackContext context)
        {
            if (ShouldIgnore(context)) return;

            InputDevice device = context.control.device;
            if (device is Gamepad || device is Joystick)
            {
                StickLookChanged?.Invoke(context.ReadValue<Vector2>()); // a held position, not a delta
                return;
            }

            onLookInput?.Raise(context.ReadValue<Vector2>()); // mouse: a per-frame delta
        }
        private void HandleJumpPressed(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onJumpPressed?.Raise(); }
        private void HandleJumpReleased(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onJumpReleased?.Raise(); }
        private void HandleSprintState(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onSprintStateChanged?.Raise(context.ReadValueAsButton()); }
        private void HandleGrabState(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onGrabStateChanged?.Raise(context.ReadValueAsButton()); }
        private void HandlePrimaryActionPressed(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onPrimaryActionPressed?.Raise(); }
        private void HandlePrimaryActionReleased(InputAction.CallbackContext context) { if (!ShouldIgnore(context)) onPrimaryActionReleased?.Raise(); }
        private void HandleMenuPressed(InputAction.CallbackContext context)
        {
            if (ShouldIgnoreGamepadWhileUnfocused(context)) return;
            onMenuPressed?.Raise();
            MenuPressed?.Invoke(context.control);
        }

        /// <summary>
        /// Local multi-instance testing: each window/process keeps simulating even when it isn't the
        /// focused one (NetworkManager's Run In Background is on, so a host/client's connection survives
        /// being alt-tabbed away from), and unlike keyboard/mouse - which the OS only ever delivers to
        /// whichever window is focused - a physical gamepad's input reaches every running instance
        /// regardless of which window has focus. Left unchecked, one gamepad drives whichever window last
        /// read its state, including a window sitting in the background. Only gamepad input is filtered
        /// here - keyboard/mouse don't need it, since the OS already scopes those correctly, and dropping
        /// them here too would risk a held key never sending its "release" and leaving movement stuck on.
        /// </summary>
        private static bool ShouldIgnoreGamepadWhileUnfocused(InputAction.CallbackContext context)
        {
            return !Application.isFocused && context.control.device is Gamepad;
        }

        /// <summary>Gameplay actions (everything but Menu) are dropped while blocked, or for an unfocused gamepad.</summary>
        private static bool ShouldIgnore(InputAction.CallbackContext context)
        {
            return GameplayInputBlocked || ShouldIgnoreGamepadWhileUnfocused(context);
        }

        #endregion
    }
}
