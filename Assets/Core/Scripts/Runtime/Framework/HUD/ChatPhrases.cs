using System.Collections.Generic;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The message builder's pieces (2026-11-10, see <see cref="ChatOverlay"/>): what a gamepad player (or anyone in
    /// a Message Builder Chat Only game) can say without typing.
    ///
    /// DURING A ROUND: "[player] should / should not [action] [when]", e.g. "Player 2 should climb a wall by the
    /// end of the game." - one action per thing the wagers can be about. Only the ids go over the network (see
    /// <see cref="ChatLog"/>); each player's game builds the sentence in their own language from a template per
    /// should / should not and time frame, with the action as an infinitive phrase ("climb a wall" - German puts
    /// it at the end of the sentence, which the templates allow for).
    ///
    /// EVERYWHERE ELSE: a short list of set phrases - the lobby, between rounds, and the Game Results screen. "I'm
    /// using tilt controls!" (the lobby) can only be sent once per game; the game has no motion controls, it's a
    /// joke. The two GG phrases stay English in every language.
    ///
    /// Ids are sent over the network: only ever append to the lists, never reorder them.
    /// </summary>
    public static class ChatPhrases
    {
        /// <summary>Where the player is chatting from - decides what the builder offers.</summary>
        public enum Context { None, Lobby, BetweenRounds, Results, Round }

        /// <summary>Which map feature an action needs to be offered.</summary>
        public enum Needs { Nothing, Pickups, Items, Dinosaurs }

        public readonly struct ChatAction
        {
            /// <summary>The builder's list label (English; other languages use the phrase, capitalized).</summary>
            public readonly string Label;
            /// <summary>The infinitive phrase that goes in the sentence - a LocStrings key.</summary>
            public readonly string Phrase;
            public readonly Needs Needs;

            public ChatAction(string label, string phrase, Needs needs = Needs.Nothing)
            {
                Label = label; Phrase = phrase; Needs = needs;
            }
        }

        /// <summary>Indexed by action id (sent over the network - append only).</summary>
        public static readonly ChatAction[] Actions =
        {
            new ChatAction("Run", "run"),                                          // 0
            new ChatAction("Walk", "walk"),                                        // 1
            new ChatAction("Jump", "jump"),                                        // 2
            new ChatAction("Grind A Rail", "grind a rail"),                        // 3
            new ChatAction("Jump From A Rail", "jump from a rail"),                // 4
            new ChatAction("Wall Climb", "climb a wall"),                          // 5
            new ChatAction("Wall Jump", "do a wall jump"),                         // 6
            new ChatAction("Vertical Pole Swing", "swing on a vertical pole"),     // 7
            new ChatAction("Horizontal Pole Swing", "swing on a horizontal pole"), // 8
            new ChatAction("Jump From A Pole", "jump from a pole"),                // 9
            new ChatAction("Spin Around A Pole", "spin around a pole"),            // 10
            new ChatAction("Slide On Ice", "slide on ice"),                        // 11
            new ChatAction("Collect Pickups", "collect pickups", Needs.Pickups),   // 12
            new ChatAction("Collect Items", "collect items", Needs.Items),          // 13
            new ChatAction("Ride A Dinosaur", "ride a dinosaur", Needs.Dinosaurs), // 14
            new ChatAction("Score Points", "score points"),                        // 15
            new ChatAction("Die", "die"),                                          // 16
        };

        /// <summary>The time frames, as the builder lists them (LocStrings keys). Index = id.</summary>
        public static readonly string[] WhenLabels = { "in his current challenge.", "soon.", "by the end of the game." };

        /// <summary>The builder's should / should not choices (LocStrings keys). Index 0 = should.</summary>
        public static readonly string[] ShouldLabels = { "Should", "Should Not" };

        // Sentence templates: {0} = the player, {1} = the action phrase. [should not?, when] (LocStrings keys).
        private static readonly string[,] Templates =
        {
            { "{0} should {1} in his current challenge.", "{0} should {1} soon.", "{0} should {1} by the end of the game." },
            { "{0} should not {1} in his current challenge.", "{0} should not {1} soon.", "{0} should not {1} by the end of the game." },
        };

        // ----- Set phrases -----

        public const int PresetHello = 0, PresetStartWithWhoWeHave = 1, PresetWaitUntilFull = 2, PresetGoodLuck = 3,
            PresetTiltControls = 4, PresetGoodJob = 5, PresetFun = 6, PresetTough = 7, PresetGoodbye = 8, PresetGG = 9,
            PresetGetGood = 10;

        /// <summary>Indexed by preset id (sent over the network - append only). LocStrings keys, except the GG ones (English only).</summary>
        public static readonly string[] Presets =
        {
            "Hello!",
            "Let's start with who we have.",
            "Let's wait until we're full.",
            "Good luck.",
            "I'm using tilt controls!",
            "Good job!",
            "That was fun.",
            "That was tough.",
            "Good bye!",
            "GG",
            "GG. And by GG I mean Get Good.",
        };

        private static readonly int[] LobbyPresets = { PresetHello, PresetStartWithWhoWeHave, PresetWaitUntilFull, PresetGoodLuck, PresetTiltControls };
        private static readonly int[] BetweenRoundPresets = { PresetGoodJob, PresetFun, PresetTough };
        private static readonly int[] ResultPresets = { PresetGoodbye, PresetGoodJob, PresetFun, PresetTough, PresetGG, PresetGetGood };

        /// <summary>The set phrases offered in <paramref name="context"/> (none during a round - that's the full builder).</summary>
        public static IReadOnlyList<int> PresetsFor(Context context)
        {
            switch (context)
            {
                case Context.Lobby: return LobbyPresets;
                case Context.BetweenRounds: return BetweenRoundPresets;
                case Context.Results: return ResultPresets;
                default: return System.Array.Empty<int>();
            }
        }

        public static bool IsValidPreset(int id) => id >= 0 && id < Presets.Length;
        public static bool IsValidAction(int id) => id >= 0 && id < Actions.Length;
        public static bool IsValidWhen(int id) => id >= 0 && id < WhenLabels.Length;

        /// <summary>A set phrase in this player's language (the GG ones have no translation, so they stay English).</summary>
        public static string Preset(int id) => IsValidPreset(id) ? Loc.T(Presets[id]) : "";

        /// <summary>The builder's label for an action, in this player's language.</summary>
        public static string ActionLabel(int id)
        {
            if (!IsValidAction(id)) return "";
            if (Loc.CurrentIndex == 0) return Actions[id].Label;
            string phrase = Loc.T(Actions[id].Phrase);
            return phrase.Length > 0 ? char.ToUpper(phrase[0]) + phrase.Substring(1) : phrase;
        }

        /// <summary>"Player 2 should climb a wall by the end of the game.", in this player's language.</summary>
        public static string Sentence(string playerName, bool should, int action, int when)
        {
            if (!IsValidAction(action) || !IsValidWhen(when)) return "";
            return Loc.F(Templates[should ? 0 : 1, when], playerName, Loc.T(Actions[action].Phrase));
        }

        /// <summary>Whether this map has what the action needs (pickups, item boxes, dinosaurs).</summary>
        public static bool IsActionAvailable(int id)
        {
            if (!IsValidAction(id)) return false;
            switch (Actions[id].Needs)
            {
                case Needs.Pickups: return PickupManager.Instance != null && PickupManager.Instance.TotalCount > 0;
                case Needs.Items: return ItemManager.ItemsEnabled && ItemManager.Instance != null && ItemManager.Instance.BoxCount > 0;
                case Needs.Dinosaurs: return Dinosaurs.All.Count > 0;
                default: return true;
            }
        }
    }
}
