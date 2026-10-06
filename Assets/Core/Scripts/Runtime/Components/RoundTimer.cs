using System;
using System.Collections.Generic;
using UnityEngine;
using Fusion;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The round: its two countdowns, who has reached the end point, the team's target score, and what
    /// happens when the round ends. One per gameplay scene (placed in [BB] Core, see ISceneSingleton).
    ///
    /// Networking (Photon Fusion, Shared mode): a scene object owned by the session's master client ("Is
    /// Master Client Object" on its NetworkObject), which runs everything below; everyone else reads the
    /// networked state. If the master client leaves, the next one carries on from the same state.
    ///
    /// TIMERS: the ROUND timer (Round Time rule) and the BONUS timer (Bonus Time rule) start together and
    /// count down side by side. The round ends when the round timer hits 0, or as soon as every player has
    /// reached the end point.
    ///
    /// FINISHING: a player touching a <see cref="PlayerEndPoint"/> is finished for the round: they earn
    /// Bonus Points Per Second (rule, 10 by default) for every whole second left on the bonus timer (what
    /// the HUD shows), fade out (<see cref="PlayerFinishFade"/>), can't move, and can't wager any more.
    ///
    /// TARGET: the team target is the round's per-player target times the number of players. The per-player
    /// target is Average Target Score x Target Score Growth^(round - 1) (see MatchProgress.PerPlayerTarget).
    ///
    /// ROUND END: Playing -> Tallying (a short pause so the last bonus points reach everyone's score) ->
    /// Ended (the result: everyone's scores added up against the target, shown for a few seconds; every
    /// client records the results in MatchProgress at this point) -> the master client loads the Round
    /// Results scene if the team hit the target, or the Final Score scene if not.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class RoundTimer : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        public enum RoundPhase
        {
            Playing = 0,
            Tallying = 1,
            Ended = 2
        }

        private const int MaxPlayers = 32;
        private const string RoundOverInputLock = "RoundOver";

        [Header("Configuration")]
        [Tooltip("Rules used when the scene is played directly (no Host Game rules). Normal defaults if left unassigned.")]
        [SerializeField] private GameRulesConfig gameRulesConfig;

        [Tooltip("Seconds between the round ending and its result being worked out (lets the last bonus points arrive).")]
        [SerializeField] private float tallySeconds = 1.5f;

        [Tooltip("Seconds the result is shown before everyone moves on to Round Results / Final Score.")]
        [SerializeField] private float resultSeconds = 4f;

        /// <summary>The single RoundTimer for the current scene, once it has awoken.</summary>
        public static RoundTimer Instance { get; private set; }

        [Networked] private float NetTimeRemaining { get; set; }
        [Networked] private float NetBonusRemaining { get; set; }
        [Networked] private int NetPhase { get; set; }
        [Networked] private NetworkBool NetInitialized { get; set; }
        [Networked] private NetworkBool NetPassed { get; set; }
        [Networked] private int NetRoundNumber { get; set; }
        [Networked] private int NetPerPlayerTarget { get; set; }
        [Networked] private int NetFinalTeamTotal { get; set; }
        [Networked] private int NetFinalTeamTarget { get; set; }
        [Networked] private TickTimer NetPhaseTimer { get; set; }
        [Networked, Capacity(MaxPlayers)] private NetworkArray<int> NetFinishedIds => default;
        [Networked] private int NetFinishedCount { get; set; }

        private HostGameRulesData m_Rules;
        private float m_LastTimeRemaining = -1f;
        private float m_LastBonusRemaining = -1f;
        private int m_LastPhase = -1;
        private int m_LastFinishedKey;
        private int m_LastRoundKey;
        private bool m_LoadRequested;
        private bool m_ResultsRecorded;
        private readonly HashSet<ulong> m_FadedPlayers = new HashSet<ulong>();

        /// <summary>Seconds left in the round (0 until spawned).</summary>
        public float TimeRemaining => IsSpawned ? NetTimeRemaining : 0f;

        /// <summary>Seconds left on the bonus timer (0 until spawned).</summary>
        public float BonusTimeRemaining => IsSpawned ? NetBonusRemaining : 0f;

        public RoundPhase Phase => IsSpawned ? (RoundPhase)NetPhase : RoundPhase.Playing;

        /// <summary>True while the round is being played (not over).</summary>
        public bool IsPlaying => IsSpawned && NetInitialized && NetPhase == (int)RoundPhase.Playing;

        /// <summary>The round being played, starting at 1.</summary>
        public int RoundNumber => IsSpawned ? Mathf.Max(1, NetRoundNumber) : MatchProgress.RoundNumber;

        /// <summary>This round's target per player.</summary>
        public int PerPlayerTarget => IsSpawned ? NetPerPlayerTarget : 0;

        /// <summary>The team's target: per-player target x players in the game (fixed once the round has ended).</summary>
        public int TeamTarget => IsSpawned && NetPhase == (int)RoundPhase.Ended ? NetFinalTeamTarget : PerPlayerTarget * Mathf.Max(1, CountPlayers());

        /// <summary>Only meaningful once <see cref="Phase"/> is Ended: whether the team hit the target.</summary>
        public bool TargetReached => IsSpawned && NetPassed;

        /// <summary>Only meaningful once <see cref="Phase"/> is Ended: the team's final total.</summary>
        public int FinalTeamTotal => IsSpawned ? NetFinalTeamTotal : 0;

        /// <summary>Points per whole second left on the bonus timer when reaching the end point.</summary>
        public int BonusPointsPerSecond => Rules.BonusPointsPerSecond;

        private HostGameRulesData Rules => m_Rules ??= MatchRules.Get(gameRulesConfig);

        /// <summary>Raised on every client whenever the round time changes (previous, new).</summary>
        public event Action<float, float> TimeRemainingChanged;

        /// <summary>Raised on every client whenever the bonus time changes (previous, new).</summary>
        public event Action<float, float> BonusTimeRemainingChanged;

        /// <summary>Raised on every client when the round's phase changes.</summary>
        public event Action<RoundPhase> PhaseChanged;

        /// <summary>Raised on every client when someone reaches the end point.</summary>
        public event Action FinishedPlayersChanged;

        /// <summary>Raised on every client when the round number or target changes (normally once, at the start).</summary>
        public event Action RoundInfoChanged;

        /// <summary>Raised on every client when a player reaches the end point (player id, bonus points earned).</summary>
        public event Action<ulong, int> PlayerFinished;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public override void OnNetworkSpawn()
        {
            m_Rules = MatchRules.Get(gameRulesConfig);

            if (IsOwner && !NetInitialized)
            {
                int round = Mathf.Max(1, MatchProgress.RoundNumber);
                NetTimeRemaining = Mathf.Max(1, m_Rules.RoundTimeSeconds);
                NetBonusRemaining = Mathf.Max(0, m_Rules.BonusTimeSeconds);
                NetRoundNumber = round;
                NetPerPlayerTarget = MatchProgress.PerPlayerTarget(m_Rules, round);
                NetPhase = (int)RoundPhase.Playing;
                NetFinishedCount = 0;
                NetInitialized = true;
            }

            // End points in the scene itself (maps placed by hand); MatchLayout does the same for its map.
            PlayerEndPoint.SetUpAllInLoadedScenes();

            DetectChanges();
        }

        public override void FixedUpdateNetwork()
        {
            if (!IsOwner || !NetInitialized) return;

            switch ((RoundPhase)NetPhase)
            {
                case RoundPhase.Playing:
                    NetTimeRemaining = Mathf.Max(0f, NetTimeRemaining - Runner.DeltaTime);
                    NetBonusRemaining = Mathf.Max(0f, NetBonusRemaining - Runner.DeltaTime);
                    if (NetTimeRemaining <= 0f || EveryoneFinished())
                    {
                        NetPhase = (int)RoundPhase.Tallying;
                        NetPhaseTimer = TickTimer.CreateFromSeconds(Runner, tallySeconds);
                    }
                    break;

                case RoundPhase.Tallying:
                    if (NetPhaseTimer.ExpiredOrNotRunning(Runner))
                    {
                        int total = ComputeTeamTotal();
                        int target = NetPerPlayerTarget * Mathf.Max(1, CountPlayers());
                        NetFinalTeamTotal = total;
                        NetFinalTeamTarget = target;
                        NetPassed = total >= target;
                        NetPhase = (int)RoundPhase.Ended;
                        NetPhaseTimer = TickTimer.CreateFromSeconds(Runner, resultSeconds);
                        Debug.Log($"[Round] Round {NetRoundNumber} over: team scored {total} of {target} - {(NetPassed ? "target reached" : "target missed")}.", this);
                    }
                    break;

                case RoundPhase.Ended:
                    if (!m_LoadRequested && NetPhaseTimer.ExpiredOrNotRunning(Runner))
                    {
                        m_LoadRequested = true;
                        string next = NetPassed ? FusionSessionService.RoundResultsSceneName : FusionSessionService.FinalScoreSceneName;
                        if (!FusionSessionService.HasInstance || !FusionSessionService.Instance.LoadSceneForEveryone(next))
                        {
                            Debug.LogError($"[Round] Couldn't load '{next}' - is it in the Build Settings?", this);
                        }
                    }
                    break;
            }
        }

        public override void Render()
        {
            DetectChanges();
        }

        #endregion

        #region Finishing

        /// <summary>Whether this player has reached the end point this round.</summary>
        public bool IsPlayerFinished(ulong playerId)
        {
            if (!IsSpawned || playerId == 0) return false;
            int count = Mathf.Clamp(NetFinishedCount, 0, MaxPlayers);
            for (int i = 0; i < count; i++)
            {
                if ((ulong)NetFinishedIds[i] == playerId) return true;
            }
            return false;
        }

        /// <summary>Whether this client's own player has reached the end point.</summary>
        public bool LocalPlayerFinished => IsPlayerFinished(NetworkPlayers.LocalClientId);

        /// <summary>Called by PlayerEndPoint on the touching player's own machine.</summary>
        public void ReportLocalPlayerFinished()
        {
            if (!IsPlaying || LocalPlayerFinished) return;
            ReportFinishedRpc();
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void ReportFinishedRpc(RpcInfo info = default)
        {
            ulong playerId = NetworkPlayers.ToClientId(info.Source);
            if (playerId == 0) playerId = NetworkPlayers.ToClientId(Runner.LocalPlayer);
            if (playerId == 0 || NetPhase != (int)RoundPhase.Playing || IsPlayerFinished(playerId)) return;

            int count = Mathf.Clamp(NetFinishedCount, 0, MaxPlayers);
            if (count >= MaxPlayers) return;
            NetFinishedIds.Set(count, (int)playerId);
            NetFinishedCount = count + 1;

            // Whole seconds as shown on the HUD's bonus timer.
            int bonus = Mathf.CeilToInt(NetBonusRemaining) * Rules.BonusPointsPerSecond;
            if (bonus > 0 && NetworkPlayers.TryGetComponent(playerId, out PlayerScore score))
            {
                score.AddScore(bonus);
            }

            PlayerFinishedRpc((int)playerId, bonus);
            DetectChanges();
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void PlayerFinishedRpc(int playerId, int bonusPoints)
        {
            PlayerFinished?.Invoke((ulong)playerId, bonusPoints);
        }

        private bool EveryoneFinished()
        {
            int players = 0;
            foreach (PlayerRef player in Runner.ActivePlayers)
            {
                if (!IsPlayerFinished(NetworkPlayers.ToClientId(player))) return false;
                players++;
            }
            return players > 0;
        }

        #endregion

        #region Team Score

        /// <summary>Everyone's current score added up.</summary>
        public static int ComputeTeamTotal()
        {
            int total = 0;
            foreach (ulong id in NetworkPlayers.Ids)
            {
                if (NetworkPlayers.TryGetComponent(id, out PlayerScore score)) total += score.Score;
            }
            return total;
        }

        private int CountPlayers()
        {
            if (Runner == null) return NetworkPlayers.Count;
            int count = 0;
            foreach (PlayerRef _ in Runner.ActivePlayers) count++;
            return count;
        }

        #endregion

        #region Change Detection

        private void DetectChanges()
        {
            if (!IsSpawned || !NetInitialized) return;

            float time = NetTimeRemaining;
            if (!Mathf.Approximately(time, m_LastTimeRemaining))
            {
                float previous = m_LastTimeRemaining < 0f ? time : m_LastTimeRemaining;
                m_LastTimeRemaining = time;
                TimeRemainingChanged?.Invoke(previous, time);
            }

            float bonus = NetBonusRemaining;
            if (!Mathf.Approximately(bonus, m_LastBonusRemaining))
            {
                float previous = m_LastBonusRemaining < 0f ? bonus : m_LastBonusRemaining;
                m_LastBonusRemaining = bonus;
                BonusTimeRemainingChanged?.Invoke(previous, bonus);
            }

            int roundKey = NetRoundNumber * 1000003 + NetPerPlayerTarget;
            if (roundKey != m_LastRoundKey)
            {
                m_LastRoundKey = roundKey;
                RoundInfoChanged?.Invoke();
            }

            int finishedKey = BuildFinishedKey();
            if (finishedKey != m_LastFinishedKey)
            {
                m_LastFinishedKey = finishedKey;
                FadeFinishedPlayers();
                FinishedPlayersChanged?.Invoke();
            }
            else if (m_FadedPlayers.Count < Mathf.Clamp(NetFinishedCount, 0, MaxPlayers))
            {
                FadeFinishedPlayers(); // a finished player's avatar wasn't registered yet last time
            }

            int phase = NetPhase;
            if (phase != m_LastPhase)
            {
                m_LastPhase = phase;
                OnPhaseChangedLocally((RoundPhase)phase);
                PhaseChanged?.Invoke((RoundPhase)phase);
            }
        }

        private int BuildFinishedKey()
        {
            unchecked
            {
                int count = Mathf.Clamp(NetFinishedCount, 0, MaxPlayers);
                int key = count;
                for (int i = 0; i < count; i++) key = key * 31 + NetFinishedIds[i];
                return key;
            }
        }

        /// <summary>Fades out (on this client) every finished player not faded yet; freezes this client's own player if it's one of them.</summary>
        private void FadeFinishedPlayers()
        {
            int count = Mathf.Clamp(NetFinishedCount, 0, MaxPlayers);
            for (int i = 0; i < count; i++)
            {
                ulong id = (ulong)NetFinishedIds[i];
                if (m_FadedPlayers.Contains(id)) continue;
                if (!NetworkPlayers.TryGet(id, out CorePlayerManager player) || player == null) continue;

                m_FadedPlayers.Add(id);
                PlayerFinishFade.Begin(player);
            }
        }

        private void OnPhaseChangedLocally(RoundPhase phase)
        {
            if (phase == RoundPhase.Playing) return;

            // The round is over for everyone - nobody moves any more.
            CorePlayerManager local = NetworkPlayers.Local;
            if (local != null) local.SetInputLock(RoundOverInputLock, true);

            if (phase == RoundPhase.Ended) RecordResults();
        }

        /// <summary>Every client writes the same results into MatchProgress before the scene changes.</summary>
        private void RecordResults()
        {
            if (m_ResultsRecorded) return;
            m_ResultsRecorded = true;

            var results = new List<RoundResultEntry>();
            foreach (ulong id in NetworkPlayers.Ids)
            {
                if (!NetworkPlayers.TryGetComponent(id, out PlayerScore score)) continue;
                results.Add(new RoundResultEntry
                {
                    PlayerId = (int)id,
                    Name = NetworkPlayers.GetPlayerLabel(id),
                    Score = score.Score
                });
            }

            int localScore = 0;
            if (NetworkPlayers.Local != null && NetworkPlayers.TryGetComponent(NetworkPlayers.Local.OwnerClientId, out PlayerScore localPlayerScore))
            {
                localScore = localPlayerScore.Score;
            }

            int round = Mathf.Max(1, NetRoundNumber);
            int players = Mathf.Max(1, CountPlayers());
            int nextTarget = MatchProgress.PerPlayerTarget(Rules, round + 1) * players;

            MatchProgress.RecordRoundEnd(round, NetPassed, NetFinalTeamTotal, NetFinalTeamTarget, nextTarget,
                results, localScore, Rules.CarryOverPercent);
        }

        #endregion
    }
}
