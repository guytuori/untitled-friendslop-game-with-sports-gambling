using System;
using System.Collections.Generic;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The master client's side of the challenges, plus the flat death penalty that applies anywhere.
    ///
    /// CHALLENGES (reworked 2026-10-17 - "more points coming into the economy"):
    ///   - Every player can complete every challenge once per round, and any number of players can be in the
    ///     same challenge at the same time. Nobody "owns" a challenge any more.
    ///   - Completing one is worth a flat <see cref="ChallengeDefinition.basePoints"/> - no time or death pars.
    ///     Going fast just leaves more time for other challenges. Dying still costs the death penalty, as it
    ///     does anywhere.
    ///   - A run starts by passing through the challenge's start flag and ends at its end flag - see
    ///     <see cref="ChallengeCourse"/> (the placed challenges and their flags) and
    ///     <see cref="LocalChallengeTracker"/> (which watches each player on their own machine and reports here).
    ///   - The master keeps who is in which challenge (<see cref="IsPlayerInChallenge"/>, for challenge-only
    ///     wagers) and who has completed what, awards the points, and tells everyone
    ///     (<see cref="PlayerChallengeStateChanged"/>, <see cref="ChallengeCompleted"/>).
    ///   - Practice sessions give no points for challenges (the practice score only counts pickups).
    ///
    /// Courses are identified over the network by their index in a list sorted by hierarchy path, the same
    /// on every client (MatchLayout builds every challenge with the same names in the same places). If the
    /// master client leaves, the next one starts with an empty record of who has completed what.
    ///
    /// The old betting window (claiming a challenge paused everyone for bets) was replaced by the wager
    /// ticker (WagerManager) on 2026-10-06. Its read-only properties and events stay for CoreHUD's (now
    /// never shown) overlay, but nothing opens it any more.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class ChallengeManager : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        [Header("Death Penalty")]
        [Tooltip("Points lost any time any player's Health stat depletes to zero, whether or not they're mid-challenge.")]
        [SerializeField] private int deathPenalty = 50;
        [Tooltip("The shared GameEvent CoreStatsHandler raises when a stat depletes - assign the project's OnStatDepleted asset.")]
        [SerializeField] private StatDepletedEvent onStatDepletedEvent;

        /// <summary>The single ChallengeManager for the current scene. Set in Awake, like RoundTimer/CoreDirector.</summary>
        public static ChallengeManager Instance { get; private set; }

        /// <summary>Raised on every client when a player starts (true) or stops (false) a challenge run.</summary>
        public static event Action<ulong, bool> PlayerChallengeStateChanged;

        /// <summary>Raised on every client when a player completes a challenge: (player, course index, points awarded).</summary>
        public static event Action<ulong, int, int> ChallengeCompleted;

        // Master-client-only bookkeeping.
        private readonly Dictionary<ulong, int> m_RunningCourseByPlayer = new Dictionary<ulong, int>();
        private readonly HashSet<long> m_Completed = new HashSet<long>(); // (player << 16) | course index

        // Every ChallengeCourse in this scene, in a stable order all clients agree on (see GetCourseIndex).
        private List<ChallengeCourse> m_Courses;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        public override void OnNetworkSpawn()
        {
            // Every client listens; only the current master client acts on it (see HandleAnyStatDepleted),
            // so a client that becomes master later is already listening.
            if (onStatDepletedEvent != null)
            {
                onStatDepletedEvent.RegisterListener(HandleAnyStatDepleted);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (onStatDepletedEvent != null)
            {
                onStatDepletedEvent.UnregisterListener(HandleAnyStatDepleted);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        #endregion

        #region Death Penalty

        /// <summary>
        /// Fires for every player's every stat depletion, on every client (see CoreStatsHandler - the stat
        /// events are raised wherever the change is seen). Only the master client applies the flat penalty.
        /// (Dying in a challenge also abandons it - the dying player's own LocalChallengeTracker reports that.)
        /// </summary>
        private void HandleAnyStatDepleted(StatDepletedPayload payload)
        {
            // Since 2026-10-25 the dying player's own machine applies the penalty (PlayerScore.HandleLocalDeath,
            // from GameManager), so the 1-Up's shield can waive it and the Death Penalty rule is used. Nothing to
            // do here any more; the serialized deathPenalty value is no longer read.
        }

        #endregion

        #region Challenge runs

        /// <summary>This machine's player passed through <paramref name="course"/>'s start flag.</summary>
        public void ReportRunStarted(ChallengeCourse course)
        {
            int index = ResolveIndex(course);
            if (IsSpawned && index >= 0) RunStartedRpc(index);
        }

        /// <summary>This machine's player passed through <paramref name="course"/>'s end flag.</summary>
        public void ReportRunCompleted(ChallengeCourse course)
        {
            int index = ResolveIndex(course);
            if (IsSpawned && index >= 0) RunCompletedRpc(index);
        }

        /// <summary>This machine's player left <paramref name="course"/> (or died in it) without finishing.</summary>
        public void ReportRunAbandoned(ChallengeCourse course)
        {
            int index = ResolveIndex(course);
            if (IsSpawned && index >= 0) RunAbandonedRpc(index);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RunStartedRpc(int courseIndex, RpcInfo info = default)
        {
            ulong player = SenderId(info);
            if (GetCourse(courseIndex) == null) return;
            if (!IsRoundOpenFor(player)) return;
            if (m_Completed.Contains(Key(player, courseIndex))) return; // once per player per round

            if (m_RunningCourseByPlayer.TryGetValue(player, out int previous))
            {
                if (previous == courseIndex) return;
                EndRun(player); // a missed "left" (shouldn't happen - squares don't overlap)
            }

            m_RunningCourseByPlayer[player] = courseIndex;
            if (WagerManager.Instance != null) WagerManager.Instance.OnChallengeStarted(player);
            PlayerChallengeStateRpc((int)player, true);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RunCompletedRpc(int courseIndex, RpcInfo info = default)
        {
            ulong player = SenderId(info);
            ChallengeCourse course = GetCourse(courseIndex);
            bool wasRunning = EndRun(player);
            if (course == null || !IsRoundOpenFor(player)) return;

            // Forgiving about a start this master never heard of (it may have only just taken over), but never
            // twice for the same player and challenge.
            if (!m_Completed.Add(Key(player, courseIndex))) return;
            if (!wasRunning) Debug.Log($"[ChallengeManager] Player {player} completed challenge {courseIndex} without a recorded start (master change?) - awarding it anyway.", this);

            int points = PracticeMode.IsActive ? 0 : course.Points; // the practice score only counts pickups
            if (points != 0) GetPlayerScore(player)?.AddScore(points);
            ChallengeCompletedRpc((int)player, courseIndex, points);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void RunAbandonedRpc(int courseIndex, RpcInfo info = default)
        {
            ulong player = SenderId(info);
            if (m_RunningCourseByPlayer.TryGetValue(player, out int running) && running == courseIndex) EndRun(player);
        }

        /// <summary>Master: forgets this player's current run, settles their challenge wagers and tells everyone. False if they weren't in one.</summary>
        private bool EndRun(ulong player)
        {
            if (!m_RunningCourseByPlayer.Remove(player)) return false;
            if (WagerManager.Instance != null) WagerManager.Instance.OnChallengeEnded(player);
            PlayerChallengeStateRpc((int)player, false);
            return true;
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void PlayerChallengeStateRpc(int playerId, NetworkBool inChallenge)
        {
            PlayerChallengeStateChanged?.Invoke((ulong)playerId, inChallenge);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void ChallengeCompletedRpc(int playerId, int courseIndex, int points)
        {
            ChallengeCompleted?.Invoke((ulong)playerId, courseIndex, points);
        }

        /// <summary>Master client only: whether this player is in the middle of a challenge run.</summary>
        public bool IsPlayerInChallenge(ulong playerId) => m_RunningCourseByPlayer.ContainsKey(playerId);

        private static long Key(ulong player, int courseIndex) => ((long)player << 16) | (uint)(courseIndex & 0xFFFF);

        #endregion

        #region Practice

        /// <summary>
        /// Practice Challenges' quick Retry (master client - the only player in a practice session): every
        /// challenge can be completed again and any run in progress is dropped, as if it had never started.
        /// </summary>
        public void ResetForPracticeRetry()
        {
            if (!IsOwner) return;

            foreach (ulong player in new List<ulong>(m_RunningCourseByPlayer.Keys))
            {
                EndRun(player);
            }
            m_Completed.Clear();

            if (LocalChallengeTracker.Local != null) LocalChallengeTracker.Local.ResetAll();
            ResetCoursesRpc();
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void ResetCoursesRpc()
        {
            if (LocalChallengeTracker.Local != null) LocalChallengeTracker.Local.ResetAll();
        }

        #endregion

        #region Courses

        /// <summary>
        /// Forgets the cached course list so it's rebuilt on next use. MatchLayout calls this on every client
        /// once it has placed the match's challenges (they don't exist when the scene first loads). On the
        /// master it also clears the run records (only Practice's Change Challenge rebuilds mid-session).
        /// </summary>
        public void RefreshZones()
        {
            m_Courses = null;
            m_RunningCourseByPlayer.Clear();
            m_Completed.Clear();
            EnsureCourses();
        }

        /// <summary>This course's index (the same on every client), or -1.</summary>
        public int GetCourseIndex(ChallengeCourse course)
        {
            EnsureCourses();
            return course != null ? m_Courses.IndexOf(course) : -1;
        }

        /// <summary>The course at <paramref name="index"/>, or null.</summary>
        public ChallengeCourse GetCourse(int index)
        {
            EnsureCourses();
            return index >= 0 && index < m_Courses.Count ? m_Courses[index] : null;
        }

        private int ResolveIndex(ChallengeCourse course)
        {
            int index = GetCourseIndex(course);
            if (index < 0 && course != null)
            {
                m_Courses = null; // made after the last refresh - look again
                index = GetCourseIndex(course);
            }
            if (index < 0 && course != null) Debug.LogWarning($"[ChallengeManager] '{course.name}' isn't one of this scene's challenges.", course);
            return index;
        }

        private void EnsureCourses()
        {
            if (m_Courses != null && !m_Courses.Exists(c => c == null)) return;

            m_Courses = new List<ChallengeCourse>();
            foreach (ChallengeCourse course in ChallengeCourse.All)
            {
                if (course != null && course.gameObject.scene == gameObject.scene) m_Courses.Add(course);
            }

            m_Courses.Sort((a, b) => string.CompareOrdinal(GetHierarchyKey(a.transform), GetHierarchyKey(b.transform)));
            for (int i = 0; i < m_Courses.Count; i++) m_Courses[i].Index = i;
        }

        /// <summary>Root-to-leaf sibling indices plus names, e.g. "0003:MatchLayout/0000:Challenges/0002:002_Challenge_16_05" - unique and identical on every client.</summary>
        private static string GetHierarchyKey(Transform transform)
        {
            var parts = new List<string>();
            for (Transform t = transform; t != null; t = t.parent)
            {
                parts.Add($"{t.GetSiblingIndex():D4}:{t.name}");
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        #endregion

        #region Old betting window (never opened since the wager ticker - kept for CoreHUD)

        /// <summary>Always false: nothing pauses the game for bets any more (ChallengePauseGate reads this).</summary>
        public bool IsPaused => false;

        /// <summary>Always false (see the region name).</summary>
        public bool IsBettingWindowActive => false;

        public float BettingTimeRemaining => 0f;
        public float BettingWindowSeconds => 5f;
        public string ActiveChallengeName => string.Empty;
        public ulong ActiveChallengerClientId => 0;
        public IEnumerable<ulong> ActiveBettorClientIds { get { yield break; } }

#pragma warning disable 0067 // never raised - the overlay they drive is never opened
        public event Action<bool, bool> PausedChanged;
        public event Action<bool, bool> BettingWindowActiveChanged;
        public event Action<float, float> BettingTimeRemainingChanged;
        public event Action ActiveBettorsChanged;
#pragma warning restore 0067

        /// <summary>Does nothing (the old betting overlay's buttons).</summary>
        public void PlaceBet(ChallengeBetChoice choice) { }

        #endregion

        #region Helpers

        /// <summary>False once the round is over, or for a player who has reached the end point (see RoundTimer).</summary>
        public static bool IsRoundOpenFor(ulong playerId)
        {
            RoundTimer round = RoundTimer.Instance;
            if (round == null || !round.IsSpawned) return true; // no round rules in this scene
            return round.IsPlaying && !round.IsPlayerFinished(playerId);
        }

        private static PlayerScore GetPlayerScore(ulong clientId)
        {
            return NetworkPlayers.TryGetComponent(clientId, out PlayerScore score) ? score : null;
        }

        private ulong SenderId(RpcInfo info)
        {
            // An RPC the master client sends to itself runs locally - the sender is then this client.
            ulong id = NetworkPlayers.ToClientId(info.Source);
            return id != 0 ? id : NetworkPlayers.ToClientId(Runner.LocalPlayer);
        }

        #endregion
    }
}
