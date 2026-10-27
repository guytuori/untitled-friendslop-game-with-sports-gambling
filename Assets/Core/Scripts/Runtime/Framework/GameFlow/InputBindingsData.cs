using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keyboard+mouse commands that ChangeKeybindingsController stores here in settings.json rather than as
    /// Input System binding overrides, because they don't have an action in GameplayInputSystem_Actions
    /// (yet): Interact, Use Item, the three Bet buttons, Dismiss Current Bet and Open Item Menu. The
    /// commands that DO have a real action (Move, Jump, Parkour = Grab, Toggle Walk = Sprint, Open Escape
    /// Menu = Menu) are real binding overrides instead (see <see cref="InputBindingOverridesStore"/> below).
    /// See ChangeKeybindingsController for how each field maps to a row.
    ///
    /// Controls overhaul (2026-10-12): the old WagerYes/WagerNo/DismissWager fields were renamed to
    /// BetYes/BetNo/DismissBet when the new defaults came in, so older saves fall back to the new defaults
    /// instead of keeping bindings that now clash with Interact / Use Item.
    ///
    /// Every field stores the bare Input System control name for a <see cref="UnityEngine.InputSystem.Keyboard"/>
    /// control (e.g. "1", "w", "escape" - i.e. what <c>KeyControl.name</c> returns), the same format
    /// InputAction binding paths use after the "&lt;Keyboard&gt;/" device prefix - kept consistent with the
    /// real bindings below so both go through the same display formatting.
    /// </summary>
    [Serializable]
    public class KeyboardBindings
    {
        public string Interact = "e";
        public string UseItem = "r";

        // Bets are a pop-up ticker answered without pausing: yes, no, or dismiss it. Bet Multiplier cycles
        // the stake x1 -> x3 -> x5 -> x10 -> x1 (it doesn't reset after a bet).
        public string BetYes = "1";
        public string BetNo = "2";
        public string BetMultiplier = "3";
        public string DismissBet = "4";

        // Practice modes only: the list of every item, to put one straight into the item slot (PracticeItemMenu).
        // Was Open Wagers Menu until 2026-10-25 (renamed, so an old save of that key falls back to this default).
        public string OpenItemMenu = "5";

        // The mouse wheel always zooms too (CoreCameraController); these are for holding a key instead.
        public string CameraZoomIn = "equals";
        public string CameraZoomOut = "minus";
    }

    /// <summary>
    /// Gamepad commands that aren't Input System actions yet - see <see cref="KeyboardBindings"/> above.
    /// Face buttons use this project's Nintendo-style names: buttonWest = Y, buttonNorth = X, buttonEast = A,
    /// buttonSouth = B. Every field stores the bare Input System control name for a
    /// <see cref="UnityEngine.InputSystem.Gamepad"/> control ("leftTrigger", "rightShoulder", etc. - i.e.
    /// what the control's own <c>.name</c> returns), matching the format used after the "&lt;Gamepad&gt;/"
    /// device prefix in a real binding path.
    /// </summary>
    [Serializable]
    public class GamepadBindings
    {
        public string Interact = "rightShoulder";   // R1
        public string UseItem = "leftTrigger";      // L2

        public string BetYes = "buttonWest";        // left face button (Y on a Switch)
        public string BetNo = "buttonNorth";        // top face button (X)
        public string BetMultiplier = "buttonEast"; // right face button (A)
        public string DismissBet = "rightTrigger";  // R2

        public string OpenItemMenu = "select";      // Select (- on a Switch) - practice item menu

        // Held to zoom the camera in / out (CoreCameraController). The right stick already aims.
        public string CameraZoomIn = "dpad/up";
        public string CameraZoomOut = "dpad/down";

        /// <summary>Change Keybindings' ENABLE RUMBLE checkbox (gamepad layout only). On by default. Read at runtime through <see cref="GamepadRumble"/>.</summary>
        public bool RumbleEnabled = true;
    }

    /// <summary>
    /// The Audio Settings screen's four sliders (Master, Music, Sound Effects, Voice), each an int 0-10
    /// representing 0%-100% in 10% steps - i.e. the 11 notches from the spec, with the slider's raw
    /// integer value directly being "tens of a percent" (7 == 70%). Master defaults to 3 (30%) and the
    /// other three to 10 (100%), so a fresh install starts at a comfortable overall level.
    ///
    /// <see cref="UISoundEffectsEnabled"/> is the Audio screen's Enable UI Sound Effects checkbox: when
    /// off, every menu/UI click sound (AudioCategory.UISoundEffects) is fully muted regardless of the
    /// sliders. On by default.
    ///
    /// Music/SoundEffects/Voice apply only to sounds explicitly tagged with the matching AudioCategory
    /// when they're played (see AudioVolumeService) - NOT by detecting which folder a clip's source asset
    /// lives in. Unity doesn't retain an AudioClip's original import folder at runtime in a built player,
    /// so there's no API to check "did this come from Assets/Music" the way the spec's wording literally
    /// describes; explicit per-clip tagging at the call site is the closest achievable equivalent, and is
    /// what AudioSettingsController/AudioVolumeService actually implement.
    /// </summary>
    [Serializable]
    public class AudioSettingsData
    {
        public int MasterVolume = 3;
        public int MusicVolume = 10;
        public int SoundEffectsVolume = 10;
        public int VoiceVolume = 10;
        public bool UISoundEffectsEnabled = true;
    }

    /// <summary>
    /// The Graphics Settings screen's four dropdowns (Resolution, Frame Rate, VSync, Windowed Mode).
    /// Stored as concrete values (a width/height pair, a raw fps number, and two bools) rather than raw
    /// dropdown indices, so the saved JSON stays self-describing and stable even if
    /// GraphicsSettingsController's own choice ordering ever changes - see that class for how each
    /// dropdown's index maps to and from these fields. Defaults (1920x1080, 60fps, VSync off, Full Screen)
    /// match the spec's stated defaults exactly.
    ///
    /// FieldOfView (added 2026-10-16) is the in-game camera's vertical field of view in degrees, 60-120 (the
    /// Field of View slider). 80 is the [BB] FreeLook camera's own lens setting. Applied every frame by
    /// CoreCameraController; read it through <see cref="ClampedFieldOfView"/> so an edited or old
    /// settings.json can't push it out of range.
    /// </summary>
    [Serializable]
    public class GraphicsSettingsData
    {
        public const int MinFieldOfView = 60;
        public const int MaxFieldOfView = 120;
        public const int DefaultFieldOfView = 80;

        public int ResolutionWidth = 1920;
        public int ResolutionHeight = 1080;
        public int FrameRate = 60;
        public bool VSyncEnabled = false;
        public bool Fullscreen = true;
        public int FieldOfView = DefaultFieldOfView;

        /// <summary>
        /// Default Zoom (added 2026-10-27): where the camera zoom starts each time the game is launched, as a
        /// percentage of CoreCameraController's zoom range - 0 = closest (3 m), 100 = furthest. 80 is the camera's
        /// own default (the camera mode's Camera Distance, 12 m in [BB] FreeLook).
        /// </summary>
        public const int MinDefaultZoom = 0;
        public const int MaxDefaultZoom = 100;
        public const int StandardDefaultZoom = 80;

        public int DefaultZoom = StandardDefaultZoom;

        public int ClampedFieldOfView => FieldOfView <= 0 ? DefaultFieldOfView : Mathf.Clamp(FieldOfView, MinFieldOfView, MaxFieldOfView);

        public int ClampedDefaultZoom => Mathf.Clamp(DefaultZoom, MinDefaultZoom, MaxDefaultZoom);
    }

    /// <summary>
    /// The Gameplay Settings screen (Settings > Gameplay, added 2026-10-12). Read at runtime through
    /// <see cref="GameplaySettings"/>.
    ///   - Language: an <see cref="Loc"/> language code ("en", "es", "it", "fr", "de", "pt"). English default.
    ///   - ShowDisplayNames: moved here from Change Profile - off shows other players as "Player 1", ...
    ///   - MatureLanguageFilter: bad words in player names show as *** (NameFilter.Mask). On by default.
    ///   - ArachnophobiaMode / ArachnophiliaMode: never both on. Nothing reads them yet - phobia mode will swap
    ///     any spiders for something else, philia mode swaps some objects for obviously fake cartoon spiders.
    /// </summary>
    [Serializable]
    public class GameplaySettingsData
    {
        public string Language = Loc.DefaultLanguageCode;
        public bool ShowDisplayNames = true;
        public bool MatureLanguageFilter = true;
        public bool ArachnophobiaMode = false;
        public bool ArachnophiliaMode = false;

        /// <summary>0 Off, 1 Protanopia, 2 Deuteranopia, 3 Tritanopia (see ColorblindFilter).</summary>
        public int ColorblindMode = 0;

        public GameplaySettingsData Clone() => (GameplaySettingsData)MemberwiseClone();
    }

    [Serializable]
    public class InputBindingsData
    {
        public KeyboardBindings Keyboard = new KeyboardBindings();
        public GamepadBindings Gamepad = new GamepadBindings();
        public AudioSettingsData Audio = new AudioSettingsData();
        public GraphicsSettingsData Graphics = new GraphicsSettingsData();
        public GameplaySettingsData Gameplay = new GameplaySettingsData();
    }

    /// <summary>
    /// Loads/saves <see cref="InputBindingsData"/> as JSON at Application.persistentDataPath - the
    /// standard per-player, per-machine location for save/config data that lives outside the Assets folder
    /// (and so outside source control), which is what lets it persist between sessions the way a save file
    /// does. Named settings.json (not keybindings.json - its name for a while, back when this store only
    /// covered the Wager-command bindings below) since every GameFlow settings screen now shares this one
    /// file: the cosmetic Keyboard/Gamepad Wager bindings, the Audio screen's four volume sliders
    /// (<see cref="AudioSettingsData"/>), and the Graphics screen's four dropdowns
    /// (<see cref="GraphicsSettingsData"/>).
    ///
    /// If the file doesn't exist yet (first launch, or it was deleted) or fails to parse for any reason,
    /// Load() falls back to InputBindingsData's own field initializers - see KeyboardBindings and
    /// GamepadBindings above for what those default bindings actually are, and AudioSettingsData/
    /// GraphicsSettingsData for those screens' own defaults.
    ///
    /// The real movement/action commands (as opposed to the cosmetic Wager bindings) are handled by
    /// <see cref="InputBindingOverridesStore"/> below instead, via Unity's own Input System binding-override
    /// mechanism rather than this hand-rolled JSON schema.
    /// </summary>
    public static class InputBindingsStore
    {
        private const string FileName = "settings.json";

        /// <summary>
        /// Raised after settings.json or the input binding overrides are saved - e.g. from Change Keybindings
        /// opened mid-game (Esc > Settings), so anything holding bindings (the player's input, the bet keys,
        /// Interact) picks the new ones up straight away.
        /// </summary>
        public static event Action Saved;

        internal static void RaiseSaved()
        {
            GamepadRumble.Refresh();
            Saved?.Invoke();
        }

        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        public static InputBindingsData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    InputBindingsData data = JsonUtility.FromJson<InputBindingsData>(json);
                    if (data != null)
                    {
                        return data;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[InputBindingsStore] Couldn't read {FilePath}, falling back to defaults: {e.Message}");
            }

            return new InputBindingsData();
        }

        public static void Save(InputBindingsData data)
        {
            try
            {
                File.WriteAllText(FilePath, JsonUtility.ToJson(data, true));
            }
            catch (Exception e)
            {
                Debug.LogError($"[InputBindingsStore] Couldn't write {FilePath}: {e.Message}");
            }
            RaiseSaved();
        }
    }

    /// <summary>
    /// Runtime access to the ENABLE RUMBLE setting for whatever gameplay code adds controller rumble
    /// later - nothing in the project rumbles yet. Route rumble through <see cref="SetMotorSpeeds"/> so
    /// the checkbox is respected automatically. The value is read from settings.json on first use and
    /// re-read after Change Keybindings saves (<see cref="Refresh"/>).
    /// </summary>
    public static class GamepadRumble
    {
        private static bool? s_Enabled;

        public static bool Enabled => s_Enabled ??= InputBindingsStore.Load().Gamepad.RumbleEnabled;

        /// <summary>Re-reads the saved setting, and stops any rumble already running if it's now off.</summary>
        public static void Refresh()
        {
            s_Enabled = null;
            if (!Enabled) Stop();
        }

        /// <summary>Same as Gamepad.current.SetMotorSpeeds, but a no-op while rumble is disabled.</summary>
        public static void SetMotorSpeeds(float lowFrequency, float highFrequency)
        {
            if (!Enabled) return;
            Gamepad.current?.SetMotorSpeeds(lowFrequency, highFrequency);
        }

        public static void Stop()
        {
            Gamepad.current?.ResetHaptics();
        }
    }

    /// <summary>
    /// Persists real Input System binding overrides - the actual rebinds ChangeKeybindingsController
    /// applies to GameplayInputSystem_Actions via InputAction.ApplyBindingOverride for the real
    /// movement/action commands (Forwards/Back/Left/Right/Jump/Run/Grab/OpenMenu on keyboard; Move/Jump/
    /// Run/Grab/OpenMenu on gamepad) - as opposed to the cosmetic Wager-only bindings in
    /// <see cref="InputBindingsStore"/> above.
    ///
    /// Wraps Unity's own InputActionAsset.SaveBindingOverridesAsJson()/LoadBindingOverridesFromJson(),
    /// which already know how to serialize exactly the set of overrides currently applied to an asset (as
    /// opposed to every binding on it) - there's no need for a custom schema here the way
    /// InputBindingsData needed one, since JsonUtility can't serialize Dictionaries and the override data
    /// isn't a simple fixed set of named fields to begin with. Persisted to a separate file from
    /// settings.json so a corrupt/missing one doesn't affect the other.
    /// </summary>
    public static class InputBindingOverridesStore
    {
        private const string FileName = "input-binding-overrides.json";

        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>
        /// Applies whatever overrides were previously saved for <paramref name="asset"/>, if any. Safe to
        /// call even if the file doesn't exist yet (first launch) - the asset is simply left with its
        /// default bindings, same as InputBindingsStore.Load() falling back to defaults.
        /// </summary>
        public static void ApplySavedOverrides(InputActionAsset asset)
        {
            if (asset == null) return;

            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    asset.LoadBindingOverridesFromJson(json);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[InputBindingOverridesStore] Couldn't apply saved overrides from {FilePath}, falling back to defaults: {e.Message}");
            }
        }

        public static void Save(InputActionAsset asset)
        {
            if (asset == null) return;

            try
            {
                string json = asset.SaveBindingOverridesAsJson();
                File.WriteAllText(FilePath, json);
            }
            catch (Exception e)
            {
                Debug.LogError($"[InputBindingOverridesStore] Couldn't write {FilePath}: {e.Message}");
            }
            InputBindingsStore.RaiseSaved();
        }
    }
}
