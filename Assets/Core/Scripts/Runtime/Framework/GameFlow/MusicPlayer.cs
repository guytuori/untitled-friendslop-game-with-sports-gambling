using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Plays the background music (<see cref="MusicCatalog"/>, chosen by <see cref="MusicTracks"/>) at the Music
    /// volume, Master x Music from Audio Settings, following live changes. One hidden DontDestroyOnLoad object,
    /// made the first time it's needed, so a song carries across scene loads.
    ///
    /// WHAT PLAYS WHEN (all automatic - nothing in any scene needs setting up):
    ///   - On each full scene load: TitleScreen -> the title song; RoundResults -> the round-won song;
    ///     FinalScore -> the round-lost song (none yet, so it fades out); [BB] Core -> whatever was playing fades
    ///     out (the match takes over, below); SplashScreen -> nothing; any other scene is a menu -> the menu song
    ///     if there is one, else the title song is left to finish, else silence. Additive loads (the in-game
    ///     settings screens) don't count.
    ///   - In a match it follows RoundTimer's phase: Intro (the flyover) -> the flyover song; Countdown -> fades
    ///     out; Playing -> the map's song (practice too; a practice challenge gets its own, none yet);
    ///     Tallying / Ended -> fades out under TIME! and the result banner.
    ///
    /// LOOPING: a song jumps from the end of its music back to its loop point with no gap. Two AudioSources take
    /// turns: each plays one pass (start->end, then loopStart->end, ...) scheduled on the audio clock with
    /// PlayScheduled / SetScheduledEndTime, so the hand-over is sample-exact rather than frame-timed. Positions
    /// skip the MP3's encoder silence (see MusicCatalog.Entry.LeadSamples), so the song also starts right on its
    /// first note. A song with no loop point (N/A) plays once, then silence.
    ///
    /// SOUND TEST (Single Player > Sound Test, see <see cref="SoundTest"/>): <see cref="Pause"/> / <see cref="Resume"/>
    /// (resume re-schedules from the paused sample, loops intact), <see cref="TryGetProgress"/>, and
    /// <see cref="HoldThroughMenus"/> - a song picked there keeps playing through menu scene loads until something
    /// else plays or stops music.
    ///
    /// Clips are loaded when needed (their imports have Preload Audio Data off) - songs coming up are loaded
    /// early (the map's song during the flyover) so starting them never hitches - and unloaded again once they
    /// stop, unless still wanted.
    /// </summary>
    public class MusicPlayer : MonoBehaviour
    {
        private const string TitleScene = "TitleScreen";
        private const string SplashScene = "SplashScreen";
        private const string MatchScene = "[BB] Core";
        private const string CreditsScene = "Credits";
        private const string RoundResultsScene = "RoundResults";
        private const string FinalScoreScene = "FinalScore";

        private const double ScheduleAhead = 0.06;  // seconds between asking for a song and its first sample
        private const float DefaultFadeOut = 0.6f;

        private static MusicPlayer s_Instance;

        /// <summary>The song playing (or about to), or null for silence.</summary>
        public static string CurrentId => s_Instance != null ? s_Instance.m_CurrentId : null;

        private readonly List<Voice> m_Voices = new List<Voice>();
        private readonly Stack<AudioSource> m_FreeSources = new Stack<AudioSource>();
        private readonly HashSet<AudioClip> m_Wanted = new HashSet<AudioClip>();
        private string m_CurrentId;
        private bool m_InMatch;
        private string m_PreloadedMapTrack;
        private bool m_Held;

        // ----- public API -----

        /// <summary>Plays a song (from the start), fading out whatever was playing. Already playing it: nothing changes.</summary>
        public static void Play(string id, float fadeOutOthers = DefaultFadeOut)
        {
            if (s_Instance != null) s_Instance.m_Held = false;
            if (string.IsNullOrEmpty(id))
            {
                Stop(fadeOutOthers);
                return;
            }
            Instance.PlayInternal(id, fadeOutOthers);
        }

        /// <summary>Fades the music out (0 = cut).</summary>
        public static void Stop(float fadeSeconds = DefaultFadeOut)
        {
            if (s_Instance == null) return;
            s_Instance.m_Held = false;
            s_Instance.m_CurrentId = null;
            foreach (Voice voice in s_Instance.m_Voices) voice.FadeOut(fadeSeconds);
        }

        /// <summary>True while the current song is playing (not paused, not fading out, not finished).</summary>
        public static bool IsPlaying
        {
            get
            {
                Voice voice = s_Instance != null ? s_Instance.CurrentVoice() : null;
                return voice != null && !voice.Paused;
            }
        }

        /// <summary>True while the current song is paused (see <see cref="Pause"/>).</summary>
        public static bool IsPaused
        {
            get
            {
                Voice voice = s_Instance != null ? s_Instance.CurrentVoice() : null;
                return voice != null && voice.Paused;
            }
        }

        /// <summary>Pauses the current song where it is (Sound Test). <see cref="Resume"/> carries on from there, gapless loops and all.</summary>
        public static void Pause() => s_Instance?.CurrentVoice()?.Pause();

        public static void Resume() => s_Instance?.CurrentVoice()?.Resume();

        /// <summary>The current song's position and length, in seconds of music. False if nothing is playing (or it's still loading).</summary>
        public static bool TryGetProgress(out float seconds, out float length)
        {
            seconds = length = 0f;
            Voice voice = s_Instance != null ? s_Instance.CurrentVoice() : null;
            return voice != null && voice.TryGetProgress(out seconds, out length);
        }

        /// <summary>
        /// Keeps the current song going through menu screens (the menu rule in OnSceneLoaded leaves it alone) until
        /// something plays or stops music - set by the Sound Test when the player leaves it with a song playing.
        /// </summary>
        public static void HoldThroughMenus()
        {
            if (s_Instance != null && s_Instance.CurrentVoice() != null) s_Instance.m_Held = true;
        }

        /// <summary>Whatever the menus normally play (<see cref="MusicTracks.Menu"/>; silence until it's written).</summary>
        public static void PlayMenuMusic(float fadeOutOthers = DefaultFadeOut)
        {
            if (!string.IsNullOrEmpty(MusicTracks.Menu)) Play(MusicTracks.Menu, fadeOutOthers);
            else Stop(fadeOutOthers);
        }

        /// <summary>Loads a song now so it can start later without a hitch. Kept loaded until the next scene load.</summary>
        public static void Preload(string id)
        {
            MusicCatalog.Entry entry = MusicCatalog.Get(id);
            if (entry == null || entry.clip == null) return;
            Instance.m_Wanted.Add(entry.clip);
            if (entry.clip.loadState == AudioDataLoadState.Unloaded) entry.clip.LoadAudioData();
        }

        // ----- scene rules -----

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            s_Instance = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Hook()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return; // e.g. Esc > Settings screens loaded over the game

            MusicPlayer player = Instance;
            player.ForgetWanted();
            player.m_InMatch = scene.name == MatchScene;
            player.m_PreloadedMapTrack = null;

            switch (scene.name)
            {
                case TitleScene:
                    Play(MusicTracks.Title, 0.3f);
                    break;
                case SplashScene:
                    break;
                case MatchScene:
                    Stop(0.75f);
                    Preload(MusicTracks.Flyover);
                    break;
                case RoundResultsScene:
                    Play(MusicTracks.RoundWon, 0.3f);
                    break;
                case FinalScoreScene:
                    Play(MusicTracks.RoundLost, 1f);
                    break;
                case CreditsScene:
                    Play(MusicTracks.Credits, 0.5f); // CreditsController follows this song's position
                    break;
                default: // a menu
                    if (player.m_Held && player.CurrentVoice() != null) break; // a Sound Test pick keeps playing
                    if (!string.IsNullOrEmpty(MusicTracks.Menu)) Play(MusicTracks.Menu);
                    else if (!Same(CurrentId, MusicTracks.Title)) Stop(1f);
                    break;
            }
        }

        /// <summary>In [BB] Core: the song for the round's current phase.</summary>
        private void FollowMatch()
        {
            RoundTimer round = RoundTimer.Instance;
            if (round == null || !round.IsSpawned) return;

            string mapTrack = MapTrack(out bool mapKnown);
            if (mapKnown && mapTrack != m_PreloadedMapTrack)
            {
                m_PreloadedMapTrack = mapTrack;
                if (mapTrack != null) Preload(mapTrack);
            }

            switch (round.Phase)
            {
                case RoundTimer.RoundPhase.Intro:
                    if (!Same(m_CurrentId, MusicTracks.Flyover)) Play(MusicTracks.Flyover, 0.5f);
                    break;
                case RoundTimer.RoundPhase.Countdown:
                    if (m_CurrentId != null) Stop(0.4f);
                    break;
                case RoundTimer.RoundPhase.Playing:
                    if (!mapKnown) break; // map still being built - decide once it's there
                    if (!Same(m_CurrentId, mapTrack))
                    {
                        if (mapTrack != null) Play(mapTrack, 0.4f);
                        else if (m_CurrentId != null) Stop(0.8f);
                    }
                    break;
                default: // Tallying, Ended
                    if (m_CurrentId != null) Stop(1.5f);
                    break;
            }
        }

        private static bool Same(string a, string b) => string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);

        private static string MapTrack(out bool known)
        {
            if (PracticeMode.IsActive && PracticeMode.IsChallenge)
            {
                known = true;
                return MusicTracks.PracticeChallenge;
            }
            string mapId = MusicTracks.MapIdOf(MatchLayout.Current);
            known = !string.IsNullOrEmpty(mapId);
            return known ? MusicTracks.ForMap(mapId) : null;
        }

        // ----- the player -----

        private static MusicPlayer Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    var go = new GameObject("Music");
                    DontDestroyOnLoad(go);
                    s_Instance = go.AddComponent<MusicPlayer>();
                }
                return s_Instance;
            }
        }

        private void OnEnable() => AudioVolumeService.OnVolumesChanged += ApplyVolumes;
        private void OnDisable() => AudioVolumeService.OnVolumesChanged -= ApplyVolumes;

        private void OnDestroy()
        {
            if (s_Instance == this) s_Instance = null;
        }

        /// <summary>The voice playing the current song (not fading out or finished), or null.</summary>
        private Voice CurrentVoice()
        {
            if (m_CurrentId == null) return null;
            for (int i = m_Voices.Count - 1; i >= 0; i--)
            {
                Voice voice = m_Voices[i];
                if (!voice.Done && !voice.FadingOut && Same(voice.Entry.id, m_CurrentId)) return voice;
            }
            return null;
        }

        private void PlayInternal(string id, float fadeOutOthers)
        {
            MusicCatalog.Entry entry = MusicCatalog.Get(id);
            if (entry == null || entry.clip == null)
            {
                if (MusicCatalog.Load() != null) Debug.LogWarning($"[Music] No song called \"{id}\" in Assets/Core/Audio/Music.");
                Stop(fadeOutOthers);
                return;
            }

            if (Same(m_CurrentId, entry.id))
            {
                foreach (Voice voice in m_Voices)
                {
                    if (voice.Entry == entry && !voice.FadingOut) return; // already on
                }
            }

            foreach (Voice voice in m_Voices) voice.FadeOut(fadeOutOthers);
            m_CurrentId = entry.id;
            if (entry.clip.loadState == AudioDataLoadState.Unloaded) entry.clip.LoadAudioData();
            m_Voices.Add(new Voice(this, entry));
        }

        private void Update()
        {
            if (m_InMatch) FollowMatch();

            float volume = AudioVolumeService.GetMultiplier(AudioCategory.Music);
            double now = AudioSettings.dspTime;
            for (int i = m_Voices.Count - 1; i >= 0; i--)
            {
                Voice voice = m_Voices[i];
                voice.Tick(now, Time.unscaledDeltaTime, volume);
                if (voice.Done)
                {
                    voice.Release();
                    m_Voices.RemoveAt(i);
                    UnloadIfUnused(voice.Entry.clip);
                }
            }
        }

        private void ApplyVolumes()
        {
            float volume = AudioVolumeService.GetMultiplier(AudioCategory.Music);
            foreach (Voice voice in m_Voices) voice.ApplyVolume(volume);
        }

        private void ForgetWanted()
        {
            var old = new List<AudioClip>(m_Wanted);
            m_Wanted.Clear();
            foreach (AudioClip clip in old) UnloadIfUnused(clip);
        }

        private void UnloadIfUnused(AudioClip clip)
        {
            if (clip == null || m_Wanted.Contains(clip)) return;
            foreach (Voice voice in m_Voices)
            {
                if (voice.Entry.clip == clip) return;
            }
            if (clip.loadState == AudioDataLoadState.Loaded) clip.UnloadAudioData();
        }

        private AudioSource TakeSource()
        {
            AudioSource source = m_FreeSources.Count > 0 ? m_FreeSources.Pop() : null;
            if (source == null)
            {
                var go = new GameObject("Music Source");
                go.transform.SetParent(transform, false);
                source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 0f;
                source.priority = 0;
                source.bypassReverbZones = true;
                source.ignoreListenerPause = true;
            }
            return source;
        }

        private void GiveBack(AudioSource source)
        {
            if (source == null) return;
            source.Stop();
            source.clip = null;
            m_FreeSources.Push(source);
        }

        /// <summary>One song playing: its two AudioSources taking turns, its fade, and pause.</summary>
        private sealed class Voice
        {
            public readonly MusicCatalog.Entry Entry;
            private readonly MusicPlayer m_Owner;
            private readonly AudioSource[] m_Sources = new AudioSource[2];

            // The pass each source has scheduled: dsp start/end and the sample it starts from.
            private readonly double[] m_PassStart = new double[2];
            private readonly double[] m_PassEnd = new double[2];
            private readonly int[] m_PassFrom = new int[2];

            private bool m_Prepared;        // clip loaded, sample positions worked out
            private bool m_Scheduled;       // passes are on the audio clock
            private bool m_Loops;
            private int m_Rate, m_Lead, m_LoopStart, m_LoopEnd;
            private int m_StartSample;      // where the next Begin starts (lead, or where it was paused)
            private double m_PassSeconds;   // one loop pass (loopStart -> end)
            private double m_ScheduledUntil; // when the last scheduled pass ends
            private double m_FreeAt;        // when the source that plays the next pass becomes free
            private int m_NextSource;
            private double m_EndsAt = double.MaxValue; // a song that doesn't loop: when it's over

            private float m_Fade = 1f;
            private float m_FadeSpeed;      // per second, while fading out

            public bool FadingOut => m_FadeSpeed > 0f;
            public bool Paused { get; private set; }
            public bool Done { get; private set; }

            public Voice(MusicPlayer owner, MusicCatalog.Entry entry)
            {
                m_Owner = owner;
                Entry = entry;
            }

            public void FadeOut(float seconds)
            {
                if (Done) return;
                if (seconds <= 0f || !m_Scheduled || Paused)
                {
                    Done = true;
                    StopSources();
                    return;
                }
                m_FadeSpeed = Mathf.Max(m_FadeSpeed, 1f / seconds);
            }

            public void Pause()
            {
                if (Done || Paused) return;
                if (m_Scheduled) m_StartSample = CurrentSample(AudioSettings.dspTime);
                m_Scheduled = false;
                StopSources();
                Paused = true;
            }

            public void Resume()
            {
                if (Done || !Paused) return;
                Paused = false; // Tick schedules it again from m_StartSample
            }

            /// <summary>Where the song is, in seconds of music, and how long the music is.</summary>
            public bool TryGetProgress(out float seconds, out float length)
            {
                seconds = length = 0f;
                if (!m_Prepared) return false;
                int sample = m_Scheduled ? CurrentSample(AudioSettings.dspTime) : m_StartSample;
                seconds = Mathf.Max(0f, (sample - m_Lead) / (float)m_Rate);
                length = (m_LoopEnd - m_Lead) / (float)m_Rate;
                return true;
            }

            public void ApplyVolume(float volume)
            {
                float v = volume * m_Fade;
                foreach (AudioSource source in m_Sources)
                {
                    if (source != null) source.volume = v;
                }
            }

            public void Tick(double now, float deltaTime, float volume)
            {
                if (Done) return;
                AudioClip clip = Entry.clip;

                if (!m_Prepared)
                {
                    if (clip == null || clip.loadState == AudioDataLoadState.Failed)
                    {
                        Done = true;
                        return;
                    }
                    if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                    if (clip.loadState != AudioDataLoadState.Loaded) return; // still loading
                    Prepare();
                }

                if (Paused) return;
                if (!m_Scheduled) Begin(now + ScheduleAhead, m_StartSample);

                if (m_FadeSpeed > 0f)
                {
                    m_Fade -= m_FadeSpeed * deltaTime;
                    if (m_Fade <= 0f)
                    {
                        Done = true;
                        StopSources();
                        return;
                    }
                }
                ApplyVolume(volume);

                if (m_Loops)
                {
                    // The other source took over at m_FreeAt: give this one the pass after the last scheduled one.
                    if (now > m_FreeAt + 0.05)
                    {
                        SchedulePass(m_NextSource, m_LoopStart, m_ScheduledUntil);
                        m_FreeAt = m_ScheduledUntil - m_PassSeconds;
                        m_NextSource ^= 1;
                    }
                }
                else if (now > m_EndsAt + 0.1)
                {
                    Done = true;
                }
            }

            private void Prepare()
            {
                AudioClip clip = Entry.clip;
                m_Prepared = true;
                m_Rate = Mathf.Max(1, clip.frequency);
                m_Lead = Entry.LeadSamples(clip);
                m_LoopEnd = Mathf.Clamp(m_Lead + Entry.MusicSamples(clip), 1, clip.samples);
                m_LoopStart = Mathf.Clamp(m_Lead + Mathf.RoundToInt(Entry.loopStartSeconds * m_Rate), m_Lead, m_LoopEnd - 1);
                m_PassSeconds = (m_LoopEnd - m_LoopStart) / (double)m_Rate;
                m_Loops = Entry.loops;
                if (m_Loops && m_PassSeconds < 0.25)
                {
                    Debug.LogWarning($"[Music] \"{Entry.id}\" loops in under 0.25 s - playing it once instead.");
                    m_Loops = false;
                }
                m_StartSample = m_Lead;

                m_Sources[0] = m_Owner.TakeSource();
                m_Sources[1] = m_Owner.TakeSource();
                foreach (AudioSource source in m_Sources) source.clip = clip;
            }

            /// <summary>Schedules playback from <paramref name="fromSample"/> to the end, then (looping) the loop passes.</summary>
            private void Begin(double at, int fromSample)
            {
                m_Scheduled = true;
                fromSample = Mathf.Clamp(fromSample, m_Lead, m_LoopEnd - 1);
                double firstEnd = SchedulePass(0, fromSample, at);
                if (m_Loops)
                {
                    SchedulePass(1, m_LoopStart, firstEnd); // and the first loop right behind it
                    m_FreeAt = firstEnd;                     // source 0 is free once the first pass is over
                    m_NextSource = 0;
                    m_EndsAt = double.MaxValue;
                }
                else
                {
                    m_Sources[1].Stop();
                    m_PassStart[1] = m_PassEnd[1] = 0;
                    m_EndsAt = firstEnd;
                }
            }

            /// <summary>Source <paramref name="index"/> plays fromSample -> loop end, starting at dsp time <paramref name="at"/>. Returns when it ends.</summary>
            private double SchedulePass(int index, int fromSample, double at)
            {
                AudioSource source = m_Sources[index];
                double end = at + (m_LoopEnd - fromSample) / (double)m_Rate;
                source.Stop();
                source.timeSamples = fromSample;
                source.PlayScheduled(at);
                source.SetScheduledEndTime(end);
                m_PassStart[index] = at;
                m_PassEnd[index] = end;
                m_PassFrom[index] = fromSample;
                m_ScheduledUntil = end;
                return end;
            }

            /// <summary>The sample playing at dsp time <paramref name="now"/>, from the scheduled passes.</summary>
            private int CurrentSample(double now)
            {
                for (int i = 0; i < 2; i++)
                {
                    if (m_PassEnd[i] > m_PassStart[i] && now >= m_PassStart[i] && now < m_PassEnd[i])
                    {
                        return Mathf.Clamp(m_PassFrom[i] + (int)((now - m_PassStart[i]) * m_Rate), m_Lead, m_LoopEnd - 1);
                    }
                }
                // Before the first pass starts (just scheduled), or after a song that doesn't loop has ended.
                int first = -1;
                for (int i = 0; i < 2; i++)
                {
                    if (m_PassEnd[i] > m_PassStart[i] && (first < 0 || m_PassStart[i] < m_PassStart[first])) first = i;
                }
                if (first >= 0 && now < m_PassStart[first]) return m_PassFrom[first];
                return m_LoopEnd - 1;
            }

            private void StopSources()
            {
                foreach (AudioSource source in m_Sources)
                {
                    if (source != null) source.Stop();
                }
            }

            public void Release()
            {
                for (int i = 0; i < m_Sources.Length; i++)
                {
                    m_Owner.GiveBack(m_Sources[i]);
                    m_Sources[i] = null;
                }
            }
        }
    }
}
