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
    /// in the Practice16 or Practice32 map (<see cref="MapForSize"/>). Reaching the end point asks "Retry
    /// Challenge?" (Retry / Return to Menu / Change Challenge - see InGameMenu), and the Esc menu becomes
    /// Retry / Change Challenge / Settings / Return to Menu. Both Retry and Change Challenge happen in place,
    /// without reloading (<see cref="PracticeRetry"/>).
    ///
    /// THE STOPWATCH (changed 2026-10-28): in Practice Challenges it shows 0:00.00 until the player passes
    /// through the challenge's start flag (LocalChallengeTracker), runs to the hundredth, and stops at Challenge
    /// Complete (the end flag). Leaving the challenge without completing it (dying, out the side or back out
    /// the start) puts it back to 0:00.00 until they go through the start again. A completed run is checked
    /// against the player's best time for that challenge (<see cref="BestTime"/>, saved on this computer): a
    /// new record plays the "new_record" voice clip, otherwise "complete". Practice Map's stopwatch still just
    /// counts whole seconds from arrival.
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

        private static float s_RunStart = -1f;   // -1 = not running (waiting for the start flag)
        private static float s_RunEnd = -1f;     // -1 = not stopped
        private static bool s_ReachedEnd;

        /// <summary>Raised once when the player reaches the end point in Practice Challenges.</summary>
        public static event Action ChallengeFinished;

        /// <summary>Raised when a Practice Challenges run is completed: the time, and whether it's a new record.</summary>
        public static event Action<float, bool> RunTimed;

        /// <summary>
        /// The player has arrived (the HUD) or a Retry put them back: Practice Challenges waits at 0:00.00 for the
        /// start flag; Practice Map starts counting now.
        /// </summary>
        public static void StartRun()
        {
            s_RunStart = IsChallenge ? -1f : Time.time;
            s_RunEnd = -1f;
            s_ReachedEnd = false;
            LastRunWasRecord = false;
        }

        /// <summary>True once the end point has been reached (the "Retry Challenge?" question is up).</summary>
        public static bool RunFinished => s_ReachedEnd;

        /// <summary>True while the stopwatch is counting.</summary>
        public static bool RunRunning => s_RunStart >= 0f && s_RunEnd < 0f;

        /// <summary>The stopwatch: 0 before the start flag, frozen once Challenge Complete stops it.</summary>
        public static float RunSeconds => s_RunStart < 0f ? 0f : Mathf.Max(0f, (s_RunEnd >= 0f ? s_RunEnd : Time.time) - s_RunStart);

        /// <summary>The last completed run set a new record.</summary>
        public static bool LastRunWasRecord { get; private set; }

        /// <summary>True once a run has been completed (the stopwatch stopped at Challenge Complete).</summary>
        public static bool RunCompleted => s_RunEnd >= 0f;

        /// <summary>The end point was reached (RoundTimer, on this machine): ask what's next.</summary>
        public static void ReportChallengeFinished()
        {
            if (!IsChallenge || s_ReachedEnd) return;
            s_ReachedEnd = true;
            if (RunRunning) s_RunEnd = Time.time; // reached the end point without a Challenge Complete: stop anyway
            ChallengeFinished?.Invoke();
        }

        /// <summary>LocalChallengeTracker: the player passed through the challenge's start flag.</summary>
        public static void OnRunStarted()
        {
            if (!IsChallenge || s_ReachedEnd) return;
            s_RunStart = Time.time;
            s_RunEnd = -1f;
            LastRunWasRecord = false;
        }

        /// <summary>LocalChallengeTracker: the run ended - completed (stop the clock, check the record) or not (back to 0).</summary>
        public static void OnRunEnded(bool completed)
        {
            if (!IsChallenge || !RunRunning) return;
            if (!completed)
            {
                s_RunStart = -1f; // left, died: wait for the start flag again
                return;
            }

            s_RunEnd = Time.time;
            float time = RunSeconds;
            LastRunWasRecord = TryRecord(ChallengeId, time);

            AudioClip clip = VoiceCatalog.Get(LastRunWasRecord ? "new_record" : "complete");
            if (clip != null) AudioVolumeService.PlayOneShot(clip, AudioCategory.Voice, Vector3.zero);
            Debug.Log($"[Practice] {ChallengeId} completed in {FormatRunTime(time)}" +
                      (LastRunWasRecord ? " - new record!" : $" (best {FormatRunTime(BestTime(ChallengeId))})."));
            RunTimed?.Invoke(time, LastRunWasRecord);
        }

        /// <summary>"1:05.37" - minutes, seconds and hundredths.</summary>
        public static string FormatRunTime(float seconds) => FormatHundredths(ToHundredths(seconds));

        private static int ToHundredths(float seconds) => Mathf.FloorToInt(Mathf.Max(0f, seconds) * 100f + 0.0001f);

        private static string FormatHundredths(int hundredths)
        {
            int whole = hundredths / 100;
            return $"{whole / 60}:{whole % 60:00}.{hundredths % 100:00}";
        }

        // ---- Best times (saved on this computer, per challenge) ----------------------------------------

        /// <summary>The best time shown before a challenge has a record: 9:59.99.</summary>
        public const int NoRecordHundredths = 59999;

        private static string RecordKey(string challengeId) => "PracticeBest." + challengeId;

        /// <summary>The player's best time for a challenge, in seconds (9:59.99 if they haven't set one).</summary>
        public static float BestTime(string challengeId) => BestHundredths(challengeId) / 100f;

        /// <summary>The best time as text, e.g. "0:42.37" (or "9:59.99").</summary>
        public static string BestTimeText(string challengeId) => FormatHundredths(BestHundredths(challengeId));

        private static int BestHundredths(string challengeId) =>
            string.IsNullOrEmpty(challengeId) ? NoRecordHundredths : PlayerPrefs.GetInt(RecordKey(challengeId), NoRecordHundredths);

        /// <summary>Saves <paramref name="seconds"/> if it beats the challenge's best time; true if it did.</summary>
        public static bool TryRecord(string challengeId, float seconds)
        {
            if (string.IsNullOrEmpty(challengeId)) return false;
            int time = ToHundredths(seconds);
            if (time >= BestHundredths(challengeId)) return false;
            PlayerPrefs.SetInt(RecordKey(challengeId), time);
            PlayerPrefs.Save();
            return true;
        }
    }
}
