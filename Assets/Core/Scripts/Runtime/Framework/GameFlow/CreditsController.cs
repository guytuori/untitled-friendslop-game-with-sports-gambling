using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The Credits scene (Single Player > Credits, added 2026-10-30), timed to the Credits song (MusicPlayer starts
    /// it when the scene loads - about 63 s, plays once):
    ///
    ///   - Black screen, the Parkour Predictions logo centred. After <see cref="logoHoldSeconds"/> everything starts
    ///     scrolling up at one steady speed, so the logo drifts up and off the top.
    ///   - The credits come from <see cref="creditsText"/> (Assets/Core/Scenes/Credits/credits.txt): sections
    ///     separated by a line of "---", first line of each a heading. Each section scrolls in from below the
    ///     screen and out the top; the last one ("Thanks for playing!") stops in the middle of the screen
    ///     exactly at <see cref="finalStopSeconds"/> into the song.
    ///   - The sections are spread evenly between the logo and the last one (<see cref="Plan"/>). With more names
    ///     the gaps shrink; if they'd get tighter than <see cref="MinGapScreens"/> the scroll speeds up instead, so
    ///     the last section still lands on time however long the file gets.
    ///   - The clock follows the song's own playback position (MusicPlayer.TryGetProgress), so the timing holds
    ///     even if the song takes a moment to load. Without the song it runs on its own after a short wait.
    ///   - Any button (keyboard, mouse, gamepad - not stick movement) brings up "Return to Menu?" Yes / No.
    ///     B / Esc on it = No.
    ///   - At <see cref="returnSeconds"/> (12 s after the song ends) it goes back to the Single Player menu by itself
    ///     - unless the question is up, then it waits for the answer.
    /// All text is white, in the game font. The scene: a UIDocument with this, an EventSystem and an AudioListener.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class CreditsController : MonoBehaviour
    {
        [Tooltip("The logo shown first (Parkour Predictions Logo Only.png).")]
        [SerializeField] private Texture2D logo;

        [Tooltip("The credits (Assets/Core/Scenes/Credits/credits.txt). Sections are separated by a line of ---; the first line of a section is its heading.")]
        [SerializeField] private TextAsset creditsText;

        [Tooltip("Where to go afterwards.")]
        [SerializeField] private string returnSceneName = "SinglePlayer";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        [Header("Timing (seconds into the Credits song)")]
        [Tooltip("How long the logo sits in the middle before the scroll starts.")]
        [SerializeField] private float logoHoldSeconds = 3f;

        [Tooltip("When the last section (Thanks for playing!) stops in the middle of the screen.")]
        [SerializeField] private float finalStopSeconds = 60f;

        [Tooltip("When the credits go back to the menu by themselves.")]
        [SerializeField] private float returnSeconds = 75f;

        /// <summary>Normal scroll speed, in screen heights per second (sped up if the credits get long).</summary>
        private const float BaseSpeedScreens = 0.1f;

        /// <summary>Smallest / largest gap between sections, in screen heights.</summary>
        private const float MinGapScreens = 0.12f;
        private const float MaxGapScreens = 0.6f;

        /// <summary>How far below the screen the first section starts (screen heights).</summary>
        private const float EntryMarginScreens = 0.06f;

        /// <summary>Logo width, as a fraction of the screen width.</summary>
        private const float LogoWidth = 0.55f;

        /// <summary>No song after this long: start anyway.</summary>
        private const float MusicWaitSeconds = 2.5f;

        private const float InputDelaySeconds = 0.6f;
        private const float PopupInputDelaySeconds = 0.25f;

        /// <summary>Set when the credits hand back to the Single Player menu, so it can focus the Credits button.</summary>
        public static bool ReturningToMenu { get; set; }

        private VisualElement m_Root;
        private Image m_Logo;
        private readonly List<VisualElement> m_Sections = new List<VisualElement>();
        private VisualElement m_Popup;
        private Button m_YesButton;

        private float m_SceneStart;
        private bool m_ClockStarted;
        private float m_Clock;
        private bool m_PopupOpen;
        private float m_PopupOpenedTime;
        private float m_InputBlockedUntil;
        private bool m_Leaving;

        // The layout plan (see Plan), worked out once everything has a size, and again if the screen changes.
        private bool m_Planned;
        private float m_PlannedHeight, m_PlannedWidth;
        private float m_Speed;                 // px per second
        private readonly List<float> m_Tops = new List<float>(); // each section's top, px below the logo's centre at scroll 0

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            m_Root = GetComponent<UIDocument>().rootVisualElement;
            m_Root.style.flexGrow = 1;
            m_Root.style.backgroundColor = Color.black;
            m_Root.style.overflow = Overflow.Hidden;

            m_Logo = new Image { image = logo, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            m_Logo.style.position = Position.Absolute;
            m_Root.Add(m_Logo);

            List<List<string>> sections = ParseSections(creditsText != null ? creditsText.text : "Thanks for playing!");
            for (int i = 0; i < sections.Count; i++)
            {
                VisualElement section = BuildSection(sections[i], i == sections.Count - 1);
                section.style.visibility = Visibility.Hidden; // until it has a size and a place
                m_Root.Add(section);
                m_Sections.Add(section);
            }

            BuildPopup();
            m_Root.RegisterCallback<NavigationCancelEvent>(OnCancel);
            GameFont.Apply(m_Root);

            m_SceneStart = Time.unscaledTime;
            m_InputBlockedUntil = Time.unscaledTime + InputDelaySeconds;
        }

        // =====================================================================================
        // Text
        // =====================================================================================

        /// <summary>The file's sections (split on lines of ---), each a list of non-empty lines.</summary>
        private static List<List<string>> ParseSections(string text)
        {
            var sections = new List<List<string>>();
            var current = new List<string>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line == "---")
                {
                    if (current.Count > 0) sections.Add(current);
                    current = new List<string>();
                }
                else if (line.Length > 0)
                {
                    current.Add(line);
                }
            }
            if (current.Count > 0) sections.Add(current);
            return sections;
        }

        private static VisualElement BuildSection(List<string> lines, bool final)
        {
            var section = new VisualElement { pickingMode = PickingMode.Ignore };
            section.style.position = Position.Absolute;
            section.style.left = 0;
            section.style.right = 0;
            section.style.alignItems = Align.Center;

            for (int i = 0; i < lines.Count; i++)
            {
                bool heading = i == 0 && lines.Count > 1;
                var label = new Label(lines[i]) { pickingMode = PickingMode.Ignore };
                label.style.color = Color.white;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.fontSize = final ? 60 : heading ? 38 : 30;
                if (heading || final) label.style.unityFontStyleAndWeight = FontStyle.Bold;
                label.style.marginBottom = heading ? 14 : 6;
                section.Add(label);
            }
            return section;
        }

        // =====================================================================================
        // Timing and layout
        // =====================================================================================

        private void Update()
        {
            UpdateClock();
            Layout();

            if (m_Leaving) return;

            if (!m_PopupOpen && Time.unscaledTime >= m_InputBlockedUntil && AnyButtonPressed()) OpenPopup();

            if (!m_PopupOpen && m_ClockStarted && m_Clock >= returnSeconds) Leave();
        }

        /// <summary>Follows the Credits song's position; runs on its own before the song starts (or without it) and after it ends.</summary>
        private void UpdateClock()
        {
            float song = 0f;
            bool songPlaying = MusicPlayer.CurrentId == MusicTracks.Credits && MusicPlayer.TryGetProgress(out song, out _);

            if (!m_ClockStarted)
            {
                if ((songPlaying && song > 0f) || Time.unscaledTime - m_SceneStart > MusicWaitSeconds)
                {
                    m_ClockStarted = true;
                    m_Clock = songPlaying ? song : 0f;
                }
                return;
            }

            m_Clock += Time.unscaledDeltaTime;
            // Ease towards the song's own position (it moves in audio-buffer steps), snapping if far off.
            if (songPlaying && song > 0f)
            {
                float error = song - m_Clock;
                if (Mathf.Abs(error) > 0.25f) m_Clock = song;
                else m_Clock += error * Mathf.Min(1f, Time.unscaledDeltaTime * 4f);
            }
        }

        /// <summary>
        /// Works out the scroll: the logo's centre is the reference (0); the first section starts just below the
        /// screen, the last one's centre must reach the screen's centre at finalStopSeconds, and the sections in
        /// between are spaced evenly. Normal speed is BaseSpeedScreens; the gaps fill the time (up to
        /// MaxGapScreens), and if even MinGapScreens doesn't fit, the speed goes up instead.
        /// </summary>
        private bool Plan()
        {
            float height = m_Root.layout.height;
            float width = m_Root.layout.width;
            if (float.IsNaN(height) || height <= 1f || float.IsNaN(width)) return false;

            // Logo size from the screen width.
            float aspect = logo != null && logo.width > 0 ? logo.height / (float)logo.width : 0.45f;
            float logoW = Mathf.Min(width * LogoWidth, height * 0.7f / Mathf.Max(0.01f, aspect));
            m_Logo.style.width = logoW;
            m_Logo.style.height = logoW * aspect;
            m_Logo.style.left = (width - logoW) * 0.5f;

            var heights = new List<float>();
            foreach (VisualElement section in m_Sections)
            {
                float h = section.layout.height;
                if (float.IsNaN(h) || h <= 0f) return false; // not laid out yet
                heights.Add(h);
            }

            int count = m_Sections.Count;
            float scrollTime = Mathf.Max(1f, finalStopSeconds - logoHoldSeconds);
            float firstTop = height * (0.5f + EntryMarginScreens);
            float finalHeight = count > 0 ? heights[count - 1] : 0f;
            int gaps = Mathf.Max(0, count - 1);       // one after each normal section (the last one before the final)
            float normalTotal = 0f;
            for (int i = 0; i < count - 1; i++) normalTotal += heights[i];

            // Where the final section's top has to be for its centre to be mid-screen at the end of the scroll.
            float speed = BaseSpeedScreens * height;
            float finalTop = speed * scrollTime - finalHeight * 0.5f;
            float gap = gaps > 0 ? (finalTop - firstTop - normalTotal) / gaps : 0f;

            if (gaps > 0 && gap < MinGapScreens * height) gap = MinGapScreens * height;   // long credits: speed up
            else if (gaps > 0 && gap > MaxGapScreens * height) gap = MaxGapScreens * height; // short credits: slow down
            if (gaps > 0) finalTop = firstTop + normalTotal + gaps * gap;
            else finalTop = Mathf.Max(finalTop, firstTop);
            speed = (finalTop + finalHeight * 0.5f) / scrollTime;

            m_Tops.Clear();
            float top = firstTop;
            for (int i = 0; i < count; i++)
            {
                if (i == count - 1) top = finalTop;
                m_Tops.Add(top);
                top += heights[i] + gap;
            }

            m_Speed = speed;
            m_PlannedHeight = height;
            m_PlannedWidth = width;
            return true;
        }

        private void Layout()
        {
            float height = m_Root.layout.height;
            float width = m_Root.layout.width;
            if (!m_Planned || !Mathf.Approximately(height, m_PlannedHeight) || !Mathf.Approximately(width, m_PlannedWidth))
            {
                m_Planned = Plan();
                if (!m_Planned) return;
            }

            float t = m_ClockStarted ? m_Clock : 0f;
            float scroll = m_Speed * Mathf.Clamp(t - logoHoldSeconds, 0f, finalStopSeconds - logoHoldSeconds);
            float centre = height * 0.5f - scroll; // screen y of the reference point (the logo's centre at scroll 0)

            float logoHeight = m_Logo.resolvedStyle.height;
            m_Logo.style.top = centre - (float.IsNaN(logoHeight) ? 0f : logoHeight * 0.5f);

            for (int i = 0; i < m_Sections.Count; i++)
            {
                VisualElement section = m_Sections[i];
                float y = centre + m_Tops[i];
                section.style.top = y;
                bool onScreen = y < height && y + section.layout.height > 0f;
                section.style.visibility = onScreen ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // =====================================================================================
        // Input and the Return to Menu? question
        // =====================================================================================

        /// <summary>Any button on a keyboard, mouse or gamepad went down this frame (stick movement doesn't count).</summary>
        private static bool AnyButtonPressed()
        {
            foreach (InputDevice device in InputSystem.devices)
            {
                if (!device.enabled || !(device is Keyboard || device is Mouse || device is Gamepad || device is Joystick)) continue;
                foreach (InputControl control in device.allControls)
                {
                    if (!(control is ButtonControl button) || control.synthetic) continue;
                    if (control.parent is StickControl) continue; // stick directions (and drift) aren't buttons
                    if (button.wasPressedThisFrame) return true;
                }
            }
            return false;
        }

        private void BuildPopup()
        {
            m_Popup = new VisualElement();
            m_Popup.style.position = Position.Absolute;
            m_Popup.style.left = m_Popup.style.right = m_Popup.style.top = m_Popup.style.bottom = 0;
            m_Popup.style.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
            m_Popup.style.alignItems = Align.Center;
            m_Popup.style.justifyContent = Justify.Center;
            m_Popup.style.display = DisplayStyle.None;

            var panel = new VisualElement();
            panel.style.backgroundColor = new Color(0.08f, 0.08f, 0.08f, 0.95f);
            panel.style.paddingLeft = panel.style.paddingRight = 40;
            panel.style.paddingTop = panel.style.paddingBottom = 28;
            panel.style.alignItems = Align.Center;
            panel.style.borderTopLeftRadius = panel.style.borderTopRightRadius = 10;
            panel.style.borderBottomLeftRadius = panel.style.borderBottomRightRadius = 10;
            m_Popup.Add(panel);

            var question = new Label("Return to Menu?");
            question.style.color = Color.white;
            question.style.fontSize = 28;
            question.style.marginBottom = 16;
            panel.Add(question);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row;
            panel.Add(buttons);

            m_YesButton = MenuUI.MakeButton("Yes", OnYes, selectSound, 160f);
            m_YesButton.style.marginRight = 16;
            buttons.Add(m_YesButton);
            buttons.Add(MenuUI.MakeButton("No", OnNo, cancelSound, 160f));

            m_Root.Add(m_Popup);
        }

        private void OpenPopup()
        {
            m_PopupOpen = true;
            m_PopupOpenedTime = Time.unscaledTime;
            m_Popup.style.display = DisplayStyle.Flex;
            m_Popup.BringToFront();
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            MenuUI.FocusFirst(m_YesButton);
        }

        private void ClosePopup()
        {
            m_PopupOpen = false;
            m_Popup.style.display = DisplayStyle.None;
            m_InputBlockedUntil = Time.unscaledTime + PopupInputDelaySeconds; // the press that closed it doesn't reopen it
            m_Root.Focus();
            if (m_ClockStarted && m_Clock >= returnSeconds) Leave(); // said No after the credits ended
        }

        /// <summary>The press that opened the question doesn't answer it.</summary>
        private bool PopupReady => m_PopupOpen && Time.unscaledTime - m_PopupOpenedTime >= PopupInputDelaySeconds;

        private void OnYes()
        {
            if (!PopupReady) return;
            Leave();
        }

        private void OnNo()
        {
            if (!PopupReady) return;
            ClosePopup();
        }

        private void OnCancel(NavigationCancelEvent evt)
        {
            evt.StopPropagation();
            if (!PopupReady) return;
            MenuUI.PlaySound(cancelSound);
            ClosePopup();
        }

        private void Leave()
        {
            if (m_Leaving) return;
            m_Leaving = true;
            ReturningToMenu = true;
            MenuTransition.LoadScene(returnSceneName);
        }
    }
}
