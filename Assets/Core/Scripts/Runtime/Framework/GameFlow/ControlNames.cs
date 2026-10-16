namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// How a bound control is shown to the player (Change Keybindings rows, the bet ticker's key hints).
    /// Takes the bare Input System control name - what comes after "&lt;Keyboard&gt;/" or "&lt;Gamepad&gt;/"
    /// in a binding path ("w", "leftShift", "buttonWest", "rightStickPress", "dpad/up").
    ///
    /// Gamepad face buttons use this project's Nintendo-style labels: South = B, East = A, North = X,
    /// West = Y; shoulders and triggers are L1/R1/L2/R2, stick clicks L3/R3.
    /// </summary>
    public static class ControlNames
    {
        public static string Keyboard(string controlName)
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
                "enter" => "Enter",
                "tab" => "Tab",
                "backspace" => "Backspace",
                "upArrow" => "Up",
                "downArrow" => "Down",
                "leftArrow" => "Left",
                "rightArrow" => "Right",
                "equals" => "=",
                "minus" => "-",
                _ when controlName.Length == 1 => controlName.ToUpperInvariant(),
                _ => char.ToUpperInvariant(controlName[0]) + controlName.Substring(1)
            };
        }

        public static string Gamepad(string controlName)
        {
            if (string.IsNullOrEmpty(controlName)) return controlName;

            return controlName switch
            {
                "leftStick" => Loc.T("Left Stick"),
                "rightStick" => Loc.T("Right Stick"),
                "buttonSouth" => "B",
                "buttonEast" => "A",
                "buttonNorth" => "X",
                "buttonWest" => "Y",
                "leftShoulder" => "L1",
                "rightShoulder" => "R1",
                "leftTrigger" => "L2",
                "rightTrigger" => "R2",
                "leftStickPress" => "L3",
                "rightStickPress" => "R3",
                // Upper case so they can't be mistaken for the menus' "Start" button text by the translator.
                "start" => "START",
                "select" => "SELECT",
                "dpad/up" => Loc.T("D-Pad Up"),
                "dpad/down" => Loc.T("D-Pad Down"),
                "dpad/left" => Loc.T("D-Pad Left"),
                "dpad/right" => Loc.T("D-Pad Right"),
                _ => controlName
            };
        }
    }
}
