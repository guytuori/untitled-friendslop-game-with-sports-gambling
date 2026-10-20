using System.Collections.Generic;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a piece of level geometry as a fireman's-pole that <see cref="PoleGrabAbility"/> can snap
    /// the player onto while Grab is held - see that class for the actual climbing/shimmying behavior.
    /// Put this on the pole's GameObject, alongside its collider.
    ///
    /// Dimensions are read from a <see cref="CapsuleCollider"/> on the same object: its direction is "along
    /// the pole" (local X, Y or Z), and its center is the pole's middle - so a pole whose pivot isn't at its
    /// middle (a Total Editor export, pivoted at its glb's origin) and a pole modeled lying down both work.
    /// Friendslop > Set Object Property > Pole builds that capsule along the mesh's longest side. Without a
    /// CapsuleCollider, the serialized fallback fields below are used, along local +Y from the pivot.
    ///
    /// VERTICAL OR HORIZONTAL is decided by which way the pole's axis points in the world
    /// (<see cref="IsHorizontal"/>): more than <see cref="HorizontalAngle"/> degrees from straight up counts as
    /// horizontal - a swing pole (see PoleGrabAbility / PoleSwing2Ability) - otherwise it's a climbing pole.
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

        /// <summary>A pole whose axis is more than this many degrees from straight up (or down) is horizontal.</summary>
        public const float HorizontalAngle = 45f;

        // Which local axis runs along the pole (0 = X, 1 = Y, 2 = Z) and where its middle is, in local space -
        // from the CapsuleCollider (see ComputeDimensions).
        private int m_AxisIndex = 1;
        private Vector3 m_LocalCenter;
        private bool m_Computed;

        /// <summary>World-space direction along the pole's climbable axis (the CapsuleCollider's direction; local +Y without one).</summary>
        public Vector3 Axis
        {
            get
            {
                if (!m_Computed) ComputeDimensions();
                return m_AxisIndex == 0 ? transform.right : m_AxisIndex == 2 ? transform.forward : transform.up;
            }
        }

        /// <summary>World-space center of the pole (the CapsuleCollider's center; the pivot without one).</summary>
        public Vector3 Center
        {
            get
            {
                if (!m_Computed) ComputeDimensions();
                return transform.TransformPoint(m_LocalCenter);
            }
        }

        /// <summary>
        /// True for a swing pole: its axis is more than <see cref="HorizontalAngle"/> degrees from vertical.
        /// Taken from the world direction of the axis, so it doesn't matter whether the object was turned or
        /// modeled lying down.
        /// </summary>
        public bool IsHorizontal
        {
            get
            {
                float fromUp = Vector3.Angle(Axis, Vector3.up);
                return Mathf.Min(fromUp, 180f - fromUp) > HorizontalAngle;
            }
        }

        /// <summary>World-space radius, accounting for non-uniform scale on the two non-axis dimensions.</summary>
        public float WorldRadius
        {
            get
            {
                if (!m_Computed) ComputeDimensions();
                Vector3 scale = transform.lossyScale;
                float sideScale = m_AxisIndex == 0 ? (scale.y + scale.z) * 0.5f
                    : m_AxisIndex == 2 ? (scale.x + scale.y) * 0.5f
                    : (scale.x + scale.z) * 0.5f;
                return RadiusLocal * Mathf.Abs(sideScale);
            }
        }

        /// <summary>World-space half-height along the pole's axis.</summary>
        public float WorldHalfHeight
        {
            get
            {
                if (!m_Computed) ComputeDimensions();
                return HalfHeightLocal * Mathf.Abs(transform.lossyScale[m_AxisIndex]);
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
            m_Computed = true;
            var capsule = GetComponent<CapsuleCollider>();
            if (capsule != null)
            {
                m_AxisIndex = Mathf.Clamp(capsule.direction, 0, 2);
                m_LocalCenter = capsule.center;
                // A capsule's height includes its rounded ends; never shorter than its own diameter.
                HalfHeightLocal = Mathf.Max(capsule.height, capsule.radius * 2f) * 0.5f;
                RadiusLocal = capsule.radius;
                return;
            }

            m_AxisIndex = 1;
            m_LocalCenter = Vector3.zero;
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
            if (!m_Computed) ComputeDimensions();

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
            ComputeDimensions(); // the collider may have been edited

            Gizmos.color = Color.green;
            Vector3 top = Center + Axis * WorldHalfHeight;
            Vector3 bottom = Center - Axis * WorldHalfHeight;
            Gizmos.DrawLine(top, bottom);
            Gizmos.DrawWireSphere(top, WorldRadius);
            Gizmos.DrawWireSphere(bottom, WorldRadius);
        }
    }
}
