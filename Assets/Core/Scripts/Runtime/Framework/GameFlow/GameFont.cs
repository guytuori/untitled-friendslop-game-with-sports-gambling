using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The game's one font (Genaminto), used for all text: every UI Toolkit screen and HUD, and TextMeshPro
    /// text such as the nameplates.
    ///
    /// Nothing has to be set up in scenes. A hidden object made at startup (<see cref="GameFontApplier"/>)
    /// puts the font on the root of every UIDocument, and everything inside inherits it; it also sets it on
    /// any TextMeshPro text it finds. Genaminto only comes in one style, so bold and italic are switched
    /// off rather than faked.
    ///
    /// To try a different font, point <see cref="FontPath"/> at it (a .ttf/.otf under a Resources folder).
    /// </summary>
    public static class GameFont
    {
        /// <summary>Resources path of the font (Assets/Core/Resources/Fonts/Genaminto-Regular.otf).</summary>
        public const string FontPath = "Fonts/Genaminto-Regular";

        private static Font s_Font;
        private static bool s_Warned;
        private static TMP_FontAsset s_TmpFont;
        private static FontDefinition s_Definition;

        /// <summary>The font, or null if it couldn't be found (then everything keeps Unity's default).</summary>
        public static Font Font
        {
            get
            {
                if (s_Font == null)
                {
                    s_Font = Resources.Load<Font>(FontPath);
                    if (s_Font != null)
                    {
                        s_Definition = FontDefinition.FromFont(s_Font);
                    }
                    else if (!s_Warned)
                    {
                        s_Warned = true;
                        Debug.LogWarning($"[GameFont] Couldn't find the font at Resources/{FontPath} - text keeps the default font.");
                    }
                }
                return s_Font;
            }
        }

        /// <summary>A TextMeshPro version of the font, made the first time it's needed.</summary>
        public static TMP_FontAsset TmpFont
        {
            get
            {
                if (s_TmpFont == null && Font != null)
                {
                    s_TmpFont = TMP_FontAsset.CreateFontAsset(s_Font);
                    if (s_TmpFont != null) s_TmpFont.name = s_Font.name + " (TMP)";
                }
                return s_TmpFont;
            }
        }

        /// <summary>Gives a UI Toolkit element (and so everything inside it) the game font.</summary>
        public static void Apply(VisualElement element)
        {
            if (element == null || Font == null) return;

            StyleFontDefinition current = element.style.unityFontDefinition;
            if (current.keyword == StyleKeyword.Undefined && current.value.font == s_Font) return;
            element.style.unityFontDefinition = s_Definition;
        }

        /// <summary>Gives a TextMeshPro text the game font (not bold or italic).</summary>
        public static void Apply(TMP_Text text)
        {
            if (text == null) return;

            TMP_FontAsset font = TmpFont;
            if (font != null && text.font != font) text.font = font;
            const FontStyles faked = FontStyles.Bold | FontStyles.Italic;
            if ((text.fontStyle & faked) != 0) text.fontStyle &= ~faked;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateApplier()
        {
            var go = new GameObject("[GameFont]") { hideFlags = HideFlags.HideInHierarchy };
            Object.DontDestroyOnLoad(go);
            go.AddComponent<GameFontApplier>();
        }
    }

    /// <summary>
    /// Keeps <see cref="GameFont"/> on everything: looks for UIDocuments and TextMeshPro text when a scene
    /// loads and every second after, and each frame re-applies the font to any UIDocument whose root was
    /// rebuilt (a UIDocument makes a new root each time it's enabled). Runs in LateUpdate, before the UI
    /// is drawn, so a screen never shows a frame in the old font.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class GameFontApplier : MonoBehaviour
    {
        private const float RescanSeconds = 1f;

        private readonly List<UIDocument> m_Documents = new List<UIDocument>();
        private float m_NextScan;

        private void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            m_NextScan = 0f; // rescan this frame
        }

        private void LateUpdate()
        {
            if (GameFont.Font == null)
            {
                enabled = false;
                return;
            }

            if (Time.unscaledTime >= m_NextScan)
            {
                m_NextScan = Time.unscaledTime + RescanSeconds;
                Rescan();
            }

            for (int i = 0; i < m_Documents.Count; i++)
            {
                UIDocument document = m_Documents[i];
                if (document != null && document.isActiveAndEnabled) GameFont.Apply(document.rootVisualElement);
            }
        }

        private void Rescan()
        {
            m_Documents.Clear();
            m_Documents.AddRange(FindObjectsByType<UIDocument>(FindObjectsInactive.Include, FindObjectsSortMode.None));

            foreach (TMP_Text text in FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                GameFont.Apply(text);
            }
        }
    }
}
