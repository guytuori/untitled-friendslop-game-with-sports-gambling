using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The animated map tiles: every tile texture named <c>something_1</c> that has a <c>something_2</c> next
    /// to it (water_1 / water_2). <see cref="AnimatedTiles"/> flips any map material showing the _1 texture
    /// to the _2 one and back. Only an underscore counts - helipad1 / helipad2 are ordinary tiles.
    ///
    /// Built automatically in the editor by AnimatedTileCatalogBuilder from the textures under Assets/Maps and
    /// stored at Resources/AnimatedTileCatalog so the game can load it - don't edit it by hand.
    /// </summary>
    public class AnimatedTileCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("The tile's name without the _1 / _2, e.g. \"water\".")]
            public string id;

            public Texture first;
            public Texture second;
        }

        public const string ResourcePath = "AnimatedTileCatalog";

        [SerializeField] private List<Entry> tiles = new List<Entry>();

        private Dictionary<Texture, Texture> m_ByTexture;
        private Dictionary<string, Texture> m_ByName;

        public IReadOnlyList<Entry> Tiles => tiles;

        /// <summary>The catalog, or null if it hasn't been built yet.</summary>
        public static AnimatedTileCatalog Load() => Resources.Load<AnimatedTileCatalog>(ResourcePath);

        /// <summary>
        /// The second frame for a tile's first frame (the texture itself, or a copy of it with the same name),
        /// or false if this texture isn't an animated tile's _1.
        /// </summary>
        public bool TryGetSecondFrame(Texture first, out Texture second)
        {
            second = null;
            if (first == null) return false;

            if (m_ByTexture == null)
            {
                m_ByTexture = new Dictionary<Texture, Texture>();
                m_ByName = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry entry in tiles)
                {
                    if (entry == null || entry.first == null || entry.second == null) continue;
                    m_ByTexture[entry.first] = entry.second;
                    if (!string.IsNullOrEmpty(entry.id)) m_ByName[entry.id] = entry.second;
                }
            }

            if (m_ByTexture.TryGetValue(first, out second)) return true;
            return TryGetTileId(first.name, out string id) && m_ByName.TryGetValue(id, out second);
        }

        /// <summary>"water_1" (or "assets/textures/tiles/water_1.png") -> "water". False for anything not ending in exactly "_1".</summary>
        public static bool TryGetTileId(string textureName, out string id)
        {
            id = null;
            if (string.IsNullOrEmpty(textureName)) return false;

            string name = textureName.Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);

            if (name.Length < 3 || !name.EndsWith("_1", StringComparison.Ordinal)) return false;
            id = name.Substring(0, name.Length - 2);
            return true;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by AnimatedTileCatalogBuilder.</summary>
        public void SetTiles(List<Entry> entries)
        {
            tiles = entries;
            m_ByTexture = null;
            m_ByName = null;
        }
#endif
    }
}
