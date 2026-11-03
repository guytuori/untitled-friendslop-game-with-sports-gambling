using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// In the gameplay scene, one press of Up / Down on the gamepad can reach the UI as TWO navigation moves, so
    /// the focus jumps two buttons (Esc menu: Up from Change Keybindings lands on Graphics, Up again wraps round
    /// to Change Keybindings). Same family as the doubled Cancel InGameMenu already guards against ("the UI's
    /// Cancel event and the direct read can both see the same press, possibly a frame apart").
    ///
    /// <see cref="Attach"/> (added 2026-11-03) puts a guard at the top of a UI: a second move in the same
    /// direction within a frame / <see cref="Window"/> seconds of the first is thrown away. Holding a direction
    /// still repeats (the UI's repeat is 0.5 s, then every 0.1 s). Used by the Esc menu (InGameMenu) and the
    /// settings screens opened over the game (SettingsOverlay); the menu scenes don't have the problem.
    ///
    /// The first time it throws one away it logs where UI input could be coming from (EventSystems, UI input
    /// modules, gamepads), to track down the real source.
    /// </summary>
    public static class NavigationDedupe
    {
        /// <summary>Seconds within which a second move the same way counts as the same press.</summary>
        public const float Window = 0.06f;

        private static bool s_Logged;

        public static void Attach(VisualElement root, string label)
        {
            if (root == null) return;

            float lastTime = float.NegativeInfinity;
            int lastFrame = -100;
            var lastDirection = NavigationMoveEvent.Direction.None;

            root.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                float now = Time.unscaledTime;
                int frame = Time.frameCount;
                bool repeat = evt.direction == lastDirection && evt.direction != NavigationMoveEvent.Direction.None
                              && (frame - lastFrame <= 1 || now - lastTime < Window);
                if (repeat)
                {
                    evt.StopPropagation();
                    root.focusController?.IgnoreEvent(evt);
                    LogOnce(label, frame - lastFrame, now - lastTime);
                    return;
                }

                lastTime = now;
                lastFrame = frame;
                lastDirection = evt.direction;
            }, TrickleDown.TrickleDown);
        }

        private static void LogOnce(string label, int frames, float seconds)
        {
            if (s_Logged) return;
            s_Logged = true;

            var sb = new StringBuilder();
            sb.Append($"[UI] {label}: ignored a doubled navigation move ({frames} frame(s), {seconds * 1000f:0} ms after the first). Possible sources:");
            foreach (EventSystem es in Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                sb.Append($"\n  EventSystem '{es.name}' in scene '{es.gameObject.scene.name}' (active {es.isActiveAndEnabled}, current {es == EventSystem.current})");
            }
            foreach (InputSystemUIInputModule m in Object.FindObjectsByType<InputSystemUIInputModule>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string asset = m.actionsAsset != null ? m.actionsAsset.name : "none";
                sb.Append($"\n  UI input module on '{m.name}' (active {m.isActiveAndEnabled}, actions '{asset}')");
            }
            foreach (Gamepad pad in Gamepad.all)
            {
                sb.Append($"\n  Gamepad '{pad.displayName}' ({pad.layout}, id {pad.deviceId}, enabled {pad.enabled})");
            }
            Debug.LogWarning(sb.ToString());
        }
    }
}
