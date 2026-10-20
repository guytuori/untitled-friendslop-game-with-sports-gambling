using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The old start / finish trigger volume of a challenge (an invisible BoxCollider over the tagged tile's
    /// footprint, made by Set Object Property > Challenge Start / Finish - see MapObjectPropertySetup).
    ///
    /// No longer used to run challenges (2026-10-17): a challenge now starts when the player runs through its
    /// start flag and ends at its end flag (see <see cref="ChallengeCourse"/>), so entering from the side
    /// doesn't count. ChallengeCourse switches these trigger colliders off when it sets a challenge up; the
    /// component stays only so the challenge prefabs keep their data (<see cref="Zone"/> tells which
    /// ChallengeZone - and so which points - belong to the start).
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class ChallengeZoneTrigger : MonoBehaviour
    {
        [SerializeField] private ChallengeZone zone;
        [SerializeField] private ChallengeZoneKind kind;

        /// <summary>The ChallengeZone this volume was wired to.</summary>
        public ChallengeZone Zone => zone;

        /// <summary>Start or Finish.</summary>
        public ChallengeZoneKind Kind => kind;

        /// <summary>Wires this trigger to its owning ChallengeZone. Called once by MapObjectPropertySetup / MapColliderSetup.</summary>
        public void Configure(ChallengeZone owningZone, ChallengeZoneKind zoneKind)
        {
            zone = owningZone;
            kind = zoneKind;
        }
    }
}
