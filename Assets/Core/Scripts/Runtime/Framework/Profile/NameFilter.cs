using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Display-name cleanup and a basic bad-word filter.
    ///
    /// MATURE LANGUAGE FILTER (2026-10-12): names are no longer refused for bad words. Instead, a player
    /// with Settings > Gameplay > Mature Language Filter on (the default) sees each bad word in a name
    /// replaced with *** (<see cref="Mask"/>, applied by NetworkPlayers.GetDisplayName), keeping the rest
    /// of the name - "Ihatefoos" shows as "Ihate***s" if "foo" is listed as an anywhere-word.
    /// Unity has no built-in profanity filter, so this is a
    /// small one of our own, driven by a plain text word list - <c>Resources/BlockedWords.txt</c> - that can
    /// be extended without touching code:
    ///   - one entry per line; blank lines and lines starting with # are ignored;
    ///   - a plain entry blocks that whole word ("ass" blocks "ass" and "a s s" but not "class");
    ///   - an entry starting with * blocks it anywhere in the name, spaces and punctuation removed - for
    ///     words that never appear inside innocent ones.
    /// Matching ignores case, punctuation and spacing ("F.u-c k"), common letter swaps ("5h1t", "@ss") and
    /// stretched letters ("fuuuuck"). It's deliberately simple - good enough to stop casual abuse, not a
    /// determined player.
    /// </summary>
    public static class NameFilter
    {
        /// <summary>Longest display name allowed.</summary>
        public const int MaxLength = 24;

        private const string WordListResource = "BlockedWords";

        private static HashSet<string> s_WholeWords;
        private static HashSet<string> s_WholeWordsCollapsed;
        private static List<string> s_Anywhere;

        /// <summary>
        /// Trims, collapses runs of whitespace to one space, drops control characters and cuts to
        /// <see cref="MaxLength"/>. Returns "" for null.
        /// </summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            var builder = new StringBuilder(name.Length);
            bool lastWasSpace = false;
            foreach (char c in name)
            {
                if (char.IsControl(c)) continue;
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                    lastWasSpace = true;
                    continue;
                }
                builder.Append(c);
                lastWasSpace = false;
            }

            string cleaned = builder.ToString().Trim();
            return cleaned.Length > MaxLength ? cleaned.Substring(0, MaxLength).TrimEnd() : cleaned;
        }

        /// <summary>True if the (already cleaned) name contains nothing from the blocked word list.</summary>
        public static bool IsAllowed(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            EnsureLoaded();

            string normalized = Normalize(name);
            string joined = normalized.Replace(" ", "");
            string joinedCollapsed = Collapse(joined);

            foreach (string token in normalized.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsBlockedWord(token)) return false;
            }

            // Spelled out with spaces or punctuation ("f u c k") - the whole name as one word.
            if (IsBlockedWord(joined)) return false;

            // Stretched letters are only squashed when the name actually has some, so innocent words that
            // merely contain a squashed bad word ("as" from "ass") aren't caught.
            bool stretched = joined != joinedCollapsed;
            foreach (string word in s_Anywhere)
            {
                if (joined.Contains(word)) return false;
                if (!stretched) continue;
                string collapsedWord = Collapse(word);
                // Too short once squashed ("kkk" -> "k") to match on safely.
                if (collapsedWord.Length >= 4 && joinedCollapsed.Contains(collapsedWord)) return false;
            }

            return true;
        }

        private static bool IsBlockedWord(string word)
        {
            if (s_WholeWords.Contains(word)) return true;
            string collapsed = Collapse(word);
            return collapsed != word && s_WholeWordsCollapsed.Contains(collapsed);
        }

        /// <summary>
        /// Checks a name for use: returns null if it's fine, or a player-facing reason it isn't.
        /// </summary>
        public static string Validate(string name)
        {
            string cleaned = Clean(name);
            if (cleaned.Length == 0) return "Enter a display name.";
            return null;
        }

        /// <summary>The text that replaces each bad word.</summary>
        public const string MaskText = "***";

        /// <summary>
        /// The name with every bad word replaced by <see cref="MaskText"/> - the rest of the name is kept.
        /// Uses the same matching as <see cref="IsAllowed"/> (whole words, anywhere-words, spaced-out
        /// spellings, letter swaps and stretched letters), mapped back onto the original characters.
        /// </summary>
        public static string Mask(string name)
        {
            if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
            EnsureLoaded();

            // Normalize keeps one character per character, so indices line up with the original name.
            string normalized = Normalize(name);
            var masked = new bool[name.Length];
            bool any = false;

            // Whole words: each run of letters.
            for (int i = 0; i < normalized.Length;)
            {
                if (normalized[i] == ' ') { i++; continue; }
                int start = i;
                while (i < normalized.Length && normalized[i] != ' ') i++;
                if (IsBlockedWord(normalized.Substring(start, i - start)))
                {
                    MarkRange(masked, start, i);
                    any = true;
                }
            }

            // The letters only, with where each came from in the original.
            var letters = new StringBuilder(name.Length);
            var letterIndex = new List<int>(name.Length);
            for (int i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] == ' ') continue;
                letters.Append(normalized[i]);
                letterIndex.Add(i);
            }
            string joined = letters.ToString();
            if (joined.Length == 0) return name;

            // Spelled out with spaces or punctuation ("f u c k") - the whole name as one word.
            if (IsBlockedWord(joined))
            {
                MarkRange(masked, letterIndex[0], letterIndex[letterIndex.Count - 1] + 1);
                any = true;
            }

            // Anywhere-words, plain and (if the name has stretched letters) squashed.
            var collapsed = new StringBuilder(joined.Length);
            var collapsedStart = new List<int>(joined.Length); // first letter (in joined) of each squashed letter
            var collapsedEnd = new List<int>(joined.Length);   // last letter (in joined) of each squashed letter
            for (int i = 0; i < joined.Length; i++)
            {
                if (i > 0 && joined[i] == joined[i - 1])
                {
                    collapsedEnd[collapsedEnd.Count - 1] = i;
                    continue;
                }
                collapsed.Append(joined[i]);
                collapsedStart.Add(i);
                collapsedEnd.Add(i);
            }
            string joinedCollapsed = collapsed.ToString();
            bool stretched = joinedCollapsed.Length != joined.Length;

            foreach (string word in s_Anywhere)
            {
                for (int at = joined.IndexOf(word, StringComparison.Ordinal); at >= 0; at = joined.IndexOf(word, at + 1, StringComparison.Ordinal))
                {
                    MarkRange(masked, letterIndex[at], letterIndex[at + word.Length - 1] + 1);
                    any = true;
                }

                if (!stretched) continue;
                string collapsedWord = Collapse(word);
                if (collapsedWord.Length < 4) continue;
                for (int at = joinedCollapsed.IndexOf(collapsedWord, StringComparison.Ordinal); at >= 0; at = joinedCollapsed.IndexOf(collapsedWord, at + 1, StringComparison.Ordinal))
                {
                    int first = collapsedStart[at];
                    int last = collapsedEnd[at + collapsedWord.Length - 1];
                    MarkRange(masked, letterIndex[first], letterIndex[last] + 1);
                    any = true;
                }
            }

            if (!any) return name;

            // Each run of masked characters (spaces and punctuation inside it included) becomes one ***.
            var result = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length;)
            {
                if (!masked[i])
                {
                    result.Append(name[i]);
                    i++;
                    continue;
                }
                while (i < name.Length && masked[i]) i++;
                result.Append(MaskText);
            }
            return result.ToString();
        }

        private static void MarkRange(bool[] masked, int start, int end)
        {
            for (int i = Math.Max(0, start); i < end && i < masked.Length; i++) masked[i] = true;
        }

        /// <summary>The name as this player should see it: masked if their Mature Language Filter is on.</summary>
        public static string ForDisplay(string name) =>
            GameplaySettings.Current.MatureLanguageFilter ? Mask(name) : name;

        /// <summary>Lower case, common letter swaps undone, everything that isn't a-z turned into a space.</summary>
        private static string Normalize(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char raw in text.ToLowerInvariant())
            {
                char c = raw switch
                {
                    '0' => 'o',
                    '1' or '!' or '|' => 'i',
                    '3' => 'e',
                    '4' or '@' => 'a',
                    '5' or '$' => 's',
                    '7' or '+' => 't',
                    '8' => 'b',
                    '9' => 'g',
                    _ => raw
                };
                builder.Append(c >= 'a' && c <= 'z' ? c : ' ');
            }
            return builder.ToString();
        }

        /// <summary>Squashes repeated letters: "fuuuuck" -> "fuck" (and "ass" -> "as", which is why entries are compared collapsed too).</summary>
        private static string Collapse(string text)
        {
            if (text.Length < 2) return text;
            var builder = new StringBuilder(text.Length);
            char previous = '\0';
            foreach (char c in text)
            {
                if (c != previous) builder.Append(c);
                previous = c;
            }
            return builder.ToString();
        }

        private static void EnsureLoaded()
        {
            if (s_WholeWords != null) return;

            s_WholeWords = new HashSet<string>();
            s_WholeWordsCollapsed = new HashSet<string>();
            s_Anywhere = new List<string>();

            var list = Resources.Load<TextAsset>(WordListResource);
            if (list == null)
            {
                Debug.LogWarning($"[NameFilter] Resources/{WordListResource}.txt not found - display names aren't being filtered.");
                return;
            }

            foreach (string rawLine in list.text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                bool anywhere = line.StartsWith("*");
                string word = Normalize(anywhere ? line.Substring(1) : line).Replace(" ", "");
                if (word.Length == 0) continue;

                if (anywhere)
                {
                    s_Anywhere.Add(word);
                }
                else
                {
                    s_WholeWords.Add(word);
                    s_WholeWordsCollapsed.Add(Collapse(word));
                }
            }
        }
    }
}
