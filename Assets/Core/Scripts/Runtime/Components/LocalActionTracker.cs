using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Counts what this client's own player does, for the wager system: jumps (all kinds, plus rail, wall
    /// and pole jumps separately), seconds running on the ground, on grind rails, on walls, on poles and on
    /// slippery floors, and deaths. Sends the running totals to everyone twice a second (sooner when a
    /// trigger action happens), along with which trigger actions happened (jumped, started running, got on
    /// a rail / wall / pole) - those are what make WagerManager offer a wager about this player.
    ///
    /// Added at runtime to the local player's avatar by NetworkPlayers.Register (no prefab setup), and
    /// starts from zero with each new avatar, i.e. each round.
    /// </summary>
    public class LocalActionTracker : MonoBehaviour
    {
        private const float SendInterval = 0.5f;
        private const float MinTriggerSendInterval = 0.2f;
        private const float RunMinSpeed = 1f;
        private const float SlipperyGrip = 0.5f;

        private static int s_Sequence; // never reset in a session, so a new avatar's reports always win

        private CorePlayerManager m_Player;
        private CoreMovement m_Movement;
        private GrindRailAbility m_Rail;
        private WallClimbAbility m_Wall;
        private PoleGrabAbility m_Pole;

        private readonly int[] m_Values = new int[WagerStats.StatCount];
        private readonly float[] m_SecondsAccum = new float[WagerStats.StatCount];
        private int m_TriggerMask;
        private bool m_Dirty;
        private float m_LastSendTime = float.NegativeInfinity;

        private bool m_WasRunning, m_WasOnRail, m_WasOnWall, m_WasOnPole;

        public static void Attach(CorePlayerManager player)
        {
            if (player == null || !player.IsOwner) return;
            if (player.GetComponent<LocalActionTracker>() != null) return;
            var tracker = player.gameObject.AddComponent<LocalActionTracker>();
            tracker.m_Player = player;
        }

        private void Start()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();
            m_Movement = m_Player != null ? m_Player.CoreMovement : GetComponent<CoreMovement>();
            m_Rail = GetComponent<GrindRailAbility>();
            m_Wall = GetComponent<WallClimbAbility>();
            m_Pole = GetComponent<PoleGrabAbility>();

            PlayerActions.Performed += HandleAction;
            if (m_Player != null && m_Player.PlayerState != null) m_Player.PlayerState.OnLifeStateChanged += HandleLifeStateChanged;

            m_Dirty = true;
        }

        private void OnDestroy()
        {
            PlayerActions.Performed -= HandleAction;
            if (m_Player != null && m_Player.PlayerState != null) m_Player.PlayerState.OnLifeStateChanged -= HandleLifeStateChanged;
        }

        private void HandleAction(GameObject actor, PlayerAction action)
        {
            if (actor != gameObject) return;

            m_Values[(int)WagerStat.Jumps]++; // every kind of jump counts as a jump
            switch (action)
            {
                case PlayerAction.RailJump: m_Values[(int)WagerStat.RailJumps]++; break;
                case PlayerAction.WallJump: m_Values[(int)WagerStat.WallJumps]++; break;
                case PlayerAction.PoleJump: m_Values[(int)WagerStat.PoleJumps]++; break;
            }

            m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.Jump);
            m_Dirty = true;
        }

        private void HandleLifeStateChanged(PlayerLifeState state)
        {
            if (state != PlayerLifeState.Eliminated) return;
            m_Values[(int)WagerStat.Deaths]++;
            m_Dirty = true;
        }

        private void Update()
        {
            if (m_Player == null || !m_Player.IsSpawned) return;
            float dt = Time.deltaTime;

            bool grounded = m_Movement != null && m_Movement.IsGrounded;
            bool running = grounded && m_Movement.IsSprinting && m_Movement.CurrentSpeed > RunMinSpeed;
            bool onRail = m_Rail != null && m_Rail.IsOnRail;
            bool onWall = m_Wall != null && m_Wall.IsClimbing;
            bool onPole = m_Pole != null && m_Pole.IsOnPole;
            bool onIce = grounded && m_Movement.GroundGrip < SlipperyGrip;

            Track(WagerStat.RunSeconds, running, dt);
            Track(WagerStat.RailSeconds, onRail, dt);
            Track(WagerStat.WallSeconds, onWall, dt);
            Track(WagerStat.PoleSeconds, onPole, dt);
            Track(WagerStat.IceSeconds, onIce, dt);

            if (running && !m_WasRunning) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.Run);
            if (onRail && !m_WasOnRail) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.GrindRail);
            if (onWall && !m_WasOnWall) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.WallRun);
            if (onPole && !m_WasOnPole) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.PoleClimb);
            m_WasRunning = running;
            m_WasOnRail = onRail;
            m_WasOnWall = onWall;
            m_WasOnPole = onPole;

            float now = Time.unscaledTime;
            bool due = m_Dirty && now - m_LastSendTime >= SendInterval;
            bool triggerDue = m_TriggerMask != 0 && now - m_LastSendTime >= MinTriggerSendInterval;
            if (due || triggerDue) Send(now);
        }

        /// <summary>Adds time to a seconds stat (kept in tenths of a second).</summary>
        private void Track(WagerStat stat, bool active, float dt)
        {
            if (!active) return;
            int i = (int)stat;
            m_SecondsAccum[i] += dt;
            int tenths = Mathf.FloorToInt(m_SecondsAccum[i] * 10f);
            if (tenths != m_Values[i])
            {
                m_Values[i] = tenths;
                m_Dirty = true;
            }
        }

        private void Send(float now)
        {
            m_LastSendTime = now;
            m_Dirty = false;
            int mask = m_TriggerMask;
            m_TriggerMask = 0;
            WagerStatsSync.Send(NetworkPlayers.Runner, ++s_Sequence, m_Values, mask);
        }
    }
}
