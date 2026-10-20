using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Holds a placed challenge's <see cref="ChallengeDefinition"/> (name and points). Added by Friendslop >
    /// Set Object Property > Challenge Start / Challenge Finish (MapObjectPropertySetup), so every challenge
    /// prefab has one on its start object (and one on its finish object - the start's is the one used).
    ///
    /// Since 2026-10-17 this is only data. Everything about running a challenge - the start and end flags,
    /// who is in which challenge, who has completed what - lives in <see cref="ChallengeCourse"/> (one per
    /// placed challenge, set up by MatchLayout), <see cref="LocalChallengeTracker"/> (this machine's player)
    /// and <see cref="ChallengeManager"/> (the master client, which awards the points).
    /// </summary>
    public class ChallengeZone : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Name and points for this challenge.")]
        [SerializeField] private ChallengeDefinition definition;

        /// <summary>This challenge's parameters (name, points). May be null.</summary>
        public ChallengeDefinition Definition => definition;
    }
}
