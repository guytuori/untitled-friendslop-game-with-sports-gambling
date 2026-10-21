using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One Mario Kart-style item box: the item_box model (from <see cref="ItemCatalog"/>) floating above the
    /// ground, spinning on a tilt and bobbing. Running through it (with no item in hand) gives a random item.
    /// When taken it pops; when it comes back (a minute later) it grows back in.
    ///
    /// MAP SET-UP: an entity named <c>itembox</c> (also <c>item_box</c>, <c>itembox (2)</c>, <c>itembox.001</c>...)
    /// in Total Editor, in the cell just above the floor. <see cref="ItemManager"/> turns each one into an
    /// ItemBox when the map is built. This component only draws and animates; who gets what is ItemManager's.
    /// </summary>
    [DisallowMultipleComponent]
    public class ItemBox : MonoBehaviour
    {
        /// <summary>The box's middle above the ground under it.</summary>
        public const float HeightAboveGround = 1.35f;

        /// <summary>Ground at most this far below the marker sets the height.</summary>
        public const float GroundSnapDistance = 2.5f;

        private const float BobHeight = 0.15f;
        private const float BobSeconds = 1.7f;
        private const float SpinSeconds = 3f;
        private const float TiltDegrees = 25f;
        private const float PopSeconds = 0.22f;
        private const float GrowSeconds = 0.45f;

        // "itembox", "item_box", "item box", "itembox (3)", "itembox.001", "itembox_2" - not "item_box_model" etc.
        private static readonly Regex MarkerName = new Regex(@"^item[\s_]?box(\s*\(\d+\)|[\s_.\-]*\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Index in ItemManager's list (hierarchy order - the same on every machine).</summary>
        public int Index { get; private set; }

        /// <summary>Where the box's middle is (before bobbing), in world space.</summary>
        public Vector3 Center { get; private set; }

        /// <summary>Half the box's height, for the touch check.</summary>
        public float HalfHeight { get; private set; } = 0.7f;

        /// <summary>Taken (by anyone), or this player touched it and is waiting to hear from the master.</summary>
        public bool Hidden { get; private set; }

        /// <summary>Until when this player's own touch keeps it hidden while the master decides.</summary>
        public float PendingUntil { get; set; } = -1f;

        private Transform m_Pivot;
        private float m_AnimStart = -1f;
        private bool m_Growing;
        private float m_Phase;
        private float m_BaseScale = 1f;

        public static bool IsMarkerName(string name) => !string.IsNullOrEmpty(name) && MarkerName.IsMatch(name.Trim());

        /// <summary>Every item box marker under <paramref name="root"/>, in hierarchy order.</summary>
        public static void FindMarkers(Transform root, List<Transform> results)
        {
            if (root == null) return;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsMarkerName(t.name)) results.Add(t);
            }
        }

        /// <summary>Builds the box. Called once by ItemManager.</summary>
        internal void Init(int index, GameObject model, float size)
        {
            Index = index;
            Center = FindCenter(transform.position);
            m_Phase = index * 0.37f;

            var pivot = new GameObject("ItemBoxVisual");
            pivot.layer = gameObject.layer;
            m_Pivot = pivot.transform;
            // Not under the marker: the marker may be scaled (Total Editor exports an entity's radius as its scale).
            m_Pivot.SetParent(transform.parent, false);
            m_Pivot.position = Center;
            float parentScale = m_Pivot.parent != null ? Mathf.Abs(m_Pivot.parent.lossyScale.x) : 1f;
            m_BaseScale = parentScale > 0.0001f ? 1f / parentScale : 1f;
            m_Pivot.localScale = Vector3.one * m_BaseScale;

            GameObject visual;
            if (model != null)
            {
                visual = Instantiate(model);
                visual.name = "ItemBoxModel";
            }
            else
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = "ItemBoxModel (placeholder)";
                var placeholderRenderer = visual.GetComponent<Renderer>();
                if (placeholderRenderer != null) placeholderRenderer.material.color = new Color(1f, 0.8f, 0.2f, 1f);
            }

            foreach (Collider c in visual.GetComponentsInChildren<Collider>(true)) Destroy(c); // pass-through
            foreach (Transform t in visual.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = gameObject.layer;

            // Centre the model on the pivot and size it, whatever the glb's own origin and size are.
            visual.transform.SetParent(m_Pivot, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one;
            Bounds bounds = LocalBounds(visual.transform, m_Pivot);
            float longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            float scale = longest > 0.0001f ? size / longest : 1f; // bounds are in the pivot's (unit-scale) space
            visual.transform.localScale = Vector3.one * scale;
            visual.transform.localPosition = -bounds.center * scale;

            HalfHeight = size * 0.5f;
            Hidden = false;
        }

        /// <summary>Brings the box back, growing in (or straight away).</summary>
        internal void Show(bool animate)
        {
            if (!Hidden) return;
            Hidden = false;
            PendingUntil = -1f;
            if (m_Pivot == null) return;
            m_Pivot.gameObject.SetActive(true);
            if (animate)
            {
                m_Growing = true;
                m_AnimStart = Time.time;
                m_Pivot.localScale = Vector3.zero;
            }
            else
            {
                m_AnimStart = -1f;
                m_Pivot.localScale = Vector3.one * m_BaseScale;
            }
        }

        /// <summary>Hides the box with a pop (or straight away).</summary>
        internal void Hide(bool animate)
        {
            if (Hidden) return;
            Hidden = true;
            if (m_Pivot == null) return;
            if (animate && isActiveAndEnabled)
            {
                m_Growing = false;
                m_AnimStart = Time.time;
            }
            else
            {
                m_AnimStart = -1f;
                m_Pivot.gameObject.SetActive(false);
            }
        }

        private void OnDestroy()
        {
            if (m_Pivot != null) Destroy(m_Pivot.gameObject);
        }

        private void LateUpdate()
        {
            if (m_Pivot == null || !m_Pivot.gameObject.activeSelf) return;

            float t = Time.time;
            float spin = Mathf.Repeat(t / SpinSeconds, 1f) * 360f;
            m_Pivot.rotation = Quaternion.AngleAxis(spin, Vector3.up) * Quaternion.Euler(TiltDegrees, 0f, TiltDegrees);
            float bob = Mathf.Sin((t / BobSeconds + m_Phase) * Mathf.PI * 2f) * BobHeight;
            Vector3 position = Center + Vector3.up * bob;

            if (m_AnimStart >= 0f)
            {
                if (m_Growing)
                {
                    float u = Mathf.Clamp01((t - m_AnimStart) / GrowSeconds);
                    m_Pivot.localScale = Vector3.one * (m_BaseScale * ElasticOut(u));
                    if (u >= 1f) m_AnimStart = -1f;
                }
                else
                {
                    float u = (t - m_AnimStart) / PopSeconds;
                    if (u >= 1f)
                    {
                        m_AnimStart = -1f;
                        m_Pivot.localScale = Vector3.one * m_BaseScale;
                        m_Pivot.gameObject.SetActive(false);
                        return;
                    }
                    // Swell, then shrink away upwards.
                    float s = u < 0.35f ? Mathf.Lerp(1f, 1.4f, u / 0.35f) : Mathf.Lerp(1.4f, 0f, (u - 0.35f) / 0.65f);
                    m_Pivot.localScale = Vector3.one * (m_BaseScale * s);
                    position += Vector3.up * (0.5f * u);
                }
            }

            m_Pivot.position = position;
        }

        private static float ElasticOut(float u)
        {
            if (u <= 0f) return 0f;
            if (u >= 1f) return 1f;
            return Mathf.Pow(2f, -10f * u) * Mathf.Sin((u * 10f - 0.75f) * (2f * Mathf.PI / 3f)) + 1f;
        }

        private static Bounds LocalBounds(Transform visual, Transform space)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(Vector3.zero, Vector3.one);
            bool any = false;
            var bounds = new Bounds();
            foreach (Renderer r in renderers)
            {
                Bounds b = r.bounds;
                Vector3 min = b.min, max = b.max;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? min.x : max.x, (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                    Vector3 local = space.InverseTransformPoint(corner);
                    if (!any) { bounds = new Bounds(local, Vector3.zero); any = true; }
                    else bounds.Encapsulate(local);
                }
            }
            return bounds;
        }

        private static Vector3 FindCenter(Vector3 markerPosition)
        {
            Vector3 start = markerPosition + Vector3.up * 0.25f;
            RaycastHit[] hits = Physics.RaycastAll(start, Vector3.down, GroundSnapDistance + 0.25f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            Vector3? ground = null;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.collider.GetComponentInParent<CorePlayerManager>() != null) continue;
                if (hit.distance < best)
                {
                    best = hit.distance;
                    ground = hit.point;
                }
            }

            return ground.HasValue ? new Vector3(markerPosition.x, ground.Value.y + HeightAboveGround, markerPosition.z) : markerPosition;
        }
    }
}
