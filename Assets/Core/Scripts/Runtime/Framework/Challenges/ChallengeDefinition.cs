using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The tunable parameters for one challenge (a "start" area to a "finish" line, like
    /// test_jump_challenge_1) - its display name, how many points reaching the finish is worth, and its
    /// two golf-style pars. Every <see cref="ChallengeZone"/> points at one of these, so a new challenge
    /// map is fully parameterized just by creating a new asset and tweaking its fields - no code changes.
    /// </summary>
    [CreateAssetMenu(fileName = "NewChallengeDefinition", menuName = "Game Rules/Challenge Definition")]
    public class ChallengeDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Shown to players - e.g. in the betting overlay ('PlayerX is attempting <name>').")]
        public string challengeName = "New Challenge";

        [Header("Reward")]
        [Tooltip("Points awarded to the challenge's owner just for reaching the finish line.")]
        public int basePoints = 100;

        [Header("Death Par")]
        [Tooltip("Reach the finish with fewer than this many deaths during the attempt to earn the bonus below.")]
        public int parDeaths = 3;
        [Tooltip("Bonus points awarded for beating the death par.")]
        public int parDeathBonus = 25;

        [Header("Time Par")]
        [Tooltip("Reach the finish in under this many seconds (from when the attempt starts) to earn the bonus below.")]
        public float parTimeSeconds = 10f;
        [Tooltip("Bonus points awarded for beating the time par.")]
        public int parTimeBonus = 25;
    }
}
