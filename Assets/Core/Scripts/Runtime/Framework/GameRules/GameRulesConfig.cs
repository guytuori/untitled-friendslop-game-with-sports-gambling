using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Placeholder for the game's configurable rules - the starting score everyone wagers with, and how long the
    /// round timer counts down from. Follows this project's config-asset convention (see StatsConfig,
    /// SessionSettings) so PlayerScore and RoundTimer both read their defaults from one shared asset instead of
    /// duplicating magic numbers.
    ///
    /// Today this is just a ScriptableObject asset assigned in the Inspector. Once there's an actual pre-game
    /// "configure the rules" screen (timer, starting score, max players, etc.), that screen would write into an
    /// asset like this one - the fields below are exactly the values it would need to expose.
    /// </summary>
    [CreateAssetMenu(fileName = "NewGameRulesConfig", menuName = "Game Rules/Game Rules Config")]
    public class GameRulesConfig : ScriptableObject
    {
        /// <summary>
        /// The score every player starts a round with. This is a wagering currency, not a health-style stat -
        /// it starts well above zero (not 0) because you need points to be able to gamble with.
        /// </summary>
        [Header("Scoring")]
        [Tooltip("The score every player starts a round with. Needs to be well above zero - it's what players wager and gamble with.")]
        public int startingScore = 1000;

        /// <summary>
        /// How long, in seconds, the round timer counts down from. Reaching 0 has no effect on the round today;
        /// the remaining time is reserved for a future faster-completion bonus once rounds have an end condition.
        /// </summary>
        [Header("Round")]
        [Tooltip("How long, in seconds, the round timer counts down from. Reaching 0 does not end the round today.")]
        public float roundDurationSeconds = 120f;
    }
}
