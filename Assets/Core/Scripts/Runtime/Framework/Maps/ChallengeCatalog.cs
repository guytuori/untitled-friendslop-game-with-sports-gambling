using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Every challenge that can be dropped into a map's holes. Built automatically in the editor by
    /// MatchCatalogBuilder from the prefabs in Assets/Core/Prefabs/Challenges (any prefab with a
    /// challenge16/challenge32 corner marker - see <see cref="ChallengeSockets"/>) and stored at
    /// Resources/ChallengeCatalog - don't edit it by hand.
    ///
    /// The order is fixed (sorted by id) and identical on every client of the same build, so an index into
    /// <see cref="Challenges"/> is what <see cref="MatchLayout"/> sends over the network.
    /// </summary>
    public class ChallengeCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("The challenge's id - its prefab file name.")]
            public string id;

            [Tooltip("16 or 32: the hole size it fits, from its corner marker's name.")]
            public int size;

            public GameObject prefab;
        }

        public const string ResourcePath = "ChallengeCatalog";

        [SerializeField] private List<Entry> challenges = new List<Entry>();

        private static ChallengeCatalog s_Loaded;

        public IReadOnlyList<Entry> Challenges => challenges;

        /// <summary>The catalog, or null if it hasn't been built yet.</summary>
        public static ChallengeCatalog Load()
        {
            if (s_Loaded == null) s_Loaded = Resources.Load<ChallengeCatalog>(ResourcePath);
            return s_Loaded;
        }

        /// <summary>The entry at <paramref name="index"/>, or null if out of range.</summary>
        public Entry Get(int index) => index >= 0 && index < challenges.Count ? challenges[index] : null;

        /// <summary>Indices of every challenge that fits a hole of this size.</summary>
        public List<int> IndicesForSize(int size)
        {
            var result = new List<int>();
            for (int i = 0; i < challenges.Count; i++)
            {
                if (challenges[i] != null && challenges[i].prefab != null && challenges[i].size == size) result.Add(i);
            }
            return result;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by MatchCatalogBuilder.</summary>
        public void SetChallenges(List<Entry> entries)
        {
            challenges = entries;
        }
#endif
    }
}
