using System;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// This machine's player's item slot - one item at a time. Added at runtime to the local player's avatar
    /// by NetworkPlayers.Register (no prefab setup); drawn by ItemHUD.
    ///
    ///   Empty -> (touches an item box) Rolling: the slot-machine spin, while the master client picks the item
    ///   -> Holding: shown on the HUD with the Use Item key -> (Use Item pressed)
    ///      single-use items: Fading (it flashes and fades away) -> Empty
    ///      timed items: Active, the timer counts down over the item -> Empty when it runs out.
    ///
    /// The slot is busy (and item boxes are passed through without being opened) from the moment a box is
    /// touched until the item is used up, so nobody can take a box they couldn't use.
    ///
    /// An item bought in the between-rounds shop (MatchProgress) is already in the slot when the round starts.
    /// Using an item calls <see cref="ItemEffects.Used"/> - the effects themselves aren't written yet.
    /// </summary>
    public class LocalItemSlot : MonoBehaviour
    {
        public enum SlotState
        {
            Empty,
            Rolling,
            Holding,
            Active,
            Fading,
        }

        /// <summary>Shortest slot-machine spin, from touching the box to the item landing.</summary>
        public const float MinRollSeconds = 1.0f;

        /// <summary>How long the reel takes to slow down and land once the item is known.</summary>
        public const float SettleSeconds = 0.65f;

        /// <summary>A single-use item's flash-and-fade after it's used.</summary>
        public const float FadeSeconds = 0.7f;

        /// <summary>No answer from the master by then (someone else got the box first): the slot empties again.</summary>
        private const float RequestTimeoutSeconds = 2.5f;

        /// <summary>The local player's slot, if spawned.</summary>
        public static LocalItemSlot Local { get; private set; }

        /// <summary>Raised when the slot changes state (the HUD listens for flourishes).</summary>
        public static event Action<LocalItemSlot> StateChanged;

        private CorePlayerManager m_Player;
        private InputBindingsData m_Bindings;

        public SlotState State { get; private set; } = SlotState.Empty;

        /// <summary>The item rolled, held, running or fading (None while the reel is still waiting for it).</summary>
        public ItemId Item { get; private set; }

        public ItemDefinition Definition => ItemCatalog.Get(Item);

        /// <summary>Time.time when the current state began.</summary>
        public float StateStartTime { get; private set; }

        /// <summary>Rolling: Time.time the item became known (-1 while still waiting).</summary>
        public float ItemKnownTime { get; private set; } = -1f;

        /// <summary>Active: Time.time the timer runs out.</summary>
        public float ActiveEndTime { get; private set; }

        /// <summary>The item came from the shop (no slot-machine spin, just a pop-in).</summary>
        public bool FromShop { get; private set; }

        /// <summary>Free to open an item box.</summary>
        public bool CanTakeBox => State == SlotState.Empty;

        /// <summary>Seconds left on an active timed item.</summary>
        public float ActiveSecondsLeft => State == SlotState.Active ? Mathf.Max(0f, ActiveEndTime - Time.time) : 0f;

        public static void Attach(CorePlayerManager player)
        {
            if (player == null || !player.IsOwner) return;
            if (player.GetComponent<LocalItemSlot>() != null) return;
            var slot = player.gameObject.AddComponent<LocalItemSlot>();
            slot.m_Player = player;
        }

        private void Start()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();
            Local = this;
            m_Bindings = InputBindingsStore.Load();
            InputBindingsStore.Saved += ReloadBindings;

            // Bought between rounds: ready to go from the start.
            ItemId bought = MatchProgress.TakeShopItem();
            if (bought != ItemId.None && ItemCatalog.Get(bought) != null)
            {
                FromShop = true;
                Item = bought;
                SetState(SlotState.Holding);
                Debug.Log($"[Items] Starting the round with {ItemCatalog.NameOf(bought)} (bought between rounds).", this);
            }
        }

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
            if (State == SlotState.Active) ItemEffects.Expired(Definition, m_Player);
            if (Local == this) Local = null;
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        // =====================================================================================
        // Item boxes (called by ItemManager)
        // =====================================================================================

        /// <summary>This player just ran into a box: start spinning while the master decides.</summary>
        internal void BeginRoll()
        {
            if (State != SlotState.Empty) return;
            FromShop = false;
            Item = ItemId.None;
            ItemKnownTime = -1f;
            SetState(SlotState.Rolling);
        }

        /// <summary>The master gave this player <paramref name="item"/> from a box.</summary>
        internal void ReceiveFromBox(ItemId item)
        {
            if (item == ItemId.None) return;

            if (State == SlotState.Empty)
            {
                // The answer beat the touch (or the touch was on another machine's frame) - spin anyway.
                BeginRoll();
            }
            if (State != SlotState.Rolling || Item != ItemId.None) return; // already busy with something else

            Item = item;
            ItemKnownTime = Time.time;
            StateChanged?.Invoke(this);
        }

        /// <summary>
        /// Practice item menu: puts <paramref name="item"/> straight into the slot, ready to use (no spin). Whatever
        /// was there is dropped - a timed item still running is ended first.
        /// </summary>
        public void GiveNow(ItemId item)
        {
            if (item == ItemId.None || ItemCatalog.Get(item) == null) return;
            if (State == SlotState.Active) ItemEffects.Expired(Definition, m_Player);

            FromShop = true; // the HUD's "Ready: X" pop-in rather than a spin
            Item = item;
            ItemKnownTime = Time.time;
            SetState(SlotState.Holding);
            Debug.Log($"[Items] Practice item menu: {ItemCatalog.NameOf(item)} put in the slot.", this);
        }

        /// <summary>When the spin lands: at least <see cref="MinRollSeconds"/> after the touch, and a settle after the item is known.</summary>
        public float RollLandTime => ItemKnownTime < 0f ? float.MaxValue : Mathf.Max(StateStartTime + MinRollSeconds, ItemKnownTime + SettleSeconds);

        // =====================================================================================
        // Update
        // =====================================================================================

        private void Update()
        {
            float now = Time.time;
            switch (State)
            {
                case SlotState.Rolling:
                    if (Item == ItemId.None && now - StateStartTime > RequestTimeoutSeconds)
                    {
                        SetState(SlotState.Empty); // someone else got that box
                    }
                    else if (Item != ItemId.None && now >= RollLandTime)
                    {
                        SetState(SlotState.Holding);
                        Debug.Log($"[Items] Got {ItemCatalog.NameOf(Item)} from an item box.", this);
                    }
                    break;

                case SlotState.Holding:
                    if (UsePressed() && CanUseNow()) Use();
                    break;

                case SlotState.Active:
                    if (now >= ActiveEndTime)
                    {
                        ItemEffects.Expired(Definition, m_Player);
                        Clear();
                    }
                    break;

                case SlotState.Fading:
                    if (now - StateStartTime >= FadeSeconds) Clear();
                    break;
            }
        }

        private bool UsePressed() =>
            m_Bindings != null && BoundControls.WasPressed(m_Bindings.Keyboard.UseItem, m_Bindings.Gamepad.UseItem);

        private bool CanUseNow()
        {
            if (InGameMenu.IsOpen || CoreInputHandler.GameplayInputBlocked) return false;
            if (m_Player == null || !m_Player.IsSpawned) return false;
            if (m_Player.PlayerState != null && m_Player.PlayerState.LifeState == PlayerLifeState.Eliminated) return false;
            return ChallengeManager.IsRoundOpenFor(m_Player.OwnerClientId);
        }

        /// <summary>Uses the held item now.</summary>
        public void Use()
        {
            if (State != SlotState.Holding) return;
            ItemDefinition item = Definition;
            ItemEffects.Used(item, m_Player);

            if (item != null && item.IsTimed)
            {
                ActiveEndTime = Time.time + item.durationSeconds;
                SetState(SlotState.Active);
            }
            else
            {
                SetState(SlotState.Fading);
            }
        }

        private void Clear()
        {
            Item = ItemId.None;
            ItemKnownTime = -1f;
            FromShop = false;
            SetState(SlotState.Empty);
        }

        private void SetState(SlotState state)
        {
            State = state;
            StateStartTime = Time.time;
            StateChanged?.Invoke(this);
        }
    }
}
