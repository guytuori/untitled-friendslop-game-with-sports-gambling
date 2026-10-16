using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The announcer's voice clips: every audio file in Assets/Core/Audio/Voice, by file name without the
    /// extension ("321", "30seconds", "five", "time", ...). Played by <see cref="RoundAnnouncer"/> at the Voice
    /// volume (Audio Settings), not Sound Effects.
    ///
    /// Built automatically in the editor by VoiceCatalogBuilder and stored at Resources/VoiceCatalog so the game
    /// can load it - don't edit it by hand. Dropping a new clip into the Voice folder adds it.
    /// </summary>
    public class VoiceCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            [Tooltip("The clip's file name without the extension, e.g. \"30seconds\".")]
            public string id;
            public AudioClip clip;
        }

        public const string ResourcePath = "VoiceCatalog";

        [SerializeField] private List<Entry> clips = new List<Entry>();

        private Dictionary<string, AudioClip> m_ById;
        private static VoiceCatalog s_Loaded;
        private static bool s_Tried;

        public IReadOnlyList<Entry> Clips => clips;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Loaded = null;
            s_Tried = false;
        }

        /// <summary>The catalog, or null if it hasn't been built yet (warns once).</summary>
        public static VoiceCatalog Load()
        {
            if (s_Loaded != null || s_Tried) return s_Loaded;
            s_Tried = true;
            s_Loaded = Resources.Load<VoiceCatalog>(ResourcePath);
            if (s_Loaded == null)
            {
                Debug.LogWarning($"[Voice] No Resources/{ResourcePath} - the announcer will be silent. It's built automatically from Assets/Core/Audio/Voice (or Friendslop > Audio > Rebuild Voice Clips).");
            }
            return s_Loaded;
        }

        /// <summary>The clip called <paramref name="id"/> (case doesn't matter), or null.</summary>
        public static AudioClip Get(string id)
        {
            VoiceCatalog catalog = Load();
            return catalog != null ? catalog.Find(id) : null;
        }

        public AudioClip Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (m_ById == null)
            {
                m_ById = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry entry in clips)
                {
                    if (entry != null && entry.clip != null && !string.IsNullOrEmpty(entry.id)) m_ById[entry.id] = entry.clip;
                }
            }
            return m_ById.TryGetValue(id, out AudioClip clip) ? clip : null;
        }

#if UNITY_EDITOR
        public void SetClips(List<Entry> entries)
        {
            clips = entries ?? new List<Entry>();
            m_ById = null;
        }
#endif
    }
}
