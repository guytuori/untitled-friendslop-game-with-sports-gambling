using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// Marks a piece of level geometry as a balance beam that <see cref="BalanceBeamAbility"/> can
    /// snap the player onto. Put this on the beam's GameObject, alongside its collider.
    ///
    /// Beam dimensions are read from a <see cref="BoxCollider"/> on the same object when one exists
    /// (the beam's local +Z is treated as "along the beam" and local +X as "across the beam" - build
    /// beam geometry unrotated relative to its own length axis, i.e. a box scaled thin in X, thin in
    /// Y, long in Z). If there's no BoxCollider, the fallback fields below are used instead - useful
    /// for a beam built from a non-box mesh, as long as you size the fallbacks to match it by hand.
    /// </summary>
    public class BalanceBeam : MonoBehaviour
    {
        [Header("Fallback Dimensions")]
        [Tooltip("Used only when this object has no BoxCollider to read dimensions from.")]
        [SerializeField] private float fallbackHalfLength = 5f;
        [SerializeField] private float fallbackHalfWidth = 0.15f;
        [SerializeField] private float fallbackTopSurfaceLocalY = 0.15f;

        [Tooltip("How far past either end (in local, unscaled units) the player can still be considered 'on' the beam - gives a little grace right at the tips rather than an instant cutoff.")]
        [SerializeField] private float exitMargin = 0.35f;

        /// <summary>Half the beam's walkable length, in the beam's own local (unscaled) space.</summary>
        public float HalfLengthLocal { get; private set; }

        /// <summary>Half the beam's width, in the beam's own local (unscaled) space.</summary>
        public float HalfWidthLocal { get; private set; }

        /// <summary>The beam's top surface, as a Y offset in the beam's own local (unscaled) space.</summary>
        public float TopSurfaceLocalY { get; private set; }

        /// <summary>World-space direction along the beam.</summary>
        public Vector3 Forward => transform.forward;

        /// <summary>World-space direction across the beam.</summary>
        public Vector3 Right => transform.right;

        private void Awake()
        {
            ComputeDimensions();
        }

        private void ComputeDimensions()
        {
            var box = GetComponent<BoxCollider>();
            if (box != null)
            {
                // Everything here is in the beam's own local (unscaled) space - Transform.TransformPoint
                // applies lossyScale/rotation/position for us in GetSnapPosition, so these must NOT be
                // pre-multiplied by scale.
                HalfLengthLocal = box.size.z * 0.5f + Mathf.Abs(box.center.z);
                HalfWidthLocal = box.size.x * 0.5f;
                TopSurfaceLocalY = box.center.y + box.size.y * 0.5f;
            }
            else
            {
                HalfLengthLocal = fallbackHalfLength;
                HalfWidthLocal = fallbackHalfWidth;
                TopSurfaceLocalY = fallbackTopSurfaceLocalY;
            }
        }

        /// <summary>
        /// Projects a world position onto the beam and returns the corresponding point on its top
        /// surface, offset sideways by <paramref name="lateralBalanceNormalized"/> (-1 = left edge,
        /// 0 = centerline, 1 = right edge - scaled down slightly so even a full lean stays visibly on
        /// the beam rather than exactly at its physical edge).
        /// </summary>
        /// <param name="worldPosition">The position to project (typically the player's current position).</param>
        /// <param name="lateralBalanceNormalized">-1..1 balance value; see <see cref="BalanceBeamAbility"/>.</param>
        /// <param name="progressNormalized">-1..1 position along the beam's length (clamped).</param>
        /// <param name="withinBeam">False once the projected point is past either end (beyond <see cref="exitMargin"/>) - the caller should release the player from the beam when this is false.</param>
        public Vector3 GetSnapPosition(Vector3 worldPosition, float lateralBalanceNormalized, out float progressNormalized, out bool withinBeam)
        {
            if (HalfLengthLocal <= 0f) ComputeDimensions();

            Vector3 local = transform.InverseTransformPoint(worldPosition);
            float clampedZ = Mathf.Clamp(local.z, -HalfLengthLocal, HalfLengthLocal);
            progressNormalized = HalfLengthLocal > 0.0001f ? clampedZ / HalfLengthLocal : 0f;
            withinBeam = Mathf.Abs(local.z) <= HalfLengthLocal + exitMargin;

            float lateralX = Mathf.Clamp(lateralBalanceNormalized, -1f, 1f) * HalfWidthLocal * 0.8f;
            Vector3 snapLocal = new Vector3(lateralX, TopSurfaceLocalY, clampedZ);
            return transform.TransformPoint(snapLocal);
        }

        private void OnDrawGizmosSelected()
        {
            if (HalfLengthLocal <= 0f) ComputeDimensions();

            Gizmos.color = Color.yellow;
            Vector3 top = transform.TransformPoint(new Vector3(0f, TopSurfaceLocalY, -HalfLengthLocal));
            Vector3 bottom = transform.TransformPoint(new Vector3(0f, TopSurfaceLocalY, HalfLengthLocal));
            Gizmos.DrawLine(top, bottom);
            Gizmos.DrawWireSphere(top, 0.1f);
            Gizmos.DrawWireSphere(bottom, 0.1f);
        }
    }
}
