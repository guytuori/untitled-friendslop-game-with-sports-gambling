using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// The whole-object property a single small map prop can be assigned - the per-object counterpart to
    /// what <see cref="MapColliderSetup"/> and <see cref="MapObstacleSetup"/> used to derive per-submesh
    /// from one giant, multi-material combined map mesh (by material name - "ice", "wall_climb",
    /// "balance_beam", "grind_rail", "pole", "player_spawn_point").
    ///
    /// That per-submesh approach was technically correct but made the map object (and the scene it lived
    /// in) too large, memory-wise, to add to source control once enough of these pieces existed side by
    /// side. The new approach: author/import each piece as its own small, single-purpose object - the
    /// whole object IS a pole, or IS ice, etc. - and build several of those into their own small group in
    /// a mini scene, rather than one enormous combined mesh. See <see cref="MapObjectPropertySetup"/>
    /// (Friendslop > Set Object Property > ...) for what actually builds the matching collider/component
    /// setup for each value below - this enum (and the component wrapping it) is just what's assigned.
    /// </summary>
    public enum MapObjectPropertyType
    {
        /// <summary>Ordinary solid geometry - a plain flat MeshCollider, no PhysicMaterial, nothing else added. The default for anything that isn't one of the special cases below.</summary>
        Regular,

        /// <summary>Ordinary solid geometry, but with the low-friction ice PhysicMaterial - same PhysicMaterial MapColliderSetup used to assign to "ice"-named submeshes.</summary>
        Ice,

        /// <summary>Ordinary solid geometry that's also climbable - a flat MeshCollider plus a <see cref="ClimbableWall"/> marker, same as MapColliderSetup's old "wall_climb" handling. Still blocks the player normally; WallClimbAbility is what actually looks for the marker.</summary>
        WallClimb,

        /// <summary>A fireman's pole - see <see cref="Pole"/> and PoleGrabAbility.</summary>
        Pole,

        /// <summary>A balance beam - see <see cref="BalanceBeam"/> and BalanceBeamAbility.</summary>
        BalanceBeam,

        /// <summary>A grindable rail - see <see cref="GrindRail"/> and GrindRailAbility.</summary>
        GrindRail,

        /// <summary>A player spawn/respawn marker - see <see cref="PlayerSpawnPoint"/> and GameManager.</summary>
        PlayerSpawnPoint,

        /// <summary>A challenge's start area - see <see cref="ChallengeZoneTrigger"/>/<see cref="ChallengeZone"/>, same as MapColliderSetup's old "start"-named-material handling. Still ordinary walkable geometry; it additionally gets an invisible full-height trigger volume.</summary>
        ChallengeStart,

        /// <summary>A challenge's finish line - see <see cref="ChallengeZoneTrigger"/>/<see cref="ChallengeZone"/>, same as MapColliderSetup's old "finish"-named-material handling. Still ordinary walkable geometry; it additionally gets an invisible full-height trigger volume.</summary>
        ChallengeFinish
    }

    /// <summary>
    /// Marks this whole object as having exactly one <see cref="MapObjectPropertyType"/>, and shows which
    /// one in the Inspector. Purely a label plus a little bookkeeping - it doesn't build anything by
    /// itself. Assign or change the property via Friendslop > Set Object Property > ... in the Editor
    /// (see <see cref="MapObjectPropertySetup"/>), which is what actually builds/tears down the matching
    /// collider and component(s) and keeps this field in sync. Changing the dropdown here by hand only
    /// changes the label, not the object's actual setup - use the menu instead.
    /// </summary>
    public class MapObjectProperty : MonoBehaviour
    {
        [Tooltip("What this whole object is. Set this via Friendslop > Set Object Property > ... rather than by hand - changing it here only updates the label, it doesn't rebuild the collider/components to match.")]
        [SerializeField] private MapObjectPropertyType propertyType = MapObjectPropertyType.Regular;

        [Tooltip("Internal bookkeeping for Set Object Property - the renderer's material(s) from before this object was set to Player Spawn Point (which hides it), so switching to any other property restores them. Not meant to be edited by hand.")]
        [SerializeField] private Material[] savedMaterialsBeforeHidden;

        /// <summary>
        /// The property currently assigned to this object, as last built by Set Object Property. Public
        /// (not internal) because the tool that writes it - MapObjectPropertySetup - lives in the separate
        /// Editor assembly (Blocks.Gameplay.Core.Editor), which can't see an internal member of a type
        /// declared in this Runtime assembly (Blocks.Gameplay.Core) without an InternalsVisibleTo this
        /// project doesn't define; an earlier version of this file got that wrong, which broke the build
        /// (see MapObjectPropertySetup's own use of this property).
        /// </summary>
        public MapObjectPropertyType PropertyType
        {
            get => propertyType;
            set => propertyType = value;
        }

        /// <summary>See the field's tooltip - used only by MapObjectPropertySetup to restore a hidden renderer's original material when switching away from Player Spawn Point. Public for the same cross-assembly reason as <see cref="PropertyType"/>.</summary>
        public Material[] SavedMaterialsBeforeHidden
        {
            get => savedMaterialsBeforeHidden;
            set => savedMaterialsBeforeHidden = value;
        }
    }
}
