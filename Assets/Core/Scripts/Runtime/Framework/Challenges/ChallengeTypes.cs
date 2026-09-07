namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Which half of a challenge a <see cref="ChallengeZoneTrigger"/> represents.
    /// </summary>
    public enum ChallengeZoneKind
    {
        Start,
        Finish
    }

    /// <summary>
    /// The three options a non-challenging player is offered during a challenge's betting window.
    /// </summary>
    public enum ChallengeBetChoice
    {
        /// <summary>"He succeeds in under both Pars."</summary>
        Success,
        /// <summary>"He fails to achieve both pars."</summary>
        Failure,
        /// <summary>"I decline to bet." Costs nothing, wins nothing.</summary>
        Decline
    }
}
