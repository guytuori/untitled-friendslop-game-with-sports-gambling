using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// One collectable pickup (think Mario coins / Sonic rings): a flat sprite floating at waist height,
    /// spinning slowly. Run through it and it pops, plays the coin sound and gives points.
    ///
    /// MAP SET-UP: place an entity named <c>pickup</c> in Total Editor (same as the challenge16 / challenge32
    /// markers) - in the grid cell just above the floor. UnityGLTF brings it in as an empty GameObject called
    /// "pickup" and <see cref="PickupManager"/> turns every one of those into a Pickup when the map is built
    /// (map and challenges alike). If there's ground up to <see cref="GroundSnapDistance"/> below the marker,
    /// the pickup is placed exactly <see cref="HeightAboveGround"/> above it (waist height); otherwise (e.g.
    /// a trail of pickups over a gap) it stays exactly where the marker is.
    ///
    /// The sprite is Resources/Pickups/Pickup.png, sized <see cref="Width"/> across. This component only
    /// draws and animates; who got it (and the points) is decided by PickupManager.
    /// </summary>
    [DisallowMultipleComponent]
    public class Pickup : MonoBehaviour
    {
        public const string TexturePath = "Pickups/Pickup";

        /// <summary>Sprite width in meters (height follows the image's shape).</summary>
        public const float Width = 2f;

        /// <summary>Center of the sprite above the ground under it (waist height).</summary>
        public const float HeightAboveGround = 1f;

        /// <summary>Ground at most this far below the marker pulls the pickup down/up to waist height.</summary>
        public const float GroundSnapDistance = 1.5f;

        private const float PopSeconds = 0.18f;
        private const float PopGrow = 1.35f;

        // "pickup", "pickup (3)", "pickup.001", "pickup_2" - not "pickups" or "pickup_something_else".
        private static readonly Regex MarkerName = new Regex(@"^pickup(\s*\(\d+\)|[\s_.\-]*\d+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static Sprite s_Sprite;
        private static bool s_SpriteLoaded;

        /// <summary>Index in PickupManager's list (hierarchy order - the same on every machine).</summary>
        public int Index { get; private set; }

        /// <summary>Where the sprite's center is, in world space.</summary>
        public Vector3 Center { get; private set; }

        /// <summary>Gone (collected by someone, or this player touched it and it's waiting on the master).</summary>
        public bool Collected { get; private set; }

        /// <summary>Half the sprite's height, in meters.</summary>
        public float HalfHeight { get; private set; } = 0.5f;

        private Transform m_Visual;
        private float m_PopStart = -1f;
        private float m_BaseScale = 1f;

        public static bool IsMarkerName(string name) => !string.IsNullOrEmpty(name) && MarkerName.IsMatch(name.Trim());

        /// <summary>Every pickup marker under <paramref name="root"/>, in hierarchy order.</summary>
        public static void FindMarkers(Transform root, List<Transform> results)
        {
            if (root == null) return;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (IsMarkerName(t.name)) results.Add(t);
            }
        }

        /// <summary>Builds the sprite. Called once by PickupManager.</summary>
        internal void Init(int index)
        {
            Index = index;
            Center = FindCenter(transform.position);

            Sprite sprite = LoadSprite();
            var visual = new GameObject("PickupSprite");
            visual.layer = gameObject.layer;
            m_Visual = visual.transform;
            m_Visual.SetParent(transform, false);
            m_Visual.position = Center;

            var spriteRenderer = visual.AddComponent<SpriteRenderer>();
            spriteRenderer.sprite = sprite;
            if (sprite != null && sprite.texture == Texture2D.whiteTexture) spriteRenderer.color = new Color(1f, 0.82f, 0.2f);
            spriteRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            spriteRenderer.receiveShadows = false;

            if (sprite != null) HalfHeight = sprite.bounds.extents.y;

            // The marker may be scaled (Total Editor exports an entity's radius as its scale) - undo that.
            float parentScale = Mathf.Abs(transform.lossyScale.x);
            m_BaseScale = parentScale > 0.0001f ? 1f / parentScale : 1f;
            m_Visual.localScale = Vector3.one * m_BaseScale;
        }

        /// <summary>Applies the shared spin (PickupManager keeps every pickup turning in step).</summary>
        internal void SetSpin(Quaternion rotation)
        {
            if (m_Visual != null) m_Visual.rotation = rotation;
        }

        /// <summary>Hides the pickup with a quick pop (or straight away if <paramref name="animate"/> is false).</summary>
        internal void Hide(bool animate)
        {
            if (Collected) return;
            Collected = true;
            if (m_Visual == null) return;

            if (animate && isActiveAndEnabled)
            {
                m_PopStart = Time.time;
            }
            else
            {
                m_Visual.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (m_PopStart < 0f || m_Visual == null) return;

            float t = (Time.time - m_PopStart) / PopSeconds;
            if (t >= 1f)
            {
                m_PopStart = -1f;
                m_Visual.gameObject.SetActive(false);
                return;
            }

            // Swell a little, then shrink away.
            float size = t < 0.35f ? Mathf.Lerp(1f, PopGrow, t / 0.35f) : Mathf.Lerp(PopGrow, 0f, (t - 0.35f) / 0.65f);
            m_Visual.localScale = Vector3.one * (m_BaseScale * size);
            m_Visual.position = Center + Vector3.up * (0.4f * t);
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

        private static Sprite LoadSprite()
        {
            if (s_SpriteLoaded) return s_Sprite;
            s_SpriteLoaded = true;

            var texture = Resources.Load<Texture2D>(TexturePath);
            if (texture == null)
            {
                Debug.LogWarning($"[Pickup] Couldn't find Resources/{TexturePath}.png - using a plain gold square.");
                texture = Texture2D.whiteTexture;
            }

            // Pixels per unit chosen so the sprite comes out Width meters across.
            float pixelsPerUnit = texture.width / Width;
            s_Sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit, 0, SpriteMeshType.FullRect);
            s_Sprite.name = "PickupSprite";
            return s_Sprite;
        }
    }
}
