using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The game's translations (Settings > Gameplay > Language): English, Spanish, Italian, French, German and
    /// Portuguese (Brazil). The strings live in <see cref="LocStrings"/>, keyed by the English text.
    ///
    /// Two ways text gets translated:
    ///   - AUTOMATICALLY: every UI Toolkit label/button whose text is exactly one of the English keys is
    ///     swapped for the current language's version (see <see cref="LocApplier"/>), so plain strings in the
    ///     menu code ("Save Changes", "MASTER VOLUME", status messages) need no changes - just a row in the
    ///     table. Switching language re-translates what's on screen, and switching back to English restores it.
    ///   - EXPLICITLY, for text built from pieces: <see cref="F"/> ("ROUND {0}", "{0} reached the end!") and
    ///     <see cref="T"/>. The English format string is the key.
    /// Anything without a table row stays English (map names, deliberately, for now).
    /// </summary>
    public static class Loc
    {
        public const string DefaultLanguageCode = "en";

        public struct Language
        {
            public string Code;
            /// <summary>The language's own name, as shown in the Language dropdown.</summary>
            public string NativeName;
        }

        /// <summary>In <see cref="LocStrings"/> column order (English first, then each translation column).</summary>
        public static readonly Language[] Languages =
        {
            new Language { Code = "en", NativeName = "English" },
            new Language { Code = "es", NativeName = "Español" },
            new Language { Code = "it", NativeName = "Italiano" },
            new Language { Code = "fr", NativeName = "Français" },
            new Language { Code = "de", NativeName = "Deutsch" },
            new Language { Code = "pt", NativeName = "Português" },
        };

        private static int s_Index;

        /// <summary>Raised when the language changes.</summary>
        public static event Action LanguageChanged;

        /// <summary>0 = English, otherwise the index into <see cref="Languages"/>.</summary>
        public static int CurrentIndex => s_Index;

        public static string CurrentCode => Languages[s_Index].Code;

        public static bool IsSupported(string code) => IndexOf(code) >= 0;

        public static int IndexOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return -1;
            for (int i = 0; i < Languages.Length; i++)
            {
                if (string.Equals(Languages[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public static void SetLanguage(string code)
        {
            int index = IndexOf(code);
            if (index < 0) index = 0;
            if (index == s_Index) return;
            s_Index = index;
            LocApplier.MarkLanguageChanged();
            LanguageChanged?.Invoke();
        }

        /// <summary>True if the text is one of the English keys.</summary>
        public static bool HasEntry(string english) => english != null && LocStrings.Table.ContainsKey(english);

        /// <summary>The current language's version of an English string (the English if there's no translation).</summary>
        public static string T(string english)
        {
            if (s_Index == 0 || string.IsNullOrEmpty(english)) return english;
            if (LocStrings.Table.TryGetValue(english, out string[] row) && s_Index - 1 < row.Length && !string.IsNullOrEmpty(row[s_Index - 1]))
            {
                return row[s_Index - 1];
            }
            return english;
        }

        /// <summary><see cref="T"/> of an English format string, then string.Format with the arguments.</summary>
        public static string F(string englishFormat, params object[] args)
        {
            string format = T(englishFormat);
            try
            {
                return string.Format(format, args);
            }
            catch (FormatException)
            {
                return string.Format(englishFormat, args); // a broken translation - fall back to English
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void CreateApplier()
        {
            var go = new GameObject("[Loc]") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<LocApplier>();
        }
    }

    /// <summary>
    /// Translates UI Toolkit text in place: each frame (in LateUpdate, before the UI is drawn) it walks every
    /// panel's elements and replaces any text that is exactly an English key with its translation, remembering
    /// the English so it can switch again (or back to English) when the language changes. Text the code sets
    /// later is picked up the next frame. Text boxes are skipped (that's what the player typed). In English
    /// it only walks once, right after switching back, to restore everything.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class LocApplier : MonoBehaviour
    {
        private sealed class Record
        {
            public string English;
            public string Shown;
            public int Language;
        }

        private static bool s_RestorePending;

        private readonly ConditionalWeakTable<TextElement, Record> m_Records = new ConditionalWeakTable<TextElement, Record>();
        private readonly List<UIDocument> m_Documents = new List<UIDocument>();
        private readonly HashSet<IPanel> m_PanelsThisFrame = new HashSet<IPanel>();
        private float m_NextScan;

        internal static void MarkLanguageChanged()
        {
            if (Loc.CurrentIndex == 0) s_RestorePending = true;
        }

        private void OnEnable() => SceneManager.sceneLoaded += HandleSceneLoaded;

        private void OnDisable() => SceneManager.sceneLoaded -= HandleSceneLoaded;

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode) => m_NextScan = 0f;

        private void LateUpdate()
        {
            bool english = Loc.CurrentIndex == 0;
            if (english && !s_RestorePending) return;

            if (Time.unscaledTime >= m_NextScan)
            {
                m_NextScan = Time.unscaledTime + 1f;
                m_Documents.Clear();
                m_Documents.AddRange(FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None));
            }

            m_PanelsThisFrame.Clear();
            foreach (UIDocument document in m_Documents)
            {
                if (document == null || !document.isActiveAndEnabled) continue;
                VisualElement root = document.rootVisualElement;
                IPanel panel = root?.panel;
                if (panel == null || !m_PanelsThisFrame.Add(panel)) continue;
                Walk(panel.visualTree);
            }

            if (english) s_RestorePending = false;
        }

        private void Walk(VisualElement element)
        {
            if (element is TextInputBaseField<string>) return; // the player's own typing

            if (element is TextElement text) Translate(text);

            int count = element.hierarchy.childCount;
            for (int i = 0; i < count; i++) Walk(element.hierarchy[i]);
        }

        private void Translate(TextElement element)
        {
            string current = element.text;
            if (string.IsNullOrEmpty(current)) return;
            int language = Loc.CurrentIndex;

            if (m_Records.TryGetValue(element, out Record record))
            {
                if (current == record.Shown)
                {
                    if (record.Language == language) return;
                    string again = Loc.T(record.English);
                    record.Shown = again;
                    record.Language = language;
                    if (again != current) element.text = again;
                    return;
                }
                m_Records.Remove(element); // the code changed the text since
            }

            if (!Loc.HasEntry(current)) return;

            string translated = Loc.T(current);
            m_Records.Add(element, new Record { English = current, Shown = translated, Language = language });
            if (translated != current) element.text = translated;
        }
    }
}
