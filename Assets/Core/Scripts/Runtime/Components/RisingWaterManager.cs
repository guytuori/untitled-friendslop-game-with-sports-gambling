using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Rising water (Ninja Pirate Tower): the map's water object is a moving death plane, and pumps up the
    /// tower raise it.
    ///
    /// MAP SET-UP (no components needed - found by name when the map is built, like pickups):
    ///   - The water: an object whose name starts with <c>ninja_pirate_water</c> (or contains
    ///     <c>rising_water</c>, for future maps). Its top surface is the water level. It starts where it's
    ///     placed in the map, and any colliders on it are removed so players fall in rather than stand on it.
    ///   - Pumps: objects whose name starts with <c>pump_head</c>. A player holding Grab within
    ///     <see cref="grabRange"/> of a pump sets it off: everyone hears Resources/Water/Water.mp3 and the
    ///     water rises (at <see cref="riseSpeed"/>) until its top is flush with the bottom of that pump.
    ///     Water never goes down - a pump at or below the current level does nothing. Add as many as you like.
    ///     Pumps are solid: any mesh in one without a collider gets a MeshCollider when the map is built.
    ///
    /// DEATH: a player whose feet are more than <see cref="DeathDepth"/> below the water's surface dies (a
    /// normal death, see LocalRespawnTracker). RESPAWN: if the spot LocalRespawnTracker would bring them back
    /// to is below where the water is heading, they come back standing on top of the pump that set the
    /// current level instead.
    ///
    /// Networking (Photon Fusion, Shared mode): a master-client scene singleton like PickupManager. Every
    /// machine finds the same water and pumps in the same (hierarchy) order, so the only networked state is
    /// which pump set the level. The player's own machine notices them grabbing a pump and asks the master;
    /// the master accepts it if it raises the water, and tells everyone (sound). Each machine animates its
    /// own water towards that pump's level; late joiners snap straight to it.
    ///
    /// Added to [BB] Core by Friendslop > Refresh Everything From Assets.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class RisingWaterManager : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        public const string WaterSoundPath = "Water/Water";

        /// <summary>A player dies once their feet are this far below the surface.</summary>
        public const float DeathDepth = 0.15f;

        /// <summary>A respawn spot this close above where the water is heading still counts as underwater.</summary>
        private const float SafeMargin = 0.5f;

        private const float ReportRetrySeconds = 0.5f;
        private const float RespawnLift = 0.1f;

        [Tooltip("How fast the water rises, in meters per second.")]
        [SerializeField] private float riseSpeed = 2f;

        [Tooltip("How close (meters, from the pump's surface to the player's body) a player holding Grab has to be to set a pump off.")]
        [SerializeField] private float grabRange = 1f;

        public static RisingWaterManager Instance { get; private set; }

        /// <summary>Which pump set the water level, plus one (0 = none yet - the water is where the map put it).</summary>
        [Networked] private int NetActivePumpPlusOne { get; set; }

        /// <summary>Raised on every machine when someone sets a pump off (pump index, player id).</summary>
        public static event Action<int, ulong> PumpActivated;

        private struct Pump
        {
            public Transform Transform;
            public Bounds Bounds; // world space, measured when the map was built
        }

        private readonly List<Pump> m_Pumps = new List<Pump>();
        private Transform m_Water;
        private float m_WaterTopOffset;  // water top minus the water transform's y
        private float m_StartTop;        // the water's top where the map placed it
        private float m_CurrentTop;      // the water's top right now (animated locally)
        private bool m_Registered;
        private bool m_SnappedToNetworked;
        private float m_LastReportTime = float.NegativeInfinity;
        private AudioClip m_WaterSound;
        private CharacterController m_LocalController;
        private CorePlayerManager m_LocalControllerOwner;

        /// <summary>True on a map with rising water.</summary>
        public bool HasWater => m_Water != null;

        /// <summary>Where the water's surface is right now (only meaningful when <see cref="HasWater"/>).</summary>
        public float CurrentTop => m_CurrentTop;

        /// <summary>Where the water's surface is heading: the active pump's bottom, or where it started.</summary>
        public float TargetTop
        {
            get
            {
                int pump = ActivePumpIndex;
                return pump >= 0 ? Mathf.Max(m_StartTop, m_Pumps[pump].Bounds.min.y) : m_StartTop;
            }
        }

        /// <summary>The pump that set the current level, or -1.</summary>
        public int ActivePumpIndex
        {
            get
            {
                int index = IsSpawned ? NetActivePumpPlusOne - 1 : -1;
                return index >= 0 && index < m_Pumps.Count ? index : -1;
            }
        }

        #endregion

        #region Static helpers (LocalRespawnTracker)

        /// <summary>The water's surface right now, if this map has rising water.</summary>
        public static bool TryGetWaterTop(out float top)
        {
            RisingWaterManager m = Instance;
            if (m != null && m.m_Registered && m.HasWater)
            {
                top = m.m_CurrentTop;
                return true;
            }
            top = 0f;
            return false;
        }

        /// <summary>True if a player standing at <paramref name="position"/> is (or soon will be) underwater.</summary>
        public static bool IsUnderwaterSoon(Vector3 position)
        {
            RisingWaterManager m = Instance;
            if (m == null || !m.m_Registered || !m.HasWater) return false;
            return position.y < Mathf.Max(m.TargetTop, m.m_CurrentTop) + SafeMargin;
        }

        /// <summary>Standing on top of the pump that set the current level - false if no pump has gone off yet.</summary>
        public static bool TryGetPumpRespawn(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            RisingWaterManager m = Instance;
            if (m == null || !m.m_Registered) return false;

            int pump = m.ActivePumpIndex;
            if (pump < 0) return false;

            Bounds b = m.m_Pumps[pump].Bounds;
            position = new Vector3(b.center.x, b.max.y + RespawnLift, b.center.z);
            rotation = Quaternion.Euler(0f, m.m_Pumps[pump].Transform.eulerAngles.y, 0f);
            return true;
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            MatchLayout.Built -= HandleMapBuilt;
        }

        public override void OnNetworkSpawn()
        {
            m_WaterSound = Resources.Load<AudioClip>(WaterSoundPath);
            MatchLayout.Built += HandleMapBuilt;
            TryRegister();
        }

        public override void OnNetworkDespawn()
        {
            MatchLayout.Built -= HandleMapBuilt;
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (!m_Registered) TryRegister();
            if (!m_Registered || !HasWater) return;

            // Late joiners (and the first frame): the water is already wherever the session has it.
            if (!m_SnappedToNetworked)
            {
                m_SnappedToNetworked = true;
                m_CurrentTop = TargetTop;
                ApplyWaterHeight();
            }

            float target = TargetTop;
            if (!Mathf.Approximately(m_CurrentTop, target))
            {
                m_CurrentTop = Mathf.MoveTowards(m_CurrentTop, target, riseSpeed * Time.deltaTime);
                ApplyWaterHeight();
            }

            CheckLocalPlayerGrab();
        }

        #endregion

        #region Finding the water and pumps

        private void HandleMapBuilt(MatchLayout layout) => TryRegister();

        private void TryRegister()
        {
            if (m_Registered) return;

            var roots = new List<Transform>();
            MatchLayout layout = MatchLayout.Current;
            if (layout != null)
            {
                if (!layout.IsBuilt) return; // wait for the map
                roots.Add(layout.transform);
            }
            else
            {
                for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject root in scene.GetRootGameObjects()) roots.Add(root.transform);
                }
            }

            m_Registered = true;

            foreach (Transform root in roots)
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (m_Water == null && IsWaterName(t.name) && !HasMatchingParent(t, IsWaterName)) m_Water = t;
                    else if (IsPumpName(t.name) && !HasMatchingParent(t, IsPumpName) && TryGetBounds(t, out Bounds bounds))
                    {
                        m_Pumps.Add(new Pump { Transform = t, Bounds = bounds });
                        AddMissingColliders(t); // solid - players bump into it rather than running through
                    }
                }
            }

            if (m_Water == null)
            {
                if (m_Pumps.Count > 0) Debug.LogWarning($"[Water] This map has {m_Pumps.Count} pump(s) but no rising water (an object named ninja_pirate_water...).", this);
                return;
            }

            // Players fall into the water - they don't stand on it.
            foreach (Collider c in m_Water.GetComponentsInChildren<Collider>(true)) Destroy(c);

            if (!TryGetBounds(m_Water, out Bounds waterBounds)) waterBounds = new Bounds(m_Water.position, Vector3.zero);
            m_StartTop = waterBounds.max.y;
            m_WaterTopOffset = m_StartTop - m_Water.position.y;
            m_CurrentTop = m_StartTop;

            Debug.Log($"[Water] Rising water found ('{m_Water.name}', top at {m_StartTop:0.##}) with {m_Pumps.Count} pump(s).", this);
        }

        /// <summary>Gives every mesh under <paramref name="root"/> that has no collider a MeshCollider of its own shape.</summary>
        private static void AddMissingColliders(Transform root)
        {
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;
                filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
            }
        }

        private static bool IsWaterName(string name)
        {
            string n = name.ToLowerInvariant();
            return n.StartsWith("ninja_pirate_water") || n.Contains("rising_water");
        }

        private static bool IsPumpName(string name) => name.ToLowerInvariant().StartsWith("pump_head");

        private static bool HasMatchingParent(Transform t, Func<string, bool> match)
        {
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                if (match(p.name)) return true;
            }
            return false;
        }

        private static bool TryGetBounds(Transform t, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }

        private void ApplyWaterHeight()
        {
            if (m_Water == null) return;
            Vector3 p = m_Water.position;
            p.y = m_CurrentTop - m_WaterTopOffset;
            m_Water.position = p;
        }

        #endregion

        #region Pumps

        private void CheckLocalPlayerGrab()
        {
            if (m_Pumps.Count == 0) return;
            if (Time.time - m_LastReportTime < ReportRetrySeconds) return;

            CorePlayerManager local = NetworkPlayers.Local;
            if (local == null || !local.IsSpawned || local.CoreMovement == null || !local.CoreMovement.IsGrabHeld) return;
            if (local.PlayerState != null && local.PlayerState.LifeState == PlayerLifeState.Eliminated) return;
            if (!ChallengeManager.IsRoundOpenFor(NetworkPlayers.LocalClientId)) return;

            if (m_LocalControllerOwner != local)
            {
                m_LocalControllerOwner = local;
                m_LocalController = local.GetComponent<CharacterController>();
            }

            Bounds body = m_LocalController != null && m_LocalController.enabled
                ? m_LocalController.bounds
                : new Bounds(local.transform.position + Vector3.up * 0.9f, new Vector3(0.7f, 1.8f, 0.7f));

            float current = TargetTop;
            for (int i = 0; i < m_Pumps.Count; i++)
            {
                Bounds pump = m_Pumps[i].Bounds;
                if (pump.min.y <= current + 0.01f) continue; // wouldn't raise the water

                // Gap between the player's body and the pump (0 when touching).
                Vector3 closestOnPump = pump.ClosestPoint(body.center);
                Vector3 closestOnBody = body.ClosestPoint(closestOnPump);
                if ((closestOnPump - closestOnBody).sqrMagnitude > grabRange * grabRange) continue;

                m_LastReportTime = Time.time;
                ActivatePumpRpc(i);
                return;
            }
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void ActivatePumpRpc(int index, RpcInfo info = default)
        {
            ulong player = NetworkPlayers.ToClientId(info.Source);
            if (player == 0) player = NetworkPlayers.ToClientId(Runner.LocalPlayer);

            if (index < 0 || index >= m_Pumps.Count) return;
            if (m_Pumps[index].Bounds.min.y <= TargetTop + 0.01f) return; // someone already raised it higher
            if (!ChallengeManager.IsRoundOpenFor(player)) return;

            NetActivePumpPlusOne = index + 1;
            PumpActivatedRpc(index, (int)player);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void PumpActivatedRpc(int index, int playerId)
        {
            AudioVolumeService.PlayOneShot(m_WaterSound, AudioCategory.SoundEffects, Vector3.zero);
            PumpActivated?.Invoke(index, (ulong)playerId);
        }

        #endregion
    }
}
