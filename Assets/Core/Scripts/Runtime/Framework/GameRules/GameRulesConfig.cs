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
        [Tooltip("How long, in seconds, the round timer counts down from. Reaching 0 ends the round.")]
        public float roundDurationSeconds = 120f;

        [Tooltip("How long, in seconds, the bonus timer counts down from (it runs alongside the round timer). Reaching the end point while it's running earns Bonus Points Per Second for every second left.")]
        public float bonusDurationSeconds = 120f;

        [Header("Team Target")]
        [Tooltip("Round 1's target score per player - the team target is this times the number of players.")]
        public int averageTargetScore = 3000;

        [Tooltip("The target is multiplied by this after every round the team clears (1 = never gets harder).")]
        public float targetScoreGrowth = 1.05f;

        [Header("Scoring")]
        [Tooltip("Points per whole second left on the bonus timer when a player reaches the end point.")]
        public int bonusPointsPerSecond = 10;

        [Tooltip("Percent of a player's final score kept (on top of the starting score) going into the next round.")]
        public int carryOverPercent = 5;
    }

    /// <summary>
    /// The rules for the match being played: the host's rules (session properties, set on the Host Game
    /// screen) when in a real game, or - when a gameplay scene is played directly in the editor - Normal
    /// defaults overridden by a <see cref="GameRulesConfig"/> asset if one is given.
    /// </summary>
    public static class MatchRules
    {
        public static HostGameRulesData Get(GameRulesConfig fallback)
        {
            if (FusionSessionService.HasInstance && FusionSessionService.Instance.InGame && !FusionSessionService.Instance.IsDevSession)
            {
                return FusionSessionService.Instance.SessionRules;
            }

            var rules = HostGamePresets.Normal;
            if (fallback != null)
            {
                rules.StartingPoints = fallback.startingScore;
                rules.RoundTimeSeconds = Mathf.RoundToInt(fallback.roundDurationSeconds);
                rules.BonusTimeSeconds = Mathf.RoundToInt(fallback.bonusDurationSeconds);
                rules.AverageTargetScore = fallback.averageTargetScore;
                rules.TargetScoreGrowth = fallback.targetScoreGrowth;
                rules.BonusPointsPerSecond = fallback.bonusPointsPerSecond;
                rules.CarryOverPercent = fallback.carryOverPercent;
            }
            return rules;
        }
    }
}
