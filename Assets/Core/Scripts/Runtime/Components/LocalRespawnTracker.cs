using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The death plane and "where do I come back" for this client's own player.
    ///
    /// DEATH PLANE: falling more than <see cref="DeathPlaneMargin"/> meters below the lowest part of the map
    /// (the map and its challenges, measured once the map is built) kills the player - their health is
    /// emptied, so it's a normal death (death penalty, death counted for challenge pars and wagers, the usual
    /// respawn countdown).
    ///
    /// RESPAWN POINT (GameManager asks <see cref="TryGetRespawnPoint"/> when respawning):
    ///   - in a challenge: the safe ground closest to where they went into that challenge (the last spot they
    ///     stood on before walking into its start);
    ///   - otherwise: the safe ground closest to where they died.
    /// "Safe ground" = spots this player has actually stood on recently (sampled while grounded and able to
    /// move), so it never picks somewhere unreachable or mid-air. Falls back to a spawn point if there's
    /// nothing yet.
    ///
    /// Added at runtime to the local player's avatar by NetworkPlayers.Register (no prefab setup).
    /// </summary>
    public class LocalRespawnTracker : MonoBehaviour
    {
        public const float DeathPlaneMargin = 5f;
        private const float SampleInterval = 0.2f;
        private const float MinSampleSpacing = 0.75f;
        private const int HistorySize = 400;
        private const float RespawnLift = 0.1f;

        /// <summary>The local player's tracker, if spawned.</summary>
        public static LocalRespawnTracker Local { get; private set; }

        private struct SafeSpot
        {
            public Vector3 Position;
            public float Yaw;
        }

        private CorePlayerManager m_Player;
        private CoreMovement m_Movement;
        private readonly List<SafeSpot> m_History = new List<SafeSpot>(HistorySize);
        private int m_HistoryNext;
        private float m_NextSampleTime;
        private SafeSpot? m_LastSafe;

        private SafeSpot? m_PendingChallengeEntry;
        private SafeSpot? m_ChallengeEntry;
        private bool m_InChallenge;

        private Vector3 m_DeathPosition;
        private bool m_HasDeathPosition;
        private bool m_Dying;

        private float m_KillY = float.NaN;
        private float m_NextMeasureTime;

        /// <summary>The height below which the player dies (NaN until the map is measured).</summary>
        public float KillY => m_KillY;

        public static void Attach(CorePlayerManager player)
        {
            if (player == null || !player.IsOwner) return;
            if (player.GetComponent<LocalRespawnTracker>() != null) return;
            var tracker = player.gameObject.AddComponent<LocalRespawnTracker>();
            tracker.m_Player = player;
        }

        private void Start()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();
            m_Movement = m_Player != null ? m_Player.CoreMovement : GetComponent<CoreMovement>();
            Local = this;

            ChallengeZone.LocalPlayerEntered += HandleLocalZoneEntered;
            ChallengeManager.PlayerChallengeStateChanged += HandleChallengeStateChanged;
            if (m_Player != null && m_Player.PlayerState != null) m_Player.PlayerState.OnLifeStateChanged += HandleLifeStateChanged;
        }

        private void OnDestroy()
        {
            if (Local == this) Local = null;
            ChallengeZone.LocalPlayerEntered -= HandleLocalZoneEntered;
            ChallengeManager.PlayerChallengeStateChanged -= HandleChallengeStateChanged;
            if (m_Player != null && m_Player.PlayerState != null) m_Player.PlayerState.OnLifeStateChanged -= HandleLifeStateChanged;
        }

        private void Update()
        {
            if (m_Player == null || !m_Player.IsSpawned) return;

            bool alive = m_Player.PlayerState == null || m_Player.PlayerState.LifeState != PlayerLifeState.Eliminated;

            // Remember where the player has safely stood.
            if (alive && Time.time >= m_NextSampleTime && m_Movement != null && m_Movement.IsGrounded && m_Movement.IsMovementEnabled)
            {
                m_NextSampleTime = Time.time + SampleInterval;
                RecordSafeSpot(transform.position, transform.eulerAngles.y);
            }

            // Death plane.
            if (float.IsNaN(m_KillY) && Time.time >= m_NextMeasureTime)
            {
                m_NextMeasureTime = Time.time + 0.5f;
                m_KillY = MeasureKillY();
            }
            if (alive && !m_Dying && !float.IsNaN(m_KillY) && transform.position.y < m_KillY)
            {
                Kill();
            }
        }

        private void RecordSafeSpot(Vector3 position, float yaw)
        {
            if (m_LastSafe.HasValue && (m_LastSafe.Value.Position - position).sqrMagnitude < MinSampleSpacing * MinSampleSpacing) return;

            var spot = new SafeSpot { Position = position, Yaw = yaw };
            m_LastSafe = spot;
            if (m_History.Count < HistorySize) m_History.Add(spot);
            else
            {
                m_History[m_HistoryNext] = spot;
                m_HistoryNext = (m_HistoryNext + 1) % HistorySize;
            }
        }

        private void Kill()
        {
            m_Dying = true;
            CoreStatsHandler stats = m_Player.CoreStats;
            if (stats == null) return;

            float health = stats.GetCurrentValue(StatKeys.Health);
            stats.ModifyStat(StatKeys.Health, -Mathf.Max(1f, health + 1f), m_Player.OwnerClientId, ModificationSource.Environmental);
        }

        private void HandleLifeStateChanged(PlayerLifeState state)
        {
            if (state == PlayerLifeState.Eliminated)
            {
                m_DeathPosition = transform.position;
                m_HasDeathPosition = true;
            }
            else
            {
                m_Dying = false;
            }
        }

        private void HandleLocalZoneEntered(ChallengeZone zone, ChallengeZoneKind kind)
        {
            if (kind != ChallengeZoneKind.Start || m_InChallenge) return;
            // Where they came from - claimed or not is only known once the master client answers.
            m_PendingChallengeEntry = m_LastSafe ?? new SafeSpot { Position = transform.position, Yaw = transform.eulerAngles.y };
        }

        private void HandleChallengeStateChanged(ulong playerId, bool inChallenge)
        {
            if (m_Player == null || playerId != m_Player.OwnerClientId) return;
            m_InChallenge = inChallenge;
            m_ChallengeEntry = inChallenge ? (m_PendingChallengeEntry ?? m_LastSafe) : null;
            if (!inChallenge) m_PendingChallengeEntry = null;
        }

        /// <summary>
        /// Where to bring the player back: by their challenge's entrance while in a challenge, otherwise the
        /// safe ground nearest (sideways first) to where they died. False if there's nowhere known yet.
        /// </summary>
        public bool TryGetRespawnPoint(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;

            SafeSpot? chosen = null;
            if (m_InChallenge && m_ChallengeEntry.HasValue)
            {
                chosen = m_ChallengeEntry;
            }
            else if (m_History.Count > 0)
            {
                Vector3 target = m_HasDeathPosition ? m_DeathPosition : transform.position;
                float best = float.MaxValue;
                foreach (SafeSpot spot in m_History)
                {
                    Vector3 d = spot.Position - target;
                    // Sideways distance matters most (a fall is straight down); height breaks ties.
                    float score = d.x * d.x + d.z * d.z + 0.1f * d.y * d.y;
                    if (score < best)
                    {
                        best = score;
                        chosen = spot;
                    }
                }
            }

            if (!chosen.HasValue) return false;
            position = chosen.Value.Position + Vector3.up * RespawnLift;
            rotation = Quaternion.Euler(0f, chosen.Value.Yaw, 0f);
            return true;
        }

        /// <summary>Lowest point of the map (and its challenges) minus the margin; NaN until the map exists.</summary>
        private float MeasureKillY()
        {
            MatchLayout layout = MatchLayout.Current;
            Bounds? bounds = null;

            if (layout != null)
            {
                if (!layout.IsBuilt) return float.NaN;
                bounds = RendererBounds(layout.transform.GetComponentsInChildren<Renderer>(true));
            }
            else
            {
                // A map placed straight into the scene: everything that isn't a player.
                var renderers = new List<Renderer>();
                foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r.GetComponentInParent<CorePlayerManager>() == null) renderers.Add(r);
                }
                bounds = RendererBounds(renderers);
            }

            if (!bounds.HasValue) return float.NaN;
            float killY = bounds.Value.min.y - DeathPlaneMargin;
            Debug.Log($"[Respawn] Death plane at y = {killY:0.0} (map bottom {bounds.Value.min.y:0.0}).", this);
            return killY;
        }

        private static Bounds? RendererBounds(IEnumerable<Renderer> renderers)
        {
            Bounds? bounds = null;
            foreach (Renderer r in renderers)
            {
                if (r == null || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (bounds.HasValue)
                {
                    Bounds b = bounds.Value;
                    b.Encapsulate(r.bounds);
                    bounds = b;
                }
                else bounds = r.bounds;
            }
            return bounds;
        }
    }
}
