using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a piece of level geometry as a fireman's-pole that <see cref="PoleGrabAbility"/> can snap
    /// the player onto while Grab is held - see that class for the actual climbing/shimmying behavior.
    /// Put this on the pole's GameObject, alongside its collider.
    ///
    /// Dimensions are read from a <see cref="CapsuleCollider"/> with `direction` set to Y (1, also the
    /// component's own default) - the shape built by MapObstacleSetup for "pillar"-textured map
    /// geometry, so a hand-placed pole with a fresh CapsuleCollider needs no extra configuration. Local
    /// +Y is treated as "along the pole" (matching the collider's own axis); falls back to the
    /// serialized fields below if no CapsuleCollider is present.
    ///
    /// The collider stays solid - a pole still blocks ordinary movement like a small wall for anyone
    /// not actively grabbing it. That's compatible with PoleGrabAbility on purpose: the ability always
    /// positions the player at the collider's own radius plus the character controller's radius, i.e.
    /// right up against the outer surface, the same way gripping a real pole from outside never puts
    /// your body inside it.
    /// </summary>
    public class Pole : MonoBehaviour
    {
        [Header("Fallback Dimensions")]
        [Tooltip("Used only when this object has no CapsuleCollider to read dimensions from.")]
        [SerializeField] private float fallbackRadius = 0.15f;
        [SerializeField] private float fallbackHalfHeight = 2f;

        [Tooltip("How far past either end (in world units, along the pole's axis) the player can still be considered within climbing range - gives a little grace right at the top/bottom rather than an instant cutoff.")]
        [SerializeField] private float exitMargin = 0.1f;

        [Tooltip("How close (world units, measured from the pole's outer surface) the player needs to be to grab this pole while holding Grab.")]
        [SerializeField] private float grabRange = 1.0f;

        /// <summary>Half the pole's climbable length, in the pole's own local (unscaled) space, along local +Y.</summary>
        public float HalfHeightLocal { get; private set; }

        /// <summary>The pole's radius, in the pole's own local (unscaled) space.</summary>
        public float RadiusLocal { get; private set; }

        /// <summary>How close (world units, from the pole's outer surface) counts as within grab range.</summary>
        public float GrabRange => grabRange;

        /// <summary>World-space direction along the pole's climbable axis (local +Y).</summary>
        public Vector3 Axis => transform.up;

        /// <summary>World-space center of the pole.</summary>
        public Vector3 Center => transform.position;

        /// <summary>World-space radius, accounting for non-uniform scale on the two non-axis dimensions.</summary>
        public float WorldRadius
        {
            get
            {
                if (RadiusLocal <= 0f) ComputeDimensions();
                Vector3 scale = transform.lossyScale;
                float sideScale = (scale.x + scale.z) * 0.5f;
                return RadiusLocal * sideScale;
            }
        }

        /// <summary>World-space half-height along the pole's axis.</summary>
        public float WorldHalfHeight
        {
            get
            {
                if (HalfHeightLocal <= 0f) ComputeDimensions();
                return HalfHeightLocal * transform.lossyScale.y;
            }
        }

        private static readonly List<Pole> s_ActivePoles = new List<Pole>();

        /// <summary>Every enabled Pole in the scene - what PoleGrabAbility scans for a grab candidate.</summary>
        public static IReadOnlyList<Pole> ActivePoles => s_ActivePoles;

        private void Awake()
        {
            ComputeDimensions();
        }

        private void OnEnable()
        {
            s_ActivePoles.Add(this);
        }

        private void OnDisable()
        {
            s_ActivePoles.Remove(this);
        }

        private void ComputeDimensions()
        {
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                HalfHeightLocal = capsule.height * 0.5f;
                RadiusLocal = capsule.radius;
                return;
            }

            HalfHeightLocal = fallbackHalfHeight;
            RadiusLocal = fallbackRadius;
        }

        /// <summary>
        /// Projects a world position onto the pole's axis. Returns the closest point on the axis line,
        /// clamped to the pole's climbable range (with <see cref="exitMargin"/> grace at either end),
        /// plus the perpendicular distance from the axis to <paramref name="worldPosition"/> and
        /// whether that projection actually falls within the climbable range at all.
        /// </summary>
        public Vector3 ClosestAxisPoint(Vector3 worldPosition, out float distanceFromAxis, out bool withinHeight)
        {
            if (RadiusLocal <= 0f) ComputeDimensions();

            Vector3 axis = Axis;
            Vector3 toPoint = worldPosition - Center;
            float along = Vector3.Dot(toPoint, axis);
            float halfHeight = WorldHalfHeight;

            withinHeight = Mathf.Abs(along) <= halfHeight + exitMargin;

            float clampedAlong = Mathf.Clamp(along, -halfHeight, halfHeight);
            Vector3 perpendicular = toPoint - axis * along;
            distanceFromAxis = perpendicular.magnitude;

            return Center + axis * clampedAlong;
        }

        private void OnDrawGizmosSelected()
        {
            if (RadiusLocal <= 0f) ComputeDimensions();

            Gizmos.color = Color.green;
            Vector3 top = Center + Axis * WorldHalfHeight;
            Vector3 bottom = Center - Axis * WorldHalfHeight;
            Gizmos.DrawLine(top, bottom);
            Gizmos.DrawWireSphere(top, WorldRadius);
            Gizmos.DrawWireSphere(bottom, WorldRadius);
        }
    }
}
