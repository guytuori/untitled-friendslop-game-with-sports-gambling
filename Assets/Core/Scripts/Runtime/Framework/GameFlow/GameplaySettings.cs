using System;
using System.IO;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Runtime access to the Gameplay Settings screen's values (<see cref="GameplaySettingsData"/>, saved in
    /// settings.json with the other settings screens). Loaded on first use; the screen calls
    /// <see cref="Apply"/> when Save Changes is clicked, which also switches the language straight away.
    ///
    /// The language is applied at startup too, before the first scene loads, so the title screen is already
    /// in the saved language.
    /// </summary>
    public static class GameplaySettings
    {
        private static GameplaySettingsData s_Current;

        /// <summary>Raised after <see cref="Apply"/>.</summary>
        public static event Action Changed;

        public static GameplaySettingsData Current
        {
            get
            {
                if (s_Current == null) s_Current = LoadFromDisk();
                return s_Current;
            }
        }

        public static bool ArachnophobiaMode => Current.ArachnophobiaMode;
        public static bool ArachnophiliaMode => Current.ArachnophiliaMode;
        public static bool InvertGliderControls => Current.InvertGliderControls;
        public static bool MessageBuilderChatOnly => Current.MessageBuilderChatOnly;

        /// <summary>Settings > Gameplay > Camera Sensitivity as a multiplier (1 = the camera's own speed).</summary>
        public static float CameraSensitivity => Current.CameraSensitivityMultiplier;

        /// <summary>Settings > Gameplay > Default Zoom, 0-100 % of the camera's zoom range.</summary>
        public static int DefaultZoom => Current.ClampedDefaultZoom;

        /// <summary>Makes <paramref name="settings"/> current (not saved - InputBindingsStore.Save does that).</summary>
        public static void Apply(GameplaySettingsData settings)
        {
            s_Current = settings != null ? settings.Clone() : new GameplaySettingsData();
            if (s_Current.ArachnophobiaMode && s_Current.ArachnophiliaMode) s_Current.ArachnophiliaMode = false;
            if (s_Current.DefaultZoom < 0) s_Current.DefaultZoom = GameplaySettingsData.StandardDefaultZoom;
            Loc.SetLanguage(s_Current.Language);
            Changed?.Invoke();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplySavedLanguage()
        {
            s_Current = null;
            Loc.SetLanguage(Current.Language);
        }

        private static GameplaySettingsData LoadFromDisk()
        {
            InputBindingsData data = InputBindingsStore.Load();
            GameplaySettingsData settings = data.Gameplay ?? new GameplaySettingsData();

            // Show Display Names used to live on the Change Profile screen (PlayerPrefs). Carry an old choice
            // over the first time, before this screen has ever been saved.
            if (!HasSavedGameplaySection() && PlayerPrefs.HasKey(LegacyShowDisplayNamesKey))
            {
                settings.ShowDisplayNames = PlayerPrefs.GetInt(LegacyShowDisplayNamesKey, 1) != 0;
            }

            if (settings.ArachnophobiaMode && settings.ArachnophiliaMode) settings.ArachnophiliaMode = false;
            if (!Loc.IsSupported(settings.Language)) settings.Language = Loc.DefaultLanguageCode;
            settings.ColorblindMode = Mathf.Clamp(settings.ColorblindMode, 0, ColorblindFilter.ModeNames.Length - 1);

            // Default Zoom used to be on the Graphics screen (until 2026-11-13): carry that choice over until this
            // screen saves one of its own.
            if (settings.DefaultZoom < 0)
            {
                int legacy = data.Graphics != null ? data.Graphics.DefaultZoom : GameplaySettingsData.StandardDefaultZoom;
                settings.DefaultZoom = Mathf.Clamp(legacy, GameplaySettingsData.MinDefaultZoom, GameplaySettingsData.MaxDefaultZoom);
            }
            settings.CameraSensitivity = settings.ClampedCameraSensitivity;
            return settings;
        }

        private const string LegacyShowDisplayNamesKey = "Profile.ShowDisplayNames";

        private static bool HasSavedGameplaySection()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "settings.json");
                return File.Exists(path) && File.ReadAllText(path).Contains("\"Gameplay\"");
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
