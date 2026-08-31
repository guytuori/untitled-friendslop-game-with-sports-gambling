using UnityEngine;

namespace Friendslop.Gameplay
{
    /// <summary>
    /// Draws a simple soft "blob" shadow directly beneath this object by raycasting straight
    /// down, instead of relying on the scene's real-time light/shadow. This keeps the shadow a
    /// clean, readable circle regardless of the sun angle, and regardless of ramps or uneven
    /// ground beneath the character - which is what actually makes it useful for judging jumps.
    ///
    /// The shadow shrinks and fades out the higher this object is above the ground, so its size
    /// alone tells you roughly how far you still have to fall.
    ///
    /// Fully self-contained: it builds its own quad mesh and material at runtime, so you only
    /// need to add this component - no extra prefab/asset setup required. Add it to the
    /// "[BB] PlatformerPlayer" prefab (or any character) via Add Component > search
    /// "Drop Shadow Projector".
    /// </summary>
    [DisallowMultipleComponent]
    public class DropShadowProjector : MonoBehaviour
    {
        [Header("Ground Detection")]
        [Tooltip("Layers the shadow will land on. Defaults to matching the movement system's ground layers.")]
        [SerializeField] private LayerMask groundLayers = 1; // Default layer only, matching CoreMovement's groundLayers.
        [Tooltip("How far below this object to search for ground before hiding the shadow.")]
        [SerializeField] private float maxRayDistance = 15f;
        [Tooltip("Small upward nudge for the raycast start, in case this object's pivot sits exactly at ground level.")]
        [SerializeField] private float rayStartOffset = 0.15f;
        [Tooltip("Tiny gap kept between the shadow decal and the surface to avoid z-fighting.")]
        [SerializeField] private float surfaceOffset = 0.02f;

        [Header("Look & Feel")]
        [Tooltip("Shadow radius (world units) when standing right on the ground.")]
        [SerializeField] private float baseRadius = 0.5f;
        [Tooltip("Shadow radius as a fraction of baseRadius at the maximum tracked height.")]
        [SerializeField, Range(0.1f, 1f)] private float minRadiusScale = 0.45f;
        [Tooltip("Shadow opacity when standing right on the ground.")]
        [SerializeField, Range(0f, 1f)] private float maxOpacity = 0.55f;
        [Tooltip("Shadow opacity at the maximum tracked height.")]
        [SerializeField, Range(0f, 1f)] private float minOpacity = 0.08f;
        [Tooltip("Height above ground (world units) at which the shadow reaches its smallest/faintest. Roughly match this to the character's jump apex.")]
        [SerializeField] private float heightForMinSize = 3f;
        [Tooltip("Texture resolution for the generated soft-circle sprite.")]
        [SerializeField] private int textureResolution = 64;

        private Transform m_ShadowTransform;
        private MeshRenderer m_ShadowRenderer;
        private Material m_ShadowMaterial;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private void Awake()
        {
            BuildShadowObject();
        }

        private void LateUpdate()
        {
            Vector3 rayOrigin = transform.position + Vector3.up * rayStartOffset;

            if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, maxRayDistance + rayStartOffset, groundLayers, QueryTriggerInteraction.Ignore))
            {
                if (!m_ShadowRenderer.enabled)
                {
                    m_ShadowRenderer.enabled = true;
                }

                float heightAboveGround = Mathf.Max(0f, hit.distance - rayStartOffset);
                float t = heightForMinSize > 0f ? Mathf.Clamp01(heightAboveGround / heightForMinSize) : 0f;

                float radius = Mathf.Lerp(baseRadius, baseRadius * minRadiusScale, t);
                float opacity = Mathf.Lerp(maxOpacity, minOpacity, t);

                m_ShadowTransform.position = hit.point + hit.normal * surfaceOffset;
                m_ShadowTransform.rotation = Quaternion.FromToRotation(Vector3.up, hit.normal);
                m_ShadowTransform.localScale = new Vector3(radius * 2f, 1f, radius * 2f);

                Color c = m_ShadowMaterial.color;
                c.a = opacity;
                m_ShadowMaterial.color = c;
            }
            else if (m_ShadowRenderer.enabled)
            {
                // Fell off the level or too high above anything to matter - hide instead of
                // leaving a shadow stuck at max raycast range.
                m_ShadowRenderer.enabled = false;
            }
        }

        private void BuildShadowObject()
        {
            GameObject shadowObj = new GameObject("DropShadow (generated)");
            shadowObj.transform.SetParent(transform, false);
            shadowObj.hideFlags = HideFlags.DontSave;

            MeshFilter filter = shadowObj.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildFlatQuadMesh();

            m_ShadowRenderer = shadowObj.AddComponent<MeshRenderer>();
            m_ShadowRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_ShadowRenderer.receiveShadows = false;
            m_ShadowRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;

            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            m_ShadowMaterial = new Material(shader) { color = new Color(0f, 0f, 0f, maxOpacity) };
            m_ShadowMaterial.mainTexture = BuildSoftCircleTexture(textureResolution);
            m_ShadowRenderer.sharedMaterial = m_ShadowMaterial;

            m_ShadowTransform = shadowObj.transform;
            m_ShadowTransform.localScale = new Vector3(baseRadius * 2f, 1f, baseRadius * 2f);
        }

        /// <summary>
        /// A 1x1 quad lying flat in the XZ plane (local +Y is "up"), visible from both sides so
        /// the base orientation never depends on triangle winding.
        /// </summary>
        private static Mesh BuildFlatQuadMesh()
        {
            Mesh mesh = new Mesh { name = "DropShadowQuad" };

            Vector3[] vertices =
            {
                new Vector3(-0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f),
                new Vector3(-0.5f, 0f, 0.5f),
            };

            Vector2[] uv =
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };

            int[] triangles = { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A soft round black-on-transparent texture: solid-ish in the middle, fading smoothly
        /// to fully transparent at the edge.
        /// </summary>
        private static Texture2D BuildSoftCircleTexture(int resolution)
        {
            resolution = Mathf.Max(8, resolution);
            var tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                name = "DropShadowSoftCircle",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };

            Color[] pixels = new Color[resolution * resolution];
            float center = (resolution - 1) * 0.5f;
            float maxDist = center;

            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) / maxDist;

                    // Solid core out to ~55% of the radius, then smooth falloff to the edge.
                    float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, dist));
                    pixels[y * resolution + x] = new Color(0f, 0f, 0f, alpha);
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
