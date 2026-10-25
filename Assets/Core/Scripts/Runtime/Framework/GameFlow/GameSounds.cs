using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One-off game sound effects that live in Assets/Core/Audio/Clips, so code can play them without each
    /// needing its own Resources copy. Built automatically in the editor by GameSoundsBuilder and stored at
    /// Resources/GameSounds - don't edit it by hand; to change a sound, replace the file (same name).
    ///   - <see cref="Death"/>: death.mp3 - any player dying (VisualsAddon), heard around where they died.
    ///   - <see cref="ChallengeSuccess"/>: challenge_success.mp3 - this player completing a challenge, with
    ///     its notification (CoreHUD).
    ///   - <see cref="Points"/>: points.mp3 - this player collecting a pickup (PickupManager).
    /// All play at the Sound Effects volume.
    /// </summary>
    public class GameSounds : ScriptableObject
    {
        public const string ResourcePath = "GameSounds";

        [SerializeField] private AudioClip death;
        [SerializeField] private AudioClip challengeSuccess;
        [SerializeField] private AudioClip points;

        private static GameSounds s_Loaded;
        private static bool s_Tried;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Loaded = null;
            s_Tried = false;
        }

        /// <summary>The sounds, or null if they haven't been built yet (warns once).</summary>
        public static GameSounds Load()
        {
            if (s_Loaded != null || s_Tried) return s_Loaded;
            s_Tried = true;
            s_Loaded = Resources.Load<GameSounds>(ResourcePath);
            if (s_Loaded == null) Debug.LogWarning($"[GameSounds] No Resources/{ResourcePath} - it's built automatically from Assets/Core/Audio/Clips when the editor loads.");
            return s_Loaded;
        }

        /// <summary>death.mp3, or null.</summary>
        public static AudioClip Death => Load() != null ? s_Loaded.death : null;

        /// <summary>challenge_success.mp3, or null.</summary>
        public static AudioClip ChallengeSuccess => Load() != null ? s_Loaded.challengeSuccess : null;

        /// <summary>points.mp3, or null.</summary>
        public static AudioClip Points => Load() != null ? s_Loaded.points : null;

#if UNITY_EDITOR
        /// <summary>Editor only - used by GameSoundsBuilder.</summary>
        public void Set(AudioClip deathClip, AudioClip challengeSuccessClip, AudioClip pointsClip)
        {
            death = deathClip;
            challengeSuccess = challengeSuccessClip;
            points = pointsClip;
        }

        public AudioClip DeathClip => death;
        public AudioClip ChallengeSuccessClip => challengeSuccess;
        public AudioClip PointsClip => points;
#endif
    }
}
