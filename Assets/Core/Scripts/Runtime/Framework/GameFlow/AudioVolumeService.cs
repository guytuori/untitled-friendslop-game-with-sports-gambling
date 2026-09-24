using System;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Which volume category a played sound belongs to, for the Audio Settings sliders. Unity does not
    /// retain an AudioClip's original import folder at runtime - there's no API for that in a built
    /// player - so "Music affects anything from Assets/Music" etc. can't be implemented as a runtime path
    /// check. Instead, whoever plays a clip tags it with the category matching where its source asset
    /// lives (see AudioVolumeService.PlayOneShot below, and the refactored click-sound calls in
    /// MainMenuController/SettingsController/ChangeKeybindingsController/AudioSettingsController, all
    /// tagged SoundEffects since their clips are UI click sounds).
    /// </summary>
    public enum AudioCategory
    {
        Music,
        SoundEffects,
        Voice
    }

    /// <summary>
    /// Applies the Audio Settings screen's four sliders (Master/Music/SoundEffects/Voice, each 0-10
    /// representing 0%-100% in 10% steps) as multiplicative volume scalars, per the spec: a played
    /// sound's actual volume is Master% * Category%, so e.g. Master 50% + Music 20% plays music at 10%
    /// (.5 * .2), while Master 50% + SoundEffects 100% still plays sound effects at 50%.
    ///
    /// There's no AudioMixer here - a real mixer asset needs to be hand-authored in the Editor (routing
    /// groups, exposed parameters), which isn't practical to build through file-only device access and
    /// would be overkill for four flat multipliers with no ducking or effects. This is instead just a
    /// static scalar table plus a PlayOneShot helper that applies it directly to the AudioSource it spins
    /// up for each one-shot clip - see PlayOneShot below for why that's a small hand-rolled player rather
    /// than a call to AudioSource.PlayClipAtPoint.
    ///
    /// Loaded at boot via RuntimeInitializeOnLoadMethod(BeforeSceneLoad) so a saved setting is already in
    /// effect for the very first sound of the very first scene, regardless of which scene the game
    /// actually boots into (the Editor can start Play Mode from any scene, and a build's first scene
    /// isn't necessarily AudioSettings or even a GameFlow scene at all).
    /// </summary>
    public static class AudioVolumeService
    {
        /// <summary>
        /// Current live volume settings - the single source of truth every PlayOneShot/GetMultiplier call
        /// reads from. AudioSettingsController owns the only writer of this outside of boot: it keeps its
        /// own in-memory working copy and calls ApplyLive after every slider change, exactly the same
        /// "live now, only permanent on Save Changes" pattern ChangeKeybindingsController uses for real
        /// gameplay rebinds.
        /// </summary>
        public static AudioSettingsData Current { get; private set; } = new AudioSettingsData();

        /// <summary>
        /// Raised whenever Current changes (a slider drag, Restore Defaults, or the initial boot load) -
        /// available for anything that might need to react to a live volume change beyond just the next
        /// PlayOneShot call (e.g. a future looping music/voice AudioSource that needs its own .volume
        /// re-applied on the fly, rather than only reading the multiplier once when it started playing).
        /// </summary>
        public static event Action OnVolumesChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeOnLoad()
        {
            Current = InputBindingsStore.Load().Audio;
        }

        /// <summary>
        /// Replaces the live settings and notifies listeners. Doesn't persist anything to disk on its own
        /// - that's InputBindingsStore.Save's job, called separately (with the rest of InputBindingsData)
        /// when the player clicks Save Changes on the Audio Settings screen.
        /// </summary>
        public static void ApplyLive(AudioSettingsData settings)
        {
            Current = settings ?? new AudioSettingsData();
            OnVolumesChanged?.Invoke();
        }

        /// <summary>
        /// The effective multiplier for a given category - Master% * Category% (each 0-1) - matching the
        /// spec's multiplicative-stacking example (Master 50% + Music 20% = 10% effective music volume).
        /// </summary>
        public static float GetMultiplier(AudioCategory category)
        {
            float master = Current.MasterVolume / 10f;
            float categoryPercent = category switch
            {
                AudioCategory.Music => Current.MusicVolume / 10f,
                AudioCategory.SoundEffects => Current.SoundEffectsVolume / 10f,
                AudioCategory.Voice => Current.VoiceVolume / 10f,
                _ => 1f
            };
            return master * categoryPercent;
        }

        /// <summary>
        /// Plays a one-shot clip, scaled by this category's current effective multiplier - a drop-in
        /// replacement for a raw AudioSource.PlayClipAtPoint(clip, position) call, now tagged with which
        /// volume slider should control it.
        ///
        /// This is a hand-rolled equivalent of PlayClipAtPoint rather than that method itself, because its
        /// own temporary GameObject isn't ours to mark DontDestroyOnLoad. Every GameFlow screen's Back
        /// button (and several other buttons - Host, Client, Settings, Change Keybindings, Audio) plays its
        /// click sound and then immediately calls SceneManager.LoadScene in the same click handler; that
        /// load synchronously destroys everything in the current scene, including a PlayClipAtPoint-created
        /// temp GameObject that was itself only just created this same frame - cutting the clip off before
        /// it's audible at all. Marking our own temp GameObject DontDestroyOnLoad lets it survive that
        /// unload and finish playing normally, then clean itself up afterward via the scheduled Destroy
        /// below - same lifetime PlayClipAtPoint itself uses, just on a GameObject we control.
        /// </summary>
        public static void PlayOneShot(AudioClip clip, AudioCategory category, Vector3 position)
        {
            if (clip == null) return;

            var temp = new GameObject($"OneShotAudio_{clip.name}");
            temp.transform.position = position;
            UnityEngine.Object.DontDestroyOnLoad(temp);

            var source = temp.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = GetMultiplier(category);
            source.spatialBlend = 0f; // UI feedback sound, not a positional world-space effect
            source.Play();

            UnityEngine.Object.Destroy(temp, clip.length);
        }
    }
}
