using UnityEngine;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// A round "blob" shadow on the ground straight below a player while they're in the air (added 2026-10-28),
    /// like Mario 64 - so you can see where you'll land and how high up you are. Real shadows are off
    /// (FullBrightMode), so this is the only cue.
    ///
    ///   - Every player gets one (NetworkPlayers.Register), so you see other players' too.
    ///   - It's a soft dark circle lying on whatever is below (a raycast down from the feet, ignoring players
    ///     and triggers), tilted to match slopes and lifted a hair to avoid flicker.
    ///   - It fades in as the feet leave the ground (from <see cref="FadeInStart"/> to <see cref="FadeInFull"/>
    ///     above it) and is hidden when standing - worked out from the height, so it works for remote players
    ///     too. Higher up it gets smaller and lighter, down to <see cref="FarScale"/> / <see cref="FarAlpha"/>
    ///     at <see cref="FarHeight"/>.
    ///   - Hidden with nothing below within <see cref="MaxDistance"/>, while the player is eliminated, or while
    ///     their character model is hidden (finished, between swaps).
    /// The shadow object isn't a child of the player (so it doesn't turn or scale with them); it's destroyed
    /// with the player.
    /// </summary>
    public class JumpShadow : MonoBehaviour
    {
        /// <summary>Diameter right at the ground, in meters.</summary>
        public const float Diameter = 0.9f;

        /// <summary>Darkness right at the ground (0-1).</summary>
        public const float NearAlpha = 0.55f;

        /// <summary>Height above the ground where it's smallest / lightest.</summary>
        public const float FarHeight = 15f;
        public const float FarScale = 0.55f;
        public const float FarAlpha = 0.25f;

        /// <summary>Feet this far off the ground: starts fading in; fully shown by <see cref="FadeInFull"/>.</summary>
        public const float FadeInStart = 0.12f;
        public const float FadeInFull = 0.45f;

        /// <summary>Nothing hit within this far below: no shadow.</summary>
        public const float MaxDistance = 80f;

        private const float RayStartAbove = 0.3f;  // start just above the feet, so ground right underfoot is found
        private const float Lift = 0.03f;          // above the ground, against z-fighting
        private const float RendererCheckSeconds = 0.5f;

        private static Sprite s_Sprite;
        private static readonly RaycastHit[] s_Hits = new RaycastHit[16];
        private static int s_Mask = -1;

        private CorePlayerManager m_Player;
        private GameObject m_Shadow;
        private SpriteRenderer m_Renderer;
        private Renderer[] m_PlayerRenderers;
        private float m_NextRendererCheck;
        private bool m_ModelVisible = true;

        public static void Attach(CorePlayerManager player)
        {
            if (player == null || player.GetComponent<JumpShadow>() != null) return;
            player.gameObject.AddComponent<JumpShadow>().m_Player = player;
        }

        private void Awake()
        {
            if (m_Player == null) m_Player = GetComponent<CorePlayerManager>();

            m_Shadow = new GameObject("Jump Shadow") { layer = 2 }; // Ignore Raycast
            m_Renderer = m_Shadow.AddComponent<SpriteRenderer>();
            m_Renderer.sprite = ShadowSprite();
            m_Renderer.color = new Color(0f, 0f, 0f, 0f);
            m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Renderer.receiveShadows = false;
            m_Shadow.SetActive(false);
        }

        private void OnDestroy()
        {
            if (m_Shadow != null) Destroy(m_Shadow);
        }

        private void OnDisable()
        {
            if (m_Shadow != null) m_Shadow.SetActive(false);
        }

        private void LateUpdate()
        {
            if (m_Shadow == null) return;

            if (!ShouldShow() || !FindGround(out RaycastHit hit))
            {
                if (m_Shadow.activeSelf) m_Shadow.SetActive(false);
                return;
            }

            Vector3 feet = transform.position;
            float height = Mathf.Max(0f, feet.y - hit.point.y);
            float show = Mathf.InverseLerp(FadeInStart, FadeInFull, height);
            if (show <= 0f)
            {
                if (m_Shadow.activeSelf) m_Shadow.SetActive(false);
                return;
            }

            float far = Mathf.Clamp01(height / FarHeight);
            float size = Diameter * Mathf.Lerp(1f, FarScale, far);
            float alpha = Mathf.Lerp(NearAlpha, FarAlpha, far) * show;

            if (!m_Shadow.activeSelf) m_Shadow.SetActive(true);
            Transform t = m_Shadow.transform;
            t.SetPositionAndRotation(hit.point + hit.normal * Lift, Quaternion.FromToRotation(Vector3.forward, hit.normal));
            t.localScale = new Vector3(size, size, 1f);
            m_Renderer.color = new Color(0f, 0f, 0f, alpha);
        }

        private bool ShouldShow()
        {
            if (m_Player == null || !m_Player.IsSpawned) return false;
            if (m_Player.PlayerState != null && m_Player.PlayerState.LifeState == PlayerLifeState.Eliminated) return false;

            // Character hidden (finished fade, model being swapped): no shadow either.
            if (Time.time >= m_NextRendererCheck)
            {
                m_NextRendererCheck = Time.time + RendererCheckSeconds;
                m_PlayerRenderers = GetComponentsInChildren<Renderer>();
                m_ModelVisible = false;
                foreach (Renderer r in m_PlayerRenderers)
                {
                    if (r != null && r.enabled && r.gameObject.activeInHierarchy) { m_ModelVisible = true; break; }
                }
            }
            return m_ModelVisible;
        }

        /// <summary>The nearest solid thing straight below the feet that isn't a player.</summary>
        private bool FindGround(out RaycastHit ground)
        {
            ground = default;
            if (s_Mask == -1) s_Mask = BuildMask();

            Vector3 origin = transform.position + Vector3.up * RayStartAbove;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, s_Hits, MaxDistance + RayStartAbove, s_Mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = s_Hits[i];
                if (hit.collider == null || hit.distance >= best) continue;
                if (hit.collider.GetComponentInParent<CorePlayerManager>() != null) continue; // a player (this one or another)
                best = hit.distance;
                ground = hit;
                found = true;
            }
            return found;
        }

        private static int BuildMask()
        {
            int mask = Physics.DefaultRaycastLayers;
            foreach (string name in new[] { "Player", "PlayerHitBox" })
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer >= 0) mask &= ~(1 << layer);
            }
            return mask;
        }

        /// <summary>A soft-edged white disc (tinted black by the renderer), made once.</summary>
        private static Sprite ShadowSprite()
        {
            if (s_Sprite != null) return s_Sprite;

            const int Size = 64;
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "Jump Shadow",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color32[Size * Size];
            float half = (Size - 1) * 0.5f;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float r = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half; // 0 centre .. 1 edge
                    float a = 1f - Mathf.SmoothStep(0.55f, 1f, r);                                    // solid middle, soft rim
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            s_Sprite = Sprite.Create(texture, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
            s_Sprite.hideFlags = HideFlags.HideAndDontSave;
            return s_Sprite;
        }
    }
}
