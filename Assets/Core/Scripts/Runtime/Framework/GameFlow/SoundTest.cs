using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Single Player > Sound Test: a popup listing every song (<see cref="MusicCatalog.SoundTest"/>, in
    /// music_info.txt order) with a small player - Play / Pause, Stop, a progress bar and a "now playing" line.
    /// Songs that aren't in the Music folder yet are listed as "Coming soon"; picking one just logs that it's not
    /// implemented yet.
    ///
    ///   - While it's open, UI sounds are muted (<see cref="AudioVolumeService.UISoundsMuted"/>) and whatever music
    ///     was playing (the title song, an earlier pick) stops, so only the song you choose is heard. Songs play
    ///     through <see cref="MusicPlayer"/>, so they loop exactly as they do in game, at the Music volume.
    ///   - Leaving with a song playing keeps it playing through the menus (<see cref="MusicPlayer.HoldThroughMenus"/>)
    ///     until something else changes the music (starting a game, opening the Sound Test again, ...). Leaving
    ///     with nothing playing (stopped, or paused) starts the normal menu music - none yet, so silence.
    ///   - Controls: Up/Down choose a song; A/Enter (or clicking it) plays it - on the song that's already on, it
    ///     pauses / resumes. Down from the last song goes to the buttons; Left/Right move between them, Up goes back
    ///     to the list. B/Esc or Back closes (handled by the owner screen, see <see cref="SinglePlayerController"/>).
    ///   - The playing song gets a little animated equalizer (three bouncing bars) in the list and in the player;
    ///     paused, the bars stop low.
    /// </summary>
    public class SoundTest
    {
        // Rows are tall and labels get vertical padding: the game font draws past its line box, and the titles need
        // overflow: hidden (for the "..." on long names), which otherwise clips the bottoms of the letters.
        private const float RowHeight = 40f;
        private const float ListHeight = RowHeight * 8f;
        private const float PanelWidth = 640f;

        private static readonly Color Gold = new Color(1f, 0.82f, 0.25f);
        private static readonly Color RowText = new Color(0.9f, 0.9f, 0.9f);
        private static readonly Color DimText = new Color(0.45f, 0.45f, 0.45f);
        private static readonly Color SelectedRow = new Color(0.22f, 0.22f, 0.22f);
        private static readonly Color SelectedBorder = new Color(1f, 0.85f, 0.2f);
        private static readonly Color SelectedBorderUnfocused = new Color(0.55f, 0.55f, 0.55f);

        /// <summary>The song last selected, so the list reopens on it.</summary>
        private static int s_LastIndex;

        private readonly Action m_OnClose;
        private readonly VisualElement m_Overlay;
        private readonly ScrollView m_Scroll;
        private readonly VisualElement m_List;
        private readonly List<Row> m_Rows = new List<Row>();
        private readonly Equalizer m_PlayerIcon;
        private readonly Label m_NowPlaying;
        private readonly Label m_Time;
        private readonly VisualElement m_ProgressFill;
        private readonly Label m_Status;
        private readonly Button m_PlayButton;
        private readonly Button m_StopButton;
        private readonly Button m_BackButton;
        private IVisualElementScheduledItem m_Ticker;
        private int m_Selected;
        private float m_StatusUntil;

        private sealed class Row
        {
            public MusicCatalog.Listing Listing;
            public bool Available;
            public VisualElement Root;
            public Equalizer Icon;
            public Label Title;
        }

        public bool IsOpen => m_Overlay.style.display == DisplayStyle.Flex;

        public SoundTest(VisualElement root, Action onClose)
        {
            m_OnClose = onClose;

            m_Overlay = new VisualElement();
            m_Overlay.style.position = Position.Absolute;
            m_Overlay.style.left = 0;
            m_Overlay.style.right = 0;
            m_Overlay.style.top = 0;
            m_Overlay.style.bottom = 0;
            m_Overlay.style.backgroundColor = new Color(0f, 0f, 0f, 0.8f);
            m_Overlay.style.justifyContent = Justify.Center;
            m_Overlay.style.alignItems = Align.Center;
            m_Overlay.style.display = DisplayStyle.None;
            root.Add(m_Overlay);

            var panel = new VisualElement();
            panel.style.width = PanelWidth;
            panel.style.backgroundColor = new Color(0.09f, 0.09f, 0.09f);
            SetBorder(panel, 2f, new Color(0.35f, 0.35f, 0.35f));
            SetRadius(panel, 8f);
            panel.style.paddingLeft = 24;
            panel.style.paddingRight = 24;
            panel.style.paddingTop = 18;
            panel.style.paddingBottom = 18;
            panel.style.alignItems = Align.Stretch;
            m_Overlay.Add(panel);

            var title = new Label("SOUND TEST");
            title.style.fontSize = 26;
            title.style.color = Color.white;
            title.style.unityTextAlign = TextAnchor.MiddleCenter;
            title.style.marginBottom = 10;
            panel.Add(title);

            // ----- the list -----
            m_Scroll = new ScrollView(ScrollViewMode.Vertical);
            m_Scroll.style.height = ListHeight;
            m_Scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            m_Scroll.focusable = false;
            m_Scroll.style.backgroundColor = new Color(0.05f, 0.05f, 0.05f);
            SetRadius(m_Scroll, 4f);
            panel.Add(m_Scroll);

            m_List = new VisualElement { focusable = true };
            m_Scroll.Add(m_List);
            BuildRows();

            // ----- the player -----
            var player = new VisualElement();
            player.style.marginTop = 14;
            player.style.paddingLeft = 12;
            player.style.paddingRight = 12;
            player.style.paddingTop = 10;
            player.style.paddingBottom = 10;
            player.style.backgroundColor = new Color(0.14f, 0.14f, 0.14f);
            SetRadius(player, 6f);
            panel.Add(player);

            var nowRow = new VisualElement();
            nowRow.style.flexDirection = FlexDirection.Row;
            nowRow.style.alignItems = Align.Center;
            player.Add(nowRow);

            m_PlayerIcon = new Equalizer(18f, 16f);
            m_PlayerIcon.Root.style.marginRight = 10;
            nowRow.Add(m_PlayerIcon.Root);

            m_NowPlaying = new Label("Nothing playing");
            m_NowPlaying.style.fontSize = 18;
            PadText(m_NowPlaying);
            m_NowPlaying.style.color = Color.white;
            m_NowPlaying.style.flexGrow = 1;
            m_NowPlaying.style.overflow = Overflow.Hidden;
            m_NowPlaying.style.textOverflow = TextOverflow.Ellipsis;
            m_NowPlaying.style.whiteSpace = WhiteSpace.NoWrap;
            nowRow.Add(m_NowPlaying);

            m_Time = new Label("");
            m_Time.style.fontSize = 16;
            m_Time.style.color = new Color(0.8f, 0.8f, 0.8f);
            m_Time.style.marginLeft = 10;
            nowRow.Add(m_Time);

            var bar = new VisualElement();
            bar.style.height = 6;
            bar.style.marginTop = 8;
            bar.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            SetRadius(bar, 3f);
            player.Add(bar);

            m_ProgressFill = new VisualElement();
            m_ProgressFill.style.height = Length.Percent(100);
            m_ProgressFill.style.width = Length.Percent(0);
            m_ProgressFill.style.backgroundColor = Gold;
            SetRadius(m_ProgressFill, 3f);
            bar.Add(m_ProgressFill);

            m_Status = new Label("");
            m_Status.style.fontSize = 14;
            m_Status.style.color = new Color(1f, 0.6f, 0.5f);
            m_Status.style.unityTextAlign = TextAnchor.MiddleCenter;
            m_Status.style.minHeight = 20;
            m_Status.style.marginTop = 6;
            panel.Add(m_Status);

            // ----- buttons -----
            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.style.justifyContent = Justify.Center;
            panel.Add(buttons);

            m_PlayButton = MenuUI.MakeButton("Play", PlayPauseButton, null, 150f);
            m_StopButton = MenuUI.MakeButton("Stop", StopButton, null, 150f);
            m_BackButton = MenuUI.MakeButton("Back", () => m_OnClose?.Invoke(), null, 150f);
            m_PlayButton.style.marginRight = 10;
            m_StopButton.style.marginRight = 10;
            buttons.Add(m_PlayButton);
            buttons.Add(m_StopButton);
            buttons.Add(m_BackButton);

            // ----- navigation -----
            m_List.RegisterCallback<NavigationMoveEvent>(OnListMove, TrickleDown.TrickleDown);
            m_List.RegisterCallback<NavigationSubmitEvent>(evt =>
            {
                evt.StopPropagation();
                evt.PreventDefault();
                PlaySelected();
            }, TrickleDown.TrickleDown);
            m_List.RegisterCallback<FocusInEvent>(_ => Refresh());
            m_List.RegisterCallback<FocusOutEvent>(_ => Refresh());

            Button[] row = { m_PlayButton, m_StopButton, m_BackButton };
            for (int i = 0; i < row.Length; i++)
            {
                int index = i;
                row[i].RegisterCallback<NavigationMoveEvent>(evt =>
                {
                    switch (evt.direction)
                    {
                        case NavigationMoveEvent.Direction.Up:
                            m_List.Focus();
                            break;
                        case NavigationMoveEvent.Direction.Left:
                            if (index > 0) MenuUI.FocusFirst(row[index - 1]);
                            break;
                        case NavigationMoveEvent.Direction.Right:
                            if (index < row.Length - 1) MenuUI.FocusFirst(row[index + 1]);
                            break;
                        default:
                            break; // Down: nothing below
                    }
                    evt.StopPropagation();
                    evt.PreventDefault();
                }, TrickleDown.TrickleDown);
            }
        }

        private void BuildRows()
        {
            MusicCatalog catalog = MusicCatalog.Load();
            if (catalog == null) return;

            foreach (MusicCatalog.Listing listing in catalog.SoundTest)
            {
                if (listing == null) continue;
                var r = new Row
                {
                    Listing = listing,
                    Available = MusicCatalog.Get(listing.id) != null
                };

                r.Root = new VisualElement();
                r.Root.style.flexDirection = FlexDirection.Row;
                r.Root.style.alignItems = Align.Center;
                r.Root.style.height = RowHeight;
                r.Root.style.paddingLeft = 8;
                r.Root.style.paddingRight = 10;
                SetBorder(r.Root, 2f, Color.clear);
                SetRadius(r.Root, 4f);

                r.Icon = new Equalizer(14f, 14f);
                r.Icon.Root.style.marginRight = 8;
                r.Root.Add(r.Icon.Root);

                var number = new Label(listing.number ?? "");
                number.style.width = 34;
                number.style.fontSize = 15;
                number.style.color = DimText;
                PadText(number);
                r.Root.Add(number);

                r.Title = new Label(listing.title ?? listing.id);
                r.Title.style.flexGrow = 1;
                r.Title.style.flexShrink = 1;
                r.Title.style.fontSize = 16;
                PadText(r.Title);
                r.Title.style.overflow = Overflow.Hidden;
                r.Title.style.textOverflow = TextOverflow.Ellipsis;
                r.Title.style.whiteSpace = WhiteSpace.NoWrap;
                r.Root.Add(r.Title);

                if (!r.Available)
                {
                    var soon = new Label("Coming soon");
                    soon.style.fontSize = 14;
                    PadText(soon);
                    soon.style.color = DimText;
                    soon.style.marginLeft = 8;
                    r.Root.Add(soon);
                }

                int index = m_Rows.Count;
                r.Root.RegisterCallback<ClickEvent>(_ =>
                {
                    Select(index);
                    m_List.Focus();
                    PlaySelected();
                });

                m_List.Add(r.Root);
                m_Rows.Add(r);
            }
        }

        // ----- open / close -----

        public void Open()
        {
            AudioVolumeService.UISoundsMuted = true;
            MusicPlayer.Stop(0.3f); // the title song, an earlier pick, menu music: only your pick plays in here
            m_Status.text = "";
            m_Overlay.style.display = DisplayStyle.Flex;
            Select(Mathf.Clamp(s_LastIndex, 0, Mathf.Max(0, m_Rows.Count - 1)));
            m_List.Focus();
            m_Ticker ??= m_Overlay.schedule.Execute(Tick).Every(33);
            m_Ticker.Resume();
            Refresh();
        }

        /// <summary>Hides the popup and hands the music back to the menus (see the class summary).</summary>
        public void Close()
        {
            if (!IsOpen) return;
            if (MusicPlayer.IsPlaying)
            {
                MusicPlayer.HoldThroughMenus();
            }
            else
            {
                MusicPlayer.Stop(0f); // a paused song counts as stopped
                MusicPlayer.PlayMenuMusic(0f);
            }
            AudioVolumeService.UISoundsMuted = false;
            m_Ticker?.Pause();
            m_Overlay.style.display = DisplayStyle.None;
        }

        // ----- playing -----

        private string SelectedId => m_Selected >= 0 && m_Selected < m_Rows.Count ? m_Rows[m_Selected].Listing.id : null;

        /// <summary>A/Enter or a click: play the selected song; on the song that's already on, pause / resume.</summary>
        private void PlaySelected()
        {
            if (m_Selected < 0 || m_Selected >= m_Rows.Count) return;
            Row row = m_Rows[m_Selected];
            if (!row.Available)
            {
                Debug.Log($"[Sound Test] \"{row.Listing.title}\" ({row.Listing.id}) is not implemented yet.");
                ShowStatus(Loc.T("Not implemented yet"));
                return;
            }

            if (IsCurrent(row.Listing.id))
            {
                if (MusicPlayer.IsPaused) MusicPlayer.Resume();
                else if (MusicPlayer.IsPlaying) MusicPlayer.Pause();
                else MusicPlayer.Play(row.Listing.id, 0.2f);
            }
            else
            {
                MusicPlayer.Play(row.Listing.id, 0.2f);
            }
            Refresh();
        }

        private void PlayPauseButton()
        {
            if (MusicPlayer.IsPlaying) MusicPlayer.Pause();
            else if (MusicPlayer.IsPaused) MusicPlayer.Resume();
            else PlaySelected();
            Refresh();
        }

        private void StopButton()
        {
            MusicPlayer.Stop(0.15f);
            Refresh();
        }

        private static bool IsCurrent(string id) =>
            !string.IsNullOrEmpty(id) && string.Equals(MusicPlayer.CurrentId, id, StringComparison.OrdinalIgnoreCase);

        private void ShowStatus(string text)
        {
            m_Status.text = text;
            m_StatusUntil = Time.unscaledTime + 2.5f;
        }

        // ----- list navigation -----

        private void OnListMove(NavigationMoveEvent evt)
        {
            evt.StopPropagation();
            evt.PreventDefault();
            switch (evt.direction)
            {
                case NavigationMoveEvent.Direction.Up:
                    if (m_Selected > 0) Select(m_Selected - 1);
                    break;
                case NavigationMoveEvent.Direction.Down:
                    if (m_Selected < m_Rows.Count - 1) Select(m_Selected + 1);
                    else MenuUI.FocusFirst(m_PlayButton);
                    break;
            }
        }

        private void Select(int index)
        {
            m_Selected = Mathf.Clamp(index, 0, Mathf.Max(0, m_Rows.Count - 1));
            s_LastIndex = m_Selected;
            ScrollIntoView(m_Selected);
            Refresh();
        }

        /// <summary>Vertical-only scrolling to keep the selected row visible (ScrollView.ScrollTo also scrolls sideways).</summary>
        private void ScrollIntoView(int index)
        {
            float view = m_Scroll.contentViewport.layout.height;
            if (float.IsNaN(view) || view <= 0f) view = ListHeight;
            float top = index * RowHeight;
            float offset = m_Scroll.scrollOffset.y;
            if (top < offset) offset = top;
            else if (top + RowHeight > offset + view) offset = top + RowHeight - view;
            m_Scroll.scrollOffset = new Vector2(0f, Mathf.Max(0f, offset));
        }

        // ----- display -----

        private void Tick()
        {
            if (!IsOpen) return;
            if (m_StatusUntil > 0f && Time.unscaledTime > m_StatusUntil)
            {
                m_Status.text = "";
                m_StatusUntil = 0f;
            }
            Refresh();
        }

        private void Refresh()
        {
            string current = MusicPlayer.CurrentId;
            bool playing = MusicPlayer.IsPlaying;
            bool paused = MusicPlayer.IsPaused;
            bool listFocused = m_List.focusController != null && m_List.focusController.focusedElement == m_List;
            float t = Time.unscaledTime;

            for (int i = 0; i < m_Rows.Count; i++)
            {
                Row row = m_Rows[i];
                bool selected = i == m_Selected;
                bool isCurrent = (playing || paused) && IsCurrent(row.Listing.id);

                row.Root.style.backgroundColor = selected ? SelectedRow : Color.clear;
                SetBorderColor(row.Root, selected ? (listFocused ? SelectedBorder : SelectedBorderUnfocused) : Color.clear);
                row.Title.style.color = !row.Available ? DimText : isCurrent ? Gold : RowText;
                row.Icon.Show(isCurrent ? (playing ? Equalizer.State.Playing : Equalizer.State.Paused) : Equalizer.State.Hidden, t);
            }

            Row now = (playing || paused) ? m_Rows.Find(r => IsCurrent(r.Listing.id)) : null;
            // Set only when it changes, and already translated (Loc.T): this runs ~30 times a second.
            SetText(m_NowPlaying, now != null ? now.Listing.title : (playing || paused) && current != null ? current.Replace('_', ' ') : Loc.T("Nothing playing"));
            m_PlayerIcon.Show(playing ? Equalizer.State.Playing : paused ? Equalizer.State.Paused : Equalizer.State.Hidden, t);
            SetText(m_PlayButton, Loc.T(playing ? "Pause" : "Play"));

            if ((playing || paused) && MusicPlayer.TryGetProgress(out float seconds, out float length) && length > 0f)
            {
                SetText(m_Time, $"{FormatTime(seconds)} / {FormatTime(length)}");
                m_ProgressFill.style.width = Length.Percent(Mathf.Clamp01(seconds / length) * 100f);
            }
            else
            {
                SetText(m_Time, "");
                m_ProgressFill.style.width = Length.Percent(0);
            }
        }

        /// <summary>Room above and below the text so the font's tall glyphs aren't cut off.</summary>
        private static void PadText(VisualElement label)
        {
            label.style.paddingTop = 3;
            label.style.paddingBottom = 5;
            label.style.unityTextAlign = TextAnchor.MiddleLeft;
        }

        private static void SetText(TextElement element, string text)
        {
            if (element.text != text) element.text = text;
        }

        private static string FormatTime(float seconds)
        {
            int s = Mathf.Max(0, Mathf.FloorToInt(seconds));
            return $"{s / 60}:{s % 60:00}";
        }

        private static void SetBorder(VisualElement element, float width, Color color)
        {
            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
            SetBorderColor(element, color);
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        /// <summary>The "now playing" icon: three gold bars that bounce while playing and sit low while paused.</summary>
        private sealed class Equalizer
        {
            public enum State { Hidden, Paused, Playing }

            private static readonly float[] Speeds = { 9.1f, 12.7f, 7.3f };
            private static readonly float[] Phases = { 0f, 1.9f, 4.1f };

            public readonly VisualElement Root;
            private readonly VisualElement[] m_Bars = new VisualElement[3];
            private readonly float m_Height;

            public Equalizer(float width, float height)
            {
                m_Height = height;
                Root = new VisualElement();
                Root.style.width = width;
                Root.style.height = height;
                Root.style.flexShrink = 0;
                Root.style.flexDirection = FlexDirection.Row;
                Root.style.alignItems = Align.FlexEnd;
                Root.style.justifyContent = Justify.SpaceBetween;
                float barWidth = Mathf.Max(2f, Mathf.Round(width / 4f));
                for (int i = 0; i < m_Bars.Length; i++)
                {
                    var bar = new VisualElement { pickingMode = PickingMode.Ignore };
                    bar.style.width = barWidth;
                    bar.style.backgroundColor = Gold;
                    bar.style.borderTopLeftRadius = 1;
                    bar.style.borderTopRightRadius = 1;
                    Root.Add(bar);
                    m_Bars[i] = bar;
                }
                Root.pickingMode = PickingMode.Ignore;
                Show(State.Hidden, 0f);
            }

            public void Show(State state, float time)
            {
                Root.style.visibility = state == State.Hidden ? Visibility.Hidden : Visibility.Visible;
                for (int i = 0; i < m_Bars.Length; i++)
                {
                    float fraction = state == State.Playing
                        ? 0.25f + 0.75f * Mathf.Abs(Mathf.Sin(time * Speeds[i] + Phases[i]))
                        : 0.25f;
                    m_Bars[i].style.height = Mathf.Max(2f, fraction * m_Height);
                    m_Bars[i].style.opacity = state == State.Paused ? 0.6f : 1f;
                }
            }
        }
    }
}
