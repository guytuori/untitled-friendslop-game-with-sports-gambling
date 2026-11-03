using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Blocks.Gameplay.Core
{
    /// <summary>
    /// "This can be interacted with" (added 2026-11-03): a flat gold arrow hovering over the object, pointing
    /// down at it, spinning like the pickups (one turn every 2 s) and bobbing gently, with a soft pulsing glow
    /// behind it and a light bloom.
    ///
    /// Who gets one (each adds its own with <see cref="Add"/> when it finds its objects):
    ///   - Ninja Pirate Tower's pumps (RisingWaterManager) - only while that pump would still raise the water;
    ///   - the zoo's dinosaurs (Dinosaurs) - the arrow follows them around.
    ///
    /// The arrow is Resources/Interact/InteractArrow.png if there is one (pointing down, transparent
    /// background), otherwise one drawn in code. Purely local visuals: nothing networked, no colliders.
    ///
    /// GLOW: the arrow and glow are drawn brighter than white (HDR), and a hidden global post-processing
    /// Volume adds a Bloom that only picks up pixels that bright (threshold 1.1), so the rest of the (very
    /// white) maps don't bloom. Without HDR on the render pipeline there's no bloom - just the arrow and glow.
    /// If a scene Volume already has a Bloom, this one (priority 999) takes over its settings.
    /// </summary>
    public sealed class InteractMarker : MonoBehaviour
    {
        public const string ArrowTexturePath = "Interact/InteractArrow";

        private const float ArrowWidth = 0.9f;        // meters
        private const float GlowWidth = 2.2f;
        private const float GapAboveTop = 0.75f;      // from the top of the object to the arrow's tip
        private const float BobHeight = 0.15f;
        private const float BobSeconds = 1.6f;
        private const float SecondsPerSpin = 2f;      // same as the pickups
        private const float PulseSeconds = 1.3f;
        private const float ArrowBrightness = 1.8f;   // HDR multipliers (only when HDR is on)
        private const float GlowBrightness = 1.5f;
        private const float BloomThreshold = 1.1f;
        private const float BloomIntensity = 0.6f;

        private static readonly Color GlowColor = new Color(1f, 0.85f, 0.4f, 1f);

        private static Sprite s_ArrowSprite;
        private static Sprite s_GlowSprite;
        private static Material s_ArrowMaterial;
        private static Material s_GlowMaterial;
        private static bool s_ResourcesMade;
        private static Volume s_BloomVolume;

        private Transform m_Target;
        private Vector3 m_LocalTop;      // the top of the object, in the target's own space
        private Func<bool> m_IsAvailable;
        private Transform m_Arrow;
        private Transform m_Glow;
        private SpriteRenderer m_GlowRenderer;
        private float m_ArrowHalfHeight;
        private float m_Phase;           // so markers don't all bob in step

        /// <summary>
        /// Puts a marker over <paramref name="target"/>. <paramref name="worldBounds"/> is the object's size now
        /// (the arrow sits above its top, centred, and follows the target as it moves and turns).
        /// <paramref name="isAvailable"/> (optional) hides the marker while it returns false. The marker removes
        /// itself when the target is destroyed.
        /// </summary>
        public static InteractMarker Add(Transform target, Bounds worldBounds, Func<bool> isAvailable = null)
        {
            if (target == null) return null;
            MakeResources();

            var go = new GameObject($"InteractMarker ({target.name})");
            var marker = go.AddComponent<InteractMarker>();
            marker.m_Target = target;
            marker.m_LocalTop = target.InverseTransformPoint(new Vector3(worldBounds.center.x, worldBounds.max.y, worldBounds.center.z));
            marker.m_IsAvailable = isAvailable;
            marker.m_Phase = UnityEngine.Random.value * BobSeconds;
            marker.Build();
            marker.LateUpdate();
            return marker;
        }

        /// <summary>The combined renderer bounds of <paramref name="root"/> (false if it has none).</summary>
        public static bool TryGetRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool found = false;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                if (!found) { bounds = r.bounds; found = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return found;
        }

        private void Build()
        {
            m_Glow = MakeSprite("Glow", s_GlowSprite, s_GlowMaterial, GlowWidth, 0, out m_GlowRenderer);
            m_Arrow = MakeSprite("Arrow", s_ArrowSprite, s_ArrowMaterial, ArrowWidth, 1, out SpriteRenderer arrow);
            m_ArrowHalfHeight = arrow.sprite != null ? arrow.sprite.bounds.extents.y * m_Arrow.localScale.y : ArrowWidth * 0.5f;
        }

        private Transform MakeSprite(string objectName, Sprite sprite, Material material, float width, int order, out SpriteRenderer renderer)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(transform, false);
            renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            if (material != null) renderer.sharedMaterial = material;
            renderer.sortingOrder = order;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            float spriteWidth = sprite != null ? sprite.bounds.size.x : 1f;
            go.transform.localScale = Vector3.one * (width / Mathf.Max(0.01f, spriteWidth));
            return go.transform;
        }

        private void LateUpdate()
        {
            if (m_Target == null)
            {
                Destroy(gameObject);
                return;
            }

            bool show = m_Target.gameObject.activeInHierarchy && (m_IsAvailable == null || m_IsAvailable());
            if (m_Arrow.gameObject.activeSelf != show)
            {
                m_Arrow.gameObject.SetActive(show);
                m_Glow.gameObject.SetActive(show);
            }
            if (!show) return;

            float t = Time.time;
            float bob = Mathf.Sin((t + m_Phase) / BobSeconds * Mathf.PI * 2f) * BobHeight;
            Vector3 top = m_Target.TransformPoint(m_LocalTop);
            Vector3 centre = top + Vector3.up * (GapAboveTop + m_ArrowHalfHeight + BobHeight + bob);
            transform.position = centre;

            // Spinning about the vertical, like the pickups.
            float angle = Mathf.Repeat(t / SecondsPerSpin, 1f) * 360f;
            m_Arrow.rotation = Quaternion.Euler(0f, angle, 0f);

            // The glow always faces the camera, and breathes.
            Camera cam = Camera.main;
            if (cam != null) m_Glow.rotation = cam.transform.rotation;
            float pulse = 0.5f + 0.5f * Mathf.Sin((t + m_Phase) / PulseSeconds * Mathf.PI * 2f);
            Color c = m_GlowRenderer.color;
            c.a = Mathf.Lerp(0.35f, 0.7f, pulse);
            m_GlowRenderer.color = c;
        }

        // =====================================================================================
        // Shared sprites, materials and the bloom
        // =====================================================================================

        private static void MakeResources()
        {
            if (s_ResourcesMade && s_ArrowSprite != null) return;
            s_ResourcesMade = true;

            bool hdr = IsHdr();

            var arrowTexture = Resources.Load<Texture2D>(ArrowTexturePath);
            if (arrowTexture == null) arrowTexture = DrawArrow(128);
            s_ArrowSprite = Sprite.Create(arrowTexture, new Rect(0f, 0f, arrowTexture.width, arrowTexture.height),
                new Vector2(0.5f, 0.5f), arrowTexture.width, 0, SpriteMeshType.FullRect);
            s_ArrowSprite.name = "InteractArrow";

            Texture2D glowTexture = DrawGlow(64);
            s_GlowSprite = Sprite.Create(glowTexture, new Rect(0f, 0f, glowTexture.width, glowTexture.height),
                new Vector2(0.5f, 0.5f), glowTexture.width, 0, SpriteMeshType.FullRect);
            s_GlowSprite.name = "InteractGlow";

            // Sprites/Default multiplies by _Color, which may go above 1 - that's what the bloom picks up.
            Shader shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                float a = hdr ? ArrowBrightness : 1f;
                float g = hdr ? GlowBrightness : 1f;
                s_ArrowMaterial = new Material(shader) { name = "InteractArrow", color = new Color(a, a, a, 1f) };
                s_GlowMaterial = new Material(shader) { name = "InteractGlow", color = new Color(GlowColor.r * g, GlowColor.g * g, GlowColor.b * g, 1f) };
            }
            else
            {
                Debug.LogWarning("[InteractMarker] No Sprites/Default shader - the markers use the default sprite material (no glow brightness).");
            }

            if (hdr) EnsureBloom();
            else Debug.Log("[InteractMarker] HDR is off on the render pipeline - interact markers without bloom.");
        }

        private static bool IsHdr()
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return urp != null && urp.supportsHDR;
        }

        private static void EnsureBloom()
        {
            if (s_BloomVolume != null) return;

            var go = new GameObject("InteractMarkerBloom") { hideFlags = HideFlags.HideInHierarchy };
            go.layer = 0; // Default - the layer the gameplay camera's volume mask includes
            DontDestroyOnLoad(go);

            s_BloomVolume = go.AddComponent<Volume>();
            s_BloomVolume.isGlobal = true;
            s_BloomVolume.priority = 999f;
            s_BloomVolume.weight = 1f;

            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "Interact Marker Bloom";
            Bloom bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(BloomThreshold);
            bloom.intensity.Override(BloomIntensity);
            bloom.scatter.Override(0.65f);
            bloom.tint.Override(Color.white);
            s_BloomVolume.sharedProfile = profile;
        }

        /// <summary>A gold arrow pointing down, with a dark outline, on a transparent background.</summary>
        private static Texture2D DrawArrow(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "InteractArrow", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float s = size / 128f;
            float outline = 7f * s;

            // In pixels, y up: head = triangle with its tip at the bottom; stem = box above it.
            Vector2 tip = new Vector2(64f, 18f) * s, headLeft = new Vector2(18f, 66f) * s, headRight = new Vector2(110f, 66f) * s;
            Vector2 stemMin = new Vector2(44f, 60f) * s, stemMax = new Vector2(84f, 116f) * s;

            var top = new Color(1f, 0.95f, 0.55f);
            var bottom = new Color(1f, 0.7f, 0.08f);
            var edge = new Color(0.32f, 0.18f, 0.02f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float d = Mathf.Min(TriangleDistance(p, tip, headLeft, headRight), BoxDistance(p, stemMin, stemMax));

                    float alpha = Mathf.Clamp01(outline + 0.5f - d);          // shape + outline, antialiased
                    float fill = Mathf.Clamp01(0.5f - d);                      // inside the shape
                    Color body = Color.Lerp(bottom, top, (float)y / size);
                    Color c = Color.Lerp(edge, body, fill);
                    c.a = alpha;
                    pixels[y * size + x] = c;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        /// <summary>A soft round glow: white, fading out to the edge.</summary>
        private static Texture2D DrawGlow(int size)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "InteractGlow", wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float r = new Vector2(x + 0.5f - half, y + 0.5f - half).magnitude / half;
                    float a = Mathf.Clamp01(1f - r);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a * a);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        // Signed distances (negative inside). Edge-plane maximum: corners come out mitred, which suits an arrow.
        private static float BoxDistance(Vector2 p, Vector2 min, Vector2 max) =>
            Mathf.Max(Mathf.Max(min.x - p.x, p.x - max.x), Mathf.Max(min.y - p.y, p.y - max.y));

        private static float TriangleDistance(Vector2 p, Vector2 a, Vector2 b, Vector2 c) =>
            Mathf.Max(Mathf.Max(EdgeDistance(p, a, b, c), EdgeDistance(p, b, c, a)), EdgeDistance(p, c, a, b));

        /// <summary>Distance from the line a-b, positive on the side away from <paramref name="inside"/>.</summary>
        private static float EdgeDistance(Vector2 p, Vector2 a, Vector2 b, Vector2 inside)
        {
            Vector2 n = new Vector2(b.y - a.y, a.x - b.x).normalized;
            if (Vector2.Dot(inside - a, n) > 0f) n = -n;
            return Vector2.Dot(p - a, n);
        }
    }
}
