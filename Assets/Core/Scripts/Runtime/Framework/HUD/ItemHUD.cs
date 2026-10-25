using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// This player's item slot on the HUD (bottom right, left of the wager phone), drawn from
    /// <see cref="LocalItemSlot"/>. CoreHUD adds it at runtime when items are on.
    ///
    ///   - No item: nothing shown.
    ///   - Item box opened: a slot-machine reel spins through the items (rainbow frame), slows down and lands
    ///     on the one rolled - with a punch, a white flash, a burst of sparkles and the item's name popping up
    ///     above the slot.
    ///   - Holding: the item's picture (its name on a tile in its colour, until there are icons) with the Use
    ///     Item key in a badge on the top-left corner.
    ///   - Timed item running: a shadow fills the box from the top as time runs out (the item still shows
    ///     through it) and the seconds left count down over it, turning red for the last five.
    ///   - Single-use item used: it flashes, swells and fades away.
    /// </summary>
    public class ItemHUD : MonoBehaviour
    {
        private const float SlotWidth = 176f;
        private const float SlotHeight = 150f;
        private const float Border = 4f;
        private const float Inset = 7f;
        private const float ViewHeight = SlotHeight - 2f * Border - 2f * Inset;
        private const float BesidePhoneRight = 250f; // the wager phone is 220 wide, 16 from the edge
        private const float Bottom = 16f;

        private const float ReelSpeed = 15f;       // tiles per second while spinning
        private const float HideSeconds = 0.35f;
        private const float PunchSeconds = 0.4f;
        private const float FlashSeconds = 0.3f;
        private const float BannerSeconds = 1.8f;
        private const int SparkleCount = 18;
        private const float SparkleLife = 0.65f;

        private class Sparkle
        {
            public VisualElement Element;
            public Vector2 Position;
            public Vector2 Velocity;
            public float Born;
            public float Spin;
        }

        private VisualElement m_Root;
        private VisualElement m_Frame;
        private VisualElement m_Viewport;
        private readonly VisualElement[] m_ReelCells = new VisualElement[3];
        private readonly VisualElement[] m_ReelTiles = new VisualElement[3];
        private VisualElement m_Shadow;
        private Label m_Countdown;
        private VisualElement m_Flash;
        private VisualElement m_KeyBadge;
        private Label m_KeyLabel;
        private VisualElement m_SparkleLayer;
        private Label m_Banner;
        private VisualElement m_BannerHolder;

        private readonly List<ItemDefinition> m_ReelItems = new List<ItemDefinition>();
        private readonly List<Sparkle> m_Sparkles = new List<Sparkle>();
        private readonly System.Random m_Rng = new System.Random();

        private LocalItemSlot.SlotState m_LastState = LocalItemSlot.SlotState.Empty;
        private ItemId m_LastItem = ItemId.None;
        private float m_RollStart;
        private bool m_Settling;
        private float m_SettleStart;
        private float m_SettleFrom;
        private int m_StopIndex;
        private ItemDefinition m_StopItem;
        private float m_HideStart = -10f;
        private float m_PunchStart = -10f;
        private float m_FlashStart = -10f;
        private float m_BannerStart = -10f;
        private InputBindingsData m_Bindings;
        private ItemDefinition m_DisplayItem;
        private bool m_HideFromFade;

        public void Initialize(VisualElement root, bool besidePhone)
        {
            m_Bindings = InputBindingsStore.Load();
            InputBindingsStore.Saved += ReloadBindings;
            Build(root, besidePhone ? BesidePhoneRight : 16f);
        }

        private void ReloadBindings() => m_Bindings = InputBindingsStore.Load();

        private void OnDestroy()
        {
            InputBindingsStore.Saved -= ReloadBindings;
            m_Root?.RemoveFromHierarchy();
            m_BannerHolder?.RemoveFromHierarchy();
        }

        // =====================================================================================
        // Layout
        // =====================================================================================

        private void Build(VisualElement parent, float right)
        {
            m_Root = new VisualElement { name = "item-slot", pickingMode = PickingMode.Ignore };
            m_Root.style.position = Position.Absolute;
            m_Root.style.right = right;
            m_Root.style.bottom = Bottom;
            m_Root.style.width = SlotWidth;
            m_Root.style.height = SlotHeight;
            m_Root.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            m_Root.style.display = DisplayStyle.None;
            parent.Add(m_Root);

            m_Frame = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Frame.style.position = Position.Absolute;
            m_Frame.style.left = m_Frame.style.top = m_Frame.style.right = m_Frame.style.bottom = 0;
            m_Frame.style.backgroundColor = new Color(0.03f, 0.03f, 0.08f, 0.85f);
            ItemTile.SetRadius(m_Frame, 12);
            ItemTile.SetBorder(m_Frame, Border, Color.white);
            m_Root.Add(m_Frame);

            m_Viewport = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Viewport.style.position = Position.Absolute;
            m_Viewport.style.left = Inset;
            m_Viewport.style.right = Inset;
            m_Viewport.style.top = Inset;
            m_Viewport.style.height = ViewHeight;
            m_Viewport.style.overflow = Overflow.Hidden;
            ItemTile.SetRadius(m_Viewport, 6);
            m_Frame.Add(m_Viewport);

            for (int i = 0; i < m_ReelCells.Length; i++)
            {
                var cell = new VisualElement { pickingMode = PickingMode.Ignore };
                cell.style.position = Position.Absolute;
                cell.style.left = 0;
                cell.style.right = 0;
                cell.style.height = ViewHeight;
                cell.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
                m_ReelCells[i] = cell;
                m_ReelTiles[i] = ItemTile.Make(22);
                cell.Add(m_ReelTiles[i]);
                m_Viewport.Add(cell);
            }

            m_Shadow = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Shadow.style.position = Position.Absolute;
            m_Shadow.style.left = 0;
            m_Shadow.style.right = 0;
            m_Shadow.style.top = 0;
            m_Shadow.style.height = 0;
            m_Shadow.style.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
            m_Viewport.Add(m_Shadow);

            m_Countdown = new Label { pickingMode = PickingMode.Ignore };
            m_Countdown.style.position = Position.Absolute;
            m_Countdown.style.left = m_Countdown.style.top = m_Countdown.style.right = m_Countdown.style.bottom = 0;
            m_Countdown.style.fontSize = 50;
            m_Countdown.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_Countdown.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Countdown.style.color = Color.white;
            m_Countdown.style.unityTextOutlineWidth = 2.5f;
            m_Countdown.style.unityTextOutlineColor = Color.black;
            m_Countdown.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
            m_Viewport.Add(m_Countdown);

            m_Flash = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Flash.style.position = Position.Absolute;
            m_Flash.style.left = m_Flash.style.top = m_Flash.style.right = m_Flash.style.bottom = 0;
            m_Flash.style.backgroundColor = Color.white;
            m_Flash.style.opacity = 0f;
            ItemTile.SetRadius(m_Flash, 12);
            m_Root.Add(m_Flash);

            m_SparkleLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            m_SparkleLayer.style.position = Position.Absolute;
            m_SparkleLayer.style.left = m_SparkleLayer.style.top = m_SparkleLayer.style.right = m_SparkleLayer.style.bottom = 0;
            m_SparkleLayer.style.overflow = Overflow.Visible;
            m_Root.Add(m_SparkleLayer);

            m_KeyBadge = new VisualElement { pickingMode = PickingMode.Ignore };
            m_KeyBadge.style.position = Position.Absolute;
            m_KeyBadge.style.left = -13;
            m_KeyBadge.style.top = -13;
            m_KeyBadge.style.minWidth = 36;
            m_KeyBadge.style.height = 36;
            m_KeyBadge.style.paddingLeft = 4;
            m_KeyBadge.style.paddingRight = 4;
            m_KeyBadge.style.alignItems = Align.Center;
            m_KeyBadge.style.justifyContent = Justify.Center;
            m_KeyBadge.style.backgroundColor = Color.white;
            ItemTile.SetRadius(m_KeyBadge, 18);
            ItemTile.SetBorder(m_KeyBadge, 2.5f, Color.black);
            m_KeyLabel = new Label { pickingMode = PickingMode.Ignore };
            m_KeyLabel.style.color = Color.black;
            m_KeyLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_KeyLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_KeyBadge.Add(m_KeyLabel);
            m_Root.Add(m_KeyBadge);

            // The "Ready: X" / "X!" toast sits in the middle of the screen (a full-screen holder centres it), so long
            // item names have room and never clip off the edge.
            m_BannerHolder = new VisualElement { pickingMode = PickingMode.Ignore };
            m_BannerHolder.style.position = Position.Absolute;
            m_BannerHolder.style.left = m_BannerHolder.style.right = m_BannerHolder.style.top = m_BannerHolder.style.bottom = 0;
            m_BannerHolder.style.alignItems = Align.Center;
            m_BannerHolder.style.justifyContent = Justify.Center;
            parent.Add(m_BannerHolder);

            m_Banner = new Label { pickingMode = PickingMode.Ignore };
            m_Banner.style.maxWidth = Length.Percent(90);
            m_Banner.style.fontSize = 24;
            m_Banner.style.unityFontStyleAndWeight = FontStyle.Bold;
            m_Banner.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Banner.style.whiteSpace = WhiteSpace.Normal;
            m_Banner.style.color = new Color(1f, 0.86f, 0.3f);
            m_Banner.style.unityTextOutlineWidth = 2f;
            m_Banner.style.unityTextOutlineColor = Color.black;
            m_Banner.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(100));
            m_Banner.style.display = DisplayStyle.None;
            m_BannerHolder.Add(m_Banner);

            GameFont.Apply(m_Root);
            GameFont.Apply(m_Banner);
        }

        // =====================================================================================
        // Update
        // =====================================================================================

        private void Update()
        {
            if (m_Root == null) return;
            float now = Time.time;

            LocalItemSlot slot = LocalItemSlot.Local;
            LocalItemSlot.SlotState state = slot != null ? slot.State : LocalItemSlot.SlotState.Empty;
            ItemId item = slot != null ? slot.Item : ItemId.None;

            if (state != m_LastState || item != m_LastItem) HandleChange(slot, m_LastState, state, now);
            m_LastState = state;
            m_LastItem = item;

            bool visible = state != LocalItemSlot.SlotState.Empty || now - m_HideStart < HideSeconds;
            m_Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

            if (visible)
            {
                UpdateReel(slot, state, now);
                UpdateTimer(slot, state, now);
                UpdateFrame(slot, state, now);
                UpdateKeyBadge(state);
            }

            UpdateEffects(now);
        }

        private void HandleChange(LocalItemSlot slot, LocalItemSlot.SlotState from, LocalItemSlot.SlotState to, float now)
        {
            switch (to)
            {
                case LocalItemSlot.SlotState.Rolling:
                    if (from != LocalItemSlot.SlotState.Rolling)
                    {
                        m_RollStart = now;
                        m_Settling = false;
                        m_StopItem = null;
                        m_DisplayItem = null;
                        FillReelItems();
                        Burst(8, 0.6f);
                    }
                    break;

                case LocalItemSlot.SlotState.Holding:
                    // Landed (or bought between rounds): ta-da.
                    m_PunchStart = now;
                    m_FlashStart = now;
                    Burst(SparkleCount, 1f);
                    ShowBanner(slot.FromShop ? Loc.F("Ready: {0}", NameUpper(slot.Definition)) : NameUpper(slot.Definition) + "!", now);
                    break;

                case LocalItemSlot.SlotState.Active:
                case LocalItemSlot.SlotState.Fading:
                    if (from == LocalItemSlot.SlotState.Holding)
                    {
                        m_FlashStart = now;
                        Burst(12, 0.8f);
                    }
                    break;

                case LocalItemSlot.SlotState.Empty:
                    if (from != LocalItemSlot.SlotState.Empty) m_HideStart = now;
                    m_HideFromFade = from == LocalItemSlot.SlotState.Fading;
                    break;
            }
        }

        private static string NameUpper(ItemDefinition item) => item != null ? (item.displayName ?? item.id.ToString()).ToUpperInvariant() : "";

        // ----- the reel -----

        private void FillReelItems()
        {
            m_ReelItems.Clear();
            foreach (ItemDefinition item in ItemCatalog.Items)
            {
                if (item != null && item.id != ItemId.None && item.boxChance > 0f) m_ReelItems.Add(item);
            }
            if (m_ReelItems.Count == 0)
            {
                foreach (ItemDefinition item in ItemCatalog.Items) if (item != null && item.id != ItemId.None) m_ReelItems.Add(item);
            }
            // Shuffle, so the reel isn't the same every time.
            for (int i = m_ReelItems.Count - 1; i > 0; i--)
            {
                int j = m_Rng.Next(i + 1);
                (m_ReelItems[i], m_ReelItems[j]) = (m_ReelItems[j], m_ReelItems[i]);
            }
        }

        private ItemDefinition ReelItemAt(int k)
        {
            if (m_StopItem != null && k == m_StopIndex) return m_StopItem;
            if (m_ReelItems.Count == 0) return null;
            int n = m_ReelItems.Count;
            return m_ReelItems[((k % n) + n) % n];
        }

        private float ReelPosition(LocalItemSlot slot, float now)
        {
            float spun = (now - m_RollStart) * ReelSpeed;
            if (slot == null || slot.Item == ItemId.None) return spun;

            if (!m_Settling)
            {
                float settleStart = slot.RollLandTime - LocalItemSlot.SettleSeconds;
                if (now < settleStart) return spun;

                // Pick where it stops: a bit ahead, so it slows down smoothly onto the rolled item.
                m_Settling = true;
                m_SettleStart = now;
                m_SettleFrom = spun;
                m_StopIndex = Mathf.CeilToInt(spun + ReelSpeed * LocalItemSlot.SettleSeconds * 0.5f);
                m_StopItem = slot.Definition;
            }

            float u = Mathf.Clamp01((now - m_SettleStart) / LocalItemSlot.SettleSeconds);
            float eased = 1f - (1f - u) * (1f - u) * (1f - u);
            return Mathf.Lerp(m_SettleFrom, m_StopIndex, eased);
        }

        private void UpdateReel(LocalItemSlot slot, LocalItemSlot.SlotState state, float now)
        {
            if (state == LocalItemSlot.SlotState.Rolling)
            {
                float p = ReelPosition(slot, now);
                int k0 = Mathf.FloorToInt(p);
                for (int i = 0; i < m_ReelCells.Length; i++)
                {
                    int k = k0 + i - 1;
                    m_ReelCells[i].style.display = DisplayStyle.Flex;
                    m_ReelCells[i].style.top = (p - k) * ViewHeight;
                    m_ReelCells[i].style.opacity = m_Settling ? 1f : 0.85f;
                    m_ReelCells[i].style.scale = new Scale(Vector2.one);
                    ItemTile.Show(m_ReelTiles[i], ReelItemAt(k));
                }
                return;
            }

            // One still picture of the item.
            ItemDefinition item = slot != null ? slot.Definition : null;
            if (item != null) m_DisplayItem = item;
            else item = m_DisplayItem;
            bool hideAll = state == LocalItemSlot.SlotState.Empty && m_HideFromFade; // already faded away
            for (int i = 0; i < m_ReelCells.Length; i++)
            {
                bool middle = i == 1 && !hideAll;
                m_ReelCells[i].style.display = middle ? DisplayStyle.Flex : DisplayStyle.None;
                if (!middle) continue;
                m_ReelCells[i].style.top = 0;
                ItemTile.Show(m_ReelTiles[i], item);

                float scale = 1f, opacity = 1f;
                if (state == LocalItemSlot.SlotState.Fading)
                {
                    float u = Mathf.Clamp01((now - slot.StateStartTime) / LocalItemSlot.FadeSeconds);
                    scale = 1f + 0.6f * u;
                    opacity = 1f - u;
                }
                m_ReelCells[i].style.scale = new Scale(new Vector2(scale, scale));
                m_ReelCells[i].style.opacity = opacity;
            }
        }

        // ----- timer -----

        private void UpdateTimer(LocalItemSlot slot, LocalItemSlot.SlotState state, float now)
        {
            if (state != LocalItemSlot.SlotState.Active || slot == null || slot.Definition == null)
            {
                m_Shadow.style.height = 0;
                m_Countdown.text = "";
                return;
            }

            float duration = Mathf.Max(0.01f, slot.Definition.durationSeconds);
            float left = slot.ActiveSecondsLeft;
            float used = Mathf.Clamp01(1f - left / duration);
            m_Shadow.style.height = used * ViewHeight;

            m_Countdown.text = Mathf.CeilToInt(left) + "s";
            bool urgent = left <= 5f;
            m_Countdown.style.color = urgent ? new Color(1f, 0.3f, 0.25f) : Color.white;
            float pulse = urgent ? 1f + 0.12f * Mathf.Abs(Mathf.Sin(now * Mathf.PI * 2f)) : 1f;
            m_Countdown.style.scale = new Scale(new Vector2(pulse, pulse));
        }

        // ----- frame, badge, punch, hide -----

        private void UpdateFrame(LocalItemSlot slot, LocalItemSlot.SlotState state, float now)
        {
            Color border;
            ItemDefinition item = slot != null ? slot.Definition : null;
            switch (state)
            {
                case LocalItemSlot.SlotState.Rolling:
                    border = Color.HSVToRGB(Mathf.Repeat(now * 2.5f, 1f), 0.85f, 1f);
                    break;
                case LocalItemSlot.SlotState.Holding:
                    Color c = item != null ? item.color : Color.white;
                    border = Color.Lerp(Color.white, Color.Lerp(c, Color.white, 0.2f), 0.5f + 0.5f * Mathf.Sin(now * 4f));
                    break;
                case LocalItemSlot.SlotState.Active:
                    border = item != null ? Color.Lerp(item.color, Color.white, 0.3f) : Color.white;
                    break;
                default:
                    border = Color.white;
                    break;
            }
            ItemTile.SetBorder(m_Frame, Border, border);

            float scale = 1f;
            float punch = (now - m_PunchStart) / PunchSeconds;
            if (punch >= 0f && punch < 1f) scale = 1f + 0.35f * Mathf.Exp(-5f * punch) * Mathf.Cos(punch * Mathf.PI * 3f);
            if (state == LocalItemSlot.SlotState.Rolling) scale *= 1f + 0.03f * Mathf.Sin(now * 40f); // rattle

            float opacity = 1f;
            if (state == LocalItemSlot.SlotState.Empty)
            {
                float u = Mathf.Clamp01((now - m_HideStart) / HideSeconds);
                opacity = 1f - u;
                scale *= 1f - 0.2f * u;
            }
            m_Root.style.scale = new Scale(new Vector2(scale, scale));
            m_Root.style.opacity = opacity;
        }

        private void UpdateKeyBadge(LocalItemSlot.SlotState state)
        {
            bool show = state == LocalItemSlot.SlotState.Holding || state == LocalItemSlot.SlotState.Active;
            m_KeyBadge.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;

            m_KeyBadge.style.opacity = state == LocalItemSlot.SlotState.Holding ? 1f : 0.45f;
            string key = "";
            if (m_Bindings != null)
            {
                key = UsingGamepad() ? ControlNames.Gamepad(m_Bindings.Gamepad.UseItem) : ControlNames.Keyboard(m_Bindings.Keyboard.UseItem);
            }
            if (m_KeyLabel.text != key) m_KeyLabel.text = key;
            m_KeyLabel.style.fontSize = key != null && key.Length <= 2 ? 18 : 12;
        }

        private static bool UsingGamepad()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;
            Keyboard keyboard = Keyboard.current;
            return keyboard == null || pad.lastUpdateTime > keyboard.lastUpdateTime;
        }

        // ----- flash, sparkles, banner -----

        private void UpdateEffects(float now)
        {
            float flash = (now - m_FlashStart) / FlashSeconds;
            m_Flash.style.opacity = flash >= 0f && flash < 1f ? 0.9f * (1f - flash) : 0f;

            for (int i = m_Sparkles.Count - 1; i >= 0; i--)
            {
                Sparkle s = m_Sparkles[i];
                float age = now - s.Born;
                if (age >= SparkleLife)
                {
                    s.Element.RemoveFromHierarchy();
                    m_Sparkles.RemoveAt(i);
                    continue;
                }
                float dt = Time.deltaTime;
                s.Velocity += new Vector2(0f, 420f) * dt; // a little gravity
                s.Position += s.Velocity * dt;
                float u = age / SparkleLife;
                s.Element.style.left = s.Position.x;
                s.Element.style.top = s.Position.y;
                s.Element.style.opacity = 1f - u;
                float size = 1f - 0.6f * u;
                s.Element.style.scale = new Scale(new Vector2(size, size));
                s.Element.style.rotate = new Rotate(new Angle(45f + s.Spin * age));
            }

            if (m_Banner.style.display == DisplayStyle.Flex)
            {
                float u = (now - m_BannerStart) / BannerSeconds;
                if (u >= 1f)
                {
                    m_Banner.style.display = DisplayStyle.None;
                }
                else
                {
                    float pop = u < 0.12f ? Mathf.Lerp(0.4f, 1.15f, u / 0.12f) : u < 0.2f ? Mathf.Lerp(1.15f, 1f, (u - 0.12f) / 0.08f) : 1f;
                    m_Banner.style.scale = new Scale(new Vector2(pop, pop));
                    m_Banner.style.opacity = u > 0.75f ? 1f - (u - 0.75f) / 0.25f : 1f;
                    m_Banner.style.translate = new Translate(0, -18f * u);
                    m_Banner.style.color = Color.Lerp(new Color(1f, 0.86f, 0.3f), Color.white, 0.5f + 0.5f * Mathf.Sin(now * 18f));
                }
            }
        }

        private void ShowBanner(string text, float now)
        {
            if (string.IsNullOrEmpty(text)) return;
            m_Banner.text = text;
            m_BannerStart = now;
            m_Banner.style.display = DisplayStyle.Flex;
        }

        private void Burst(int count, float power)
        {
            Vector2 centre = new Vector2(SlotWidth * 0.5f, SlotHeight * 0.5f);
            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * Mathf.PI * 2f + (float)m_Rng.NextDouble() * 0.4f;
                float speed = Mathf.Lerp(220f, 380f, (float)m_Rng.NextDouble()) * power;
                float size = Mathf.Lerp(8f, 14f, (float)m_Rng.NextDouble());

                var e = new VisualElement { pickingMode = PickingMode.Ignore };
                e.style.position = Position.Absolute;
                e.style.width = size;
                e.style.height = size;
                e.style.marginLeft = -size * 0.5f;
                e.style.marginTop = -size * 0.5f;
                e.style.backgroundColor = Color.HSVToRGB((float)m_Rng.NextDouble(), 0.6f, 1f);
                ItemTile.SetRadius(e, 2);
                m_SparkleLayer.Add(e);

                m_Sparkles.Add(new Sparkle
                {
                    Element = e,
                    Position = centre,
                    Velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * speed,
                    Born = Time.time,
                    Spin = Mathf.Lerp(-540f, 540f, (float)m_Rng.NextDouble()),
                });
            }
        }
    }
}
