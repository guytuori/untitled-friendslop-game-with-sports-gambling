using UnityEngine;
using Unity.Netcode;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Sits on one challenge's start (or finish) trigger volume - an invisible BoxCollider covering that
    /// tile area's full airspace, not just its walkable floor, so "pass overhead while jumping" counts the
    /// same as walking through it. See MapColliderSetup, which creates one of these alongside the normal
    /// walkable collider for any submesh whose material is named "start" or "finish".
    ///
    /// Only reports the LOCAL player's own entry (checks NetworkObject.IsOwner) - every machine simulates
    /// every player's collider, so without that check this would fire once per observing machine instead
    /// of once, for the wrong player half the time.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class ChallengeZoneTrigger : MonoBehaviour
    {
        [SerializeField] private ChallengeZone zone;
        [SerializeField] private ChallengeZoneKind kind;

        /// <summary>Wires this trigger to its owning ChallengeZone. Called once by MapColliderSetup.</summary>
        public void Configure(ChallengeZone owningZone, ChallengeZoneKind zoneKind)
        {
            zone = owningZone;
            kind = zoneKind;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (zone == null) return;
            if (!other.TryGetComponent(out NetworkObject networkObject)) return;
            if (!networkObject.IsOwner) return;

            zone.RequestEnterRpc(kind);
        }
    }
}
