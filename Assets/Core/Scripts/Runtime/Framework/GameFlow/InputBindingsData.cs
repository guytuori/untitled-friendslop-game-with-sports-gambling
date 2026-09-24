using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keyboard+mouse commands that ChangeKeybindingsController still stores cosmetically, purely for its
    /// own settings-screen JSON - the "real" movement/action commands (Forwards/Back/Left/Right/Jump/
    /// Run/Grab/OpenMenu) moved to actual Input System binding overrides (see
    /// <see cref="InputBindingOverridesStore"/> below) once real gameplay input started reading from this
    /// screen's rebinds. What's left here is the Wager commands, which don't correspond to any action in
    /// GameplayInputSystem_Actions yet - see ChangeKeybindingsController for how each field maps to a row.
    ///
    /// Every field stores the bare Input System control name for a <see cref="UnityEngine.InputSystem.Keyboard"/>
    /// control (e.g. "1", "w", "escape" - i.e. what <c>KeyControl.name</c> returns), the same format
    /// InputAction binding paths use after the "&lt;Keyboard&gt;/" device prefix - kept consistent with the
    /// real bindings below so both go through the same display formatting.
    /// </summary>
    [Serializable]
    public class KeyboardBindings
    {
        public string WagerOption1 = "1";
        public string WagerOption2 = "2";
        public string WagerOption3 = "3";
    }

    /// <summary>
    /// Gamepad Wager commands - see <see cref="KeyboardBindings"/> above for why only the Wager commands
    /// live here now. Every field stores the bare Input System control name for a
    /// <see cref="UnityEngine.InputSystem.Gamepad"/> control ("leftTrigger", "rightShoulder", etc. - i.e.
    /// what the control's own <c>.name</c> returns), matching the format used after the "&lt;Gamepad&gt;/"
    /// device prefix in a real binding path.
    /// </summary>
    [Serializable]
    public class GamepadBindings
    {
        public string WagerCursorUp = "leftTrigger";
        public string WagerCursorDown = "rightTrigger";
        public string WagerSelect = "rightShoulder";
    }

    /// <summary>
    /// The Audio Settings screen's four sliders (Master, Music, Sound Effects, Voice), each an int 0-10
    /// representing 0%-100% in 10% steps - i.e. the 11 notches from the spec, with the slider's raw
    /// integer value directly being "tens of a percent" (7 == 70%). All default to 10 (100%), so a fresh
    /// install plays at full volume until the player turns something down.
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
        public int MasterVolume = 10;
        public int MusicVolume = 10;
        public int SoundEffectsVolume = 10;
        public int VoiceVolume = 10;
    }

    [Serializable]
    public class InputBindingsData
    {
        public KeyboardBindings Keyboard = new KeyboardBindings();
        public GamepadBindings Gamepad = new GamepadBindings();
        public AudioSettingsData Audio = new AudioSettingsData();
    }

    /// <summary>
    /// Loads/saves <see cref="InputBindingsData"/> (the cosmetic Wager-only bindings) as JSON at
    /// Application.persistentDataPath - the standard per-player, per-machine location for save/config
    /// data that lives outside the Assets folder (and so outside source control), which is what lets it
    /// persist between sessions the way a save file does.
    ///
    /// If the file doesn't exist yet (first launch, or it was deleted) or fails to parse for any reason,
    /// Load() falls back to InputBindingsData's own field initializers - see KeyboardBindings and
    /// GamepadBindings above for what those default bindings actually are.
    ///
    /// This only covers the Wager commands. The real movement/action commands are handled by
    /// <see cref="InputBindingOverridesStore"/> below, via Unity's own Input System binding-override
    /// mechanism rather than a hand-rolled JSON schema.
    /// </summary>
    public static class InputBindingsStore
    {
        private const string FileName = "keybindings.json";

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
    /// keybindings.json so a corrupt/missing one doesn't affect the other.
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
        }
    }
}
