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
        /// <summary>Started running on the ground (no wagers use it since the walk button ones replaced them).</summary>
        Run = 2,
        GrindRail = 3,
        WallRun = 4,
        /// <summary>Got on any pole (sections that just say "pole").</summary>
        PoleClimb = 5,
        /// <summary>Not a section in the text file: fired when a player claims a challenge, so any challenge-only wager can follow.</summary>
        ChallengeStart = 6,
        /// <summary>Got on an upright (climbing) pole.</summary>
        VerticalPole = 7,
        /// <summary>Got on a horizontal pole (a bar).</summary>
        HorizontalPole = 8,
        /// <summary>Started walking on the ground (the walk button held / on while moving).</summary>
        Walk = 9
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
        /// <summary>Items received from item boxes this round. Only offered when items are on and the map has boxes.</summary>
        Items = 13,
        /// <summary>Seconds walking on the ground (the walk button) - added 2026-10-30 with the per-wager spans.</summary>
        WalkSeconds = 14,
        VerticalPoleSeconds = 15,
        VerticalPoleJumps = 16,
        /// <summary>Complete turns round an upright pole (Pole Swing 2).</summary>
        VerticalPoleRotations = 17,
        HorizontalPoleSeconds = 18,
        HorizontalPoleJumps = 19,
        /// <summary>Complete turns round a bar (Pole Swing 2).</summary>
        HorizontalPoleRotations = 20,
        /// <summary>Complete turns round any pole.</summary>
        PoleRotations = 21
    }

    /// <summary>How long a wager runs.</summary>
    public enum WagerScope
    {
        /// <summary>To the end of the round ("before the end of the game").</summary>
        Round = 0,
        /// <summary>To the end of the subject's current challenge (!CHALLENGE! in the text file).</summary>
        Challenge = 1,
        /// <summary>
        /// For a rolled number of seconds from when it's offered ("in the next [40-90] seconds"). Counts from
        /// what the player had done when it was offered (score ones are the score itself, not points gained).
        /// </summary>
        Window = 2
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

        /// <summary>
        /// Bumped when the importer changes how it reads the text file, so the editor re-imports an older list by
        /// itself. 2 (2026-10-30): per-wager spans in brackets ([1-100]) and "in the next [40-90] seconds".
        /// </summary>
        public const int CurrentVersion = 2;

        [SerializeField] private int version;

        public int Version => version;

        [Serializable]
        public class Template
        {
            [Tooltip("The wager as shown, with [PLAYER] and the placeholders still in it (no !CHALLENGE! marker). The text file's [1-100] spans become [NUMBER] / [SECONDS_NUMBER] / [SCORE_NUMBER], and the time window's becomes [WINDOW_SECONDS] - the English text is the localization key (LocStrings).")]
            public string text;

            [Tooltip("The placeholder that gets the random number, e.g. NUMBER (empty if there isn't one).")]
            public string numberPlaceholder;

            [Tooltip("Random span for that number (inclusive) - this wager's own [min-max] in the text file.")]
            public int minNumber = 1;
            public int maxNumber = 100;

            [Tooltip("Window wagers: the placeholder that gets the window's length (WINDOW_SECONDS), else empty.")]
            public string windowPlaceholder;

            [Tooltip("Window wagers: random span for the window's length in seconds (inclusive) - the [min-max] after 'in the next'.")]
            public int minWindowSeconds;
            public int maxWindowSeconds;

            public WagerTrigger trigger;
            public WagerScope scope;
            public WagerStat stat;

            [Tooltip("False when the line couldn't be read properly (no number to roll) - never offered. Pickup wagers are offered whenever the map has pickups left, item wagers whenever items are on and the map has item boxes.")]
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
            stat == WagerStat.PoleSeconds || stat == WagerStat.IceSeconds || stat == WagerStat.WalkSeconds ||
            stat == WagerStat.VerticalPoleSeconds || stat == WagerStat.HorizontalPoleSeconds;

        public static bool IsScoreStat(WagerStat stat) => stat == WagerStat.ScoreReached || stat == WagerStat.FinalScore;

        /// <summary>The wager's text with the player's name, the rolled number and (window wagers) the rolled window filled in.</summary>
        public static string Format(Template template, string playerName, int number, int windowSeconds = 0)
        {
            if (template == null) return "";
            string text = Loc.T(template.text).Replace("[PLAYER]", playerName ?? Loc.T("Someone"));
            if (!string.IsNullOrEmpty(template.numberPlaceholder))
            {
                text = text.Replace("[" + template.numberPlaceholder + "]", number.ToString());
            }
            if (!string.IsNullOrEmpty(template.windowPlaceholder))
            {
                text = text.Replace("[" + template.windowPlaceholder + "]", windowSeconds.ToString());
            }
            return text;
        }

#if UNITY_EDITOR
        /// <summary>Editor only - used by WagerCatalogImporter.</summary>
        public void SetTemplates(List<Template> list)
        {
            templates = list;
            version = CurrentVersion;
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
