using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Every selectable player character, with its thumbnail. Built automatically in the editor by
    /// CharacterCatalogBuilder from the models in the characters folder (see that class), and stored at
    /// Resources/CharacterCatalog so the game can load it - don't edit it by hand.
    /// </summary>
    public class CharacterCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("Stable id saved in profiles - the model's file name.")]
            public string id;

            [Tooltip("Name shown on the Change Profile screen (the file name for now).")]
            public string displayName;

            [Tooltip("The Humanoid model (FBX or prefab) the player's avatar switches to.")]
            public GameObject model;

            [Tooltip("Front view of the model in its T-pose.")]
            public Texture2D thumbnail;
        }

        public const string ResourcePath = "CharacterCatalog";

        [SerializeField] private List<Entry> characters = new List<Entry>();

        private static CharacterCatalog s_Loaded;

        public IReadOnlyList<Entry> Characters => characters;

        /// <summary>The catalog, or null if it hasn't been built yet.</summary>
        public static CharacterCatalog Load()
        {
            if (s_Loaded == null) s_Loaded = Resources.Load<CharacterCatalog>(ResourcePath);
            return s_Loaded;
        }

        /// <summary>The entry with this id, or null.</summary>
        public Entry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (Entry entry in characters)
            {
                if (entry != null && entry.id == id) return entry;
            }
            return null;
        }

        public int IndexOf(string id)
        {
            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i] != null && characters[i].id == id) return i;
            }
            return -1;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by CharacterCatalogBuilder.</summary>
        public void SetCharacters(List<Entry> entries)
        {
            characters = entries;
        }
#endif
    }
}
