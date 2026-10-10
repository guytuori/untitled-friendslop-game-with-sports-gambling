using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Builds the match's map: loads the chosen map prefab, then fills each of its holes (challenge16 /
    /// challenge32 entities - see <see cref="ChallengeSockets"/>) with a randomly chosen challenge of the
    /// right size, so the fixed parts of a map are always the same but the challenges change every match.
    ///
    /// Networking (Photon Fusion, Shared mode): a scene singleton owned by the session's master client, like
    /// RoundTimer. Nothing it creates is networked - the map and challenges have no networked components -
    /// so instead of spawning them through Fusion, the master client decides the layout once and writes it
    /// to networked state (the map's id, and one challenge index per hole), and every client, late joiners
    /// included, builds the identical layout locally from that. Everything is parented under this object
    /// with deterministic names, so ChallengeManager's hierarchy-based zone indices match on every client.
    ///
    /// Which map: the session's <see cref="HostGameRulesData.MapPrefab"/> (set from the Host Game screen),
    /// or <see cref="devMapPrefab"/> when the scene is played directly. Map ids are the prefab file names in
    /// Assets/Core/Prefabs/Maps (see <see cref="MapCatalog"/>); challenges come from
    /// Assets/Core/Prefabs/Challenges (see <see cref="ChallengeCatalog"/>).
    ///
    /// GameManager waits for <see cref="IsBuilt"/> before spawning the local player, so nobody spawns before
    /// the map (and its spawn points) exist. Added to [BB] Core by Friendslop > Refresh Everything From Assets.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class MatchLayout : CoreNetworkBehaviour, ISceneSingleton
    {
        #region Fields & Properties

        /// <summary>Most holes a map can have (networked array capacity).</summary>
        public const int MaxSockets = 128;

        /// <summary>A hole's pick meaning "the plain Practice Map piece" (ChallengeCatalog.GetPracticePiece) instead of a challenge.</summary>
        public const int PracticePiecePick = -2;

        [Tooltip("Map prefab (file name in Assets/Core/Prefabs/Maps) used when this scene is played directly instead of through Host Game.")]
        [SerializeField] private string devMapPrefab = "LastStop";

        /// <summary>The MatchLayout in the current gameplay scene, if any.</summary>
        public static MatchLayout Current { get; private set; }

        /// <summary>Raised on every client once the map and its challenges have been built.</summary>
        public static event Action<MatchLayout> Built;

        [Networked] private NetworkBool NetLayoutReady { get; set; }
        [Networked] private NetworkString<_64> NetMapPrefab { get; set; }
        [Networked] private int NetSocketCount { get; set; }
        [Networked, Capacity(MaxSockets)] private NetworkArray<int> NetPicks => default;

        private GameObject m_MapInstance;
        private string m_MapInstanceId;
        private Material m_DefaultSkybox;
        private bool m_DefaultSkyboxSaved;
        private bool m_Built;

        /// <summary>True once this client has built the map and challenges (or there was nothing to build).</summary>
        public bool IsBuilt => m_Built;

        /// <summary>The map instance, once built (null if the map couldn't be found).</summary>
        public GameObject MapInstance => m_MapInstance;

        #endregion

        #region Unity & Network Lifecycle

        private void Awake()
        {
            Current = this;
        }

        private void OnDestroy()
        {
            if (Current == this) Current = null;
        }

        public override void OnNetworkSpawn()
        {
            TryBuild();
        }

        public override void FixedUpdateNetwork()
        {
            // Also covers a master client that left before choosing: the next one picks it up here.
            if (IsOwner && !NetLayoutReady)
            {
                ChooseLayout();
            }
        }

        public override void Render()
        {
            TryBuild();
        }

        #endregion

        #region Choosing (master client)

        private void ChooseLayout()
        {
            string mapId = ResolveMapId();
            GameObject map = EnsureMapInstance(mapId);

            List<ChallengeSockets.Socket> sockets = map != null ? ChallengeSockets.FindSockets(map.transform) : new List<ChallengeSockets.Socket>();
            if (sockets.Count > MaxSockets)
            {
                Debug.LogError($"[MatchLayout] Map '{mapId}' has {sockets.Count} holes - only the first {MaxSockets} get challenges.", this);
            }

            int[] picks;
            if (PracticeMode.IsActive)
            {
                // Practice Map is about the map itself: every hole gets the plain piece, no challenges.
                picks = new int[sockets.Count];
                for (int i = 0; i < picks.Length; i++) picks[i] = PracticePiecePick;
            }
            else
            {
                picks = PickChallenges(sockets, ChallengeCatalog.Load(), new System.Random());
            }
            int count = Mathf.Min(picks.Length, MaxSockets);
            for (int i = 0; i < count; i++) NetPicks.Set(i, picks[i]);

            NetSocketCount = count;
            NetMapPrefab = map != null ? mapId : "";
            NetLayoutReady = true;

            Debug.Log($"[MatchLayout] Chose map '{mapId}' with {count} challenge hole(s).", this);
        }

        private string ResolveMapId()
        {
            if (FusionSessionService.HasInstance && FusionSessionService.Instance.InGame && !FusionSessionService.Instance.IsDevSession)
            {
                string fromRules = FusionSessionService.Instance.SessionRules.MapPrefab;
                if (!string.IsNullOrWhiteSpace(fromRules)) return fromRules.Trim();
            }
            return devMapPrefab;
        }

        /// <summary>
        /// One challenge catalog index per socket (-1 = leave the hole empty): each socket gets a random
        /// challenge of its size, without repeats until every challenge of that size has been used once.
        /// Shared with the editor's preview menu.
        /// </summary>
        public static int[] PickChallenges(IReadOnlyList<ChallengeSockets.Socket> sockets, ChallengeCatalog catalog, System.Random random)
        {
            var picks = new int[sockets.Count];
            var bags = new Dictionary<int, List<int>>();

            for (int i = 0; i < sockets.Count; i++)
            {
                int size = sockets[i].Size;
                if (catalog == null)
                {
                    picks[i] = -1;
                    continue;
                }

                if (!bags.TryGetValue(size, out List<int> bag) || bag.Count == 0)
                {
                    bag = catalog.IndicesForSize(size);
                    bags[size] = bag;
                }

                if (bag.Count == 0)
                {
                    picks[i] = -1; // no challenges of this size yet
                    continue;
                }

                int slot = random.Next(bag.Count);
                picks[i] = bag[slot];
                bag.RemoveAt(slot);
            }
            return picks;
        }

        #endregion

        #region Building (every client)

        private void TryBuild()
        {
            if (m_Built || !IsSpawned || !NetLayoutReady) return;
            Build();
        }

        private void Build()
        {
            m_Built = true;

            string mapId = NetMapPrefab.ToString();
            GameObject map = string.IsNullOrEmpty(mapId) ? null : EnsureMapInstance(mapId);
            if (map == null)
            {
                Debug.LogWarning($"[MatchLayout] No map to build (map id '{mapId}').", this);
                Built?.Invoke(this);
                return;
            }

            PlayerEndPoint.SetUpAll(map.transform);

            List<ChallengeSockets.Socket> sockets = ChallengeSockets.FindSockets(map.transform);
            int count = NetSocketCount;
            if (sockets.Count != count)
            {
                Debug.LogWarning($"[MatchLayout] Map '{mapId}' has {sockets.Count} holes here but the host chose for {count} - is everyone on the same build?", this);
            }

            ChallengeCatalog catalog = ChallengeCatalog.Load();
            var challengesRoot = new GameObject("Challenges").transform;
            challengesRoot.SetParent(transform, false);

            int placed = 0;
            for (int i = 0; i < Mathf.Min(count, sockets.Count); i++)
            {
                GameObject prefab;
                string id;
                if (NetPicks[i] == PracticePiecePick)
                {
                    prefab = catalog != null ? catalog.GetPracticePiece(sockets[i].Size) : null;
                    id = prefab != null ? prefab.name : "";
                    if (prefab == null)
                    {
                        Debug.LogWarning($"[MatchLayout] No practice piece for {sockets[i].Size}x{sockets[i].Size} holes - add Challenge{sockets[i].Size}.prefab to Assets/Core/Prefabs/Challenges and rebuild the lists.", this);
                        continue;
                    }
                }
                else
                {
                    ChallengeCatalog.Entry entry = catalog != null ? catalog.Get(NetPicks[i]) : null;
                    if (entry == null || entry.prefab == null) continue;
                    prefab = entry.prefab;
                    id = entry.id;
                }

                string instanceName = $"{i:D3}_{id}";
                GameObject placedInstance = ChallengeSockets.Place(prefab, sockets[i], challengesRoot, instanceName);
                if (placedInstance == null) continue;
                placed++;

                // The plain practice pieces come straight from a glb, without the collision a real
                // challenge's set-up gives it - make sure they can be stood on.
                if (NetPicks[i] == PracticePiecePick) AddMissingColliders(placedInstance);
            }

            // Zones are indexed by hierarchy - make ChallengeManager re-scan now that the challenges exist.
            if (ChallengeManager.Instance != null) ChallengeManager.Instance.RefreshZones();

            Debug.Log($"[MatchLayout] Built map '{mapId}' with {placed} challenge(s) in {sockets.Count} hole(s).", this);
            Built?.Invoke(this);
        }

        private static void AddMissingColliders(GameObject root)
        {
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || filter.GetComponent<Collider>() != null) continue;
                filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
            }
        }

        /// <summary>Instantiates the map prefab (once) under this object, at the position saved in the prefab.</summary>
        private GameObject EnsureMapInstance(string mapId)
        {
            if (m_MapInstance != null && string.Equals(m_MapInstanceId, mapId, StringComparison.OrdinalIgnoreCase)) return m_MapInstance;

            if (m_MapInstance != null) Destroy(m_MapInstance);
            m_MapInstance = null;
            m_MapInstanceId = null;

            MapCatalog catalog = MapCatalog.Load();
            MapCatalog.Entry entry = catalog != null ? catalog.Find(mapId) : null;
            if (entry == null || entry.prefab == null)
            {
                Debug.LogError(catalog == null
                    ? "[MatchLayout] There's no MapCatalog yet - run Friendslop > Maps > Rebuild Map And Challenge Lists."
                    : $"[MatchLayout] No map '{mapId}' in the MapCatalog (prefab file names in Assets/Core/Prefabs/Maps).", this);
                return null;
            }

            Transform source = entry.prefab.transform;
            m_MapInstance = Instantiate(entry.prefab, source.position, source.rotation, transform);
            m_MapInstance.name = "Map_" + entry.id;
            m_MapInstanceId = mapId;
            ApplySkybox(entry);
            return m_MapInstance;
        }

        /// <summary>
        /// Switches the sky to the map's own (MapCatalog entry, from its <c>map_name_skybox</c> image), or back to
        /// the scene's default sky if the map hasn't got one.
        /// </summary>
        private void ApplySkybox(MapCatalog.Entry entry)
        {
            if (!m_DefaultSkyboxSaved)
            {
                m_DefaultSkybox = RenderSettings.skybox;
                m_DefaultSkyboxSaved = true;
            }

            Material sky = entry != null && entry.skybox != null ? entry.skybox : m_DefaultSkybox;
            if (RenderSettings.skybox == sky) return;

            RenderSettings.skybox = sky;
            DynamicGI.UpdateEnvironment();
        }

        #endregion
    }
}
