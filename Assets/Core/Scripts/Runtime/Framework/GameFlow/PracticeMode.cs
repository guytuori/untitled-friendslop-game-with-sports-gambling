using System;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The two single-player practice modes, both started by <see cref="FusionSessionService.StartPracticeAsync"/>
    /// (a private one-player session), so <see cref="IsActive"/> is simply "this session is a practice session".
    /// Both have no time limit, unlimited lives, no wagers, no death penalty, and a score that starts at 0 and
    /// only counts pickups. Everything that behaves differently checks this: MatchLayout, RoundTimer,
    /// PlayerScore, ChallengeManager, WagerManager, CoreHUD and InGameMenu.
    ///
    /// PRACTICE MAP (<see cref="IsChallenge"/> false): the player alone on a chosen map, to learn it. The
    /// challenge holes get the plain Challenge16 / Challenge32 pieces, the end point does nothing, and the way
    /// out is Esc > Quit to Main Menu (without the usual 2-second safety delay).
    ///
    /// PRACTICE CHALLENGES (<see cref="IsChallenge"/> true): one chosen challenge (<see cref="ChallengeId"/>),
    /// in the Practice16 or Practice32 map (<see cref="MapForSize"/>). Reaching the end point stops the clock
    /// and asks "Retry Challenge?" (Retry / Return to Menu / Change Challenge - see InGameMenu), and the Esc
    /// menu becomes Retry / Change Challenge / Settings / Return to Menu. Both Retry and Change Challenge
    /// happen in place, without reloading (<see cref="PracticeRetry"/>).
    /// </summary>
    public static class PracticeMode
    {
        /// <summary>True while in a practice session (Practice Map or Practice Challenges).</summary>
        public static bool IsActive => FusionSessionService.HasInstance && FusionSessionService.Instance.IsPracticeSession;

        /// <summary>The ChallengeCatalog id being practised, or null in Practice Map.</summary>
        public static string ChallengeId => IsActive ? FusionSessionService.Instance.PracticeChallengeId : null;

        /// <summary>True in Practice Challenges.</summary>
        public static bool IsChallenge => !string.IsNullOrEmpty(ChallengeId);

        /// <summary>The map a challenge of this size is practised in (Assets/Core/Prefabs/Maps).</summary>
        public static string MapForSize(int size) => size == 32 ? "Practice32" : "Practice16";

        // ---- The run's stopwatch (the HUD's PRACTICE box) ----------------------------------------------

        private static float s_RunStart;
        private static float s_RunEnd = -1f;

        /// <summary>Raised once when the player reaches the end point in Practice Challenges.</summary>
        public static event Action ChallengeFinished;

        /// <summary>Starts the stopwatch (the HUD calls this when the player arrives).</summary>
        public static void StartRun()
        {
            s_RunStart = Time.time;
            s_RunEnd = -1f;
        }

        /// <summary>True once the end point has been reached this run.</summary>
        public static bool RunFinished => s_RunEnd >= 0f;

        /// <summary>Seconds since the run started - frozen once it's finished.</summary>
        public static float RunSeconds => Mathf.Max(0f, (RunFinished ? s_RunEnd : Time.time) - s_RunStart);

        /// <summary>The end point was reached (RoundTimer, on this machine): stop the clock and ask what's next.</summary>
        public static void ReportChallengeFinished()
        {
            if (!IsChallenge || RunFinished) return;
            s_RunEnd = Time.time;
            ChallengeFinished?.Invoke();
        }

        /// <summary>"1:05.3" - minutes, seconds and tenths.</summary>
        public static string FormatRunTime(float seconds)
        {
            int tenths = Mathf.FloorToInt(Mathf.Max(0f, seconds) * 10f);
            int whole = tenths / 10;
            return $"{whole / 60}:{whole % 60:00}.{tenths % 10}";
        }
    }
}
