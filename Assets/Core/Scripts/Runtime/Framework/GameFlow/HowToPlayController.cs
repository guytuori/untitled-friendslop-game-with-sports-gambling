using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The How To Play scene (Single Player > How To Play, added 2026-11-03): a slideshow of pictures with a text box
    /// at the top, to Preliminary_Parkour (MusicPlayer starts it when the scene loads).
    ///
    /// SLIDES come from <see cref="slidesText"/> (Assets/Core/Scenes/HowToPlay/how_to_play.txt):
    ///   3---GAMEPAD          a header: the slide's number, "---", and optionally GAMEPAD or KEYBOARD
    ///   how_to_play3.png     the picture (a file in <see cref="images"/>, by name)
    ///   To move press ...    the text, one line per paragraph
    /// A slide with a GAMEPAD and a KEYBOARD version shows whichever the player last used (keyboard / mouse or a
    /// gamepad - it switches live). If both versions of a slide carry the same tag, the second is taken as the
    /// keyboard one (with a warning).
    ///
    /// CONTROLS in the text: a name in brackets - [Jump], [Buy Yes], [Movement Stick], ... (the Change Keybindings
    /// rows' names) - is replaced by what that control is bound to right now, e.g. [Space] or [B], shown in bold.
    /// Names it doesn't know are left as written.
    ///
    /// INPUT: Open Menu (the Menu action - Esc / START by default, rebindable) brings up "Return to Menu?" Yes / No;
    /// pressing it again (or B / Esc) answers No. Any other button - keyboard, mouse or gamepad, not stick movement -
    /// goes to the next slide, and on the last one back to the Single Player menu (which then focuses How To Play).
    ///
    /// LOOK: the picture fills the screen (kept in proportion, black around it); the text box uses the wager
    /// ticker's look (white, gold border, black text) - wider, and growing to fit the lines. The scene: a UIDocument
    /// with this, an EventSystem and an AudioListener (a copy of the Credits scene).
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public class HowToPlayController : MonoBehaviour
    {
        [Tooltip("The slides (Assets/Core/Scenes/HowToPlay/how_to_play.txt) - see the class summary for the format.")]
        [SerializeField] private TextAsset slidesText;

        [Tooltip("The slides' pictures, found by file name. Filled in from the text file's folder whenever this scene is edited - save the scene after adding pictures.")]
        [SerializeField] private Texture2D[] images = new Texture2D[0];

        [Tooltip("Where to go afterwards.")]
        [SerializeField] private string returnSceneName = "SinglePlayer";

        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip cancelSound;

        private const float BoxWidth = 1500f;
        private const float BoxTop = 36f;
        private const int FontSize = 28;
        private const int SmallFontSize = 24; // slides with lots of lines
        private const int ManyLines = 5;

        private const float InputDelaySeconds = 0.6f;
        private const float SlideInputDelaySeconds = 0.12f;
        private const float PopupInputDelaySeconds = 0.25f;

        private static readonly Color BoxColor = new Color(1f, 1f, 1f, 0.95f);
        private static readonly Color BoxBorder = new Color(1f, 0.8f, 0.2f);
        private static readonly Regex Header = new Regex(@"^(\d+)\s*-{3,}\s*([A-Za-z]*)\s*$");
        private static readonly Regex ControlTag = new Regex(@"\[([^\[\]]+)\]");

        /// <summary>Set when the slideshow hands back to the Single Player menu, so it can focus the How To Play button.</summary>
        public static bool ReturningToMenu { get; set; }

        private sealed class Slide
        {
            public int Number;
            public string GamepadImage, KeyboardImage;
            public List<string> GamepadText, KeyboardText;
        }

        private sealed class Variant
        {
            public int Number;
            public string Tag = "";
            public string Image;
            public readonly List<string> Lines = new List<string>();
        }

        private readonly List<Slide> m_Slides = new List<Slide>();
        private int m_Index;
        private bool m_ShownGamepad;

        private VisualElement m_Root;
        private Image m_Picture;
        private VisualElement m_Box;
        private Label m_Page;
        private VisualElement m_Popup;
        private Button m_YesButton;

        private bool m_PopupOpen;
        private float m_PopupOpenedTime;
        private float m_InputBlockedUntil;
        private bool m_Leaving;

        private GameplayInputSystem_Actions m_Actions;
        private InputAction m_MenuAction;
        private InputBindingsData m_Bindings;

        // =====================================================================================
        // Setup
        // =====================================================================================

        private void Awake()
        {
            GamepadUIBindingFix.Apply();

            // The player's own bindings: the Menu action (to open the question) and the control names in the text.
            m_Bindings = InputBindingsStore.Load();
            m_Actions = new GameplayInputSystem_Actions();
            InputBindingOverridesStore.ApplySavedOverrides(m_Actions.asset);
            m_MenuAction = m_Actions.Player.Menu;
            m_MenuAction.Enable();

            m_Root = GetComponent<UIDocument>().rootVisualElement;
            m_Root.style.flexGrow = 1;
            m_Root.style.backgroundColor = Color.black;
            m_Root.style.overflow = Overflow.Hidden;

            m_Picture = new Image { scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            m_Picture.style.position = Position.Absolute;
            m_Picture.style.left = m_Picture.style.right = m_Picture.style.top = m_Picture.style.bottom = 0;
            m_Root.Add(m_Picture);

            BuildBox();
            BuildPage();
            BuildPopup();
            m_Root.RegisterCallback<NavigationCancelEvent>(OnCancel);

            ParseSlides(slidesText != null ? slidesText.text : "");
            if (m_Slides.Count == 0) Debug.LogWarning("[How To Play] No slides - check how_to_play.txt.", this);

            m_ShownGamepad = UsingGamepad();
            ShowSlide(0);
            GameFont.Apply(m_Root);

            m_InputBlockedUntil = Time.unscaledTime + InputDelaySeconds;
        }

        private void OnDestroy()
        {
            m_MenuAction?.Disable();
            m_Actions?.Dispose();
        }

        private void BuildBox()
        {
            // The wager ticker's current row, wider and as tall as the text needs.
            m_Box = new VisualElement { pickingMode = PickingMode.Ignore };
            m_Box.style.position = Position.Absolute;
            m_Box.style.top = BoxTop;
            m_Box.style.left = Length.Percent(50);
            m_Box.style.width = BoxWidth;
            m_Box.style.maxWidth = Length.Percent(92);
            m_Box.style.translate = new Translate(Length.Percent(-50), 0);
            m_Box.style.flexDirection = FlexDirection.Column;
            m_Box.style.paddingLeft = m_Box.style.paddingRight = 28;
            m_Box.style.paddingTop = m_Box.style.paddingBottom = 16;
            m_Box.style.backgroundColor = BoxColor;
            m_Box.style.borderTopWidth = m_Box.style.borderBottomWidth = m_Box.style.borderLeftWidth = m_Box.style.borderRightWidth = 3;
            m_Box.style.borderTopColor = m_Box.style.borderBottomColor = m_Box.style.borderLeftColor = m_Box.style.borderRightColor = BoxBorder;
            m_Root.Add(m_Box);
        }

        private void BuildPage()
        {
            m_Page = new Label("") { pickingMode = PickingMode.Ignore };
            m_Page.style.position = Position.Absolute;
            m_Page.style.right = 28;
            m_Page.style.bottom = 20;
            m_Page.style.color = new Color(1f, 1f, 1f, 0.85f);
            m_Page.style.fontSize = 22;
            m_Page.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
            m_Page.style.paddingLeft = m_Page.style.paddingRight = 12;
            m_Page.style.paddingTop = m_Page.style.paddingBottom = 4;
            m_Page.style.borderTopLeftRadius = m_Page.style.borderTopRightRadius = 8;
            m_Page.style.borderBottomLeftRadius = m_Page.style.borderBottomRightRadius = 8;
            m_Root.Add(m_Page);
        }

        // =====================================================================================
        // Slides
        // =====================================================================================

        private void ParseSlides(string text)
        {
            var variants = new List<Variant>();
            Variant current = null;
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                Match header = Header.Match(line);
                if (header.Success)
                {
                    current = new Variant { Number = int.Parse(header.Groups[1].Value), Tag = header.Groups[2].Value.ToUpperInvariant() };
                    variants.Add(current);
                    continue;
                }
                if (current == null) continue; // anything before the first header

                if (current.Image == null && current.Lines.Count == 0 && IsImageName(line)) current.Image = StripExtension(line);
                else current.Lines.Add(line);
            }

            // Group the versions by slide number, in the order the slides first appear.
            var order = new List<int>();
            var byNumber = new Dictionary<int, List<Variant>>();
            foreach (Variant v in variants)
            {
                if (!byNumber.TryGetValue(v.Number, out List<Variant> list))
                {
                    list = new List<Variant>();
                    byNumber[v.Number] = list;
                    order.Add(v.Number);
                }
                list.Add(v);
            }

            foreach (int number in order)
            {
                List<Variant> list = byNumber[number];
                Variant pad = list.Find(v => IsGamepadTag(v.Tag));
                Variant keys = list.Find(v => IsKeyboardTag(v.Tag));
                Variant plain = list.Find(v => v.Tag.Length == 0);

                if (keys == null && list.Count > 1 && pad != null)
                {
                    // e.g. two "3---GAMEPAD" headers: the second one is the keyboard version.
                    Variant other = list.Find(v => v != pad);
                    if (other != null && IsGamepadTag(other.Tag))
                    {
                        Debug.LogWarning($"[How To Play] Slide {number} has two GAMEPAD versions - showing the second one to keyboard players. Change its header to {number}---KEYBOARD.", this);
                    }
                    keys = other;
                }

                pad ??= plain ?? keys ?? list[0];
                keys ??= plain ?? pad;
                m_Slides.Add(new Slide
                {
                    Number = number,
                    GamepadImage = pad.Image, KeyboardImage = keys.Image,
                    GamepadText = pad.Lines, KeyboardText = keys.Lines,
                });
            }
        }

        private static bool IsGamepadTag(string tag) => tag == "GAMEPAD" || tag == "PAD" || tag == "CONTROLLER" || tag == "GAMEPADS";
        private static bool IsKeyboardTag(string tag) => tag == "KEYBOARD" || tag == "KB" || tag == "MOUSE" || tag == "PC" || tag == "KEYBOARDMOUSE";

        private static bool IsImageName(string line)
        {
            string l = line.ToLowerInvariant();
            return l.EndsWith(".png") || l.EndsWith(".jpg") || l.EndsWith(".jpeg") || l.EndsWith(".tga") || l.EndsWith(".psd");
        }

        private static string StripExtension(string file)
        {
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file.Substring(0, dot) : file;
        }

        private void ShowSlide(int index)
        {
            m_Index = Mathf.Clamp(index, 0, Mathf.Max(0, m_Slides.Count - 1));
            m_Box.Clear();
            if (m_Slides.Count == 0)
            {
                m_Box.style.display = DisplayStyle.None;
                m_Page.text = "";
                return;
            }

            Slide slide = m_Slides[m_Index];
            bool gamepad = m_ShownGamepad;
            m_Picture.image = FindImage(gamepad ? slide.GamepadImage : slide.KeyboardImage);

            List<string> lines = gamepad ? slide.GamepadText : slide.KeyboardText;
            m_Box.style.display = lines.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            int size = lines.Count >= ManyLines ? SmallFontSize : FontSize;
            for (int i = 0; i < lines.Count; i++)
            {
                var label = new Label(FillControls(Loc.T(lines[i]), gamepad)) { pickingMode = PickingMode.Ignore };
                label.enableRichText = true;
                label.style.color = Color.black;
                label.style.fontSize = size;
                label.style.whiteSpace = WhiteSpace.Normal;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.marginTop = i == 0 ? 0 : 6;
                m_Box.Add(label);
            }
            GameFont.Apply(m_Box);

            m_Page.text = $"{m_Index + 1} / {m_Slides.Count}";
        }

        private Texture2D FindImage(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (images != null)
            {
                foreach (Texture2D texture in images)
                {
                    if (texture != null && string.Equals(texture.name, name, StringComparison.OrdinalIgnoreCase)) return texture;
                }
            }
#if UNITY_EDITOR
            // Not in the list yet (added since the scene was saved): load it straight from the folder in the editor.
            if (slidesText != null)
            {
                string folder = System.IO.Path.GetDirectoryName(UnityEditor.AssetDatabase.GetAssetPath(slidesText))?.Replace('\\', '/');
                foreach (string guid in UnityEditor.AssetDatabase.FindAssets(name + " t:Texture2D", new[] { folder }))
                {
                    var texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                    if (texture != null && string.Equals(texture.name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogWarning($"[How To Play] {name} isn't in the scene's picture list yet - open and save the HowToPlay scene so builds have it.", this);
                        return texture;
                    }
                }
            }
#endif
            Debug.LogWarning($"[How To Play] Couldn't find the picture {name}.", this);
            return null;
        }

        // =====================================================================================
        // Control names in the text
        // =====================================================================================

        /// <summary>Replaces [Jump], [Buy Yes], ... with what they're bound to (bold); unknown names stay as written.</summary>
        private string FillControls(string line, bool gamepad)
        {
            return ControlTag.Replace(line, match =>
            {
                string bound = BoundName(match.Groups[1].Value, gamepad);
                return string.IsNullOrEmpty(bound) ? match.Value : $"<b>[{bound}]</b>";
            });
        }

        private string BoundName(string name, bool gamepad)
        {
            var player = m_Actions.Player;
            string key = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", "");
            switch (key)
            {
                case "movementstick":
                case "movestick":
                case "move":
                    return gamepad ? Real(player.Move, true, null)
                        : $"{Real(player.Move, false, "up")}/{Real(player.Move, false, "left")}/{Real(player.Move, false, "down")}/{Real(player.Move, false, "right")}";
                case "camerastick":
                case "camera":
                    return gamepad ? Real(player.Look, true, null) : Loc.T("Mouse");
                case "moveforward": return gamepad ? Real(player.Move, true, null) : Real(player.Move, false, "up");
                case "moveback": return gamepad ? Real(player.Move, true, null) : Real(player.Move, false, "down");
                case "strafeleft": return gamepad ? Real(player.Move, true, null) : Real(player.Move, false, "left");
                case "straferight": return gamepad ? Real(player.Move, true, null) : Real(player.Move, false, "right");
                case "jump": return Real(player.Jump, gamepad, null);
                case "parkour":
                case "grab": return Real(player.Grab, gamepad, null);
                case "walk":
                case "togglewalk": return Real(player.Sprint, gamepad, null);
                case "openmenu":
                case "menu":
                case "openescapemenu": return Real(player.Menu, gamepad, null);
                case "interact": return Cosmetic(m_Bindings.Keyboard.Interact, m_Bindings.Gamepad.Interact, gamepad);
                case "useitem": return Cosmetic(m_Bindings.Keyboard.UseItem, m_Bindings.Gamepad.UseItem, gamepad);
                case "buyyes":
                case "betyes": return Cosmetic(m_Bindings.Keyboard.BetYes, m_Bindings.Gamepad.BetYes, gamepad);
                case "buyno":
                case "betno": return Cosmetic(m_Bindings.Keyboard.BetNo, m_Bindings.Gamepad.BetNo, gamepad);
                case "purchasemultiplier":
                case "betmultiplier": return Cosmetic(m_Bindings.Keyboard.BetMultiplier, m_Bindings.Gamepad.BetMultiplier, gamepad);
                case "dismisscurrentoption":
                case "dismisscurrentbet": return Cosmetic(m_Bindings.Keyboard.DismissBet, m_Bindings.Gamepad.DismissBet, gamepad);
                case "openitemmenu": return Cosmetic(m_Bindings.Keyboard.OpenItemMenu, m_Bindings.Gamepad.OpenItemMenu, gamepad);
                case "camerazoomin": return Cosmetic(m_Bindings.Keyboard.CameraZoomIn, m_Bindings.Gamepad.CameraZoomIn, gamepad);
                case "camerazoomout": return Cosmetic(m_Bindings.Keyboard.CameraZoomOut, m_Bindings.Gamepad.CameraZoomOut, gamepad);
                default: return null;
            }
        }

        /// <summary>A real action's binding for the scheme (the same lookup as the Change Keybindings screen).</summary>
        private static string Real(InputAction action, bool gamepad, string compositePart)
        {
            if (action == null) return null;
            string group = gamepad ? "Gamepad" : "Keyboard&Mouse";
            var bindings = action.bindings;
            for (int i = 0; i < bindings.Count; i++)
            {
                InputBinding b = bindings[i];
                if (compositePart != null)
                {
                    if (!b.isPartOfComposite || !string.Equals(b.name, compositePart, StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (b.isComposite || b.isPartOfComposite)
                {
                    continue;
                }
                if (!InGroup(b.groups, group)) continue;

                string path = b.effectivePath;
                int slash = path != null ? path.IndexOf('/') : -1;
                string control = slash >= 0 && slash + 1 < path.Length ? path.Substring(slash + 1) : path;
                return gamepad ? ControlNames.Gamepad(control) : ControlNames.Keyboard(control);
            }
            return null;
        }

        private static bool InGroup(string groups, string group)
        {
            if (string.IsNullOrEmpty(groups)) return false;
            foreach (string g in groups.Split(';'))
            {
                if (string.Equals(g, group, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Cosmetic(string keyboard, string gamepadControl, bool gamepad) =>
            gamepad ? ControlNames.Gamepad(gamepadControl) : ControlNames.Keyboard(keyboard);

        // =====================================================================================
        // Input
        // =====================================================================================

        private void Update()
        {
            if (m_Leaving) return;

            // Keyboard <-> gamepad: show the other version of the slide.
            bool gamepad = UsingGamepad();
            if (gamepad != m_ShownGamepad)
            {
                m_ShownGamepad = gamepad;
                ShowSlide(m_Index);
            }

            bool menu = m_MenuAction != null && m_MenuAction.WasPressedThisFrame();

            if (m_PopupOpen)
            {
                if (menu && PopupReady)
                {
                    MenuUI.PlaySound(cancelSound);
                    ClosePopup();
                }
                return;
            }

            if (Time.unscaledTime < m_InputBlockedUntil) return;

            if (menu)
            {
                OpenPopup();
                return;
            }
            if (AnyButtonPressed()) Next();
        }

        private void Next()
        {
            if (m_Index >= m_Slides.Count - 1)
            {
                MenuUI.PlaySound(selectSound);
                Leave();
                return;
            }
            MenuUI.PlaySound(selectSound);
            ShowSlide(m_Index + 1);
            m_InputBlockedUntil = Time.unscaledTime + SlideInputDelaySeconds;
        }

        /// <summary>Same rule as the wager ticker's key hints: whichever was touched last, keyboard or gamepad.</summary>
        private static bool UsingGamepad()
        {
            Gamepad pad = Gamepad.current;
            if (pad == null) return false;
            Keyboard keyboard = Keyboard.current;
            Mouse mouse = Mouse.current;
            double keys = Math.Max(keyboard != null ? keyboard.lastUpdateTime : 0, mouse != null ? mouse.lastUpdateTime : 0);
            return pad.lastUpdateTime > keys;
        }

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

        // =====================================================================================
        // Return to Menu?
        // =====================================================================================

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
            m_InputBlockedUntil = Time.unscaledTime + PopupInputDelaySeconds; // the press that closed it doesn't turn the slide
            m_Root.Focus();
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
            if (!PopupReady) return; // without the question up, B / Esc is just "any button" (or Open Menu)
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

#if UNITY_EDITOR
        /// <summary>Keeps the picture list in step with the text file's folder (save the scene to keep it).</summary>
        private void OnValidate()
        {
            if (slidesText == null) return;
            string folder = System.IO.Path.GetDirectoryName(UnityEditor.AssetDatabase.GetAssetPath(slidesText))?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder)) return;

            var found = new List<Texture2D>();
            foreach (string guid in UnityEditor.AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                var texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (texture != null) found.Add(texture);
            }
            found.Sort((a, b) => NaturalKey(a.name).CompareTo(NaturalKey(b.name)));

            bool same = images != null && images.Length == found.Count;
            for (int i = 0; same && i < found.Count; i++) same = images[i] == found[i];
            if (!same) images = found.ToArray();
        }

        /// <summary>how_to_play2 before how_to_play10.</summary>
        private static string NaturalKey(string name) => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(6, '0'));
#endif
    }
}
