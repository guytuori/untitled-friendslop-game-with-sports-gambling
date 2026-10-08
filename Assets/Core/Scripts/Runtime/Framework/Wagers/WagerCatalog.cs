using System;
using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>What makes a wager eligible to pop up about a player (the section headers in wagers.txt).</summary>
    public enum WagerTrigger
    {
        /// <summary>"No particular trigger" - offered every so often about a random player.</summary>
        None = 0,
        Jump = 1,
        Run = 2,
        GrindRail = 3,
        WallRun = 4,
        PoleClimb = 5,
        /// <summary>Not a section in the text file: fired when a player claims a challenge, so any challenge-only wager can follow.</summary>
        ChallengeStart = 6
    }

    /// <summary>The hidden per-player number a wager is about. Seconds stats are tracked in tenths of a second.</summary>
    public enum WagerStat
    {
        Jumps = 0,
        RunSeconds = 1,
        RailSeconds = 2,
        RailJumps = 3,
        WallJumps = 4,
        WallSeconds = 5,
        PoleSeconds = 6,
        PoleJumps = 7,
        Deaths = 8,
        IceSeconds = 9,
        /// <summary>Score reached at any point (resolves YES the moment it's reached).</summary>
        ScoreReached = 10,
        /// <summary>Score at the end of the round (only resolves then).</summary>
        FinalScore = 11,
        /// <summary>Pickups collected this round (PickupManager). Only offered on maps that have pickups left.</summary>
        Pickups = 12,
        /// <summary>Not tracked yet (no items in the game) - wagers about it are imported but never offered.</summary>
        Items = 13
    }

    /// <summary>Whether a wager runs to the end of the round, or only to the end of the subject's current challenge.</summary>
    public enum WagerScope
    {
        Round = 0,
        Challenge = 1
    }

    /// <summary>
    /// The master list of wagers, built from wagers.txt by Friendslop > Wagers > Update Wager Master List
    /// From Text File (WagerCatalogImporter - it also re-runs whenever the text file is saved), and stored
    /// at Resources/WagerCatalog. Don't edit the asset by hand - edit the text file.
    ///
    /// Template indices are what's networked (WagerManager), so every player must be on the same build.
    /// </summary>
    public class WagerCatalog : ScriptableObject
    {
        public const string ResourcePath = "WagerCatalog";

        [Serializable]
        public class Template
        {
            [Tooltip("The wager as shown, with [PLAYER] and the random-number placeholder still in it (no !CHALLENGE! marker).")]
            public string text;

            [Tooltip("The placeholder that gets the random number, e.g. NUMBER (empty if there isn't one).")]
            public string numberPlaceholder;

            [Tooltip("Random span for that number (inclusive), from the RANDOM SPANS part of the text file.")]
            public int minNumber = 1;
            public int maxNumber = 100;

            public WagerTrigger trigger;
            public WagerScope scope;
            public WagerStat stat;

            [Tooltip("False for stats the game doesn't track yet (items) - never offered. Pickups wagers are offered whenever the map has pickups left.")]
            public bool supported = true;

            /// <summary>Seconds-based stats are stored in tenths of a second.</summary>
            public bool IsSeconds => WagerCatalog.IsSecondsStat(stat);
        }

        [SerializeField] private List<Template> templates = new List<Template>();

        private static WagerCatalog s_Loaded;

        public IReadOnlyList<Template> Templates => templates;

        public static WagerCatalog Load()
        {
            if (s_Loaded == null) s_Loaded = Resources.Load<WagerCatalog>(ResourcePath);
            return s_Loaded;
        }

        public Template Get(int index) => index >= 0 && index < templates.Count ? templates[index] : null;

        public static bool IsSecondsStat(WagerStat stat) =>
            stat == WagerStat.RunSeconds || stat == WagerStat.RailSeconds || stat == WagerStat.WallSeconds ||
            stat == WagerStat.PoleSeconds || stat == WagerStat.IceSeconds;

        public static bool IsScoreStat(WagerStat stat) => stat == WagerStat.ScoreReached || stat == WagerStat.FinalScore;

        /// <summary>The wager's text with the player's name and the rolled number filled in.</summary>
        public static string Format(Template template, string playerName, int number)
        {
            if (template == null) return "";
            string text = template.text.Replace("[PLAYER]", playerName ?? "Someone");
            if (!string.IsNullOrEmpty(template.numberPlaceholder))
            {
                text = text.Replace("[" + template.numberPlaceholder + "]", number.ToString());
            }
            return text;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by WagerCatalogImporter.</summary>
        public void SetTemplates(List<Template> list)
        {
            templates = list;
        }
#endif
    }

    /// <summary>
    /// Player actions the movement code reports as they happen (jumps of each kind). LocalActionTracker
    /// listens for its own player's and counts them for the wager system.
    /// </summary>
    public enum PlayerAction
    {
        Jump,
        RailJump,
        WallJump,
        PoleJump
    }

    public static class PlayerActions
    {
        /// <summary>Raised with the acting player's GameObject (the avatar root) and what they did.</summary>
        public static event Action<GameObject, PlayerAction> Performed;

        public static void Report(GameObject player, PlayerAction action)
        {
            if (player != null) Performed?.Invoke(player, action);
        }
    }
}
