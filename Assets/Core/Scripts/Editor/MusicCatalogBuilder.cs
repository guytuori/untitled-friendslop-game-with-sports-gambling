using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Keeps <see cref="MusicCatalog"/> (Resources/MusicCatalog) in step with the songs: every audio file in
    /// <see cref="MusicFolder"/>, by file name, with its loop point from <see cref="InfoFile"/> and, for MP3s, the
    /// encoder delay / padding from the file's LAME header (see MusicCatalog for why). Rebuilt automatically when
    /// a song or music_info.txt is added, changed, moved or deleted; when the editor loads (or scripts reload) if
    /// the catalog is missing or the folder changed since it was built (sizes / modified times - catches songs
    /// swapped while Unity was closed); when play mode ends if something changed during it (rebuilding is skipped
    /// in play mode); and via Friendslop > Audio > Rebuild Music.
    ///
    /// music_info.txt: one block per song, separated by blank lines -
    ///     01 - Parkour Predictions!                    (display title; "NN - " is the track number)
    ///     Parkour_Predictions.mp3                      (the file; anything after // is a comment)
    ///     loop point: N/A                              ("1.668s", or N/A = plays once, no loop)
    /// The colon and the "s" are optional, and a block may skip the title line (the file name is used). The
    /// order is the Sound Test's order (MusicCatalog.SoundTest); songs listed but not in the folder yet are shown
    /// there and do nothing when played. A song in the folder that isn't listed loops from the start (warning).
    /// </summary>
    [InitializeOnLoad]
    public static class MusicCatalogBuilder
    {
        public const string MusicFolder = "Assets/Core/Audio/Music";
        public const string InfoFile = MusicFolder + "/music_info.txt";
        private const string ResourcesFolder = "Assets/Core/Resources";
        private const string CatalogPath = ResourcesFolder + "/" + MusicCatalog.ResourcePath + ".asset";

        private static readonly Regex LoopLine = new Regex(@"^\s*loop\s*point\s*:?\s*(?<value>n\s*/\s*a|[0-9]*\.?[0-9]+)\s*s?\s*$", RegexOptions.IgnoreCase);

        private static bool s_Building;
        private static bool s_Scheduled;

        static MusicCatalogBuilder()
        {
            EditorApplication.delayCall += () =>
            {
                // Missing, or built before the Sound Test list existed (2026-10-17): build it now.
                var existing = AssetDatabase.LoadAssetAtPath<MusicCatalog>(CatalogPath);
                if (existing == null || existing.SoundTest.Count == 0 || existing.SourceStamp != FolderStamp()) Rebuild();
            };
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredEditMode || !s_ChangedInPlayMode) return;
                s_ChangedInPlayMode = false;
                ScheduleRebuild();
            };
        }

        private static bool s_ChangedInPlayMode;

        private static string QuickHash(FileInfo file)
        {
            const int Chunk = 64 * 1024;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (FileStream stream = file.OpenRead())
            {
                byte[] data;
                if (stream.Length <= Chunk * 2)
                {
                    data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                }
                else
                {
                    data = new byte[Chunk * 2];
                    stream.Read(data, 0, Chunk);
                    stream.Seek(-Chunk, SeekOrigin.End);
                    stream.Read(data, Chunk, Chunk);
                }
                return BitConverter.ToString(md5.ComputeHash(data)).Replace("-", "").Substring(0, 12);
            }
        }

        /// <summary>
        /// The songs and music_info.txt in the folder - names, sizes and a hash of the contents (all of
        /// music_info.txt; the first and last 64 KB of a song) - so a catalog built from different files can be
        /// spotted. No file times, so it's the same on every machine and a fresh clone doesn't rebuild.
        /// </summary>
        private static string FolderStamp()
        {
            try
            {
                string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", MusicFolder);
                if (!Directory.Exists(folder)) return "";
                var parts = Directory.GetFiles(folder)
                    .Select(f => new FileInfo(f))
                    .Where(f => IsAudio(f.Name) || string.Equals(MusicFolder + "/" + f.Name, InfoFile, StringComparison.Ordinal))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => $"{f.Name}|{f.Length}|{QuickHash(f)}");
                return string.Join(";", parts);
            }
            catch (Exception)
            {
                return "";
            }
        }

        [MenuItem("Friendslop/Audio/Rebuild Music")]
        public static void RebuildFromMenu()
        {
            EditorUtility.DisplayDialog("Music", Rebuild(), "OK");
        }

        internal static void ScheduleRebuild()
        {
            if (s_Building || s_Scheduled) return;
            s_Scheduled = true;
            EditorApplication.delayCall += () =>
            {
                s_Scheduled = false;
                Rebuild();
            };
        }

        internal static bool IsWatchedPath(string path) =>
            path.StartsWith(MusicFolder + "/", StringComparison.Ordinal) && (IsAudio(path) || path == InfoFile);

        private static bool IsAudio(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".mp3" || extension == ".wav" || extension == ".ogg" || extension == ".aif" || extension == ".aiff";
        }

        /// <summary>Rebuilds the catalog. Returns a summary (also logged).</summary>
        public static string Rebuild()
        {
            if (s_Building) return "Already rebuilding.";
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                s_ChangedInPlayMode = true; // rebuilt as soon as play mode ends
                return "Not rebuilt in play mode - it will be when play mode ends.";
            }

            s_Building = true;
            try
            {
                List<InfoSong> songs = ReadInfo(out string infoProblem);
                var loopPoints = new Dictionary<string, float?>(StringComparer.OrdinalIgnoreCase);
                foreach (InfoSong song in songs)
                {
                    if (song.HasLoop && !string.IsNullOrEmpty(song.Id)) loopPoints[song.Id] = song.Loop;
                }
                var entries = new List<MusicCatalog.Entry>();
                var lines = new List<string>();
                var warnings = new List<string>();
                if (infoProblem != null) warnings.Add(infoProblem);

                if (AssetDatabase.IsValidFolder(MusicFolder))
                {
                    IEnumerable<string> paths = AssetDatabase.FindAssets("t:AudioClip", new[] { MusicFolder })
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Where(IsAudio)
                        .Distinct()
                        .OrderBy(p => p, StringComparer.Ordinal);
                    foreach (string path in paths)
                    {
                        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                        if (clip == null) continue;
                        string id = Path.GetFileNameWithoutExtension(path);
                        var entry = new MusicCatalog.Entry { id = id, clip = clip };

                        if (loopPoints.TryGetValue(id, out float? loop))
                        {
                            entry.loops = loop.HasValue;
                            entry.loopStartSeconds = loop ?? 0f;
                        }
                        else
                        {
                            entry.loops = true;
                            entry.loopStartSeconds = 0f;
                            warnings.Add($"{id} isn't in music_info.txt - it loops from the start.");
                        }

                        if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryReadMp3Gapless(path, out int rate, out int musical, out int delay, out int padding))
                            {
                                entry.sourceSampleRate = rate;
                                entry.musicalSamples = musical;
                                entry.encoderDelay = delay;
                                entry.encoderPadding = padding;
                            }
                            else
                            {
                                warnings.Add($"{id}.mp3 has no LAME header, so its start/end silence can't be skipped - a WAV or OGG would loop more cleanly.");
                            }
                        }

                        float musicSeconds = entry.MusicSamples(clip) / (float)Mathf.Max(1, clip.frequency);
                        if (entry.loops && entry.loopStartSeconds >= musicSeconds)
                        {
                            warnings.Add($"{id}: loop point {entry.loopStartSeconds:0.000}s is past the end ({musicSeconds:0.000}s).");
                        }

                        int lead = entry.LeadSamples(clip);
                        lines.Add($"{id} ({musicSeconds:0.000}s, " +
                                  (entry.loops ? $"loops to {entry.loopStartSeconds:0.000}s" : "plays once") +
                                  (lead > 0 ? $", skips {lead} samples ({lead * 1000f / clip.frequency:0.0} ms) of encoder silence" : "") + ")");
                        entries.Add(entry);
                    }
                }

                // The Sound Test list: music_info.txt's order, then any song in the folder it doesn't list.
                var listing = new List<MusicCatalog.Listing>();
                var missing = new List<string>();
                foreach (InfoSong song in songs)
                {
                    if (string.IsNullOrEmpty(song.Id)) continue;
                    SplitNumber(song.Title, out string number, out string title);
                    listing.Add(new MusicCatalog.Listing { number = number, title = title, id = song.Id });
                    if (!entries.Any(e => string.Equals(e.id, song.Id, StringComparison.OrdinalIgnoreCase))) missing.Add(song.Id);
                }
                foreach (MusicCatalog.Entry entry in entries)
                {
                    if (!listing.Any(l => string.Equals(l.id, entry.id, StringComparison.OrdinalIgnoreCase)))
                    {
                        listing.Add(new MusicCatalog.Listing { number = "", title = entry.id.Replace('_', ' '), id = entry.id });
                    }
                }

                EnsureFolder(ResourcesFolder);
                var catalog = AssetDatabase.LoadAssetAtPath<MusicCatalog>(CatalogPath);
                if (catalog == null)
                {
                    catalog = ScriptableObject.CreateInstance<MusicCatalog>();
                    AssetDatabase.CreateAsset(catalog, CatalogPath);
                }
                catalog.SetTracks(entries, listing, FolderStamp());
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();

                var summary = new StringBuilder();
                summary.Append(entries.Count > 0 ? $"Music ({entries.Count}):\n  " + string.Join("\n  ", lines) : $"No music in {MusicFolder}.");
                summary.Append($"\n\nSound Test: {listing.Count} song(s)");
                if (missing.Count > 0) summary.Append($" - not added yet: {string.Join(", ", missing)}");
                summary.Append(".");
                if (warnings.Count > 0) summary.Append("\n\n" + string.Join("\n", warnings));
                if (warnings.Count > 0) Debug.LogWarning("[Music] " + summary);
                else Debug.Log("[Music] " + summary);
                return summary.ToString();
            }
            finally
            {
                s_Building = false;
            }
        }

        /// <summary>One block of music_info.txt.</summary>
        private struct InfoSong
        {
            public string Title;   // "01 - Parkour Predictions!"
            public string Id;      // "Parkour_Predictions"
            public bool HasLoop;   // a loop point line was there
            public float? Loop;    // seconds, or null = no loop (N/A)
        }

        /// <summary>music_info.txt's songs, in order (see the class summary for the format).</summary>
        private static List<InfoSong> ReadInfo(out string problem)
        {
            problem = null;
            var result = new List<InfoSong>();
            string full = Path.GetFullPath(InfoFile);
            if (!File.Exists(full))
            {
                problem = $"No {InfoFile} - every song loops from the start.";
                return result;
            }

            var pending = new List<string>();
            void Flush(bool hasLoop, float? loop)
            {
                if (pending.Count > 0)
                {
                    string fileLine = pending[pending.Count - 1];
                    string id = FileId(fileLine);
                    string title = pending.Count >= 2 ? pending[0] : id.Replace('_', ' ');
                    if (!string.IsNullOrEmpty(id)) result.Add(new InfoSong { Title = title, Id = id, HasLoop = hasLoop, Loop = loop });
                }
                pending.Clear();
            }

            foreach (string raw in File.ReadAllLines(full))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    Flush(false, null); // a block that ended without a loop line
                    continue;
                }
                Match match = LoopLine.Match(line);
                if (!match.Success)
                {
                    pending.Add(line);
                    continue;
                }

                string value = match.Groups["value"].Value;
                float? loop = value.Replace(" ", "").Equals("n/a", StringComparison.OrdinalIgnoreCase)
                    ? (float?)null
                    : float.Parse(value, CultureInfo.InvariantCulture);
                Flush(true, loop);
            }
            Flush(false, null);
            return result;
        }

        /// <summary>"Last_Stop.mp3 //not implemented yet" -> "Last_Stop".</summary>
        private static string FileId(string line)
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line.Substring(0, comment);
            line = line.Trim();
            string extension = Path.GetExtension(line);
            if (!string.IsNullOrEmpty(extension) && extension.Length <= 5) line = line.Substring(0, line.Length - extension.Length);
            return line.Trim();
        }

        private static readonly Regex NumberedTitle = new Regex(@"^\s*(?<number>\d+)\s*[-.)]\s*(?<title>.+?)\s*$");

        /// <summary>"01 - Parkour Predictions!" -> "01", "Parkour Predictions!".</summary>
        private static void SplitNumber(string text, out string number, out string title)
        {
            Match match = NumberedTitle.Match(text ?? "");
            if (match.Success)
            {
                number = match.Groups["number"].Value;
                title = match.Groups["title"].Value;
            }
            else
            {
                number = "";
                title = (text ?? "").Trim();
            }
        }

        /// <summary>
        /// Reads an MP3's Xing/Info + LAME header: the music's real length in samples (frames x samples per frame
        /// - delay - padding, the same count ffmpeg and other gapless players use) and the encoder delay/padding.
        /// </summary>
        internal static bool TryReadMp3Gapless(string assetPath, out int sampleRate, out int musicalSamples, out int delay, out int padding)
        {
            sampleRate = musicalSamples = delay = padding = 0;
            byte[] b;
            try { b = File.ReadAllBytes(Path.GetFullPath(assetPath)); }
            catch (Exception) { return false; }

            int i = 0;
            if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            {
                int size = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
                i = 10 + size + ((b[5] & 0x10) != 0 ? 10 : 0);
            }
            while (i + 4 < b.Length && !(b[i] == 0xFF && (b[i + 1] & 0xE0) == 0xE0)) i++;
            if (i + 4 >= b.Length) return false;

            int version = (b[i + 1] >> 3) & 3;       // 3 = MPEG-1, 2 = MPEG-2, 0 = MPEG-2.5
            int rateIndex = (b[i + 2] >> 2) & 3;
            int mode = (b[i + 3] >> 6) & 3;          // 3 = mono
            if (version == 1 || rateIndex == 3) return false;
            int[] rates = { 44100, 48000, 32000 };
            sampleRate = version == 3 ? rates[rateIndex] : version == 2 ? rates[rateIndex] / 2 : rates[rateIndex] / 4;
            int samplesPerFrame = version == 3 ? 1152 : 576;
            int sideInfo = version == 3 ? (mode == 3 ? 17 : 32) : (mode == 3 ? 9 : 17);

            int x = i + 4 + sideInfo;
            if (x + 8 > b.Length) return false;
            string tag = Encoding.ASCII.GetString(b, x, 4);
            if (tag != "Xing" && tag != "Info") return false;
            int flags = (b[x + 4] << 24) | (b[x + 5] << 16) | (b[x + 6] << 8) | b[x + 7];
            int p = x + 8;
            if ((flags & 1) == 0) return false;      // no frame count
            int frames = (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];
            p += 4;
            if ((flags & 2) != 0) p += 4;            // byte count
            if ((flags & 4) != 0) p += 100;          // seek table
            if ((flags & 8) != 0) p += 4;            // quality
            if (p + 24 > b.Length) return false;

            // LAME extension ("LAME3.100", "Lavc...", ...): delay and padding are 12 bits each at +21.
            if (!char.IsLetter((char)b[p])) return false;
            delay = (b[p + 21] << 4) | (b[p + 22] >> 4);
            padding = ((b[p + 22] & 0x0F) << 8) | b[p + 23];
            musicalSamples = frames * samplesPerFrame - delay - padding;
            return musicalSamples > 0;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    /// <summary>Triggers a rebuild when a song or music_info.txt is imported, moved or deleted.</summary>
    internal class MusicCatalogWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Concat(deleted).Concat(moved).Concat(movedFrom).Any(MusicCatalogBuilder.IsWatchedPath))
            {
                MusicCatalogBuilder.ScheduleRebuild();
            }
        }
    }
}
