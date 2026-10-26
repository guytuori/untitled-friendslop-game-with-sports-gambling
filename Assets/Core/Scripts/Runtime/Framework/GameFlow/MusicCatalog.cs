using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The background music: every audio file in Assets/Core/Audio/Music, by file name without the extension
    /// ("Last_Stop", "Ready_to_Run", ...), with its loop point. Played by <see cref="MusicPlayer"/> at the Music
    /// volume (Audio Settings). Which track plays where is <see cref="MusicTracks"/>.
    ///
    /// Built automatically in the editor by MusicCatalogBuilder and stored at Resources/MusicCatalog - don't edit
    /// it by hand. Loop points come from music_info.txt in the Music folder ("loop point: 1.668s", or N/A for a
    /// track that plays once). Dropping a new song into the folder adds it.
    ///
    /// GAPLESS LOOPING: an MP3 file starts with ~25 ms of encoder silence and ends with some padding, which the
    /// encoder records in the file's LAME header. Unity doesn't trim them, so a plain loop would hiccup and
    /// every loop point would be ~25 ms early. The builder reads the header (<see cref="Entry.musicalSamples"/>,
    /// <see cref="Entry.encoderDelay"/>, <see cref="Entry.encoderPadding"/>) and <see cref="Entry.LeadSamples"/>
    /// works out, from the imported clip's actual length, where the music really starts, so loop points and the
    /// loop end are measured from the real music. WAV / OGG files have no padding and just work.
    /// </summary>
    public class MusicCatalog : ScriptableObject
    {
        /// <summary>Samples an MP3 decoder outputs before the encoder's own delay (the standard 528 + 1).</summary>
        public const int Mp3DecoderDelay = 529;

        [Serializable]
        public class Entry
        {
            [Tooltip("The song's file name without the extension, e.g. \"Last_Stop\".")]
            public string id;
            public AudioClip clip;

            [Tooltip("Off for a song that plays once and then leaves silence (loop point N/A).")]
            public bool loops = true;

            [Tooltip("Where the loop jumps back to, in seconds from the start of the music.")]
            public float loopStartSeconds;

            [Tooltip("Length of the actual music in samples (MP3: from the LAME header), 0 if unknown.")]
            public int musicalSamples;

            [Tooltip("The sample rate musicalSamples / encoderDelay / encoderPadding are in.")]
            public int sourceSampleRate;

            [Tooltip("MP3 encoder delay (silence at the start), from the LAME header.")]
            public int encoderDelay;

            [Tooltip("MP3 encoder padding (silence at the end), from the LAME header.")]
            public int encoderPadding;

            private float Scale => sourceSampleRate > 0 && clip != null ? clip.frequency / (float)sourceSampleRate : 1f;

            /// <summary>The music's length in the clip's own samples (the whole clip if unknown).</summary>
            public int MusicSamples(AudioClip forClip)
            {
                if (forClip == null) return 0;
                if (musicalSamples <= 0) return forClip.samples;
                return Mathf.Min(forClip.samples, Mathf.RoundToInt(musicalSamples * Scale));
            }

            /// <summary>
            /// How many samples of encoder silence the imported clip has before the music. The clip's extra length
            /// over the music is lead + trail, and the trail an MP3 decoder leaves is (padding - 529), so whether
            /// Unity decoded every frame (lead = delay + 529), skipped the decoder delay (lead = delay) or trimmed
            /// it all (lead = 0), this comes out right.
            /// </summary>
            public int LeadSamples(AudioClip forClip)
            {
                if (forClip == null || musicalSamples <= 0) return 0;
                int extra = forClip.samples - MusicSamples(forClip);
                if (extra <= 0) return 0;
                int trail = Mathf.RoundToInt(Mathf.Max(0, encoderPadding - Mp3DecoderDelay) * Scale);
                return Mathf.Clamp(extra - trail, 0, extra);
            }
        }

        /// <summary>One line of the Sound Test list, in music_info.txt order - including songs that aren't in the folder yet.</summary>
        [Serializable]
        public class Listing
        {
            [Tooltip("The track number from music_info.txt (\"01\"), or empty.")]
            public string number;
            [Tooltip("The song's display title (\"Parkour Predictions!\").")]
            public string title;
            [Tooltip("The file name without the extension - matches Entry.id once the song exists.")]
            public string id;
        }

        public const string ResourcePath = "MusicCatalog";

        [SerializeField] private List<Entry> tracks = new List<Entry>();
        [SerializeField] private List<Listing> soundTest = new List<Listing>();

        // Editor only (MusicCatalogBuilder): the music folder's files (names, sizes, content hashes) this was built from,
        // so a change made while the editor was closed or in play mode is caught on the next editor load.
        [SerializeField, HideInInspector] private string sourceStamp = "";

        /// <summary>The Sound Test's song list (Single Player > Sound Test).</summary>
        public IReadOnlyList<Listing> SoundTest => soundTest;

        private Dictionary<string, Entry> m_ById;
        private static MusicCatalog s_Loaded;
        private static bool s_Tried;

        public IReadOnlyList<Entry> Tracks => tracks;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Loaded = null;
            s_Tried = false;
        }

        /// <summary>The catalog, or null if it hasn't been built yet (warns once).</summary>
        public static MusicCatalog Load()
        {
            if (s_Loaded != null || s_Tried) return s_Loaded;
            s_Tried = true;
            s_Loaded = Resources.Load<MusicCatalog>(ResourcePath);
            if (s_Loaded == null)
            {
                Debug.LogWarning($"[Music] No Resources/{ResourcePath} - no music will play. It's built automatically from Assets/Core/Audio/Music (or Friendslop > Audio > Rebuild Music).");
            }
            return s_Loaded;
        }

        /// <summary>The track called <paramref name="id"/> (case doesn't matter), or null.</summary>
        public static Entry Get(string id)
        {
            MusicCatalog catalog = Load();
            return catalog != null ? catalog.Find(id) : null;
        }

        public Entry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (m_ById == null)
            {
                m_ById = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                foreach (Entry entry in tracks)
                {
                    if (entry != null && entry.clip != null && !string.IsNullOrEmpty(entry.id)) m_ById[entry.id] = entry;
                }
            }
            return m_ById.TryGetValue(id, out Entry found) ? found : null;
        }

#if UNITY_EDITOR
        public void SetTracks(List<Entry> entries, List<Listing> listing, string stamp = null)
        {
            tracks = entries ?? new List<Entry>();
            soundTest = listing ?? new List<Listing>();
            if (stamp != null) sourceStamp = stamp;
            m_ById = null;
        }

        /// <summary>Editor only: the folder stamp the catalog was last built from (see MusicCatalogBuilder).</summary>
        public string SourceStamp => sourceStamp;
#endif
    }
}
