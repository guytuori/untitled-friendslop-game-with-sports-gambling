using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The tunable parameters for one challenge: its display name and how many points completing it is worth.
    /// Every <see cref="ChallengeZone"/> points at one of these, so a challenge is parameterized just by
    /// tweaking its asset - no code changes.
    ///
    /// Since 2026-10-17 the reward is flat: run through the start flag, reach the end flag, get
    /// <see cref="basePoints"/> - once per player per round (see <see cref="ChallengeManager"/>). The old death
    /// and time pars (and their bonuses) are gone; going fast just leaves more time for other challenges.
    /// </summary>
    [CreateAssetMenu(fileName = "NewChallengeDefinition", menuName = "Game Rules/Challenge Definition")]
    public class ChallengeDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("The challenge's name (for logs and anything that wants to show it).")]
        public string challengeName = "New Challenge";

        [Header("Reward")]
        [Tooltip("Points every player gets for completing this challenge (each player once per round).")]
        public int basePoints = 100;
    }
}
