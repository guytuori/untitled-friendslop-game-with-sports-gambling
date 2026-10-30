using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Counts what this client's own player does, for the wager system: jumps (all kinds, plus rail, wall
    /// and pole jumps separately), seconds running and walking on the ground, on grind rails, on walls, on
    /// poles and on slippery floors, complete turns round poles, items from item boxes, and deaths. Pole
    /// stats are kept for any pole and separately for upright (vertical) poles and bars (horizontal).
    /// Sends the running totals to everyone twice a second (sooner when a trigger action happens), along
    /// with which trigger actions happened (jumped, started walking / running, got on a rail / wall / pole) -
    /// those are what make WagerManager offer a wager about this player.
    ///
    /// Walking = on the ground, moving, with the walk button (CoreMovement.IsSprinting is the walk toggle
    /// since the walk/run speeds were swapped); running = the same without it.
    ///
    /// Poles: Pole Swing 2 (PoleSwing2Ability) when it's on the player, otherwise Dan's PoleGrabAbility
    /// (which kind of pole it's on is then taken from the nearest pole). Rotations only come from Pole Swing 2.
    ///
    /// Added at runtime to the local player's avatar by NetworkPlayers.Register (no prefab setup), and
    /// starts from zero with each new avatar, i.e. each round.
    /// </summary>
    public class LocalActionTracker : MonoBehaviour
    {
        private const float SendInterval = 0.5f;
        private const float MinTriggerSendInterval = 0.2f;
        private const float MoveMinSpeed = 1f;
        private const float SlipperyGrip = 0.5f;

        private static int s_Sequence; // never reset in a session, so a new avatar's reports always win

        private CorePlayerManager m_Player;
        private CoreMovement m_Movement;
        private GrindRailAbility m_Rail;
        private WallClimbAbility m_Wall;
        private PoleGrabAbility m_Pole;
        private PoleSwing2Ability m_Swing2;

        private readonly int[] m_Values = new int[WagerStats.StatCount];
        private readonly float[] m_SecondsAccum = new float[WagerStats.StatCount];
        private int m_TriggerMask;
        private bool m_Dirty;
        private float m_LastSendTime = float.NegativeInfinity;

        private bool m_WasRunning, m_WasWalking, m_WasOnRail, m_WasOnWall, m_WasOnPole;
        private bool m_PoleHorizontal; // the pole the player is on, or was on last (pole jumps are reported just after letting go)
        private int m_LastRotations;

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
            m_Swing2 = GetComponent<PoleSwing2Ability>();
            if (m_Swing2 != null) m_LastRotations = m_Swing2.CompletedRotations;

            PlayerActions.Performed += HandleAction;
            ItemManager.ItemBoxOpened += HandleItemBoxOpened;
            if (m_Player != null && m_Player.PlayerState != null) m_Player.PlayerState.OnLifeStateChanged += HandleLifeStateChanged;

            m_Dirty = true;
        }

        private void OnDestroy()
        {
            PlayerActions.Performed -= HandleAction;
            ItemManager.ItemBoxOpened -= HandleItemBoxOpened;
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
                case PlayerAction.PoleJump:
                    m_Values[(int)WagerStat.PoleJumps]++;
                    m_Values[(int)(m_PoleHorizontal ? WagerStat.HorizontalPoleJumps : WagerStat.VerticalPoleJumps)]++;
                    break;
            }

            m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.Jump);
            m_Dirty = true;
        }

        /// <summary>An item box opened by this player (the item counts when the master hands it out).</summary>
        private void HandleItemBoxOpened(ulong playerId, int boxIndex, ItemId item)
        {
            if (playerId == 0 || playerId != NetworkPlayers.LocalClientId) return;
            m_Values[(int)WagerStat.Items]++;
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
            bool moving = grounded && m_Movement.CurrentSpeed > MoveMinSpeed;
            // IsSprinting is the walk toggle now (the walk/run speeds were swapped).
            bool walking = moving && m_Movement.IsSprinting;
            bool running = moving && !m_Movement.IsSprinting;
            bool onRail = m_Rail != null && m_Rail.IsOnRail;
            bool onWall = m_Wall != null && m_Wall.IsClimbing;
            bool onSwing2 = m_Swing2 != null && m_Swing2.IsSwinging;
            bool onPole = onSwing2 || (m_Pole != null && m_Pole.IsOnPole);
            bool onIce = grounded && m_Movement.GroundGrip < SlipperyGrip;

            // Which kind of pole, worked out on grabbing: Pole Swing 2 knows; for Dan's poles, the nearest one.
            if (onPole && !m_WasOnPole) m_PoleHorizontal = onSwing2 ? !m_Swing2.IsOnUprightPole : NearestPoleIsHorizontal();

            Track(WagerStat.RunSeconds, running, dt);
            Track(WagerStat.WalkSeconds, walking, dt);
            Track(WagerStat.RailSeconds, onRail, dt);
            Track(WagerStat.WallSeconds, onWall, dt);
            Track(WagerStat.PoleSeconds, onPole, dt);
            Track(WagerStat.VerticalPoleSeconds, onPole && !m_PoleHorizontal, dt);
            Track(WagerStat.HorizontalPoleSeconds, onPole && m_PoleHorizontal, dt);
            Track(WagerStat.IceSeconds, onIce, dt);

            // Complete turns round a pole (Pole Swing 2 counts them).
            if (m_Swing2 != null && m_Swing2.CompletedRotations != m_LastRotations)
            {
                int turns = Mathf.Max(0, m_Swing2.CompletedRotations - m_LastRotations);
                m_LastRotations = m_Swing2.CompletedRotations;
                m_Values[(int)WagerStat.PoleRotations] += turns;
                m_Values[(int)(m_PoleHorizontal ? WagerStat.HorizontalPoleRotations : WagerStat.VerticalPoleRotations)] += turns;
                m_Dirty = true;
            }

            if (running && !m_WasRunning) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.Run);
            if (walking && !m_WasWalking) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.Walk);
            if (onRail && !m_WasOnRail) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.GrindRail);
            if (onWall && !m_WasOnWall) m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.WallRun);
            if (onPole && !m_WasOnPole)
            {
                m_TriggerMask |= WagerStats.TriggerBit(WagerTrigger.PoleClimb);
                m_TriggerMask |= WagerStats.TriggerBit(m_PoleHorizontal ? WagerTrigger.HorizontalPole : WagerTrigger.VerticalPole);
            }
            m_WasRunning = running;
            m_WasWalking = walking;
            m_WasOnRail = onRail;
            m_WasOnWall = onWall;
            m_WasOnPole = onPole;

            float now = Time.unscaledTime;
            bool due = m_Dirty && now - m_LastSendTime >= SendInterval;
            bool triggerDue = m_TriggerMask != 0 && now - m_LastSendTime >= MinTriggerSendInterval;
            if (due || triggerDue) Send(now);
        }

        /// <summary>Whether the pole nearest the player's hands is a bar (for Dan's PoleGrabAbility, which doesn't say).</summary>
        private bool NearestPoleIsHorizontal()
        {
            Vector3 hands = transform.position + Vector3.up * 1.6f;
            Pole nearest = null;
            float best = float.MaxValue;
            foreach (Pole pole in Pole.ActivePoles)
            {
                if (pole == null) continue;
                pole.ClosestAxisPoint(hands, out float distance, out bool withinHeight);
                if (!withinHeight) distance += 100f; // past an end: only if nothing else is near
                if (distance < best)
                {
                    best = distance;
                    nearest = pole;
                }
            }
            return nearest != null && nearest.IsHorizontal;
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
