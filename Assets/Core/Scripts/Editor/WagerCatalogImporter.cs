using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Builds the wager master list (Resources/WagerCatalog.asset) from the plain-text wager file
    /// (<see cref="TextFilePath"/>), so new wagers are added by editing that file:
    ///
    ///   On jump:                                         &lt;- a section header (ends with ':') = the trigger
    ///   Will [PLAYER] jump [NUMBER] times before ...?    &lt;- a wager
    ///   !CHALLENGE! Will [PLAYER] jump [NUMBER] ...      &lt;- challenge-only (the marker isn't shown in game)
    ///   ...
    ///   RANDOM SPANS                                     &lt;- after this: a placeholder name, then its range
    ///   NUMBER
    ///   1-100
    ///
    /// Headers: "No particular trigger", or ones mentioning grind (rail), wall, pole, jump or run. [PLAYER]
    /// becomes the player's display name; the other [PLACEHOLDER] gets a random number from its span.
    ///
    /// Which hidden stat a wager is about is worked out from its wording (see InferStat - e.g. "jump ...
    /// from a grind rail" = rail jumps, "seconds on a wall" = wall time, "end with a score" = final score).
    /// To force one, put it in braces anywhere in the line, e.g. {RailSeconds} - see WagerStat for names.
    /// The summary after an update lists what each line was read as, so check it after adding wagers.
    /// Items aren't in the game yet, so wagers about them are kept but never offered. Pickup wagers are
    /// offered on maps that have pickups (see PickupManager).
    ///
    /// Run from Friendslop > Wagers > Update Wager Master List From Text File; it also runs by itself
    /// whenever the text file is saved, and when the editor loads if there's no list yet.
    /// </summary>
    [InitializeOnLoad]
    public static class WagerCatalogImporter
    {
        public const string TextFilePath = "Assets/Core/Scenes/Core/wagers.txt";
        private const string CatalogPath = "Assets/Core/Resources/" + WagerCatalog.ResourcePath + ".asset";
        private const string ChallengeMarker = "!CHALLENGE!";
        private const string SpansHeader = "RANDOM SPANS";

        private static readonly Regex Placeholder = new Regex(@"\[([A-Za-z_]+)\]");
        private static readonly Regex StatTag = new Regex(@"\{\s*([A-Za-z]+)\s*\}");
        private static readonly Regex Range = new Regex(@"^\s*(-?\d+)\s*-\s*(-?\d+)\s*$");

        static WagerCatalogImporter()
        {
            EditorApplication.delayCall += () =>
            {
                if (AssetDatabase.LoadAssetAtPath<WagerCatalog>(CatalogPath) == null && File.Exists(TextFilePath))
                {
                    Import(showDialog: false);
                }
            };
        }

        [MenuItem("Friendslop/Wagers/Update Wager Master List From Text File")]
        public static void ImportFromMenu()
        {
            Import(showDialog: true);
        }

        [MenuItem("Friendslop/Wagers/Select Wager Master List")]
        public static void SelectCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<WagerCatalog>(CatalogPath);
            if (catalog == null)
            {
                EditorUtility.DisplayDialog("Wagers", "There's no wager master list yet - run Update Wager Master List From Text File first.", "OK");
                return;
            }
            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        /// <summary>Reads the text file and rewrites the catalog. Returns a summary (also logged).</summary>
        public static string Import(bool showDialog)
        {
            if (!File.Exists(TextFilePath))
            {
                string message = $"Couldn't find the wager text file at {TextFilePath}.";
                Debug.LogError("[Wagers] " + message);
                if (showDialog) EditorUtility.DisplayDialog("Wagers", message, "OK");
                return message;
            }

            var problems = new StringBuilder();
            List<WagerCatalog.Template> templates = Parse(File.ReadAllLines(TextFilePath), problems);

            EnsureFolder(Path.GetDirectoryName(CatalogPath).Replace('\\', '/'));
            var catalog = AssetDatabase.LoadAssetAtPath<WagerCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<WagerCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            catalog.SetTemplates(templates);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            var summary = new StringBuilder();
            summary.AppendLine($"Wager master list updated: {templates.Count} wager(s), {templates.Count(t => t.supported)} offered in game.");
            foreach (WagerCatalog.Template t in templates)
            {
                string span = string.IsNullOrEmpty(t.numberPlaceholder) ? "" : $" {t.numberPlaceholder} {t.minNumber}-{t.maxNumber},";
                summary.AppendLine($"- [{t.trigger}, {t.scope}] {t.stat},{span} {(t.supported ? "" : "(not tracked yet - never offered) ")}{t.text}");
            }
            if (problems.Length > 0) summary.AppendLine().AppendLine("Problems:").Append(problems);

            string result = summary.ToString().TrimEnd();
            if (problems.Length > 0) Debug.LogWarning("[Wagers] " + result, catalog);
            else Debug.Log("[Wagers] " + result, catalog);

            if (showDialog)
            {
                // The dialog gets long - show the headline and problems, the full list is in the Console.
                string dialog = $"{templates.Count} wager(s) imported, {templates.Count(t => t.supported)} offered in game.\n\nThe full list (with what each line was read as) is in the Console.";
                if (problems.Length > 0) dialog += "\n\nProblems:\n" + problems;
                EditorUtility.DisplayDialog("Wagers", dialog, "OK");
            }
            return result;
        }

        internal static List<WagerCatalog.Template> Parse(string[] lines, StringBuilder problems)
        {
            var templates = new List<WagerCatalog.Template>();
            var spans = new Dictionary<string, (int Min, int Max)>(StringComparer.OrdinalIgnoreCase);

            WagerTrigger trigger = WagerTrigger.None;
            bool inSpans = false;
            string pendingSpanName = null;

            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n].Trim();
                if (line.Length == 0) continue;

                if (string.Equals(line, SpansHeader, StringComparison.OrdinalIgnoreCase))
                {
                    inSpans = true;
                    continue;
                }

                if (inSpans)
                {
                    Match range = Range.Match(line);
                    if (range.Success && pendingSpanName != null)
                    {
                        int a = int.Parse(range.Groups[1].Value), b = int.Parse(range.Groups[2].Value);
                        spans[pendingSpanName] = (Mathf.Min(a, b), Mathf.Max(a, b));
                        pendingSpanName = null;
                    }
                    else if (!range.Success)
                    {
                        pendingSpanName = line.Trim('[', ']', ':', ' ');
                    }
                    else
                    {
                        problems.AppendLine($"Line {n + 1}: a range with no name above it ('{line}').");
                    }
                    continue;
                }

                if (line.EndsWith(":"))
                {
                    if (!TryParseTrigger(line, out trigger))
                    {
                        problems.AppendLine($"Line {n + 1}: didn't recognize the section '{line}' - its wagers are treated as 'no particular trigger'.");
                        trigger = WagerTrigger.None;
                    }
                    continue;
                }

                var template = new WagerCatalog.Template { trigger = trigger, scope = WagerScope.Round };
                string text = line;
                if (text.StartsWith(ChallengeMarker, StringComparison.OrdinalIgnoreCase))
                {
                    template.scope = WagerScope.Challenge;
                    text = text.Substring(ChallengeMarker.Length).Trim();
                }

                WagerStat? stat = null;
                Match tag = StatTag.Match(text);
                if (tag.Success)
                {
                    if (Enum.TryParse(tag.Groups[1].Value, true, out WagerStat tagged)) stat = tagged;
                    else problems.AppendLine($"Line {n + 1}: unknown stat {{{tag.Groups[1].Value}}} - working it out from the wording instead.");
                    text = StatTag.Replace(text, "").Replace("  ", " ").Trim();
                }

                stat ??= InferStat(text);
                if (stat == null)
                {
                    problems.AppendLine($"Line {n + 1}: couldn't tell what this wager is about - skipped. Add a stat tag like {{Jumps}}: {line}");
                    continue;
                }

                template.text = text;
                template.stat = stat.Value;
                template.supported = stat != WagerStat.Items;

                foreach (Match m in Placeholder.Matches(text))
                {
                    string name = m.Groups[1].Value;
                    if (string.Equals(name, "PLAYER", StringComparison.OrdinalIgnoreCase)) continue;
                    template.numberPlaceholder = name;
                    break;
                }

                templates.Add(template);
            }

            // Fill in the spans now that they've all been read (they're at the bottom of the file).
            foreach (WagerCatalog.Template template in templates)
            {
                if (string.IsNullOrEmpty(template.numberPlaceholder))
                {
                    problems.AppendLine($"No [NUMBER]-style placeholder in: {template.text}");
                    template.supported = false;
                    continue;
                }
                if (spans.TryGetValue(template.numberPlaceholder, out var span))
                {
                    template.minNumber = span.Min;
                    template.maxNumber = span.Max;
                }
                else
                {
                    problems.AppendLine($"No RANDOM SPANS entry for [{template.numberPlaceholder}] - using 1-100: {template.text}");
                    template.minNumber = 1;
                    template.maxNumber = 100;
                }
            }

            return templates;
        }

        private static bool TryParseTrigger(string header, out WagerTrigger trigger)
        {
            string h = header.ToLowerInvariant();
            if (h.Contains("no particular")) trigger = WagerTrigger.None;
            else if (h.Contains("grind")) trigger = WagerTrigger.GrindRail;
            else if (h.Contains("wall")) trigger = WagerTrigger.WallRun;
            else if (h.Contains("pole")) trigger = WagerTrigger.PoleClimb;
            else if (h.Contains("jump")) trigger = WagerTrigger.Jump;
            else if (h.Contains("run")) trigger = WagerTrigger.Run;
            else
            {
                trigger = WagerTrigger.None;
                return false;
            }
            return true;
        }

        /// <summary>What the wager is about, from its wording. Order matters: the more specific phrases come first.</summary>
        internal static WagerStat? InferStat(string text)
        {
            string t = text.ToLowerInvariant();
            bool jump = t.Contains("jump");

            if (jump && t.Contains("grind")) return WagerStat.RailJumps;
            if (jump && t.Contains("pole")) return WagerStat.PoleJumps;
            if (t.Contains("wall jump")) return WagerStat.WallJumps;
            if (t.Contains("grind")) return WagerStat.RailSeconds;
            if (t.Contains("wall")) return WagerStat.WallSeconds;
            if (t.Contains("pole")) return WagerStat.PoleSeconds;
            if (t.Contains("slippery") || t.Contains(" ice")) return WagerStat.IceSeconds;
            if (t.Contains(" run") || t.Contains("running")) return WagerStat.RunSeconds;
            if (Regex.IsMatch(t, @"\bdie\b|\bdies\b|\bdeath")) return WagerStat.Deaths;
            if (t.Contains("end with a score") || t.Contains("finish with a score")) return WagerStat.FinalScore;
            if (t.Contains("score")) return WagerStat.ScoreReached;
            if (t.Contains("pickup")) return WagerStat.Pickups;
            if (Regex.IsMatch(t, @"\bitems?\b")) return WagerStat.Items;
            if (jump) return WagerStat.Jumps;
            return null;
        }

        internal static bool IsTextFile(string path) =>
            string.Equals(path, TextFilePath, StringComparison.OrdinalIgnoreCase);

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    /// <summary>Re-imports the wager master list whenever the wager text file is saved.</summary>
    internal class WagerTextFileWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (imported.Any(WagerCatalogImporter.IsTextFile) || moved.Any(WagerCatalogImporter.IsTextFile))
            {
                EditorApplication.delayCall += () => WagerCatalogImporter.Import(showDialog: false);
            }
        }
    }
}
