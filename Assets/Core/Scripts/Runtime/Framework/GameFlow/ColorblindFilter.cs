using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Gameplay Settings screen's COLORBLIND MODE: a whole-screen color correction for protanopia,
    /// deuteranopia or tritanopia, applied to the 3D world through URP post-processing.
    ///
    /// How: one hidden, scene-surviving global post-processing Volume (highest priority) with a Channel Mixer
    /// override. The mixer's 3x3 matrix is a "daltonization" correction - the colors a player with that kind
    /// of color blindness can't tell apart (worked out with Machado et al. 2009's simulation matrices) get
    /// shifted into colors they can - folded into one matrix, so it costs nothing extra (it's baked into
    /// the color-grading lookup URP already builds). Off just disables the volume.
    ///
    /// Only cameras with post-processing enabled get it (the gameplay camera in [BB] Core has it on).
    /// The UI Toolkit menus and HUD are drawn on top afterwards and aren't filtered - those should not rely
    /// on color alone anyway (text, icons, position).
    /// </summary>
    public static class ColorblindFilter
    {
        public enum Mode { Off = 0, Protanopia = 1, Deuteranopia = 2, Tritanopia = 3 }

        /// <summary>Names for the settings dropdown, in <see cref="Mode"/> order.</summary>
        public static readonly string[] ModeNames = { "Off", "Protanopia", "Deuteranopia", "Tritanopia" };

        // Daltonization matrices (rows = output red/green/blue, columns = input red/green/blue), in percent,
        // as URP's Channel Mixer takes them: M = I + E * (I - S), with S the Machado 2009 simulation matrix for
        // full-strength color blindness and E the usual error-redistribution matrix. Every row sums to 100,
        // so white and greys are unchanged.
        private static readonly float[][,] Matrices =
        {
            null, // Off
            new float[,] { { 100f, 0f, 0f }, { 47.9f, 47.7f, 4.4f }, { 59.7f, -68.9f, 109.2f } },   // Protanopia
            new float[,] { { 100f, 0f, 0f }, { 16.3f, 72.5f, 11.2f }, { 45.5f, -64.6f, 119.1f } },  // Deuteranopia
            new float[,] { { 74.1f, -40.7f, 66.6f }, { 7.5f, 58.5f, 34.0f }, { 0f, 0f, 100f } },    // Tritanopia
        };

        private static Volume s_Volume;
        private static ChannelMixer s_Mixer;
        private static Mode s_Mode;

        public static Mode Current => s_Mode;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            GameplaySettings.Changed -= ApplySaved;
            GameplaySettings.Changed += ApplySaved;
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            SceneManager.sceneLoaded += HandleSceneLoaded;
            ApplySaved();
        }

        private static void ApplySaved() => Apply((Mode)Mathf.Clamp(GameplaySettings.Current.ColorblindMode, 0, Matrices.Length - 1));

        private static void HandleSceneLoaded(Scene scene, LoadSceneMode mode) => Apply(s_Mode);

        /// <summary>Switches the correction on (or off) straight away.</summary>
        public static void Apply(Mode mode)
        {
            s_Mode = mode;
            if (mode == Mode.Off)
            {
                if (s_Volume != null) s_Volume.enabled = false;
                return;
            }

            EnsureVolume();
            float[,] m = Matrices[(int)mode];
            s_Mixer.redOutRedIn.Override(m[0, 0]);
            s_Mixer.redOutGreenIn.Override(m[0, 1]);
            s_Mixer.redOutBlueIn.Override(m[0, 2]);
            s_Mixer.greenOutRedIn.Override(m[1, 0]);
            s_Mixer.greenOutGreenIn.Override(m[1, 1]);
            s_Mixer.greenOutBlueIn.Override(m[1, 2]);
            s_Mixer.blueOutRedIn.Override(m[2, 0]);
            s_Mixer.blueOutGreenIn.Override(m[2, 1]);
            s_Mixer.blueOutBlueIn.Override(m[2, 2]);
            s_Volume.enabled = true;
        }

        private static void EnsureVolume()
        {
            if (s_Volume != null) return;

            var go = new GameObject("ColorblindFilter") { hideFlags = HideFlags.HideInHierarchy };
            go.layer = 0; // Default - the layer the gameplay camera's volume mask includes
            Object.DontDestroyOnLoad(go);

            s_Volume = go.AddComponent<Volume>();
            s_Volume.isGlobal = true;
            s_Volume.priority = 1000f; // above any scene volume
            s_Volume.weight = 1f;

            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Colorblind Correction";
            s_Mixer = profile.Add<ChannelMixer>(true);
            s_Volume.sharedProfile = profile;
        }
    }
}
