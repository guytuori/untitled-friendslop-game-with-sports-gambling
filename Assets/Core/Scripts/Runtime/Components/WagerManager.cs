using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The wager ticker: offers wagers about players while the round plays on (nothing pauses), takes
    /// everyone's YES/NO bets, and pays them out.
    ///
    /// WHERE WAGERS COME FROM: the master list in WagerCatalog (built from wagers.txt). A wager is offered
    /// about a player when they do one of its trigger actions (jump, start running, get on a rail / wall /
    /// pole - reported by their LocalActionTracker), or, for the "no particular trigger" ones, every so
    /// often about a random player. Challenge-only wagers (!CHALLENGE! in the text file) are only offered
    /// about a player who's in a challenge, and run until that challenge ends. New wagers come at most every
    /// <see cref="minSpawnInterval"/>-<see cref="maxSpawnInterval"/> seconds, whatever happens, so 16 people
    /// jumping at once doesn't bury everyone in wagers. The [NUMBER]-style placeholder is rolled from its
    /// span, never at or below what the player has already done.
    ///
    /// PRICES: from how far along the player already is (the hidden WagerStats): YES costs
    /// 100 - % complete, NO costs % complete (each at least 1, at most 99), updated live. Betting costs the
    /// price immediately; a correct bet pays price x the Wager Payout rule.
    ///
    /// SETTLING: a wager settles YES the moment the player gets there (except "end with a score", which only
    /// settles at the end), challenge wagers settle when that challenge ends, and everything left settles
    /// when the round ends (before RoundTimer adds up the team score). Wagers nobody bet on just disappear
    /// once their betting window (<see cref="bettingWindowSeconds"/>) is over.
    ///
    /// Who can bet: anyone still in the round, except on wagers about themselves - unless they're the only
    /// player in the session (so solo testing works).
    ///
    /// Networking (Photon Fusion, Shared mode): a master-client scene singleton like RoundTimer. The open
    /// wagers are networked (everyone sees the same ticker and prices); bets go to the master client by RPC.
    /// The bets themselves only live on the master client, so a master change mid-round loses open bets.
    /// Added to [BB] Core by Friendslop > Refresh Everything From Assets. WagerHUD draws it.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class WagerManager : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        public const int MaxWagers = 24;

        /// <summary>One open wager, as everyone sees it.</summary>
        public struct WagerState : INetworkStruct
        {
            public int Id;
            public int TemplateIndex;
            public int SubjectId;
            /// <summary>The rolled number, in the units the text shows (seconds, jumps, points).</summary>
            public int Target;
            /// <summary>Challenge wagers: the stat's value when the wager was made (progress counts from there).</summary>
            public int Baseline;
            public int YesCost;
            public int NoCost;
            public TickTimer BetWindow;
            public NetworkBool Active;
        }

        /// <summary>What WagerHUD needs to show one wager.</summary>
        public struct WagerView
        {
            public int Id;
            public ulong SubjectId;
            public string Text;
            public int YesCost;
            public int NoCost;
            public bool BettingOpen;
        }

        [Header("Pacing")]
        [Tooltip("Shortest gap between two new wagers (seconds).")]
        [SerializeField] private float minSpawnInterval = 3f;
        [Tooltip("Longest gap between two new wagers when something is happening (seconds).")]
        [SerializeField] private float maxSpawnInterval = 5f;
        [Tooltip("Seconds into a round before the first wager.")]
        [SerializeField] private float firstWagerDelay = 5f;
        [Tooltip("At least this many seconds between 'no particular trigger' wagers.")]
        [SerializeField] private float idleWagerInterval = 10f;
        [Tooltip("A trigger action only counts for this many seconds while waiting for the cooldown.")]
        [SerializeField] private float triggerMemorySeconds = 3f;

        [Header("Betting")]
        [Tooltip("How long a wager can be bet on after it appears (seconds). It keeps running for the people who bet.")]
        [SerializeField] private float bettingWindowSeconds = 30f;

        public static WagerManager Instance { get; private set; }

        [Networked, Capacity(MaxWagers)] private NetworkArray<WagerState> NetWagers => default;
        [Networked] private int NetNextId { get; set; }
        [Networked] private NetworkBool NetRoundSettled { get; set; }

        /// <summary>Raised on every client when the open wagers or their prices change.</summary>
        public event Action WagersChanged;

        /// <summary>Raised on this client when the master accepted one of this player's bets (wager id, yes?, cost).</summary>
        public event Action<int, bool, int> LocalBetAccepted;

        /// <summary>Raised on this client when the master turned down one of this player's bets (wager id, reason).</summary>
        public event Action<int, string> LocalBetRejected;

        /// <summary>Raised on every client when a wager settles (wager id, YES won?, refunded?).</summary>
        public event Action<int, bool, bool> WagerSettled;

        // Master-only bookkeeping.
        private readonly Dictionary<int, Dictionary<ulong, (bool Yes, int Cost)>> m_Bets = new Dictionary<int, Dictionary<ulong, (bool, int)>>();
        private readonly List<(ulong Player, WagerTrigger Trigger, float Time)> m_PendingTriggers = new List<(ulong, WagerTrigger, float)>();
        private float m_NextSpawnTime;
        private float m_NextUpdateTime;
        private float m_LastIdleWagerTime = float.NegativeInfinity;
        private bool m_SpawnClockStarted;

        private int m_LastChangeKey;
        private WagerCatalog m_Catalog;

        private WagerCatalog Catalog => m_Catalog != null ? m_Catalog : (m_Catalog = WagerCatalog.Load());

        /// <summary>Win = cost x this (the Wager Payout rule).</summary>
        public float PayoutMultiplier => MatchRules.Get(null).WagerPayout;

        #endregion

        #region Lifecycle

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
            WagerStats.Clear(); // tallies are per round
            WagerStats.TriggersReported += HandleTriggersReported;

            if (Catalog == null)
            {
                Debug.LogWarning("[Wagers] There's no WagerCatalog yet - run Friendslop > Wagers > Update Wager Master List From Text File.", this);
            }
            DetectChanges();
        }

        public override void OnNetworkDespawn()
        {
            WagerStats.TriggersReported -= HandleTriggersReported;
        }

        public override void FixedUpdateNetwork()
        {
            if (!IsOwner) return;
            if (PracticeMode.IsActive) return; // no wagers in practice
            float now = Runner.SimulationTime;

            if (!m_SpawnClockStarted)
            {
                m_SpawnClockStarted = true;
                m_NextSpawnTime = now + firstWagerDelay;
            }

            RoundTimer round = RoundTimer.Instance;
            if (round != null && round.IsSpawned && !round.IsPlaying)
            {
                // Round over: settle everything before the team score is added up.
                if (!NetRoundSettled)
                {
                    NetRoundSettled = true;
                    SettleAll();
                }
                return;
            }

            if (now >= m_NextUpdateTime)
            {
                m_NextUpdateTime = now + 0.25f;
                UpdateOpenWagers();
            }

            if (now >= m_NextSpawnTime)
            {
                m_PendingTriggers.RemoveAll(t => now - t.Time > triggerMemorySeconds);
                m_NextSpawnTime = TrySpawn(now) ? now + Random.Range(minSpawnInterval, maxSpawnInterval) : now + 0.5f;
            }
        }

        public override void Render()
        {
            DetectChanges();
        }

        #endregion

        #region Reading (every client)

        /// <summary>Every open wager, oldest first.</summary>
        public List<WagerView> GetOpenWagers()
        {
            var list = new List<WagerView>();
            if (!IsSpawned || Catalog == null) return list;

            for (int i = 0; i < MaxWagers; i++)
            {
                WagerState s = NetWagers[i];
                if (!s.Active) continue;
                WagerCatalog.Template template = Catalog.Get(s.TemplateIndex);
                if (template == null) continue;

                list.Add(new WagerView
                {
                    Id = s.Id,
                    SubjectId = (ulong)s.SubjectId,
                    Text = WagerCatalog.Format(template, NetworkPlayers.GetDisplayName((ulong)s.SubjectId), s.Target),
                    YesCost = s.YesCost,
                    NoCost = s.NoCost,
                    BettingOpen = !s.BetWindow.ExpiredOrNotRunning(Runner)
                });
            }
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>Whether this player may bet on wagers about <paramref name="subjectId"/> (never yourself, unless you're alone).</summary>
        public bool CanBetOnSubject(ulong bettorId, ulong subjectId)
        {
            return bettorId != subjectId || CountActivePlayers() <= 1;
        }

        private void DetectChanges()
        {
            if (!IsSpawned) return;
            int key = 17;
            unchecked
            {
                for (int i = 0; i < MaxWagers; i++)
                {
                    WagerState s = NetWagers[i];
                    if (!s.Active) continue;
                    key = key * 31 + s.Id;
                    key = key * 31 + s.YesCost;
                    key = key * 31 + s.NoCost;
                    key = key * 31 + (s.BetWindow.ExpiredOrNotRunning(Runner) ? 1 : 0);
                }
            }
            if (key == m_LastChangeKey) return;
            m_LastChangeKey = key;
            WagersChanged?.Invoke();
        }

        #endregion

        #region Betting

        /// <summary>Called by WagerHUD when this player picks YES or NO on a wager.</summary>
        public void PlaceBet(int wagerId, bool yes)
        {
            if (!IsSpawned) return;
            PlaceBetRpc(wagerId, yes);
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void PlaceBetRpc(int wagerId, NetworkBool yes, RpcInfo info = default)
        {
            ulong bettor = NetworkPlayers.ToClientId(info.Source);
            if (bettor == 0) bettor = NetworkPlayers.ToClientId(Runner.LocalPlayer);

            int slot = FindSlot(wagerId);
            if (slot < 0) { BetRejectedRpc((int)bettor, wagerId, "That wager has already settled."); return; }

            WagerState s = NetWagers[slot];
            if (s.BetWindow.ExpiredOrNotRunning(Runner)) { BetRejectedRpc((int)bettor, wagerId, "Betting on that wager has closed."); return; }
            if (!ChallengeManager.IsRoundOpenFor(bettor)) { BetRejectedRpc((int)bettor, wagerId, "You've finished this round - no more wagers."); return; }
            if (!CanBetOnSubject(bettor, (ulong)s.SubjectId)) return;

            if (!m_Bets.TryGetValue(wagerId, out var bets))
            {
                bets = new Dictionary<ulong, (bool, int)>();
                m_Bets[wagerId] = bets;
            }
            if (bets.ContainsKey(bettor)) return;

            int cost = yes ? s.YesCost : s.NoCost;
            if (!NetworkPlayers.TryGetComponent(bettor, out PlayerScore score)) return;
            if (score.Score < cost) { BetRejectedRpc((int)bettor, wagerId, $"Not enough points ({cost} needed)."); return; }

            score.AddScore(-cost);
            bets[bettor] = (yes, cost);
            BetAcceptedRpc((int)bettor, wagerId, yes, cost);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void BetAcceptedRpc(int playerId, int wagerId, NetworkBool yes, int cost)
        {
            if ((ulong)playerId == NetworkPlayers.LocalClientId) LocalBetAccepted?.Invoke(wagerId, yes, cost);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void BetRejectedRpc(int playerId, int wagerId, string reason)
        {
            if ((ulong)playerId == NetworkPlayers.LocalClientId) LocalBetRejected?.Invoke(wagerId, reason);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void WagerSettledRpc(int wagerId, NetworkBool yesWon, NetworkBool refunded)
        {
            WagerSettled?.Invoke(wagerId, yesWon, refunded);
        }

        #endregion

        #region Challenges (called by ChallengeManager on the master client)

        public void OnChallengeStarted(ulong playerId)
        {
            if (!IsOwner) return;
            m_PendingTriggers.Add((playerId, WagerTrigger.ChallengeStart, Runner.SimulationTime));
        }

        /// <summary>Settles every challenge wager about this player (their challenge just ended).</summary>
        public void OnChallengeEnded(ulong playerId)
        {
            if (!IsOwner || Catalog == null) return;
            for (int i = 0; i < MaxWagers; i++)
            {
                WagerState s = NetWagers[i];
                if (!s.Active || (ulong)s.SubjectId != playerId) continue;
                WagerCatalog.Template template = Catalog.Get(s.TemplateIndex);
                if (template == null || template.scope != WagerScope.Challenge) continue;
                Settle(i, Progress(s, template) >= TargetUnits(s, template), refund: false);
            }
        }

        private static bool IsInChallenge(ulong playerId) =>
            ChallengeManager.Instance != null && ChallengeManager.Instance.IsPlayerInChallenge(playerId);

        #endregion

        #region Master: triggers, spawning, updating, settling

        private void HandleTriggersReported(int playerId, int mask)
        {
            if (!IsSpawned || !IsOwner) return;
            float now = Runner.SimulationTime;
            foreach (WagerTrigger trigger in (WagerTrigger[])Enum.GetValues(typeof(WagerTrigger)))
            {
                if (trigger == WagerTrigger.None) continue;
                if ((mask & WagerStats.TriggerBit(trigger)) != 0) m_PendingTriggers.Add(((ulong)playerId, trigger, now));
            }
        }

        private bool TrySpawn(float now)
        {
            if (Catalog == null || Catalog.Templates.Count == 0) return false;
            if (CountActive() >= MaxWagers - 1) return false;

            // Something just happened? Offer a wager about it (most recent first).
            for (int t = m_PendingTriggers.Count - 1; t >= 0; t--)
            {
                (ulong player, WagerTrigger trigger, _) = m_PendingTriggers[t];
                m_PendingTriggers.RemoveAt(t);
                if (!IsEligibleSubject(player)) continue;

                bool inChallenge = IsInChallenge(player);
                var candidates = new List<int>();
                for (int i = 0; i < Catalog.Templates.Count; i++)
                {
                    WagerCatalog.Template template = Catalog.Templates[i];
                    if (!IsOfferable(template)) continue;
                    if (template.scope == WagerScope.Challenge && !inChallenge) continue;
                    bool matches = trigger == WagerTrigger.ChallengeStart
                        ? template.scope == WagerScope.Challenge
                        : template.trigger == trigger;
                    if (matches) candidates.Add(i);
                }

                if (TryCreateFromCandidates(candidates, player)) return true;
            }

            // Nothing happening: now and then, a "no particular trigger" wager about someone.
            if (now - m_LastIdleWagerTime >= idleWagerInterval)
            {
                var players = new List<ulong>();
                foreach (PlayerRef p in Runner.ActivePlayers)
                {
                    ulong id = NetworkPlayers.ToClientId(p);
                    if (IsEligibleSubject(id)) players.Add(id);
                }
                if (players.Count > 0)
                {
                    ulong subject = players[Random.Range(0, players.Count)];
                    var candidates = new List<int>();
                    for (int i = 0; i < Catalog.Templates.Count; i++)
                    {
                        WagerCatalog.Template template = Catalog.Templates[i];
                        if (IsOfferable(template) && template.trigger == WagerTrigger.None && template.scope == WagerScope.Round) candidates.Add(i);
                    }
                    if (TryCreateFromCandidates(candidates, subject))
                    {
                        m_LastIdleWagerTime = now;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>Whether the game can track this wager here: pickup wagers only on maps with pickups left.</summary>
        private static bool IsOfferable(WagerCatalog.Template template)
        {
            if (template.stat == WagerStat.Pickups)
            {
                return !string.IsNullOrEmpty(template.numberPlaceholder) &&
                       PickupManager.Instance != null && PickupManager.Instance.RemainingCount > 0;
            }
            return template.supported;
        }

        /// <summary>A player wagers can be about: spawned, still in the round.</summary>
        private bool IsEligibleSubject(ulong playerId)
        {
            if (playerId == 0 || !NetworkPlayers.TryGetComponent(playerId, out PlayerScore _)) return false;
            return ChallengeManager.IsRoundOpenFor(playerId);
        }

        private bool TryCreateFromCandidates(List<int> candidates, ulong subject)
        {
            // Random order, first one that works.
            for (int n = candidates.Count; n > 0; n--)
            {
                int pick = Random.Range(0, n);
                int templateIndex = candidates[pick];
                candidates[pick] = candidates[n - 1];
                if (TryCreate(templateIndex, subject)) return true;
            }
            return false;
        }

        private bool TryCreate(int templateIndex, ulong subject)
        {
            WagerCatalog.Template template = Catalog.Get(templateIndex);
            if (template == null) return false;

            // Not the same wager about the same player twice at once.
            for (int i = 0; i < MaxWagers; i++)
            {
                WagerState s = NetWagers[i];
                if (s.Active && s.TemplateIndex == templateIndex && (ulong)s.SubjectId == subject) return false;
            }

            int slot = -1;
            for (int i = 0; i < MaxWagers; i++)
            {
                if (!NetWagers[i].Active) { slot = i; break; }
            }
            if (slot < 0) return false;

            int raw = WagerStats.Get((int)subject, template.stat);
            int baseline = template.scope == WagerScope.Challenge ? raw : 0;
            int alreadyDone = template.scope == WagerScope.Challenge ? 0 : (template.IsSeconds ? raw / 10 : raw);

            // Roll a number the player hasn't reached yet.
            int low = Mathf.Max(template.minNumber, alreadyDone + 1);
            int high = Mathf.Max(template.minNumber, template.maxNumber);
            if (template.stat == WagerStat.Pickups && PickupManager.Instance != null)
            {
                // Never more than there are left to collect.
                high = Mathf.Min(high, alreadyDone + PickupManager.Instance.RemainingCount);
            }
            if (low > high) return false;
            int target = Random.Range(low, high + 1);

            var state = new WagerState
            {
                Id = ++NetNextId,
                TemplateIndex = templateIndex,
                SubjectId = (int)subject,
                Target = target,
                Baseline = baseline,
                BetWindow = TickTimer.CreateFromSeconds(Runner, bettingWindowSeconds),
                Active = true
            };
            Price(ref state, template);
            NetWagers.Set(slot, state);
            DetectChanges();
            return true;
        }

        /// <summary>Re-prices open wagers, settles any that came true, and drops ones nobody bet on once betting closes.</summary>
        private void UpdateOpenWagers()
        {
            if (Catalog == null) return;
            for (int i = 0; i < MaxWagers; i++)
            {
                WagerState s = NetWagers[i];
                if (!s.Active) continue;
                WagerCatalog.Template template = Catalog.Get(s.TemplateIndex);
                if (template == null) { Settle(i, false, refund: true); continue; }

                if (!NetworkPlayers.TryGetComponent((ulong)s.SubjectId, out PlayerScore _))
                {
                    Settle(i, false, refund: true); // the player left
                    continue;
                }

                bool hasBets = m_Bets.TryGetValue(s.Id, out var bets) && bets.Count > 0;
                if (template.stat != WagerStat.FinalScore && Progress(s, template) >= TargetUnits(s, template))
                {
                    Settle(i, true, refund: false);
                    continue;
                }
                if (!hasBets && s.BetWindow.ExpiredOrNotRunning(Runner))
                {
                    Settle(i, false, refund: false); // nobody bet - just goes away
                    continue;
                }

                int yes = s.YesCost, no = s.NoCost;
                Price(ref s, template);
                if (yes != s.YesCost || no != s.NoCost) NetWagers.Set(i, s);
            }
        }

        private void SettleAll()
        {
            if (Catalog == null) return;
            for (int i = 0; i < MaxWagers; i++)
            {
                WagerState s = NetWagers[i];
                if (!s.Active) continue;
                WagerCatalog.Template template = Catalog.Get(s.TemplateIndex);
                if (template == null) { Settle(i, false, refund: true); continue; }
                Settle(i, Progress(s, template) >= TargetUnits(s, template), refund: false);
            }
        }

        private void Settle(int slot, bool yesWon, bool refund)
        {
            WagerState s = NetWagers[slot];
            if (!s.Active) return;

            if (m_Bets.TryGetValue(s.Id, out var bets))
            {
                float multiplier = PayoutMultiplier;
                foreach (var bet in bets)
                {
                    if (!NetworkPlayers.TryGetComponent(bet.Key, out PlayerScore score)) continue;
                    if (refund) score.AddScore(bet.Value.Cost);
                    else if (bet.Value.Yes == yesWon) score.AddScore(Mathf.RoundToInt(bet.Value.Cost * multiplier));
                }
                m_Bets.Remove(s.Id);
            }

            s.Active = false;
            NetWagers.Set(slot, s);
            WagerSettledRpc(s.Id, yesWon, refund);
            DetectChanges();
        }

        /// <summary>How far the player is, in the stat's own units (tenths of a second for time).</summary>
        private static int Progress(WagerState s, WagerCatalog.Template template)
        {
            int value = WagerStats.Get(s.SubjectId, template.stat);
            return template.scope == WagerScope.Challenge ? value - s.Baseline : value;
        }

        private static int TargetUnits(WagerState s, WagerCatalog.Template template) =>
            template.IsSeconds ? s.Target * 10 : s.Target;

        /// <summary>YES costs 100 - % complete, NO costs % complete (1-99 each).</summary>
        private static void Price(ref WagerState s, WagerCatalog.Template template)
        {
            float complete = Mathf.Clamp01(Progress(s, template) / (float)Mathf.Max(1, TargetUnits(s, template)));
            int percent = Mathf.RoundToInt(complete * 100f);
            s.YesCost = Mathf.Clamp(100 - percent, 1, 99);
            s.NoCost = Mathf.Clamp(percent, 1, 99);
        }

        private int FindSlot(int wagerId)
        {
            for (int i = 0; i < MaxWagers; i++)
            {
                WagerState s = NetWagers[i];
                if (s.Active && s.Id == wagerId) return i;
            }
            return -1;
        }

        private int CountActive()
        {
            int count = 0;
            for (int i = 0; i < MaxWagers; i++) if (NetWagers[i].Active) count++;
            return count;
        }

        private int CountActivePlayers()
        {
            if (Runner == null) return NetworkPlayers.Count;
            int count = 0;
            foreach (PlayerRef _ in Runner.ActivePlayers) count++;
            return count;
        }

        #endregion
    }
}
