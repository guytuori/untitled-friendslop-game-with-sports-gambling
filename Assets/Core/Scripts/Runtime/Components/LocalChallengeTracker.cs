using System;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Runs the challenges for this machine's own player (see <see cref="ChallengeCourse"/>): every frame it
    /// checks the path the player moved along since the last frame against the flags.
    ///   - Not in a challenge: passing through a start flag (from outside, of a challenge they haven't
    ///     completed this round) starts that challenge.
    ///   - In a challenge: passing through its end flag completes it; leaving its square any other way (the
    ///     side, back out of the start) or dying abandons it, and its start flag comes back.
    /// The master client is told each start, finish and abandon (<see cref="ChallengeManager"/>), and awards
    /// the points. Nothing starts while the round isn't open for this player (see ChallengeManager.IsRoundOpenFor).
    ///
    /// Added at runtime to the local player's avatar by NetworkPlayers.Register (no prefab setup).
    /// </summary>
    public class LocalChallengeTracker : MonoBehaviour
    {
        /// <summary>Why a challenge run ended.</summary>
        public enum EndReason
        {
            /// <summary>Passed through the end flag.</summary>
            Completed,
            /// <summary>Left the challenge's square without finishing (or the round closed).</summary>
            Left,
            /// <summary>Died during it.</summary>
            Died,
            /// <summary>Practice Retry / Change Challenge put everything back.</summary>
            Reset,
        }

        /// <summary>A move longer than this in one frame is a teleport (a respawn, a retry) - no flag crossings.</summary>
        private const float MaxStep = 6f;

        /// <summary>
        /// How far outside a challenge's square the player's middle can be before they've left it. Small, so
        /// nobody can walk round the outside edge to the end flag.
        /// </summary>
        private const float ExitMargin = 0.2f;

        /// <summary>The local player's tracker, if spawned.</summary>
        public static LocalChallengeTracker Local { get; private set; }

        /// <summary>Raised on this machine when its player starts a challenge (passes through its start flag).</summary>
        public static event Action<ChallengeCourse> RunStarted;

        /// <summary>Raised on this machine when its player's challenge run ends.</summary>
        public static event Action<ChallengeCourse, EndReason> RunEnded;

        private CorePlayerManager m_Player;
        private ChallengeCourse m_Current;
        private bool m_HasCurrent;
        private Vector3 m_Last;
        private bool m_HasLast;

        /// <summary>The challenge this player is running now, or null.</summary>
        public ChallengeCourse Current => m_HasCurrent ? m_Current : null;

        public static void Attach(CorePlayerManager player)
        {
            if (player == null || !player.IsOwner) return;
            if (player.GetComponent<LocalChallengeTracker>() != null) return;
            var tracker = player.gameObject.AddComponent<LocalChallengeTracker>();
            tracker.m_Player = player;
        }

        private void Start()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();
            Local = this;
        }

        private void OnDestroy()
        {
            if (Local == this) Local = null;
        }

        private void LateUpdate()
        {
            if (m_Player == null || !m_Player.IsSpawned)
            {
                m_HasLast = false;
                return;
            }

            // Its challenge was removed (Practice Change Challenge rebuilds them).
            if (m_HasCurrent && m_Current == null)
            {
                m_HasCurrent = false;
                m_Current = null;
            }

            Vector3 position = transform.position;
            bool moved = m_HasLast && (position - m_Last).sqrMagnitude <= MaxStep * MaxStep;
            bool alive = m_Player.PlayerState == null || m_Player.PlayerState.LifeState != PlayerLifeState.Eliminated;
            bool open = ChallengeManager.IsRoundOpenFor(m_Player.OwnerClientId);

            if (m_HasCurrent)
            {
                if (!alive) EndRun(EndReason.Died);
                else if (!open) EndRun(EndReason.Left);
                else if (moved && m_Current.CrossesEnd(m_Last, position)) EndRun(EndReason.Completed);
                else if (!m_Current.Contains(position, ExitMargin)) EndRun(EndReason.Left);
            }
            else if (alive && open && moved)
            {
                foreach (ChallengeCourse course in ChallengeCourse.All)
                {
                    if (course == null || course.State != ChallengeCourse.LocalState.Open) continue;
                    if (!course.CrossesStart(m_Last, position)) continue;
                    StartRun(course);
                    break;
                }
            }

            m_Last = position;
            m_HasLast = true;
        }

        private void StartRun(ChallengeCourse course)
        {
            m_Current = course;
            m_HasCurrent = true;
            course.SetLocalState(ChallengeCourse.LocalState.Running);
            RunStarted?.Invoke(course);
            if (ChallengeManager.Instance != null) ChallengeManager.Instance.ReportRunStarted(course);
        }

        private void EndRun(EndReason reason)
        {
            ChallengeCourse course = m_Current;
            m_Current = null;
            m_HasCurrent = false;
            if (course == null) return;

            course.SetLocalState(reason == EndReason.Completed ? ChallengeCourse.LocalState.Done : ChallengeCourse.LocalState.Open);
            RunEnded?.Invoke(course, reason);

            if (ChallengeManager.Instance == null || reason == EndReason.Reset) return;
            if (reason == EndReason.Completed) ChallengeManager.Instance.ReportRunCompleted(course);
            else ChallengeManager.Instance.ReportRunAbandoned(course);
        }

        /// <summary>
        /// Practice Retry: every challenge is open again (start flags back), and any run in progress is dropped.
        /// </summary>
        public void ResetAll()
        {
            if (m_HasCurrent) EndRun(EndReason.Reset);
            foreach (ChallengeCourse course in ChallengeCourse.All)
            {
                if (course != null) course.SetLocalState(ChallengeCourse.LocalState.Open);
            }
            m_HasLast = false; // the player is about to be moved
        }
    }
}
