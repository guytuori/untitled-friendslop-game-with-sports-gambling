using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Display-name cleanup and a basic bad-word filter. Unity has no built-in profanity filter, so this is a
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
            if (!IsAllowed(cleaned)) return "That display name isn't allowed.";
            return null;
        }

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
