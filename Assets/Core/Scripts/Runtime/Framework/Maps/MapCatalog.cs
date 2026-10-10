using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Every playable map prefab, by id (the prefab's file name, e.g. "LastStop" - the value the Host Game
    /// screen's map entries put in HostGameRulesData.MapPrefab). Built automatically in the editor by
    /// MatchCatalogBuilder from Assets/Core/Prefabs/Maps and stored at Resources/MapCatalog so the game can
    /// load it - don't edit it by hand.
    ///
    /// Note: this references every map prefab directly, so loading it loads every map. Fine with a couple of
    /// maps; once there are many big ones this should move to Addressables (load just the chosen map).
    /// </summary>
    public class MapCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("The map's id - its prefab file name.")]
            public string id;

            public GameObject prefab;

            [Tooltip("How many 16x16 holes (challenge16 entities) the map has. Informational.")]
            public int sockets16;

            [Tooltip("How many 32x32 holes (challenge32 entities) the map has. Informational.")]
            public int sockets32;

            [Tooltip("The map's sky (a Skybox material), or empty to keep [BB] Core's default sky. Filled in from a " +
                     "texture or material named <map>_skybox, e.g. dinosaur_zoo_skybox.png for DinosaurZoo.")]
            public Material skybox;
        }

        public const string ResourcePath = "MapCatalog";

        [SerializeField] private List<Entry> maps = new List<Entry>();

        private static MapCatalog s_Loaded;

        public IReadOnlyList<Entry> Maps => maps;

        /// <summary>The catalog, or null if it hasn't been built yet.</summary>
        public static MapCatalog Load()
        {
            if (s_Loaded == null) s_Loaded = Resources.Load<MapCatalog>(ResourcePath);
            return s_Loaded;
        }

        /// <summary>The entry with this id (case-insensitive), or null.</summary>
        public Entry Find(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            foreach (Entry entry in maps)
            {
                if (entry != null && string.Equals(entry.id, id, StringComparison.OrdinalIgnoreCase)) return entry;
            }
            return null;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by MatchCatalogBuilder.</summary>
        public void SetMaps(List<Entry> entries)
        {
            maps = entries;
        }
#endif
    }
}
