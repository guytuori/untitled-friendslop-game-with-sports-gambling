using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Mario Kart-style item boxes: every <c>itembox</c> entity in the map and its challenges becomes a
    /// spinning, bobbing <see cref="ItemBox"/>. A player with an empty item slot who runs through one gets a
    /// random item (rolled by the item box chances in <see cref="ItemCatalog"/>); a player who already has an
    /// item (held or still running) passes straight through without opening it. A taken box is gone for
    /// everyone and comes back <see cref="ItemCatalog.BoxRespawnSeconds"/> (60 s) later, so camping a box
    /// isn't worth it.
    ///
    /// Respects the Items rule from the Host Game screen - with Items Off the markers stay empty. Never in
    /// Practice Challenges.
    ///
    /// Networking (Photon Fusion, Shared mode): a master-client scene singleton like PickupManager. Every
    /// machine finds the same markers in the same (hierarchy) order, so a box is just an index. The player's
    /// own machine notices the touch (a distance check, no colliders), hides the box, starts the slot-machine
    /// spin straight away and asks the master. The master gives the box to whoever asked first: it sets the
    /// box's bit in a networked bit set (so everyone - late joiners too - hides it), rolls the item and tells
    /// everyone who got what. The master also brings boxes back when their minute is up.
    ///
    /// Added to [BB] Core by Friendslop > Refresh Everything From Assets.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class ItemManager : CoreNetworkBehaviour, ISceneSingleton
    {
        private const int BitWords = 8;

        /// <summary>Most item boxes a map (with its challenges) can have.</summary>
        public const int MaxBoxes = BitWords * 32;

        [Tooltip("How close (sideways, in meters) the player's body has to get to a box's middle to open it.")]
        [SerializeField] private float touchRadius = 0.9f;

        [Tooltip("Seconds this player's own touch keeps a box hidden while waiting for the master's answer.")]
        [SerializeField] private float pendingSeconds = 2.5f;

        public static ItemManager Instance { get; private set; }

        /// <summary>Raised on every machine when someone opens a box (player id, box index, item).</summary>
        public static event Action<ulong, int, ItemId> ItemBoxOpened;

        [Networked, Capacity(BitWords)] private NetworkArray<int> NetTaken => default;

        private readonly List<ItemBox> m_Boxes = new List<ItemBox>();
        private readonly Dictionary<int, float> m_RespawnAt = new Dictionary<int, float>(); // master only, Time.time
        private readonly System.Random m_Rng = new System.Random();
        private bool m_Registered;
        private bool m_FirstApply = true;
        private CharacterController m_LocalController;
        private CorePlayerManager m_LocalControllerOwner;

        /// <summary>Item boxes in this map (0 until it's built, or with Items off).</summary>
        public int BoxCount => m_Boxes.Count;

        /// <summary>Items are in play this game (the Items rule, and not Practice Challenges).</summary>
        public static bool ItemsEnabled => !PracticeMode.IsActive && MatchRules.Get(null).ItemsEnabled;

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
            MatchLayout.Built += HandleMapBuilt;
            TryRegister();
        }

        public override void OnNetworkDespawn()
        {
            MatchLayout.Built -= HandleMapBuilt;
        }

        public override void Render()
        {
            ApplyTakenBits();
        }

        private void Update()
        {
            if (!IsSpawned) return;
            if (!m_Registered) TryRegister();
            if (m_Boxes.Count == 0) return;

            if (IsOwner) RespawnDueBoxes();
            CheckLocalPlayerTouch();
        }

        #endregion

        #region Finding the boxes

        private void HandleMapBuilt(MatchLayout layout) => TryRegister();

        private void TryRegister()
        {
            if (m_Registered) return;

            var markers = new List<Transform>();
            MatchLayout layout = MatchLayout.Current;
            if (layout != null)
            {
                if (!layout.IsBuilt) return; // wait for the map and challenges
                ItemBox.FindMarkers(layout.transform, markers);
            }
            else
            {
                // A map placed straight into the scene.
                for (int s = 0; s < UnityEngine.SceneManagement.SceneManager.sceneCount; s++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject root in scene.GetRootGameObjects()) ItemBox.FindMarkers(root.transform, markers);
                }
            }

            m_Registered = true;
            if (markers.Count == 0) return;

            if (!ItemsEnabled)
            {
                Debug.Log($"[Items] Items are off for this game - {markers.Count} item box marker(s) left empty.", this);
                return;
            }

            if (markers.Count > MaxBoxes)
            {
                Debug.LogWarning($"[Items] This map has {markers.Count} item boxes; only the first {MaxBoxes} are used.", this);
                markers.RemoveRange(MaxBoxes, markers.Count - MaxBoxes);
            }

            GameObject model = ItemCatalog.ItemBoxModel;
            if (model == null) Debug.LogWarning($"[Items] No item box model in the ItemCatalog ({ItemCatalog.DefaultItemBoxModelPath}) - using plain cubes.", this);

            Physics.SyncTransforms(); // the challenges were only just moved into place
            float size = ItemCatalog.BoxSize;
            for (int i = 0; i < markers.Count; i++)
            {
                ItemBox box = markers[i].GetComponent<ItemBox>();
                if (box == null) box = markers[i].gameObject.AddComponent<ItemBox>();
                box.Init(i, model, size);
                m_Boxes.Add(box);
            }

            Debug.Log($"[Items] {m_Boxes.Count} item box(es), back {ItemCatalog.BoxRespawnSeconds:0} s after being taken.", this);
            m_FirstApply = true;
            ApplyTakenBits();
        }

        #endregion

        #region Opening boxes

        private void CheckLocalPlayerTouch()
        {
            LocalItemSlot slot = LocalItemSlot.Local;
            if (slot == null || !slot.CanTakeBox) return; // holding an item: walk straight through

            CorePlayerManager local = NetworkPlayers.Local;
            if (local == null || !local.IsSpawned) return;
            if (local.PlayerState != null && local.PlayerState.LifeState == PlayerLifeState.Eliminated) return;
            if (!ChallengeManager.IsRoundOpenFor(NetworkPlayers.LocalClientId)) return;

            if (m_LocalControllerOwner != local)
            {
                m_LocalControllerOwner = local;
                m_LocalController = local.GetComponent<CharacterController>();
            }

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

            float reach = touchRadius + bodyRadius;
            float reachSq = reach * reach;
            foreach (ItemBox box in m_Boxes)
            {
                if (box == null || box.Hidden) continue;

                Vector3 c = box.Center;
                if (c.y + box.HalfHeight < bottom || c.y - box.HalfHeight > top) continue;
                float dx = c.x - axis.x;
                float dz = c.z - axis.z;
                if (dx * dx + dz * dz > reachSq) continue;

                // Feels instant: the box pops and the reel starts spinning now; the master picks the item.
                box.Hide(true);
                box.PendingUntil = Time.time + pendingSeconds;
                slot.BeginRoll();
                OpenBoxRpc(box.Index);
                return; // one box at a time
            }
        }

        [Rpc(RpcSources.All, RpcTargets.StateAuthority)]
        private void OpenBoxRpc(int index, RpcInfo info = default)
        {
            ulong player = NetworkPlayers.ToClientId(info.Source);
            if (player == 0) player = NetworkPlayers.ToClientId(Runner.LocalPlayer);

            if (index < 0 || index >= MaxBoxes || IsBitSet(index)) return; // someone beat them to it
            if (!ChallengeManager.IsRoundOpenFor(player)) return;

            ItemId item = ItemCatalog.RollBox(m_Rng);
            if (item == ItemId.None) return;

            SetBit(index, true);
            m_RespawnAt[index] = Time.time + ItemCatalog.BoxRespawnSeconds;
            BoxOpenedRpc(index, (int)player, (int)item);
        }

        [Rpc(RpcSources.StateAuthority, RpcTargets.All)]
        private void BoxOpenedRpc(int index, int playerId, int item)
        {
            if (index >= 0 && index < m_Boxes.Count && m_Boxes[index] != null) m_Boxes[index].Hide(true);

            if ((ulong)playerId == NetworkPlayers.LocalClientId && LocalItemSlot.Local != null)
            {
                LocalItemSlot.Local.ReceiveFromBox((ItemId)item);
            }
            ItemBoxOpened?.Invoke((ulong)playerId, index, (ItemId)item);
        }

        /// <summary>Master: boxes whose minute is up come back for everyone.</summary>
        private void RespawnDueBoxes()
        {
            float now = Time.time;
            for (int i = 0; i < m_Boxes.Count; i++)
            {
                if (!IsBitSet(i)) continue;
                if (!m_RespawnAt.TryGetValue(i, out float due))
                {
                    // Taken before this client became the master - give it a fresh minute.
                    m_RespawnAt[i] = now + ItemCatalog.BoxRespawnSeconds;
                    continue;
                }
                if (now < due) continue;
                m_RespawnAt.Remove(i);
                SetBit(i, false);
            }
        }

        /// <summary>Shows / hides every box to match the networked bit set (also catches up late joiners).</summary>
        private void ApplyTakenBits()
        {
            if (!IsSpawned || !m_Registered || m_Boxes.Count == 0) return;

            bool first = m_FirstApply;
            m_FirstApply = false;
            float now = Time.time;
            for (int i = 0; i < m_Boxes.Count; i++)
            {
                ItemBox box = m_Boxes[i];
                if (box == null) continue;
                bool taken = IsBitSet(i);
                if (taken)
                {
                    if (!box.Hidden) box.Hide(!first);
                }
                else if (box.Hidden && now >= box.PendingUntil)
                {
                    box.Show(!first);
                }
            }
        }

        private bool IsBitSet(int index) => (NetTaken[index >> 5] & (1 << (index & 31))) != 0;

        private void SetBit(int index, bool value)
        {
            int word = index >> 5;
            int mask = 1 << (index & 31);
            NetTaken.Set(word, value ? NetTaken[word] | mask : NetTaken[word] & ~mask);
        }

        #endregion
    }
}
