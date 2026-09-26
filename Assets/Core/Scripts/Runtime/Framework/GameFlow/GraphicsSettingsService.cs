using System;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Applies the Graphics Settings screen's four dropdowns (Resolution, Frame Rate, VSync, Windowed
    /// Mode) to the actual running game - the Graphics equivalent of AudioVolumeService for the Audio
    /// screen's sliders, following the exact same "live now, only permanent on Save Changes" pattern:
    /// GraphicsSettingsController keeps its own in-memory working copy and calls ApplyLive after every
    /// dropdown change, and only writes it into settings.json (via InputBindingsData.Graphics) when
    /// Save Changes is clicked.
    ///
    /// Loaded at boot via RuntimeInitializeOnLoadMethod(BeforeSceneLoad), same as AudioVolumeService, so a
    /// saved resolution/frame-rate/VSync/windowed setting is already in effect before the very first
    /// scene's Awake runs, regardless of which scene the game actually boots into.
    /// </summary>
    public static class GraphicsSettingsService
    {
        /// <summary>Current live graphics settings - the single source of truth ApplyToScreen reads from. GraphicsSettingsController owns the only writer of this outside of boot.</summary>
        public static GraphicsSettingsData Current { get; private set; } = new GraphicsSettingsData();

        /// <summary>Raised whenever Current changes (a dropdown change, Restore Defaults, or the initial boot load).</summary>
        public static event Action OnSettingsChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeOnLoad()
        {
            Current = InputBindingsStore.Load().Graphics;
            ApplyToScreen(Current);
        }

        /// <summary>
        /// Replaces the live settings, applies them to the actual Screen/QualitySettings state, and
        /// notifies listeners. Doesn't persist anything to disk on its own - that's InputBindingsStore.Save's
        /// job, called separately (with the rest of InputBindingsData) when the player clicks Save Changes
        /// on the Graphics Settings screen.
        /// </summary>
        public static void ApplyLive(GraphicsSettingsData settings)
        {
            Current = settings ?? new GraphicsSettingsData();
            ApplyToScreen(Current);
            OnSettingsChanged?.Invoke();
        }

        /// <summary>
        /// Full Screen maps to FullScreenMode.FullScreenWindow (borderless fullscreen) rather than
        /// ExclusiveFullScreen - the more broadly compatible choice across displays/OSes, and the one that
        /// won't force an exclusive display-mode switch every time this screen's Restore Defaults or a
        /// dropdown change re-applies it live while the player is mid-adjustment.
        ///
        /// Frame rate is applied via Application.targetFrameRate rather than Screen.SetResolution's own
        /// preferred-refresh-rate parameter (deliberately not passed here) - Unity ignores
        /// targetFrameRate entirely whenever QualitySettings.vSyncCount is non-zero, which is exactly the
        /// "VSync overrides the frame rate cap" behavior the spec implies by treating them as two separate
        /// dropdowns rather than one combined setting.
        /// </summary>
        private static void ApplyToScreen(GraphicsSettingsData settings)
        {
            FullScreenMode mode = settings.Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            Screen.SetResolution(settings.ResolutionWidth, settings.ResolutionHeight, mode);

            QualitySettings.vSyncCount = settings.VSyncEnabled ? 1 : 0;
            Application.targetFrameRate = settings.VSyncEnabled ? -1 : settings.FrameRate;
        }
    }
}
