using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Fixes a project-wide gamepad UI navigation mismatch without touching the shared stock UI actions
    /// asset every EventSystem's InputSystemUIInputModule uses (guid ca9f5fa95ffab41fb9a615ab714db018).
    ///
    /// That asset's Submit/Cancel bindings are Unity's out-of-the-box defaults: Submit on gamepad South,
    /// Cancel on gamepad East. This project's actual pad is Nintendo-style, where South is the player's
    /// own "B" and East is their "A" (see GamepadControlDisplayName in ChangeKeybindingsController for
    /// the same convention) - so the stock defaults are backwards from the real Nintendo confirm/cancel
    /// convention (A/East = confirm, B/South = cancel). Concretely, this used to mean pressing South to
    /// submit a focused button on the Settings screen would ALSO trigger SettingsController's own
    /// South-is-Back shortcut in the same frame, making gamepad-only selection of anything but Back
    /// effectively unusable there.
    ///
    /// Rather than forking the shared asset into a project-local copy (which would mean every scene's
    /// EventSystem needs re-pointing at a new guid, and a second UI actions asset to keep in sync with
    /// the original if Unity ever changes its stock defaults), this applies the swap at runtime via
    /// InputAction.ApplyBindingOverride - the same official rebinding mechanism ChangeKeybindingsController
    /// uses for the real gameplay bindings. It only touches the Gamepad-grouped binding on Submit/Cancel;
    /// the keyboard/mouse bindings (Enter/Space for Submit, Escape for Cancel) are left exactly as they are.
    ///
    /// Call Apply() once per scene, early in Awake, from every GameFlow screen that has its own
    /// EventSystem (MainMenuController, SettingsController, ChangeKeybindingsController) - each scene's
    /// EventSystem is a fresh component (see those classes' summaries for why), so the override needs to
    /// be (re)applied on every scene load rather than once globally.
    /// </summary>
    public static class GamepadUIBindingFix
    {
        /// <summary>
        /// The bare gamepad control name Submit is bound to after Apply() - "A" in this project's
        /// Nintendo-style convention. Named here (rather than inlined below) so the mapping has one
        /// place to change. Note this only governs UI navigation (menus) - it's a completely separate
        /// InputActionAsset from GameplayInputSystem_Actions, so a player is still free to rebind any
        /// gameplay command (Jump, Grab, etc. - see ChangeKeybindingsController) onto East/West/South
        /// without that ever affecting what Submit does in a menu, or vice versa.
        /// </summary>
        public const string SubmitControl = "buttonEast";

        /// <summary>
        /// The bare gamepad control name Cancel is bound to after Apply() - "B" in this project's
        /// Nintendo-style convention. Same purpose and independence from gameplay bindings as
        /// <see cref="SubmitControl"/> above.
        /// </summary>
        public const string CancelControl = "buttonSouth";

        public static void Apply()
        {
            var module = Object.FindFirstObjectByType<InputSystemUIInputModule>();
            if (module == null) return;

            // Swap: Submit moves off South and onto East (the project's "A"), Cancel moves off East and
            // onto South (the project's "B") - matching real Nintendo-pad confirm/cancel convention.
            OverrideGamepadBinding(module.submit.action, $"<Gamepad>/{SubmitControl}");
            OverrideGamepadBinding(module.cancel.action, $"<Gamepad>/{CancelControl}");
        }

        /// <summary>
        /// Finds the one binding on <paramref name="action"/> whose original path targets the Gamepad
        /// device and overrides just that binding to <paramref name="newPath"/>, leaving every other
        /// binding (keyboard, mouse, touch, etc.) on the action untouched.
        /// </summary>
        private static void OverrideGamepadBinding(InputAction action, string newPath)
        {
            if (action == null) return;

            for (int i = 0; i < action.bindings.Count; i++)
            {
                string path = action.bindings[i].path;
                if (path != null && path.StartsWith("<Gamepad>"))
                {
                    action.ApplyBindingOverride(i, newPath);
                    return;
                }
            }
        }
    }
}
