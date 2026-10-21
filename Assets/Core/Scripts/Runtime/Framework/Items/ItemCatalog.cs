using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Every item in the game. The numbers are sent over the network and saved in the catalog asset, so never
    /// renumber or reuse one - add new items at the end.
    /// </summary>
    public enum ItemId
    {
        None = 0,
        DoubleJumpBoots = 1,
        TripleJumpBoots = 2,
        SpeedBoots = 3,
        SuperSpeedBoots = 4,
        SmallPoints = 5,
        MediumPoints = 6,
        BigPoints = 7,
        OneUp = 8,
        Teleport = 9,
        Jetpack = 10,
        SpringShoes = 11,
        Glider = 12,
        MagnetBoots = 13,
        StrongerArms = 14,
        QuadrupleJumpBoots = 15,
        SlipperyBoots = 16,
        GravityBoots = 17,
    }

    /// <summary>Single use (works straight away, then it's gone) or lasts a while (a timer counts down).</summary>
    public enum ItemKind
    {
        Instant = 0,
        Timed = 1,
    }

    /// <summary>One item's settings (an entry in <see cref="ItemCatalog"/>, edited in its Inspector).</summary>
    [Serializable]
    public class ItemDefinition
    {
        public ItemId id;
        public string displayName;
        [TextArea(1, 3)] public string description;

        public ItemKind kind;
        [Tooltip("Timed items: how long the effect lasts.")]
        public float durationSeconds;

        [Header("Item boxes")]
        [Tooltip("Chance (percent) of getting this from an item box. 0 = never from a box. The chances don't have to add up to 100 - they're relative.")]
        [Min(0f)] public float boxChance;

        [Header("Between-rounds shop")]
        public bool inShop = true;
        [Min(0)] public int shopCost = 200;
        [Tooltip("1 = basic, 2 = good, 3 = best. Which tiers a player is offered depends on how they placed last round (see ItemCatalog's shop settings).")]
        [Range(1, 3)] public int shopTier = 1;
        [Tooltip("Relative chance of being offered, among the items of the tiers on offer.")]
        [Min(0f)] public float shopWeight = 1f;

        [Header("Look")]
        [Tooltip("Optional. Until there are icons, the item is drawn as a coloured tile with its name.")]
        public Texture2D icon;
        public Color color = Color.gray;

        public bool IsTimed => kind == ItemKind.Timed && durationSeconds > 0f;

        public ItemDefinition Clone() => (ItemDefinition)MemberwiseClone();
    }

    /// <summary>
    /// The items (names, item box odds, durations, shop prices and tiers), the item box model and the box /
    /// shop tuning, all in one asset: Resources/ItemCatalog, made (and topped up with any new items) by
    /// ItemCatalogBuilder when the editor loads. Edit the numbers in its Inspector - the builder never
    /// overwrites them. If the asset is missing, the built-in defaults below are used.
    ///
    /// Item boxes roll by <see cref="ItemDefinition.boxChance"/>. The between-rounds shop offers three items
    /// from the tiers a player's placing allows (<see cref="TiersFor"/>): the bottom third of the table sees
    /// tiers 1-2, the middle all tiers, the top third tiers 2-3 - so the best items are off the table for
    /// players doing poorly and the leader doesn't get offered the basic ones.
    /// </summary>
    public class ItemCatalog : ScriptableObject
    {
        public const string ResourcePath = "ItemCatalog";
        public const string DefaultItemBoxModelPath = "Assets/Maps/item_box.glb";

        [Header("Item boxes")]
        [Tooltip("The item box model (Assets/Maps/item_box.glb).")]
        [SerializeField] private GameObject itemBoxModel;
        [Tooltip("Seconds before a taken item box comes back.")]
        [SerializeField, Min(1f)] private float boxRespawnSeconds = 60f;
        [Tooltip("How big the box is drawn (meters along its longest side).")]
        [SerializeField, Min(0.1f)] private float boxSize = 1.4f;

        [Header("Between-rounds shop")]
        [Tooltip("Placing at or below this (0 = last, 1 = first) is 'doing poorly': offered tiers 1 and 2 only.")]
        [SerializeField, Range(0f, 1f)] private float lowStandingMax = 0.34f;
        [Tooltip("Placing at or above this is 'doing well': offered tiers 2 and 3 only.")]
        [SerializeField, Range(0f, 1f)] private float highStandingMin = 0.67f;
        [Tooltip("How many items the shop offers.")]
        [SerializeField, Range(1, 5)] private int shopOfferCount = 3;

        [SerializeField] private List<ItemDefinition> items = new List<ItemDefinition>();

        private static ItemCatalog s_Loaded;
        private static bool s_Tried;
        private static List<ItemDefinition> s_Defaults;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Loaded = null;
            s_Tried = false;
        }

        /// <summary>The catalog asset, or null if it hasn't been built (then the defaults are used).</summary>
        public static ItemCatalog Instance
        {
            get
            {
                if (s_Loaded != null || s_Tried) return s_Loaded;
                s_Tried = true;
                s_Loaded = Resources.Load<ItemCatalog>(ResourcePath);
                if (s_Loaded == null) Debug.LogWarning($"[Items] No Resources/{ResourcePath} - using the built-in item defaults. It's made automatically when the editor loads.");
                return s_Loaded;
            }
        }

        public static IReadOnlyList<ItemDefinition> Items
        {
            get
            {
                ItemCatalog catalog = Instance;
                return catalog != null && catalog.items.Count > 0 ? catalog.items : Defaults;
            }
        }

        public static GameObject ItemBoxModel => Instance != null ? Instance.itemBoxModel : null;
        public static float BoxRespawnSeconds => Instance != null ? Instance.boxRespawnSeconds : 60f;
        public static float BoxSize => Instance != null ? Instance.boxSize : 1.4f;
        public static int ShopOfferCount => Instance != null ? Instance.shopOfferCount : 3;

        /// <summary>The item's settings, or null for <see cref="ItemId.None"/> / an unknown id.</summary>
        public static ItemDefinition Get(ItemId id)
        {
            if (id == ItemId.None) return null;
            foreach (ItemDefinition item in Items)
            {
                if (item != null && item.id == id) return item;
            }
            foreach (ItemDefinition item in Defaults)
            {
                if (item.id == id) return item;
            }
            return null;
        }

        public static string NameOf(ItemId id) => Get(id)?.displayName ?? id.ToString();

        /// <summary>A random item from an item box, by the box chances.</summary>
        public static ItemId RollBox(System.Random rng)
        {
            float total = 0f;
            foreach (ItemDefinition item in Items)
            {
                if (item != null && item.id != ItemId.None && item.boxChance > 0f) total += item.boxChance;
            }
            if (total <= 0f) return ItemId.None;

            double pick = rng.NextDouble() * total;
            foreach (ItemDefinition item in Items)
            {
                if (item == null || item.id == ItemId.None || item.boxChance <= 0f) continue;
                pick -= item.boxChance;
                if (pick < 0) return item.id;
            }
            return ItemId.None;
        }

        /// <summary>
        /// Which shop tiers a player is offered, from how they placed last round: <paramref name="standing"/>
        /// 1 = first, 0 = last (0.5 when they were the only player).
        /// </summary>
        public static bool[] TiersFor(float standing)
        {
            float low = Instance != null ? Instance.lowStandingMax : 0.34f;
            float high = Instance != null ? Instance.highStandingMin : 0.67f;
            var tiers = new bool[4]; // index 1..3
            if (standing <= low) { tiers[1] = true; tiers[2] = true; }
            else if (standing >= high) { tiers[2] = true; tiers[3] = true; }
            else { tiers[1] = true; tiers[2] = true; tiers[3] = true; }
            return tiers;
        }

        /// <summary>
        /// The shop's offer for a player: <see cref="ShopOfferCount"/> different items from the tiers their
        /// placing allows, picked by shop weight.
        /// </summary>
        public static List<ItemDefinition> RollShop(float standing, System.Random rng)
        {
            bool[] tiers = TiersFor(standing);
            var pool = new List<ItemDefinition>();
            foreach (ItemDefinition item in Items)
            {
                if (item == null || item.id == ItemId.None || !item.inShop || item.shopWeight <= 0f) continue;
                if (!tiers[Mathf.Clamp(item.shopTier, 1, 3)]) continue;
                pool.Add(item);
            }

            var offer = new List<ItemDefinition>();
            int count = ShopOfferCount;
            while (offer.Count < count && pool.Count > 0)
            {
                float total = 0f;
                foreach (ItemDefinition item in pool) total += item.shopWeight;
                double pick = rng.NextDouble() * total;
                int chosen = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    pick -= pool[i].shopWeight;
                    if (pick < 0) { chosen = i; break; }
                }
                offer.Add(pool[chosen]);
                pool.RemoveAt(chosen);
            }
            return offer;
        }

        // =====================================================================================
        // Defaults (also what ItemCatalogBuilder fills a new asset with)
        // =====================================================================================

        public static IReadOnlyList<ItemDefinition> Defaults
        {
            get
            {
                if (s_Defaults == null) s_Defaults = CreateDefaults();
                return s_Defaults;
            }
        }

        private static List<ItemDefinition> CreateDefaults()
        {
            // Shop costs are all 200 for now (to be balanced). Tiers: 1 basic, 2 good, 3 best.
            return new List<ItemDefinition>
            {
                Timed(ItemId.DoubleJumpBoots, "Double Jump Boots", "Double jump for 30 seconds.", 30f, 20f, 1, new Color(0.20f, 0.55f, 0.95f)),
                Timed(ItemId.TripleJumpBoots, "Triple Jump Boots", "Triple jump for 15 seconds.", 15f, 5f, 3, new Color(0.10f, 0.30f, 0.85f)),
                Timed(ItemId.SpeedBoots, "Speed Boots", "Run faster for 30 seconds.", 30f, 20f, 1, new Color(0.95f, 0.55f, 0.10f)),
                Timed(ItemId.SuperSpeedBoots, "Super Speed Boots", "Run a lot faster for 15 seconds.", 15f, 5f, 3, new Color(0.90f, 0.25f, 0.05f)),
                Instant(ItemId.SmallPoints, "Small Points", "Single use: gain 100 points straight away.", 20f, 1, new Color(0.45f, 0.75f, 0.30f)),
                Instant(ItemId.MediumPoints, "Medium Points", "Single use: gain 300 points straight away.", 10f, 2, new Color(0.25f, 0.65f, 0.20f)),
                Instant(ItemId.BigPoints, "Big Points", "Single use: gain 500 points straight away.", 5f, 3, new Color(0.95f, 0.78f, 0.15f)),
                Instant(ItemId.OneUp, "1-Up", "Single use: gain a life if lives are limited; otherwise your next death costs no points.", 5f, 2, new Color(0.15f, 0.70f, 0.35f)),
                Instant(ItemId.Teleport, "Teleport", "Single use: teleports you to a random challenge you haven't completed yet, or to the end if you've done them all.", 10f, 2, new Color(0.65f, 0.25f, 0.85f)),
                Timed(ItemId.Jetpack, "Jetpack", "You get a jetpack for 15 seconds.", 15f, 0f, 3, new Color(0.55f, 0.55f, 0.60f)),
                Timed(ItemId.SpringShoes, "Spring Shoes", "You get a high jump for 30 seconds.", 30f, 0f, 1, new Color(0.30f, 0.80f, 0.75f)),
                Timed(ItemId.Glider, "Glider", "You can glide after jumping for 30 seconds.", 30f, 0f, 2, new Color(0.45f, 0.75f, 0.95f)),
                Timed(ItemId.MagnetBoots, "Magnet Boots", "You ignore slippery floors for 30 seconds.", 30f, 0f, 1, new Color(0.75f, 0.20f, 0.25f)),
                Timed(ItemId.StrongerArms, "Stronger Arms", "More launching power from poles for 30 seconds.", 30f, 0f, 2, new Color(0.85f, 0.45f, 0.35f)),
                Timed(ItemId.QuadrupleJumpBoots, "Quadruple Jump Boots", "Quadruple jump for 15 seconds.", 15f, 0f, 3, new Color(0.05f, 0.15f, 0.60f)),
                Timed(ItemId.SlipperyBoots, "Slippery Boots", "Every floor is slippery for 30 seconds - but challenges and pickups are worth double.", 30f, 0f, 2, new Color(0.60f, 0.85f, 1.00f)),
                Timed(ItemId.GravityBoots, "Gravity Boots", "Triple gravity for 30 seconds - but challenges and pickups are worth double.", 30f, 0f, 2, new Color(0.35f, 0.25f, 0.50f)),
            };
        }

        private static ItemDefinition Timed(ItemId id, string name, string description, float seconds, float boxChance, int tier, Color color) =>
            new ItemDefinition { id = id, displayName = name, description = description, kind = ItemKind.Timed, durationSeconds = seconds, boxChance = boxChance, inShop = true, shopCost = 200, shopTier = tier, shopWeight = 1f, color = color };

        private static ItemDefinition Instant(ItemId id, string name, string description, float boxChance, int tier, Color color) =>
            new ItemDefinition { id = id, displayName = name, description = description, kind = ItemKind.Instant, durationSeconds = 0f, boxChance = boxChance, inShop = true, shopCost = 200, shopTier = tier, shopWeight = 1f, color = color };

#if UNITY_EDITOR
        /// <summary>Editor only (ItemCatalogBuilder): adds any item the asset doesn't have yet and fills a missing box model. True if anything changed.</summary>
        public bool EditorTopUp(GameObject boxModel)
        {
            bool changed = false;
            foreach (ItemDefinition def in Defaults)
            {
                if (items.Exists(i => i != null && i.id == def.id)) continue;
                items.Add(def.Clone());
                changed = true;
            }
            if (itemBoxModel == null && boxModel != null)
            {
                itemBoxModel = boxModel;
                changed = true;
            }
            return changed;
        }
#endif
    }
}
