namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Practice Map (Single Player > Practice Map): the player alone on a chosen map, to learn it - no time
    /// limit, unlimited lives, no wagers, no death penalty, and a score that starts at 0 and only counts
    /// pickups. The challenge holes are filled with the plain Challenge16 / Challenge32 pieces instead of
    /// real challenges, the end point does nothing, and the only way out is Esc > Quit to Main Menu
    /// (without the usual 2-second safety delay).
    ///
    /// Started by <see cref="FusionSessionService.StartPracticeAsync"/> (a private one-player session), so
    /// <see cref="IsActive"/> is simply "this session is a practice session". Everything that behaves
    /// differently checks it: MatchLayout, RoundTimer, PlayerScore, ChallengeManager, WagerManager, CoreHUD
    /// and InGameMenu.
    /// </summary>
    public static class PracticeMode
    {
        /// <summary>True while in a Practice Map session.</summary>
        public static bool IsActive => FusionSessionService.HasInstance && FusionSessionService.Instance.IsPracticeSession;
    }
}
