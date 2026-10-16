using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Reads the controls that are stored as plain control names in settings.json (InputBindingsStore) rather
    /// than as Input System actions - Interact, Use Item, the bet buttons... Takes the bare control name for
    /// each device, e.g. ("e", "rightShoulder").
    /// </summary>
    public static class BoundControls
    {
        /// <summary>The keyboard key or gamepad button was pressed this frame.</summary>
        public static bool WasPressed(string keyboardControl, string gamepadControl)
        {
            if (Keyboard.current != null && !string.IsNullOrEmpty(keyboardControl))
            {
                var key = Keyboard.current.TryGetChildControl<KeyControl>(keyboardControl);
                if (key != null && key.wasPressedThisFrame) return true;
            }

            Gamepad pad = Gamepad.current;
            if (pad != null && !string.IsNullOrEmpty(gamepadControl) && Application.isFocused)
            {
                ButtonControl button = FindButton(pad, gamepadControl);
                if (button != null && button.wasPressedThisFrame) return true;
            }
            return false;
        }

        /// <summary>The keyboard key or gamepad button is being held down.</summary>
        public static bool IsHeld(string keyboardControl, string gamepadControl)
        {
            if (Keyboard.current != null && !string.IsNullOrEmpty(keyboardControl))
            {
                var key = Keyboard.current.TryGetChildControl<KeyControl>(keyboardControl);
                if (key != null && key.isPressed) return true;
            }

            Gamepad pad = Gamepad.current;
            if (pad != null && !string.IsNullOrEmpty(gamepadControl) && Application.isFocused)
            {
                ButtonControl button = FindButton(pad, gamepadControl);
                if (button != null && button.isPressed) return true;
            }
            return false;
        }

        private static ButtonControl FindButton(Gamepad pad, string name)
        {
            var direct = pad.TryGetChildControl<ButtonControl>(name);
            if (direct != null) return direct;
            foreach (InputControl control in pad.allControls)
            {
                if (control is ButtonControl button && control.name == name) return button;
            }
            return null;
        }
    }
}
