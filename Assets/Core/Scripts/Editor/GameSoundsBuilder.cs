using System;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps <see cref="GameSounds"/> (Resources/GameSounds) pointing at its clips in Assets/Core/Audio/Clips.
    /// Runs when the editor loads (if the asset is missing or a clip isn't set) and whenever one of the clips
    /// is imported, moved or deleted.
    /// </summary>
    [InitializeOnLoad]
    public static class GameSoundsBuilder
    {
        public const string DeathPath = "Assets/Core/Audio/Clips/death.mp3";
        public const string ChallengeSuccessPath = "Assets/Core/Audio/Clips/challenge_success.mp3";
        public const string PointsPath = "Assets/Core/Audio/Clips/points.mp3";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string AssetPath = ResourcesFolder + "/" + GameSounds.ResourcePath + ".asset";

        private static bool s_Scheduled;

        static GameSoundsBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                var sounds = AssetDatabase.LoadAssetAtPath<GameSounds>(AssetPath);
                if (sounds == null || sounds.DeathClip == null || sounds.ChallengeSuccessClip == null || sounds.PointsClip == null) Rebuild();
            };
        }

        internal static bool IsWatchedPath(string path) =>
            string.Equals(path, DeathPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, ChallengeSuccessPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, PointsPath, StringComparison.OrdinalIgnoreCase);

        internal static void Schedule()
        {
            if (s_Scheduled) return;
            s_Scheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_Scheduled = false;
                Rebuild();
            };
        }

        public static void Rebuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            if (!AssetDatabase.IsValidFolder(ResourcesFolder)) AssetDatabase.CreateFolder("Assets/Core", "Resources");

            var sounds = AssetDatabase.LoadAssetAtPath<GameSounds>(AssetPath);
            if (sounds == null)
            {
                sounds = ScriptableObject.CreateInstance<GameSounds>();
                AssetDatabase.CreateAsset(sounds, AssetPath);
            }

            var death = AssetDatabase.LoadAssetAtPath<AudioClip>(DeathPath);
            var success = AssetDatabase.LoadAssetAtPath<AudioClip>(ChallengeSuccessPath);
            var points = AssetDatabase.LoadAssetAtPath<AudioClip>(PointsPath);
            if (sounds.DeathClip == death && sounds.ChallengeSuccessClip == success && sounds.PointsClip == points) return;

            sounds.Set(death, success, points);
            EditorUtility.SetDirty(sounds);
            AssetDatabase.SaveAssets();
            Debug.Log($"[GameSounds] Death = {(death != null ? death.name : "missing (" + DeathPath + ")")}, challenge success = {(success != null ? success.name : "missing (" + ChallengeSuccessPath + ")")}, points = {(points != null ? points.name : "missing (" + PointsPath + ")")}.", sounds);
        }
    }

    internal class GameSoundsWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string[] list in new[] { imported, deleted, moved, movedFrom })
            {
                foreach (string path in list)
                {
                    if (!GameSoundsBuilder.IsWatchedPath(path)) continue;
                    GameSoundsBuilder.Schedule();
                    return;
                }
            }
        }
    }
}
