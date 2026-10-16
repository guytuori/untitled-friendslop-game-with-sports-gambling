using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The round's announcer: voice clips (Assets/Core/Audio/Voice, through <see cref="VoiceCatalog"/>, at the
    /// Voice volume from Audio Settings) and the big on-screen text that goes with them.
    ///
    /// COUNTDOWN (RoundTimer's Countdown phase, after the intro): 321.mp3 plays and the middle of the screen
    /// shows 3, 2, 1, PARKOUR! on the clip's own beats (<see cref="Beats"/>: 0, 0.469, 0.823 and 1.192 s) -
    /// each one slammed in with rays, a shockwave ring, sparks, a flash and a screen shake, over a dark band.
    /// Until "PARKOUR!" this machine's player can't move (input lock "Countdown", also held through the
    /// intro so there's no gap) and the Esc menu is shut and won't open (<see cref="InGameMenu.Suppress"/>).
    /// The round itself starts at 1.192 s (the master switches RoundTimer to Playing then), while PARKOUR!
    /// is on screen; it fades out over the next ~2.7 s, drifting up and shrinking, so it doesn't get in the
    /// way.
    ///
    /// TIME CALLOUTS (while Playing): 30seconds.mp3 / 10seconds.mp3 when the round timer reaches 30 / 10,
    /// with a small, half-transparent "30 SECONDS REMAINING" at the bottom middle; then five, four, three,
    /// two, one at 5 ... 1 with a half-transparent number in the same place. Nothing there covers the HUD:
    /// the timers and wager ticker are top middle, the scoreboard left, health bottom left, the phone
    /// bottom right.
    ///
    /// TIME! (the round ending: Playing -> Tallying, whether the clock ran out or everyone reached the end):
    /// time.mp3 and the full works - dark screen, red and gold bands sliding in, spinning rays, repeating
    /// rings, sparks, a big flash and heavy shake, the letters slamming in one by one and bouncing - gone
    /// after ~1.7 s, as the round result comes up.
    ///
    /// Added at runtime by RoundTimer on every client (no scene setup); not in practice. Its overlay hides
    /// behind the Esc menu and settings screens.
    /// </summary>
    public class RoundAnnouncer : MonoBehaviour
    {
        public const string InputLock = "Countdown";

        /// <summary>Where "3", "2", "1" and "PARKOUR!" start in 321.mp3, in seconds.</summary>
        public static readonly float[] Beats = { 0f, 0.469f, 0.823f, RoundTimer.CountdownSeconds };

        private static readonly int[] CalloutSeconds = { 30, 10, 5, 4, 3, 2, 1 };
        private static readonly string[] CalloutClips = { "30seconds", "10seconds", "five", "four", "three", "two", "one" };
        private const string CountdownClip = "321";
        private const string TimeClip = "time";

        private const float AudioLead = 0.05f;              // the 321 clip is scheduled this far ahead, and the visuals use the same start
        private const float CountdownSafetySeconds = 4f;    // let go of the player after this whatever happens
        private const float SortingOrder = 55f;             // over the HUD and the intro overlay

        private static readonly Color[] DigitColors =
        {
            new Color(1f, 0.27f, 0.25f),  // 3
            new Color(1f, 0.62f, 0.12f),  // 2
            new Color(1f, 0.9f, 0.18f),   // 1
        };
        private static readonly Color Ink = new Color(0.07f, 0.04f, 0.14f);
        private static readonly Color Magenta = new Color(1f, 0.22f, 0.78f);
        private static readonly Color Cyan = new Color(0.15f, 0.9f, 1f);
        private static readonly Color Gold = new Color(1f, 0.84f, 0.2f);
        private static readonly Color Crimson = new Color(0.85f, 0.08f, 0.14f);

        private RoundTimer m_Round;
        private GameObject m_Host;
        private AudioSource m_CountdownSource, m_CalloutSource;

        private UIDocument m_Document;
        private VisualElement m_Root, m_Dim, m_Stage, m_Flash, m_Bottom;
        private FxCanvas m_Canvas;
        private readonly List<Fx> m_Effects = new List<Fx>();
        private bool m_CanvasDirty;

        private bool m_CountdownStarted, m_CountdownDone;
        private float m_CountdownStart;
        private int m_NextBeat;
        private BandFx m_CountdownBand;
        private bool m_TimeShown;
        private bool m_SawPlaying;
        private readonly HashSet<int> m_CalloutsDone = new HashSet<int>();
        private CorePlayerManager m_LockedPlayer;
        private bool m_MenuSuppressed;

        private float m_DimFrom, m_DimTo, m_DimStart, m_DimSeconds = 1f;
        private float m_FlashPeak, m_FlashStart, m_FlashSeconds = 1f;
        private float m_ShakeAmount, m_ShakeStart, m_ShakeSeconds = 1f;
        private readonly System.Random m_Random = new System.Random(1234);

        /// <summary>Adds the announcer to the round (RoundTimer calls this on every client when it spawns).</summary>
        public static void Begin(RoundTimer round)
        {
            if (round == null || round.GetComponent<RoundAnnouncer>() != null) return;
            round.gameObject.AddComponent<RoundAnnouncer>().m_Round = round;
        }

        private float Now => Time.unscaledTime;

        // =====================================================================================
        // Lifecycle
        // =====================================================================================

        private void Start()
        {
            m_Host = new GameObject("RoundAnnouncer");
            m_CountdownSource = MakeSource(m_Host);
            m_CalloutSource = MakeSource(m_Host);
            AudioVolumeService.OnVolumesChanged += ApplyVolume;
            PreloadClips();

            BuildOverlay();

            if (m_Round != null)
            {
                m_Round.TimeRemainingChanged += HandleTimeChanged;
                // Joined after the countdown: nothing to count down.
                if (m_Round.IsSpawned && m_Round.Phase != RoundTimer.RoundPhase.Intro && m_Round.Phase != RoundTimer.RoundPhase.Countdown)
                {
                    m_CountdownDone = true;
                }
            }
        }

        private void OnDestroy()
        {
            AudioVolumeService.OnVolumesChanged -= ApplyVolume;
            if (m_Round != null) m_Round.TimeRemainingChanged -= HandleTimeChanged;
            ReleasePlayer();
            if (m_MenuSuppressed) InGameMenu.Suppress(false);
            m_MenuSuppressed = false;
            if (m_Host != null) Destroy(m_Host);
        }

        private static AudioSource MakeSource(GameObject host)
        {
            AudioSource source = host.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f; // the announcer, not a sound in the world
            source.priority = 0;
            source.bypassReverbZones = true;
            source.volume = AudioVolumeService.GetMultiplier(AudioCategory.Voice);
            return source;
        }

        private void ApplyVolume()
        {
            float volume = AudioVolumeService.GetMultiplier(AudioCategory.Voice);
            if (m_CountdownSource != null) m_CountdownSource.volume = volume;
            if (m_CalloutSource != null) m_CalloutSource.volume = volume;
        }

        /// <summary>Loads every voice clip now (they aren't preloaded), so 321 starts exactly on time.</summary>
        private static void PreloadClips()
        {
            VoiceCatalog catalog = VoiceCatalog.Load();
            if (catalog == null) return;
            foreach (VoiceCatalog.Entry entry in catalog.Clips)
            {
                if (entry?.clip != null && entry.clip.loadState == AudioDataLoadState.Unloaded) entry.clip.LoadAudioData();
            }
        }

        private void PlayCallout(string id)
        {
            AudioClip clip = VoiceCatalog.Get(id);
            if (clip == null || m_CalloutSource == null) return;
            m_CalloutSource.volume = AudioVolumeService.GetMultiplier(AudioCategory.Voice);
            m_CalloutSource.PlayOneShot(clip);
        }

        // =====================================================================================
        // Every frame
        // =====================================================================================

        private void Update()
        {
            if (m_Round == null) { Destroy(this); return; }
            if (!m_Round.IsSpawned) return;

            RoundTimer.RoundPhase phase = m_Round.Phase;
            bool beforeRound = phase == RoundTimer.RoundPhase.Intro || phase == RoundTimer.RoundPhase.Countdown;

            // The player stays locked from the intro right through to "PARKOUR!".
            if (!m_CountdownDone && (beforeRound || m_CountdownStarted)) HoldPlayer();

            if (phase == RoundTimer.RoundPhase.Countdown && !m_CountdownStarted && !m_CountdownDone) StartCountdown();
            if (!beforeRound && !m_CountdownStarted && !m_CountdownDone)
            {
                // The round started without a countdown here (it was missed) - don't keep the player held.
                m_CountdownDone = true;
                ReleasePlayer();
            }
            if (m_CountdownStarted && !m_CountdownDone) UpdateCountdown(phase);

            if (phase == RoundTimer.RoundPhase.Playing) m_SawPlaying = true;
            if (phase == RoundTimer.RoundPhase.Tallying && m_SawPlaying && !m_TimeShown)
            {
                m_TimeShown = true;
                ShowTime();
            }

            if (m_Root != null)
            {
                m_Root.style.display = InGameMenu.IsOpen || SettingsOverlay.IsOpen ? DisplayStyle.None : DisplayStyle.Flex;
            }
            TickEffects();
        }

        private void HoldPlayer()
        {
            CorePlayerManager local = NetworkPlayers.Local;
            if (local == null || local == m_LockedPlayer) return;
            ReleasePlayer();
            local.SetInputLock(InputLock, true);
            m_LockedPlayer = local;
        }

        private void ReleasePlayer()
        {
            if (m_LockedPlayer != null) m_LockedPlayer.SetInputLock(InputLock, false);
            m_LockedPlayer = null;
        }

        // =====================================================================================
        // 3, 2, 1, PARKOUR!
        // =====================================================================================

        private void StartCountdown()
        {
            m_CountdownStarted = true;
            m_CountdownStart = Now + AudioLead;
            m_NextBeat = 0;

            InGameMenu.Suppress(true);
            m_MenuSuppressed = true;

            AudioClip clip = VoiceCatalog.Get(CountdownClip);
            if (clip != null)
            {
                m_CountdownSource.clip = clip;
                m_CountdownSource.volume = AudioVolumeService.GetMultiplier(AudioCategory.Voice);
                m_CountdownSource.PlayScheduled(AudioSettings.dspTime + AudioLead);
            }

            SetDim(0.28f, 0.12f);
            m_CountdownBand = Add(new BandFx
            {
                Start = m_CountdownStart, CenterFrac = 0.5f, HeightFrac = 0.56f, SkewFrac = 0.07f,
                Fill = new Color(Ink.r, Ink.g, Ink.b, 0.62f), Edge = Magenta, FromLeft = true, SlideSeconds = 0.14f,
            });
        }

        private void UpdateCountdown(RoundTimer.RoundPhase phase)
        {
            float elapsed = Now - m_CountdownStart;
            while (m_NextBeat < Beats.Length && elapsed >= Beats[m_NextBeat])
            {
                float at = m_CountdownStart + Beats[m_NextBeat];
                if (m_NextBeat < 3) ShowDigit(m_NextBeat, at);
                else ShowParkour(at);
                m_NextBeat++;
            }

            // Go at "PARKOUR!" - once the round really has started here too (it's the master's call).
            bool go = elapsed >= RoundTimer.CountdownSeconds &&
                      phase != RoundTimer.RoundPhase.Countdown && phase != RoundTimer.RoundPhase.Intro;
            if (go || elapsed >= CountdownSafetySeconds)
            {
                m_CountdownDone = true;
                ReleasePlayer();
                InGameMenu.Suppress(false);
                m_MenuSuppressed = false;
            }
        }

        private void ShowDigit(int beat, float at)
        {
            Color color = DigitColors[beat];
            float next = m_CountdownStart + Beats[beat + 1];
            float sign = beat % 2 == 0 ? 1f : -1f;
            const float slam = 0.09f;

            Add(new RaysFx
            {
                Start = at, End = next + 0.08f, ColorA = Fade(color, 0.42f), ColorB = Fade(Color.white, 0.12f),
                Count = 16, InnerFrac = 0.12f, OuterFrac = 1.3f, Spin = 70f * sign, Rotation = beat * 7f, PopSeconds = 0.12f,
            });
            Add(new RingFx { Start = at + slam, Seconds = 0.34f, Color = color, FromFrac = 0.14f, ToFrac = 0.8f, WidthFrac = 0.035f });
            Add(new SparksFx(m_Random) { Start = at + slam, Seconds = 0.45f, Count = 18, ColorA = color, ColorB = Color.white, SpeedFrac = 1.3f });
            Add(new Stamp
            {
                Start = at, Text = (3 - beat).ToString(), SizeFrac = 0.42f, StartScale = 3.2f, Rotation = 20f * sign,
                SlamSeconds = slam, Face = color, Outline = Ink, GhostA = Fade(color, 0.55f), GhostB = Fade(Color.white, 0.45f),
                ExitAt = next, ExitSeconds = 0.08f, ExitScale = 1.5f,
            }, m_Stage);
            Add(new LaterFx { Start = at + slam, Action = () => { Flash(0.32f, 0.14f); Shake(12f + beat * 6f, 0.2f); } });
        }

        private void ShowParkour(float at)
        {
            const float slam = 0.1f;
            SetDim(0f, 0.25f);
            if (m_CountdownBand != null) m_CountdownBand.ExitAt = at;

            Add(new RaysFx
            {
                Start = at, End = at + 0.65f, ColorA = Fade(Magenta, 0.6f), ColorB = Fade(Cyan, 0.45f),
                Count = 22, InnerFrac = 0.08f, OuterFrac = 1.7f, Spin = 110f, PopSeconds = 0.18f, Expand = true,
            });
            Add(new RingFx { Start = at + slam, Seconds = 0.5f, Color = Color.white, FromFrac = 0.12f, ToFrac = 1.1f, WidthFrac = 0.03f });
            Add(new RingFx { Start = at + slam + 0.09f, Seconds = 0.5f, Color = Cyan, FromFrac = 0.08f, ToFrac = 0.9f, WidthFrac = 0.022f });
            Add(new SparksFx(m_Random) { Start = at + slam, Seconds = 0.7f, Count = 70, ColorA = Magenta, ColorB = Cyan, ColorC = Gold, SpeedFrac = 1.8f });
            Add(new Stamp
            {
                Start = at, Text = "PARKOUR!", SizeFrac = 0.2f, StartScale = 3.4f, Rotation = 22f, Stagger = 0.028f,
                SlamSeconds = slam, Face = Magenta, Outline = Color.white, GhostA = Fade(Cyan, 0.75f), GhostB = Fade(Gold, 0.65f),
                // Full strength only briefly - the round is on - then it drifts up, shrinks and fades away.
                ExitAt = at + 0.45f, ExitSeconds = 2.3f, ExitScale = 0.8f, ExitRiseFrac = 0.14f, ExitPower = 1.6f,
            }, m_Stage);
            Add(new LaterFx { Start = at + slam, Action = () => { Flash(0.42f, 0.16f); Shake(20f, 0.32f); } });
        }

        // =====================================================================================
        // Time callouts
        // =====================================================================================

        private void HandleTimeChanged(float previous, float current)
        {
            if (m_Round == null || m_Round.Phase != RoundTimer.RoundPhase.Playing) return;
            for (int i = 0; i < CalloutSeconds.Length; i++)
            {
                int seconds = CalloutSeconds[i];
                if (previous > seconds && current <= seconds && m_CalloutsDone.Add(seconds))
                {
                    PlayCallout(CalloutClips[i]);
                    if (seconds > 5) ShowSecondsLeft(seconds);
                    else ShowNumber(seconds);
                }
            }
        }

        private void ShowSecondsLeft(int seconds)
        {
            ClearBottom();
            Color color = seconds > 10 ? new Color(1f, 0.86f, 0.4f) : new Color(1f, 0.52f, 0.32f);
            Add(new BannerFx { Start = Now, Text = Loc.F("{0} SECONDS REMAINING", seconds), Color = color }, m_Bottom);
        }

        private void ShowNumber(int seconds)
        {
            ClearBottom();
            Color color = Color.Lerp(new Color(1f, 0.9f, 0.3f), new Color(1f, 0.25f, 0.2f), (5 - seconds) / 4f);
            Add(new NumberFx { Start = Now, Text = seconds.ToString(), Color = color }, m_Bottom);
        }

        private void ClearBottom()
        {
            foreach (Fx fx in m_Effects)
            {
                if (fx is BottomFx bottom) bottom.ExitAt = Mathf.Min(bottom.ExitAt, Now);
            }
        }

        // =====================================================================================
        // TIME!
        // =====================================================================================

        private void ShowTime()
        {
            float at = Now;
            const float hold = 1.45f;
            ClearBottom();
            PlayCallout(TimeClip);

            SetDim(0.5f, 0.15f);
            Add(new LaterFx { Start = at + hold, Action = () => SetDim(0f, 0.3f) });

            Add(new BandFx
            {
                Start = at, CenterFrac = 0.36f, HeightFrac = 0.2f, SkewFrac = 0.08f, Fill = Fade(Crimson, 0.85f), Edge = Gold,
                FromLeft = true, SlideSeconds = 0.16f, ExitAt = at + hold, ExitSeconds = 0.25f,
            });
            Add(new BandFx
            {
                Start = at + 0.05f, CenterFrac = 0.66f, HeightFrac = 0.16f, SkewFrac = 0.08f, Fill = Fade(Gold, 0.8f), Edge = Crimson,
                FromLeft = false, SlideSeconds = 0.16f, ExitAt = at + hold, ExitSeconds = 0.25f,
            });
            Add(new RaysFx
            {
                Start = at, End = at + hold + 0.25f, ColorA = Fade(Crimson, 0.5f), ColorB = Fade(Gold, 0.38f),
                Count = 26, InnerFrac = 0.1f, OuterFrac = 1.5f, Spin = 35f, PopSeconds = 0.15f,
            });
            for (int i = 0; i < 6; i++)
            {
                Add(new RingFx
                {
                    Start = at + 0.12f + i * 0.22f, Seconds = 0.45f, Color = i % 2 == 0 ? Color.white : Gold,
                    FromFrac = 0.15f, ToFrac = 1.1f, WidthFrac = 0.03f,
                });
            }
            Add(new SparksFx(m_Random) { Start = at + 0.12f, Seconds = 0.9f, Count = 80, ColorA = Gold, ColorB = Crimson, ColorC = Color.white, SpeedFrac = 2f });
            Add(new SparksFx(m_Random) { Start = at + 0.5f, Seconds = 0.8f, Count = 50, ColorA = Color.white, ColorB = Gold, SpeedFrac = 1.6f });

            const string text = "TIME!";
            const float stagger = 0.05f, slam = 0.1f;
            Add(new Stamp
            {
                Start = at, Text = text, SizeFrac = 0.3f, StartScale = 5f, Rotation = 28f, Stagger = stagger, SlamSeconds = slam,
                Face = Gold, Outline = new Color(0.42f, 0.02f, 0.05f), GhostA = Fade(Crimson, 0.85f), GhostB = Fade(Color.white, 0.7f),
                Bob = true, ExitAt = at + hold, ExitSeconds = 0.28f, ExitScale = 1.7f,
            }, m_Stage);

            Add(new LaterFx { Start = at + slam, Action = () => { Flash(0.75f, 0.3f); Shake(28f, 0.5f); } });
            for (int i = 1; i < text.Length; i++)
            {
                // Each letter's landing jolts the screen again.
                Add(new LaterFx { Start = at + slam + i * stagger, Action = () => Shake(16f, 0.16f) });
            }
        }

        // =====================================================================================
        // Overlay and effects
        // =====================================================================================

        private void BuildOverlay()
        {
            PanelSettings panel = null;
            foreach (UIDocument doc in FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (doc.panelSettings != null) { panel = doc.panelSettings; break; }
            }
            if (panel == null)
            {
                Debug.LogWarning("[RoundAnnouncer] No UI panel settings found in the scene - the countdown and callouts will have no text.", this);
                return;
            }

            var go = new GameObject("RoundAnnouncerOverlay");
            go.transform.SetParent(m_Host.transform, false);
            go.SetActive(false);
            m_Document = go.AddComponent<UIDocument>();
            m_Document.panelSettings = panel;
            m_Document.sortingOrder = SortingOrder;
            go.SetActive(true);

            m_Root = m_Document.rootVisualElement;
            m_Root.style.flexGrow = 1;
            m_Root.pickingMode = PickingMode.Ignore;

            m_Dim = FullScreen(Color.black);
            m_Dim.style.opacity = 0f;
            m_Root.Add(m_Dim);

            m_Canvas = new FxCanvas(m_Effects);
            Fill(m_Canvas);
            m_Root.Add(m_Canvas);

            m_Stage = new VisualElement();
            Fill(m_Stage);
            m_Root.Add(m_Stage);

            m_Bottom = new VisualElement();
            m_Bottom.style.position = Position.Absolute;
            m_Bottom.style.left = 0;
            m_Bottom.style.right = 0;
            m_Bottom.style.bottom = Length.Percent(9);
            m_Bottom.style.height = 120;
            m_Bottom.pickingMode = PickingMode.Ignore;
            m_Root.Add(m_Bottom);

            m_Flash = FullScreen(Color.white);
            m_Flash.style.opacity = 0f;
            m_Root.Add(m_Flash);

            GameFont.Apply(m_Root);
        }

        private static VisualElement FullScreen(Color color)
        {
            var element = new VisualElement();
            Fill(element);
            element.style.backgroundColor = color;
            return element;
        }

        private static void Fill(VisualElement element)
        {
            element.style.position = Position.Absolute;
            element.style.left = 0;
            element.style.right = 0;
            element.style.top = 0;
            element.style.bottom = 0;
            element.pickingMode = PickingMode.Ignore;
        }

        private T Add<T>(T fx, VisualElement parent = null) where T : Fx
        {
            if (m_Root == null) return fx;
            fx.Build(parent, Height);
            m_Effects.Add(fx);
            return fx;
        }

        /// <summary>The overlay's height in panel units (sizes are fractions of it).</summary>
        private float Height
        {
            get
            {
                float h = m_Root != null ? m_Root.layout.height : float.NaN;
                if (float.IsNaN(h) || h < 10f) h = m_Root?.panel?.visualTree?.layout.height ?? float.NaN;
                if (float.IsNaN(h) || h < 10f) h = 1080f;
                return h;
            }
        }

        private void TickEffects()
        {
            if (m_Root == null) return;
            float now = Now;
            float h = Height;
            bool canvas = false;
            for (int i = 0; i < m_Effects.Count; i++)
            {
                Fx fx = m_Effects[i];
                if (!fx.Tick(now, h))
                {
                    fx.Kill();
                    m_Effects.RemoveAt(i--);
                    continue;
                }
                if (fx is CanvasFx) canvas = true;
            }

            if (canvas || m_CanvasDirty) m_Canvas.MarkDirtyRepaint();
            m_CanvasDirty = canvas; // one more repaint after the last shape goes, to clear it

            float dim = Mathf.Lerp(m_DimFrom, m_DimTo, Mathf.Clamp01((now - m_DimStart) / m_DimSeconds));
            m_Dim.style.opacity = dim;

            float flash = Mathf.Clamp01((now - m_FlashStart) / m_FlashSeconds);
            m_Flash.style.opacity = flash < 1f ? m_FlashPeak * (1f - flash) * (1f - flash) : 0f;

            float shakeK = Mathf.Clamp01((now - m_ShakeStart) / m_ShakeSeconds);
            float amount = shakeK < 1f ? m_ShakeAmount * (1f - shakeK) * (1f - shakeK) : 0f;
            var offset = new Translate(
                amount * (Mathf.PerlinNoise(now * 38f, 0.3f) * 2f - 1f),
                amount * (Mathf.PerlinNoise(0.7f, now * 41f) * 2f - 1f));
            m_Stage.style.translate = offset;
            m_Canvas.style.translate = offset;
        }

        private void SetDim(float target, float seconds)
        {
            if (m_Dim == null) return;
            m_DimFrom = m_Dim.resolvedStyle.opacity;
            m_DimTo = target;
            m_DimStart = Now;
            m_DimSeconds = Mathf.Max(0.01f, seconds);
        }

        private void Flash(float peak, float seconds)
        {
            m_FlashPeak = peak;
            m_FlashStart = Now;
            m_FlashSeconds = Mathf.Max(0.01f, seconds);
        }

        private void Shake(float amount, float seconds)
        {
            float k = Mathf.Clamp01((Now - m_ShakeStart) / m_ShakeSeconds);
            float remaining = k < 1f ? m_ShakeAmount * (1f - k) * (1f - k) : 0f;
            if (amount < remaining) return;
            m_ShakeAmount = amount;
            m_ShakeStart = Now;
            m_ShakeSeconds = Mathf.Max(0.01f, seconds);
        }

        private static Color Fade(Color color, float alpha) => new Color(color.r, color.g, color.b, alpha);
        private static float OutCubic(float k) { k = 1f - Mathf.Clamp01(k); return 1f - k * k * k; }
        private static float InOut(float k) { k = Mathf.Clamp01(k); return k * k * (3f - 2f * k); }

        private static Label MakeLabel(string text, float size, Color color)
        {
            var label = new Label(text);
            label.pickingMode = PickingMode.Ignore;
            label.style.fontSize = size;
            label.style.color = color;
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.marginLeft = label.style.marginRight = label.style.marginTop = label.style.marginBottom = 0;
            label.style.paddingLeft = label.style.paddingRight = label.style.paddingTop = label.style.paddingBottom = 0;
            return label;
        }

        // ---------------------------------------------------------------------------------------
        // Effect types
        // ---------------------------------------------------------------------------------------

        /// <summary>Something on screen for a while. Tick returns false when it's done.</summary>
        private abstract class Fx
        {
            public float Start;
            public virtual void Build(VisualElement parent, float h) { }
            public abstract bool Tick(float now, float h);
            public virtual void Kill() { }
        }

        /// <summary>Runs an action at <see cref="Fx.Start"/>.</summary>
        private sealed class LaterFx : Fx
        {
            public Action Action;
            public override bool Tick(float now, float h)
            {
                if (now < Start) return true;
                Action?.Invoke();
                return false;
            }
        }

        /// <summary>Big text in the middle, slammed in letter by letter, with coloured ghosts and a zoom trail.</summary>
        private sealed class Stamp : Fx
        {
            public string Text;
            public float SizeFrac, StartScale = 3f, Rotation = 15f, Stagger, SlamSeconds = 0.1f;
            public Color Face, Outline, GhostA, GhostB;
            public float ExitAt = float.MaxValue, ExitSeconds = 0.1f, ExitScale = 1.35f, ExitRiseFrac, ExitPower = 1f;
            public bool Bob;

            private sealed class Cell { public VisualElement Root; public Label Trail, A, B; }

            private VisualElement m_Row;
            private readonly List<Cell> m_Cells = new List<Cell>();
            private float m_Size;

            public override void Build(VisualElement parent, float h)
            {
                m_Size = h * SizeFrac;
                m_Row = new VisualElement();
                Fill(m_Row);
                m_Row.style.flexDirection = FlexDirection.Row;
                m_Row.style.justifyContent = Justify.Center;
                m_Row.style.alignItems = Align.Center;
                m_Row.style.visibility = Visibility.Hidden;

                float outline = m_Size * 0.05f;
                foreach (char c in Text)
                {
                    string s = c.ToString();
                    var cell = new Cell { Root = new VisualElement() };
                    cell.Root.pickingMode = PickingMode.Ignore;
                    cell.Root.style.marginLeft = cell.Root.style.marginRight = -m_Size * 0.015f;

                    cell.Trail = Ghost(s, Fade(Color.white, 0.4f), outline);
                    cell.A = Ghost(s, GhostA, outline);
                    cell.B = Ghost(s, GhostB, outline);
                    Label main = MakeLabel(s, m_Size, Face);
                    main.style.unityTextOutlineWidth = outline;
                    main.style.unityTextOutlineColor = Outline;
                    main.style.textShadow = new TextShadow { offset = new Vector2(0f, m_Size * 0.05f), blurRadius = m_Size * 0.03f, color = new Color(0f, 0f, 0f, 0.55f) };

                    cell.Root.Add(cell.Trail);
                    cell.Root.Add(cell.A);
                    cell.Root.Add(cell.B);
                    cell.Root.Add(main);
                    m_Row.Add(cell.Root);
                    m_Cells.Add(cell);
                }
                parent.Add(m_Row);
            }

            private Label Ghost(string s, Color color, float outline)
            {
                Label ghost = MakeLabel(s, m_Size, color);
                ghost.style.position = Position.Absolute;
                ghost.style.left = 0;
                ghost.style.right = 0;
                ghost.style.top = 0;
                ghost.style.bottom = 0;
                ghost.style.unityTextOutlineWidth = outline;
                ghost.style.unityTextOutlineColor = color;
                return ghost;
            }

            public override bool Tick(float now, float h)
            {
                float t = now - Start;
                if (t < 0f) return true;
                m_Row.style.visibility = Visibility.Visible;

                float exit = ExitAt < float.MaxValue ? Mathf.Clamp01((now - ExitAt) / ExitSeconds) : 0f;
                if (exit >= 1f) return false;
                m_Row.style.opacity = Mathf.Pow(1f - exit, ExitPower);
                m_Row.style.scale = new Scale(Vector3.one * Mathf.Lerp(1f, ExitScale, OutCubic(exit)));
                m_Row.style.translate = new Translate(0f, -ExitRiseFrac * h * InOut(exit));

                for (int i = 0; i < m_Cells.Count; i++)
                {
                    Cell cell = m_Cells[i];
                    float tl = t - i * Stagger;
                    if (tl < 0f) { cell.Root.style.opacity = 0f; continue; }

                    float k = Mathf.Clamp01(tl / SlamSeconds);
                    float e = OutCubic(k);
                    float sign = m_Cells.Count > 1 && i % 2 == 1 ? -1f : 1f;
                    float scale = Mathf.Lerp(StartScale, 1f, e);
                    float rotation = Rotation * sign * (1f - e);
                    float sx = 1f, sy = 1f, y = 0f;

                    float u = tl - SlamSeconds;
                    if (u > 0f)
                    {
                        // Squash and wobble on landing.
                        float wobble = 0.16f * Mathf.Exp(-u * 12f) * Mathf.Sin(u * 45f);
                        sx = 1f + wobble;
                        sy = 1f - wobble;
                        rotation += Rotation * 0.25f * sign * Mathf.Exp(-u * 10f) * Mathf.Sin(u * 38f);
                        if (Bob)
                        {
                            y = Mathf.Sin(tl * 10f + i * 0.9f) * h * 0.012f;
                            scale *= 1f + 0.03f * Mathf.Sin(tl * 14f + i);
                        }
                    }

                    cell.Root.style.opacity = Mathf.Clamp01(k * 4f);
                    cell.Root.style.scale = new Scale(new Vector3(scale * sx, scale * sy, 1f));
                    cell.Root.style.rotate = new Rotate(new Angle(rotation, AngleUnit.Degree));
                    cell.Root.style.translate = new Translate(0f, y);

                    float spread = m_Size * 0.045f * (1f + 3f * (1f - e));
                    cell.A.style.translate = new Translate(-spread, spread * 0.3f);
                    cell.B.style.translate = new Translate(spread, -spread * 0.3f);

                    float trail = Mathf.Clamp01(tl / 0.28f);
                    cell.Trail.style.scale = new Scale(Vector3.one * (1f + 0.6f * (1f - trail)));
                    cell.Trail.style.opacity = 0.4f * (1f - trail);
                }
                return true;
            }

            public override void Kill() => m_Row?.RemoveFromHierarchy();
        }

        /// <summary>Bottom-middle callouts: small, half transparent; a newer one pushes the old one out.</summary>
        private abstract class BottomFx : Fx
        {
            public float ExitAt = float.MaxValue;
            protected VisualElement m_Element;
            public override void Kill() => m_Element?.RemoveFromHierarchy();

            protected void Centre(VisualElement parent)
            {
                var holder = new VisualElement();
                Fill(holder);
                holder.style.alignItems = Align.Center;
                holder.style.justifyContent = Justify.Center;
                holder.Add(m_Element);
                parent.Add(holder);
                m_Element = holder;
            }
        }

        /// <summary>"30 SECONDS REMAINING": slides up, holds, fades.</summary>
        private sealed class BannerFx : BottomFx
        {
            public string Text;
            public Color Color;
            private const float MaxOpacity = 0.78f, InSeconds = 0.2f, HoldUntil = 1.9f, OutSeconds = 0.5f;
            private VisualElement m_Pill, m_Bar;

            public override void Build(VisualElement parent, float h)
            {
                m_Pill = new VisualElement();
                m_Pill.pickingMode = PickingMode.Ignore;
                m_Pill.style.alignItems = Align.Center;
                m_Pill.style.backgroundColor = new Color(0f, 0f, 0f, 0.32f);
                m_Pill.style.borderTopLeftRadius = m_Pill.style.borderTopRightRadius = 10;
                m_Pill.style.borderBottomLeftRadius = m_Pill.style.borderBottomRightRadius = 10;
                m_Pill.style.paddingTop = 6;
                m_Pill.style.paddingBottom = 6;
                m_Pill.style.paddingLeft = 22;
                m_Pill.style.paddingRight = 22;

                Label label = MakeLabel(Text, 30, Color);
                label.style.unityTextOutlineWidth = 1.5f;
                label.style.unityTextOutlineColor = new Color(0f, 0f, 0f, 0.6f);
                label.style.letterSpacing = 3;
                m_Pill.Add(label);

                m_Bar = new VisualElement();
                m_Bar.pickingMode = PickingMode.Ignore;
                m_Bar.style.height = 3;
                m_Bar.style.marginTop = 4;
                m_Bar.style.backgroundColor = Color;
                m_Bar.style.width = Length.Percent(0);
                m_Pill.Add(m_Bar);

                m_Element = m_Pill;
                Centre(parent);
            }

            public override bool Tick(float now, float h)
            {
                float t = now - Start;
                float exitStart = Mathf.Min(Start + HoldUntil, ExitAt);
                float outSeconds = ExitAt < Start + HoldUntil ? 0.1f : OutSeconds;
                float exit = Mathf.Clamp01((now - exitStart) / outSeconds);
                if (exit >= 1f) return false;

                float e = OutCubic(t / InSeconds);
                m_Pill.style.opacity = MaxOpacity * Mathf.Clamp01(t / InSeconds) * (1f - exit);
                m_Pill.style.translate = new Translate(0f, 24f * (1f - e));
                m_Pill.style.scale = new Scale(Vector3.one * Mathf.Lerp(1.2f, 1f, e));
                m_Bar.style.width = Length.Percent(100f * OutCubic(t / 0.35f));
                return true;
            }
        }

        /// <summary>5 ... 1: pops in, then fades before the next.</summary>
        private sealed class NumberFx : BottomFx
        {
            public string Text;
            public Color Color;
            private const float MaxOpacity = 0.7f, PopSeconds = 0.14f, FadeFrom = 0.35f, FadeUntil = 0.92f;
            private Label m_Label;

            public override void Build(VisualElement parent, float h)
            {
                m_Label = MakeLabel(Text, 72, Color);
                m_Label.style.unityTextOutlineWidth = 3f;
                m_Label.style.unityTextOutlineColor = new Color(0f, 0f, 0f, 0.55f);
                m_Element = m_Label;
                Centre(parent);
            }

            public override bool Tick(float now, float h)
            {
                float t = now - Start;
                if (t >= FadeUntil || now >= ExitAt + 0.05f) return false;
                float fade = 1f - Mathf.Clamp01((t - FadeFrom) / (FadeUntil - FadeFrom));
                m_Label.style.opacity = MaxOpacity * Mathf.Clamp01(t / 0.05f) * fade;
                m_Label.style.scale = new Scale(Vector3.one * Mathf.Lerp(1.7f, 1f, OutCubic(t / PopSeconds)));
                return true;
            }
        }

        /// <summary>Drawn on the overlay's canvas (rays, rings, bands, sparks).</summary>
        private abstract class CanvasFx : Fx
        {
            protected float m_Now;
            public override bool Tick(float now, float h)
            {
                m_Now = now;
                return Alive(now);
            }
            protected abstract bool Alive(float now);
            public abstract void Draw(Painter2D painter, Vector2 size);

            protected static Vector2 Dir(float degrees)
            {
                float r = degrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
            }
        }

        /// <summary>A starburst behind the text, turning.</summary>
        private sealed class RaysFx : CanvasFx
        {
            public float End;
            public Color ColorA, ColorB;
            public int Count = 16;
            public float InnerFrac = 0.1f, OuterFrac = 1.3f, Spin, Rotation, PopSeconds = 0.12f;
            public bool Expand;

            protected override bool Alive(float now) => now < End;

            public override void Draw(Painter2D p, Vector2 size)
            {
                float t = m_Now - Start;
                if (t < 0f) return;
                float h = size.y;
                float pop = OutCubic(t / PopSeconds);
                float fadeOut = Mathf.Clamp01((End - m_Now) / 0.15f);
                float alpha = pop * fadeOut;
                float outer = h * OuterFrac * (Expand ? Mathf.Lerp(0.3f, 1f, OutCubic(t / 0.4f)) : Mathf.Lerp(0.6f, 1f, pop));
                float inner = h * InnerFrac;
                Vector2 c = size * 0.5f;
                float step = 360f / Count;
                float rotation = Rotation + Spin * t;

                for (int i = 0; i < Count; i++)
                {
                    Color color = i % 2 == 0 ? ColorA : ColorB;
                    float a = rotation + i * step;
                    float half = step * (i % 2 == 0 ? 0.3f : 0.18f);
                    p.fillColor = new Color(color.r, color.g, color.b, color.a * alpha);
                    p.BeginPath();
                    p.MoveTo(c + Dir(a) * inner);
                    p.LineTo(c + Dir(a - half) * outer);
                    p.LineTo(c + Dir(a + half) * outer);
                    p.ClosePath();
                    p.Fill();
                }
            }
        }

        /// <summary>A shockwave ring growing out from the middle.</summary>
        private sealed class RingFx : CanvasFx
        {
            public float Seconds = 0.35f, FromFrac = 0.1f, ToFrac = 0.8f, WidthFrac = 0.03f;
            public Color Color;
            private const int Segments = 56;

            protected override bool Alive(float now) => now < Start + Seconds;

            public override void Draw(Painter2D p, Vector2 size)
            {
                float t = m_Now - Start;
                if (t < 0f) return;
                float k = Mathf.Clamp01(t / Seconds);
                float h = size.y;
                float radius = h * Mathf.Lerp(FromFrac, ToFrac, OutCubic(k));
                Vector2 c = size * 0.5f;
                p.strokeColor = new Color(Color.r, Color.g, Color.b, Color.a * (1f - k));
                p.lineWidth = Mathf.Max(1f, h * WidthFrac * (1f - k * 0.8f));
                p.BeginPath();
                p.MoveTo(c + Dir(0f) * radius);
                for (int i = 1; i < Segments; i++) p.LineTo(c + Dir(i * 360f / Segments) * radius);
                p.ClosePath();
                p.Stroke();
            }
        }

        /// <summary>A slanted band across the screen that slides in from one side and out the other.</summary>
        private sealed class BandFx : CanvasFx
        {
            public float CenterFrac = 0.5f, HeightFrac = 0.3f, SkewFrac = 0.06f, SlideSeconds = 0.15f;
            public float ExitAt = float.MaxValue, ExitSeconds = 0.15f;
            public Color Fill, Edge;
            public bool FromLeft = true;

            protected override bool Alive(float now) => now < ExitAt + ExitSeconds;

            public override void Draw(Painter2D p, Vector2 size)
            {
                float t = m_Now - Start;
                if (t < 0f) return;
                float w = size.x, h = size.y;
                float side = FromLeft ? -1f : 1f;
                float slideIn = 1f - OutCubic(t / SlideSeconds);
                float slideOut = ExitAt < float.MaxValue ? Mathf.Clamp01((m_Now - ExitAt) / ExitSeconds) : 0f;
                float offset = side * w * 1.4f * slideIn - side * w * 1.4f * slideOut * slideOut;

                float y = h * CenterFrac, half = h * HeightFrac * 0.5f, skew = h * SkewFrac;
                Quad(p, Fill, offset, y - half, y + half, skew, w);
                float edge = Mathf.Max(2f, h * 0.008f);
                Quad(p, Edge, offset, y - half - edge * 2f, y - half - edge, skew, w);
                Quad(p, Edge, offset, y + half + edge, y + half + edge * 2f, skew, w);
            }

            private static void Quad(Painter2D p, Color color, float offset, float top, float bottom, float skew, float w)
            {
                p.fillColor = color;
                p.BeginPath();
                p.MoveTo(new Vector2(-w * 0.2f + offset + skew, top));
                p.LineTo(new Vector2(w * 1.2f + offset + skew, top));
                p.LineTo(new Vector2(w * 1.2f + offset - skew, bottom));
                p.LineTo(new Vector2(-w * 0.2f + offset - skew, bottom));
                p.ClosePath();
                p.Fill();
            }
        }

        /// <summary>Little diamonds bursting out from the middle.</summary>
        private sealed class SparksFx : CanvasFx
        {
            public float Seconds = 0.6f, SpeedFrac = 1.5f;
            public int Count = 30;
            public Color ColorA = Color.white, ColorB = Color.white, ColorC = Color.clear;
            private readonly System.Random m_Random;
            private Vector2[] m_Dirs;
            private float[] m_Speeds, m_Sizes, m_Spins;
            private int[] m_Colors;

            public SparksFx(System.Random random) { m_Random = random; }

            public override void Build(VisualElement parent, float h)
            {
                m_Dirs = new Vector2[Count];
                m_Speeds = new float[Count];
                m_Sizes = new float[Count];
                m_Spins = new float[Count];
                m_Colors = new int[Count];
                for (int i = 0; i < Count; i++)
                {
                    m_Dirs[i] = Dir((float)m_Random.NextDouble() * 360f);
                    m_Speeds[i] = Mathf.Lerp(0.35f, 1f, (float)m_Random.NextDouble());
                    m_Sizes[i] = Mathf.Lerp(0.006f, 0.016f, (float)m_Random.NextDouble());
                    m_Spins[i] = Mathf.Lerp(-720f, 720f, (float)m_Random.NextDouble());
                    m_Colors[i] = m_Random.Next(ColorC.a > 0f ? 3 : 2);
                }
            }

            protected override bool Alive(float now) => now < Start + Seconds;

            public override void Draw(Painter2D p, Vector2 size)
            {
                float t = m_Now - Start;
                if (t < 0f) return;
                float k = Mathf.Clamp01(t / Seconds);
                float h = size.y;
                Vector2 c = size * 0.5f;
                float travel = h * SpeedFrac * OutCubic(k) * 0.5f;
                for (int i = 0; i < Count; i++)
                {
                    Color color = m_Colors[i] == 0 ? ColorA : m_Colors[i] == 1 ? ColorB : ColorC;
                    Vector2 at = c + m_Dirs[i] * (h * 0.08f + travel * m_Speeds[i]) + Vector2.up * (h * 0.12f * k * k);
                    float r = h * m_Sizes[i] * (1f - k * 0.6f);
                    float spin = m_Spins[i] * t;
                    p.fillColor = new Color(color.r, color.g, color.b, color.a * (1f - k));
                    p.BeginPath();
                    p.MoveTo(at + Dir(spin) * r * 1.8f);
                    p.LineTo(at + Dir(spin + 90f) * r);
                    p.LineTo(at + Dir(spin + 180f) * r * 1.8f);
                    p.LineTo(at + Dir(spin + 270f) * r);
                    p.ClosePath();
                    p.Fill();
                }
            }
        }

        /// <summary>Draws every live <see cref="CanvasFx"/>, oldest first.</summary>
        private sealed class FxCanvas : VisualElement
        {
            private readonly List<Fx> m_Effects;

            public FxCanvas(List<Fx> effects)
            {
                m_Effects = effects;
                pickingMode = PickingMode.Ignore;
                generateVisualContent += Draw;
            }

            private void Draw(MeshGenerationContext context)
            {
                Painter2D painter = context.painter2D;
                Vector2 size = contentRect.size;
                if (size.x <= 0f || size.y <= 0f) return;
                foreach (Fx fx in m_Effects)
                {
                    if (fx is CanvasFx shape) shape.Draw(painter, size);
                }
            }
        }
    }
}
