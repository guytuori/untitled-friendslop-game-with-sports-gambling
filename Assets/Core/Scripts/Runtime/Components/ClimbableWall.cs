using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a piece of level geometry as climbable, checked by <see cref="WallClimbAbility"/>'s
    /// IsSurfaceClimbable. A collider hit by the wall-climb check only counts as climbable if this
    /// component is present on it (or on a parent - see GetComponentInParent in WallClimbAbility) -
    /// every other surface is rejected outright, no matter its angle. This makes wall-climbing opt-in
    /// per surface, the same way <see cref="Pole"/> makes fireman's-pole grabbing opt-in per pillar,
    /// rather than the old "every sufficiently vertical surface counts" behavior.
    ///
    /// Purely an identity tag - no fields or logic of its own. Doesn't require any particular collider
    /// type or shape, since WallClimbAbility already derives the wall's normal/angle from whatever
    /// collider it hits; this component only answers "is this specific hit eligible at all".
    ///
    /// Added automatically by MapColliderSetup to the per-submesh flat collider it builds for any
    /// material whose name contains "brickwall" (see MapColliderSetup.ClimbableKeyword) - unlike the
    /// checker/bluecarpet/pillar materials, a climbable wall keeps its ordinary flat MeshCollider (it
    /// should still block the player and be walked into normally); it just also gets this marker so
    /// WallClimbAbility knows to accept it. Can also be added by hand to any other collider (e.g. a
    /// hand-placed test wall) to make it climbable without going through the map-geometry pipeline.
    /// </summary>
    public class ClimbableWall : MonoBehaviour
    {
    }
}
