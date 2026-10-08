using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Pickups (Mario coins / Sonic rings): every <c>pickup</c> entity in the map and its challenges becomes a
    /// floating, spinning <see cref="Pickup"/>. Running through one pops it, plays the coin sound and gives
    /// <see cref="pointsPerPickup"/> points. Once someone has it, it's gone for everyone for the rest of the
    /// round (the next round reloads the scene, so they're all back).
    ///
    /// Respects the Pickups rule from the Host Game screen - with Pickups Off, the markers are left empty.
    ///
    /// Networking (Photon Fusion, Shared mode): a master-client scene singleton like RoundTimer. Every
    /// machine finds the same markers in the same (hierarchy) order once MatchLayout has built the map, so a
    /// pickup is just an index. Each player's own machine notices its own player touching one (no trigger
    /// colliders - a simple distance check, so nothing else in the game can bump into them), hides it and
    /// plays the sound straight away, then asks the master for it. The master gives it to whoever asked
    /// first: it sets that pickup's bit in a networked bit set (so everyone - late joiners too - hides it),
    /// adds the points, and tells everyone who got it (counted per player for the "collect N pickups" wagers).
    ///
    /// Added to [BB] Core by Friendslop > Refresh Everything From Assets.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class PickupManager : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        private const int BitWords = 64;

        /// <summary>Most pickups a map (with its challenges) can have.</summary>
        public const int MaxPickups = BitWords * 32;

        public const string CoinSoundPath = "Pickups/Coin";

        [Tooltip("Points for each pickup collected (the master client's value is the one used).")]
        [SerializeField] private int pointsPerPickup = 10;

        [Tooltip("How close (sideways, in meters) the player's body has to get to a pickup's center to collect it.")]
        [SerializeField] private float pickupRadius = 0.8f;

        [Tooltip("Seconds for one full turn of the spin.")]
        [SerializeField] private float secondsPerSpin = 2f;

        public static PickupManager Instance { get; private set; }

        [Networked, Capacity(BitWords)] private NetworkArray<int> NetCollected => default;
        [Networked] private int NetCollectedCount { get; set; }

        /// <summary>How many each player has collected this round (everyone keeps this, for the wagers).</summary>
        private static readonly Dictionary<int, int> s_CollectedByPlayer = new Dictionary<int, int>();

        /// <summary>Raised on every machine when someone collects one (player id, pickup index).</summary>
        public static event Action<ulong, int> PickupCollected;

        private readonly List<Pickup> m_Pickups = new List<Pickup>();
        private bool m_Registered;
        private int m_AppliedCount = -1;
        private AudioClip m_CoinSound;
        private CharacterController m_LocalController;
        private CorePlayerManager m_LocalControllerOwner;

        public int PointsPerPickup => pointsPerPickup;

        /// <summary>Pickups in this map (0 until the map is built, or with the Pickups rule off).</summary>
        public int TotalCount => m_Pickups.Count;

        /// <summary>Pickups nobody has collected yet.</summary>
        public int RemainingCount => IsSpawned ? Mathf.Max(0, m_Pickups.Count - NetCollectedCount) : m_Pickups.Count;

        /// <summary>How many pickups the player has collected this round.</summary>
        public static int CollectedBy(int playerId) => s_CollectedByPlayer.TryGetValue(playerId, out int n) ? n : 0;

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
            s_CollectedByPlayer.Clear(); // per round
            m_CoinSound = Resources.Load<AudioClip>(CoinSoundPath);
            if (m_CoinSound == null) Debug.LogWarning($"[Pickups] Couldn't find Resources/{CoinSoundPath}.mp3 - pickups will be silent.", this);

            MatchLayout.Built += HandleMapBuilt;
            TryRegister();
        }

        public override void OnNetworkDespawn()
        {
            MatchLayout.Built -= HandleMapBuilt;
        }

        public override void Render()
        {
            ApplyCollectedBits();
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (!m_Registered) TryRegister();
            if (m_Pickups.Count == 0) return;

            CheckLocalPlayerTouch();
        }

        private void LateUpdate()
        {
            if (m_Pickups.Count == 0) return;

            // Every pickup turns in step, one full turn every secondsPerSpin.
            float angle = Mathf.Repeat(Time.time / Mathf.Max(0.1f, secondsPerSpin), 1f) * 360f;
            Quaternion spin = Quaternion.Euler(0f, angle, 0f);
            foreach (Pickup pickup in m_Pickups)
            {
                if (pickup != null && !pickup.Collected) pickup.SetSpin(spin);
            }
        }

        #endregion

        #region Finding the pickups

        private void HandleMapBuilt(MatchLayout layout) => TryRegister();

        private void TryRegister()
        {
            if (m_Registered) return;

            var markers = new List<Transform>();
            MatchLayout layout = MatchLayout.Current;
            if (layout != null)
            {
                if (!layout.IsBuilt) return; // wait for the map and challenges
                Pickup.FindMarkers(layout.transform, markers);
            }
            else
            {
                // A map placed straight into the scene.
                for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject root in scene.GetRootGameObjects()) Pickup.FindMarkers(root.transform, markers);
                }
            }

            m_Registered = true;
            if (markers.Count == 0) return;

            if (!MatchRules.Get(null).PickupsEnabled)
            {
                Debug.Log($"[Pickups] Pickups are off for this game - {markers.Count} pickup marker(s) left empty.", this);
                return;
            }

            if (markers.Count > MaxPickups)
            {
                Debug.LogWarning($"[Pickups] This map has {markers.Count} pickups; only the first {MaxPickups} are used.", this);
                markers.RemoveRange(MaxPickups, markers.Count - MaxPickups);
            }

            Physics.SyncTransforms(); // the challenges were only just moved into place
            for (int i = 0; i < markers.Count; i++)
            {
                Pickup pickup = markers[i].GetComponent<Pickup>();
                if (pickup == null) pickup = markers[i].gameObject.AddComponent<Pickup>();
                pickup.Init(i);
                m_Pickups.Add(pickup);
            }

            Debug.Log($"[Pickups] {m_Pickups.Count} pickup(s), {pointsPerPickup} points each.", this);
            m_AppliedCount = -1;
            ApplyCollectedBits();
        }

        #endregion

        #region Collecting

        private void CheckLocalPlayerTouch()
        {
            CorePlayerManager local = NetworkPlayers.Local;
            if (local == null || !local.IsSpawned) return;
            if (local.PlayerState != null && local.PlayerState.LifeState == PlayerLifeState.Eliminated) return;
            if (!ChallengeManager.IsRoundOpenFor(NetworkPlayers.LocalClientId)) return;

            if (m_LocalControllerOwner != local)
            {
                m_LocalControllerOwner = local;
                m_LocalController = local.GetComponent<CharacterController>();
            }

            // The player's body as an upright capsule-ish column.
            float bottom, top, bodyRadius;
            Vector3 axis;
            if (m_LocalController != null && m_LocalController.enabled)
            {
                Bounds b = m_LocalController.bounds;
                bottom = b.min.y;
                top = b.max.y;
                axis = b.center;
                bodyRadius = Mathf.Max(b.extents.x, b.extents.z);
            }
            else
            {
                axis = local.transform.position;
                bottom = axis.y;
                top = axis.y + 1.8f;
                bodyRadius = 0.35f;
            }

            float reach = pickupRadius + bodyRadius;
            float reachSq = reach * reach;
            foreach (Pickup pickup in m_Pickups)
            {
                if (pickup == null || pickup.Collected) continue;

                Vector3 c = pickup.Center;
                if (c.y + pickup.HalfHeight < bottom || c.y - pickup.HalfHeight > top) continue;
                float dx = c.x - axis.x;
                float dz = c.z - axis.z;
                if (dx * dx + dz * dz > reachSq) continue;

                // Feels instant: gone and "ding" now; the master decides the points.
                pickup.Hide(true);
                AudioVolumeService.PlayOneShot(m_CoinSound, AudioCategory.SoundEffects, c);
                CollectRpc(pickup.Index);
            }
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void CollectRpc(int index, RpcInfo info = default)
        {
            ulong player = NetworkPlayers.ToClientId(info.Source);
            if (player == 0) player = NetworkPlayers.ToClientId(Runner.LocalPlayer);

            if (index < 0 || index >= MaxPickups || IsBitSet(index)) return; // someone beat them to it
            if (!ChallengeManager.IsRoundOpenFor(player)) return;

            SetBit(index);
            NetCollectedCount++;

            if (NetworkPlayers.TryGetComponent(player, out PlayerScore score)) score.AddScore(pointsPerPickup);
            PickupCollectedRpc(index, (int)player);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void PickupCollectedRpc(int index, int playerId)
        {
            s_CollectedByPlayer[playerId] = CollectedBy(playerId) + 1;
            if (index >= 0 && index < m_Pickups.Count && m_Pickups[index] != null) m_Pickups[index].Hide(true);
            PickupCollected?.Invoke((ulong)playerId, index);
        }

        /// <summary>Hides everything the networked bit set says is taken (also catches up late joiners).</summary>
        private void ApplyCollectedBits()
        {
            if (!IsSpawned || !m_Registered || m_Pickups.Count == 0) return;

            int count = NetCollectedCount;
            if (count == m_AppliedCount) return;
            bool firstTime = m_AppliedCount < 0;
            m_AppliedCount = count;

            for (int i = 0; i < m_Pickups.Count; i++)
            {
                if (m_Pickups[i] != null && !m_Pickups[i].Collected && IsBitSet(i)) m_Pickups[i].Hide(!firstTime);
            }
        }

        private bool IsBitSet(int index) => (NetCollected[index >> 5] & (1 << (index & 31))) != 0;

        private void SetBit(int index)
        {
            int word = index >> 5;
            NetCollected.Set(word, NetCollected[word] | (1 << (index & 31)));
        }

        #endregion
    }
}
