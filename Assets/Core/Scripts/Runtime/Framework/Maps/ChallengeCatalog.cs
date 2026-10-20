using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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

            [Tooltip("A view of the challenge for the Practice Challenges list - rendered by MatchCatalogBuilder.")]
            public Texture2D thumbnail;
        }

        public const string ResourcePath = "ChallengeCatalog";

        [SerializeField] private List<Entry> challenges = new List<Entry>();

        [Tooltip("Practice Map fills every 16x16 hole with this (the Challenge16 prefab - no underscore, so not in the rotation).")]
        [SerializeField] private GameObject practice16;

        [Tooltip("Practice Map fills every 32x32 hole with this (the Challenge32 prefab).")]
        [SerializeField] private GameObject practice32;

        [Header("Start / end flags (see ChallengeCourse)")]
        [Tooltip("Challenge16StartFlag: shown over a 16x16 challenge's start row until the local player has completed it.")]
        [SerializeField] private GameObject startFlag16;
        [Tooltip("Challenge16EndFlag: shown over a 16x16 challenge's finish row while the local player is in it.")]
        [SerializeField] private GameObject endFlag16;
        [Tooltip("Challenge32StartFlag.")]
        [SerializeField] private GameObject startFlag32;
        [Tooltip("Challenge32EndFlag.")]
        [SerializeField] private GameObject endFlag32;
        [Tooltip("The see-through material the flags are drawn with (its Base Color alpha is the flags' opacity; each flag texture gets a copy). Made once by MatchCatalogBuilder, then left alone so it can be tuned.")]
        [SerializeField] private Material flagMaterial;

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

        /// <summary>The plain piece Practice Map puts in a hole of this size (null if there isn't one).</summary>
        public GameObject GetPracticePiece(int size) => size == 16 ? practice16 : size == 32 ? practice32 : null;

        /// <summary>The start and end flag prefabs for a challenge of this size (either may be null).</summary>
        public void GetFlags(int size, out GameObject startFlag, out GameObject endFlag)
        {
            startFlag = size == 32 ? startFlag32 : size == 16 ? startFlag16 : null;
            endFlag = size == 32 ? endFlag32 : size == 16 ? endFlag16 : null;
        }

        /// <summary>The see-through material the flags are drawn with (null if the lists haven't been rebuilt since it was added).</summary>
        public Material FlagMaterial => flagMaterial;

        /// <summary>The index of the challenge with this id (its prefab file name), or -1.</summary>
        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < challenges.Count; i++)
            {
                if (challenges[i] != null && string.Equals(challenges[i].id, id, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static readonly Regex NumberedId = new Regex(@"_(\d+)$", RegexOptions.CultureInvariant);

        /// <summary>
        /// The challenge's number among those of its size: the number at the end of its id
        /// ("Challenge_16_03" is 3), or failing that its position in the list (1-based).
        /// </summary>
        public int NumberOf(Entry entry)
        {
            if (entry == null) return 0;
            Match match = NumberedId.Match(entry.id ?? "");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int number) && number > 0) return number;

            int position = 0;
            foreach (Entry e in challenges)
            {
                if (e == null || e.size != entry.size) continue;
                position++;
                if (e == entry) return position;
            }
            return position;
        }

        /// <summary>"Challenge 16.3": size.number (see <see cref="NumberOf"/>).</summary>
        public string DisplayName(Entry entry) => entry == null ? "" : Loc.F("Challenge {0}.{1}", entry.size, NumberOf(entry));

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

        /// <summary>Editor only - used by MatchCatalogBuilder.</summary>
        public void SetPracticePieces(GameObject piece16, GameObject piece32)
        {
            practice16 = piece16;
            practice32 = piece32;
        }

        /// <summary>Editor only - used by MatchCatalogBuilder.</summary>
        public void SetFlags(GameObject start16, GameObject end16, GameObject start32, GameObject end32, Material material)
        {
            startFlag16 = start16;
            endFlag16 = end16;
            startFlag32 = start32;
            endFlag32 = end32;
            flagMaterial = material;
        }
#endif
    }
}
