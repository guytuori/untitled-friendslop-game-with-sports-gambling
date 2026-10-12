using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The list of player characters - the one place that decides who's on the Change Profile screen, in
    /// what order, and which model each one plays as. Lives at Resources/CharacterCatalog so the game can
    /// load it; edit it by hand in the Inspector (Friendslop > Characters > Edit Character List selects it).
    ///
    /// Each entry only needs a model (a Humanoid FBX or prefab, from anywhere in the project). When the list
    /// is saved, CharacterCatalogBuilder (editor only) fills in whatever was left blank: the id from the
    /// model's file name, the display name from the id ("FastFoodGuy" -> "Fast Food Guy"), and a T-pose
    /// thumbnail. It also sets the model up as a Humanoid if it isn't one yet. Anything filled in by hand is
    /// kept.
    ///
    /// The id is what player profiles save, so don't change it once people have picked that character (they
    /// would fall back to the first character in the list).
    ///
    /// SPIDERS (2026-10-12): entries ticked <see cref="Entry.isSpider"/> (Arachne) follow the viewer's
    /// Settings > Gameplay spider options - see <see cref="ResolveForViewer"/>:
    ///   - Arachnophobia Mode: spiders aren't offered on Change Profile, and other players who picked one are
    ///     shown as <see cref="ArachnophobiaStandInId"/> (Girl) on this player's screen;
    ///   - Arachnophilia Mode: every player, this one included, is shown as the first spider in the list.
    /// </summary>
    public class CharacterCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Stable id saved in player profiles. Leave blank to use the model's file name. Don't change it after people have picked this character.")]
            public string id;

            [Tooltip("Name shown on the Change Profile screen. Leave blank to use the id with spaces (\"FastFoodGuy\" -> \"Fast Food Guy\").")]
            public string displayName;

            [Tooltip("The Humanoid model (FBX or prefab) the player's avatar switches to.")]
            public GameObject model;

            [Tooltip("Picture for the Change Profile screen. Leave blank and a front view of the model's T-pose is rendered automatically.")]
            public Texture2D thumbnail;

            [Tooltip("A spider. Hidden from (and swapped out for) players with Arachnophobia Mode on; with Arachnophilia Mode on, everyone becomes the first spider in the list.")]
            public bool isSpider;

            /// <summary>Has what the game needs to offer it (an id and a model).</summary>
            public bool IsUsable => !string.IsNullOrWhiteSpace(id) && model != null;

            public string DisplayNameOrId => string.IsNullOrWhiteSpace(displayName) ? id : displayName;
        }

        public const string ResourcePath = "CharacterCatalog";

        [Tooltip("Shown on the Change Profile screen in this order, 3 per row. The first one is the default for new players.")]
        [SerializeField] private List<Entry> characters = new List<Entry>();

        [Tooltip("The character a spider is shown as on the screen of a player with Arachnophobia Mode on.")]
        [SerializeField] private string arachnophobiaStandInId = "Girl";

        public string ArachnophobiaStandInId => arachnophobiaStandInId;

        private static CharacterCatalog s_Loaded;

        /// <summary>Every entry, exactly as edited (may include half-filled ones - see <see cref="GetSelectable"/>).</summary>
        public IReadOnlyList<Entry> Characters => characters;

        /// <summary>The catalog, or null if there isn't one.</summary>
        public static CharacterCatalog Load()
        {
            if (s_Loaded == null) s_Loaded = Resources.Load<CharacterCatalog>(ResourcePath);
            return s_Loaded;
        }

        /// <summary>The characters players can pick, in list order: entries with an id and a model, first of each id only.</summary>
        public List<Entry> GetSelectable()
        {
            var result = new List<Entry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in characters)
            {
                if (entry != null && entry.IsUsable && seen.Add(entry.id)) result.Add(entry);
            }
            return result;
        }

        /// <summary><see cref="GetSelectable()"/>, without the spiders when <paramref name="hideSpiders"/> (Arachnophobia Mode).</summary>
        public List<Entry> GetSelectable(bool hideSpiders)
        {
            List<Entry> result = GetSelectable();
            if (hideSpiders) result.RemoveAll(e => e.isSpider);
            return result;
        }

        /// <summary>
        /// The character this player should SEE for a player who picked <paramref name="pickedId"/> (empty =
        /// the default model), given this player's spider settings: Arachnophilia turns everyone into the first
        /// spider; Arachnophobia shows a spider as <see cref="ArachnophobiaStandInId"/>. Null means "the default
        /// model". Purely local - nothing about it is networked.
        /// </summary>
        public Entry ResolveForViewer(string pickedId, bool arachnophobia, bool arachnophilia)
        {
            if (arachnophilia)
            {
                foreach (Entry entry in characters)
                {
                    if (entry != null && entry.IsUsable && entry.isSpider) return entry;
                }
            }

            Entry picked = Find(pickedId);
            if (picked != null && picked.isSpider && arachnophobia)
            {
                Entry standIn = Find(arachnophobiaStandInId);
                if (standIn != null && !standIn.isSpider) return standIn;
                foreach (Entry entry in characters)
                {
                    if (entry != null && entry.IsUsable && !entry.isSpider) return entry;
                }
                return null;
            }
            return picked;
        }

        /// <summary>The usable entry with this id, or null.</summary>
        public Entry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (Entry entry in characters)
            {
                if (entry != null && entry.IsUsable && entry.id == id) return entry;
            }
            return null;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - the live list, for CharacterCatalogBuilder to fill in blanks.</summary>
        public List<Entry> EditableCharacters => characters;
#endif
    }
}
